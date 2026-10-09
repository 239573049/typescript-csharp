// Ported from tsgo internal/project/projectcollection.go (whole file).

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;

namespace Ts.Project;

// projectcollection.go:15-39 — ProjectCollection.
public sealed class ProjectCollection
{
    // Fields are internal (not private): the project package mutates them from the
    // builders exactly like Go's same-package field access (projectcollectionbuilder.go).
    internal readonly Func<string, global::Ts.TsPath.Path> toPath;
    internal ConfigFileRegistry configFileRegistry;

    // fileDefaultProjects is a map of file paths to the config file path (the key
    // into `configuredProjects`) of the default project for that file. If the file
    // belongs to the inferred project, the value is `inferredProjectName`. This map
    // contains quick lookups for only the associations discovered during the latest
    // snapshot update.
    internal Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>? fileDefaultProjects;

    // configuredProjects is the set of loaded projects associated with a tsconfig
    // file, keyed by the config file path.
    internal Dictionary<global::Ts.TsPath.Path, Project> configuredProjects;

    // openFiles is the set of open file paths associated with the snapshot that owns
    // this project collection.
    internal global::Ts.Collections.Set<global::Ts.TsPath.Path> openFiles;

    // inferredProject is a fallback project that is used when no configured
    // project can be found for an open file.
    internal Project? inferredProject;

    // apiState tracks the projects and files that API clients have explicitly
    // opened so they are kept loaded across snapshots.
    internal APIState apiState;

    // Go: openConfiguredProjectsOnce sync.Once → double-checked lazy init.
    private readonly object openConfiguredProjectsOnce = new();
    private global::Ts.Collections.Set<global::Ts.TsPath.Path>? openConfiguredProjects;

    // Cross-type accessors (Go: same-package field access).
    internal Dictionary<global::Ts.TsPath.Path, Project> ConfiguredProjectsMap => configuredProjects;

    internal Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>? FileDefaultProjects => fileDefaultProjects;

    internal global::Ts.Collections.Set<global::Ts.TsPath.Path> OpenFiles => openFiles;

    internal Project? InferredProjectValue => inferredProject;

    internal ConfigFileRegistry Registry => configFileRegistry;

    internal APIState ApiState => apiState;

    private ProjectCollection(
        Func<string, global::Ts.TsPath.Path> toPath,
        ConfigFileRegistry configFileRegistry,
        Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>? fileDefaultProjects,
        Dictionary<global::Ts.TsPath.Path, Project> configuredProjects,
        global::Ts.Collections.Set<global::Ts.TsPath.Path> openFiles,
        Project? inferredProject,
        APIState apiState)
    {
        this.toPath = toPath;
        this.configFileRegistry = configFileRegistry;
        this.fileDefaultProjects = fileDefaultProjects;
        this.configuredProjects = configuredProjects;
        this.openFiles = openFiles;
        this.inferredProject = inferredProject;
        this.apiState = apiState;
    }

    // Snapshot.NewSnapshot is the only place a fresh collection is seeded (Go: the
    // zero value + openFilePaths assignment in snapshot.go:210-214).
    internal static ProjectCollection NewProjectCollection(
        Func<string, global::Ts.TsPath.Path> toPath,
        global::Ts.Collections.Set<global::Ts.TsPath.Path> openFiles)
    {
        return new ProjectCollection(
            toPath,
            new ConfigFileRegistry(),
            new Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>(),
            new Dictionary<global::Ts.TsPath.Path, Project>(),
            openFiles,
            null,
            new APIState());
    }

    // projectcollection.go:41-53 — APIState tracks the projects and files that API
    // clients have explicitly opened. Opens and closes are ref-counted so multiple API
    // clients don't clobber each other, and it is carried across snapshots so API-opened
    // resources stay loaded. (Go value struct → sealed class; it is always manipulated
    // through the owning builder/snapshot reference.)
    internal sealed class APIState
    {
        // openProjects is the ref-counted set of projects to keep open for API
        // clients, keyed by config file path. The value is the number of outstanding
        // API opens.
        internal Dictionary<global::Ts.TsPath.Path, int> openProjects = [];

        // openFiles is the ref-counted set of files to keep open for API clients,
        // keyed by file path. Files with no configured project are loaded into the
        // inferred project.
        internal Dictionary<global::Ts.TsPath.Path, ApiOpenedFile> openFiles = [];

        // projectcollection.go:55-60 — clone.
        internal APIState Clone()
        {
            return new APIState
            {
                // Go: maps.Clone — the C# fields carry `= []` initializers (never null),
                // so a plain copy stands in for Go's nil-preserving clone; MapsEqual treats
                // empty and nil alike.
                openProjects = new Dictionary<global::Ts.TsPath.Path, int>(openProjects),
                openFiles = new Dictionary<global::Ts.TsPath.Path, ApiOpenedFile>(openFiles),
            };
        }

