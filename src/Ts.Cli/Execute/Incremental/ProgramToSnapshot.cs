// Ported from tsgo internal/execute/incremental/programtosnapshot.go.

using System.Collections.Generic;
using System.Threading;
using Ts.Collections;
using Ts.Core;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

internal static class ProgramToSnapshotConverter
{
    // programtosnapshot.go:16-39
    internal static Snapshot ProgramToSnapshot(global::Ts.Compiler.Program program, Program? oldProgram, bool hashWithText)
    {
        if (oldProgram != null && oldProgram.program == program)
        {
            return oldProgram.snapshot;
        }
        var snapshot = new Snapshot
        {
            options = program.Options(),
            hashWithText = hashWithText,
            checkPending = program.Options().NoCheck.IsTrue(),
        };
        var to = new ToProgramSnapshot
        {
            program = program,
            oldProgram = oldProgram,
            snapshot = snapshot,
        };

        if (to.snapshot.CanUseIncrementalState())
        {
            to.ReuseFromOldProgram();
            to.ComputeProgramFileChanges();
            to.HandleFileDelete();
            to.HandlePendingEmit();
            to.HandlePendingCheck();
        }
        return snapshot;
    }

    // programtosnapshot.go:208-231
    internal static bool FileAffectsGlobalScope(global::Ts.Ast.SourceFile file)
    {
        global::Ts.Binder.Binder.BindSourceFile(file);
        // if file contains anything that augments to global scope we need to build them as if
        // they are global files as well as module
        foreach (var augmentation in file.ModuleAugmentations ?? [])
        {
            if (global::Ts.Ast.AstUtilities.IsGlobalScopeAugmentation(augmentation.Parent!))
            {
                return true;
            }
        }

        if (global::Ts.Ast.AstUtilities.IsExternalOrCommonJSModule(file) || global::Ts.Ast.AstUtilities.IsJsonSourceFile(file))
        {
            return false;
        }

        // For script files that contains only ambient external modules, although they are not actually external module files,
        // they can only be consumed via importing elements from them. Regular script files cannot consume them. Therefore,
        // there are no point to rebuild all script files if these special files have changed. However, if any statement
        // in the file is not ambient external module, we treat it as a regular script file.
        if (file.Statements?.Nodes == null)
        {
            return false;
        }
        foreach (var stmt in file.Statements.Nodes)
        {
            if (!global::Ts.Ast.AstUtilities.IsModuleWithStringLiteralName(stmt))
            {
                return true;
            }
        }
        return false;
    }

    // programtosnapshot.go:233-246
    internal static void AddReferencedFilesFromSymbol(global::Ts.Ast.SourceFile file, Set<PathT> referencedFiles, global::Ts.Ast.Symbol? symbol)
    {
        if (symbol == null)
        {
            return;
        }
        foreach (var declaration in symbol.Declarations ?? [])
        {
            var fileOfDecl = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(declaration);
            if (fileOfDecl == null)
            {
                continue;
            }
            if (file != fileOfDecl)
            {
                referencedFiles.Add(fileOfDecl.Path());
            }
        }
    }

    // programtosnapshot.go:248-252 — Get the module source file and all augmenting files from the import name node from file
    internal static void AddReferencedFilesFromImportLiteral(global::Ts.Ast.SourceFile file, Set<PathT> referencedFiles, global::Ts.Checker.Checker checker, global::Ts.Ast.Node importName)
    {
        var symbol = checker.GetSymbolAtLocation(importName);
        AddReferencedFilesFromSymbol(file, referencedFiles, symbol);
    }

    // programtosnapshot.go:254-261 — Gets the path to reference file from file name, it could be resolvedPath if present otherwise path
    internal static void AddReferencedFileFromFileName(global::Ts.Compiler.Program program, string fileName, Set<PathT> referencedFiles, string sourceFileDirectory)
    {
        var redirect = program.GetParseFileRedirect(fileName);
        if (redirect.Length > 0)
        {
            referencedFiles.Add(TsPathApi.ToPath(redirect, program.GetCurrentDirectory(), program.UseCaseSensitiveFileNames()));
        }
        else
        {
            referencedFiles.Add(TsPathApi.ToPath(fileName, sourceFileDirectory, program.UseCaseSensitiveFileNames()));
        }
    }

