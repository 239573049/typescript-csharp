// Ported from tsgo internal/execute/tsc/emit.go and statistics.go — the shared typed
// emit/report shell (EmitInput → EmitFilesAndReportErrors → EmitAndReportStatistics) used by the
// plain, incremental and --build drivers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using Ts.Tracing;

namespace Ts.Execute;

using PathT = global::Ts.TsPath.Path;

// compile.go — CompileTimes (referenced by emit.go/statistics.go; ConfigTime/BuildInfoReadTime/
// ParseTime/ChangesComputeTime are set by the drivers, bind/check/emit/total by the shell).
public sealed class CompileTimes
{
    public TimeSpan ConfigTime;
    public TimeSpan BuildInfoReadTime;
    public TimeSpan ParseTime;
    public TimeSpan ChangesComputeTime;
    internal TimeSpan bindTime;
    internal TimeSpan checkTime;
    internal TimeSpan emitTime;
    internal TimeSpan totalTime;
}

// compile.go — CompileAndEmitResult.
public sealed class CompileAndEmitResult
{
    public global::Ts.Ast.Diagnostic[] Diagnostics = [];
    public global::Ts.Compiler.EmitResult? EmitResult;
    public ExitStatus Status;
    internal CompileTimes? times;
}

// emit.go:30-43
public sealed class EmitInput
{
    public ISystem Sys = null!;
    public global::Ts.Compiler.ProgramLike ProgramLike = null!;
    public global::Ts.Compiler.Program Program = null!;
    public global::Ts.TsOptions.ParsedCommandLine Config = null!;
    public DiagnosticReporter ReportDiagnostic = null!;
    public DiagnosticsReporter ReportErrorSummary = null!;
    public TextWriter Writer = null!;
    public global::Ts.Compiler.WriteFile? WriteFile;
    public CompileTimes CompileTimes = null!;
    public ICommandLineTesting? Testing;
    public SyncMap<PathT, DateTime>? TestingMTimesCache;
    public global::Ts.Tracing.Tracing? Tracing;
}

public static class EmitAndReport
{
    // emit.go:20-28
    public static Action<global::Ts.Diagnostics.Message, object?[]> GetTraceWithWriterFromSys(
        TextWriter w, global::Ts.Locale.Locale locale, ICommandLineTesting? testing)
    {
        if (testing?.GetTrace(w, locale) is { } trace)
        {
            return trace;
        }
        return (msg, args) => w.Write(global::Ts.Diagnostics.Diagnostics.Localize(msg, locale, args) + "\n");
    }

    // emit.go:45-71
    public static (CompileAndEmitResult Result, Statistics? Statistics) EmitAndReportStatistics(EmitInput input)
    {
        Statistics? statistics = null;
        var result = EmitFilesAndReportErrors(input);
        if (result.Status != ExitStatus.Success)
        {
            // compile exited early
            return (result, null);
        }
        result.times!.totalTime = input.Sys.SinceStart();

        if (input.Config.CompilerOptions()!.Diagnostics.IsTrue() || input.Config.CompilerOptions()!.ExtendedDiagnostics.IsTrue())
        {
            // GC must be called twice to allow things to settle.
            GC.Collect();
            GC.Collect();

            statistics = Statistics.FromProgram(input);
            statistics.Report(input.Writer, input.Testing);
        }

        if (result.EmitResult!.EmitSkipped && result.Diagnostics.Length > 0)
        {
            result.Status = ExitStatus.DiagnosticsPresent_OutputsSkipped;
        }
        else if (result.Diagnostics.Length > 0)
        {
            result.Status = ExitStatus.DiagnosticsPresent_OutputsGenerated;
        }
        return (result, statistics);
    }

