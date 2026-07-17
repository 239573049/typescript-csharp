// Ported from tsgo internal/execute/build/orchestrator.go.
//
// PORT-NOTE: the fswatch event arms (Watch/DoCycle/checkTasksForEventChanges/computeDesiredWatches/
// updateWatch) land with the watchmanager package wave; the single-shot --build / --build --dry /
// --build --clean orchestration is complete here.

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

// orchestrator.go:26-30
public sealed class BuildOptionsBag
{
    public ISystem Sys = null!;
    public global::Ts.TsOptions.ParsedBuildCommandLine Command = null!;
    public ICommandLineTesting? Testing;
}

// orchestrator.go:32-37
internal sealed class OrchestratorResult
{
    internal CommandLineResult result = new();
    internal List<global::Ts.Ast.Diagnostic>? errors;
    internal readonly Statistics statistics = new();
    internal List<string>? filesToDelete;

    // orchestrator.go:39-60
    internal void Report(Orchestrator o)
    {
        var errorCount = errors?.Count ?? 0;
        if (o.opts.Command.CompilerOptions!.Watch.IsTrue())
        {
            o.watchStatusReporter!(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                errorCount == 1 ? Diag.Found_1_error_Watching_for_file_changes : Diag.Found_0_errors_Watching_for_file_changes,
                errorCount));
        }
        else
        {
            o.errorSummaryReporter!(errors ?? (IReadOnlyList<global::Ts.Ast.Diagnostic>)[]);
        }
        if (filesToDelete != null)
        {
            o.CreateBuilderStatusReporter(null)(
                global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    Diag.A_non_dry_build_would_delete_the_following_files_Colon_0,
                    string.Concat(filesToDelete.Select(f => "\r\n * " + f))));
        }
        if (!o.opts.Command.CompilerOptions.Diagnostics.IsTrue() && !o.opts.Command.CompilerOptions.ExtendedDiagnostics.IsTrue())
        {
            return;
        }
        statistics.SetTotalTime(o.opts.Sys.SinceStart());
        statistics.Report(o.opts.Sys.Writer(), o.opts.Testing);
    }
}

// orchestrator.go:62-77
public sealed class Orchestrator : IExecuteWatcher
{
    internal BuildOptionsBag opts = null!;
    internal global::Ts.TsPath.ComparePathsOptions comparePathsOptions;
    internal BuildHost host = null!;

    // order generation result
    internal SyncMap<PathT, BuildTask> tasks = new();
    internal List<string> order = new();
    internal List<global::Ts.Ast.Diagnostic> errors = new();

    internal DiagnosticsReporter? errorSummaryReporter;
    internal DiagnosticReporter? watchStatusReporter;

    // orchestrator.go:81-83
    internal string RelativeFileName(string fileName) => TsPathApi.ConvertToRelativePath(fileName, comparePathsOptions);

    // orchestrator.go:85-87
    internal PathT ToPath(string fileName) => TsPathApi.ToPath(fileName, comparePathsOptions.CurrentDirectory, comparePathsOptions.UseCaseSensitiveFileNames);

    // orchestrator.go:89-94
    internal string ResolveBuildInfoFileName(string fileName, string buildInfoDir)
    {
        if (global::Ts.Execute.Incremental.BuildInfoHelpers.IsBuildInfoFileNameDefaultLibrary(fileName))
        {
            return TsPathApi.CombinePaths(host.DefaultLibraryPath(), fileName);
        }
        return TsPathApi.GetNormalizedAbsolutePath(fileName, buildInfoDir);
    }

    // orchestrator.go:96-98
    public string[] Order() => order.ToArray();

    // orchestrator.go:100-106
    public string[] Upstream(string configName)
    {
        var task = GetTask(ToPath(configName));
        return task.upStream.Select(t => t.task.config).ToArray();
    }

    // orchestrator.go:108-114
    public string[] Downstream(string configName)
    {
        var task = GetTask(ToPath(configName));
        return task.downStream.Select(t => t.config).ToArray();
    }

    // orchestrator.go:116-122
    internal BuildTask GetTask(PathT path)
    {
        if (!tasks.TryLoad(path, out var task))
        {
            throw new InvalidOperationException("No build task found for " + path);
        }
        return task!;
    }

