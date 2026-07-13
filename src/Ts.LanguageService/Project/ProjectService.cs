// Foundation subset of tsgo internal/project/session.go,
// projectcollection.go, and projectcollectionbuilder.go.
//
// Session/Snapshot in the pinned Go tree cover substantially more territory
// (immutable snapshot ref-counting, ATA, auto imports, API opens, diagnostics,
// and project-reference graphs). ProjectService preserves the core ownership
// loop required here: ScriptInfo registry -> config discovery -> configured or
// inferred Project -> Program -> per-project LanguageService.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project;

public sealed class ProjectService : IDisposable
{
    private readonly Lock gate = new();
    private readonly string currentDirectory;
    private readonly string defaultLibraryPath;
    private readonly OverlayFileSystem fileSystem;
    private readonly global::Ts.FsWatch.IWatcher? watcher;
    private readonly global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects;
    private readonly Dictionary<global::Ts.TsPath.Path, ScriptInfo> scriptInfos = [];
    private readonly Dictionary<global::Ts.TsPath.Path, Project> configuredProjects = [];
    private readonly Dictionary<global::Ts.TsPath.Path, Project> fileDefaultProjects = [];
    private Project? inferredProject;
    private ulong updateId;
    private bool disposed;
    private Exception? lastWatchError;

    public ProjectService(
        global::Ts.Vfs.IFileSystem fileSystem,
        string currentDirectory,
        string defaultLibraryPath,
        global::Ts.FsWatch.IWatcher? watcher = null,
        global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(defaultLibraryPath);

        this.currentDirectory = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(currentDirectory, "");
        this.defaultLibraryPath = defaultLibraryPath;
        this.fileSystem = new OverlayFileSystem(fileSystem, this.currentDirectory);
        this.watcher = watcher;
        this.compilerOptionsForInferredProjects = compilerOptionsForInferredProjects;
    }

    public string CurrentDirectory => currentDirectory;

    public global::Ts.Vfs.IFileSystem FileSystem => fileSystem;

    public Exception? LastWatchError
    {
        get
        {
            using (gate.EnterScope())
            {
                return lastWatchError;
            }
        }
    }

    public IReadOnlyList<Project> Projects
    {
        get
        {
            using (gate.EnterScope())
            {
                // map-iter: 已排序 — configured projects are sorted by canonical ID below.
                var projects = new List<Project>(configuredProjects.Values);
                projects.Sort(static (left, right) => string.CompareOrdinal(left.Id().Value, right.Id().Value));
                if (inferredProject is not null)
                {
                    projects.Add(inferredProject);
                }
                return projects.ToArray();
            }
        }
    }

    public IReadOnlyList<ScriptInfo> OpenFiles
    {
        get
        {
            using (gate.EnterScope())
            {
                var result = new List<ScriptInfo>();
                // map-iter: 已排序 — the result is sorted by canonical path below.
                foreach (ScriptInfo scriptInfo in scriptInfos.Values)
                {
                    if (scriptInfo.IsOpen)
                    {
                        result.Add(scriptInfo);
                    }
                }
                result.Sort(static (left, right) => string.CompareOrdinal(left.Path.Value, right.Path.Value));
                return result.ToArray();
            }
        }
    }