        // projectcollection.go:62-64 — equals.
        internal bool EqualTo(APIState other)
        {
            return MapsEqual(openProjects, other.openProjects) && ApiOpenedFileMapsEqual(openFiles, other.openFiles);
        }

        private static bool MapsEqual(Dictionary<global::Ts.TsPath.Path, int>? a, Dictionary<global::Ts.TsPath.Path, int>? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }
            if (a.Count != b.Count)
            {
                return false;
            }
            foreach ((global::Ts.TsPath.Path key, int value) in a)
            {
                if (!b.TryGetValue(key, out int other) || other != value)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ApiOpenedFileMapsEqual(Dictionary<global::Ts.TsPath.Path, ApiOpenedFile>? a, Dictionary<global::Ts.TsPath.Path, ApiOpenedFile>? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }
            if (a.Count != b.Count)
            {
                return false;
            }
            foreach ((global::Ts.TsPath.Path key, ApiOpenedFile value) in a)
            {
                if (!b.TryGetValue(key, out ApiOpenedFile? other) || other is null || other.fileName != value.fileName || other.refCount != value.refCount)
                {
                    return false;
                }
            }
            return true;
        }
    }

    // projectcollection.go:66-70 — apiOpenedFile tracks a file kept open by API clients
    // along with its ref count. (Go value struct → mutable class; callers treat the map
    // value as a replaceable record exactly like Go's `entry := m[path]; entry.x = …; m[path] = entry`.)
    internal sealed class ApiOpenedFile
    {
        internal string fileName = "";
        internal int refCount;
    }

    // projectcollection.go:72 — ConfigFileRegistry.
    public ConfigFileRegistry ConfigFileRegistry() => configFileRegistry;

    // projectcollection.go:74-76 — ConfiguredProject.
    public Project? ConfiguredProject(global::Ts.TsPath.Path path) => configuredProjects.GetValueOrDefault(path);

    // projectcollection.go:78-88 — GetProjectByPath.
    public Project? GetProjectByPath(global::Ts.TsPath.Path projectPath)
    {
        if (configuredProjects.TryGetValue(projectPath, out Project? project))
        {
            return project;
        }

        if (projectPath.Value == Project.InferredProjectName)
        {
            return inferredProject;
        }

        return null;
    }

    // projectcollection.go:90-95 — ConfiguredProjects returns all configured projects in a stable order.
    public List<Project> ConfiguredProjects()
    {
        var projects = new List<Project>(configuredProjects.Count);
        FillConfiguredProjects(projects);
        return projects;
    }

    private void FillConfiguredProjects(List<Project> projects)
    {
        // map-iter: 序无关 — sorted below by name, matching Go's slices.SortFunc.
        foreach (Project p in configuredProjects.Values)
        {
            projects.Add(p);
        }
        projects.Sort(static (a, b) => string.CompareOrdinal(a.Name(), b.Name()));
    }

    // projectcollection.go:106-119 — ProjectsByPath returns an ordered map of configured projects
    // keyed by their config file path, plus the inferred project, if it exists, with the key
    // `inferredProjectName`.
    public global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, Project> ProjectsByPath()
    {
        var projects = new global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, Project>(
            configuredProjects.Count + global::Ts.Core.CoreUtil.IfElse(inferredProject is not null, 1, 0));
        foreach (Project project in ConfiguredProjects())
        {
            projects.Set(project.configFilePath, project);
        }
        if (inferredProject is not null)
        {
            projects.Set(new global::Ts.TsPath.Path(Project.InferredProjectName), inferredProject);
        }
        return projects;
    }

    // projectcollection.go:121-130 — Projects returns all projects, including the inferred
    // project if it exists, in a stable order.
    public List<Project> Projects()
    {
        if (inferredProject is null)
        {
            return ConfiguredProjects();
        }
        var projects = new List<Project>(configuredProjects.Count + 1);
        FillConfiguredProjects(projects);
        projects.Add(inferredProject);
        return projects;
    }

    // projectcollection.go:132-134 — InferredProject.
    public Project? InferredProject() => inferredProject;

    // projectcollection.go:136-147 — GetProjectsContainingFile.
    public List<LsProject> GetProjectsContainingFile(global::Ts.TsPath.Path path)
    {
        var projects = new List<LsProject>();
        foreach (Project project in ConfiguredProjects())
        {
            if (project.ContainsFile(path))
            {
                projects.Add(project);
            }
        }
        if (inferredProject is not null && inferredProject.ContainsFile(path))
        {
            projects.Add(inferredProject);
        }
        return projects;
    }

