// Ported from tsgo internal/execute/build/buildtask.go.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Execute.Build;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// buildtask.go:22-28
internal enum BuildKind : uint
{
    None = 0,
    Pseudo,
    Program,
}

// buildtask.go:30-33
internal sealed class UpstreamTask
{
    internal BuildTask task = null!;
    internal int refIndex;
}

// buildtask.go:34-39
internal sealed class BuildInfoEntry
{
    internal global::Ts.Execute.Incremental.BuildInfo? buildInfo;
    internal PathT path;
    internal DateTime mTime;
    internal DateTime? dtsTime;
}

// buildtask.go:41-50
internal sealed class TaskResult
{
    internal readonly StringBuilder builder = new();
    internal DiagnosticReporter reportStatus = null!;
    internal DiagnosticReporter diagnosticReporter = null!;
    internal ExitStatus exitStatus;
    internal Statistics? statistics;
    internal global::Ts.Execute.Incremental.Program? program;
    internal BuildKind buildKind;
    internal List<string>? filesToDelete;
}

// buildtask.go:52-74
internal sealed class BuildTask
{
    internal string config = "";
    internal global::Ts.TsOptions.ParsedCommandLine? resolved;
    internal List<UpstreamTask> upStream = new();
    internal List<BuildTask> downStream = new(); // Only set and used in watch mode
    internal UpToDateStatus? status;
    internal ManualResetEventSlim done = null!; // Go: chan struct{} closed on completion

    // task reporting
    internal TaskResult? result;
    internal BuildTask? prevReporter;
    internal ManualResetEventSlim reportDone = null!;

    internal BuildInfoEntry? buildInfoEntry;
    internal readonly Lock buildInfoEntryMu = new();
    internal string[] packageJsons = [];

    internal List<global::Ts.Ast.Diagnostic> errors = new();
    private int pendingFlag;
    internal bool isInitialCycle;
    internal readonly Lock downStreamUpdateMu = new();
    internal bool dirty;

    internal bool Pending
    {
        get => Volatile.Read(ref pendingFlag) != 0;
        set => Volatile.Write(ref pendingFlag, value ? 1 : 0);
    }

    // buildtask.go:76-80
    internal void WaitOnUpstream()
    {
        foreach (var upstream in upStream)
        {
            upstream.task.done.Wait();
        }
    }

    // buildtask.go:82-86
    internal void UnblockDownstream()
    {
        Pending = false;
        isInitialCycle = false;
        done.Set();
    }

    // buildtask.go:88-91
    internal void ReportDiagnostic(global::Ts.Ast.Diagnostic err)
    {
        errors.Add(err);
        result!.diagnosticReporter(err);
    }

    // buildtask.go:93-121
    internal void Report(Orchestrator orchestrator, PathT configPath, OrchestratorResult buildResult)
    {
        prevReporter?.reportDone.Wait();
        if (errors.Count > 0)
        {
            buildResult.errors ??= new List<global::Ts.Ast.Diagnostic>();
            buildResult.errors.AddRange(errors);
        }
        orchestrator.opts.Sys.Writer().Write(result!.builder.ToString());
        if (result.exitStatus > buildResult.result.Status)
        {
            buildResult.result.Status = result.exitStatus;
        }
        if (result.statistics != null)
        {
            buildResult.statistics.Aggregate(result.statistics);
        }
        // If we built the program, or updated timestamps, or had errors, we need to
        // delete files that are no longer needed
        switch (result.buildKind)
        {
            case BuildKind.Program:
                orchestrator.opts.Testing?.OnProgram(result.program);
                buildResult.statistics.ProjectsBuilt++;
                break;
            case BuildKind.Pseudo:
                buildResult.statistics.TimestampUpdates++;
                break;
        }
        if (result.filesToDelete != null)
        {
            buildResult.filesToDelete ??= new List<string>();
            buildResult.filesToDelete.AddRange(result.filesToDelete);
        }
        result = null;
        reportDone.Set();
    }

    // buildtask.go:123-152
    internal void BuildProject(Orchestrator orchestrator, PathT path)
    {
        // Wait on upstream tasks to complete
        WaitOnUpstream();
        if (Pending)
        {
            status = GetUpToDateStatus(orchestrator, path);
            ReportUpToDateStatus(orchestrator);
            if (!HandleStatusThatDoesntRequireBuild(orchestrator))
            {
                CompileAndEmit(orchestrator, path);
                UpdateDownstream(orchestrator, path);
            }
            else
            {
                if (resolved != null)
                {
                    foreach (var diagnostic in resolved.GetConfigFileParsingDiagnostics() ?? [])
                    {
                        ReportDiagnostic(diagnostic);
                    }
                }
                if (errors.Count > 0)
                {
                    result!.exitStatus = ExitStatus.DiagnosticsPresent_OutputsSkipped;
                }
            }
        }
        else
        {
            if (errors.Count > 0)
            {
                ReportUpToDateStatus(orchestrator);
                foreach (var err in errors)
                {
                    // Should not add the diagnostics so just reporting
                    result!.diagnosticReporter(err);
                }
            }
        }
        UnblockDownstream();
    }

