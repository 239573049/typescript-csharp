// Ported from tsgo internal/execute/tsc.go (CommandLine / tscCompilation / performCompilation) and
// internal/execute/tsc/emit.go (EmitFilesAndReportErrors / EmitAndReportStatistics).
//
// SCOPE (CLI wave — tsc type-check-and-report subset, phase 7): this drives the `tscs file.ts` path:
// parse argv → build a Program from the command-line file names → collect syntactic + program + global
// + semantic diagnostics → format them through the diagnosticwriter → report a summary → set the exit
// code. This is exactly the --noEmit behaviour (type-check and report only).
//
// DELIBERATELY OUT OF SCOPE (stubbed with PORT-NOTE, this wave does type-check-and-report only):
//   * emit (Program.Emit is a phase 6 deliverable — printer/transformers). We never call Emit; the
//     result is treated as EmitSkipped, i.e. the --noEmit path.
//   * --watch / -b|--build / incremental / .tsbuildinfo — the watcher, build orchestrator and
//     incremental program are not ported. `tscs --build` and `tscs --watch` report "not implemented".
//   * tsconfig.json loading (GetParsedCommandLineOfConfigFile is a phase 4 stub) — the config-file
//     compilation path is stubbed; only the command-line-file path runs.
//   * --init / --showConfig / pprof / tracing / statistics.
//
// §1.5 (namespace shadowing): sibling-namespace static classes / types are fully qualified with global::.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Compiler;
using Ts.Core;

namespace Ts.Execute;

// Named Tsc (not Execute) to avoid the §1.5 namespace-shadowing trap: a static class named `Execute`
// inside namespace `Ts.Execute` shadows against the namespace tail, so unqualified `Execute.CommandLine`
// resolves the namespace instead of the class. Tsc mirrors Go's `execute.CommandLine` entry point.
public static class Tsc
{
    // tsc.go:52-63 — the entry dispatcher. -b/--build routes to the (stubbed) build path; everything
    // else routes to tsc compilation.
    public static CommandLineResult CommandLine(ISystem sys, string[] commandLineArgs)
    {
        if (commandLineArgs.Length > 0)
        {
            // Go: switch strings.ToLower(commandLineArgs[0]). These flags are ASCII, so an
            // ordinal-ignore-case comparison is exact and avoids the banned culture-sensitive ToLower.
            string first = commandLineArgs[0];
            if (EqualsOrdinalIgnoreCase(first, "-b") ||
                EqualsOrdinalIgnoreCase(first, "--b") ||
                EqualsOrdinalIgnoreCase(first, "-build") ||
                EqualsOrdinalIgnoreCase(first, "--build"))
            {
                // PORT-NOTE (deferred): the build orchestrator (execute/build) is out of scope for the
                // type-check-and-report wave.
                sys.Writer().Write("tscs: --build is not implemented in this build (type-check-and-report only).\n");
                return new CommandLineResult { Status = ExitStatus.NotImplemented };
            }
        }

        return TscCompilation(sys, global::Ts.TsOptions.CommandLineParser.ParseCommandLine(commandLineArgs, sys));
    }

    // tsc.go:121-264 — the tsc compilation driver, reduced to the type-check-and-report subset.
    private static CommandLineResult TscCompilation(ISystem sys, global::Ts.TsOptions.ParsedCommandLine commandLine)
    {
        var locale = global::Ts.Locale.Locale.Default; // PORT-NOTE: commandLine.Locale() is not ported; default tag.
        var compilerOptions = commandLine.CompilerOptions() ?? new CompilerOptions();
        var reportDiagnostic = DiagnosticReporters.CreateDiagnosticReporter(sys, sys.Writer(), locale, compilerOptions);

        // tsc.go:126-131 — command-line parse errors are unrecoverable.
        if (commandLine.Errors is not null && commandLine.Errors.Length > 0)
        {
            foreach (var e in commandLine.Errors)
            {
                reportDiagnostic(e);
            }
            return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
        }

        // tsc.go:144-147
        if (compilerOptions.Version.IsTrue())
        {
            PrintVersion(sys);
            return new CommandLineResult { Status = ExitStatus.Success };
        }

        // tsc.go:149-152
        if (compilerOptions.Help.IsTrue() || compilerOptions.All.IsTrue())
        {
            PrintVersion(sys);
            PrintHelp(sys);
            return new CommandLineResult { Status = ExitStatus.Success };
        }

        // tsc.go:154-157
        if (compilerOptions.Watch.IsTrue())
        {
            // PORT-NOTE (deferred): the watcher (execute/watcher.go) is out of scope for this wave.
            sys.Writer().Write("tscs: --watch is not implemented in this build (type-check-and-report only).\n");
            return new CommandLineResult { Status = ExitStatus.NotImplemented };
        }

        // tsc.go:159-197 — tsconfig.json discovery / --project. GetParsedCommandLineOfConfigFile is a
        // phase 4 stub, so the config-file compilation path can't run yet. If no files were given on the
        // command line and no config path was requested, print version + help (mirrors tsc.go:188-195's
        // "no inputs" fallthrough).
        var fileNames = commandLine.FileNames() ?? [];
        if (compilerOptions.Project != "")
        {
            // PORT-NOTE (deferred): --project needs GetParsedCommandLineOfConfigFile (phase 4 stub).
            sys.Writer().Write("tscs: --project / tsconfig.json compilation is not implemented in this build (command-line files only).\n");
            return new CommandLineResult { Status = ExitStatus.NotImplemented };
        }
        if (fileNames.Length == 0)
        {
            // PORT-NOTE: tsgo searches for tsconfig.json here (tsc.go:179-196). With config loading deferred,
            // "no command-line files" prints version + help, matching tsc's no-inputs behaviour.
            PrintVersion(sys);
            PrintHelp(sys);
            return new CommandLineResult { Status = ExitStatus.DiagnosticsPresent_OutputsSkipped };
        }

        var reportErrorSummary = DiagnosticReporters.CreateReportErrorSummary(sys, locale, compilerOptions);
        return PerformCompilation(sys, commandLine, reportDiagnostic, reportErrorSummary);
    }

