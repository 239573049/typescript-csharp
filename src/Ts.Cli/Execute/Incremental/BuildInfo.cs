// Ported from tsgo internal/execute/incremental/buildInfo.go — the .tsbuildinfo data model with
// its polymorphic JSON shapes (plan/01 §JSON 字节保真: hand-written Utf8JsonWriter with
// UnsafeRelaxedJsonEscaping, key order = Go struct declaration order, omitzero semantics,
// compact single-line output; reads via JsonDocument — both AOT-safe, no reflection STJ).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Ts.Core;

namespace Ts.Execute.Incremental;

// buildInfo.go:16-19
// BuildInfoFileId / BuildInfoFileIdListId dissolve to int (Go named int types).

// buildInfo.go:21-70 — buildInfoRoot is
// - for incremental program buildinfo
//   - start and end of FileId for consecutive fileIds to be included as root
//   - start - single fileId that is root
// - for non incremental program buildinfo
//   - string that is the root file name
public sealed class BuildInfoRoot
{
    public int Start;
    public int End;
    public string NonIncremental = ""; // Root of a non incremental program

    // buildInfo.go:34-44
    internal void WriteTo(Utf8JsonWriter w)
    {
        if (Start != 0)
        {
            if (End != 0)
            {
                w.WriteStartArray();
                w.WriteNumberValue(Start);
                w.WriteNumberValue(End);
                w.WriteEndArray();
            }
            else
            {
                w.WriteNumberValue(Start);
            }
        }
        else
        {
            w.WriteStringValue(NonIncremental);
        }
    }

    // buildInfo.go:46-70
    internal static BuildInfoRoot? ReadFrom(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                if (e.GetArrayLength() != 2)
                {
                    return null;
                }
                return new BuildInfoRoot { Start = e[0].GetInt32(), End = e[1].GetInt32() };
            case JsonValueKind.Number:
                return new BuildInfoRoot { Start = e.GetInt32() };
            case JsonValueKind.String:
                return new BuildInfoRoot { NonIncremental = e.GetString()! };
            default:
                return null;
        }
    }
}

// buildInfo.go:72-77
internal sealed class BuildInfoFileInfoNoSignature
{
    internal string Version = "";
    internal bool NoSignature;
    internal bool AffectsGlobalScope;
    internal ModuleKind ImpliedNodeFormat;

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (Version.Length > 0)
        {
            w.WriteString("version", Version);
        }
        if (NoSignature)
        {
            w.WriteBoolean("noSignature", true);
        }
        if (AffectsGlobalScope)
        {
            w.WriteBoolean("affectsGlobalScope", true);
        }
        if (ImpliedNodeFormat != ModuleKind.None)
        {
            w.WriteNumber("impliedNodeFormat", (int)ImpliedNodeFormat);
        }
        w.WriteEndObject();
    }

    internal static BuildInfoFileInfoNoSignature ReadFrom(JsonElement e)
    {
        var r = new BuildInfoFileInfoNoSignature();
        if (e.TryGetProperty("version", out var v))
        {
            r.Version = v.GetString() ?? "";
        }
        if (e.TryGetProperty("noSignature", out var ns))
        {
            r.NoSignature = ns.GetBoolean();
        }
        if (e.TryGetProperty("affectsGlobalScope", out var ags))
        {
            r.AffectsGlobalScope = ags.GetBoolean();
        }
        if (e.TryGetProperty("impliedNodeFormat", out var inf))
        {
            r.ImpliedNodeFormat = (ModuleKind)inf.GetInt32();
        }
        return r;
    }
}

// buildInfo.go:79-87 — Signature is
//   - undefined if FileInfo.version === FileInfo.signature
//   - string actual signature
internal sealed class BuildInfoFileInfoWithSignature
{
    internal string Version = "";
    internal string Signature = "";
    internal bool AffectsGlobalScope;
    internal ModuleKind ImpliedNodeFormat;

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (Version.Length > 0)
        {
            w.WriteString("version", Version);
        }
        if (Signature.Length > 0)
        {
            w.WriteString("signature", Signature);
        }
        if (AffectsGlobalScope)
        {
            w.WriteBoolean("affectsGlobalScope", true);
        }
        if (ImpliedNodeFormat != ModuleKind.None)
        {
            w.WriteNumber("impliedNodeFormat", (int)ImpliedNodeFormat);
        }
        w.WriteEndObject();
    }

    internal static BuildInfoFileInfoWithSignature ReadFrom(JsonElement e)
    {
        var r = new BuildInfoFileInfoWithSignature();
        if (e.TryGetProperty("version", out var v))
        {
            r.Version = v.GetString() ?? "";
        }
        if (e.TryGetProperty("signature", out var s))
        {
            r.Signature = s.GetString() ?? "";
        }
        if (e.TryGetProperty("affectsGlobalScope", out var ags))
        {
            r.AffectsGlobalScope = ags.GetBoolean();
        }
        if (e.TryGetProperty("impliedNodeFormat", out var inf))
        {
            r.ImpliedNodeFormat = (ModuleKind)inf.GetInt32();
        }
        return r;
    }
}