    // orchestrator.go:124-155
    internal void CreateBuildTasks(SyncMap<PathT, BuildTask>? oldTasks, string[] configs, IWorkGroup wg)
    {
        foreach (var configName in configs)
        {
            var config = configName;
            wg.Queue(() =>
            {
                var path = ToPath(config);
                BuildTask? task = null;
                BuildInfoEntry? buildInfo = null;
                if (oldTasks != null)
                {
                    if (oldTasks.TryLoad(path, out var existing))
                    {
                        if (!existing!.dirty)
                        {
                            // Reuse existing task if config is same
                            task = existing;
                        }
                        else
                        {
                            buildInfo = existing.buildInfoEntry;
                        }
                    }
                }
                if (task == null)
                {
                    task = new BuildTask { config = config, isInitialCycle = oldTasks == null };
                    task.Pending = true;
                    task.buildInfoEntry = buildInfo;
                }
                var (_, loaded) = tasks.LoadOrStore(path, task);
                if (loaded)
                {
                    return;
                }
                task.resolved = host.GetResolvedProjectReference(config, path);
                task.upStream = new List<UpstreamTask>();
                if (task.resolved != null)
                {
                    CreateBuildTasks(oldTasks, task.resolved.ResolvedProjectReferencePaths() ?? [], wg);
                }
            });
        }
    }

