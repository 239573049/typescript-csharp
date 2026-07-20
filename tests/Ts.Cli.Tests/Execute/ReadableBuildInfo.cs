// Ported from tsgo internal/execute/tsctests/readablebuildinfo.go — the human-readable
// .tsbuildinfo.readable.baseline.txt writer (json.MarshalIndent with 2-space indent, key
// order = Go struct declaration order, omitzero semantics; plan/01 §JSON 字节保真).

using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ts.Core;
using Ts.Execute.Incremental;

namespace Ts.Cli.Tests.Execute;

internal static class ReadableBuildInfo
{
    // readablebuildinfo.go:209-238 — toReadableBuildInfo.
    internal static string ToReadableBuildInfo(BuildInfo buildInfo, string buildInfoText)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            new Writer(buildInfo).Write(w, buildInfoText.Length);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class Writer
    {
        private readonly BuildInfo buildInfo;
        private string[][] fileIdsList = [];

        internal Writer(BuildInfo buildInfo) => this.buildInfo = buildInfo;

        // readablebuildinfo.go:240-242
        private string ToFilePath(int fileId) => buildInfo.FileNames![fileId - 1];

        // readablebuildinfo.go:244-246
        private string[] ToFilePathSet(int fileIdListId) => fileIdsList[fileIdListId - 1];

        // Field order mirrors the readableBuildInfo struct declaration (readablebuildinfo.go:14-42).
        internal void Write(Utf8JsonWriter w, int size)
        {
            fileIdsList = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(buildInfo.FileIdsList ?? [], ids =>
                    System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(ids, ToFilePath))));

