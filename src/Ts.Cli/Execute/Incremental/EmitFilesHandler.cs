// Ported from tsgo internal/execute/incremental/emitfileshandler.go.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using CoreUtil = global::Ts.Core.CoreUtil;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// emitfileshandler.go:15-19
internal sealed class EmitUpdate
{
    internal FileEmitKind pendingKind;
    internal global::Ts.Compiler.EmitResult? result;
    internal bool dtsErrorsFromCache;
}

// emitfileshandler.go:21-31
internal sealed class EmitFilesHandler
{
    internal CancellationToken ctx;
    internal Program program = null!;
    internal bool isForDtsErrors;
    internal readonly SyncMap<PathT, string> signatures = new();
    internal readonly SyncMap<PathT, EmitSignature> emitSignatures = new();
    internal readonly SyncMap<PathT, string> latestChangedDtsFiles = new();
    internal readonly Set<PathT> deletedPendingKinds = new();
    internal readonly SyncMap<PathT, EmitUpdate> emitUpdates = new();
    private int hasEmitDiagnosticsFlag;

    private bool HasEmitDiagnostics
    {
        get => Volatile.Read(ref hasEmitDiagnosticsFlag) != 0;
        set => Volatile.Write(ref hasEmitDiagnosticsFlag, value ? 1 : 0);
    }

    // emitfileshandler.go:33-43
    // Determining what all is pending to be emitted based on previous options or previous file emit flags
    internal FileEmitKind GetPendingEmitKindForEmitOptions(FileEmitKind emitKind, global::Ts.Compiler.EmitOptions options)
    {
        var pendingKind = FileEmitKindHelpers.GetPendingEmitKind(emitKind, 0);
        if (options.EmitOnly == global::Ts.Compiler.EmitOnly.Dts)
        {
            pendingKind &= FileEmitKind.AllDts;
        }
        if (isForDtsErrors)
        {
            pendingKind &= FileEmitKind.DtsErrors;
        }
        return pendingKind;
    }

    // emitfileshandler.go:45-91
    // Emits the next affected file's emit result (EmitResult and sourceFiles emitted) or returns undefined if iteration is complete
    // The first of writeFile if provided, writeFile of BuilderProgramHost if provided, writeFile of compiler host
    // in that order would be used to write the files
    internal global::Ts.Compiler.EmitResult? EmitAllAffectedFiles(global::Ts.Compiler.EmitOptions options)
    {
        // Emit all affected files
        if (program.snapshot.CanUseIncrementalState())
        {
            var results = EmitFilesIncremental(options);
            if (isForDtsErrors)
            {
                if (options.TargetSourceFile != null)
                {
                    // Result from cache
                    program.snapshot.emitDiagnosticsPerFile.TryLoad(options.TargetSourceFile.Path(), out var diagnostics);
                    var result = new global::Ts.Compiler.EmitResult
                    {
                        EmitSkipped = true,
                        Diagnostics = diagnostics?.GetDiagnostics(program.program, options.TargetSourceFile) ?? [],
                    };
                    UpdateHasEmitDiagnostics(result);
                    return result;
                }
                foreach (var result in results ?? [])
                {
                    UpdateHasEmitDiagnostics(result);
                }
                return global::Ts.Compiler.EmitResults.CombineEmitResults(results ?? []);
            }
            else
            {
                // Combine results and update buildInfo
                var result = global::Ts.Compiler.EmitResults.CombineEmitResults(results ?? []);
                UpdateHasEmitDiagnostics(result);
                EmitBuildInfo(options, result);
                return result;
            }
        }
        else if (!isForDtsErrors)
        {
            var result = program.program.Emit(ctx, GetEmitOptions(options));
            UpdateHasEmitDiagnostics(result);
            UpdateSnapshot();
            EmitBuildInfo(options, result);
            return result;
        }
        else
        {
            var result = new global::Ts.Compiler.EmitResult
            {
                EmitSkipped = true,
                Diagnostics = program.program.GetDeclarationDiagnostics(ctx, options.TargetSourceFile),
            };
            if (result.Diagnostics is { Length: > 0 })
            {
                UpdateHasEmitDiagnostics(result);
                program.snapshot.hasEmitDiagnostics = true;
            }
            return result;
        }
    }