    // orchestrator.go:157-201
    internal BuildTask? SetupBuildTask(
        string configName,
        BuildTask? downStream,
        bool inCircularContext,
        Set<PathT> completed,
        Set<PathT> analyzing,
        List<string> circularityStack)
    {
        var path = ToPath(configName);
        var task = GetTask(path);
        if (!completed.Has(path))
        {
            if (analyzing.Has(path))
            {
                if (!inCircularContext)
                {
                    errors.Add(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                        Diag.Project_references_may_not_form_a_circular_graph_Cycle_detected_Colon_0,
                        string.Join("\n", circularityStack)));
                }
                return null;
            }
            analyzing.Add(path);
            circularityStack.Add(configName);
            if (task.resolved != null)
            {
                var index = 0;
                foreach (var subReference in task.resolved.ResolvedProjectReferencePaths() ?? [])
                {
                    var upstream = SetupBuildTask(subReference, task, inCircularContext || task.resolved.ProjectReferences()![index].Circular, completed, analyzing, circularityStack);
                    if (upstream != null)
                    {
                        task.upStream.Add(new UpstreamTask { task = upstream, refIndex = index });
                    }
                    index++;
                }
            }
            circularityStack.RemoveAt(circularityStack.Count - 1);
            completed.Add(path);
            task.reportDone = new ManualResetEventSlim(false);
            var prev = order.Count > 0 ? order[^1] : "";
            if (prev.Length > 0)
            {
                task.prevReporter = GetTask(ToPath(prev));
            }
            task.done = new ManualResetEventSlim(false);
            order.Add(configName);
        }
        if (opts.Command.CompilerOptions!.Watch.IsTrue() && downStream != null)
        {
            task.downStream.Add(downStream);
        }
        return task;
    }

    // orchestrator.go:203-209
    public void GenerateGraphReusingOldTasks()
    {
        var oldTasks = tasks;
        tasks = new SyncMap<PathT, BuildTask>();
        order = new List<string>();
        errors = new List<global::Ts.Ast.Diagnostic>();
        GenerateGraph(oldTasks);
    }

    // orchestrator.go:211-225
    internal void GenerateGraph(SyncMap<PathT, BuildTask>? oldTasks)
    {
        var projects = opts.Command.ResolvedProjectPaths() ?? [];
        // Parse all config files in parallel
        var wg = WorkGroup.Create(opts.Command.CompilerOptions!.SingleThreaded.IsTrue());
        CreateBuildTasks(oldTasks, projects, wg);
        wg.RunAndWait();

        // Generate the graph
        var completed = new Set<PathT>();
        var analyzing = new Set<PathT>();
        var circularityStack = new List<string>();
        foreach (var project in projects)
        {
            SetupBuildTask(project, null, false, completed, analyzing, circularityStack);
        }
    }

    // orchestrator.go:227-238
    public CommandLineResult Start()
    {
        if (opts.Command.CompilerOptions!.Watch.IsTrue())
        {
            watchStatusReporter!(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Starting_compilation_in_watch_mode));
        }
        GenerateGraph(null);
        var result = BuildOrClean();
        if (opts.Command.CompilerOptions.Watch.IsTrue())
        {
            // PORT-NOTE: the fswatch run loop (orchestrator.go:240-263 Watch) lands with the
            // watchmanager wave.
            result.Watcher = this;
        }
        return result;
    }

    // orchestrator.go:591-617
    internal CommandLineResult BuildOrClean()
    {
        if (!opts.Command.BuildOptions!.Clean.IsTrue() && opts.Command.BuildOptions.Verbose.IsTrue())
        {
            CreateBuilderStatusReporter(null)(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                Diag.Projects_in_this_build_Colon_0,
                string.Concat(order.Select(p => "\r\n    * " + RelativeFileName(p)))));
        }
        var buildResult = new OrchestratorResult();
        if (errors.Count == 0)
        {
            buildResult.statistics.Projects = order.Count;
            RangeTask((path, task) => BuildOrCleanProject(task, path, buildResult));
        }
        else
        {
            // Circularity errors prevent any project from being built
            buildResult.result.Status = ExitStatus.ProjectReferenceCycle_OutputsSkipped;
            var reportDiagnostic = CreateDiagnosticReporter(null);
            foreach (var err in errors)
            {
                reportDiagnostic(err);
            }
            buildResult.errors = new List<global::Ts.Ast.Diagnostic>(errors);
        }
        buildResult.Report(this);
        return buildResult.result;
    }

    // orchestrator.go:619-653
    internal void RangeTask(Action<PathT, BuildTask> f)
    {
        var numRoutines = 4;
        if (opts.Command.CompilerOptions!.SingleThreaded.IsTrue())
        {
            numRoutines = 1;
        }
        else if (opts.Command.BuildOptions!.Builders is { } builders)
        {
            numRoutines = builders;
        }

        var currentTaskIndex = -1;
        (PathT Path, BuildTask? Task, bool Ok) GetNextTask()
        {
            var index = Interlocked.Increment(ref currentTaskIndex);
            if (index >= order.Count)
            {
                return (default, null, false);
            }
            var config = order[index];
            var path = ToPath(config);
            var task = GetTask(path);
            return (path, task, true);
        }
        void RunTask()
        {
            for (var (path, task, ok) = GetNextTask(); ok; (path, task, ok) = GetNextTask())
            {
                f(path, task!);
            }
        }

        if (numRoutines == 1)
        {
            RunTask();
        }
        else
        {
            var wg = WorkGroup.Create(false);
            for (var i = 0; i < numRoutines; i++)
            {
                wg.Queue(RunTask);
            }
            wg.RunAndWait();
        }
    }

    // orchestrator.go:655-665
    internal void BuildOrCleanProject(BuildTask task, PathT path, OrchestratorResult buildResult)
    {
        task.result = new TaskResult();
        task.result.reportStatus = CreateBuilderStatusReporter(task);
        task.result.diagnosticReporter = CreateDiagnosticReporter(task);
        if (!opts.Command.BuildOptions!.Clean.IsTrue())
        {
            task.BuildProject(this, path);
        }
        else
        {
            task.CleanProject(this, path);
        }
        task.Report(this, path, buildResult);
    }

    // orchestrator.go:667-672
    internal TextWriter GetWriter(BuildTask? task)
    {
        if (task == null)
        {
            return opts.Sys.Writer();
        }
        return new StringWriter(task.result!.builder);
    }

    // orchestrator.go:674-676
    internal DiagnosticReporter CreateBuilderStatusReporter(BuildTask? task) =>
        DiagnosticReporters.CreateBuilderStatusReporter(opts.Sys, GetWriter(task), global::Ts.Locale.Locale.Default, opts.Command.CompilerOptions!, opts.Testing);

    // orchestrator.go:678-680
    internal DiagnosticReporter CreateDiagnosticReporter(BuildTask? task) =>
        DiagnosticReporters.CreateDiagnosticReporter(opts.Sys, GetWriter(task), global::Ts.Locale.Locale.Default, opts.Command.CompilerOptions!);

    // orchestrator.go:682-713
    public static Orchestrator NewOrchestrator(BuildOptionsBag opts)
    {
        var orchestrator = new Orchestrator
        {
            opts = opts,
            comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = opts.Sys.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = opts.Sys.FS().UseCaseSensitiveFileNames(),
            },
        };
        orchestrator.host = new BuildHost
        {
            orchestrator = orchestrator,
            host = global::Ts.Compiler.CompilerHosts.NewCachedFSCompilerHost(
                orchestrator.opts.Sys.GetCurrentDirectory(),
                orchestrator.opts.Sys.FS(),
                orchestrator.opts.Sys.DefaultLibraryPath(),
                null,
                null),
        };
        if (opts.Command.CompilerOptions!.Watch.IsTrue())
        {
            orchestrator.watchStatusReporter = DiagnosticReporters.CreateWatchStatusReporter(opts.Sys, global::Ts.Locale.Locale.Default, opts.Command.CompilerOptions, opts.Testing);
        }
        else
        {
            orchestrator.errorSummaryReporter = DiagnosticReporters.CreateReportErrorSummary(opts.Sys, global::Ts.Locale.Locale.Default, opts.Command.CompilerOptions);
        }
        return orchestrator;
    }

    // IExecuteWatcher — watch-mode surface.
    // PORT-NOTE: orchestrator.go:538-589 DoCycle (fswatch event drain + graph refresh) lands with
    // the watchmanager wave; until then a cycle is a no-op.
    public void DoCycle()
    {
    }

    public void Dispose()
    {
    }
}
