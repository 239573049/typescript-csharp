// Ported from the project-graph and build-order tail of tsgo
// internal/execute/build/orchestrator.go and build/buildtask.go.

using System;
using System.Collections.Generic;

namespace Ts.Execute;

internal sealed class BuildOrchestrator
{
    private sealed class BuildProjectTask
    {
        internal readonly string ConfigFileName;
        internal global::Ts.TsOptions.ParsedCommandLine? Resolved;
        internal global::Ts.Ast.Diagnostic[] LoadErrors = Array.Empty<global::Ts.Ast.Diagnostic>();
        internal BuildProjectTask?[] Upstream = Array.Empty<BuildProjectTask?>();

        internal BuildProjectTask(string configFileName)
        {
            ConfigFileName = configFileName;
        }
    }

    private readonly ISystem sys;
    private readonly global::Ts.TsOptions.ParsedBuildCommandLine command;
    private readonly ICommandLineTesting? testing;
    private readonly StringComparer pathComparer;
    private readonly Dictionary<string, BuildProjectTask> tasks;
    private readonly List<string> order = new();
    private readonly List<global::Ts.Ast.Diagnostic> graphErrors = new();
    private readonly global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw;

    internal BuildOrchestrator(
        ISystem sys,
        global::Ts.TsOptions.ParsedBuildCommandLine command,
        ICommandLineTesting? testing)
    {
        this.sys = sys;
        this.command = command;
        this.testing = testing;
        pathComparer = sys.FS().UseCaseSensitiveFileNames() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        tasks = new Dictionary<string, BuildProjectTask>(pathComparer);

        if (command.Raw is global::Ts.Collections.OrderedMap<string, object?> raw)
        {
            commandLineRaw = new global::Ts.Collections.OrderedMap<string, object?>();
            commandLineRaw.Set("compilerOptions", raw);
        }
    }

    internal CommandLineResult Start()
    {
        GenerateGraph();
        if (graphErrors.Count > 0)
        {
            var reporter = DiagnosticReporters.CreateDiagnosticReporter(
                sys,
                sys.Writer(),
                global::Ts.Locale.Locale.Default,
                command.CompilerOptions!);
            foreach (var error in graphErrors)
            {
                reporter(error);
            }
            return new CommandLineResult { Status = ExitStatus.ProjectReferenceCycle_OutputsSkipped };
        }

        var allDiagnostics = new List<global::Ts.Ast.Diagnostic>();
        ExitStatus status = ExitStatus.Success;
        var reportDiagnostic = DiagnosticReporters.CreateDiagnosticReporter(
            sys,
            sys.Writer(),
            global::Ts.Locale.Locale.Default,
            command.CompilerOptions!);
        var reportBuildStatus = DiagnosticReporters.CreateBuilderStatusReporter(
            sys,
            sys.Writer(),
            global::Ts.Locale.Locale.Default,
            command.CompilerOptions!,
            testing);

        // PORT-NOTE: Go's rangeTask runs dependency-gated tasks with up to four workers and then
        // serializes their buffered reports in graph order. This skeleton executes the same
        // dependency-first order synchronously; parallel builders and buffered reporter handoff
        // are deferred to the full execute/build wave.
        // PORT-NOTE: Go also gives --clean/--dry/--stopBuildOnErrors their own task-state paths.
        // This requested orchestration skeleton is the ordinary build path and deliberately tries
        // every project in the closure; those build-option state machines remain in the full wave.
        foreach (string configFileName in order)
        {
            BuildProjectTask task = tasks[CanonicalPath(configFileName)];
            if (task.Resolved is null)
            {
                foreach (var error in task.LoadErrors)
                {
                    reportDiagnostic(error);
                    allDiagnostics.Add(error);
                }
                status = MaxStatus(status, ExitStatus.InvalidProject_OutputsSkipped);
                continue;
            }

            if (command.BuildOptions!.Verbose == global::Ts.Core.Tristate.True)
            {
                string relative = global::Ts.TsPath.TsPath.ConvertToRelativePath(
                    configFileName,
                    new global::Ts.TsPath.ComparePathsOptions
                    {
                        CurrentDirectory = sys.GetCurrentDirectory(),
                        UseCaseSensitiveFileNames = sys.FS().UseCaseSensitiveFileNames(),
                    });
                reportBuildStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                    global::Ts.Diagnostics.Diagnostics.Building_project_0,
                    relative));
            }

