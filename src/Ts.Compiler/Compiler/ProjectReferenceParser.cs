// Ported from tsgo internal/compiler/projectreferenceparser.go.

using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;

namespace Ts.Compiler;

// projectreferenceparser.go:13-17
internal sealed class ProjectReferenceParseTask
{
    internal string ConfigName = "";
    internal global::Ts.TsOptions.ParsedCommandLine? Resolved;
    internal List<ProjectReferenceParseTask>? SubTasks;

    // projectreferenceparser.go:19-32
    internal void Parse(ProjectReferenceParser projectReferenceParser)
    {
        var loader = projectReferenceParser.Loader;
        var releaseTracing = global::Ts.Tracing.TracingExtensions.Push(
            loader.Opts.Tracing, global::Ts.Tracing.Phase.Parse, "parseJsonSourceFileConfigFileContent",
            new Dictionary<string, object?> { ["path"] = ConfigName }, false);
        try
        {
            Resolved = loader.Opts.Host.GetResolvedProjectReference(ConfigName, loader.ToPath(ConfigName));
            if (Resolved == null)
            {
                return;
            }
            Resolved.ParseInputOutputNames();
            if (Resolved.ResolvedProjectReferencePaths() is { Length: > 0 } subReferences)
            {
                SubTasks = ProjectReferenceParseTasks.Create(subReferences);
            }
        }
        finally
        {
            releaseTracing();
        }
    }
}

// projectreferenceparser.go:34-40
internal static class ProjectReferenceParseTasks
{
    internal static List<ProjectReferenceParseTask> Create(string[] projectReferences)
    {
        var result = new List<ProjectReferenceParseTask>(projectReferences.Length);
        foreach (var configName in projectReferences)
        {
            result.Add(new ProjectReferenceParseTask { ConfigName = configName });
        }
        return result;
    }
}

// projectreferenceparser.go:42-46
internal sealed class ProjectReferenceParser
{
    internal FileLoader Loader = null!;
    internal IWorkGroup Wg = null!;
    internal readonly SyncMap<global::Ts.TsPath.Path, ProjectReferenceParseTask> TasksByFileName = new();

    // projectreferenceparser.go:48-53
    internal void Parse(List<ProjectReferenceParseTask> tasks)
    {
        Loader.ProjectReferenceFileMapper.Loader = Loader;
        Start(tasks);
        Wg.RunAndWait();
        InitMapper(tasks);
    }

    // projectreferenceparser.go:55-67
    private void Start(List<ProjectReferenceParseTask>? tasks)
    {
        if (tasks == null)
        {
            return;
        }
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var path = Loader.ToPath(task.ConfigName);
            var (loadedTask, loaded) = TasksByFileName.LoadOrStore(path, task);
            if (loaded)
            {
                // dedup tasks to ensure correct file order, regardless of which task would be started first
                tasks[i] = loadedTask;
            }
            else
            {
                Wg.Queue(() =>
                {
                    task.Parse(this);
                    Start(task.SubTasks);
                });
            }
        }
    }

    // projectreferenceparser.go:69-80
    private void InitMapper(List<ProjectReferenceParseTask> tasks)
    {
        var mapper = Loader.ProjectReferenceFileMapper;
        mapper.ConfigToProjectReference = new Dictionary<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine>();
        mapper.ReferencesInConfigFile = new Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path[]>();
        mapper.SourceToProjectReference = new Dictionary<global::Ts.TsPath.Path, global::Ts.TsOptions.SourceOutputAndProjectReference>();
        mapper.OutputDtsToProjectReference = new Dictionary<global::Ts.TsPath.Path, global::Ts.TsOptions.SourceOutputAndProjectReference>();
        mapper.ReferencesInConfigFile[Loader.Opts.Config.ConfigFile!.SourceFile.Path()] = InitMapperWorker(tasks, new Set<ProjectReferenceParseTask>());
        // PORT-NOTE: newProjectReferenceDtsFakingHost (projectreferenceparser.go:77-79 — source
        // redirection for the LSP UseSourceOfProjectReference mode) is deferred with the LS wave;
        // the CLI never enables CanUseProjectReferenceSource.
    }

    // projectreferenceparser.go:82-115
    private global::Ts.TsPath.Path[] InitMapperWorker(List<ProjectReferenceParseTask>? tasks, Set<ProjectReferenceParseTask> seen)
    {
        if (tasks == null || tasks.Count == 0)
        {
            return [];
        }
        var mapper = Loader.ProjectReferenceFileMapper;
        var results = new List<global::Ts.TsPath.Path>(tasks.Count);
        foreach (var task in tasks)
        {
            var path = Loader.ToPath(task.ConfigName);
            results.Add(path);
            // ensure we only walk each task once
            if (!seen.AddIfAbsent(task))
            {
                continue;
            }
            if (task.Resolved != null)
            {
                mapper.ConfigToProjectReference![path] = task.Resolved;
            }
            if (task.Resolved != null && !ReferenceEquals(mapper.Opts.Config.ConfigFile, task.Resolved.ConfigFile))
            {
                // Map current task's files first, before recursing into subtasks.
                // This matches TypeScript's behavior where child project references
                // overwrite parent entries when a file belongs to multiple projects.
                foreach (var (key, value) in task.Resolved.SourceToProjectReference()) // map-iter: 序无关 (maps.Copy)
                {
                    mapper.SourceToProjectReference![key] = value;
                }
                foreach (var (key, value) in task.Resolved.OutputDtsToProjectReference()) // map-iter: 序无关 (maps.Copy)
                {
                    mapper.OutputDtsToProjectReference![key] = value;
                }
                if (mapper.Opts.CanUseProjectReferenceSource())
                {
                    var declDir = task.Resolved.CompilerOptions()!.DeclarationDir;
                    if (declDir.Length == 0)
                    {
                        declDir = task.Resolved.CompilerOptions()!.OutDir;
                    }
                    if (declDir.Length > 0)
                    {
                        Loader.DtsDirectories.Add(Loader.ToPath(declDir));
                    }
                }
            }
            var referencesInConfig = InitMapperWorker(task.SubTasks, seen);
            mapper.ReferencesInConfigFile![path] = referencesInConfig;
        }
        return results.ToArray();
    }
}