// buildInfo.go:89-173
public sealed class BuildInfoFileInfo
{
    internal string signature = "";
    internal BuildInfoFileInfoNoSignature? noSignature;
    internal BuildInfoFileInfoWithSignature? fileInfo;

    // buildInfo.go:95-114
    internal static BuildInfoFileInfo NewBuildInfoFileInfo(FileInfo fileInfo)
    {
        if (fileInfo.version == fileInfo.signature)
        {
            if (!fileInfo.affectsGlobalScope && fileInfo.impliedNodeFormat == ModuleKind.CommonJS)
            {
                return new BuildInfoFileInfo { signature = fileInfo.signature };
            }
        }
        else if (fileInfo.signature.Length == 0)
        {
            return new BuildInfoFileInfo
            {
                noSignature = new BuildInfoFileInfoNoSignature
                {
                    Version = fileInfo.version,
                    NoSignature = true,
                    AffectsGlobalScope = fileInfo.affectsGlobalScope,
                    ImpliedNodeFormat = fileInfo.impliedNodeFormat,
                },
            };
        }
        return new BuildInfoFileInfo
        {
            fileInfo = new BuildInfoFileInfoWithSignature
            {
                Version = fileInfo.version,
                Signature = fileInfo.signature == fileInfo.version ? "" : fileInfo.signature,
                AffectsGlobalScope = fileInfo.affectsGlobalScope,
                ImpliedNodeFormat = fileInfo.impliedNodeFormat,
            },
        };
    }

    // buildInfo.go:116-140
    public FileInfo GetFileInfo()
    {
        if (signature.Length > 0)
        {
            return new FileInfo(
                version: signature,
                signature: signature,
                impliedNodeFormat: ModuleKind.CommonJS);
        }
        if (noSignature != null)
        {
            return new FileInfo(
                version: noSignature.Version,
                affectsGlobalScope: noSignature.AffectsGlobalScope,
                impliedNodeFormat: noSignature.ImpliedNodeFormat);
        }
        return new FileInfo(
            version: fileInfo!.Version,
            signature: fileInfo.Signature.Length == 0 ? fileInfo.Version : fileInfo.Signature,
            affectsGlobalScope: fileInfo.AffectsGlobalScope,
            impliedNodeFormat: fileInfo.ImpliedNodeFormat);
    }

    // buildInfo.go:142-144
    public bool HasSignature() => signature.Length > 0;

    // buildInfo.go:146-154
    internal void WriteTo(Utf8JsonWriter w)
    {
        if (signature.Length > 0)
        {
            w.WriteStringValue(signature);
            return;
        }
        if (noSignature != null)
        {
            noSignature.WriteTo(w);
            return;
        }
        fileInfo!.WriteTo(w);
    }

    // buildInfo.go:156-173
    internal static BuildInfoFileInfo? ReadFrom(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            return new BuildInfoFileInfo { signature = e.GetString()! };
        }
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var noSig = BuildInfoFileInfoNoSignature.ReadFrom(e);
        if (noSig.NoSignature)
        {
            return new BuildInfoFileInfo { noSignature = noSig };
        }
        return new BuildInfoFileInfo { fileInfo = BuildInfoFileInfoWithSignature.ReadFrom(e) };
    }
}

// buildInfo.go:175-194
public sealed class BuildInfoReferenceMapEntry
{
    public int FileId;
    public int FileIdListId;

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartArray();
        w.WriteNumberValue(FileId);
        w.WriteNumberValue(FileIdListId);
        w.WriteEndArray();
    }

    internal static BuildInfoReferenceMapEntry? ReadFrom(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2)
        {
            return null;
        }
        return new BuildInfoReferenceMapEntry { FileId = e[0].GetInt32(), FileIdListId = e[1].GetInt32() };
    }
}

