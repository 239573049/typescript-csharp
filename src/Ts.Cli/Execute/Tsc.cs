// Ported from tsgo internal/execute/tsc.go (CommandLine, tscCompilation,
// tscBuildCompilation, performCompilation) and internal/execute/tsc/emit.go.
//
// This file owns the CLI dispatcher and shared full-compilation primitive. Watch-session mechanics
// live in Watcher.cs; the dependency graph and --build orchestration live in Build.cs.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Execute;

internal sealed class CompilationOutcome
{
    internal global::Ts.Compiler.Program Program = null!;
    internal global::Ts.Ast.Diagnostic[] Diagnostics = Array.Empty<global::Ts.Ast.Diagnostic>();
    internal ExitStatus Status;
}

// Named Tsc (not Execute) to avoid the plan/05 §1.5 namespace-tail shadowing trap.
public static class Tsc
{
    public static CommandLineResult CommandLine(ISystem sys, string[] commandLineArgs)
    {
        return CommandLine(sys, commandLineArgs, testing: null);
    }

    public static CommandLineResult CommandLine(
        ISystem sys,
        string[] commandLineArgs,
        ICommandLineTesting? testing)
    {
        return CommandLine(sys, commandLineArgs, testing, CancellationToken.None);
    }

    // tsc.go:52-63 — entry dispatcher. The large-stack boundary covers argv/config parsing and every
    // parse/bind/check/emit path, including the production watch loop. Go's leading ctx
    // (main.go:30) is carried as the trailing CancellationToken, matching Watcher.Start/Orchestrator.
    public static CommandLineResult CommandLine(
        ISystem sys,
        string[] commandLineArgs,
        ICommandLineTesting? testing,
        CancellationToken ctx)
    {
        return global::Ts.Core.LargeStackExecutor.Run(() => CommandLineWorker(sys, commandLineArgs, testing, ctx));
    }

    private static CommandLineResult CommandLineWorker(
        ISystem sys,
        string[] commandLineArgs,
        ICommandLineTesting? testing,
        CancellationToken ctx)
    {
        if (commandLineArgs.Length > 0)
        {
            string first = commandLineArgs[0];
            if (EqualsOrdinalIgnoreCase(first, "-b") ||
                EqualsOrdinalIgnoreCase(first, "--b") ||
                EqualsOrdinalIgnoreCase(first, "-build") ||
                EqualsOrdinalIgnoreCase(first, "--build"))
            {
                return TscBuildCompilation(
                    sys,
                    global::Ts.TsOptions.CommandLineParser.ParseBuildCommandLine(commandLineArgs, sys),
                    testing,
                    ctx);
            }
        }

        return TscCompilation(
            sys,
            global::Ts.TsOptions.CommandLineParser.ParseCommandLine(commandLineArgs, sys),
            testing,
            ctx);
    }

    // tsc.go:27-41 — start the --generateTrace session when requested. Go returns
    // (*Tracing, error) and prints a warning on failure; the C# StartTracing surfaces the
    // same failure as IOException (plan/05 §4), caught here to keep the identical message.
    private static global::Ts.Tracing.Tracing? StartTracingIfNeeded(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        ICommandLineTesting? testing)
    {
        var traceDir = config.CompilerOptions()!.GenerateTrace;
        if (traceDir == "")
        {
            return null;
        }
        var configFilePath = "";
        if (config.ConfigFile != null && config.ConfigFile.SourceFile != null)
        {
            configFilePath = config.ConfigFile.SourceFile.FileName();
        }
        try
        {
            return global::Ts.Tracing.Tracing.StartTracing(sys.FS(), traceDir, configFilePath, testing != null);
        }
        catch (System.IO.IOException ex)
        {
            sys.Writer().Write($"Warning: Failed to start tracing: {ex.Message}\n");
            return null;
        }
    }

    // tsc.go:43-50
    private static void StopTracing(ISystem sys, global::Ts.Tracing.Tracing? tr)
    {
        if (tr == null)
        {
            return;
        }
        try
        {
            tr.StopTracing();
        }
        catch (System.IO.IOException ex)
        {
            sys.Writer().Write($"Warning: Failed to stop tracing: {ex.Message}\n");
        }
    }

