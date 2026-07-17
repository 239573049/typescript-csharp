// Ported from tsgo internal/execute/incremental/program.go.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using Ts.PackageJson;
using Ts.Tracing;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// program.go:21-27
public enum SignatureUpdateKind : byte
{
    ComputedDts = 0,
    StoredAtEmit = 1,
    UsedVersion = 2,
}

// program.go:29-38
public sealed class Program : global::Ts.Compiler.ProgramLike
{
    internal Snapshot snapshot = null!;
    internal global::Ts.Compiler.Program program = null!;
    internal IHost host = null!;

    // Testing data
    internal TestingData? testingData;

    // program.go:40-58
    public static Program NewProgram(global::Ts.Compiler.Program program, Program? oldProgram, IHost host, bool testing)
    {
        var incrementalProgram = new Program
        {
            snapshot = ProgramToSnapshotConverter.ProgramToSnapshot(program, oldProgram, testing),
            program = program,
            host = host,
        };

        if (testing)
        {
            incrementalProgram.testingData = new TestingData
            {
                SemanticDiagnosticsPerFile = incrementalProgram.snapshot.semanticDiagnosticsPerFile,
                OldProgramSemanticDiagnosticsPerFile = oldProgram != null
                    ? oldProgram.snapshot.semanticDiagnosticsPerFile
                    : new SyncMap<PathT, DiagnosticsOrBuildInfoDiagnosticsWithFileName>(),
                UpdatedSignatureKinds = new Dictionary<PathT, SignatureUpdateKind>(),
            };
        }
        return incrementalProgram;
    }

    // program.go:66-68
    public TestingData? GetTestingData() => testingData;

    // program.go:70-74
    private void PanicIfNoProgram(string method)
    {
        if (program == null)
        {
            throw new InvalidOperationException(method + ": should not be called without program");
        }
    }

    // program.go:76-79
    public global::Ts.Compiler.Program GetProgram()
    {
        PanicIfNoProgram("GetProgram");
        return program;
    }

    // program.go:81-83
    public bool HasChangedDtsFile() => snapshot.hasChangedDtsFile;

    // program.go:85-88 — Options implements compiler.AnyProgram interface.
    public CompilerOptions Options() => snapshot.options;

    // program.go:90-94 — CommonSourceDirectory implements compiler.AnyProgram interface.
    public string CommonSourceDirectory()
    {
        PanicIfNoProgram("CommonSourceDirectory");
        return program.CommonSourceDirectory();
    }

    // program.go:96-100 — Program implements compiler.AnyProgram interface.
    public global::Ts.Compiler.Program GetUnderlyingProgram()
    {
        PanicIfNoProgram("Program");
        return program;
    }

    // program.go:102-106 — IsSourceFileDefaultLibrary implements compiler.AnyProgram interface.
    public bool IsSourceFileDefaultLibrary(PathT path)
    {
        PanicIfNoProgram("IsSourceFileDefaultLibrary");
        return program.IsSourceFileDefaultLibrary(path);
    }

    // program.go:108-112 — GetSourceFiles implements compiler.AnyProgram interface.
    public global::Ts.Ast.SourceFile[] SourceFiles()
    {
        PanicIfNoProgram("GetSourceFiles");
        return program.SourceFiles();
    }

    // program.go:114-118 — GetSourceFile implements compiler.AnyProgram interface.
    public global::Ts.Ast.SourceFile? GetSourceFile(string path)
    {
        PanicIfNoProgram("GetSourceFile");
        return program.GetSourceFile(path);
    }

    // program.go:120-124 — GetConfigFileParsingDiagnostics implements compiler.AnyProgram interface.
    public global::Ts.Ast.Diagnostic[] GetConfigFileParsingDiagnostics()
    {
        PanicIfNoProgram("GetConfigFileParsingDiagnostics");
        return program.GetConfigFileParsingDiagnostics();
    }