// buildInfo.go:196-212
public sealed class BuildInfoDiagnostic
{
    // BuildInfoFileId if it is for a File thats other than its stored for
    public int File;
    public bool NoFile;
    public int Pos;
    public int End;
    public int Code;
    public global::Ts.Diagnostics.Category Category;
    public string MessageKey = "";
    public string[]? MessageArgs;
    public BuildInfoDiagnostic[]? MessageChain;
    public BuildInfoDiagnostic[]? RelatedInformation;
    public bool ReportsUnnecessary;
    public bool ReportsDeprecated;
    public bool SkippedOnNoEmit;
    public BuildInfoRepopulateInfo? RepopulateInfo;

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (File != 0)
        {
            w.WriteNumber("file", File);
        }
        if (NoFile)
        {
            w.WriteBoolean("noFile", true);
        }
        if (Pos != 0)
        {
            w.WriteNumber("pos", Pos);
        }
        if (End != 0)
        {
            w.WriteNumber("end", End);
        }
        if (Code != 0)
        {
            w.WriteNumber("code", Code);
        }
        if (Category != 0)
        {
            w.WriteNumber("category", (int)Category);
        }
        if (MessageKey.Length > 0)
        {
            w.WriteString("messageKey", MessageKey);
        }
        if (MessageArgs is { Length: > 0 })
        {
            w.WritePropertyName("messageArgs");
            w.WriteStartArray();
            foreach (var arg in MessageArgs)
            {
                w.WriteStringValue(arg);
            }
            w.WriteEndArray();
        }
        if (MessageChain is { Length: > 0 })
        {
            w.WritePropertyName("messageChain");
            w.WriteStartArray();
            foreach (var d in MessageChain)
            {
                d.WriteTo(w);
            }
            w.WriteEndArray();
        }
        if (RelatedInformation is { Length: > 0 })
        {
            w.WritePropertyName("relatedInformation");
            w.WriteStartArray();
            foreach (var d in RelatedInformation)
            {
                d.WriteTo(w);
            }
            w.WriteEndArray();
        }
        if (ReportsUnnecessary)
        {
            w.WriteBoolean("reportsUnnecessary", true);
        }
        if (ReportsDeprecated)
        {
            w.WriteBoolean("reportsDeprecated", true);
        }
        if (SkippedOnNoEmit)
        {
            w.WriteBoolean("skippedOnNoEmit", true);
        }
        if (RepopulateInfo != null)
        {
            w.WritePropertyName("repopulateInfo");
            RepopulateInfo.WriteTo(w);
        }
        w.WriteEndObject();
    }

    internal static BuildInfoDiagnostic ReadFrom(JsonElement e)
    {
        var r = new BuildInfoDiagnostic();
        if (e.TryGetProperty("file", out var f))
        {
            r.File = f.GetInt32();
        }
        if (e.TryGetProperty("noFile", out var nf))
        {
            r.NoFile = nf.GetBoolean();
        }
        if (e.TryGetProperty("pos", out var p))
        {
            r.Pos = p.GetInt32();
        }
        if (e.TryGetProperty("end", out var en))
        {
            r.End = en.GetInt32();
        }
        if (e.TryGetProperty("code", out var c))
        {
            r.Code = c.GetInt32();
        }
        if (e.TryGetProperty("category", out var cat))
        {
            r.Category = (global::Ts.Diagnostics.Category)cat.GetInt32();
        }
        if (e.TryGetProperty("messageKey", out var mk))
        {
            r.MessageKey = mk.GetString() ?? "";
        }
        if (e.TryGetProperty("messageArgs", out var ma) && ma.ValueKind == JsonValueKind.Array)
        {
            var args = new List<string>(ma.GetArrayLength());
            foreach (var a in ma.EnumerateArray())
            {
                args.Add(a.GetString() ?? "");
            }
            r.MessageArgs = args.ToArray();
        }
        if (e.TryGetProperty("messageChain", out var mc) && mc.ValueKind == JsonValueKind.Array)
        {
            var chain = new List<BuildInfoDiagnostic>(mc.GetArrayLength());
            foreach (var d in mc.EnumerateArray())
            {
                chain.Add(ReadFrom(d));
            }
            r.MessageChain = chain.ToArray();
        }
        if (e.TryGetProperty("relatedInformation", out var ri) && ri.ValueKind == JsonValueKind.Array)
        {
            var rel = new List<BuildInfoDiagnostic>(ri.GetArrayLength());
            foreach (var d in ri.EnumerateArray())
            {
                rel.Add(ReadFrom(d));
            }
            r.RelatedInformation = rel.ToArray();
        }
        if (e.TryGetProperty("reportsUnnecessary", out var ru))
        {
            r.ReportsUnnecessary = ru.GetBoolean();
        }
        if (e.TryGetProperty("reportsDeprecated", out var rd))
        {
            r.ReportsDeprecated = rd.GetBoolean();
        }
        if (e.TryGetProperty("skippedOnNoEmit", out var sk))
        {
            r.SkippedOnNoEmit = sk.GetBoolean();
        }
        if (e.TryGetProperty("repopulateInfo", out var rp) && rp.ValueKind == JsonValueKind.Object)
        {
            r.RepopulateInfo = BuildInfoRepopulateInfo.ReadFrom(rp);
        }
        return r;
    }
}

// buildInfo.go:214-219
public sealed class BuildInfoRepopulateInfo
{
    public global::Ts.Ast.RepopulateDiagnosticKind Kind;
    public string ModuleReference = "";
    public ModuleKind Mode;
    public string PackageName = "";

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("kind", (int)Kind); // json:"kind" — NOT omitzero
        if (ModuleReference.Length > 0)
        {
            w.WriteString("moduleReference", ModuleReference);
        }
        if (Mode != ModuleKind.None)
        {
            w.WriteNumber("mode", (int)Mode);
        }
        if (PackageName.Length > 0)
        {
            w.WriteString("packageName", PackageName);
        }
        w.WriteEndObject();
    }

    internal static BuildInfoRepopulateInfo ReadFrom(JsonElement e)
    {
        var r = new BuildInfoRepopulateInfo();
        if (e.TryGetProperty("kind", out var k))
        {
            r.Kind = (global::Ts.Ast.RepopulateDiagnosticKind)k.GetInt32();
        }
        if (e.TryGetProperty("moduleReference", out var m))
        {
            r.ModuleReference = m.GetString() ?? "";
        }
        if (e.TryGetProperty("mode", out var mo))
        {
            r.Mode = (ModuleKind)mo.GetInt32();
        }
        if (e.TryGetProperty("packageName", out var p))
        {
            r.PackageName = p.GetString() ?? "";
        }
        return r;
    }
}