    // emit.go:73-132
    public static CompileAndEmitResult EmitFilesAndReportErrors(EmitInput input)
    {
        var result = new CompileAndEmitResult { times = input.CompileTimes };
        var ctx = CancellationToken.None;

        var allDiagnosticsArr = global::Ts.Compiler.Program.GetDiagnosticsOfAnyProgram(
            ctx,
            input.ProgramLike,
            null,
            false,
            (c, file) =>
            {
                // Options diagnostics include global diagnostics (even though we collect them separately),
                // and global diagnostics create checkers, which then bind all of the files. Do this binding
                // early so we can track the time.
                var releaseTracing = input.Tracing.Push(Phase.Bind, "bindSourceFiles", null, true);
                try
                {
                    var bindStart = input.Sys.Now();
                    var diags = input.ProgramLike.GetBindDiagnostics(c, file);
                    result.times.bindTime = input.Sys.Now() - bindStart;
                    return diags;
                }
                finally
                {
                    releaseTracing();
                }
            },
            (c, file) =>
            {
                var releaseTracing = input.Tracing.Push(Phase.Check, "checkSourceFiles", null, true);
                try
                {
                    var checkStart = input.Sys.Now();
                    var diags = input.ProgramLike.GetSemanticDiagnostics(c, file);
                    result.times.checkTime = input.Sys.Now() - checkStart;
                    return diags;
                }
                finally
                {
                    releaseTracing();
                }
            });
        var allDiagnostics = new List<global::Ts.Ast.Diagnostic>(allDiagnosticsArr);

        var emitResult = new global::Ts.Compiler.EmitResult { EmitSkipped = true, Diagnostics = [] };
        if (!input.ProgramLike.Options().ListFilesOnly.IsTrue())
        {
            var emitStart = input.Sys.Now();
            emitResult = input.ProgramLike.Emit(ctx, new global::Ts.Compiler.EmitOptions
            {
                WriteFile = input.WriteFile,
            }) ?? emitResult;
            result.times.emitTime = input.Sys.Now() - emitStart;
        }
        if (emitResult.Diagnostics is not null)
        {
            allDiagnostics.AddRange(emitResult.Diagnostics);
        }
        input.Testing?.OnEmittedFiles(emitResult, input.TestingMTimesCache);

        var sorted = global::Ts.Compiler.Program.SortAndDeduplicateDiagnostics(allDiagnostics.ToArray());
        foreach (var diagnostic in sorted)
        {
            input.ReportDiagnostic(diagnostic);
        }

        ListFiles(input, emitResult);

        input.ReportErrorSummary(sorted);
        result.Diagnostics = sorted;
        result.EmitResult = emitResult;
        result.Status = ExitStatus.Success;
        return result;
    }

    // emit.go:134-152
    private static void ListFiles(EmitInput input, global::Ts.Compiler.EmitResult emitResult)
    {
        input.Testing?.OnListFilesStart(input.Writer);
        try
        {
            var options = input.Program.Options();
            if (options.ListEmittedFiles.IsTrue())
            {
                foreach (var file in emitResult.EmittedFiles ?? [])
                {
                    input.Writer.Write("TSFILE: " + global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(file, input.Program.GetCurrentDirectory()) + "\n");
                }
            }
            // PORT-NOTE: ExplainFiles (--explainFiles) is a deferred subsystem (include-reason
            // rendering); the ListFiles/ListFilesOnly arms are real.
            if (options.ListFiles.IsTrue() || options.ListFilesOnly.IsTrue())
            {
                foreach (var file in input.Program.SourceFiles())
                {
                    input.Writer.Write(file.FileName() + "\n");
                }
            }
        }
        finally
        {
            input.Testing?.OnListFilesEnd(input.Writer);
        }
    }
}

// statistics.go:13-19
internal sealed class StatisticsTable
{
    private readonly List<(string Name, string Value)> rows = new();

    // statistics.go:22-27
    internal void Add(string name, object value)
    {
        if (value is TimeSpan d)
        {
            value = FormatDuration(d);
        }
        rows.Add((name, value switch
        {
            string str => str,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        }));
    }

    // statistics.go:29-40
    internal void Print(TextWriter w)
    {
        var nameWidth = 0;
        var valueWidth = 0;
        foreach (var (name, value) in rows)
        {
            nameWidth = Math.Max(nameWidth, name.Length);
            valueWidth = Math.Max(valueWidth, value.Length);
        }
        foreach (var (name, value) in rows)
        {
            w.Write((name + ":").PadRight(nameWidth + 2) + value.PadLeft(valueWidth) + "\n");
        }
    }

    // statistics.go:42-44
    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "s";
}

// statistics.go:54-69
public sealed class Statistics
{
    internal bool isAggregate;
    public int Projects;
    public int ProjectsBuilt;
    public int TimestampUpdates;
    internal int files;
    internal int lines;
    internal int identifiers;
    internal int symbols;
    internal int types;
    internal int instantiations;
    internal ulong memoryUsed;
    internal ulong memoryAllocs;
    internal CompileTimes? compileTimes;