    // program.go:126-130 — GetSyntacticDiagnostics implements compiler.AnyProgram interface.
    public global::Ts.Ast.Diagnostic[] GetSyntacticDiagnostics(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        PanicIfNoProgram("GetSyntacticDiagnostics");
        return program.GetSyntacticDiagnostics(ctx, file);
    }

    // program.go:132-136 — GetBindDiagnostics implements compiler.AnyProgram interface.
    public global::Ts.Ast.Diagnostic[] GetBindDiagnostics(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        PanicIfNoProgram("GetBindDiagnostics");
        return program.GetBindDiagnostics(ctx, file);
    }

    // program.go:138-141
    public global::Ts.Ast.Diagnostic[] GetProgramDiagnostics()
    {
        PanicIfNoProgram("GetProgramDiagnostics");
        return program.GetProgramDiagnostics();
    }

    // program.go:143-146
    public global::Ts.Ast.Diagnostic[] GetGlobalDiagnostics(CancellationToken ctx)
    {
        PanicIfNoProgram("GetGlobalDiagnostics");
        return program.GetGlobalDiagnostics(ctx);
    }

    // program.go:148-171 — GetSemanticDiagnostics implements compiler.AnyProgram interface.
    public global::Ts.Ast.Diagnostic[] GetSemanticDiagnostics(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        PanicIfNoProgram("GetSemanticDiagnostics");
        if (snapshot.options.NoCheck.IsTrue())
        {
            return [];
        }

        // Ensure all the diagnsotics are cached
        CollectSemanticDiagnosticsOfAffectedFiles(ctx, file);
        if (ctx.IsCancellationRequested)
        {
            return [];
        }

        // Return result from cache
        if (file != null)
        {
            return GetSemanticDiagnosticsOfFile(file);
        }

        var diagnostics = new List<global::Ts.Ast.Diagnostic>();
        foreach (var sourceFile in program.SourceFiles())
        {
            diagnostics.AddRange(GetSemanticDiagnosticsOfFile(sourceFile));
        }
        return diagnostics.ToArray();
    }

    // program.go:173-182
    private global::Ts.Ast.Diagnostic[] GetSemanticDiagnosticsOfFile(global::Ts.Ast.SourceFile file)
    {
        if (!snapshot.semanticDiagnosticsPerFile.TryLoad(file.Path(), out var cachedDiagnostics))
        {
            throw new InvalidOperationException("After handling all the affected files, there shouldnt be more changes");
        }
        return CoreUtil.Concatenate(
            global::Ts.Compiler.Program.FilterNoEmitSemanticDiagnostics(cachedDiagnostics!.GetDiagnostics(program, file), snapshot.options),
            program.GetIncludeProcessorDiagnostics(file)) ?? [];
    }

    // program.go:184-194 — GetDeclarationDiagnostics implements compiler.AnyProgram interface.
    public global::Ts.Ast.Diagnostic[] GetDeclarationDiagnostics(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        PanicIfNoProgram("GetDeclarationDiagnostics");
        var result = EmitFilesHandler.EmitFiles(ctx, this, new global::Ts.Compiler.EmitOptions
        {
            TargetSourceFile = file,
        }, true);
        if (result != null)
        {
            return result.Diagnostics ?? [];
        }
        return [];
    }

    // program.go:196-200 — GetSuggestionDiagnostics implements compiler.AnyProgram interface.
    public global::Ts.Ast.Diagnostic[] GetSuggestionDiagnostics(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        PanicIfNoProgram("GetSuggestionDiagnostics");
        return program.GetSuggestionDiagnostics(ctx, file); // TODO: incremental suggestion diagnostics (only relevant in editor incremental builder?)
    }