// buildInfo.go:221-255
public sealed class BuildInfoDiagnosticsOfFile
{
    public int FileId;
    public BuildInfoDiagnostic[] Diagnostics = [];

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartArray();
        w.WriteNumberValue(FileId);
        w.WriteStartArray();
        foreach (var d in Diagnostics)
        {
            d.WriteTo(w);
        }
        w.WriteEndArray();
        w.WriteEndArray();
    }

    internal static BuildInfoDiagnosticsOfFile? ReadFrom(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2)
        {
            return null;
        }
        if (e[0].ValueKind != JsonValueKind.Number || e[1].ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var diags = new List<BuildInfoDiagnostic>(e[1].GetArrayLength());
        foreach (var d in e[1].EnumerateArray())
        {
            diags.Add(BuildInfoDiagnostic.ReadFrom(d));
        }
        return new BuildInfoDiagnosticsOfFile { FileId = e[0].GetInt32(), Diagnostics = diags.ToArray() };
    }
}

// buildInfo.go:257-285
public sealed class BuildInfoSemanticDiagnostic
{
    public int FileId;                              // File is not in changedSet and still doesnt have cached diagnostics
    public BuildInfoDiagnosticsOfFile? Diagnostics; // Diagnostics for file

    internal void WriteTo(Utf8JsonWriter w)
    {
        if (FileId != 0)
        {
            w.WriteNumberValue(FileId);
            return;
        }
        Diagnostics!.WriteTo(w);
    }

    internal static BuildInfoSemanticDiagnostic? ReadFrom(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            return new BuildInfoSemanticDiagnostic { FileId = e.GetInt32() };
        }
        var diags = BuildInfoDiagnosticsOfFile.ReadFrom(e);
        if (diags == null)
        {
            return null;
        }
        return new BuildInfoSemanticDiagnostic { Diagnostics = diags };
    }
}

// buildInfo.go:287-335 —
// fileId if pending emit is same as what compilerOptions suggest
// [fileId] if pending emit is only dts file emit
// [fileId, emitKind] if any other type emit is pending
public sealed class BuildInfoFilePendingEmit
{
    public int FileId;
    public FileEmitKind EmitKind;

    internal void WriteTo(Utf8JsonWriter w)
    {
        if (EmitKind == 0)
        {
            w.WriteNumberValue(FileId);
            return;
        }
        if (EmitKind == FileEmitKind.Dts)
        {
            w.WriteStartArray();
            w.WriteNumberValue(FileId);
            w.WriteEndArray();
            return;
        }
        w.WriteStartArray();
        w.WriteNumberValue(FileId);
        w.WriteNumberValue((int)EmitKind);
        w.WriteEndArray();
    }

    internal static BuildInfoFilePendingEmit? ReadFrom(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            return new BuildInfoFilePendingEmit { FileId = e.GetInt32() };
        }
        if (e.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        switch (e.GetArrayLength())
        {
            case 1:
                return new BuildInfoFilePendingEmit { FileId = e[0].GetInt32(), EmitKind = FileEmitKind.Dts };
            case 2:
                return new BuildInfoFilePendingEmit { FileId = e[0].GetInt32(), EmitKind = (FileEmitKind)e[1].GetInt32() };
            default:
                return null;
        }
    }
}

// buildInfo.go:337-439 —
// [fileId, signature] if different from file's signature
// fileId if file wasnt emitted
public sealed class BuildInfoEmitSignature
{
    public int FileId;
    public string Signature = "";    // Signature if it is different from file's Signature
    public bool DiffersOnlyInDtsMap; // true if signature is different only in dtsMap value
    public bool DiffersInOptions;    // true if signature is different in options used to emit file

    // buildInfo.go:346-348
    internal bool NoEmitSignature() => Signature.Length == 0 && !DiffersOnlyInDtsMap && !DiffersInOptions;

    // buildInfo.go:350-367
    internal EmitSignature ToEmitSignature(string path, Func<string, EmitSignature?> loadExistingEmitSignature)
    {
        var signature = "";
        string[]? signatureWithDifferentOptions = null;
        if (DiffersOnlyInDtsMap)
        {
            var info = loadExistingEmitSignature(path);
            signatureWithDifferentOptions = [info!.signature];
        }
        else if (DiffersInOptions)
        {
            signatureWithDifferentOptions = [Signature];
        }
        else
        {
            signature = Signature;
        }
        return new EmitSignature
        {
            signature = signature,
            signatureWithDifferentOptions = signatureWithDifferentOptions,
        };
    }

