// Ported from tsgo internal/execute/incremental/snapshot.go (remainder: the diagnostic
// with-file-name model, repopulation, and the snapshot struct itself). The pure data-model
// half (FileInfo/ComputeHash/FileEmitKind/EmitSignature) lives in Snapshot.Model.cs.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Ts.Collections;
using Ts.Core;

// PORT-NOTE: several fields are populated only by the buildinfotosnapshot/programtosnapshot
// converters that land in the next construction batch; CS0649 is suppressed until then.
#pragma warning disable CS0649

namespace Ts.Execute.Incremental;

using TsPathNs = global::Ts.TsPath;
using PathT = global::Ts.TsPath.Path;

// snapshot.go:133-149
internal sealed class BuildInfoDiagnosticWithFileName
{
    // filename if it is for a File thats other than its stored for
    internal PathT file;
    internal bool noFile;
    internal int pos;
    internal int end;
    internal int code;
    internal global::Ts.Diagnostics.Category category;
    internal string messageKey = "";
    internal string[]? messageArgs;
    internal BuildInfoDiagnosticWithFileName[]? messageChain;
    internal BuildInfoDiagnosticWithFileName[]? relatedInformation;
    internal bool reportsUnnecessary;
    internal bool reportsDeprecated;
    internal bool skippedOnNoEmit;
    internal global::Ts.Ast.RepopulateDiagnosticInfo? repopulateInfo;

    // snapshot.go:156-189
    internal global::Ts.Ast.Diagnostic ToDiagnostic(global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile? file)
    {
        global::Ts.Ast.SourceFile? fileForDiagnostic = null;
        if (this.file != default)
        {
            fileForDiagnostic = p.GetSourceFileByPath(this.file);
        }
        else if (!noFile)
        {
            fileForDiagnostic = file;
        }

        if (repopulateInfo != null)
        {
            return RepopulateDiagnosticChain(this, p, fileForDiagnostic);
        }

        List<global::Ts.Ast.Diagnostic>? chain = null;
        foreach (var msg in messageChain ?? [])
        {
            (chain ??= new()).Add(msg.ToDiagnostic(p, fileForDiagnostic));
        }
        List<global::Ts.Ast.Diagnostic>? related = null;
        foreach (var info in relatedInformation ?? [])
        {
            (related ??= new()).Add(info.ToDiagnostic(p, fileForDiagnostic));
        }
        return global::Ts.Ast.Diagnostic.NewDiagnosticFromSerialized(
            fileForDiagnostic,
            new TextRange(pos, end),
            code,
            category,
            messageKey,
            messageArgs,
            chain?.ToArray(),
            related?.ToArray(),
            reportsUnnecessary,
            reportsDeprecated,
            skippedOnNoEmit);
    }

    // snapshot.go:191-204 — repopulateDiagnosticChain recomputes a diagnostic chain entry that
    // depends on program state which may have changed between incremental builds.
    internal static global::Ts.Ast.Diagnostic RepopulateDiagnosticChain(
        BuildInfoDiagnosticWithFileName b, global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile? file)
    {
        var info = b.repopulateInfo!;
        switch (info.Kind)
        {
            case global::Ts.Ast.RepopulateDiagnosticKind.RepopulateModeMismatch:
                return RepopulateModeMismatchChain(b, p, file);
            case global::Ts.Ast.RepopulateDiagnosticKind.RepopulateModuleNotFound:
                return RepopulateModuleNotFoundChain(b, p, file, info);
            default:
                // Fall back to using the stored (possibly stale) data
                return b.ToDiagnosticWithoutRepopulate(p, file);
        }
    }

    // snapshot.go:206-228
    private global::Ts.Ast.Diagnostic ToDiagnosticWithoutRepopulate(global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile? file)
    {
        List<global::Ts.Ast.Diagnostic>? chain = null;
        foreach (var msg in messageChain ?? [])
        {
            (chain ??= new()).Add(msg.ToDiagnostic(p, file));
        }
        List<global::Ts.Ast.Diagnostic>? related = null;
        foreach (var info in relatedInformation ?? [])
        {
            (related ??= new()).Add(info.ToDiagnostic(p, file));
        }
        return global::Ts.Ast.Diagnostic.NewDiagnosticFromSerialized(
            file,
            new TextRange(pos, end),
            code,
            category,
            messageKey,
            messageArgs,
            chain?.ToArray(),
            related?.ToArray(),
            reportsUnnecessary,
            reportsDeprecated,
            skippedOnNoEmit);
    }

    // snapshot.go:230-255
    private static global::Ts.Ast.Diagnostic RepopulateModeMismatchChain(
        BuildInfoDiagnosticWithFileName b, global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile? file)
    {
        if (file == null)
        {
            return b.ToDiagnosticWithoutRepopulate(p, file);
        }

        var details = global::Ts.Checker.Checker.CreateModeMismatchDetails(p, file);

        List<global::Ts.Ast.Diagnostic>? nextChain = null;
        foreach (var msg in b.messageChain ?? [])
        {
            (nextChain ??= new()).Add(msg.ToDiagnostic(p, file));
        }

        return global::Ts.Ast.Diagnostic.NewDiagnosticFromSerialized(
            file,
            new TextRange(b.pos, b.end),
            details.Message.Code,
            details.Message.Category,
            details.Message.Key,
            global::Ts.Diagnostics.Diagnostics.StringifyArgs(details.Args ?? []),
            nextChain?.ToArray(),
            null,
            false,
            false,
            false);
    }