    // tsc.go:90-119
    private static CommandLineResult TscBuildCompilation(
        ISystem sys,
        global::Ts.TsOptions.ParsedBuildCommandLine buildCommand,
        ICommandLineTesting? testing,
        CancellationToken ctx)
    {
        var locale = buildCommand.Locale(); // tsc.go:91
        var compilerOptions = buildCommand.CompilerOptions!;
        var reportDiagnostic = DiagnosticReporters.CreateDiagnosticReporter(
            sys,
            sys.Writer(),
            locale,
            compilerOptions);

        if (buildCommand.Errors is not null && buildCommand.Errors.Length > 0)
        {
            foreach (var error in buildCommand.Errors)
            {
                reportDiagnostic(error);
            }
            return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
        }

        global::Ts.Profiling.ProfileSession? profileSession = null;
        try
        {
            // tsc.go:101-105
            if (compilerOptions.PprofDir != "")
            {
                // !!! stderr?
                profileSession = global::Ts.Profiling.Pprof.BeginProfiling(compilerOptions.PprofDir, sys.Writer());
            }

            if (IsTrue(compilerOptions.Help))
            {
                Help.PrintVersion(sys, locale);
                Help.PrintBuildHelp(sys, locale, global::Ts.TsOptions.DeclsBuild.BuildOpts);
                return new CommandLineResult { Status = ExitStatus.Success };
            }

            // tsc.go:118 — hand off to the 1:1 build orchestrator (build.NewOrchestrator + Start).
            return Build.Orchestrator.NewOrchestrator(new Build.BuildOptionsBag
            {
                Sys = sys,
                Command = buildCommand,
                Testing = testing,
            }).Start(ctx);
        }
        finally
        {
            // tsc.go:104 — Go: defer profileSession.Stop()
            profileSession?.Stop();
        }
    }

    // tsc.go:121-264 — ordinary tsc compilation driver, including config discovery/loading and
    // watch-mode dispatch.
    private static CommandLineResult TscCompilation(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine commandLine,
        ICommandLineTesting? testing,
        CancellationToken ctx)
    {
        var locale = commandLine.Locale(); // tsc.go:123
        var compilerOptions = commandLine.CompilerOptions()!;
        var reportDiagnostic = DiagnosticReporters.CreateDiagnosticReporter(
            sys,
            sys.Writer(),
            locale,
            compilerOptions);

        if (commandLine.Errors is not null && commandLine.Errors.Length > 0)
        {
            foreach (var error in commandLine.Errors)
            {
                reportDiagnostic(error);
            }
            return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
        }

        global::Ts.Profiling.ProfileSession? profileSession = null;
        try
        {
            // tsc.go:133-137
            if (compilerOptions.PprofDir != "")
            {
                // !!! stderr?
                profileSession = global::Ts.Profiling.Pprof.BeginProfiling(compilerOptions.PprofDir, sys.Writer());
            }

            if (IsTrue(compilerOptions.Init))
            {
                Init.WriteConfigFile(sys, locale, reportDiagnostic, (commandLine.Raw as global::Ts.Collections.OrderedMap<string, object?>)!);
                return new CommandLineResult { Status = ExitStatus.Success };
            }

            if (IsTrue(compilerOptions.Version))
            {
                Help.PrintVersion(sys, locale);
                return new CommandLineResult { Status = ExitStatus.Success };
            }

            if (IsTrue(compilerOptions.Help) || IsTrue(compilerOptions.All))
            {
                Help.PrintHelp(sys, locale, commandLine);
                return new CommandLineResult { Status = ExitStatus.Success };
            }

            if (IsTrue(compilerOptions.Watch) && IsTrue(compilerOptions.ListFilesOnly))
            {
                reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    global::Ts.Diagnostics.Diagnostics.Options_0_and_1_cannot_be_combined,
                    "watch",
                    "listFilesOnly"));
                return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
            }