    // buildInfo.go:369-385
    internal void WriteTo(Utf8JsonWriter w)
    {
        if (NoEmitSignature())
        {
            w.WriteNumberValue(FileId);
            return;
        }
        w.WriteStartArray();
        w.WriteNumberValue(FileId);
        if (DiffersOnlyInDtsMap)
        {
            w.WriteStartArray();
            w.WriteEndArray();
        }
        else if (DiffersInOptions)
        {
            w.WriteStartArray();
            w.WriteStringValue(Signature);
            w.WriteEndArray();
        }
        else
        {
            w.WriteStringValue(Signature);
        }
        w.WriteEndArray();
    }

    // buildInfo.go:387-438
    internal static BuildInfoEmitSignature? ReadFrom(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            return new BuildInfoEmitSignature { FileId = e.GetInt32() };
        }
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2)
        {
            return null;
        }
        if (e[0].ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        var fileId = e[0].GetInt32();
        var signature = "";
        var differsOnlyInDtsMap = false;
        var differsInOptions = false;
        var sigElement = e[1];
        if (sigElement.ValueKind == JsonValueKind.String)
        {
            signature = sigElement.GetString()!;
        }
        else if (sigElement.ValueKind == JsonValueKind.Array)
        {
            switch (sigElement.GetArrayLength())
            {
                case 0:
                    differsOnlyInDtsMap = true;
                    break;
                case 1:
                    if (sigElement[0].ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }
                    signature = sigElement[0].GetString()!;
                    differsInOptions = true;
                    break;
                default:
                    return null;
            }
        }
        else
        {
            return null;
        }
        return new BuildInfoEmitSignature
        {
            FileId = fileId,
            Signature = signature,
            DiffersOnlyInDtsMap = differsOnlyInDtsMap,
            DiffersInOptions = differsInOptions,
        };
    }
}

// buildInfo.go:441-460
public sealed class BuildInfoResolvedRoot
{
    public int Resolved;
    public int Root;

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Resolved);
        w.WriteNumberValue(Root);
        w.WriteEndArray();
    }

    internal static BuildInfoResolvedRoot? ReadFrom(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2)
        {
            return null;
        }
        return new BuildInfoResolvedRoot { Resolved = e[0].GetInt32(), Root = e[1].GetInt32() };
    }
}

// buildInfo.go:462-488
public sealed class BuildInfo
{
    public string Version = "";

    // Common between incremental and tsc -b buildinfo for non incremental programs
    public bool Errors;
    public bool CheckPending;
    public BuildInfoRoot[]? Root;
    public string[]? PackageJsons;
    public string[]? MissingPackageJsons;

    // IncrementalProgram info
    public string[]? FileNames;
    public BuildInfoFileInfo[]? FileInfos;
    public int[][]? FileIdsList;
    public global::Ts.Collections.OrderedMap<string, object?>? Options;
    public BuildInfoReferenceMapEntry[]? ReferencedMap;
    public BuildInfoSemanticDiagnostic[]? SemanticDiagnosticsPerFile;
    public BuildInfoDiagnosticsOfFile[]? EmitDiagnosticsPerFile;
    public int[]? ChangeFileSet;
    public BuildInfoFilePendingEmit[]? AffectedFilesPendingEmit;
    public string LatestChangedDtsFile = ""; // Because this is only output file in the program, we dont need fileId to deduplicate name
    public BuildInfoEmitSignature[]? EmitSignatures;
    public BuildInfoResolvedRoot[]? ResolvedRoot;

    // NonIncrementalProgram info
    public bool SemanticErrors;

    // buildInfo.go:490-492
    public bool IsValidVersion() => Version == global::Ts.Core.TsVersion.Version();

    // buildInfo.go:494-496
    public bool IsIncremental() => FileNames is { Length: > 0 };

    // buildInfo.go:502-507
    internal string FileName(int fileId)
    {
        if (fileId < 1 || FileNames == null || fileId > FileNames.Length)
        {
            return "";
        }
        return FileNames[fileId - 1];
    }

    // buildInfo.go:509-514
    internal BuildInfoFileInfo? FileInfoFor(int fileId)
    {
        if (fileId < 1 || FileInfos == null || fileId > FileInfos.Length)
        {
            return null;
        }
        return FileInfos[fileId - 1];
    }

    // buildInfo.go:516-530
    public CompilerOptions GetCompilerOptions(string buildInfoDirectory)
    {
        var options = new CompilerOptions();
        if (Options != null)
        {
            for (var i = 0; Options.TryEntryAt(i, out var option, out var value); i++)
            {
                if (buildInfoDirectory.Length > 0 &&
                    global::Ts.TsOptions.ParsingHelpers.TryConvertOptionToAbsolutePath(
                        option, value, global::Ts.TsOptions.TsConfigParsing.CommandLineCompilerOptionsMap, buildInfoDirectory, out var result))
                {
                    global::Ts.TsOptions.ParsingHelpers.ParseCompilerOptions(option, result, options);
                    continue;
                }
                global::Ts.TsOptions.ParsingHelpers.ParseCompilerOptions(option, value, options);
            }
        }
        return options;
    }