    // snapshot.go:257-287
    private static global::Ts.Ast.Diagnostic RepopulateModuleNotFoundChain(
        BuildInfoDiagnosticWithFileName b, global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile? file, global::Ts.Ast.RepopulateDiagnosticInfo info)
    {
        if (file == null)
        {
            return b.ToDiagnosticWithoutRepopulate(p, file);
        }

        var packageName = info.PackageName;
        if (packageName.Length == 0)
        {
            packageName = info.ModuleReference;
        }

        var details = global::Ts.Checker.Checker.CreateModuleNotFoundChain(p, file, info.ModuleReference, info.Mode, packageName);

        List<global::Ts.Ast.Diagnostic>? nextChain = null;
        foreach (var msg in b.messageChain ?? [])
        {
            (nextChain ??= new()).Add(msg.ToDiagnostic(p, file));
        }

        return global::Ts.Ast.Diagnostic.NewDiagnosticFromSerialized(
            file,
            new TextRange(b.pos, b.end),
            details.Message.Code,
            details.Message.Category,
            details.Message.Key,
            global::Ts.Diagnostics.Diagnostics.StringifyArgs(details.Args ?? []),
            nextChain?.ToArray(),
            null,
            false,
            false,
            false);
    }
}

// snapshot.go:151-154, 289-298
internal sealed class DiagnosticsOrBuildInfoDiagnosticsWithFileName
{
    internal global::Ts.Ast.Diagnostic[]? diagnostics;
    internal BuildInfoDiagnosticWithFileName[]? buildInfoDiagnostics;

    // snapshot.go:289-298
    internal global::Ts.Ast.Diagnostic[] GetDiagnostics(global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile? file)
    {
        if (diagnostics != null)
        {
            return diagnostics;
        }
        // Convert and cache the diagnostics
        var result = new global::Ts.Ast.Diagnostic[buildInfoDiagnostics?.Length ?? 0];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = buildInfoDiagnostics![i].ToDiagnostic(p, file);
        }
        diagnostics = result;
        return diagnostics;
    }
}

// snapshot.go:300-346
internal sealed class Snapshot
{
    // These are the fields that get serialized

    // Information of the file eg. its version, signature etc
    internal readonly SyncMap<PathT, FileInfo> fileInfos = new();
    internal CompilerOptions options = null!;
    //  Contains the map of ReferencedSet=Referenced files of the file if module emit is enabled
    internal readonly ReferenceMap referencedMap = new();
    // Cache of semantic diagnostics for files with their Path being the key
    internal readonly SyncMap<PathT, DiagnosticsOrBuildInfoDiagnosticsWithFileName> semanticDiagnosticsPerFile = new();
    // Cache of dts emit diagnostics for files with their Path being the key
    internal readonly SyncMap<PathT, DiagnosticsOrBuildInfoDiagnosticsWithFileName> emitDiagnosticsPerFile = new();
    // The map has key by source file's path that has been changed
    internal SyncSet<PathT> changedFilesSet = new(); // reassigned by affectedFilesHandler.updateSnapshot (go:351)
    // Files pending to be emitted
    internal readonly SyncMap<PathT, FileEmitKind> affectedFilesPendingEmit = new();
    // Name of the file whose dts was the latest to change
    internal string latestChangedDtsFile = "";
    // Hash of d.ts emitted for the file, use to track when emit of d.ts changes
    internal readonly SyncMap<PathT, EmitSignature> emitSignatures = new();
    // Recorded if program had errors that need to be reported even with --noCheck
    internal Tristate hasErrors;
    // Recorded if program had semantic errors only for non incremental build
    internal bool hasSemanticErrors;
    // If semantic diagnostic check is pending
    internal bool checkPending;
    // Looked up package.json files from
    internal string[]? packageJsons;
    internal string[]? missingPackageJsons;

    // Additional fields that are not serialized but needed to track state

    // true if build info emit is pending (atomic.Bool → int-based Interlocked flag)
    private int buildInfoEmitPendingFlag;
    internal Tristate hasErrorsFromOldState;
    internal bool hasSemanticErrorsFromOldState;
    private readonly Lock allFilesExcludingDefaultLibraryFileLock = new();
    internal string[]? packageJsonsFromOldState;
    internal string[]? missingPackageJsonsFromOldState;
    //  Cache of all files excluding default library file for the current program
    internal global::Ts.Ast.SourceFile[]? allFilesExcludingDefaultLibraryFile;
    internal bool hasChangedDtsFile;
    internal bool hasEmitDiagnostics;

    // Used with testing to add text of hash for better comparison
    internal bool hashWithText;