            // PORT-NOTE: Go calls BuildTask.getUpToDateStatus here, reads .tsbuildinfo, compares
            // input/output mtimes and signatures, and may skip or pseudo-build an up-to-date
            // project. .tsbuildinfo staleness checking is explicitly deferred to a later wave;
            // this pass always rebuilds every project in the dependency-first order.
            // PORT-NOTE: the landed Compiler.ProjectReferenceParser.Parse currently throws its
            // explicit phase-4/5 NotImplementedException whenever Program sees project
            // references. The execute orchestrator has already consumed those references to
            // build and order the complete graph, so this skeleton compiles an otherwise-identical
            // ParsedCommandLine view with references omitted. Remove this compatibility view when
            // the Compiler project-reference loader lands; no graph edge is discarded by the
            // orchestrator itself.
            global::Ts.TsOptions.ParsedCommandLine standaloneConfig = CreateStandaloneConfig(task.Resolved);
            CompilationOutcome outcome = Tsc.PerformCompilationOutcome(
                sys,
                standaloneConfig,
                reportDiagnostic,
                DiagnosticReporters.QuietDiagnosticsReporter,
                testing);
            allDiagnostics.AddRange(outcome.Diagnostics);
            status = MaxStatus(status, outcome.Status);
        }

        DiagnosticReporters.CreateReportErrorSummary(
            sys,
            global::Ts.Locale.Locale.Default,
            command.CompilerOptions!)(allDiagnostics);

        // PORT-NOTE: Go's build orchestrator can remain live for `--build --watch`, reconcile the
        // whole graph's watches, regenerate dirty tasks, and incrementally rebuild downstream
        // projects. This pass lands the requested non-watch --build orchestration skeleton only;
        // combined build-watch graph maintenance is deferred to the full build/watch wave.
        return new CommandLineResult { Status = status };
    }

    private void GenerateGraph()
    {
        string[] projects = command.ResolvedProjectPaths() ?? Array.Empty<string>();
        foreach (string project in projects)
        {
            LoadTask(project);
        }

        var completed = new HashSet<string>(pathComparer);
        var analyzing = new HashSet<string>(pathComparer);
        var circularityStack = new List<string>();
        foreach (string project in projects)
        {
            _ = SetupBuildTask(project, inCircularContext: false, completed, analyzing, circularityStack);
        }
    }

    private BuildProjectTask LoadTask(string configFileName)
    {
        configFileName = NormalizeAbsolutePath(configFileName);
        string path = CanonicalPath(configFileName);
        if (tasks.TryGetValue(path, out BuildProjectTask? existing))
        {
            return existing;
        }

        // Store the placeholder before following references so a reference cycle cannot recurse
        // forever during config loading (matching createBuildTasks.LoadOrStore in Go).
        var task = new BuildProjectTask(configFileName);
        tasks.Add(path, task);
        var (resolved, errors) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFile(
            configFileName,
            command.CompilerOptions,
            commandLineRaw,
            sys,
            extendedConfigCache: null);
        task.Resolved = resolved;
        task.LoadErrors = errors ?? Array.Empty<global::Ts.Ast.Diagnostic>();
        if (resolved is not null)
        {
            string[] references = resolved.ResolvedProjectReferencePaths() ?? Array.Empty<string>();
            task.Upstream = new BuildProjectTask?[references.Length];
            for (int i = 0; i < references.Length; i++)
            {
                task.Upstream[i] = LoadTask(references[i]);
            }
        }
        return task;
    }

    private BuildProjectTask? SetupBuildTask(
        string configFileName,
        bool inCircularContext,
        HashSet<string> completed,
        HashSet<string> analyzing,
        List<string> circularityStack)
    {
        configFileName = NormalizeAbsolutePath(configFileName);
        string path = CanonicalPath(configFileName);
        BuildProjectTask task = tasks[path];
        if (!completed.Contains(path))
        {
            if (analyzing.Contains(path))
            {
                if (!inCircularContext)
                {
                    graphErrors.Add(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                        global::Ts.Diagnostics.Diagnostics.Project_references_may_not_form_a_circular_graph_Cycle_detected_Colon_0,
                        string.Join("\n", circularityStack)));
                }
                return null;
            }

            analyzing.Add(path);
            circularityStack.Add(configFileName);
            if (task.Resolved is not null)
            {
                string[] references = task.Resolved.ResolvedProjectReferencePaths() ?? Array.Empty<string>();
                global::Ts.Core.ProjectReference[] projectReferences =
                    task.Resolved.ProjectReferences() ?? Array.Empty<global::Ts.Core.ProjectReference>();
                for (int i = 0; i < references.Length; i++)
                {
                    bool circular = i < projectReferences.Length && projectReferences[i].Circular;
                    BuildProjectTask? upstream = SetupBuildTask(
                        references[i],
                        inCircularContext || circular,
                        completed,
                        analyzing,
                        circularityStack);
                    task.Upstream[i] = upstream;
                }
            }
            circularityStack.RemoveAt(circularityStack.Count - 1);
            analyzing.Remove(path);
            completed.Add(path);
            order.Add(configFileName);
        }
        return task;
    }

    private string NormalizeAbsolutePath(string path) =>
        global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(path, sys.GetCurrentDirectory());

    private static global::Ts.TsOptions.ParsedCommandLine CreateStandaloneConfig(
        global::Ts.TsOptions.ParsedCommandLine config)
    {
        var result = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            config.CompilerOptions()!,
            config.FileNames() ?? Array.Empty<string>(),
            new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = config.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = config.UseCaseSensitiveFileNames(),
            });
        result.ParsedConfig.WatchOptions = config.ParsedConfig.WatchOptions;
        result.ParsedConfig.TypeAcquisition = config.ParsedConfig.TypeAcquisition;
        result.ConfigFile = config.ConfigFile;
        result.Errors = config.Errors;
        result.Raw = config.Raw;
        result.CompileOnSave = config.CompileOnSave;
        return result;
    }

    private string CanonicalPath(string path) =>
        global::Ts.TsPath.TsPath.GetCanonicalFileName(NormalizeAbsolutePath(path), sys.FS().UseCaseSensitiveFileNames());

    private static ExitStatus MaxStatus(ExitStatus left, ExitStatus right) => left > right ? left : right;
}