    // buildInfo.go:532-542
    public bool IsEmitPending(global::Ts.TsOptions.ParsedCommandLine resolved, string buildInfoDirectory)
    {
        // Some of the emit files like source map or dts etc are not yet done
        if (!resolved.CompilerOptions()!.NoEmit.IsTrue() || resolved.CompilerOptions()!.GetEmitDeclarations())
        {
            var pendingEmit = FileEmitKindHelpers.GetPendingEmitKindWithOptions(resolved.CompilerOptions()!, GetCompilerOptions(buildInfoDirectory));
            if (resolved.CompilerOptions()!.NoEmit.IsTrue())
            {
                pendingEmit &= FileEmitKind.DtsErrors;
            }
            return pendingEmit != 0;
        }
        return false;
    }

    // buildInfo.go:544-550
    public IEnumerable<string> GetPackageJsons(string buildInfoDirectory) =>
        GetNormalizedPaths(PackageJsons, buildInfoDirectory);

    public IEnumerable<string> GetMissingPackageJsons(string buildInfoDirectory) =>
        GetNormalizedPaths(MissingPackageJsons, buildInfoDirectory);

    // buildInfo.go:552-560
    private static IEnumerable<string> GetNormalizedPaths(string[]? paths, string buildInfoDirectory)
    {
        foreach (var path in paths ?? [])
        {
            yield return global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(path, buildInfoDirectory);
        }
    }

    // ── JSON (marshal: key order = declaration order, omitzero; compact) ─────────────