    // buildtask.go:154-188
    internal void UpdateDownstream(Orchestrator orchestrator, PathT path)
    {
        if (isInitialCycle)
        {
            return;
        }
        if (orchestrator.opts.Command.BuildOptions!.StopBuildOnErrors.IsTrue() && status!.IsError())
        {
            return;
        }

        foreach (var downStreamTask in downStream)
        {
            lock (downStreamTask.downStreamUpdateMu)
            {
                if (downStreamTask.status != null)
                {
                    switch (downStreamTask.status.kind)
                    {
                        case UpToDateStatusType.UpToDate:
                            if (!result!.program!.HasChangedDtsFile())
                            {
                                downStreamTask.status = new UpToDateStatus { kind = UpToDateStatusType.UpToDateWithUpstreamTypes, data = downStreamTask.status.data };
                                break;
                            }
                            goto case UpToDateStatusType.UpToDateWithUpstreamTypes; // Go fallthrough
                        case UpToDateStatusType.UpToDateWithUpstreamTypes:
                        case UpToDateStatusType.UpToDateWithInputFileText:
                            if (result!.program!.HasChangedDtsFile())
                            {
                                downStreamTask.status = new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = config, output = downStreamTask.status.OldestOutputFileName() } };
                            }
                            break;
                        case UpToDateStatusType.UpstreamErrors:
                            var upstreamErrors = downStreamTask.status.UpstreamErrors();
                            var refConfig = global::Ts.Core.Core.ResolveConfigFileNameOfProjectReference(upstreamErrors.reference);
                            if (orchestrator.ToPath(refConfig) == path)
                            {
                                downStreamTask.ResetStatus();
                            }
                            break;
                    }
                }
                downStreamTask.Pending = true;
            }
        }
    }

    // buildtask.go:190-254
    internal void CompileAndEmit(Orchestrator orchestrator, PathT path)
    {
        errors.Clear();
        if (orchestrator.opts.Command.BuildOptions!.Verbose.IsTrue())
        {
            result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Building_project_0, orchestrator.RelativeFileName(config)));
        }

        // Real build
        var compileTimes = new CompileTimes();
        orchestrator.host.configTimes.TryLoad(path, out var configTime);
        compileTimes.ConfigTime = configTime;
        var buildInfoReadStart = orchestrator.opts.Sys.Now();
        global::Ts.Execute.Incremental.Program? oldProgram = null;
        if (!orchestrator.opts.Command.BuildOptions.Force.IsTrue())
        {
            oldProgram = global::Ts.Execute.Incremental.BuildInfoReaderHelpers.ReadBuildInfoProgram(resolved!, orchestrator.host, orchestrator.host);
        }
        compileTimes.BuildInfoReadTime = orchestrator.opts.Sys.Now() - buildInfoReadStart;
        var parseStart = orchestrator.opts.Sys.Now();
        var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Config = resolved!,
            Host = new BuildCompilerHost
            {
                host = orchestrator.host,
                trace = EmitAndReport.GetTraceWithWriterFromSys(new StringWriter(result!.builder), global::Ts.Locale.Locale.Default, orchestrator.opts.Testing),
            },
        });
        compileTimes.ParseTime = orchestrator.opts.Sys.Now() - parseStart;
        var changesComputeStart = orchestrator.opts.Sys.Now();
        result.program = global::Ts.Execute.Incremental.Program.NewProgram(program, oldProgram, orchestrator.host, orchestrator.opts.Testing != null);
        compileTimes.ChangesComputeTime = orchestrator.opts.Sys.Now() - changesComputeStart;

        var (emitOutcome, statistics) = EmitAndReport.EmitAndReportStatistics(new EmitInput
        {
            Sys = orchestrator.opts.Sys,
            ProgramLike = result.program,
            Program = program,
            Config = resolved!,
            ReportDiagnostic = ReportDiagnostic,
            ReportErrorSummary = DiagnosticReporters.QuietDiagnosticsReporter,
            Writer = new StringWriter(result.builder),
            WriteFile = (fileName, text, data) => WriteFileWorker(orchestrator, fileName, text, data),
            CompileTimes = compileTimes,
            Testing = orchestrator.opts.Testing,
            TestingMTimesCache = orchestrator.host.mTimes,
        });
        result.exitStatus = emitOutcome.Status;
        result.statistics = statistics;
        packageJsons = result.program.PackageJsonLookupPaths();
        if ((!program.Options().NoEmitOnError.IsTrue() || emitOutcome.Diagnostics.Length == 0) &&
            ((emitOutcome.EmitResult!.EmittedFiles?.Length ?? 0) > 0 || status!.kind != UpToDateStatusType.OutOfDateBuildInfoWithErrors))
        {
            // Update time stamps for rest of the outputs
            UpdateTimeStamps(orchestrator, emitOutcome.EmitResult.EmittedFiles, Diag.Updating_unchanged_output_timestamps_of_project_0);
        }
        result.buildKind = BuildKind.Program;
        if (emitOutcome.Status == ExitStatus.DiagnosticsPresent_OutputsSkipped || emitOutcome.Status == ExitStatus.DiagnosticsPresent_OutputsGenerated)
        {
            status = new UpToDateStatus { kind = UpToDateStatusType.BuildErrors };
        }
        else
        {
            string oldestOutputFileName;
            if ((emitOutcome.EmitResult!.EmittedFiles?.Length ?? 0) > 0)
            {
                oldestOutputFileName = emitOutcome.EmitResult.EmittedFiles![0];
            }
            else
            {
                oldestOutputFileName = resolved!.GetOutputFileNames().FirstOrDefault() ?? "";
            }
            status = new UpToDateStatus { kind = UpToDateStatusType.UpToDate, data = oldestOutputFileName };
        }
    }

    // buildtask.go:256-304
    internal bool HandleStatusThatDoesntRequireBuild(Orchestrator orchestrator)
    {
        switch (status!.kind)
        {
            case UpToDateStatusType.UpToDate:
                if (orchestrator.opts.Command.BuildOptions!.Dry.IsTrue())
                {
                    result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Project_0_is_up_to_date, config));
                }
                return true;
            case UpToDateStatusType.UpstreamErrors:
            {
                var upstreamStatus = status.UpstreamErrors();
                if (orchestrator.opts.Command.BuildOptions!.Verbose.IsTrue())
                {
                    result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                        upstreamStatus.refHasUpstreamErrors
                            ? Diag.Skipping_build_of_project_0_because_its_dependency_1_was_not_built
                            : Diag.Skipping_build_of_project_0_because_its_dependency_1_has_errors,
                        orchestrator.RelativeFileName(config),
                        orchestrator.RelativeFileName(upstreamStatus.reference)));
                }
                return true;
            }
            case UpToDateStatusType.Solution:
                return true;
            case UpToDateStatusType.ConfigFileNotFound:
                ReportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.File_0_not_found, config));
                return true;
        }

        // update timestamps
        if (status.IsPseudoBuild())
        {
            if (orchestrator.opts.Command.BuildOptions!.Dry.IsTrue())
            {
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.A_non_dry_build_would_update_timestamps_for_output_of_project_0, config));
                status = new UpToDateStatus { kind = UpToDateStatusType.UpToDate };
                return true;
            }

            UpdateTimeStamps(orchestrator, null, Diag.Updating_output_timestamps_of_project_0);
            status = new UpToDateStatus { kind = UpToDateStatusType.UpToDate, data = status.data };
            result!.buildKind = BuildKind.Pseudo;
            return true;
        }

        if (orchestrator.opts.Command.BuildOptions!.Dry.IsTrue())
        {
            result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.A_non_dry_build_would_build_project_0, config));
            status = new UpToDateStatus { kind = UpToDateStatusType.UpToDate };
            return true;
        }
        return false;
    }

    // buildtask.go:306-564
    internal UpToDateStatus GetUpToDateStatus(Orchestrator orchestrator, PathT configPath)
    {
        if (status != null)
        {
            return status;
        }
        // Config file not found
        if (resolved == null)
        {
            return new UpToDateStatus { kind = UpToDateStatusType.ConfigFileNotFound };
        }

        // Solution - nothing to build
        if ((resolved.FileNames() ?? []).Length == 0 && resolved.ProjectReferences() != null)
        {
            return new UpToDateStatus { kind = UpToDateStatusType.Solution };
        }

        foreach (var upstream in upStream)
        {
            if (orchestrator.opts.Command.BuildOptions!.StopBuildOnErrors.IsTrue() && upstream.task.status!.IsError())
            {
                // Upstream project has errors, so we cannot build this project
                return new UpToDateStatus
                {
                    kind = UpToDateStatusType.UpstreamErrors,
                    data = new UpstreamErrorsData
                    {
                        reference = resolved.ProjectReferences()![upstream.refIndex].Path,
                        refHasUpstreamErrors = upstream.task.status!.kind == UpToDateStatusType.UpstreamErrors,
                    },
                };
            }
        }

        if (orchestrator.opts.Command.BuildOptions!.Force.IsTrue())
        {
            return new UpToDateStatus { kind = UpToDateStatusType.ForceBuild };
        }

        // Check the build info
        var buildInfoPath = resolved.GetBuildInfoFileName();
        string? buildInfoDirectoryMemo = null;
        string GetBuildInfoDirectory() => buildInfoDirectoryMemo ??=
            TsPathApi.GetDirectoryPath(TsPathApi.GetNormalizedAbsolutePath(buildInfoPath, orchestrator.comparePathsOptions.CurrentDirectory));
        var (buildInfo, buildInfoTime) = LoadOrStoreBuildInfo(orchestrator, configPath, buildInfoPath);
        if (buildInfo == null)
        {
            return new UpToDateStatus { kind = UpToDateStatusType.OutputMissing, data = buildInfoPath };
        }

        // build info version
        if (!buildInfo.IsValidVersion())
        {
            return new UpToDateStatus { kind = UpToDateStatusType.TsVersionOutputOfDate, data = buildInfo.Version };
        }

        // Report errors if build info indicates errors
        if (buildInfo.Errors || // Errors that need to be reported irrespective of "--noCheck"
            (!resolved.CompilerOptions()!.NoCheck.IsTrue() && (buildInfo.SemanticErrors || buildInfo.CheckPending))) // Errors without --noCheck
        {
            return new UpToDateStatus { kind = UpToDateStatusType.OutOfDateBuildInfoWithErrors, data = buildInfoPath };
        }

        if (resolved.CompilerOptions()!.IsIncremental())
        {
            if (!buildInfo.IsIncremental())
            {
                // Program options out of date
                return new UpToDateStatus { kind = UpToDateStatusType.OutOfDateOptions, data = buildInfoPath };
            }

            // Errors need to be reported if build info has errors
            if ((resolved.CompilerOptions()!.GetEmitDeclarations() && buildInfo.EmitDiagnosticsPerFile != null) || // Always reported errors
                (!resolved.CompilerOptions()!.NoCheck.IsTrue() && // Semantic errors if not --noCheck
                    (buildInfo.ChangeFileSet != null || buildInfo.SemanticDiagnosticsPerFile != null)))
            {
                return new UpToDateStatus { kind = UpToDateStatusType.OutOfDateBuildInfoWithErrors, data = buildInfoPath };
            }

            // Pending emit files
            if (!resolved.CompilerOptions()!.NoEmit.IsTrue() &&
                (buildInfo.ChangeFileSet != null || buildInfo.AffectedFilesPendingEmit != null))
            {
                return new UpToDateStatus { kind = UpToDateStatusType.OutOfDateBuildInfoWithPendingEmit, data = buildInfoPath };
            }

            // Some of the emit files like source map or dts etc are not yet done
            if (buildInfo.IsEmitPending(resolved, GetBuildInfoDirectory()))
            {
                return new UpToDateStatus { kind = UpToDateStatusType.OutOfDateOptions, data = buildInfoPath };
            }
        }
        var inputTextUnchanged = false;
        var oldestOutputFileAndTime = new FileAndTime { file = buildInfoPath, time = buildInfoTime };
        var newestInputFileAndTime = new FileAndTime();
        var seenRoots = new Set<PathT>();
        global::Ts.Execute.Incremental.BuildInfoRootInfoReader? rootInfoReaderMemo = null;
        global::Ts.Execute.Incremental.BuildInfoRootInfoReader GetBuildInfoRootInfoReaderMemo() => rootInfoReaderMemo ??=
            global::Ts.Execute.Incremental.BuildInfoRootInfoReaderExtensions.GetBuildInfoRootInfoReader(buildInfo, GetBuildInfoDirectory(), orchestrator.comparePathsOptions.UseCaseSensitiveFileNames);
        foreach (var inputFile in resolved.FileNames() ?? [])
        {
            var inputTime = orchestrator.host.GetMTime(inputFile);
            if (inputTime == default)
            {
                return new UpToDateStatus { kind = UpToDateStatusType.InputFileMissing, data = inputFile };
            }
            var inputPath = orchestrator.ToPath(inputFile);
            if (inputTime > oldestOutputFileAndTime.time)
            {
                var version = "";
                var currentVersion = "";
                if (buildInfo.IsIncremental())
                {
                    var (buildInfoFileInfo, resolvedInputPath) = GetBuildInfoRootInfoReaderMemo().GetBuildInfoFileInfo(inputPath);
                    var fileInfo = buildInfoFileInfo?.GetFileInfo();
                    if (fileInfo != null && fileInfo.Version().Length > 0)
                    {
                        version = fileInfo.Version();
                        if (orchestrator.host.FS().TryReadFile(resolvedInputPath.ToString(), out var text))
                        {
                            currentVersion = global::Ts.Execute.Incremental.IncrementalHash.ComputeHash(text, orchestrator.opts.Testing != null);
                            if (version == currentVersion)
                            {
                                inputTextUnchanged = true;
                            }
                        }
                    }
                }

                if (version.Length == 0 || version != currentVersion)
                {
                    return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = inputFile, output = buildInfoPath } };
                }
            }
            if (inputTime > newestInputFileAndTime.time)
            {
                newestInputFileAndTime = new FileAndTime { file = inputFile, time = inputTime };
            }
            seenRoots.Add(inputPath);
        }

        foreach (var root in GetBuildInfoRootInfoReaderMemo().Roots())
        {
            if (!seenRoots.Has(root))
            {
                // File was root file when project was built but its not any more
                return new UpToDateStatus { kind = UpToDateStatusType.OutOfDateRoots, data = new InputOutputName { input = root.ToString(), output = buildInfoPath } };
            }
        }

        if (buildInfo.IsIncremental())
        {
            var resolvedRoots = new Set<PathT>();
            foreach (var root in GetBuildInfoRootInfoReaderMemo().Roots())
            {
                var (_, resolvedRoot) = GetBuildInfoRootInfoReaderMemo().GetBuildInfoFileInfo(root);
                if (resolvedRoot.ToString().Length > 0)
                {
                    resolvedRoots.Add(resolvedRoot);
                }
            }
            var index = 0;
            foreach (var buildInfoFileInfo in buildInfo.FileInfos ?? [])
            {
                var buildInfoFileName = buildInfo.FileNames![index];
                index++;
                // Lib files bundled with the compiler can change only with the version of the compiler,
                // which is already verified with buildInfo.Version
                if (global::Ts.Execute.Incremental.BuildInfoHelpers.IsBuildInfoFileNameDefaultLibrary(buildInfoFileName))
                {
                    continue;
                }
                var inputFile = TsPathApi.GetNormalizedAbsolutePath(buildInfoFileName, GetBuildInfoDirectory());
                var inputPath = orchestrator.ToPath(inputFile);
                // Root files are already checked
                if (seenRoots.Has(inputPath) || resolvedRoots.Has(inputPath))
                {
                    continue;
                }
                var inputTime = orchestrator.host.GetMTime(inputFile);
                if (inputTime == default)
                {
                    // Input file that was part of the program is missing (eg: dependency was removed)
                    return new UpToDateStatus { kind = UpToDateStatusType.InputFileMissing, data = inputFile };
                }
                if (inputTime > oldestOutputFileAndTime.time)
                {
                    var currentVersion = "";
                    var version = buildInfoFileInfo.GetFileInfo().Version();
                    if (version.Length > 0)
                    {
                        if (orchestrator.host.FS().TryReadFile(inputFile, out var text))
                        {
                            currentVersion = global::Ts.Execute.Incremental.IncrementalHash.ComputeHash(text, orchestrator.opts.Testing != null);
                        }
                    }
                    if (version.Length == 0 || version != currentVersion)
                    {
                        return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = inputFile, output = buildInfoPath } };
                    }
                    inputTextUnchanged = true;
                }
            }
        }

        if (!resolved.CompilerOptions()!.IsIncremental())
        {
            // Check output file stamps
            foreach (var outputFile in resolved.GetOutputFileNames())
            {
                var outputTime = orchestrator.host.GetMTime(outputFile);
                if (outputTime == default)
                {
                    // Output file missing
                    return new UpToDateStatus { kind = UpToDateStatusType.OutputMissing, data = outputFile };
                }

                if (outputTime < newestInputFileAndTime.time)
                {
                    // Output file is older than input file
                    return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = newestInputFileAndTime.file, output = outputFile } };
                }

                if (outputTime < oldestOutputFileAndTime.time)
                {
                    oldestOutputFileAndTime = new FileAndTime { file = outputFile, time = outputTime };
                }
            }
        }

        var refDtsUnchanged = false;
        foreach (var upstream in upStream)
        {
            if (upstream.task.status!.kind == UpToDateStatusType.Solution)
            {
                // Not dependent on the status or this upstream project
                // (eg: expected cycle was detected and hence skipped, or is solution)
                continue;
            }

            // If the upstream project's newest file is older than our oldest output,
            // we can't be out of date because of it
            // inputTime will not be present if we just built this project or updated timestamps
            // - in that case we do want to either build or update timestamps
            var refInputOutputFileAndTime = upstream.task.status.InputOutputFileAndTime();
            if (refInputOutputFileAndTime != null && refInputOutputFileAndTime.input.time != default && refInputOutputFileAndTime.input.time < oldestOutputFileAndTime.time)
            {
                continue;
            }

            // Check if tsbuildinfo path is shared, then we need to rebuild
            if (HasConflictingBuildInfo(orchestrator, upstream.task))
            {
                // We have an output older than an upstream output - we are out of date
                return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = resolved.ProjectReferences()![upstream.refIndex].Path, output = oldestOutputFileAndTime.file } };
            }

            // If the upstream project has only change .d.ts files, and we've built
            // *after* those files, then we're "pseudo up to date" and eligible for a fast rebuild
            var newestDtsChangeTime = upstream.task.GetLatestChangedDtsMTime(orchestrator);
            if (newestDtsChangeTime != default && newestDtsChangeTime < oldestOutputFileAndTime.time)
            {
                refDtsUnchanged = true;
                continue;
            }

            // We have an output older than an upstream output - we are out of date
            return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = resolved.ProjectReferences()![upstream.refIndex].Path, output = oldestOutputFileAndTime.file } };
        }

        UpToDateStatus? CheckInputFileTime(string inputFile)
        {
            var inputTime = orchestrator.host.GetMTime(inputFile);
            if (inputTime > oldestOutputFileAndTime.time)
            {
                // Output file is older than input file
                return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = inputFile, output = oldestOutputFileAndTime.file } };
            }
            return null;
        }

        var configStatus = CheckInputFileTime(config);
        if (configStatus != null)
        {
            return configStatus;
        }

        foreach (var extendedConfig in resolved.ExtendedSourceFiles() ?? [])
        {
            var extendedConfigStatus = CheckInputFileTime(extendedConfig);
            if (extendedConfigStatus != null)
            {
                return extendedConfigStatus;
            }
        }

        foreach (var packageJson in buildInfo.GetPackageJsons(GetBuildInfoDirectory()))
        {
            var packageJsonTime = orchestrator.host.GetMTime(packageJson);
            if (packageJsonTime == default)
            {
                return new UpToDateStatus { kind = UpToDateStatusType.InputFileMissing, data = packageJson };
            }
            if (packageJsonTime > oldestOutputFileAndTime.time)
            {
                return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = packageJson, output = oldestOutputFileAndTime.file } };
            }
        }
        foreach (var packageJson in buildInfo.GetMissingPackageJsons(GetBuildInfoDirectory()))
        {
            if (orchestrator.host.GetMTime(packageJson) != default)
            {
                return new UpToDateStatus { kind = UpToDateStatusType.InputFileNewer, data = new InputOutputName { input = packageJson, output = oldestOutputFileAndTime.file } };
            }
        }
        packageJsons = buildInfo.GetPackageJsons(GetBuildInfoDirectory()).Concat(buildInfo.GetMissingPackageJsons(GetBuildInfoDirectory())).ToArray();

        return new UpToDateStatus
        {
            kind = refDtsUnchanged
                ? UpToDateStatusType.UpToDateWithUpstreamTypes
                : (inputTextUnchanged ? UpToDateStatusType.UpToDateWithInputFileText : UpToDateStatusType.UpToDate),
            data = new InputOutputFileAndTime { input = newestInputFileAndTime, output = oldestOutputFileAndTime, buildInfo = buildInfoPath },
        };
    }

    // buildtask.go:566-676
    internal void ReportUpToDateStatus(Orchestrator orchestrator)
    {
        if (!orchestrator.opts.Command.BuildOptions!.Verbose.IsTrue())
        {
            return;
        }
        switch (status!.kind)
        {
            case UpToDateStatusType.ConfigFileNotFound:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_config_file_does_not_exist,
                    orchestrator.RelativeFileName(config)));
                break;
            case UpToDateStatusType.UpstreamErrors:
            {
                var upstreamStatus = status.UpstreamErrors();
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    upstreamStatus.refHasUpstreamErrors
                        ? Diag.Project_0_can_t_be_built_because_its_dependency_1_was_not_built
                        : Diag.Project_0_can_t_be_built_because_its_dependency_1_has_errors,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName(upstreamStatus.reference)));
                break;
            }
            case UpToDateStatusType.BuildErrors:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_it_has_errors,
                    orchestrator.RelativeFileName(config)));
                break;
            case UpToDateStatusType.UpToDate:
                // This is to ensure skipping verbose log for projects that were built,
                // and then some other package changed but this package doesnt need update
                if (status.InputOutputFileAndTime() is { } inputOutputFileAndTime)
                {
                    result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                        Diag.Project_0_is_up_to_date_because_newest_input_1_is_older_than_output_2,
                        orchestrator.RelativeFileName(config),
                        orchestrator.RelativeFileName(inputOutputFileAndTime.input.file),
                        orchestrator.RelativeFileName(inputOutputFileAndTime.output.file)));
                }
                break;
            case UpToDateStatusType.UpToDateWithUpstreamTypes:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_up_to_date_with_d_ts_files_from_its_dependencies,
                    orchestrator.RelativeFileName(config)));
                break;
            case UpToDateStatusType.UpToDateWithInputFileText:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_up_to_date_but_needs_to_update_timestamps_of_output_files_that_are_older_than_input_files,
                    orchestrator.RelativeFileName(config)));
                break;
            case UpToDateStatusType.InputFileMissing:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_input_1_does_not_exist,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName((string)status.data!)));
                break;
            case UpToDateStatusType.OutputMissing:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_output_file_1_does_not_exist,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName((string)status.data!)));
                break;
            case UpToDateStatusType.InputFileNewer:
            {
                var inputOutput = status.InputOutputName()!;
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_output_1_is_older_than_input_2,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName(inputOutput.output),
                    orchestrator.RelativeFileName(inputOutput.input)));
                break;
            }
            case UpToDateStatusType.OutOfDateBuildInfoWithPendingEmit:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_that_some_of_the_changes_were_not_emitted,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName((string)status.data!)));
                break;
            case UpToDateStatusType.OutOfDateBuildInfoWithErrors:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_that_program_needs_to_report_errors,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName((string)status.data!)));
                break;
            case UpToDateStatusType.OutOfDateOptions:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_there_is_change_in_compilerOptions,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName((string)status.data!)));
                break;
            case UpToDateStatusType.OutOfDateRoots:
            {
                var inputOutput = status.InputOutputName()!;
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_that_file_2_was_root_file_of_compilation_but_not_any_more,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName(inputOutput.output),
                    orchestrator.RelativeFileName(inputOutput.input)));
                break;
            }
            case UpToDateStatusType.TsVersionOutputOfDate:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_out_of_date_because_output_for_it_was_generated_with_version_1_that_differs_with_current_version_2,
                    orchestrator.RelativeFileName(config),
                    orchestrator.RelativeFileName((string)status.data!),
                    global::Ts.Core.TsVersion.Version()));
                break;
            case UpToDateStatusType.ForceBuild:
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.Project_0_is_being_forcibly_rebuilt,
                    orchestrator.RelativeFileName(config)));
                break;
            case UpToDateStatusType.Solution:
                // Does not need to report status
                break;
            default:
                throw new InvalidOperationException($"Unknown up to date status kind: {status.kind}");
        }
    }

    // buildtask.go:678-680
    internal bool CanUpdateJsDtsOutputTimestamps() =>
        !resolved!.CompilerOptions()!.NoEmit.IsTrue() && !resolved.CompilerOptions()!.IsIncremental();

    // buildtask.go:682-715
    internal void UpdateTimeStamps(Orchestrator orchestrator, string[]? emittedFiles, global::Ts.Diagnostics.Message verboseMessage)
    {
        var emitted = new Set<string>();
        foreach (var f in emittedFiles ?? [])
        {
            emitted.Add(f);
        }
        var verboseMessageReported = false;
        var buildInfoName = resolved!.GetBuildInfoFileName();
        var now = orchestrator.opts.Sys.Now();
        void UpdateTimeStamp(string file)
        {
            if (emitted.Has(file))
            {
                return;
            }
            if (!verboseMessageReported && orchestrator.opts.Command.BuildOptions!.Verbose.IsTrue())
            {
                result!.reportStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(verboseMessage, orchestrator.RelativeFileName(config)));
                verboseMessageReported = true;
            }
            try
            {
                orchestrator.host.SetMTime(file, now);
            }
            catch
            {
                return;
            }
            if (file == buildInfoName)
            {
                lock (buildInfoEntryMu)
                {
                    if (buildInfoEntry != null)
                    {
                        buildInfoEntry.mTime = now;
                    }
                }
            }
            else if (StoreOutputTimeStamp(orchestrator))
            {
                orchestrator.host.StoreMTime(file, now);
            }
        }

        if (CanUpdateJsDtsOutputTimestamps())
        {
            foreach (var outputFile in resolved.GetOutputFileNames())
            {
                UpdateTimeStamp(outputFile);
            }
        }
        UpdateTimeStamp(resolved.GetBuildInfoFileName());
    }

    // buildtask.go:717-729
    internal void CleanProject(Orchestrator orchestrator, PathT path)
    {
        if (resolved == null)
        {
            ReportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.File_0_not_found, config));
            result!.exitStatus = ExitStatus.DiagnosticsPresent_OutputsSkipped;
            return;
        }

        var inputs = new Set<PathT>();
        foreach (var fileName in resolved.FileNames() ?? [])
        {
            inputs.Add(orchestrator.ToPath(fileName));
        }
        foreach (var outputFile in resolved.GetOutputFileNames())
        {
            CleanProjectOutput(orchestrator, outputFile, inputs);
        }
        CleanProjectOutput(orchestrator, resolved.GetBuildInfoFileName(), inputs);
    }

    // buildtask.go:731-747
    internal void CleanProjectOutput(Orchestrator orchestrator, string outputFile, Set<PathT> inputs)
    {
        var outputPath = orchestrator.ToPath(outputFile);
        // If output name is same as input file name, do not delete and ignore the error
        if (inputs.Has(outputPath))
        {
            return;
        }
        if (orchestrator.host.FS().FileExists(outputFile))
        {
            if (!orchestrator.opts.Command.BuildOptions!.Dry.IsTrue())
            {
                try
                {
                    orchestrator.host.FS().Remove(outputFile);
                }
                catch
                {
                    ReportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Failed_to_delete_file_0, outputFile));
                }
            }
            else
            {
                result!.filesToDelete ??= new List<string>();
                result.filesToDelete.Add(outputFile);
            }
        }
    }

    // buildtask.go:749-757
    internal void UpdateWatch(Orchestrator orchestrator, SyncMap<PathT, DateTime> oldCache)
    {
        if (resolved != null)
        {
            if (CanUpdateJsDtsOutputTimestamps())
            {
                foreach (var outputFile in resolved.GetOutputFileNames())
                {
                    orchestrator.host.StoreMTimeFromOldCache(outputFile, oldCache);
                }
            }
        }
    }

    // buildtask.go:759-763
    internal void ResetStatus()
    {
        status = null;
        Pending = true;
        errors.Clear();
    }

    // buildtask.go:765-768
    internal void ResetConfig(Orchestrator orchestrator, PathT path)
    {
        dirty = true;
        orchestrator.host.resolvedReferences.Delete(path);
    }

    // buildtask.go:770-787
    internal (global::Ts.Execute.Incremental.BuildInfo? BuildInfo, DateTime MTime) LoadOrStoreBuildInfo(Orchestrator orchestrator, PathT configPath, string buildInfoFileName)
    {
        var path = orchestrator.ToPath(buildInfoFileName);
        lock (buildInfoEntryMu)
        {
            if (buildInfoEntry != null && buildInfoEntry.path == path)
            {
                return (buildInfoEntry.buildInfo, buildInfoEntry.mTime);
            }
            buildInfoEntry = new BuildInfoEntry
            {
                buildInfo = ReadBuildInfoWorker(orchestrator),
                path = path,
            };
            var mTime = default(DateTime);
            if (buildInfoEntry.buildInfo != null)
            {
                mTime = orchestrator.host.GetMTime(buildInfoFileName);
            }
            buildInfoEntry.mTime = mTime;
            return (buildInfoEntry.buildInfo, mTime);
        }
    }

    // buildtask.go:778 — incremental.NewBuildInfoReader(orchestrator.host).ReadBuildInfo(t.resolved)
    private global::Ts.Execute.Incremental.BuildInfo? ReadBuildInfoWorker(Orchestrator orchestrator)
    {
        if (resolved == null)
        {
            return null;
        }
        return global::Ts.Execute.Incremental.BuildInfoReaderHelpers.NewBuildInfoReader(orchestrator.host).ReadBuildInfo(resolved);
    }

    // buildtask.go:789-805
    internal void OnBuildInfoEmit(Orchestrator orchestrator, string buildInfoFileName, global::Ts.Execute.Incremental.BuildInfo buildInfo, bool hasChangedDtsFile)
    {
        lock (buildInfoEntryMu)
        {
            DateTime? dtsTime = null;
            var mTime = orchestrator.opts.Sys.Now();
            if (hasChangedDtsFile)
            {
                dtsTime = mTime;
            }
            else if (buildInfoEntry != null)
            {
                dtsTime = buildInfoEntry.dtsTime;
            }
            buildInfoEntry = new BuildInfoEntry
            {
                buildInfo = buildInfo,
                path = orchestrator.ToPath(buildInfoFileName),
                mTime = mTime,
                dtsTime = dtsTime,
            };
        }
    }

    // buildtask.go:807-812
    internal bool HasConflictingBuildInfo(Orchestrator orchestrator, BuildTask upstream)
    {
        if (buildInfoEntry != null && upstream.buildInfoEntry != null)
        {
            return buildInfoEntry.path == upstream.buildInfoEntry.path;
        }
        return false;
    }

    // buildtask.go:814-828
    internal DateTime GetLatestChangedDtsMTime(Orchestrator orchestrator)
    {
        lock (buildInfoEntryMu)
        {
            if (buildInfoEntry!.dtsTime is { } existing)
            {
                return existing;
            }
            var dtsTime = orchestrator.host.GetMTime(
                TsPathApi.GetNormalizedAbsolutePath(
                    buildInfoEntry.buildInfo!.LatestChangedDtsFile,
                    TsPathApi.GetDirectoryPath(buildInfoEntry.path.ToString())));
            buildInfoEntry.dtsTime = dtsTime;
            return dtsTime;
        }
    }

    // buildtask.go:830-832
    internal bool StoreOutputTimeStamp(Orchestrator orchestrator) =>
        orchestrator.opts.Command.CompilerOptions!.Watch.IsTrue() && !resolved!.CompilerOptions()!.IsIncremental();

    // buildtask.go:834-845
    internal void WriteFileWorker(Orchestrator orchestrator, string fileName, string text, global::Ts.Compiler.WriteFileData? data)
    {
        orchestrator.host.FS().WriteFile(fileName, text);
        if (data != null && data.BuildInfo != null)
        {
            OnBuildInfoEmit(orchestrator, fileName, (global::Ts.Execute.Incremental.BuildInfo)data.BuildInfo, result!.program!.HasChangedDtsFile());
        }
        else if (StoreOutputTimeStamp(orchestrator))
        {
            // Store time stamps
            orchestrator.host.StoreMTime(fileName, orchestrator.opts.Sys.Now());
        }
    }
}