            string configFileName = "";
            string[] fileNames = commandLine.FileNames() ?? Array.Empty<string>();
            if (compilerOptions.Project != "")
            {
                if (fileNames.Length != 0)
                {
                    reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                        global::Ts.Diagnostics.Diagnostics.Option_project_cannot_be_mixed_with_source_files_on_a_command_line));
                    return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
                }

                string fileOrDirectory = global::Ts.TsPath.TsPath.NormalizePath(compilerOptions.Project);
                if (sys.FS().DirectoryExists(fileOrDirectory))
                {
                    configFileName = global::Ts.TsPath.TsPath.CombinePaths(fileOrDirectory, "tsconfig.json");
                    if (!sys.FS().FileExists(configFileName))
                    {
                        reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.Cannot_find_a_tsconfig_json_file_at_the_current_directory_Colon_0,
                            configFileName));
                        return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
                    }
                }
                else
                {
                    configFileName = fileOrDirectory;
                    if (!sys.FS().FileExists(configFileName))
                    {
                        reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.The_specified_path_does_not_exist_Colon_0,
                            fileOrDirectory));
                        return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
                    }
                }
            }
            else if (!IsTrue(compilerOptions.IgnoreConfig) || fileNames.Length == 0)
            {
                string searchPath = global::Ts.TsPath.TsPath.NormalizePath(sys.GetCurrentDirectory());
                configFileName = FindConfigFile(searchPath, sys.FS().FileExists, "tsconfig.json");
                if (fileNames.Length != 0)
                {
                    if (configFileName != "")
                    {
                        reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.X_tsconfig_json_is_present_but_will_not_be_loaded_if_files_are_specified_on_commandline_Use_ignoreConfig_to_skip_this_error));
                        return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
                    }
                }
                else if (configFileName == "")
                {
                    if (IsTrue(compilerOptions.ShowConfig))
                    {
                        // tsc.go:189-190
                        reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.Cannot_find_a_tsconfig_json_file_at_the_current_directory_Colon_0,
                            global::Ts.TsPath.TsPath.NormalizePath(sys.GetCurrentDirectory())));
                    }
                    else
                    {
                        Help.PrintVersion(sys, locale);
                        Help.PrintHelp(sys, locale, commandLine);
                    }
                    return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
                }
            }

            var compilerOptionsFromCommandLine = compilerOptions;
            var configForCompilation = commandLine;
            var extendedConfigCache = new TscExtendedConfigCache(); // tsc.go:202
            var compileTimes = new CompileTimes(); // tsc.go:203
            global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw = null;
            if (configFileName != "")
            {
                var configStart = sys.Now(); // tsc.go:206
                if (commandLine.Raw is global::Ts.Collections.OrderedMap<string, object?> raw)
                {
                    commandLineRaw = new global::Ts.Collections.OrderedMap<string, object?>();
                    commandLineRaw.Set("compilerOptions", raw);
                }

                var (configParseResult, errors) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFile(
                    configFileName,
                    compilerOptionsFromCommandLine,
                    commandLineRaw,
                    sys,
                    extendedConfigCache);
                compileTimes.ConfigTime = sys.Now() - configStart; // tsc.go:214
                if (errors is not null && errors.Length > 0)
                {
                    foreach (var error in errors)
                    {
                        reportDiagnostic(error);
                    }
                    return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsGenerated };
                }

                // Go unconditionally assigns configParseResult after the no-errors branch; retain the
                // same invariant with null-forgiving rather than adding a divergent fallback.
                configForCompilation = configParseResult!;
                reportDiagnostic = DiagnosticReporters.CreateDiagnosticReporter(
                    sys,
                    sys.Writer(),
                    locale,
                    compilerOptions);
            }

            var reportErrorSummary = DiagnosticReporters.CreateReportErrorSummary(
                sys,
                locale,
                configForCompilation.CompilerOptions()!);
            if (IsTrue(compilerOptionsFromCommandLine.ShowConfig))
            {
                // tsc.go:228-231
                ShowConfig(sys, configForCompilation, configFileName);
                return new CommandLineResult { Status = ExitStatus.Success };
            }
            if (IsTrue(configForCompilation.CompilerOptions()!.Watch))
            {
                // tsc.go:233-243 — createWatcher + start.
                var watcher = Watcher.CreateWatcher(
                    sys,
                    configForCompilation,
                    compilerOptionsFromCommandLine,
                    commandLineRaw,
                    reportDiagnostic,
                    reportErrorSummary,
                    testing);
                watcher.Start(ctx);
                return new CommandLineResult
                {
                    Status = ExitStatus.Success,
                    Watcher = watcher,
                };
            }

            if (configForCompilation.CompilerOptions()!.IsIncremental())
            {
                return new CommandLineResult
                {
                    Status = PerformIncrementalCompilationOutcome(
                        sys,
                        configForCompilation,
                        reportDiagnostic,
                        reportErrorSummary,
                        extendedConfigCache,
                        compileTimes,
                        testing).Status,
                };
            }

            return new CommandLineResult
            {
                Status = PerformCompilationOutcome(
                    sys,
                    configForCompilation,
                    reportDiagnostic,
                    reportErrorSummary,
                    extendedConfigCache,
                    compileTimes,
                    testing).Status,
            };
        }
        finally
        {
            // tsc.go:136 — Go: defer profileSession.Stop()
            profileSession?.Stop();
        }
    }

    // tsc.go:333-371 — build Program and hand off to the shared emit/report shell
    // (EmitAndReportStatistics), with --generateTrace and CompileTimes.ParseTime wired.
    internal static CompilationOutcome PerformCompilationOutcome(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        CompileTimes compileTimes,
        ICommandLineTesting? testing)
    {
        testing?.OnCompilation(config);
        var host = global::Ts.Compiler.CompilerHosts.NewCachedFSCompilerHost(
            sys.GetCurrentDirectory(),
            sys.FS(),
            sys.DefaultLibraryPath(),
            extendedConfigCache,
            EmitAndReport.GetTraceWithWriterFromSys(sys.Writer(), config.Locale(), testing)); // tsc.go:342 getTraceFromSys

        var tr = StartTracingIfNeeded(sys, config, testing); // tsc.go:344

        var parseStart = sys.Now();
        var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Host = host,
            Config = config,
            Tracing = tr, // tsc.go:350
        });
        compileTimes.ParseTime = sys.Now() - parseStart; // tsc.go:352
        var (result, _) = EmitAndReport.EmitAndReportStatistics(new EmitInput // tsc.go:353-364
        {
            Sys = sys,
            ProgramLike = program,
            Program = program,
            Config = config,
            ReportDiagnostic = reportDiagnostic,
            ReportErrorSummary = reportErrorSummary,
            Writer = sys.Writer(),
            CompileTimes = compileTimes,
            Testing = testing,
            Tracing = tr,
        });

        StopTracing(sys, tr); // tsc.go:366

        testing?.OnCompilationComplete(config, result.Diagnostics.Length);
        return new CompilationOutcome
        {
            Program = program,
            Diagnostics = result.Diagnostics,
            Status = result.Status,
        };
    }

    // tsc.go:284-330 — incremental compilation: chain the previous .tsbuildinfo snapshot into a new
    // incremental.Program and drive the shared emit/report shell through it, with tracing and the
    // BuildInfoRead/Parse/ChangesCompute timings wired.
    internal static CompilationOutcome PerformIncrementalCompilationOutcome(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        CompileTimes compileTimes,
        ICommandLineTesting? testing)
    {
        testing?.OnCompilation(config);
        var host = global::Ts.Compiler.CompilerHosts.NewCachedFSCompilerHost(
            sys.GetCurrentDirectory(),
            sys.FS(),
            sys.DefaultLibraryPath(),
            extendedConfigCache,
            EmitAndReport.GetTraceWithWriterFromSys(sys.Writer(), config.Locale(), testing)); // tsc.go:293 getTraceFromSys

        var buildInfoReadStart = sys.Now(); // tsc.go:294
        var oldProgram = global::Ts.Execute.Incremental.BuildInfoReaderHelpers.ReadBuildInfoProgram(
            config,
            global::Ts.Execute.Incremental.BuildInfoReaderHelpers.NewBuildInfoReader(host),
            host);
        compileTimes.BuildInfoReadTime = sys.Now() - buildInfoReadStart; // tsc.go:296

        var tr = StartTracingIfNeeded(sys, config, testing); // tsc.go:298

        var parseStart = sys.Now();
        var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Config = config,
            Host = host,
            Tracing = tr, // tsc.go:304
        });
        compileTimes.ParseTime = sys.Now() - parseStart; // tsc.go:306

        var changesComputeStart = sys.Now(); // tsc.go:307
        var incrementalProgram = global::Ts.Execute.Incremental.Program.NewProgram(
            program,
            oldProgram,
            global::Ts.Execute.Incremental.HostHelpers.CreateHost(host),
            testing != null);
        compileTimes.ChangesComputeTime = sys.Now() - changesComputeStart; // tsc.go:309

        var (result, _) = EmitAndReport.EmitAndReportStatistics(new EmitInput // tsc.go:310-321
        {
            Sys = sys,
            ProgramLike = incrementalProgram,
            Program = incrementalProgram.GetProgram(),
            Config = config,
            ReportDiagnostic = reportDiagnostic,
            ReportErrorSummary = reportErrorSummary,
            Writer = sys.Writer(),
            CompileTimes = compileTimes,
            Testing = testing,
            Tracing = tr,
        });

        StopTracing(sys, tr); // tsc.go:323

        testing?.OnProgram(incrementalProgram); // tsc.go:325-327
        testing?.OnCompilationComplete(config, result.Diagnostics.Length);
        return new CompilationOutcome
        {
            Program = incrementalProgram.GetProgram(),
            Diagnostics = result.Diagnostics,
            Status = result.Status,
        };
    }

    // tsc.go:373-376 — emit the resolved tsconfig as 4-space-indented JSON.
    private static void ShowConfig(ISystem sys, global::Ts.TsOptions.ParsedCommandLine config, string configFileName)
    {
        var tsConfig = global::Ts.TsOptions.ShowConfig.ConvertToTSConfig(config, configFileName);
        global::Ts.TsOptions.ShowConfig.MarshalIndentWrite(sys.Writer(), tsConfig);
    }

    private static string FindConfigFile(string searchPath, Func<string, bool> fileExists, string configName)
    {
        string current = searchPath;
        while (true)
        {
            string candidate = global::Ts.TsPath.TsPath.CombinePaths(current, configName);
            if (fileExists(candidate))
            {
                return candidate;
            }
            string parent = global::Ts.TsPath.TsPath.GetDirectoryPath(current);
            if (parent == current)
            {
                return "";
            }
            current = parent;
        }
    }

    private static bool EqualsOrdinalIgnoreCase(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(global::Ts.Core.Tristate value) =>
        value == global::Ts.Core.Tristate.True;

}