    public string Marshal()
    {
        var buffer = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var w = new Utf8JsonWriter(buffer, options))
        {
            w.WriteStartObject();
            if (Version.Length > 0)
            {
                w.WriteString("version", Version);
            }
            if (Errors)
            {
                w.WriteBoolean("errors", true);
            }
            if (CheckPending)
            {
                w.WriteBoolean("checkPending", true);
            }
            WriteArray(w, "root", Root, static (w, v) => v.WriteTo(w));
            WriteStringArray(w, "packageJsons", PackageJsons);
            WriteStringArray(w, "missingPackageJsons", MissingPackageJsons);
            WriteStringArray(w, "fileNames", FileNames);
            WriteArray(w, "fileInfos", FileInfos, static (w, v) => v.WriteTo(w));
            if (FileIdsList is { Length: > 0 })
            {
                w.WritePropertyName("fileIdsList");
                w.WriteStartArray();
                foreach (var list in FileIdsList)
                {
                    w.WriteStartArray();
                    foreach (var id in list)
                    {
                        w.WriteNumberValue(id);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            if (Options != null && Options.TryEntryAt(0, out _, out _))
            {
                w.WritePropertyName("options");
                w.WriteStartObject();
                for (var i = 0; Options.TryEntryAt(i, out var key, out var value); i++)
                {
                    w.WritePropertyName(key);
                    WriteJsonValue(w, value);
                }
                w.WriteEndObject();
            }
            WriteArray(w, "referencedMap", ReferencedMap, static (w, v) => v.WriteTo(w));
            WriteArray(w, "semanticDiagnosticsPerFile", SemanticDiagnosticsPerFile, static (w, v) => v.WriteTo(w));
            WriteArray(w, "emitDiagnosticsPerFile", EmitDiagnosticsPerFile, static (w, v) => v.WriteTo(w));
            if (ChangeFileSet is { Length: > 0 })
            {
                w.WritePropertyName("changeFileSet");
                w.WriteStartArray();
                foreach (var id in ChangeFileSet)
                {
                    w.WriteNumberValue(id);
                }
                w.WriteEndArray();
            }
            WriteArray(w, "affectedFilesPendingEmit", AffectedFilesPendingEmit, static (w, v) => v.WriteTo(w));
            if (LatestChangedDtsFile.Length > 0)
            {
                w.WriteString("latestChangedDtsFile", LatestChangedDtsFile);
            }
            WriteArray(w, "emitSignatures", EmitSignatures, static (w, v) => v.WriteTo(w));
            WriteArray(w, "resolvedRoot", ResolvedRoot, static (w, v) => v.WriteTo(w));
            if (SemanticErrors)
            {
                w.WriteBoolean("semanticErrors", true);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteArray<T>(Utf8JsonWriter w, string name, T[]? values, Action<Utf8JsonWriter, T> writeItem)
    {
        if (values is not { Length: > 0 })
        {
            return;
        }
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var v in values)
        {
            writeItem(w, v);
        }
        w.WriteEndArray();
    }

    private static void WriteStringArray(Utf8JsonWriter w, string name, string[]? values)
    {
        if (values is not { Length: > 0 })
        {
            return;
        }
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }
        w.WriteEndArray();
    }

    // Go json.Marshal of `any` option values (bool/string/float64/[]any); integers print without
    // a fractional part exactly as Go does for whole float64 values via jsnum-style formatting.
    internal static void WriteJsonValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case double d:
                if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15)
                {
                    w.WriteNumberValue((long)d);
                }
                else
                {
                    w.WriteNumberValue(d);
                }
                break;
            case global::Ts.Core.Tristate t:
                // core.Tristate.MarshalJSON (tristate.go:55-64): true/false, unknown → null.
                if (t == global::Ts.Core.Tristate.True)
                {
                    w.WriteBooleanValue(true);
                }
                else if (t == global::Ts.Core.Tristate.False)
                {
                    w.WriteBooleanValue(false);
                }
                else
                {
                    w.WriteNullValue();
                }
                break;
            case string[] strings:
                w.WriteStartArray();
                foreach (var s in strings)
                {
                    w.WriteStringValue(s);
                }
                w.WriteEndArray();
                break;
            case global::Ts.Collections.OrderedMap<string, string[]> map:
                // core.CompilerOptions.Paths — ordered object of string arrays.
                w.WriteStartObject();
                foreach (var (key, arr) in global::Ts.Collections.OrderedMapExtensions.Entries(map))
                {
                    w.WritePropertyName(key);
                    w.WriteStartArray();
                    foreach (var s in arr)
                    {
                        w.WriteStringValue(s);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                break;
            case Enum en:
                // Go int-typed option enums (ScriptTarget/ModuleKind/…) marshal as numbers.
                w.WriteNumberValue(((IConvertible)en).ToInt64(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case IEnumerable<object?> list:
                w.WriteStartArray();
                foreach (var item in list)
                {
                    WriteJsonValue(w, item);
                }
                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(value.ToString());
                break;
        }
    }

    // json.Unmarshal([]byte, &BuildInfo) — returns null on any malformed shape (Go err != nil).
    public static BuildInfo? Unmarshal(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var b = new BuildInfo();
            if (e.TryGetProperty("version", out var v))
            {
                b.Version = v.GetString() ?? "";
            }
            if (e.TryGetProperty("errors", out var errs))
            {
                b.Errors = errs.GetBoolean();
            }
            if (e.TryGetProperty("checkPending", out var cp))
            {
                b.CheckPending = cp.GetBoolean();
            }
            b.Root = ReadArray(e, "root", BuildInfoRoot.ReadFrom);
            b.PackageJsons = ReadStringArray(e, "packageJsons");
            b.MissingPackageJsons = ReadStringArray(e, "missingPackageJsons");
            b.FileNames = ReadStringArray(e, "fileNames");
            b.FileInfos = ReadArray(e, "fileInfos", BuildInfoFileInfo.ReadFrom);
            if (e.TryGetProperty("fileIdsList", out var fil) && fil.ValueKind == JsonValueKind.Array)
            {
                var lists = new List<int[]>(fil.GetArrayLength());
                foreach (var l in fil.EnumerateArray())
                {
                    var ids = new List<int>(l.GetArrayLength());
                    foreach (var id in l.EnumerateArray())
                    {
                        ids.Add(id.GetInt32());
                    }
                    lists.Add(ids.ToArray());
                }
                b.FileIdsList = lists.ToArray();
            }
            if (e.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
            {
                b.Options = new global::Ts.Collections.OrderedMap<string, object?>();
                foreach (var prop in opts.EnumerateObject())
                {
                    b.Options.Set(prop.Name, ReadJsonValue(prop.Value));
                }
            }
            b.ReferencedMap = ReadArray(e, "referencedMap", BuildInfoReferenceMapEntry.ReadFrom);
            b.SemanticDiagnosticsPerFile = ReadArray(e, "semanticDiagnosticsPerFile", BuildInfoSemanticDiagnostic.ReadFrom);
            b.EmitDiagnosticsPerFile = ReadArray(e, "emitDiagnosticsPerFile", BuildInfoDiagnosticsOfFile.ReadFrom);
            if (e.TryGetProperty("changeFileSet", out var cfs) && cfs.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<int>(cfs.GetArrayLength());
                foreach (var id in cfs.EnumerateArray())
                {
                    ids.Add(id.GetInt32());
                }
                b.ChangeFileSet = ids.ToArray();
            }
            b.AffectedFilesPendingEmit = ReadArray(e, "affectedFilesPendingEmit", BuildInfoFilePendingEmit.ReadFrom);
            if (e.TryGetProperty("latestChangedDtsFile", out var lc))
            {
                b.LatestChangedDtsFile = lc.GetString() ?? "";
            }
            b.EmitSignatures = ReadArray(e, "emitSignatures", BuildInfoEmitSignature.ReadFrom);
            b.ResolvedRoot = ReadArray(e, "resolvedRoot", BuildInfoResolvedRoot.ReadFrom);
            if (e.TryGetProperty("semanticErrors", out var se))
            {
                b.SemanticErrors = se.GetBoolean();
            }
            return b;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static T[]? ReadArray<T>(JsonElement e, string name, Func<JsonElement, T?> readItem)
        where T : class
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<T>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            var value = readItem(item);
            if (value == null)
            {
                throw new JsonException($"invalid {name} entry");
            }
            result.Add(value);
        }
        return result.ToArray();
    }

    private static string[]? ReadStringArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            result.Add(item.GetString() ?? "");
        }
        return result.ToArray();
    }

    // Go json.Unmarshal into `any`: objects→map, arrays→[]any, numbers→float64, strings, bools.
    internal static object? ReadJsonValue(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return e.GetDouble();
            case JsonValueKind.Array:
            {
                var list = new List<object?>(e.GetArrayLength());
                foreach (var item in e.EnumerateArray())
                {
                    list.Add(ReadJsonValue(item));
                }
                return list;
            }
            case JsonValueKind.Object:
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in e.EnumerateObject())
                {
                    map[prop.Name] = ReadJsonValue(prop.Value);
                }
                return map;
            }
            default:
                return null;
        }
    }
}