    // program.go:202-229
    public global::Ts.Compiler.EmitResult? Emit(CancellationToken ctx, global::Ts.Compiler.EmitOptions options)
    {
        PanicIfNoProgram("Emit");

        global::Ts.Compiler.EmitResult? result;
        if (snapshot.options.NoEmit.IsTrue())
        {
            result = new global::Ts.Compiler.EmitResult { EmitSkipped = true };
        }
        else
        {
            result = global::Ts.Compiler.Program.HandleNoEmitOnError(ctx, this, options.TargetSourceFile);
            if (ctx.IsCancellationRequested)
            {
                return null;
            }
        }
        if (result != null)
        {
            if (options.TargetSourceFile != null)
            {
                return result;
            }

            // Emit buildInfo and combine result
            var buildInfoResult = EmitBuildInfo(ctx, options);
            if (buildInfoResult != null)
            {
                result.Diagnostics = CoreUtil.Concatenate(result.Diagnostics, buildInfoResult.Diagnostics) ?? [];
                result.EmittedFiles = CoreUtil.Concatenate(result.EmittedFiles, buildInfoResult.EmittedFiles) ?? [];
            }
            return result;
        }
        return EmitFilesHandler.EmitFiles(ctx, this, options, false);
    }

    // program.go:231-276 — Handle affected files and cache the semantic diagnostics for all of them or the file asked for
    private void CollectSemanticDiagnosticsOfAffectedFiles(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        if (snapshot.CanUseIncrementalState())
        {
            // Get all affected files
            AffectedFilesHandler.CollectAllAffectedFiles(ctx, this);
            if (ctx.IsCancellationRequested)
            {
                return;
            }

            if (snapshot.semanticDiagnosticsPerFile.Size() == program.SourceFiles().Length)
            {
                // If we have all the files,
                return;
            }
        }

        global::Ts.Ast.SourceFile[] affectedFiles;
        if (file != null)
        {
            if (snapshot.semanticDiagnosticsPerFile.TryLoad(file.Path(), out _))
            {
                return;
            }
            affectedFiles = [file];
        }
        else
        {
            var pending = new List<global::Ts.Ast.SourceFile>();
            foreach (var sourceFile in program.SourceFiles())
            {
                if (!snapshot.semanticDiagnosticsPerFile.TryLoad(sourceFile.Path(), out _))
                {
                    pending.Add(sourceFile);
                }
            }
            affectedFiles = pending.ToArray();
        }

        // Get their diagnostics and cache them
        var diagnosticsPerFile = program.GetSemanticDiagnosticsWithoutNoEmitFiltering(ctx, affectedFiles);
        // commit changes if no err
        if (ctx.IsCancellationRequested)
        {
            return;
        }

        // Commit changes to snapshot
        foreach (var (diagFile, diagnostics) in diagnosticsPerFile) // map-iter: 序无关 (SyncMap stores)
        {
            snapshot.semanticDiagnosticsPerFile.Store(diagFile.Path(), new DiagnosticsOrBuildInfoDiagnosticsWithFileName { diagnostics = diagnostics });
        }
        if (snapshot.semanticDiagnosticsPerFile.Size() == program.SourceFiles().Length && snapshot.checkPending && !snapshot.options.NoCheck.IsTrue())
        {
            snapshot.checkPending = false;
        }
        snapshot.BuildInfoEmitPending = true;
    }