    // tsc.go:333-371 — build the Program (NewCompilerHost → NewProgram) and hand off to the emit /
    // report shell. For the type-check-and-report wave, emit is skipped (--noEmit behaviour).
    private static CommandLineResult PerformCompilation(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary)
    {
        var host = CompilerHosts.NewCachedFSCompilerHost(
            sys.GetCurrentDirectory(),
            sys.FS(),
            sys.DefaultLibraryPath(),
            extendedConfigCache: null,
            trace: null);

        var program = new Program(new ProgramOptions
        {
            Host = host,
            Config = config,
        });

        var status = EmitFilesAndReportErrors(sys, program, config, reportDiagnostic, reportErrorSummary);
        return new CommandLineResult { Status = status };
    }

    // emit.go:73-132 — collect all diagnostics, (skip) emit, report them + summary, compute status.
    private static ExitStatus EmitFilesAndReportErrors(
        ISystem sys,
        Program program,
        global::Ts.TsOptions.ParsedCommandLine config,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary)
    {
        var ctx = CancellationToken.None;

        // emit.go:77-103 — bind + check diagnostics via the program's aggregation shell.
        var allDiagnostics = new List<Diagnostic>(Program.GetDiagnosticsOfAnyProgram(
            ctx,
            program,
            file: null,
            skipNoEmitCheckForDtsDiagnostics: false,
            getBindDiagnostics: (c, f) => program.GetBindDiagnostics(c, f),
            getSemanticDiagnostics: (c, f) => program.GetSemanticDiagnostics(c, f)));

        // emit.go:105-118 — emit is a phase 6 deliverable; this wave does type-check-and-report only, so
        // emit is skipped (EmitSkipped == true, the --noEmit behaviour). No output files are written.
        bool emitSkipped = true;

        // emit.go:120-123 — sort + dedupe, then report each diagnostic.
        var sorted = Program.SortAndDeduplicateDiagnostics(allDiagnostics.ToArray());
        foreach (var diagnostic in sorted)
        {
            reportDiagnostic(diagnostic);
        }

        // emit.go:127 — error summary.
        reportErrorSummary(sorted);

        // emit.go:65-69 (from EmitAndReportStatistics) — status from diagnostic presence + emit-skipped.
        if (emitSkipped && sorted.Length > 0)
        {
            return ExitStatus.DiagnosticsPresent_OutputsSkipped;
        }
        if (sorted.Length > 0)
        {
            return ExitStatus.DiagnosticsPresent_OutputsGenerated;
        }
        return ExitStatus.Success;
    }

    // PORT-NOTE: tsgo prints the version via tsc.PrintVersion (help.go, localized). Minimal port here
    // (task: keep --version smoke). Format mirrors `Version <n>`.
    private static void PrintVersion(ISystem sys)
    {
        string version = global::Ts.Core.TsVersion.Value;
        sys.Writer().Write($"Version {version}\n");
    }

    // Ordinal case-insensitive equality (ASCII flags). Avoids culture-sensitive ToLower (banned by the
    // InvariantGlobalization analyzer, plan/07 §7).
    private static bool EqualsOrdinalIgnoreCase(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // PORT-NOTE: the full localized help table (execute/tsc/help.go, ~16k) is out of scope for this wave.
    private static void PrintHelp(ISystem sys)
    {
        sys.Writer().Write("tscs — type-check a TypeScript program and report diagnostics.\n");
        sys.Writer().Write("Usage: tscs [options] file.ts...\n");
    }
}