// buildInfo.go:562-611
public sealed partial class BuildInfoRootInfoReaderHost
{
}

public static class BuildInfoRootInfoReaderExtensions
{
    // buildInfo.go:562-611 — GetBuildInfoRootInfoReader.
    public static BuildInfoRootInfoReader GetBuildInfoRootInfoReader(this BuildInfo b, string buildInfoDirectory, bool useCaseSensitiveFileNames)
    {
        var resolvedRootFileInfos = new Dictionary<global::Ts.TsPath.Path, BuildInfoFileInfo>(b.FileNames?.Length ?? 0);
        // Roots of the File
        var rootToResolved = new global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, global::Ts.TsPath.Path>(b.FileNames?.Length ?? 0);
        var resolvedToRoot = new Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>(b.ResolvedRoot?.Length ?? 0);
        global::Ts.TsPath.Path ToPath(string fileName) =>
            global::Ts.TsPath.TsPath.ToPath(fileName, buildInfoDirectory, useCaseSensitiveFileNames);

        // Create map from resolvedRoot to Root
        foreach (var resolved in b.ResolvedRoot ?? [])
        {
            var resolvedRoot = b.FileName(resolved.Resolved);
            var root = b.FileName(resolved.Root);
            if (resolvedRoot.Length > 0 && root.Length > 0)
            {
                resolvedToRoot[ToPath(resolvedRoot)] = ToPath(root);
            }
        }

        void AddRoot(string resolvedRoot, BuildInfoFileInfo? fileInfo)
        {
            if (resolvedRoot.Length == 0)
            {
                return;
            }
            var resolvedRootPath = ToPath(resolvedRoot);
            if (resolvedToRoot.TryGetValue(resolvedRootPath, out var rootPath))
            {
                rootToResolved.Set(rootPath, resolvedRootPath);
            }
            else
            {
                rootToResolved.Set(resolvedRootPath, resolvedRootPath);
            }
            if (fileInfo != null)
            {
                resolvedRootFileInfos[resolvedRootPath] = fileInfo;
            }
        }

        foreach (var root in b.Root ?? [])
        {
            if (root.NonIncremental.Length > 0)
            {
                AddRoot(root.NonIncremental, null);
            }
            else if (root.End == 0)
            {
                AddRoot(b.FileName(root.Start), b.FileInfoFor(root.Start));
            }
            else
            {
                for (var i = root.Start; i <= root.End; i++)
                {
                    AddRoot(b.FileName(i), b.FileInfoFor(i));
                }
            }
        }

        return new BuildInfoRootInfoReader(resolvedRootFileInfos, rootToResolved);
    }
}

// buildInfo.go:613-630
public sealed class BuildInfoRootInfoReader
{
    private readonly Dictionary<global::Ts.TsPath.Path, BuildInfoFileInfo> resolvedRootFileInfos;
    private readonly global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, global::Ts.TsPath.Path> rootToResolved;

    internal BuildInfoRootInfoReader(
        Dictionary<global::Ts.TsPath.Path, BuildInfoFileInfo> resolvedRootFileInfos,
        global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, global::Ts.TsPath.Path> rootToResolved)
    {
        this.resolvedRootFileInfos = resolvedRootFileInfos;
        this.rootToResolved = rootToResolved;
    }

    // buildInfo.go:618-626
    public (BuildInfoFileInfo? Info, global::Ts.TsPath.Path Path) GetBuildInfoFileInfo(global::Ts.TsPath.Path inputFilePath)
    {
        if (resolvedRootFileInfos.TryGetValue(inputFilePath, out var info))
        {
            return (info, inputFilePath);
        }
        if (rootToResolved.TryGet(inputFilePath, out var resolved))
        {
            resolvedRootFileInfos.TryGetValue(resolved, out var resolvedInfo);
            return (resolvedInfo, resolved);
        }
        return (null, default);
    }

    // buildInfo.go:628-630
    public IEnumerable<global::Ts.TsPath.Path> Roots()
    {
        for (var i = 0; rootToResolved.TryEntryAt(i, out var key, out _); i++)
        {
            yield return key;
        }
    }
}

public static class BuildInfoHelpers
{
    // buildInfo.go:498-500
    public static bool IsBuildInfoFileNameDefaultLibrary(string fileName)
    {
        return !global::Ts.TsPath.TsPath.PathIsRelative(fileName) && !global::Ts.TsPath.TsPath.PathIsAbsolute(fileName);
    }
}