    public Project OpenDocument(
        string fileName,
        string content,
        int version = 1,
        global::Ts.Core.ScriptKind scriptKind = global::Ts.Core.ScriptKind.Unknown)
    {
        ArgumentNullException.ThrowIfNull(content);
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            string normalizedFileName = NormalizeFileName(fileName);
            global::Ts.TsPath.Path path = ToPath(normalizedFileName);
            if (scriptKind == global::Ts.Core.ScriptKind.Unknown)
            {
                scriptKind = global::Ts.Core.CoreUtil.GetScriptKindFromFileName(normalizedFileName);
            }

            if (scriptInfos.TryGetValue(path, out ScriptInfo? scriptInfo))
            {
                scriptInfo.Open(content, version, scriptKind);
            }
            else
            {
                scriptInfo = new ScriptInfo(normalizedFileName, path, content, version, scriptKind);
                scriptInfos[path] = scriptInfo;
            }

            PublishOpenFiles();
            RebuildProjectAssociations();
            return fileDefaultProjects[path];
        }
    }

    public Project ChangeDocument(string fileName, string content, int version)
    {
        ArgumentNullException.ThrowIfNull(content);
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            string normalizedFileName = NormalizeFileName(fileName);
            global::Ts.TsPath.Path path = ToPath(normalizedFileName);
            if (!scriptInfos.TryGetValue(path, out ScriptInfo? scriptInfo))
            {
                throw new InvalidOperationException("script is not registered: " + normalizedFileName);
            }

            scriptInfo.Change(content, version);
            PublishOpenFiles();

            if (IsConfigFileName(normalizedFileName))
            {
                RebuildProjectAssociations();
                return fileDefaultProjects[path];
            }

            if (!fileDefaultProjects.TryGetValue(path, out Project? project))
            {
                RebuildProjectAssociations();
                return fileDefaultProjects[path];
            }

            project.MarkDirty();
            if (!project.Reload(fileSystem, defaultLibraryPath, NextUpdateId()))
            {
                RebuildProjectAssociations();
                return fileDefaultProjects[path];
            }
            return project;
        }
    }

    public void CloseDocument(string fileName)
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            string normalizedFileName = NormalizeFileName(fileName);
            global::Ts.TsPath.Path path = ToPath(normalizedFileName);
            if (!scriptInfos.TryGetValue(path, out ScriptInfo? scriptInfo))
            {
                throw new InvalidOperationException("script is not registered: " + normalizedFileName);
            }

            scriptInfo.Close();
            PublishOpenFiles();
            RebuildProjectAssociations();
        }
    }

    public ScriptInfo? GetScriptInfo(string fileName)
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            scriptInfos.TryGetValue(ToPath(NormalizeFileName(fileName)), out ScriptInfo? scriptInfo);
            return scriptInfo;
        }
    }

    public Project? GetDefaultProject(string fileName)
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            fileDefaultProjects.TryGetValue(ToPath(NormalizeFileName(fileName)), out Project? project);
            return project;
        }
    }

    public global::Ts.Ls.LanguageService GetLanguageService(string fileName)
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            global::Ts.TsPath.Path path = ToPath(NormalizeFileName(fileName));
            if (!fileDefaultProjects.TryGetValue(path, out Project? project))
            {
                throw new InvalidOperationException("no project found for file: " + fileName);
            }

            return project.GetLanguageService()
                ?? throw new InvalidOperationException("project has no language service: " + project.Name());
        }
    }

    public string? FindConfigFile(string fileName)
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            return ConfigFileDiscovery.FindConfigFile(NormalizeFileName(fileName), fileSystem);
        }
    }

    // Consumes disk/file-watcher changes after the underlying VFS has already
    // observed the new state. Config changes can alter ownership, so they redo
    // discovery; ordinary source changes dirty only relevant projects.
    public void NotifyFileChanged(string fileName)
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            NotifyFileChangedLocked(NormalizeFileName(fileName));
        }
    }

    public void ReloadProjects()
    {
        using (gate.EnterScope())
        {
            ThrowIfDisposed();
            RebuildProjectAssociations();
        }
    }

    public void Dispose()
    {
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            DisposeProjects();
            configuredProjects.Clear();
            fileDefaultProjects.Clear();
            inferredProject = null;
        }
    }

    private void RebuildProjectAssociations()
    {
        DisposeProjects();
        configuredProjects.Clear();
        fileDefaultProjects.Clear();
        inferredProject = null;

        var openFiles = new List<ScriptInfo>();
        // map-iter: 已排序 — open files are sorted before project discovery.
        foreach (ScriptInfo scriptInfo in scriptInfos.Values)
        {
            if (scriptInfo.IsOpen)
            {
                openFiles.Add(scriptInfo);
            }
        }
        openFiles.Sort(static (left, right) => string.CompareOrdinal(left.Path.Value, right.Path.Value));

        var configuredGroups = new Dictionary<global::Ts.TsPath.Path, ConfiguredOpenFiles>();
        var inferredFiles = new List<ScriptInfo>();
        foreach (ScriptInfo scriptInfo in openFiles)
        {
            string? configFileName = ConfigFileDiscovery.FindConfigFile(scriptInfo.FileName, fileSystem);
            if (configFileName is null)
            {
                inferredFiles.Add(scriptInfo);
                continue;
            }

            global::Ts.TsPath.Path configPath = ToPath(configFileName);
            if (!configuredGroups.TryGetValue(configPath, out ConfiguredOpenFiles? group))
            {
                group = new ConfiguredOpenFiles(configFileName);
                configuredGroups[configPath] = group;
            }
            group.Files.Add(scriptInfo);
        }

        var configPaths = new List<global::Ts.TsPath.Path>(configuredGroups.Keys);
        configPaths.Sort(static (left, right) => string.CompareOrdinal(left.Value, right.Value));
        foreach (global::Ts.TsPath.Path configPath in configPaths)
        {
            ConfiguredOpenFiles group = configuredGroups[configPath];
            var project = Project.NewConfiguredProject(
                group.ConfigFileName,
                global::Ts.TsPath.TsPath.GetDirectoryPath(group.ConfigFileName),
                fileSystem.UseCaseSensitiveFileNames());
            if (!project.Reload(fileSystem, defaultLibraryPath, NextUpdateId()))
            {
                inferredFiles.AddRange(group.Files);
                project.Dispose();
                continue;
            }

            bool retained = false;
            foreach (ScriptInfo scriptInfo in group.Files)
            {
                // Like Go findDefaultConfiguredProject, a nearby config is only
                // the default when its Program actually contains the file.
                if (project.HasFile(scriptInfo.FileName))
                {
                    fileDefaultProjects[scriptInfo.Path] = project;
                    retained = true;
                }
                else
                {
                    inferredFiles.Add(scriptInfo);
                }
            }

            if (retained)
            {
                configuredProjects[configPath] = project;
                AttachConfiguredProjectWatches(project);
            }
            else
            {
                project.Dispose();
            }
        }

        if (inferredFiles.Count > 0)
        {
            inferredFiles.Sort(static (left, right) => string.CompareOrdinal(left.Path.Value, right.Path.Value));
            var rootFileNames = new string[inferredFiles.Count];
            for (int i = 0; i < inferredFiles.Count; i++)
            {
                rootFileNames[i] = inferredFiles[i].FileName;
            }

            inferredProject = Project.NewInferredProject(
                currentDirectory,
                compilerOptionsForInferredProjects,
                rootFileNames,
                fileSystem.UseCaseSensitiveFileNames());
            if (!inferredProject.Reload(fileSystem, defaultLibraryPath, NextUpdateId()))
            {
                throw new InvalidOperationException("failed to create inferred project");
            }

            AttachInferredProjectWatch(inferredProject);

            foreach (ScriptInfo scriptInfo in inferredFiles)
            {
                fileDefaultProjects[scriptInfo.Path] = inferredProject;
            }
        }
    }

    private void NotifyFileChangedLocked(string normalizedFileName)
    {
        if (IsConfigFileName(normalizedFileName))
        {
            RebuildProjectAssociations();
            return;
        }

        global::Ts.TsPath.Path changedPath = ToPath(normalizedFileName);
        var affectedProjects = new List<Project>();
        // map-iter: 已排序 — affected configured projects are sorted below.
        foreach (Project project in configuredProjects.Values)
        {
            global::Ts.TsPath.Path rootPath = ToPath(project.CurrentDirectory);
            if (project.HasFile(normalizedFileName) || rootPath.ContainsPath(changedPath))
            {
                affectedProjects.Add(project);
            }
        }
        affectedProjects.Sort(static (left, right) => string.CompareOrdinal(left.Id().Value, right.Id().Value));

        if (inferredProject is not null && inferredProject.HasFile(normalizedFileName))
        {
            affectedProjects.Add(inferredProject);
        }

        foreach (Project project in affectedProjects)
        {
            project.MarkDirty();
            if (!project.Reload(fileSystem, defaultLibraryPath, NextUpdateId()))
            {
                RebuildProjectAssociations();
                return;
            }
        }
    }

    private void AttachConfiguredProjectWatches(Project project)
    {
        if (watcher is null)
        {
            return;
        }

        string configFileName = project.ConfigFileName();
        try
        {
            project.AddWatch(watcher.WatchFile(configFileName, OnWatchEvents));
            project.AddWatch(watcher.WatchDirectory(
                project.CurrentDirectory,
                OnWatchEvents,
                global::Ts.FsWatch.Watcher.WithRecursive(),
                global::Ts.FsWatch.Watcher.WithIgnore(path =>
                    ToPath(path) == project.ConfigFilePath() || !IsRelevantProjectFileName(path))));
        }
        catch (Exception error) when (error is global::Ts.FsWatch.FsWatchException or ArgumentException)
        {
            lastWatchError = error;
        }
    }

    private void AttachInferredProjectWatch(Project project)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            project.AddWatch(watcher.WatchDirectory(
                currentDirectory,
                OnWatchEvents,
                global::Ts.FsWatch.Watcher.WithRecursive(),
                global::Ts.FsWatch.Watcher.WithIgnore(path => !IsRelevantProjectFileName(path))));
        }
        catch (Exception error) when (error is global::Ts.FsWatch.FsWatchException or ArgumentException)
        {
            lastWatchError = error;
        }
    }

    private void OnWatchEvents(IReadOnlyList<global::Ts.FsWatch.Event> events, Exception? error)
    {
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return;
            }

            if (error is not null)
            {
                lastWatchError = error;
                RebuildProjectAssociations();
                return;
            }

            var paths = new List<string>(events.Count);
            foreach (global::Ts.FsWatch.Event fileEvent in events)
            {
                paths.Add(fileEvent.Path);
            }
            paths.Sort(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                NotifyFileChangedLocked(NormalizeFileName(path));
            }
        }
    }

    private void PublishOpenFiles()
    {
        // map-iter: 序无关 — OverlayFileSystem publishes by canonical path.
        fileSystem.ReplaceOpenFiles(scriptInfos.Values);
    }

    private void DisposeProjects()
    {
        // map-iter: 序无关 — project watches are independent and all are closed.
        foreach (Project project in configuredProjects.Values)
        {
            project.Dispose();
        }
        inferredProject?.Dispose();
    }

    private string NormalizeFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, currentDirectory);
    }

    private global::Ts.TsPath.Path ToPath(string fileName) =>
        global::Ts.TsPath.TsPath.ToPath(fileName, currentDirectory, fileSystem.UseCaseSensitiveFileNames());

    private ulong NextUpdateId() => ++updateId;

    private static bool IsConfigFileName(string fileName)
    {
        string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(fileName);
        return string.Equals(baseName, "tsconfig.json", StringComparison.Ordinal)
            || string.Equals(baseName, "jsconfig.json", StringComparison.Ordinal);
    }

    private static bool IsRelevantProjectFileName(string fileName) =>
        global::Ts.Core.CoreUtil.GetScriptKindFromFileName(fileName) != global::Ts.Core.ScriptKind.Unknown;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class ConfiguredOpenFiles
    {
        internal ConfiguredOpenFiles(string configFileName)
        {
            ConfigFileName = configFileName;
        }

        internal string ConfigFileName { get; }

        internal List<ScriptInfo> Files { get; } = [];
    }
}