    // projectcollection.go:149-170 — GetOpenConfiguredProjects returns configured projects
    // containing at least one open file.
    public global::Ts.Collections.Set<global::Ts.TsPath.Path> GetOpenConfiguredProjects()
    {
        if (openConfiguredProjects is null)
        {
            lock (openConfiguredProjectsOnce)
            {
                if (openConfiguredProjects is null)
                {
                    var openProjects = new global::Ts.Collections.Set<global::Ts.TsPath.Path>(configuredProjects.Count);
                    // map-iter: 序无关 — set accumulation over open files.
                    foreach (global::Ts.TsPath.Path path in openFiles.Keys())
                    {
                        // Go: nil-map read ⇒ zero, false; mirror with a null guard.
                        if (fileDefaultProjects is not null && fileDefaultProjects.TryGetValue(path, out global::Ts.TsPath.Path projectPath) && projectPath.Value != Project.InferredProjectName)
                        {
                            if (configuredProjects.ContainsKey(projectPath))
                            {
                                openProjects.Add(projectPath);
                                continue;
                            }
                        }

                        // map-iter: 序无关 — membership probing per project.
                        foreach (Project project in configuredProjects.Values)
                        {
                            if (project.ContainsFile(path))
                            {
                                openProjects.Add(project.configFilePath);
                            }
                        }
                    }
                    openConfiguredProjects = openProjects;
                }
            }
        }
        return openConfiguredProjects;
    }

    // projectcollection.go:172-178 — openFilePaths (hosted in SnapshotUtil; see note there).

    // !!! result could be cached
    // projectcollection.go:180-232 — GetDefaultProject.
    public Project? GetDefaultProject(global::Ts.TsPath.Path path)
    {
        // Go: nil-map read ⇒ zero, false; mirror with a null guard.
        if (fileDefaultProjects is not null && fileDefaultProjects.TryGetValue(path, out global::Ts.TsPath.Path result))
        {
            if (result.Value == Project.InferredProjectName)
            {
                return inferredProject;
            }
            return configuredProjects.GetValueOrDefault(result);
        }

        List<Project> containingProjects;
        Project? firstConfiguredProject = null;
        Project? firstNonSourceOfProjectReferenceRedirect = null;
        bool multipleDirectInclusions = false;
        var containing = new List<Project>();
        foreach (Project p in ConfiguredProjects())
        {
            if (p.ContainsFile(path))
            {
                containing.Add(p);
                if (!multipleDirectInclusions && !p.IsSourceFromProjectReference(path))
                {
                    if (firstNonSourceOfProjectReferenceRedirect is null)
                    {
                        firstNonSourceOfProjectReferenceRedirect = p;
                    }
                    else
                    {
                        multipleDirectInclusions = true;
                    }
                }
                if (firstConfiguredProject is null)
                {
                    firstConfiguredProject = p;
                }
            }
        }
        containingProjects = containing;
        if (containingProjects.Count == 1)
        {
            return containingProjects[0];
        }
        if (containingProjects.Count == 0)
        {
            if (inferredProject is not null && inferredProject.ContainsFile(path))
            {
                return inferredProject;
            }
            return null;
        }
        if (!multipleDirectInclusions)
        {
            if (firstNonSourceOfProjectReferenceRedirect is not null)
            {
                // Multiple projects include the file, but only one is a direct inclusion.
                return firstNonSourceOfProjectReferenceRedirect;
            }
            // Multiple projects include the file, and none are direct inclusions.
            return firstConfiguredProject;
        }
        // Multiple projects include the file directly.
        Project? defaultProject = FindDefaultConfiguredProject(path);
        if (defaultProject is not null)
        {
            return defaultProject;
        }
        return firstConfiguredProject;
    }

    // projectcollection.go:234-239 — findDefaultConfiguredProject.
    internal Project? FindDefaultConfiguredProject(global::Ts.TsPath.Path path)
    {
        string configFileName = configFileRegistry.GetConfigFileName(path);
        if (configFileName != "")
        {
            return FindDefaultConfiguredProjectWorker(path, configFileName, null, null);
        }
        return null;
    }