    // emitfileshandler.go:93-97
    internal void UpdateHasEmitDiagnostics(global::Ts.Compiler.EmitResult? result)
    {
        if (result?.Diagnostics is { Length: > 0 })
        {
            HasEmitDiagnostics = true;
        }
    }

    // emitfileshandler.go:99-105
    internal void EmitBuildInfo(global::Ts.Compiler.EmitOptions options, global::Ts.Compiler.EmitResult result)
    {
        var buildInfoResult = program.EmitBuildInfo(ctx, options);
        if (buildInfoResult != null)
        {
            result.Diagnostics = CoreUtil.Concatenate(result.Diagnostics, buildInfoResult.Diagnostics) ?? [];
            result.EmittedFiles = CoreUtil.Concatenate(result.EmittedFiles, buildInfoResult.EmittedFiles) ?? [];
        }
    }

    // emitfileshandler.go:107-184
    internal List<global::Ts.Compiler.EmitResult>? EmitFilesIncremental(global::Ts.Compiler.EmitOptions options)
    {
        // Get all affected files
        AffectedFilesHandler.CollectAllAffectedFiles(ctx, program);
        if (ctx.IsCancellationRequested)
        {
            return null;
        }

        var wg = WorkGroup.Create(program.program.SingleThreaded());
        program.snapshot.affectedFilesPendingEmit.Range((path, emitKind) =>
        {
            var affectedFile = program.program.GetSourceFileByPath(path);
            if (affectedFile == null || !program.program.SourceFileMayBeEmitted(affectedFile, false))
            {
                deletedPendingKinds.Add(path);
                return true;
            }
            var pendingKind = GetPendingEmitKindForEmitOptions(emitKind, options);
            if (pendingKind != 0)
            {
                wg.Queue(() =>
                {
                    // Determine if we can do partial emit
                    var emitOnly = default(global::Ts.Compiler.EmitOnly);
                    if ((pendingKind & FileEmitKind.AllJs) != 0)
                    {
                        emitOnly = global::Ts.Compiler.EmitOnly.Js;
                    }
                    if ((pendingKind & FileEmitKind.AllDts) != 0)
                    {
                        if (emitOnly == global::Ts.Compiler.EmitOnly.Js)
                        {
                            emitOnly = global::Ts.Compiler.EmitOnly.All;
                        }
                        else
                        {
                            emitOnly = global::Ts.Compiler.EmitOnly.Dts;
                        }
                    }
                    global::Ts.Compiler.EmitResult result;
                    if (!isForDtsErrors)
                    {
                        result = program.program.Emit(ctx, GetEmitOptions(new global::Ts.Compiler.EmitOptions
                        {
                            TargetSourceFile = affectedFile,
                            EmitOnly = emitOnly,
                            WriteFile = options.WriteFile,
                        }));
                    }
                    else
                    {
                        result = new global::Ts.Compiler.EmitResult
                        {
                            EmitSkipped = true,
                            Diagnostics = program.program.GetDeclarationDiagnostics(ctx, affectedFile),
                        };
                    }
                    UpdateHasEmitDiagnostics(result);

                    // Update the pendingEmit for the file
                    emitUpdates.Store(path, new EmitUpdate { pendingKind = FileEmitKindHelpers.GetPendingEmitKind(emitKind, pendingKind), result = result });
                });
            }
            return true;
        });
        wg.RunAndWait();
        if (ctx.IsCancellationRequested)
        {
            return null;
        }

        // Get updated errors that were not included in affected files emit
        program.snapshot.emitDiagnosticsPerFile.Range((path, diagnostics) =>
        {
            if (!emitUpdates.TryLoad(path, out _))
            {
                var affectedFile = program.program.GetSourceFileByPath(path);
                if (affectedFile == null || !program.program.SourceFileMayBeEmitted(affectedFile, false))
                {
                    deletedPendingKinds.Add(path);
                    return true;
                }
                program.snapshot.affectedFilesPendingEmit.TryLoad(path, out var pendingKind);
                emitUpdates.Store(path, new EmitUpdate
                {
                    pendingKind = pendingKind,
                    result = new global::Ts.Compiler.EmitResult
                    {
                        EmitSkipped = true,
                        Diagnostics = diagnostics.GetDiagnostics(program.program, affectedFile),
                    },
                    dtsErrorsFromCache = true,
                });
            }
            return true;
        });

        return UpdateSnapshot();
    }

