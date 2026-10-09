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

    // orchestrator.go:75-76 — fswatch event-based watching
    internal global::Ts.Execute.WatchManagement.WatchManager wm = null!;

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

    // orchestrator.go:227-238 — Go: Start(ctx context.Context); the ctx becomes the
    // CancellationToken threaded in from Tsc.CommandLine (tsc.go:118).
    public CommandLineResult Start(CancellationToken ctx)
    {
        if (opts.Command.CompilerOptions!.Watch.IsTrue())
        {
            watchStatusReporter!(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Starting_compilation_in_watch_mode));
        }
        GenerateGraph(null);
        var result = BuildOrClean();
        if (opts.Command.CompilerOptions.Watch.IsTrue())
        {
            Watch(ctx); // orchestrator.go:234
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
        DiagnosticReporters.CreateBuilderStatusReporter(opts.Sys, GetWriter(task), opts.Command.Locale(), opts.Command.CompilerOptions!, opts.Testing);

    // orchestrator.go:678-680
    internal DiagnosticReporter CreateDiagnosticReporter(BuildTask? task) =>
        DiagnosticReporters.CreateDiagnosticReporter(opts.Sys, GetWriter(task), opts.Command.Locale(), opts.Command.CompilerOptions!);

    // orchestrator.go:682-713
    public static Orchestrator NewOrchestrator(BuildOptionsBag opts)
    {
        var wm = new global::Ts.Execute.WatchManagement.WatchManager(opts.Sys.Writer(), dir => opts.Sys.FS().DirectoryExists(dir));
        var orchestrator = new Orchestrator
        {
            opts = opts,
            comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = opts.Sys.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = opts.Sys.FS().UseCaseSensitiveFileNames(),
            },
            wm = wm,
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
            orchestrator.watchStatusReporter = DiagnosticReporters.CreateWatchStatusReporter(opts.Sys, opts.Command.Locale(), opts.Command.CompilerOptions, opts.Testing);
            if (opts.Testing is global::Ts.Execute.WatchManagement.ICommandLineTestingWithWatchBackend t)
            {
                wm.SetBackend(t.WatchBackend());
            }
        }
        else
        {
            orchestrator.errorSummaryReporter = DiagnosticReporters.CreateReportErrorSummary(opts.Sys, opts.Command.Locale(), opts.Command.CompilerOptions);
        }
        return orchestrator;
    }

    // orchestrator.go:240-263
    public void Watch(CancellationToken ctx)
    {
        wm.Lock();

        if (opts.Testing == null)
        {
            if (opts.Sys.GetEnvironmentVariable("TS_WATCH_DEBUG").Length > 0)
            {
                wm.DebugLog = opts.Sys.Writer();
            }
            wm.EnsureDefaultBackend();
        }

        UpdateWatch();
        var desiredDirs = ComputeDesiredWatches();
        try
        {
            wm.ReconcileWatches(desiredDirs);
        }
        catch (Exception e)
        {
            opts.Sys.Writer().Write(e.Message + "\n");
            wm.ForceOverflow();
        }
        ResetCaches();

        wm.Unlock();

        if (opts.Testing == null)
        {
            wm.RunLoop(ctx, DoCycle);
        }
    }

    // orchestrator.go:265-271
    internal void UpdateWatch()
    {
        var oldCache = host.mTimes;
        host.mTimes = new SyncMap<PathT, DateTime>();
        RangeTask((path, task) => task.UpdateWatch(this, oldCache));
    }

    // orchestrator.go:273-280
    internal void ResetCaches()
    {
        // Clean out all the caches
        var cachesVfs = (global::Ts.Vfs.CachedVfs.CachedFs)host.host.FS();
        cachesVfs.ClearCache();
        host.extendedConfigCache = new TscExtendedConfigCache();
        host.sourceFiles.Reset();
        host.configTimes = new SyncMap<PathT, TimeSpan>();
    }

    // Go: atomic.Bool locals threaded by pointer (orchestrator.go:282).
    internal sealed class Flag
    {
        private int value;

        internal bool Load() => Volatile.Read(ref value) != 0;

        internal void Store(bool v) => Volatile.Write(ref value, v ? 1 : 0);
    }

    // orchestrator.go:282-404
    internal void CheckTasksForEventChanges(Dictionary<string, global::Ts.FsWatch.EventKind> changedPaths, Flag needsConfigUpdate, Flag needsUpdate)
    {
        var normalizedPaths = new Dictionary<PathT, global::Ts.FsWatch.EventKind>(changedPaths.Count);
        foreach (var (eventPath, kind) in changedPaths) // map-iter: 序无关 (normalization)
        {
            normalizedPaths[ToPath(eventPath)] = kind;
        }

        for (var i = 0; i < order.Count; i++)
        {
            var config = order[i];
            var path = ToPath(config);
            var task = GetTask(path);

            var configPath = ToPath(task.config);
            if (normalizedPaths.ContainsKey(configPath))
            {
                task.ResetConfig(this, path);
                needsConfigUpdate.Store(true);
                needsUpdate.Store(true);
                continue;
            }

            if (task.resolved == null)
            {
                continue;
            }

            var configChanged = false;
            foreach (var file in task.resolved.ExtendedSourceFiles() ?? [])
            {
                var fp = ToPath(file);
                if (normalizedPaths.ContainsKey(fp))
                {
                    task.ResetConfig(this, path);
                    needsConfigUpdate.Store(true);
                    needsUpdate.Store(true);
                    configChanged = true;
                    break;
                }
            }
            if (configChanged)
            {
                continue;
            }

            var rootChanged = false;
            var fileNames = task.resolved.FileNames() ?? [];
            var roots = new Set<PathT>(fileNames.Length);
            foreach (var file in fileNames)
            {
                var fp = ToPath(file);
                roots.Add(fp);
                if (!rootChanged)
                {
                    if (normalizedPaths.ContainsKey(fp))
                    {
                        task.ResetStatus();
                        needsUpdate.Store(true);
                        rootChanged = true;
                    }
                }
            }

            if (!rootChanged)
            {
                BuildInfoEntry? bi;
                lock (task.buildInfoEntryMu)
                {
                    bi = task.buildInfoEntry;
                }
                if (bi is { buildInfo: not null })
                {
                    var buildInfoDir = TsPathApi.GetDirectoryPath(bi.path.ToString());
                    foreach (var fileName in bi.buildInfo.FileNames ?? [])
                    {
                        var fp = ToPath(ResolveBuildInfoFileName(fileName, buildInfoDir));
                        if (roots.Has(fp))
                        {
                            continue;
                        }
                        if (normalizedPaths.ContainsKey(fp))
                        {
                            task.ResetStatus();
                            needsUpdate.Store(true);
                            break;
                        }
                    }
                    foreach (var packageJson in bi.buildInfo.GetPackageJsons(buildInfoDir))
                    {
                        if (PackageJsonLookupChanged(packageJson, normalizedPaths))
                        {
                            task.ResetStatus();
                            needsUpdate.Store(true);
                            break;
                        }
                    }
                    foreach (var packageJson in bi.buildInfo.GetMissingPackageJsons(buildInfoDir))
                    {
                        if (PackageJsonLookupChanged(packageJson, normalizedPaths))
                        {
                            task.ResetStatus();
                            needsUpdate.Store(true);
                            break;
                        }
                    }
                }
                foreach (var packageJson in task.packageJsons)
                {
                    if (PackageJsonLookupChanged(packageJson, normalizedPaths))
                    {
                        task.ResetStatus();
                        needsUpdate.Store(true);
                        break;
                    }
                }
            }

            task.reportDone = new ManualResetEventSlim(false);
            task.done = new ManualResetEventSlim(false);

            var newConfig = task.resolved.ReloadFileNamesOfParsedCommandLine(host.FS());
            if (!(task.resolved.FileNames() ?? []).AsSpan().SequenceEqual(newConfig.FileNames() ?? []))
            {
                host.resolvedReferences.Store(path, newConfig);
                task.resolved = newConfig;
                task.ResetStatus();
                needsUpdate.Store(true);
            }
        }

        if (!needsUpdate.Load())
        {
            var cmpOpts = comparePathsOptions;
            foreach (var eventPath in changedPaths.Keys) // map-iter: 序无关 (first hit resets all)
            {
                if (host.FS().DirectoryExists(eventPath))
                {
                    if (wm.IsPathUnderWatch(eventPath, cmpOpts))
                    {
                        RangeTask((path, task) =>
                        {
                            task.ResetStatus();
                            task.reportDone = new ManualResetEventSlim(false);
                            task.done = new ManualResetEventSlim(false);
                        });
                        needsUpdate.Store(true);
                        break;
                    }
                }
            }
        }
    }

    // orchestrator.go:406-417
    internal bool PackageJsonLookupChanged(string packageJson, Dictionary<PathT, global::Ts.FsWatch.EventKind> changedPaths)
    {
        var packageJsonPath = ToPath(packageJson);
        if (changedPaths.ContainsKey(packageJsonPath))
        {
            return true;
        }
        foreach (var (changedPath, kind) in changedPaths) // map-iter: 序无关 (existence check)
        {
            if (kind == global::Ts.FsWatch.EventKind.EventDelete && TsPathApi.ContainsPath(changedPath.ToString(), packageJsonPath.ToString(), comparePathsOptions))
            {
                return true;
            }
        }
        return false;
    }

    // orchestrator.go:419-501
    internal Dictionary<string, bool> ComputeDesiredWatches()
    {
        var desiredDirs = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (var i = 0; i < order.Count; i++)
        {
            var config = order[i];
            var path = ToPath(config);
            var task = GetTask(path);

            // Watch config file directory
            var configDir = TsPathApi.GetDirectoryPath(task.config);
            var realConfigDir = host.FS().Realpath(configDir);
            desiredDirs.TryAdd(realConfigDir, false);

            if (task.resolved == null)
            {
                continue;
            }

            // Extended config file directories
            foreach (var cfgPath in task.resolved.ExtendedSourceFiles() ?? [])
            {
                var realPath = host.FS().Realpath(cfgPath);
                var dir = TsPathApi.GetDirectoryPath(realPath);
                desiredDirs.TryAdd(dir, false);
            }

            // Wildcard directories from tsconfig
            foreach (var (dir, recursive) in task.resolved.WildcardDirectories() ?? new Dictionary<string, bool>()) // map-iter: 序无关 (|| merge)
            {
                var realDir = host.FS().Realpath(dir);
                if (desiredDirs.TryGetValue(realDir, out var existing))
                {
                    desiredDirs[realDir] = existing || recursive;
                }
                else
                {
                    desiredDirs[realDir] = recursive;
                }
            }

            // Input file directories not already covered
            foreach (var fileName in task.resolved.FileNames() ?? [])
            {
                var absPath = TsPathApi.GetNormalizedAbsolutePath(fileName, opts.Sys.GetCurrentDirectory());
                var dir = TsPathApi.GetDirectoryPath(absPath);
                if (!global::Ts.Execute.WatchManagement.WatchManagerUtil.IsDirCoveredByWatch(desiredDirs, dir, comparePathsOptions))
                {
                    if (global::Ts.Execute.WatchManagement.WatchPathUtil.CanWatchDirectory(dir))
                    {
                        desiredDirs[dir] = false;
                    }
                }
            }

            // Non-root dependency directories from buildinfo (e.g. node_modules .d.ts files).
            BuildInfoEntry? bi;
            lock (task.buildInfoEntryMu)
            {
                bi = task.buildInfoEntry;
            }
            if (bi is { buildInfo: not null })
            {
                var buildInfoDir = TsPathApi.GetDirectoryPath(bi.path.ToString());
                var roots = new Set<PathT>();
                foreach (var fileName in task.resolved.FileNames() ?? [])
                {
                    roots.Add(ToPath(fileName));
                }
                foreach (var fileName in bi.buildInfo.FileNames ?? [])
                {
                    var absPath = host.FS().Realpath(ResolveBuildInfoFileName(fileName, buildInfoDir));
                    var fp = ToPath(absPath);
                    if (roots.Has(fp))
                    {
                        continue;
                    }
                    var dir = TsPathApi.GetDirectoryPath(absPath);
                    if (!global::Ts.Execute.WatchManagement.WatchManagerUtil.IsDirCoveredByWatch(desiredDirs, dir, comparePathsOptions))
                    {
                        if (global::Ts.Execute.WatchManagement.WatchPathUtil.CanWatchDirectory(dir))
                        {
                            desiredDirs[dir] = false;
                        }
                    }
                }
                foreach (var packageJson in bi.buildInfo.GetPackageJsons(buildInfoDir))
                {
                    AddPackageJsonWatchDirs(desiredDirs, packageJson);
                }
                foreach (var packageJson in bi.buildInfo.GetMissingPackageJsons(buildInfoDir))
                {
                    AddPackageJsonWatchDirs(desiredDirs, packageJson);
                }
            }
            foreach (var packageJson in task.packageJsons)
            {
                AddPackageJsonWatchDirs(desiredDirs, packageJson);
            }
        }

        return wm.ResolveDesiredDirs(desiredDirs);
    }

    // orchestrator.go:503-507
    internal void AddWatchDir(Dictionary<string, bool> desiredDirs, string dir)
    {
        if (!global::Ts.Execute.WatchManagement.WatchManagerUtil.IsDirCoveredByWatch(desiredDirs, dir, comparePathsOptions) && global::Ts.Execute.WatchManagement.WatchPathUtil.CanWatchDirectory(dir))
        {
            desiredDirs[dir] = false;
        }
    }

    // orchestrator.go:509-536
    internal void AddPackageJsonWatchDirs(Dictionary<string, bool> desiredDirs, string packageJson)
    {
        var dir = TsPathApi.GetDirectoryPath(packageJson);
        var dirs = new List<string> { dir };
        var foundNodeModules = false;
        for (var current = dir; ;)
        {
            var parent = TsPathApi.GetDirectoryPath(current);
            if (parent.Length == 0 || parent == current)
            {
                break;
            }
            dirs.Add(parent);
            if (TsPathApi.GetBaseFileName(parent) == "node_modules")
            {
                foundNodeModules = true;
                var grandparent = TsPathApi.GetDirectoryPath(parent);
                if (grandparent.Length > 0 && grandparent != parent)
                {
                    dirs.Add(grandparent);
                }
                break;
            }
            current = parent;
        }

        if (!foundNodeModules)
        {
            AddWatchDir(desiredDirs, dir);
            return;
        }
        foreach (var d in dirs)
        {
            AddWatchDir(desiredDirs, d);
        }
    }

    // orchestrator.go:538-589
    public void DoCycle()
    {
        wm.Lock();
        try
        {
            var (changedPaths, overflow) = wm.DrainEvents();
            var hasEvents = (changedPaths?.Count ?? 0) > 0 || overflow;

            if (!hasEvents)
            {
                wm.DebugLog?.Write("[watch] DoCycle: no events, skipping\n");
                return;
            }

            var needsConfigUpdate = new Flag();
            var needsUpdate = new Flag();

            if (overflow)
            {
                // Overflow: reset all tasks to force a full rebuild.
                RangeTask((path, task) =>
                {
                    task.ResetConfig(this, path);
                    task.reportDone = new ManualResetEventSlim(false);
                    task.done = new ManualResetEventSlim(false);
                });
                needsConfigUpdate.Store(true);
                needsUpdate.Store(true);
            }
            else
            {
                // Event-driven: check only tasks affected by changed paths
                CheckTasksForEventChanges(changedPaths!, needsConfigUpdate, needsUpdate);
            }

            if (!needsUpdate.Load())
            {
                ResetCaches();
                return;
            }

            watchStatusReporter!(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.File_change_detected_Starting_incremental_compilation));
            if (needsConfigUpdate.Load())
            {
                // Generate new tasks
                GenerateGraphReusingOldTasks();
            }

            BuildOrClean();
            UpdateWatch();
            var desiredDirs = ComputeDesiredWatches();
            try
            {
                wm.ReconcileWatches(desiredDirs);
            }
            catch (Exception e)
            {
                opts.Sys.Writer().Write(e.Message + "\n");
                // Mark overflow so the next event triggers a full rebuild
                wm.ForceOverflow();
            }
            ResetCaches();
        }
        finally
        {
            wm.Unlock();
        }
    }

    public void Dispose()
    {
        wm.CloseAllWatches();
    }
}