    // projectcollection.go:241-295 — findDefaultConfiguredProjectWorker.
    private Project? FindDefaultConfiguredProjectWorker(global::Ts.TsPath.Path path, string configFileName, global::Ts.Collections.SyncSet<Project>? visited, Project? fallback)
    {
        global::Ts.TsPath.Path configFilePath = toPath(configFileName);
        if (!configuredProjects.TryGetValue(configFilePath, out Project? project))
        {
            return null;
        }
        if (visited is null)
        {
            visited = new global::Ts.Collections.SyncSet<Project>();
        }

        // Look in the config's project and its references recursively.
        global::Ts.Core.BreadthFirstSearchResult<Project> search = global::Ts.Core.Bfs.BreadthFirstSearchParallelEx(
            project,
            project =>
            {
                if (project.CommandLine is null)
                {
                    return [];
                }
                // A referenced project may not be loaded if `disableReferencedProjectLoad` is true.
                // MapNonNil strips the nulls (unloaded referenced projects are dropped), so
                // project to a non-nullable array (Checker.IterationTypes precedent).
                return Array.ConvertAll(
                    global::Ts.Core.CoreUtil.MapNonNil(
                        project.CommandLine.ResolvedProjectReferencePaths(),
                        configFileName => configuredProjects.GetValueOrDefault(toPath(configFileName))) ?? [],
                    p => p!);
            },
            project =>
            {
                if (project.ContainsFile(path))
                {
                    return (true, !project.IsSourceFromProjectReference(path));
                }
                return (false, false);
            },
            new global::Ts.Core.BreadthFirstSearchOptions<Project, Project>
            {
                Visited = visited,
            },
            global::Ts.Core.CoreUtil.Identity);

        if (search.Stopped)
        {
            // If we found a project that contains the file, return it.
            return search.Path![0];
        }
        if (search.Path is { Length: > 0 } && fallback is null)
        {
            // If we found a project that contains the file, but it is a source from
            // a project reference, record it as a fallback.
            fallback = search.Path[0];
        }

        // Look for tsconfig.json files higher up the directory tree and do the same. This handles
        // the common case where a higher-level "solution" tsconfig.json contains all projects in a
        // workspace.
        global::Ts.TsOptions.ParsedCommandLine? config = configFileRegistry.GetConfig(path);
        if (config is not null && config.CompilerOptions()!.DisableSolutionSearching.IsTrue())
        {
            return fallback;
        }
        string ancestorConfigName = configFileRegistry.GetAncestorConfigFileName(path, configFileName);
        if (ancestorConfigName != "")
        {
            return FindDefaultConfiguredProjectWorker(path, ancestorConfigName, visited, fallback);
        }
        return fallback;
    }

    // projectcollection.go:297-308 — clone creates a shallow copy of the project collection.
    internal ProjectCollection Clone()
    {
        return new ProjectCollection(
            toPath,
            configFileRegistry,
            fileDefaultProjects,
            configuredProjects,
            openFiles,
            inferredProject,
            apiState);
    }

    // projectcollection.go:310-359 — findDefaultConfiguredProjectFromProgramInclusion finds the
    // default configured project for a file based on the file's inclusion in existing projects.
    // The projects should be sorted, as ties will be broken by slice order. `getProject` should
    // return a project with an up-to-date program. Along with the resulting project path, a
    // boolean is returned indicating whether there were multiple direct inclusions of the file
    // in different projects, indicating that the caller may want to perform additional logic to
    // determine the best project.
    internal static (global::Ts.TsPath.Path Result, bool MultipleCandidates) FindDefaultConfiguredProjectFromProgramInclusion(
        string fileName,
        global::Ts.TsPath.Path path,
        global::Ts.TsPath.Path[] projectPaths,
        Func<global::Ts.TsPath.Path, Project?> getProject)
    {
        _ = fileName;
        List<global::Ts.TsPath.Path> containingProjects;
        global::Ts.TsPath.Path firstConfiguredProject = default;
        global::Ts.TsPath.Path firstNonSourceOfProjectReferenceRedirect = default;
        bool multipleDirectInclusions = false;
        var containing = new List<global::Ts.TsPath.Path>();

        foreach (global::Ts.TsPath.Path projectPath in projectPaths)
        {
            Project? p = getProject(projectPath);
            if (p is not null && p.ContainsFile(path))
            {
                containing.Add(projectPath);
                if (!multipleDirectInclusions && !p.IsSourceFromProjectReference(path))
                {
                    if (firstNonSourceOfProjectReferenceRedirect.Value is null)
                    {
                        firstNonSourceOfProjectReferenceRedirect = projectPath;
                    }
                    else
                    {
                        multipleDirectInclusions = true;
                    }
                }
                if (firstConfiguredProject.Value is null)
                {
                    firstConfiguredProject = projectPath;
                }
            }
        }
        containingProjects = containing;

        if (containingProjects.Count == 1)
        {
            return (containingProjects[0], false);
        }
        if (!multipleDirectInclusions)
        {
            if (firstNonSourceOfProjectReferenceRedirect.Value is not null)
            {
                // Multiple projects include the file, but only one is a direct inclusion.
                return (firstNonSourceOfProjectReferenceRedirect, false);
            }
            // Multiple projects include the file, and none are direct inclusions.
            return (firstConfiguredProject, false);
        }
        // Multiple projects include the file directly.
        return (firstConfiguredProject, true);
    }
}