            w.WriteStartObject();
            if (buildInfo.Version.Length > 0)
            {
                w.WriteString("version", buildInfo.Version);
            }
            if (buildInfo.Errors)
            {
                w.WriteBoolean("errors", true);
            }
            if (buildInfo.CheckPending)
            {
                w.WriteBoolean("checkPending", true);
            }
            WriteRoot(w);
            WriteStringArray(w, "packageJsons", buildInfo.PackageJsons);
            WriteStringArray(w, "missingPackageJsons", buildInfo.MissingPackageJsons);
            WriteStringArray(w, "fileNames", buildInfo.FileNames);
            WriteFileInfos(w);
            WriteFileIdsList(w);
            WriteOptions(w);
            WriteReferencedMap(w);
            WriteSemanticDiagnostics(w);
            WriteEmitDiagnostics(w);
            WriteStringArray(w, "changeFileSet", System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(buildInfo.ChangeFileSet ?? [], ToFilePath)));
            WriteAffectedFilesPendingEmit(w);
            if (buildInfo.LatestChangedDtsFile.Length > 0)
            {
                w.WriteString("latestChangedDtsFile", buildInfo.LatestChangedDtsFile);
            }
            WriteEmitSignatures(w);
            WriteResolvedRoot(w);
            if (size != 0)
            {
                w.WriteNumber("size", size);
            }
            if (buildInfo.SemanticErrors)
            {
                w.WriteBoolean("semanticErrors", true);
            }
            w.WriteEndObject();
        }

        private static void WriteStringArray(Utf8JsonWriter w, string name, string[]? values)
        {
            if (values is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName(name);
            w.WriteStartArray();
            foreach (var value in values)
            {
                w.WriteStringValue(value);
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:297-315 — setRoot (readableBuildInfoRoot {files, original}).
        private void WriteRoot(Utf8JsonWriter w)
        {
            if (buildInfo.Root is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName("root");
            w.WriteStartArray();
            foreach (var original in buildInfo.Root)
            {
                w.WriteStartObject();
                w.WritePropertyName("files");
                w.WriteStartArray();
                if (original.NonIncremental.Length > 0)
                {
                    w.WriteStringValue(original.NonIncremental);
                }
                else if (original.End == 0)
                {
                    w.WriteStringValue(ToFilePath(original.Start));
                }
                else
                {
                    for (var i = original.Start; i <= original.End; i++)
                    {
                        w.WriteStringValue(ToFilePath(i));
                    }
                }
                w.WriteEndArray();
                w.WritePropertyName("original");
                original.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:279-295 — setFileInfos.
        private void WriteFileInfos(Utf8JsonWriter w)
        {
            if (buildInfo.FileInfos is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName("fileInfos");
            w.WriteStartArray();
            for (var index = 0; index < buildInfo.FileInfos.Length; index++)
            {
                var original = buildInfo.FileInfos[index];
                var fileInfo = original.GetFileInfo();
                w.WriteStartObject();
                w.WriteString("fileName", ToFilePath(index + 1));
                if (fileInfo.Version().Length > 0)
                {
                    w.WriteString("version", fileInfo.Version());
                }
                if (fileInfo.Signature().Length > 0)
                {
                    w.WriteString("signature", fileInfo.Signature());
                }
                if (fileInfo.AffectsGlobalScope())
                {
                    w.WriteBoolean("affectsGlobalScope", true);
                }
                var impliedNodeFormat = fileInfo.ImpliedNodeFormat().ToDisplayString();
                if (impliedNodeFormat.Length > 0)
                {
                    w.WriteString("impliedNodeFormat", impliedNodeFormat);
                }
                // Dont set original for string encoding
                if (!original.HasSignature())
                {
                    w.WritePropertyName("original");
                    original.WriteTo(w);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:317-321 — setFileIdsList.
        private void WriteFileIdsList(Utf8JsonWriter w)
        {
            if (fileIdsList.Length == 0)
            {
                return;
            }
            w.WritePropertyName("fileIdsList");
            w.WriteStartArray();
            foreach (var ids in fileIdsList)
            {
                w.WriteStartArray();
                foreach (var id in ids)
                {
                    w.WriteStringValue(id);
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        private void WriteOptions(Utf8JsonWriter w)
        {
            if (buildInfo.Options == null || !buildInfo.Options.TryEntryAt(0, out _, out _))
            {
                return;
            }
            w.WritePropertyName("options");
            w.WriteStartObject();
            foreach (var (key, value) in global::Ts.Collections.OrderedMapExtensions.Entries(buildInfo.Options))
            {
                w.WritePropertyName(key);
                BuildInfo.WriteJsonValue(w, value);
            }
            w.WriteEndObject();
        }

        // readablebuildinfo.go:323-330 — setReferencedMap (ordered object keyed by file path).
        private void WriteReferencedMap(Utf8JsonWriter w)
        {
            if (buildInfo.ReferencedMap == null)
            {
                return;
            }
            w.WritePropertyName("referencedMap");
            w.WriteStartObject();
            foreach (var entry in buildInfo.ReferencedMap)
            {
                w.WritePropertyName(ToFilePath(entry.FileId));
                w.WriteStartArray();
                foreach (var reference in ToFilePathSet(entry.FileIdListId))
                {
                    w.WriteStringValue(reference);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        // readablebuildinfo.go:248-270 — toReadableBuildInfoDiagnostic.
        private void WriteDiagnostics(Utf8JsonWriter w, BuildInfoDiagnostic[] diagnostics)
        {
            w.WriteStartArray();
            foreach (var d in diagnostics)
            {
                WriteDiagnostic(w, d);
            }
            w.WriteEndArray();
        }

        private void WriteDiagnostic(Utf8JsonWriter w, BuildInfoDiagnostic d)
        {
            w.WriteStartObject();
            if (d.File != 0)
            {
                w.WriteString("file", ToFilePath(d.File));
            }
            if (d.NoFile)
            {
                w.WriteBoolean("noFile", true);
            }
            if (d.Pos != 0)
            {
                w.WriteNumber("pos", d.Pos);
            }
            if (d.End != 0)
            {
                w.WriteNumber("end", d.End);
            }
            if (d.Code != 0)
            {
                w.WriteNumber("code", d.Code);
            }
            if (d.Category != 0)
            {
                w.WriteNumber("category", (int)d.Category);
            }
            if (d.MessageKey.Length > 0)
            {
                w.WriteString("messageKey", d.MessageKey);
            }
            if (d.MessageArgs is { Length: > 0 })
            {
                w.WritePropertyName("messageArgs");
                w.WriteStartArray();
                foreach (var arg in d.MessageArgs)
                {
                    w.WriteStringValue(arg);
                }
                w.WriteEndArray();
            }
            if (d.MessageChain is { Length: > 0 })
            {
                w.WritePropertyName("messageChain");
                WriteDiagnostics(w, d.MessageChain);
            }
            if (d.RelatedInformation is { Length: > 0 })
            {
                w.WritePropertyName("relatedInformation");
                WriteDiagnostics(w, d.RelatedInformation);
            }
            if (d.ReportsUnnecessary)
            {
                w.WriteBoolean("reportsUnnecessary", true);
            }
            if (d.ReportsDeprecated)
            {
                w.WriteBoolean("reportsDeprecated", true);
            }
            if (d.SkippedOnNoEmit)
            {
                w.WriteBoolean("skippedOnNoEmit", true);
            }
            w.WriteEndObject();
        }

        // readablebuildinfo.go:272-277 + 80-85 — [file, diagnostics] pair.
        private void WriteDiagnosticsOfFile(Utf8JsonWriter w, BuildInfoDiagnosticsOfFile diagnostics)
        {
            w.WriteStartArray();
            w.WriteStringValue(ToFilePath(diagnostics.FileId));
            WriteDiagnostics(w, diagnostics.Diagnostics);
            w.WriteEndArray();
        }

        // readablebuildinfo.go:336-347 — setSemanticDiagnostics.
        private void WriteSemanticDiagnostics(Utf8JsonWriter w)
        {
            if (buildInfo.SemanticDiagnosticsPerFile is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName("semanticDiagnosticsPerFile");
            w.WriteStartArray();
            foreach (var diagnostics in buildInfo.SemanticDiagnosticsPerFile)
            {
                if (diagnostics.FileId != 0)
                {
                    w.WriteStringValue(ToFilePath(diagnostics.FileId));
                }
                else
                {
                    WriteDiagnosticsOfFile(w, diagnostics.Diagnostics!);
                }
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:349-351 — setEmitDiagnostics.
        private void WriteEmitDiagnostics(Utf8JsonWriter w)
        {
            if (buildInfo.EmitDiagnosticsPerFile is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName("emitDiagnosticsPerFile");
            w.WriteStartArray();
            foreach (var diagnostics in buildInfo.EmitDiagnosticsPerFile)
            {
                WriteDiagnosticsOfFile(w, diagnostics);
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:353-366 — setAffectedFilesPendingEmit ([file, emitKind, original]).
        private void WriteAffectedFilesPendingEmit(Utf8JsonWriter w)
        {
            if (buildInfo.AffectedFilesPendingEmit == null)
            {
                return;
            }
            var fullEmitKind = FileEmitKindHelpers.GetFileEmitKind(buildInfo.GetCompilerOptions(""));
            w.WritePropertyName("affectedFilesPendingEmit");
            w.WriteStartArray();
            foreach (var pendingEmit in buildInfo.AffectedFilesPendingEmit)
            {
                var emitKind = pendingEmit.EmitKind == 0 ? fullEmitKind : pendingEmit.EmitKind;
                w.WriteStartArray();
                w.WriteStringValue(ToFilePath(pendingEmit.FileId));
                w.WriteStringValue(ToReadableFileEmitKind(emitKind));
                pendingEmit.WriteTo(w);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:408-418 — setEmitSignatures.
        private void WriteEmitSignatures(Utf8JsonWriter w)
        {
            if (buildInfo.EmitSignatures is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName("emitSignatures");
            w.WriteStartArray();
            foreach (var signature in buildInfo.EmitSignatures)
            {
                w.WriteStartObject();
                w.WriteString("file", ToFilePath(signature.FileId));
                if (signature.Signature.Length > 0)
                {
                    w.WriteString("signature", signature.Signature);
                }
                if (signature.DiffersOnlyInDtsMap)
                {
                    w.WriteBoolean("differsOnlyInDtsMap", true);
                }
                if (signature.DiffersInOptions)
                {
                    w.WriteBoolean("differsInOptions", true);
                }
                w.WritePropertyName("original");
                signature.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        // readablebuildinfo.go:420-427 + 193-195 — setResolvedRoot ([resolved, root] string pairs).
        private void WriteResolvedRoot(Utf8JsonWriter w)
        {
            if (buildInfo.ResolvedRoot is not { Length: > 0 })
            {
                return;
            }
            w.WritePropertyName("resolvedRoot");
            w.WriteStartArray();
            foreach (var original in buildInfo.ResolvedRoot)
            {
                w.WriteStartArray();
                w.WriteStringValue(ToFilePath(original.Resolved));
                w.WriteStringValue(ToFilePath(original.Root));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
    }

    // readablebuildinfo.go:368-406 — toReadableFileEmitKind.
    internal static string ToReadableFileEmitKind(FileEmitKind fileEmitKind)
    {
        var builder = new StringBuilder();
        void AddFlags(string flags)
        {
            if (builder.Length == 0)
            {
                builder.Append(flags);
            }
            else
            {
                builder.Append('|');
                builder.Append(flags);
            }
        }
        if (fileEmitKind != 0)
        {
            if ((fileEmitKind & FileEmitKind.Js) != 0)
            {
                AddFlags("Js");
            }
            if ((fileEmitKind & FileEmitKind.JsMap) != 0)
            {
                AddFlags("JsMap");
            }
            if ((fileEmitKind & FileEmitKind.JsInlineMap) != 0)
            {
                AddFlags("JsInlineMap");
            }
            if ((fileEmitKind & FileEmitKind.Dts) == FileEmitKind.Dts)
            {
                AddFlags("Dts");
            }
            else
            {
                if ((fileEmitKind & FileEmitKind.DtsEmit) != 0)
                {
                    AddFlags("DtsEmit");
                }
                if ((fileEmitKind & FileEmitKind.DtsErrors) != 0)
                {
                    AddFlags("DtsErrors");
                }
            }
            if ((fileEmitKind & FileEmitKind.DtsMap) != 0)
            {
                AddFlags("DtsMap");
            }
        }
        if (builder.Length != 0)
        {
            return builder.ToString();
        }
        return "None";
    }
}
