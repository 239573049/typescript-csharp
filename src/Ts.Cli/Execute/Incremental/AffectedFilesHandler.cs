// Ported from tsgo internal/execute/incremental/affectedfileshandler.go.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Collections;
using Ts.Core;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// affectedfileshandler.go:18-22
internal sealed class DtsMayChange : Dictionary<PathT, FileEmitKind>
{
    internal void AddFileToAffectedFilesPendingEmit(PathT filePath, FileEmitKind emitKind) => this[filePath] = emitKind;
}

// affectedfileshandler.go:24-28
internal sealed class UpdatedSignature
{
    internal readonly Lock mu = new();
    internal string signature = "";
    internal SignatureUpdateKind kind;
}

// affectedfileshandler.go:30-39
internal sealed class AffectedFilesHandler
{
    internal CancellationToken ctx;
    internal Program program = null!;
    private int hasAllFilesExcludingDefaultLibraryFileFlag;
    internal readonly SyncMap<PathT, UpdatedSignature> updatedSignatures = new();
    internal readonly List<DtsMayChange> dtsMayChange = new();
    internal readonly SyncSet<PathT> filesToRemoveDiagnostics = new();
    // sync.Once → double-checked lock (Ts.Compiler's Once helper is assembly-internal).
    private readonly Lock cleanedDiagnosticsOfLibFilesLock = new();
    private bool cleanedDiagnosticsOfLibFilesDone;
    internal readonly SyncMap<PathT, bool> seenFileAndReferences = new();

    private bool HasAllFilesExcludingDefaultLibraryFile
    {
        get => Volatile.Read(ref hasAllFilesExcludingDefaultLibraryFileFlag) != 0;
        set => Volatile.Write(ref hasAllFilesExcludingDefaultLibraryFileFlag, value ? 1 : 0);
    }

    // affectedfileshandler.go:41-45
    internal DtsMayChange GetDtsMayChange(PathT affectedFilePath, FileEmitKind affectedFileEmitKind)
    {
        var result = new DtsMayChange { [affectedFilePath] = affectedFileEmitKind };
        dtsMayChange.Add(result);
        return result;
    }

    // affectedfileshandler.go:47-53
    internal bool IsChangedSignature(PathT path)
    {
        updatedSignatures.TryLoad(path, out var newSignature);
        // This method is called after updating signatures of that path, so signature is present in updatedSignatures
        // And is already calculated, so no need to lock and unlock mutex on the entry
        program.snapshot.fileInfos.TryLoad(path, out var oldInfo);
        return newSignature!.signature != oldInfo!.Signature();
    }

    // affectedfileshandler.go:55-57
    internal void RemoveSemanticDiagnosticsOf(PathT path) => filesToRemoveDiagnostics.Add(path);

    // affectedfileshandler.go:59-67
    internal void RemoveDiagnosticsOfLibraryFiles()
    {
        if (Volatile.Read(ref cleanedDiagnosticsOfLibFilesDone))
        {
            return;
        }
        lock (cleanedDiagnosticsOfLibFilesLock)
        {
            if (cleanedDiagnosticsOfLibFilesDone)
            {
                return;
            }
            foreach (var file in program.SourceFiles())
            {
                if (program.program.IsSourceFileDefaultLibrary(file.Path()) && !program.program.SkipTypeChecking(file, true))
                {
                    RemoveSemanticDiagnosticsOf(file.Path());
                }
            }
            Volatile.Write(ref cleanedDiagnosticsOfLibFilesDone, true);
        }
    }

    // affectedfileshandler.go:69-83
    internal string ComputeDtsSignature(global::Ts.Ast.SourceFile file)
    {
        var signature = "";
        program.program.Emit(ctx, new global::Ts.Compiler.EmitOptions
        {
            TargetSourceFile = file,
            EmitOnly = global::Ts.Compiler.EmitOnly.ForcedDts,
            WriteFile = (fileName, text, data) =>
            {
                if (!TsPathApi.IsDeclarationFileName(fileName))
                {
                    throw new InvalidOperationException("File extension for signature expected to be dts, got : " + fileName);
                }
                signature = program.snapshot.ComputeSignatureWithDiagnostics(file, text, data!);
            },
        });
        return signature;
    }