    // programtosnapshot.go:263-304 — Gets the referenced files for a file from the program with values for the keys as referenced file's path to be true
    internal static Set<PathT>? GetReferencedFiles(global::Ts.Compiler.Program program, global::Ts.Ast.SourceFile file)
    {
        var referencedFiles = new Set<PathT>();

        // We need to use a set here since the code can contain the same import twice,
        // but that will only be one dependency.
        // To avoid invernal conversion, the key of the referencedFiles map must be of type Path
        var (checker, done) = program.GetTypeCheckerForFileExclusive(CancellationToken.None, file);
        try
        {
            foreach (var importName in file.Imports() ?? [])
            {
                AddReferencedFilesFromImportLiteral(file, referencedFiles, checker, importName);
            }

            var sourceFileDirectory = TsPathApi.GetDirectoryPath(file.FileName());
            // Handle triple slash references
            foreach (var referencedFile in file.ReferencedFiles ?? [])
            {
                AddReferencedFileFromFileName(program, referencedFile.FileName, referencedFiles, sourceFileDirectory);
            }

            // Handle type reference directives
            if (program.GetResolvedTypeReferenceDirectives().TryGetValue(file.Path(), out var typeRefsInFile))
            {
                foreach (var typeRef in typeRefsInFile.Values) // map-iter: 序无关 (set inserts only)
                {
                    if (typeRef.ResolvedFileName.Length > 0)
                    {
                        AddReferencedFileFromFileName(program, typeRef.ResolvedFileName, referencedFiles, sourceFileDirectory);
                    }
                }
            }

            // Add module augmentation as references
            foreach (var moduleName in file.ModuleAugmentations ?? [])
            {
                if (!global::Ts.Ast.NodeTests.IsStringLiteral(moduleName))
                {
                    continue;
                }
                AddReferencedFilesFromImportLiteral(file, referencedFiles, checker, moduleName);
            }

            // From ambient modules
            foreach (var ambientModule in checker.GetAmbientModules())
            {
                AddReferencedFilesFromSymbol(file, referencedFiles, ambientModule);
            }
        }
        finally
        {
            done();
        }
        return referencedFiles.Len() > 0 ? referencedFiles : null;
    }

    // programtosnapshot.go:306-320 — repopulateDiagnosticsOfFile repopulates diagnostic chains that depend on program state.
    // When diagnostics are copied from a previous build, their message chains may reference
    // stale program state (e.g., resolved module alternate results, package.json scope).
    // This function recomputes those chains using the current program's state.
    internal static DiagnosticsOrBuildInfoDiagnosticsWithFileName RepopulateDiagnosticsOfFile(DiagnosticsOrBuildInfoDiagnosticsWithFileName diags, global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile file)
    {
        if (diags.diagnostics != null)
        {
            var repopulated = RepopulateDiagnosticsList(diags.diagnostics, p, file);
            if (repopulated == null)
            {
                return diags;
            }
            return new DiagnosticsOrBuildInfoDiagnosticsWithFileName { diagnostics = repopulated };
        }
        // buildInfoDiagnostics will be repopulated via toDiagnostic's repopulateInfo handling
        return diags;
    }

    // programtosnapshot.go:322-342 — repopulateDiagnosticsList repopulates diagnostic chains in a list of diagnostics.
    // Returns nil if no diagnostics needed repopulation (i.e., no changes were made).
    internal static global::Ts.Ast.Diagnostic[]? RepopulateDiagnosticsList(global::Ts.Ast.Diagnostic[] diags, global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile file)
    {
        var changed = false;
        var result = new global::Ts.Ast.Diagnostic[diags.Length];
        for (var i = 0; i < diags.Length; i++)
        {
            var d = diags[i];
            var repopulated = RepopulateDiagnosticMessageChain(d.MessageChain(), p, file);
            if (repopulated != null)
            {
                var clone = d.Clone();
                clone.SetMessageChain(repopulated);
                result[i] = clone;
                changed = true;
            }
            else
            {
                result[i] = d;
            }
        }
        if (!changed)
        {
            return null;
        }
        return result;
    }

