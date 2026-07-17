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

    // tsc.go:52-63 — entry dispatcher. The large-stack boundary covers argv/config parsing and every
    // parse/bind/check/emit path, including the production watch loop.
    public static CommandLineResult CommandLine(
        ISystem sys,
        string[] commandLineArgs,
        ICommandLineTesting? testing)
    {
        return global::Ts.Core.LargeStackExecutor.Run(() => CommandLineWorker(sys, commandLineArgs, testing));
    }

    private static CommandLineResult CommandLineWorker(
        ISystem sys,
        string[] commandLineArgs,
        ICommandLineTesting? testing)
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
                    testing);
            }
        }

        return TscCompilation(
            sys,
            global::Ts.TsOptions.CommandLineParser.ParseCommandLine(commandLineArgs, sys),
            testing);
    }

    // tsc.go:90-119
    private static CommandLineResult TscBuildCompilation(
        ISystem sys,
        global::Ts.TsOptions.ParsedBuildCommandLine buildCommand,
        ICommandLineTesting? testing)
    {
        // PORT-NOTE: ParsedBuildCommandLine.Locale() is still an explicit phase-9 stub in the
        // landed TsOptions surface. Default is Go's language.Und and therefore preserves the
        // English diagnostics used by this build-tail wave.
        var compilerOptions = buildCommand.CompilerOptions!;
        var reportDiagnostic = DiagnosticReporters.CreateDiagnosticReporter(
            sys,
            sys.Writer(),
            global::Ts.Locale.Locale.Default,
            compilerOptions);

        if (buildCommand.Errors is not null && buildCommand.Errors.Length > 0)
        {
            foreach (var error in buildCommand.Errors)
            {
                reportDiagnostic(error);
            }
            return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
        }

        if (IsTrue(compilerOptions.Help))
        {
            Help.PrintVersion(sys, global::Ts.Locale.Locale.Default);
            Help.PrintBuildHelp(sys, global::Ts.Locale.Locale.Default, global::Ts.TsOptions.DeclsBuild.BuildOpts);
            return new CommandLineResult { Status = ExitStatus.Success };
        }

        // tsc.go:118 — hand off to the 1:1 build orchestrator (build.NewOrchestrator + Start).
        return Build.Orchestrator.NewOrchestrator(new Build.BuildOptionsBag
        {
            Sys = sys,
            Command = buildCommand,
            Testing = testing,
        }).Start();
    }

    // tsc.go:121-264 — ordinary tsc compilation driver, including config discovery/loading and
    // watch-mode dispatch.
    private static CommandLineResult TscCompilation(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine commandLine,
        ICommandLineTesting? testing)
    {
        var locale = global::Ts.Locale.Locale.Default;
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
                Help.PrintVersion(sys, locale);
                Help.PrintHelp(sys, locale, commandLine);
                return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
            }
        }

        var compilerOptionsFromCommandLine = compilerOptions;
        var configForCompilation = commandLine;
        var extendedConfigCache = new TscExtendedConfigCache(); // tsc.go:202
        global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw = null;
        if (configFileName != "")
        {
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
            watcher.Start(CancellationToken.None);
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
                testing).Status,
        };
    }

    // tsc.go:333-371 — build Program and hand off to the shared emit/report shell.
    internal static CompilationOutcome PerformCompilationOutcome(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary,
        ICommandLineTesting? testing,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache = null)
    {
        testing?.OnCompilation(config);
        var host = global::Ts.Compiler.CompilerHosts.NewCachedFSCompilerHost(
            sys.GetCurrentDirectory(),
            sys.FS(),
            sys.DefaultLibraryPath(),
            extendedConfigCache,
            trace: null);
        var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Host = host,
            Config = config,
        });
        var outcome = EmitFilesAndReportErrors(program, program, reportDiagnostic, reportErrorSummary);
        testing?.OnCompilationComplete(config, outcome.Diagnostics.Length);
        return outcome;
    }

    // tsc.go:284-330 — incremental compilation: chain the previous .tsbuildinfo snapshot into a new
    // incremental.Program and drive the shared emit/report shell through it.
    // PORT-NOTE: CompileTimes/tracing/statistics reporting is deferred with the statistics tail
    // (consistent with the rest of this shell).
    internal static CompilationOutcome PerformIncrementalCompilationOutcome(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        ICommandLineTesting? testing)
    {
        testing?.OnCompilation(config);
        var host = global::Ts.Compiler.CompilerHosts.NewCachedFSCompilerHost(
            sys.GetCurrentDirectory(),
            sys.FS(),
            sys.DefaultLibraryPath(),
            extendedConfigCache,
            trace: null);
        var oldProgram = global::Ts.Execute.Incremental.BuildInfoReaderHelpers.ReadBuildInfoProgram(
            config,
            global::Ts.Execute.Incremental.BuildInfoReaderHelpers.NewBuildInfoReader(host),
            host);

        var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Config = config,
            Host = host,
        });
        var incrementalProgram = global::Ts.Execute.Incremental.Program.NewProgram(
            program,
            oldProgram,
            global::Ts.Execute.Incremental.HostHelpers.CreateHost(host),
            testing != null);
        var outcome = EmitFilesAndReportErrors(incrementalProgram, incrementalProgram.GetProgram(), reportDiagnostic, reportErrorSummary);
        testing?.OnCompilationComplete(config, outcome.Diagnostics.Length);
        return outcome;
    }

    // emit.go:73-132 — collect diagnostics, emit, report diagnostics/summary, compute status.
    internal static CompilationOutcome EmitFilesAndReportErrors(
        global::Ts.Compiler.ProgramLike programLike,
        global::Ts.Compiler.Program program,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary)
    {
        var ctx = CancellationToken.None;
        var allDiagnostics = new List<global::Ts.Ast.Diagnostic>(
            global::Ts.Compiler.Program.GetDiagnosticsOfAnyProgram(
                ctx,
                programLike,
                file: null,
                skipNoEmitCheckForDtsDiagnostics: false,
                getBindDiagnostics: (c, f) => programLike.GetBindDiagnostics(c, f),
                getSemanticDiagnostics: (c, f) => programLike.GetSemanticDiagnostics(c, f)));

        var emitResult = new global::Ts.Compiler.EmitResult
        {
            EmitSkipped = true,
            Diagnostics = Array.Empty<global::Ts.Ast.Diagnostic>(),
        };
        if (!IsTrue(programLike.Options().ListFilesOnly))
        {
            emitResult = programLike.Emit(ctx, new global::Ts.Compiler.EmitOptions()) ?? emitResult;
        }
        if (emitResult.Diagnostics is not null)
        {
            allDiagnostics.AddRange(emitResult.Diagnostics);
        }

        var sorted = global::Ts.Compiler.Program.SortAndDeduplicateDiagnostics(allDiagnostics.ToArray());
        foreach (var diagnostic in sorted)
        {
            reportDiagnostic(diagnostic);
        }
        reportErrorSummary(sorted);

        ExitStatus status;
        if (emitResult.EmitSkipped && sorted.Length > 0)
        {
            status = ExitStatus.DiagnosticsPresent_OutputsSkipped;
        }
        else if (sorted.Length > 0)
        {
            status = ExitStatus.DiagnosticsPresent_OutputsGenerated;
        }
        else
        {
            status = ExitStatus.Success;
        }

        return new CompilationOutcome
        {
            Program = program,
            Diagnostics = sorted,
            Status = status,
        };
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