    // affectedfileshandler.go:85-108
    internal bool UpdateShapeSignature(global::Ts.Ast.SourceFile file, bool useFileVersionAsSignature)
    {
        var update = new UpdatedSignature();
        lock (update.mu)
        {
            // If we have cached the result for this file, that means hence forth we should assume file shape is uptodate
            var (existing, loaded) = updatedSignatures.LoadOrStore(file.Path(), update);
            if (loaded)
            {
                // Ensure calculations for existing ones are complete before using the value
                lock (existing.mu)
                {
                    return false;
                }
            }

            program.snapshot.fileInfos.TryLoad(file.Path(), out var info);
            var prevSignature = info!.Signature();
            if (!file.IsDeclarationFile && !useFileVersionAsSignature)
            {
                update.signature = ComputeDtsSignature(file);
            }
            // Default is to use file version as signature
            if (update.signature.Length == 0)
            {
                update.signature = info.Version();
                update.kind = SignatureUpdateKind.UsedVersion;
            }
            return update.signature != prevSignature;
        }
    }

    // affectedfileshandler.go:110-146
    internal global::Ts.Ast.SourceFile[] GetFilesAffectedBy(PathT path)
    {
        var file = program.program.GetSourceFileByPath(path);
        if (file == null)
        {
            return [];
        }

        if (!UpdateShapeSignature(file, false))
        {
            return [file];
        }

        program.snapshot.fileInfos.TryLoad(file.Path(), out var info);
        if (info!.AffectsGlobalScope())
        {
            HasAllFilesExcludingDefaultLibraryFile = true;
            program.snapshot.GetAllFilesExcludingDefaultLibraryFile(program.program, file);
        }

        if (program.snapshot.options.IsolatedModules.IsTrue())
        {
            return [file];
        }

        // Now we need to if each file in the referencedBy list has a shape change as well.
        // Because if so, its own referencedBy files need to be saved as well to make the
        // emitting result consistent with files on disk.
        var seenFileNamesMap = ForEachFileReferencedBy(
            file,
            (currentFile, currentPath) =>
            {
                // If the current file is not nil and has a shape change, we need to queue it for processing
                if (currentFile != null && UpdateShapeSignature(currentFile, false))
                {
                    return (true, false);
                }
                return (false, false);
            });
        // Return array of values that needs emit
        return seenFileNamesMap.Values.Where(f => f != null).Select(f => f!).ToArray(); // map-iter: 序无关 (results feed a SyncSet)
    }

    // affectedfileshandler.go:148-174
    internal Dictionary<PathT, global::Ts.Ast.SourceFile?> ForEachFileReferencedBy(
        global::Ts.Ast.SourceFile file,
        Func<global::Ts.Ast.SourceFile?, PathT, (bool QueueForFile, bool FastReturn)> fn)
    {
        // Now we need to if each file in the referencedBy list has a shape change as well.
        // Because if so, its own referencedBy files need to be saved as well to make the
        // emitting result consistent with files on disk.
        var seenFileNamesMap = new Dictionary<PathT, global::Ts.Ast.SourceFile?>();
        // Start with the paths this file was referenced by
        seenFileNamesMap[file.Path()] = file;
        var queue = new List<PathT>(program.snapshot.referencedMap.GetReferencedBy(file.Path()));
        while (queue.Count > 0)
        {
            var currentPath = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            if (!seenFileNamesMap.ContainsKey(currentPath))
            {
                var currentFile = program.program.GetSourceFileByPath(currentPath);
                seenFileNamesMap[currentPath] = currentFile;
                var (queueForFile, fastReturn) = fn(currentFile, currentPath);
                if (fastReturn)
                {
                    return seenFileNamesMap;
                }
                if (queueForFile)
                {
                    foreach (var reference in program.snapshot.referencedMap.GetReferencedBy(currentFile!.Path()))
                    {
                        queue.Add(reference);
                    }
                }
            }
        }
        return seenFileNamesMap;
    }