    // programtosnapshot.go:344-387 — repopulateDiagnosticMessageChain repopulates chains that have repopulate info.
    // Returns nil if no changes were made.
    internal static global::Ts.Ast.Diagnostic[]? RepopulateDiagnosticMessageChain(global::Ts.Ast.Diagnostic[]? chain, global::Ts.Compiler.Program p, global::Ts.Ast.SourceFile file)
    {
        if (chain == null || chain.Length == 0)
        {
            return null;
        }
        var changed = false;
        var result = new global::Ts.Ast.Diagnostic[chain.Length];
        for (var i = 0; i < chain.Length; i++)
        {
            var c = chain[i];
            if (c.RepopulateInfo() != null)
            {
                // Convert to buildInfoDiagnosticWithFileName and repopulate
                var b = new BuildInfoDiagnosticWithFileName
                {
                    pos = c.Pos(),
                    end = c.End(),
                    code = c.Code(),
                    category = c.Category(),
                    messageKey = c.MessageKey(),
                    messageArgs = c.MessageArgs(),
                    repopulateInfo = c.RepopulateInfo(),
                };
                // Recursively handle nested chains
                if (c.MessageChain() is { Length: > 0 } nestedChain)
                {
                    var converted = new List<BuildInfoDiagnosticWithFileName>(nestedChain.Length);
                    foreach (var nested in nestedChain)
                    {
                        converted.Add(AstDiagToBuildInfoDiag(nested));
                    }
                    b.messageChain = converted.ToArray();
                }
                result[i] = BuildInfoDiagnosticWithFileName.RepopulateDiagnosticChain(b, p, file);
                changed = true;
            }
            else
            {
                // Check nested chains
                var nested = RepopulateDiagnosticMessageChain(c.MessageChain(), p, file);
                if (nested != null)
                {
                    var clone = c.Clone();
                    clone.SetMessageChain(nested);
                    result[i] = clone;
                    changed = true;
                }
                else
                {
                    result[i] = c;
                }
            }
        }
        if (!changed)
        {
            return null;
        }
        return result;
    }

    // programtosnapshot.go:389-403
    internal static BuildInfoDiagnosticWithFileName AstDiagToBuildInfoDiag(global::Ts.Ast.Diagnostic d)
    {
        var b = new BuildInfoDiagnosticWithFileName
        {
            pos = d.Pos(),
            end = d.End(),
            code = d.Code(),
            category = d.Category(),
            messageKey = d.MessageKey(),
            messageArgs = d.MessageArgs(),
            repopulateInfo = d.RepopulateInfo(),
        };
        if (d.MessageChain() is { Length: > 0 } nestedChain)
        {
            var converted = new List<BuildInfoDiagnosticWithFileName>(nestedChain.Length);
            foreach (var nested in nestedChain)
            {
                converted.Add(AstDiagToBuildInfoDiag(nested));
            }
            b.messageChain = converted.ToArray();
        }
        return b;
    }
}

// programtosnapshot.go:41-46
internal sealed class ToProgramSnapshot
{
    internal global::Ts.Compiler.Program program = null!;
    internal Program? oldProgram;
    internal Snapshot snapshot = null!;
    internal bool globalFileRemoved;

    // programtosnapshot.go:48-70
    internal void ReuseFromOldProgram()
    {
        if (oldProgram != null)
        {
            if (snapshot.options.Composite.IsTrue())
            {
                snapshot.latestChangedDtsFile = oldProgram.snapshot.latestChangedDtsFile;
            }
            // Copy old snapshot's changed files set
            oldProgram.snapshot.changedFilesSet.Range(key =>
            {
                snapshot.changedFilesSet.Add(key);
                return true;
            });
            oldProgram.snapshot.affectedFilesPendingEmit.Range((key, emitKind) =>
            {
                snapshot.affectedFilesPendingEmit.Store(key, emitKind);
                return true;
            });
            snapshot.BuildInfoEmitPending = oldProgram.snapshot.BuildInfoEmitPending;
            snapshot.hasErrorsFromOldState = oldProgram.snapshot.hasErrors;
            snapshot.hasSemanticErrorsFromOldState = oldProgram.snapshot.hasSemanticErrors;
            snapshot.packageJsonsFromOldState = oldProgram.snapshot.packageJsons;
            snapshot.missingPackageJsonsFromOldState = oldProgram.snapshot.missingPackageJsons;
        }
        else
        {
            snapshot.BuildInfoEmitPending = snapshot.options.IsIncremental();
        }
    }