    // emitfileshandler.go:186-237
    internal global::Ts.Compiler.EmitOptions GetEmitOptions(global::Ts.Compiler.EmitOptions options)
    {
        if (!program.snapshot.options.GetEmitDeclarations())
        {
            return options;
        }
        var canUseIncrementalState = program.snapshot.CanUseIncrementalState();
        return new global::Ts.Compiler.EmitOptions
        {
            TargetSourceFile = options.TargetSourceFile,
            EmitOnly = options.EmitOnly,
            WriteFile = (fileName, text, data) =>
            {
                var differsOnlyInMap = false;
                if (TsPathApi.IsDeclarationFileName(fileName))
                {
                    if (canUseIncrementalState)
                    {
                        var emitSignature = "";
                        program.snapshot.fileInfos.TryLoad(options.TargetSourceFile!.Path(), out var info);
                        if (info!.Signature() == info.Version())
                        {
                            var signature = program.snapshot.ComputeSignatureWithDiagnostics(options.TargetSourceFile, text, data!);
                            // With d.ts diagnostics they are also part of the signature so emitSignature will be different from it since its just hash of d.ts
                            if (data!.Diagnostics is not { Length: > 0 })
                            {
                                emitSignature = signature;
                            }
                            if (signature != info.Version())
                            {
                                // Update it
                                signatures.Store(options.TargetSourceFile.Path(), signature);
                            }
                        }

                        // Store d.ts emit hash so later can be compared to check if d.ts has changed.
                        // Currently we do this only for composite projects since these are the only projects that can be referenced by other projects
                        // and would need their d.ts change time in --build mode
                        if (SkipDtsOutputOfComposite(options.TargetSourceFile, fileName, text, data, emitSignature, ref differsOnlyInMap))
                        {
                            return;
                        }
                    }
                }

                var aTime = default(DateTime);
                if (differsOnlyInMap)
                {
                    aTime = program.host.GetMTime(fileName);
                }
                if (options.WriteFile != null)
                {
                    options.WriteFile(fileName, text, data);
                }
                else
                {
                    program.program.Host().FS().WriteFile(fileName, text);
                }
                if (differsOnlyInMap)
                {
                    // Revert the time to original one
                    program.host.SetMTime(fileName, aTime);
                }
            },
        };
    }

    // emitfileshandler.go:239-273
    // Compare to existing computed signature and store it or handle the changes in d.ts map option from before
    // returning undefined means that, we dont need to emit this d.ts file since its contents didnt change
    internal bool SkipDtsOutputOfComposite(global::Ts.Ast.SourceFile file, string outputFileName, string text, global::Ts.Compiler.WriteFileData? data, string newSignature, ref bool differsOnlyInMap)
    {
        if (!program.snapshot.options.Composite.IsTrue())
        {
            return false;
        }
        var oldSignature = "";
        var loadedOldSignatureFormat = program.snapshot.emitSignatures.TryLoad(file.Path(), out var oldSignatureFormat);
        if (loadedOldSignatureFormat)
        {
            if (oldSignatureFormat!.signature.Length > 0)
            {
                oldSignature = oldSignatureFormat.signature;
            }
            else
            {
                oldSignature = oldSignatureFormat.signatureWithDifferentOptions![0];
            }
        }
        if (newSignature.Length == 0)
        {
            newSignature = program.snapshot.ComputeHash(Snapshot.GetTextHandlingSourceMapForSignature(text, data!));
        }
        // Dont write dts files if they didn't change
        if (newSignature == oldSignature)
        {
            // If the signature was encoded as string the dts map options match so nothing to do
            if (loadedOldSignatureFormat && oldSignatureFormat!.signature == oldSignature)
            {
                data!.SkippedDtsWrite = true;
                return true;
            }
            else
            {
                // Mark as differsOnlyInMap so that we can reverse the timestamp with --build so that
                // the downstream projects dont detect this as change in d.ts file
                differsOnlyInMap = program.Options().Build.IsTrue();
            }
        }
        else
        {
            latestChangedDtsFiles.Store(file.Path(), outputFileName);
        }
        emitSignatures.Store(file.Path(), new EmitSignature { signature = newSignature });
        return false;
    }