    // affectedfileshandler.go:176-268
    // Handles semantic diagnostics and dts emit for affectedFile and files, that are referencing modules that export entities from affected file
    // This is because even though js emit doesnt change, dts emit / type used can change resulting in need for dts emit and js change
    internal void HandleDtsMayChangeOfAffectedFile(DtsMayChange dtsMayChange, global::Ts.Ast.SourceFile affectedFile)
    {
        RemoveSemanticDiagnosticsOf(affectedFile.Path());

        // If affected files is everything except default library, then nothing more to do
        if (HasAllFilesExcludingDefaultLibraryFile)
        {
            RemoveDiagnosticsOfLibraryFiles();
            // When a change affects the global scope, all files are considered to be affected without updating their signature
            // That means when affected file is handled, its signature can be out of date
            // To avoid this, ensure that we update the signature for any affected file in this scenario.
            UpdateShapeSignature(affectedFile, false);
            return;
        }

        if (program.snapshot.options.AssumeChangesOnlyAffectDirectDependencies.IsTrue())
        {
            return;
        }

        // Iterate on referencing modules that export entities from affected file and delete diagnostics and add pending emit
        // If there was change in signature (dts output) for the changed file,
        // then only we need to handle pending file emit
        if (!program.snapshot.changedFilesSet.Has(affectedFile.Path()) ||
            !IsChangedSignature(affectedFile.Path()))
        {
            return;
        }

        // At this point affectedFile is actually one of the changed files
        // that has some change in its .d.ts signature.

        // Since isolated modules dont change js files, files affected by change in signature is itself
        // But we need to cleanup semantic diagnostics and queue dts emit for affected files
        if (program.snapshot.options.IsolatedModules.IsTrue())
        {
            ForEachFileReferencedBy(
                affectedFile,
                (currentFile, currentPath) =>
                {
                    if (HandleDtsMayChangeOfGlobalScope(dtsMayChange, currentPath, /*invalidateJsFiles*/ false))
                    {
                        return (false, true);
                    }
                    HandleDtsMayChangeOf(dtsMayChange, currentPath, /*invalidateJsFiles*/ false);
                    if (IsChangedSignature(currentPath))
                    {
                        return (true, false);
                    }
                    return (false, false);
                });
        }

        var invalidateJsFiles = false;
        global::Ts.Checker.Checker? typeChecker = null;
        Action? done = null;
        // If exported const enum, we need to ensure that js files are emitted as well since the const enum value changed
        if (affectedFile.Symbol != null)
        {
            foreach (var (_, exported) in affectedFile.Symbol.Exports ?? new Dictionary<string, global::Ts.Ast.Symbol>()) // map-iter: 序无关 (boolean accumulation)
            {
                if ((exported.Flags & global::Ts.Ast.SymbolFlags.ConstEnum) != 0)
                {
                    invalidateJsFiles = true;
                    break;
                }
                if (typeChecker == null)
                {
                    (typeChecker, done) = program.program.GetTypeCheckerForFileExclusive(ctx, affectedFile);
                }
                var aliased = global::Ts.Checker.Checker.SkipAlias(exported, typeChecker);
                if (aliased == exported || aliased == null)
                {
                    continue;
                }
                if ((aliased.Flags & global::Ts.Ast.SymbolFlags.ConstEnum) != 0)
                {
                    if ((aliased.Declarations ?? []).Any(d => global::Ts.Ast.AstUtilities.GetSourceFileOfNode(d) == affectedFile))
                    {
                        invalidateJsFiles = true;
                        break;
                    }
                }
            }
        }
        done?.Invoke();

        // Go through files that reference affected file and handle dts emit and semantic diagnostics for them and their references
        foreach (var fileReferencingChangedFile in program.snapshot.referencedMap.GetReferencedBy(affectedFile.Path()))
        {
            if (HandleDtsMayChangeOfGlobalScope(dtsMayChange, fileReferencingChangedFile, invalidateJsFiles))
            {
                return;
            }
            // Since references of changed file = affected files - we would have already handled d.ts emit and semantic diagnostics
            // for those files. Now we need to handle files referencing those affected files to ensure correctness.
            foreach (var fileReferencingAffectedFile in program.snapshot.referencedMap.GetReferencedBy(fileReferencingChangedFile))
            {
                if (HandleDtsMayChangeOfFileAndReferences(dtsMayChange, fileReferencingAffectedFile, invalidateJsFiles))
                {
                    return;
                }
            }
        }
    }

    // affectedfileshandler.go:270-290
    internal bool HandleDtsMayChangeOfFileAndReferences(DtsMayChange dtsMayChange, PathT filePath, bool invalidateJsFiles)
    {
        var (existing, loaded) = seenFileAndReferences.LoadOrStore(filePath, invalidateJsFiles);
        if (loaded && (existing || !invalidateJsFiles))
        {
            return false;
        }
        else if (loaded && invalidateJsFiles)
        {
            seenFileAndReferences.Store(filePath, true);
        }

        if (HandleDtsMayChangeOfGlobalScope(dtsMayChange, filePath, invalidateJsFiles))
        {
            return true;
        }
        HandleDtsMayChangeOf(dtsMayChange, filePath, invalidateJsFiles);

        // Remove the diagnostics of files that import this file and
        // any files that are referenced by it (directly or indirectly)
        foreach (var referencingFilePath in program.snapshot.referencedMap.GetReferencedBy(filePath))
        {
            if (HandleDtsMayChangeOfFileAndReferences(dtsMayChange, referencingFilePath, invalidateJsFiles))
            {
                return true;
            }
        }
        return false;
    }