    // program.go:278-333
    internal global::Ts.Compiler.EmitResult? EmitBuildInfo(CancellationToken ctx, global::Ts.Compiler.EmitOptions options)
    {
        var releaseTracing = program.Tracing().Push(Phase.Emit, "emitBuildInfo", null, true);
        try
        {
            var buildInfoFileName = global::Ts.OutputPaths.OutputPaths.GetBuildInfoFileName(snapshot.options, new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = program.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = program.UseCaseSensitiveFileNames(),
            });
            if (buildInfoFileName.Length == 0 || program.IsEmitBlocked(buildInfoFileName))
            {
                return null;
            }
            if (snapshot.hasErrors == Tristate.Unknown)
            {
                EnsureHasErrorsForState(ctx, program);
                if (snapshot.hasErrors != snapshot.hasErrorsFromOldState || snapshot.hasSemanticErrors != snapshot.hasSemanticErrorsFromOldState)
                {
                    snapshot.BuildInfoEmitPending = true;
                }
            }
            if (snapshot.packageJsons == null)
            {
                EnsurePackageJsonsForState();
                if (!(snapshot.packageJsons ?? []).SequenceEqual(snapshot.packageJsonsFromOldState ?? [], StringComparer.Ordinal) ||
                    !(snapshot.missingPackageJsons ?? []).SequenceEqual(snapshot.missingPackageJsonsFromOldState ?? [], StringComparer.Ordinal))
                {
                    snapshot.BuildInfoEmitPending = true;
                }
            }
            if (!snapshot.BuildInfoEmitPending)
            {
                return null;
            }
            if (ctx.IsCancellationRequested)
            {
                return null;
            }
            var buildInfo = SnapshotToBuildInfoConverter.SnapshotToBuildInfo(snapshot, program, buildInfoFileName);
            var text = buildInfo.Marshal();
            try
            {
                if (options.WriteFile != null)
                {
                    options.WriteFile(buildInfoFileName, text, new global::Ts.Compiler.WriteFileData
                    {
                        BuildInfo = buildInfo,
                    });
                }
                else
                {
                    program.Host().FS().WriteFile(buildInfoFileName, text);
                }
            }
            catch (Exception e)
            {
                return new global::Ts.Compiler.EmitResult
                {
                    EmitSkipped = true,
                    Diagnostics =
                    [
                        global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Could_not_write_file_0_Colon_1, buildInfoFileName, e.Message),
                    ],
                };
            }
            snapshot.BuildInfoEmitPending = false;
            return new global::Ts.Compiler.EmitResult
            {
                EmitSkipped = false,
                EmittedFiles = [buildInfoFileName],
            };
        }
        finally
        {
            releaseTracing();
        }
    }

    // program.go:335-400
    private void EnsureHasErrorsForState(CancellationToken ctx, global::Ts.Compiler.Program program)
    {
        Func<bool>? hasIncludeProcessingDiagnostics = null;
        var hasEmitDiagnostics = false;
        if (snapshot.CanUseIncrementalState())
        {
            if (program.SourceFiles().Any(file =>
            {
                if (snapshot.emitDiagnosticsPerFile.TryLoad(file.Path(), out _))
                {
                    // emit diagnostics will be encoded in buildInfo;
                    return true;
                }
                if (hasIncludeProcessingDiagnostics == null && this.program.GetIncludeProcessorDiagnostics(file).Length > 0)
                {
                    hasIncludeProcessingDiagnostics = static () => true;
                }
                return false;
            }))
            {
                hasEmitDiagnostics = true;
            }
            hasIncludeProcessingDiagnostics ??= static () => false;
        }
        else
        {
            hasEmitDiagnostics = snapshot.hasEmitDiagnostics;
            hasIncludeProcessingDiagnostics = () => program.SourceFiles().Any(file => this.program.GetIncludeProcessorDiagnostics(file).Length > 0);
        }

        if (hasEmitDiagnostics)
        {
            // Record this for only non incremental build info
            snapshot.hasErrors = snapshot.options.IsIncremental() ? Tristate.False : Tristate.True;
            // Dont need to encode semantic errors state since the emit diagnostics are encoded
            snapshot.hasSemanticErrors = false;
            return;
        }

        if (hasIncludeProcessingDiagnostics() ||
            program.GetConfigFileParsingDiagnostics().Length > 0 ||
            program.GetSyntacticDiagnostics(ctx, null).Length > 0 ||
            program.GetProgramDiagnostics().Length > 0 ||
            program.GetGlobalDiagnostics(ctx).Length > 0)
        {
            snapshot.hasErrors = Tristate.True;
            // Dont need to encode semantic errors state since the syntax and program diagnostics are encoded as present
            snapshot.hasSemanticErrors = false;
            return;
        }

        snapshot.hasErrors = Tristate.False;
        // Check semantic and emit diagnostics first as we dont need to ask program about it
        if (this.program.SourceFiles().Any(file =>
        {
            if (!snapshot.semanticDiagnosticsPerFile.TryLoad(file.Path(), out var semanticDiagnostics))
            {
                // Missing semantic diagnostics in cache will be encoded in incremental buildInfo
                return snapshot.options.IsIncremental();
            }
            if (semanticDiagnostics!.diagnostics is { Length: > 0 } || semanticDiagnostics.buildInfoDiagnostics is { Length: > 0 })
            {
                // cached semantic diagnostics will be encoded in buildInfo
                return true;
            }
            return false;
        }))
        {
            // Because semantic diagnostics are recorded in buildInfo, we dont need to encode hasErrors in incremental buildInfo
            // But encode as errors in non incremental buildInfo
            snapshot.hasSemanticErrors = !snapshot.options.IsIncremental();
        }
    }

    // program.go:402-423
    private void EnsurePackageJsonsForState()
    {
        var config = TsPathApi.GetDirectoryPath(program.CommandLine().ConfigName());
        List<string>? packageJsons = null;
        List<string>? missingPackageJsons = null;
        if (config.Length > 0)
        {
            program.PackageJsonCacheEntries((key, value) =>
            {
                if (value == null)
                {
                    return true;
                }
                var packageJson = TsPathApi.CombinePaths(value.PackageDirectory, "package.json");
                if (value.Exists() || value.DirectoryExists)
                {
                    packageJson = program.Host().FS().Realpath(packageJson);
                }
                if (value.Exists())
                {
                    packageJsons ??= new List<string>();
                    packageJsons.Add(packageJson);
                }
                else if (packageJson.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    missingPackageJsons ??= new List<string>();
                    missingPackageJsons.Add(packageJson);
                }
                return true;
            });
        }
        snapshot.packageJsons = NormalizePackageJsons(packageJsons?.ToArray());
        snapshot.missingPackageJsons = NormalizePackageJsons(missingPackageJsons?.ToArray());
    }

    // program.go:425-431
    private static string[] NormalizePackageJsons(string[]? packageJsons)
    {
        if (packageJsons == null)
        {
            return [];
        }
        Array.Sort(packageJsons, StringComparer.Ordinal);
        return CoreUtil.Deduplicate(packageJsons) ?? [];
    }

    // program.go:433-453
    public string[] PackageJsonLookupPaths()
    {
        var config = TsPathApi.GetDirectoryPath(program.CommandLine().ConfigName());
        if (config.Length == 0)
        {
            return [];
        }

        var packageJsons = new List<string>();
        program.PackageJsonCacheEntries((key, value) =>
        {
            if (value == null)
            {
                return true;
            }
            var packageJson = TsPathApi.CombinePaths(value.PackageDirectory, "package.json");
            if (value.Exists() || value.DirectoryExists)
            {
                packageJson = program.Host().FS().Realpath(packageJson);
            }
            packageJsons.Add(packageJson);
            return true;
        });
        packageJsons.Sort(StringComparer.Ordinal);
        return CoreUtil.Deduplicate(packageJsons.ToArray()) ?? [];
    }
}

// program.go:60-64
public sealed class TestingData
{
    internal SyncMap<PathT, DiagnosticsOrBuildInfoDiagnosticsWithFileName> SemanticDiagnosticsPerFile = null!;
    internal SyncMap<PathT, DiagnosticsOrBuildInfoDiagnosticsWithFileName> OldProgramSemanticDiagnosticsPerFile = null!;
    public Dictionary<PathT, SignatureUpdateKind> UpdatedSignatureKinds = null!;
}