    internal bool BuildInfoEmitPending
    {
        get => Volatile.Read(ref buildInfoEmitPendingFlag) != 0;
        set => Volatile.Write(ref buildInfoEmitPendingFlag, value ? 1 : 0);
    }

    // snapshot.go:348-351
    internal void AddFileToChangeSet(PathT filePath)
    {
        changedFilesSet.Add(filePath);
        BuildInfoEmitPending = true;
    }

    // snapshot.go:353-360
    internal void AddFileToAffectedFilesPendingEmit(PathT filePath, FileEmitKind emitKind)
    {
        affectedFilesPendingEmit.TryLoad(filePath, out var existingKind);
        affectedFilesPendingEmit.Store(filePath, existingKind | emitKind);
        if ((emitKind & FileEmitKind.DtsErrors) != 0)
        {
            emitDiagnosticsPerFile.Delete(filePath);
        }
        BuildInfoEmitPending = true;
    }

    // snapshot.go:362-381
    internal global::Ts.Ast.SourceFile[] GetAllFilesExcludingDefaultLibraryFile(
        global::Ts.Compiler.Program program, global::Ts.Ast.SourceFile? firstSourceFile)
    {
        // sync.Once → double-checked lock (single-shot compute; plan/05 §11).
        var cached = Volatile.Read(ref allFilesExcludingDefaultLibraryFile);
        if (cached != null)
        {
            return cached;
        }
        lock (allFilesExcludingDefaultLibraryFileLock)
        {
            cached = allFilesExcludingDefaultLibraryFile;
            if (cached != null)
            {
                return cached;
            }
            var files = program.SourceFiles();
            var result = new List<global::Ts.Ast.SourceFile>(files.Length);
            void AddSourceFile(global::Ts.Ast.SourceFile file)
            {
                if (!program.IsSourceFileDefaultLibrary(file.Path()))
                {
                    result.Add(file);
                }
            }
            if (firstSourceFile != null)
            {
                AddSourceFile(firstSourceFile);
            }
            foreach (var file in files)
            {
                if (file != firstSourceFile)
                {
                    AddSourceFile(file);
                }
            }
            Volatile.Write(ref allFilesExcludingDefaultLibraryFile, result.ToArray());
            return allFilesExcludingDefaultLibraryFile!;
        }
    }

    // snapshot.go:390-397
    internal string ComputeSignatureWithDiagnostics(global::Ts.Ast.SourceFile file, string text, global::Ts.Compiler.WriteFileData data)
    {
        var builder = new StringBuilder();
        builder.Append(GetTextHandlingSourceMapForSignature(text, data));
        foreach (var diag in data.Diagnostics ?? [])
        {
            DiagnosticToStringBuilder(diag, file, builder);
        }
        return ComputeHash(builder.ToString());
    }

    // snapshot.go:383-388
    internal static string GetTextHandlingSourceMapForSignature(string text, global::Ts.Compiler.WriteFileData data)
    {
        if (data.SourceMapUrlPos != -1)
        {
            return text[..data.SourceMapUrlPos];
        }
        return text;
    }

    // snapshot.go:399-428
    internal static void DiagnosticToStringBuilder(global::Ts.Ast.Diagnostic? diagnostic, global::Ts.Ast.SourceFile file, StringBuilder builder)
    {
        if (diagnostic == null)
        {
            return;
        }
        builder.Append('\n');
        if (diagnostic.File() != file)
        {
            builder.Append(TsPathNs.TsPath.EnsurePathIsNonModuleName(TsPathNs.TsPath.GetRelativePathFromDirectory(
                TsPathNs.TsPath.GetDirectoryPath(file.Path().ToString()),
                diagnostic.File()!.Path().ToString(),
                new TsPathNs.ComparePathsOptions())));
        }
        if (diagnostic.File() != null)
        {
            builder.Append($"({diagnostic.Loc().Pos},{diagnostic.Len()}): ");
        }
        builder.Append(global::Ts.Diagnostics.CategoryExtensions.Name(diagnostic.Category()));
        builder.Append($"{diagnostic.Code()}: ");
        builder.Append(diagnostic.MessageKey());
        builder.Append('\n');
        foreach (var arg in diagnostic.MessageArgs() ?? [])
        {
            builder.Append(arg);
            builder.Append('\n');
        }
        foreach (var chain in diagnostic.MessageChain() ?? [])
        {
            DiagnosticToStringBuilder(chain, file, builder);
        }
        foreach (var info in diagnostic.RelatedInformation() ?? [])
        {
            DiagnosticToStringBuilder(info, file, builder);
        }
    }

    // snapshot.go:430-432
    internal string ComputeHash(string text) => IncrementalHash.ComputeHash(text, hashWithText);

    // snapshot.go:434-440
    internal bool CanUseIncrementalState()
    {
        if (!options.IsIncremental() && options.Build.IsTrue())
        {
            // If not incremental build (with tsc -b), we don't need to track state except diagnostics per file so we can use it
            return false;
        }
        return true;
    }
}