    // statistics.go:71-83 — statisticsFromProgram. (Go reads runtime.MemStats; C# reads the GC's
    // equivalent counters after the double collect in the caller.)
    internal static Statistics FromProgram(EmitInput input)
    {
        return new Statistics
        {
            files = input.Program.SourceFiles().Length,
            lines = input.Program.LineCount(),
            identifiers = input.Program.IdentifierCount(),
            symbols = input.Program.SymbolCount(),
            types = input.Program.TypeCount(),
            instantiations = input.Program.InstantiationCount(),
            memoryUsed = (ulong)GC.GetTotalMemory(false),
            memoryAllocs = (ulong)GC.GetTotalAllocatedBytes(),
            compileTimes = input.CompileTimes,
        };
    }

    // statistics.go:85-125
    public void Report(TextWriter w, ICommandLineTesting? testing)
    {
        testing?.OnStatisticsStart(w);
        try
        {
            var table = new StatisticsTable();
            var prefix = "";

            if (isAggregate)
            {
                prefix = "Aggregate ";
                table.Add("Projects in scope", Projects);
                table.Add("Projects built", ProjectsBuilt);
                table.Add("Timestamps only updates", TimestampUpdates);
            }
            table.Add(prefix + "Files", files);
            table.Add(prefix + "Lines", lines);
            table.Add(prefix + "Identifiers", identifiers);
            table.Add(prefix + "Symbols", symbols);
            table.Add(prefix + "Types", types);
            table.Add(prefix + "Instantiations", instantiations);
            table.Add(prefix + "Memory used", (memoryUsed / 1024) + "K");
            table.Add(prefix + "Memory allocs", memoryAllocs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (compileTimes!.ConfigTime != TimeSpan.Zero)
            {
                table.Add(prefix + "Config time", compileTimes.ConfigTime);
            }
            if (compileTimes.BuildInfoReadTime != TimeSpan.Zero)
            {
                table.Add(prefix + "BuildInfo read time", compileTimes.BuildInfoReadTime);
            }
            table.Add(prefix + "Parse time", compileTimes.ParseTime);
            if (compileTimes.bindTime != TimeSpan.Zero)
            {
                table.Add(prefix + "Bind time", compileTimes.bindTime);
            }
            if (compileTimes.checkTime != TimeSpan.Zero)
            {
                table.Add(prefix + "Check time", compileTimes.checkTime);
            }
            if (compileTimes.emitTime != TimeSpan.Zero)
            {
                table.Add(prefix + "Emit time", compileTimes.emitTime);
            }
            if (compileTimes.ChangesComputeTime != TimeSpan.Zero)
            {
                table.Add(prefix + "Changes compute time", compileTimes.ChangesComputeTime);
            }
            table.Add(prefix + "Total time", compileTimes.totalTime);
            table.Print(w);
        }
        finally
        {
            testing?.OnStatisticsEnd(w);
        }
    }

    // statistics.go:127-148
    public void Aggregate(Statistics stat)
    {
        isAggregate = true;
        compileTimes ??= new CompileTimes();
        // Aggregate statistics
        files += stat.files;
        lines += stat.lines;
        identifiers += stat.identifiers;
        symbols += stat.symbols;
        types += stat.types;
        instantiations += stat.instantiations;
        memoryUsed += stat.memoryUsed;
        memoryAllocs += stat.memoryAllocs;
        compileTimes.ConfigTime += stat.compileTimes!.ConfigTime;
        compileTimes.BuildInfoReadTime += stat.compileTimes.BuildInfoReadTime;
        compileTimes.ParseTime += stat.compileTimes.ParseTime;
        compileTimes.bindTime += stat.compileTimes.bindTime;
        compileTimes.checkTime += stat.compileTimes.checkTime;
        compileTimes.emitTime += stat.compileTimes.emitTime;
        compileTimes.ChangesComputeTime += stat.compileTimes.ChangesComputeTime;
    }

    // statistics.go:150-155
    public void SetTotalTime(TimeSpan totalTime)
    {
        compileTimes ??= new CompileTimes();
        compileTimes.totalTime = totalTime;
    }
}