    // affectedfileshandler.go:292-302
    internal bool HandleDtsMayChangeOfGlobalScope(DtsMayChange dtsMayChange, PathT filePath, bool invalidateJsFiles)
    {
        if (!program.snapshot.fileInfos.TryLoad(filePath, out var info) || !info!.AffectsGlobalScope())
        {
            return false;
        }
        // Every file needs to be handled
        foreach (var file in program.snapshot.GetAllFilesExcludingDefaultLibraryFile(program.program, null))
        {
            HandleDtsMayChangeOf(dtsMayChange, file.Path(), invalidateJsFiles);
        }
        RemoveDiagnosticsOfLibraryFiles();
        return true;
    }

    // affectedfileshandler.go:304-327
    // Handle the dts may change, so they need to be added to pending emit if dts emit is enabled,
    // Also we need to make sure signature is updated for these files
    internal void HandleDtsMayChangeOf(DtsMayChange dtsMayChange, PathT path, bool invalidateJsFiles)
    {
        if (program.snapshot.changedFilesSet.Has(path))
        {
            return;
        }
        var file = program.program.GetSourceFileByPath(path);
        if (file == null)
        {
            return;
        }
        RemoveSemanticDiagnosticsOf(path);
        // Even though the js emit doesnt change and we are already handling dts emit and semantic diagnostics
        // we need to update the signature to reflect correctness of the signature(which is output d.ts emit) of this file
        // This ensures that we dont later during incremental builds considering wrong signature.
        // Eg where this also is needed to ensure that .tsbuildinfo generated by incremental build should be same as if it was first fresh build
        // But we avoid expensive full shape computation, as using file version as shape is enough for correctness.
        UpdateShapeSignature(file, true);
        // If not dts emit, nothing more to do
        if (invalidateJsFiles)
        {
            dtsMayChange.AddFileToAffectedFilesPendingEmit(path, FileEmitKindHelpers.GetFileEmitKind(program.snapshot.options));
        }
        else if (program.snapshot.options.GetEmitDeclarations())
        {
            dtsMayChange.AddFileToAffectedFilesPendingEmit(path, program.snapshot.options.DeclarationMap.IsTrue() ? FileEmitKind.AllDts : FileEmitKind.Dts);
        }
    }

    // affectedfileshandler.go:329-353
    internal void UpdateSnapshot()
    {
        if (ctx.IsCancellationRequested)
        {
            return;
        }
        updatedSignatures.Range((filePath, update) =>
        {
            if (program.snapshot.fileInfos.TryLoad(filePath, out var info))
            {
                info!.signature = update.signature;
                if (program.testingData != null)
                {
                    program.testingData.UpdatedSignatureKinds[filePath] = update.kind;
                }
            }
            return true;
        });
        filesToRemoveDiagnostics.Range(file =>
        {
            program.snapshot.semanticDiagnosticsPerFile.Delete(file);
            return true;
        });
        foreach (var change in dtsMayChange)
        {
            foreach (var (filePath, emitKind) in change) // map-iter: 序无关 (SyncMap stores)
            {
                program.snapshot.AddFileToAffectedFilesPendingEmit(filePath, emitKind);
            }
        }
        program.snapshot.changedFilesSet = new SyncSet<PathT>();
        program.snapshot.BuildInfoEmitPending = true;
    }

    // affectedfileshandler.go:355-393
    internal static void CollectAllAffectedFiles(CancellationToken ctx, Program program)
    {
        if (program.snapshot.changedFilesSet.Size() == 0)
        {
            return;
        }

        var handler = new AffectedFilesHandler { ctx = ctx, program = program };
        var wg = WorkGroup.Create(handler.program.program.SingleThreaded());
        var result = new SyncSet<global::Ts.Ast.SourceFile>();
        program.snapshot.changedFilesSet.Range(file =>
        {
            wg.Queue(() =>
            {
                foreach (var affectedFile in handler.GetFilesAffectedBy(file))
                {
                    result.Add(affectedFile);
                }
            });
            return true;
        });
        wg.RunAndWait();

        if (ctx.IsCancellationRequested)
        {
            return;
        }

        // For all the affected files, get all the files that would need to change their dts or js files,
        // update their diagnostics
        wg = WorkGroup.Create(program.program.SingleThreaded());
        var emitKind = FileEmitKindHelpers.GetFileEmitKind(program.snapshot.options);
        result.Range(file =>
        {
            // remove the cached semantic diagnostics and handle dts emit and js emit if needed
            var dtsMayChange = handler.GetDtsMayChange(file.Path(), emitKind);
            wg.Queue(() =>
            {
                handler.HandleDtsMayChangeOfAffectedFile(dtsMayChange, file);
            });
            return true;
        });
        wg.RunAndWait();

        // Update the snapshot with the new state
        handler.UpdateSnapshot();
    }
}