    // emitfileshandler.go:275-325
    internal List<global::Ts.Compiler.EmitResult>? UpdateSnapshot()
    {
        if (program.snapshot.CanUseIncrementalState())
        {
            signatures.Range((file, signature) =>
            {
                program.snapshot.fileInfos.TryLoad(file, out var info);
                info!.signature = signature;
                if (program.testingData != null)
                {
                    program.testingData.UpdatedSignatureKinds[file] = SignatureUpdateKind.StoredAtEmit;
                }
                program.snapshot.BuildInfoEmitPending = true;
                return true;
            });
            emitSignatures.Range((file, signature) =>
            {
                program.snapshot.emitSignatures.Store(file, signature);
                program.snapshot.BuildInfoEmitPending = true;
                return true;
            });
            foreach (var file in SetExtensions.Keys(deletedPendingKinds)) // map-iter: 序无关 (deletes only)
            {
                program.snapshot.affectedFilesPendingEmit.Delete(file);
                program.snapshot.BuildInfoEmitPending = true;
            }
            // Always use correct order when to collect the result
            List<global::Ts.Compiler.EmitResult>? results = null;
            foreach (var file in program.SourceFiles())
            {
                if (latestChangedDtsFiles.TryLoad(file.Path(), out var latestChangedDtsFile))
                {
                    program.snapshot.latestChangedDtsFile = latestChangedDtsFile!;
                    program.snapshot.BuildInfoEmitPending = true;
                    program.snapshot.hasChangedDtsFile = true;
                }
                if (emitUpdates.TryLoad(file.Path(), out var update))
                {
                    if (!update!.dtsErrorsFromCache)
                    {
                        if (update.pendingKind == 0)
                        {
                            program.snapshot.affectedFilesPendingEmit.Delete(file.Path());
                        }
                        else
                        {
                            program.snapshot.affectedFilesPendingEmit.Store(file.Path(), update.pendingKind);
                        }
                        program.snapshot.BuildInfoEmitPending = true;
                    }
                    if (update.result != null)
                    {
                        results ??= new List<global::Ts.Compiler.EmitResult>();
                        results.Add(update.result);
                        if (update.result.Diagnostics is { Length: > 0 })
                        {
                            program.snapshot.emitDiagnosticsPerFile.Store(file.Path(), new DiagnosticsOrBuildInfoDiagnosticsWithFileName { diagnostics = update.result.Diagnostics });
                        }
                    }
                }
            }
            return results;
        }
        else if (HasEmitDiagnostics)
        {
            program.snapshot.hasEmitDiagnostics = true;
        }
        return null;
    }

    // emitfileshandler.go:327-343
    internal static global::Ts.Compiler.EmitResult? EmitFiles(CancellationToken ctx, Program program, global::Ts.Compiler.EmitOptions options, bool isForDtsErrors)
    {
        var emitHandler = new EmitFilesHandler { ctx = ctx, program = program, isForDtsErrors = isForDtsErrors };

        // Single file emit - do direct from program
        if (!isForDtsErrors && options.TargetSourceFile != null)
        {
            var result = program.program.Emit(ctx, emitHandler.GetEmitOptions(options));
            emitHandler.UpdateHasEmitDiagnostics(result);
            if (ctx.IsCancellationRequested)
            {
                return null;
            }
            emitHandler.UpdateSnapshot();
            return result;
        }

        // Emit only affected files if using builder for emit
        return emitHandler.EmitAllAffectedFiles(options);
    }
}