    // programtosnapshot.go:72-155
    internal void ComputeProgramFileChanges()
    {
        var canCopySemanticDiagnostics = oldProgram != null &&
            !global::Ts.TsOptions.DeclsCompiler.CompilerOptionsAffectSemanticDiagnostics(oldProgram.snapshot.options, program.Options());
        // We can only reuse emit signatures (i.e. .d.ts signatures) if the .d.ts file is unchanged,
        // which will eg be depedent on change in options like declarationDir and outDir options are unchanged.
        // We need to look in oldState.compilerOptions, rather than oldCompilerOptions (i.e.we need to disregard useOldState) because
        // oldCompilerOptions can be undefined if there was change in say module from None to some other option
        // which would make useOldState as false since we can now use reference maps that are needed to track what to emit, what to check etc
        // but that option change does not affect d.ts file name so emitSignatures should still be reused.
        var canCopyEmitSignatures = snapshot.options.Composite.IsTrue() &&
            oldProgram != null &&
            !global::Ts.TsOptions.DeclsCompiler.CompilerOptionsAffectDeclarationPath(oldProgram.snapshot.options, program.Options());
        var copyDeclarationFileDiagnostics = canCopySemanticDiagnostics &&
            snapshot.options.SkipLibCheck.IsTrue() == oldProgram!.snapshot.options.SkipLibCheck.IsTrue();
        var copyLibFileDiagnostics = copyDeclarationFileDiagnostics &&
            snapshot.options.SkipDefaultLibCheck.IsTrue() == oldProgram!.snapshot.options.SkipDefaultLibCheck.IsTrue();

        var files = program.SourceFiles();
        var wg = WorkGroup.Create(program.SingleThreaded());
        foreach (var file in files)
        {
            wg.Queue(() =>
            {
                var version = snapshot.ComputeHash(file.Text());
                var impliedNodeFormat = program.GetSourceFileMetaData(file.Path()).ImpliedNodeFormat;
                var affectsGlobalScope = ProgramToSnapshotConverter.FileAffectsGlobalScope(file);
                var signature = "";
                var newReferences = ProgramToSnapshotConverter.GetReferencedFiles(program, file);
                if (newReferences != null)
                {
                    snapshot.referencedMap.StoreReferences(file.Path(), newReferences);
                }
                if (oldProgram != null)
                {
                    if (oldProgram.snapshot.fileInfos.TryLoad(file.Path(), out var oldFileInfo))
                    {
                        signature = oldFileInfo!.Signature();
                        if (oldFileInfo.Version() != version || oldFileInfo.AffectsGlobalScope() != affectsGlobalScope || oldFileInfo.ImpliedNodeFormat() != impliedNodeFormat)
                        {
                            snapshot.AddFileToChangeSet(file.Path());
                        }
                        else
                        {
                            oldProgram.snapshot.referencedMap.TryGetReferences(file.Path(), out var oldReferences);
                            if (!SetsEqual(newReferences, oldReferences))
                            {
                                // Referenced files changed
                                snapshot.AddFileToChangeSet(file.Path());
                            }
                            else if (newReferences != null)
                            {
                                foreach (var refPath in SetExtensions.Keys(newReferences))
                                {
                                    if (program.GetSourceFileByPath(refPath) == null)
                                    {
                                        if (oldProgram.snapshot.fileInfos.TryLoad(refPath, out _))
                                        {
                                            // Referenced file was deleted in the new program
                                            snapshot.AddFileToChangeSet(file.Path());
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        snapshot.AddFileToChangeSet(file.Path());
                    }
                    if (!snapshot.changedFilesSet.Has(file.Path()))
                    {
                        if (oldProgram.snapshot.emitDiagnosticsPerFile.TryLoad(file.Path(), out var emitDiagnostics))
                        {
                            snapshot.emitDiagnosticsPerFile.Store(file.Path(), ProgramToSnapshotConverter.RepopulateDiagnosticsOfFile(emitDiagnostics!, program, file));
                        }
                        if (canCopySemanticDiagnostics)
                        {
                            if ((!file.IsDeclarationFile || copyDeclarationFileDiagnostics) &&
                                (!program.IsSourceFileDefaultLibrary(file.Path()) || copyLibFileDiagnostics))
                            {
                                // Unchanged file copy diagnostics
                                if (oldProgram.snapshot.semanticDiagnosticsPerFile.TryLoad(file.Path(), out var diagnostics))
                                {
                                    snapshot.semanticDiagnosticsPerFile.Store(file.Path(), ProgramToSnapshotConverter.RepopulateDiagnosticsOfFile(diagnostics!, program, file));
                                }
                            }
                        }
                    }
                    if (canCopyEmitSignatures)
                    {
                        if (oldProgram.snapshot.emitSignatures.TryLoad(file.Path(), out var oldEmitSignature))
                        {
                            snapshot.emitSignatures.Store(file.Path(), oldEmitSignature!.GetNewEmitSignature(oldProgram.snapshot.options, snapshot.options));
                        }
                    }
                }
                else
                {
                    snapshot.AddFileToAffectedFilesPendingEmit(file.Path(), FileEmitKindHelpers.GetFileEmitKind(snapshot.options));
                    signature = version;
                }
                snapshot.fileInfos.Store(file.Path(), new FileInfo(
                    version: version,
                    signature: signature,
                    affectsGlobalScope: affectsGlobalScope,
                    impliedNodeFormat: impliedNodeFormat));
            });
        }
        wg.RunAndWait();
    }

    // collections.Set.Equals with Go nil-receiver semantics (maps.Equal: nil and empty are equal).
    private static bool SetsEqual(Set<PathT>? a, Set<PathT>? b)
    {
        var lenA = a?.Len() ?? 0;
        var lenB = b?.Len() ?? 0;
        if (lenA == 0 && lenB == 0)
        {
            return true;
        }
        return a != null && a.Equals(b);
    }

    // programtosnapshot.go:157-175
    internal void HandleFileDelete()
    {
        if (oldProgram != null)
        {
            // If the global file is removed, add all files as changed
            oldProgram.snapshot.fileInfos.Range((filePath, oldInfo) =>
            {
                if (!snapshot.fileInfos.TryLoad(filePath, out _))
                {
                    if (oldInfo.AffectsGlobalScope())
                    {
                        foreach (var file in snapshot.GetAllFilesExcludingDefaultLibraryFile(program, null))
                        {
                            snapshot.AddFileToChangeSet(file.Path());
                        }
                        globalFileRemoved = true;
                    }
                    else
                    {
                        snapshot.BuildInfoEmitPending = true;
                    }
                    return false;
                }
                return true;
            });
        }
    }

    // programtosnapshot.go:177-198
    internal void HandlePendingEmit()
    {
        if (oldProgram != null && !globalFileRemoved)
        {
            // If options affect emit, then we need to do complete emit per compiler options
            // otherwise only the js or dts that needs to emitted because its different from previously emitted options
            FileEmitKind pendingEmitKind;
            if (global::Ts.TsOptions.DeclsCompiler.CompilerOptionsAffectEmit(oldProgram.snapshot.options, snapshot.options))
            {
                pendingEmitKind = FileEmitKindHelpers.GetFileEmitKind(snapshot.options);
            }
            else
            {
                pendingEmitKind = FileEmitKindHelpers.GetPendingEmitKindWithOptions(snapshot.options, oldProgram.snapshot.options);
            }
            if (pendingEmitKind != FileEmitKind.None)
            {
                // Add all files to affectedFilesPendingEmit since emit changed
                foreach (var file in program.SourceFiles())
                {
                    // Add to affectedFilesPending emit only if not changed since any changed file will do full emit
                    if (!snapshot.changedFilesSet.Has(file.Path()))
                    {
                        snapshot.AddFileToAffectedFilesPendingEmit(file.Path(), pendingEmitKind);
                    }
                }
                snapshot.BuildInfoEmitPending = true;
            }
        }
    }

    // programtosnapshot.go:200-206
    internal void HandlePendingCheck()
    {
        if (oldProgram != null &&
            snapshot.semanticDiagnosticsPerFile.Size() != program.SourceFiles().Length &&
            oldProgram.snapshot.checkPending != snapshot.checkPending)
        {
            snapshot.BuildInfoEmitPending = true;
        }
    }
}
