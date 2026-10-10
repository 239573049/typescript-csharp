// Ported from tsgo internal/project/projectcollectionbuilder.go (whole file).
// PORT-NOTEs:
//  * Go `map[tspath.Path]struct{}` → Dictionary<Path, byte> (repo convention, cf.
//    ConfigFileRegistryBuilder.cs / Collections.SyncSet); `maps.Copy`/`maps.Clone`/
//    `maps.Equal` over those sets are expressed with the small helpers at the bottom.
//  * `core.NewWorkGroup(false)` → Ts.Core.WorkGroup.Create(false) (IWorkGroup).
//  * `collections.SyncMap` (non-dirty) is qualified Ts.Collections.SyncMap to avoid the
//    CS0104 clash with Ts.Project.Dirty.SyncMap.
//  * changeFileResult lives nested in ConfigFileRegistryBuilder (Go declares it at
//    package level in configfileregistrybuilder.go; the C# host file nested it).

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Project.Dirty;
using System.Linq;
using Ts.Core;
using Ts.Project.Logging;
using static global::Ts.LsProto.DocumentUriExtensions;
using static global::Ts.Compiler.ProgramLsExports;

using FileChangeType = global::Ts.LsProto.FileChangeType;

namespace Ts.Project;

// projectcollectionbuilder.go:21-28 — projectLoadKind.
internal enum ProjectLoadKind
{
    // Project is not created or updated, only looked up in cache
    Find,
    // Project is created and then its graph is updated
    Create,
}

// projectcollectionbuilder.go:30-54 — ProjectCollectionBuilder.
public sealed class ProjectCollectionBuilder
{
    internal readonly SessionOptions sessionOptions;
    internal readonly RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache;
    internal readonly ExtendedConfigCache extendedConfigCache;
    internal readonly Func<string, global::Ts.TsPath.Path> toPath;

    private readonly global::Ts.Core.RequestContext ctx;
    internal readonly SnapshotFSBuilder fs;
    private readonly ProjectCollection baseCollection; // Go: base
    private readonly global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects;
    internal readonly ConfigFileRegistryBuilder ConfigFileRegistryBuilder; // Go: configFileRegistryBuilder

    // optional; used for project loading notifications
    private readonly Client? client;

    internal readonly ulong newSnapshotID;
    private bool programStructureChanged;
    private bool defaultProjectsInvalidated;
    private bool openFilesChanged;

    private Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>? fileDefaultProjects;
    private readonly Dirty.SyncMap<global::Ts.TsPath.Path, Project> configuredProjects;
    private readonly Dirty.Box<Project> inferredProject;

    private readonly ProjectCollection.APIState apiState;

    // Cross-type accessors (Go: same-package field access).
    internal RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> ParseCache => parseCache;

    // projectcollectionbuilder.go:56-86 — newProjectCollectionBuilder.
    internal static ProjectCollectionBuilder NewProjectCollectionBuilder(
        global::Ts.Core.RequestContext ctx,
        ulong newSnapshotID,
        SnapshotFSBuilder fs,
        ProjectCollection oldProjectCollection,
        ConfigFileRegistry oldConfigFileRegistry,
        ProjectCollection.APIState oldAPIState,
        global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects,
        SessionOptions sessionOptions,
        string customConfigFileName,
        RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache,
        ExtendedConfigCache extendedConfigCache,
        Client? client)
    {
        return new ProjectCollectionBuilder(
            ctx,
            newSnapshotID,
            fs,
            oldProjectCollection,
            oldConfigFileRegistry,
            oldAPIState,
            compilerOptionsForInferredProjects,
            sessionOptions,
            customConfigFileName,
            parseCache,
            extendedConfigCache,
            client);
    }

    private ProjectCollectionBuilder(
        global::Ts.Core.RequestContext ctx,
        ulong newSnapshotID,
        SnapshotFSBuilder fs,
        ProjectCollection oldProjectCollection,
        ConfigFileRegistry oldConfigFileRegistry,
        ProjectCollection.APIState oldAPIState,
        global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects,
        SessionOptions sessionOptions,
        string customConfigFileName,
        RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache,
        ExtendedConfigCache extendedConfigCache,
        Client? client)
    {
        this.ctx = ctx;
        this.fs = fs;
        toPath = fs.toPath;
        this.compilerOptionsForInferredProjects = compilerOptionsForInferredProjects;
        this.sessionOptions = sessionOptions;
        this.parseCache = parseCache;
        this.extendedConfigCache = extendedConfigCache;
        baseCollection = oldProjectCollection;
        // Go: lsproto.GetClientCapabilities(ctx) — C# registered deviation: the resolved
        // capabilities travel on SessionOptions (see LsProto.Missing.cs ClientCapabilitiesAccess).
        ConfigFileRegistryBuilder = ConfigFileRegistryBuilder.NewConfigFileRegistryBuilder(
            ClientCapabilitiesAccess.GetClientCapabilities(sessionOptions).Workspace.DidChangeWatchedFiles.RelativePatternSupport,
            fs,
            oldConfigFileRegistry,
            extendedConfigCache,
            newSnapshotID,
            sessionOptions,
            customConfigFileName,
            null);
        this.newSnapshotID = newSnapshotID;
        configuredProjects = new Dirty.SyncMap<global::Ts.TsPath.Path, Project>(oldProjectCollection.ConfiguredProjectsMap);
        inferredProject = new Dirty.Box<Project>(oldProjectCollection.InferredProjectValue!);
        apiState = oldAPIState.Clone();
        this.client = client;
    }

    // projectcollectionbuilder.go:88-130 — Finalize.
    internal (ProjectCollection ProjectCollection, ConfigFileRegistry ConfigFileRegistry) Finalize(global::Ts.Project.Logging.LogTree? logger)
    {
        _ = logger; // Go accepts a logger and does not use it.
        bool changed = false;
        ProjectCollection newProjectCollection = baseCollection;
        void EnsureCloned()
        {
            if (!changed)
            {
                newProjectCollection = newProjectCollection.Clone();
                changed = true;
            }
        }

        (Dictionary<global::Ts.TsPath.Path, Project>? configuredProjectsResult, bool configuredProjectsChanged) = configuredProjects.Finalize();
        if (configuredProjectsChanged)
        {
            EnsureCloned();
            newProjectCollection.configuredProjects = configuredProjectsResult!;
        }

        if (openFilesChanged)
        {
            EnsureCloned();
            newProjectCollection.openFiles = SnapshotUtil.OpenFilePaths(fs.overlays);
        }

        if (!MapsEqual(fileDefaultProjects, baseCollection.FileDefaultProjects))
        {
            EnsureCloned();
            newProjectCollection.fileDefaultProjects = fileDefaultProjects;
        }

        (Project newInferredProject, bool inferredProjectChanged) = inferredProject.Finalize();
        if (inferredProjectChanged)
        {
            EnsureCloned();
            newProjectCollection.inferredProject = newInferredProject;
        }

        ConfigFileRegistry configFileRegistry = ConfigFileRegistryBuilder.Finalize();
        if (!ReferenceEquals(configFileRegistry, baseCollection.Registry))
        {
            EnsureCloned();
            newProjectCollection.configFileRegistry = configFileRegistry;
        }

        if (!apiState.EqualTo(baseCollection.ApiState))
        {
            EnsureCloned();
            newProjectCollection.apiState = apiState;
        }

        return (newProjectCollection, configFileRegistry);
    }

    // projectcollectionbuilder.go:132-144 — forEachProject.
    private void ForEachProject(Func<DirtyValue<Project>, bool> fn)
    {
        bool keepGoing = true;
        configuredProjects.Range(entry =>
        {
            keepGoing = fn(entry);
            return keepGoing;
        });
        if (!keepGoing)
        {
            return;
        }
        if (inferredProject.Value() is not null)
        {
            fn(inferredProject);
        }
    }

    // projectcollectionbuilder.go:146-252 — HandleAPIRequest.
    internal Exception? HandleAPIRequest(Snapshot.APISnapshotRequest apiRequest, global::Ts.Project.Logging.LogTree? logger)
    {
        Dictionary<global::Ts.TsPath.Path, byte>? projectsToClose = null;
        if (apiRequest.CloseProjects is not null)
        {
            // map-iter: 序无关 — ref-counted close per project path.
            foreach (global::Ts.TsPath.Path projectPath in apiRequest.CloseProjects.Keys())
            {
                // Ref-counted close: only actually close the project once the last
                // API client that opened it releases it.
                int count = apiState.openProjects.GetValueOrDefault(projectPath);
                if (count > 1)
                {
                    apiState.openProjects[projectPath] = count - 1;
                }
                else if (count == 1)
                {
                    apiState.openProjects.Remove(projectPath);
                    projectsToClose ??= new Dictionary<global::Ts.TsPath.Path, byte>();
                    projectsToClose[projectPath] = 0;
                }
            }
        }

        if (apiRequest.OpenProjects is not null)
        {
            // map-iter: 序无关 — open/ref-count per config file name.
            foreach (string configFileName in apiRequest.OpenProjects.Keys())
            {
                global::Ts.TsPath.Path configPath = toPath(configFileName);
                Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? entry = FindOrCreateProject(configFileName, configPath, ProjectLoadKind.Create, logger);
                if (entry is not null)
                {
                    apiState.openProjects[configPath] = apiState.openProjects.GetValueOrDefault(configPath) + 1;
                    // A project re-opened in the same request shouldn't be closed.
                    projectsToClose?.Remove(configPath);
                }
                else
                {
                    return new InvalidOperationException($"project not found for open: {configFileName}");
                }
            }
        }

        if (apiRequest.CloseFiles is not null)
        {
            // map-iter: 序无关 — ref-counted close per file path.
            foreach (global::Ts.TsPath.Path path in apiRequest.CloseFiles.Keys())
            {
                // Ref-counted close mirroring projects above.
                if (apiState.openFiles.TryGetValue(path, out ProjectCollection.ApiOpenedFile? entry))
                {
                    if (entry.refCount > 1)
                    {
                        entry.refCount--;
                        apiState.openFiles[path] = entry;
                    }
                    else
                    {
                        apiState.openFiles.Remove(path);
                    }
                }
            }
        }

        if (apiRequest.OpenFiles is not null)
        {
            // map-iter: 序无关 — open/ref-count per URI.
            foreach (global::Ts.LsProto.DocumentUri uri in apiRequest.OpenFiles.Keys())
            {
                string fileName = uri.FileName();
                global::Ts.TsPath.Path path = toPath(fileName);
                ProjectCollection.ApiOpenedFile entry = apiState.openFiles.GetValueOrDefault(path) ?? new ProjectCollection.ApiOpenedFile();
                entry.fileName = fileName;
                entry.refCount++;
                apiState.openFiles[path] = entry;
            }
        }

        // map-iter: 序无关 — update pass over open API projects; the only cross-item
        // effect is early error exit.
        foreach (global::Ts.TsPath.Path configPath in apiState.openProjects.Keys)
        {
            if (FindConfiguredProject(configPath, ProjectLoadKind.Find) is { } entry)
            {
                UpdateProgram(entry, logger);
            }
            else
            {
                return new InvalidOperationException($"project not found for update: {configPath.Value}");
            }
        }

        // map-iter: 序无关 — per-overlay default-project probe.
        foreach (Overlay overlay in fs.overlays.Values)
        {
            if (FindDefaultConfiguredProject(overlay.FileName(), toPath(overlay.FileName())) is { } entry)
            {
                projectsToClose?.Remove(entry.Value()!.configFilePath);
            }
        }

        if (projectsToClose is not null)
        {
            // map-iter: 序无关 — per-path project deletion.
            foreach (global::Ts.TsPath.Path projectPath in projectsToClose.Keys)
            {
                if (configuredProjects.Load(projectPath) is { Ok: true } entry)
                {
                    DeleteConfiguredProject(entry.Entry!, logger);
                }
            }
        }

        // Ensure each API-opened file is placed like LSP's textDocument/didOpen: search
        // up ancestor directories for a configured project that contains it, and only
        // fall back to the inferred project if none is found. This also keeps already
        // loaded configured projects up to date. Then run the same cleanup the LSP open
        // path uses, so configured projects auto-loaded for files that are no longer open
        // are torn down instead of leaking.
        if (apiRequest.OpenFiles is not null || apiRequest.CloseFiles is not null)
        {
            var retain = new global::Ts.Collections.Set<global::Ts.TsPath.Path>();
            // map-iter: 序无关 — retain sets union across API-opened files.
            foreach ((global::Ts.TsPath.Path path, ProjectCollection.ApiOpenedFile file) in apiState.openFiles)
            {
                if (fs.IsOpenFile(path))
                {
                    // Already an LSP overlay; its project membership is handled by the
                    // overlay pass in cleanupConfiguredProjects.
                    continue;
                }
                SearchResult result = EnsureConfiguredProjectAndAncestorsForFile(file.fileName, path, logger);
                retain.Union(result.Retain);
            }
            CleanupConfiguredProjects(retain, logger);
            if (inferredProject.Value() is not null)
            {
                UpdateProgram(inferredProject, logger);
            }
        }

        return null;
    }

    // projectcollectionbuilder.go:254-330 — DidChangeFiles.
    internal void DidChangeFiles(FileChangeSummary summary, global::Ts.Project.Logging.LogTree? logger)
    {
        openFilesChanged = openFilesChanged || !string.IsNullOrEmpty(summary.Opened.Value) || summary.Closed.Len() > 0;

        var changedFiles = new List<global::Ts.TsPath.Path>(summary.Changed.Len());
        // map-iter: 序无关 — per-URI path collection.
        foreach (global::Ts.LsProto.DocumentUri uri in summary.Changed.Keys())
        {
            string fileName = uri.FileName();
            global::Ts.TsPath.Path path = toPath(fileName);
            changedFiles.Add(path);
        }

        global::Ts.Project.Logging.LogTree? configChangeLogger = logger.Fork("Checking for changes affecting config files");
        ConfigFileRegistryBuilder.ChangeFileResult configChangeResult = ConfigFileRegistryBuilder.DidChangeFiles(summary, configChangeLogger!);
        LogChangeFileResult(configChangeResult, configChangeLogger);

        programStructureChanged = MarkProjectsAffectedByConfigChanges(configChangeResult, logger);

        ForEachProject(entry =>
        {
            // Only consider change/delete; creates are handled by the config file registry
            if (summary.HasExcessiveNonCreateWatchEvents())
            {
                entry.Change(p =>
                {
                    p.IsDirty = true;
                    p.dirtyFilePath = default;
                    if (logger is not null)
                    {
                        logger.Logf("Marking project as dirty due to excessive watch changes: {0}", p.configFilePath.Value);
                    }
                });
                return true;
            }

            // Handle closed and changed files
            MarkFilesChanged(entry, changedFiles.ToArray(), FileChangeType.Changed, logger);
            if (entry.Value()!.Kind == Kind.Inferred && summary.Closed.Len() > 0)
            {
                Dictionary<global::Ts.TsPath.Path, string> rootFilesMap = entry.Value()!.CommandLine!.FileNamesByPath();
                string[] newRootFiles = entry.Value()!.CommandLine!.FileNames() ?? [];
                // map-iter: 序无关 — each closed URI removes at most its own entry.
                foreach (global::Ts.LsProto.DocumentUri uri in summary.Closed.Keys())
                {
                    string fileName = uri.FileName();
                    global::Ts.TsPath.Path path = toPath(fileName);
                    if (rootFilesMap.ContainsKey(path))
                    {
                        // Go: slices.Delete(newRootFiles, slices.Index(...), slices.Index(...)+1)
                        // — removes the first matching occurrence.
                        int index = Array.IndexOf(newRootFiles, fileName);
                        if (index >= 0)
                        {
                            newRootFiles = newRootFiles.Take(index).Concat(newRootFiles.Skip(index + 1)).ToArray();
                        }
                    }
                }
                UpdateInferredProjectRoots(newRootFiles, logger);
            }

            // Handle deleted files
            if (summary.Deleted.Len() > 0)
            {
                var deletedPaths = new List<global::Ts.TsPath.Path>(summary.Deleted.Len());
                // map-iter: 序无关 — per-URI path collection.
                foreach (global::Ts.LsProto.DocumentUri uri in summary.Deleted.Keys())
                {
                    string fileName = uri.FileName();
                    global::Ts.TsPath.Path path = toPath(fileName);
                    deletedPaths.Add(path);
                }
                MarkFilesChanged(entry, deletedPaths.ToArray(), FileChangeType.Deleted, logger);
            }

            // Handle created files
            if (summary.Created.Len() > 0)
            {
                var createdPaths = new List<global::Ts.TsPath.Path>(summary.Created.Len());
                // map-iter: 序无关 — per-URI path collection.
                foreach (global::Ts.LsProto.DocumentUri uri in summary.Created.Keys())
                {
                    string fileName = uri.FileName();
                    global::Ts.TsPath.Path path = toPath(fileName);
                    createdPaths.Add(path);
                }
                MarkFilesChanged(entry, createdPaths.ToArray(), FileChangeType.Created, logger);
            }

            return true;
        });

        // Handle opened file
        if (!string.IsNullOrEmpty(summary.Opened.Value) || !string.IsNullOrEmpty(summary.Reopened.Value))
        {
            string fileName = global::Ts.Core.CoreUtil.FirstNonZero(summary.Opened, summary.Reopened).FileName();
            global::Ts.TsPath.Path path = toPath(fileName);
            SearchResult openFileResult = EnsureConfiguredProjectAndAncestorsForFile(fileName, path, logger);
            CleanupConfiguredProjects(openFileResult.Retain, logger);
        }
    }

    // cleanupConfiguredProjects sweeps the loaded configured projects and unloads those
    // that are no longer needed. Starting from the set of all configured projects, it
    // retains any project that is the default project (along with its references and
    // ancestor configs) of an open overlay file or an API-opened file, any project
    // explicitly opened through the API, and any project in retain (e.g. the ancestor
    // solution tree built for a freshly opened overlay file). Every other configured
    // project is deleted, the inferred project roots are recomputed, and the config file
    // registry is cleaned up. This is the shared mechanism that keeps the set of loaded
    // projects minimal for both LSP file opens and API file opens/closes.
    // projectcollectionbuilder.go:332-409 — cleanupConfiguredProjects.
    private void CleanupConfiguredProjects(global::Ts.Collections.Set<global::Ts.TsPath.Path> retain, global::Ts.Project.Logging.LogTree? logger)
    {
        var toRemoveProjects = new global::Ts.Collections.Set<global::Ts.TsPath.Path>();
        configuredProjects.Range(entry =>
        {
            toRemoveProjects.Add(entry.Key());
            return true;
        });

        void RetainProjectAndReferences(Project project)
        {
            // Retain project
            toRemoveProjects.Delete(project.configFilePath);
            if (project.GetProgram() is { } program)
            {
                program.RangeResolvedProjectReferenceForLs((referencePath, _, _, _) =>
                {
                    if (configuredProjects.Load(referencePath).Ok)
                    {
                        toRemoveProjects.Delete(referencePath);
                    }
                    return true;
                });
            }
        }

        void RetainDefaultConfiguredProject(global::Ts.TsPath.Path openFilePath, Project project)
        {
            // Retain project and its references
            RetainProjectAndReferences(project);

            // Retain all the ancestor projects
            ConfigFileRegistryBuilder.ForEachConfigFileNameFor(openFilePath, configFileName =>
            {
                if (FindOrCreateProject(configFileName, toPath(configFileName), ProjectLoadKind.Find, logger) is { } ancestor)
                {
                    RetainProjectAndReferences(ancestor.Value()!);
                }
            });
        }

        List<string>? inferredProjectFiles = null;
        // map-iter: 序无关 — per-overlay retain/infer classification.
        foreach (Overlay overlay in fs.overlays.Values)
        {
            string openFile = overlay.FileName();
            global::Ts.TsPath.Path openFilePath = toPath(openFile);
            if (FindDefaultConfiguredProject(openFile, openFilePath) is { } p)
            {
                RetainDefaultConfiguredProject(openFilePath, p.Value()!);
            }
            else
            {
                inferredProjectFiles ??= [];
                inferredProjectFiles.Add(openFile);
            }
        }
        // Treat API-opened files like open files: retain their configured project (so
        // an LSP-driven open doesn't close it), or keep them as inferred project roots.
        // map-iter: 序无关 — per-API-file retain/infer classification.
        foreach ((global::Ts.TsPath.Path path, ProjectCollection.ApiOpenedFile file) in apiState.openFiles)
        {
            if (fs.IsOpenFile(path))
            {
                continue;
            }
            if (FindDefaultConfiguredProject(file.fileName, path) is { } p)
            {
                RetainDefaultConfiguredProject(path, p.Value()!);
            }
            else
            {
                inferredProjectFiles ??= [];
                inferredProjectFiles.Add(file.fileName);
            }
        }

        // map-iter: 序无关 — per-path retention/deletion decision.
        foreach (global::Ts.TsPath.Path projectPath in toRemoveProjects.Keys())
        {
            if (retain.Has(projectPath))
            {
                continue;
            }
            if (apiState.openProjects.ContainsKey(projectPath))
            {
                continue;
            }
            if (configuredProjects.Load(projectPath) is { Ok: true } p)
            {
                DeleteConfiguredProject(p.Entry!, logger);
            }
        }
        UpdateInferredProjectRoots(inferredProjectFiles?.ToArray() ?? [], logger);
        ConfigFileRegistryBuilder.Cleanup();
    }

    // projectcollectionbuilder.go:411-418 — logChangeFileResult.
    private static void LogChangeFileResult(ConfigFileRegistryBuilder.ChangeFileResult result, global::Ts.Project.Logging.LogTree? logger)
    {
        if ((result.affectedProjects?.Count ?? 0) > 0)
        {
            // Go: %v over map keys — log-only, keep a stable ordinal rendering.
            var keys = new List<string>();
            // map-iter: 已排序 — rendering only.
            foreach (global::Ts.TsPath.Path key in result.affectedProjects!.Keys)
            {
                keys.Add(key.Value);
            }
            keys.Sort(StringComparer.Ordinal);
            logger!.Logf("Config file change affected projects: [{0}]", string.Join(" ", keys));
        }
        if ((result.affectedFiles?.Count ?? 0) > 0)
        {
            logger!.Logf("Config file change affected config file lookups for {0} files", result.affectedFiles!.Count);
        }
    }

    // projectcollectionbuilder.go:420-428 — collectInferredProjectRoots.
    private List<string> CollectInferredProjectRoots()
    {
        List<string>? inferredProjectFiles = null;
        // map-iter: 序无关 — per-overlay classification.
        foreach ((global::Ts.TsPath.Path path, Overlay overlay) in fs.overlays)
        {
            if (FindDefaultConfiguredProject(overlay.FileName(), path) is null)
            {
                inferredProjectFiles ??= [];
                inferredProjectFiles.Add(overlay.FileName());
            }
        }
        return AppendAPIOpenedInferredRoots(inferredProjectFiles ?? []);
    }

    // appendAPIOpenedInferredRoots appends API-opened files that aren't open in an
    // overlay and have no configured project, so they're kept as inferred project
    // roots and persist across snapshots.
    // projectcollectionbuilder.go:430-443 — appendAPIOpenedInferredRoots.
    private List<string> AppendAPIOpenedInferredRoots(List<string> inferredProjectFiles)
    {
        // map-iter: 序无关 — per-API-file classification.
        foreach ((global::Ts.TsPath.Path path, ProjectCollection.ApiOpenedFile file) in apiState.openFiles)
        {
            if (fs.IsOpenFile(path))
            {
                continue;
            }
            if (FindDefaultConfiguredProject(file.fileName, path) is null)
            {
                inferredProjectFiles.Add(file.fileName);
            }
        }
        return inferredProjectFiles;
    }

    // projectcollectionbuilder.go:445-447 — cleanupInferredProject.
    private void CleanupInferredProject(global::Ts.Project.Logging.LogTree? logger)
    {
        UpdateInferredProjectRoots(CollectInferredProjectRoots().ToArray(), logger);
    }

    // projectcollectionbuilder.go:449-457 — ensureInferredProjectIncludesClosedFile.
    private void EnsureInferredProjectIncludesClosedFile(string fileName, global::Ts.Project.Logging.LogTree? logger)
    {
        // Collect existing inferred project roots (open files not in configured projects)
        // plus this closed file.
        List<string> inferredProjectFiles = CollectInferredProjectRoots();
        inferredProjectFiles.Add(fileName);
        UpdateInferredProjectRoots(inferredProjectFiles.ToArray(), logger);
        if (inferredProject.Value() is not null)
        {
            UpdateProgram(inferredProject, logger);
        }
    }

    // DidRequestFile ensures projects are loaded for the given URI.
    // If configuredProjectsOnly is true, only configured projects are loaded; no inferred project is created
    // and it is not guaranteed that there will be any project containing the file in the resulting snapshot.
    // projectcollectionbuilder.go:459-524 — DidRequestFile.
    internal void DidRequestFile(global::Ts.LsProto.DocumentUri uri, bool configuredProjectsOnly, global::Ts.Project.Logging.LogTree? logger)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        string fileName = uri.FileName();
        global::Ts.TsPath.Path path = toPath(fileName);
        if (defaultProjectsInvalidated)
        {
            EnsureConfiguredProjectAndAncestorsForFile(fileName, path, logger);
            if (!fs.IsOpenFile(path))
            {
                FinishLog();
                return;
            }
        }
        if (fs.IsOpenFile(path))
        {
            bool hasChanges = programStructureChanged;

            // See if we can find a default project without updating a bunch of stuff.
            DirtyValue<Project>? result = FindDefaultProject(fileName, path);
            if (result is not null)
            {
                hasChanges = UpdateProgram(result, logger) || hasChanges;
                if (result.Value() is not null)
                {
                    if (hasChanges)
                    {
                        CleanupInferredProject(logger);
                        if (inferredProject.Value() is not null)
                        {
                            UpdateProgram(inferredProject, logger);
                        }
                    }
                    FinishLog();
                    return;
                }
            }

            // Make sure all projects we know about are up to date...
            configuredProjects.Range(entry =>
            {
                hasChanges = UpdateProgram(entry, logger) || hasChanges;
                return true;
            });
            if (hasChanges)
            {
                // If the structure of other projects changed, we might need to move files
                // in/out of the inferred project.
                CleanupInferredProject(logger);
            }

            if (inferredProject.Value() is not null)
            {
                UpdateProgram(inferredProject, logger);
            }

            // At this point we should be able to find the default project for the file without
            // creating anything else. Initially, I verified that and panicked if nothing was found,
            // but that panic was getting triggered by fourslash infrastructure when it told us to
            // open a package.json file. This is something the VS Code client would never do, but
            // it seems possible that another client would. There's no point in panicking; we don't
            // really even have an error condition until it tries to ask us language questions about
            // a non-TS-handleable file.
        }
        else
        {
            SearchResult result = EnsureConfiguredProjectAndAncestorsForFile(fileName, path, logger);
            if (result.Project is null && !configuredProjectsOnly)
            {
                // No configured project found for this closed file.
                // Add it to the inferred project so language service requests can be served.
                EnsureInferredProjectIncludesClosedFile(fileName, logger);
            }
        }

        FinishLog();

        void FinishLog()
        {
            if (logger is not null)
            {
                logger.Log($"Completed file request for {fileName} in {startTime.Elapsed}");
            }
        }
    }

    // projectcollectionbuilder.go:526-543 — DidRequestProject.
    internal void DidRequestProject(global::Ts.TsPath.Path projectId, global::Ts.Project.Logging.LogTree? logger)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        if (projectId.Value == Project.InferredProjectName)
        {
            // Update inferred project
            if (inferredProject.Value() is not null)
            {
                UpdateProgram(inferredProject, logger);
            }
        }
        else
        {
            if (configuredProjects.Load(projectId) is { Ok: true } entry)
            {
                UpdateProgram(entry.Entry!, logger);
            }
        }

        if (logger is not null)
        {
            logger.Log($"Completed project update request for {projectId.Value} in {startTime.Elapsed}");
        }
    }

    // projectcollectionbuilder.go:545-574 — DidRequestProjectTrees.
    internal void DidRequestProjectTrees(Snapshot.ProjectTreeRequest projectTreeRequest, global::Ts.Project.Logging.LogTree? logger)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();

        var currentProjects = new List<global::Ts.TsPath.Path>();
        configuredProjects.Range(sme =>
        {
            currentProjects.Add(sme.Key());
            return true;
        });

        var seenProjects = new global::Ts.Collections.SyncSet<global::Ts.TsPath.Path>();
        global::Ts.Core.IWorkGroup wg = global::Ts.Core.WorkGroup.Create(false);
        // map-iter: 序无关 — each project's tree is discovered independently under the WorkGroup.
        foreach (global::Ts.TsPath.Path projectId in currentProjects)
        {
            global::Ts.TsPath.Path capturedProjectId = projectId;
            wg.Queue(() =>
            {
                if (configuredProjects.Load(capturedProjectId) is { Ok: true } loadedEntry && loadedEntry.Entry is not null)
                {
                    Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project> entry = loadedEntry.Entry;
                    // If this project has potential project reference for any of the project we are loading ancestor tree for
                    // load this project first
                    if (entry.Value() is { } project && (projectTreeRequest.IsAllProjects() || project.HasPotentialProjectReference(projectTreeRequest)))
                    {
                        UpdateProgram(entry, logger);
                    }
                    EnsureProjectTree(wg, entry, projectTreeRequest, seenProjects, logger);
                }
            });
        }
        wg.RunAndWait();

        if (logger is not null)
        {
            logger.Log($"Completed project tree request for {SnapshotUtil.FormatPaths(projectTreeRequest.Projects())} in {startTime.Elapsed}");
        }
    }

    // projectcollectionbuilder.go:576-628 — ensureProjectTree.
    private void EnsureProjectTree(
        global::Ts.Core.IWorkGroup wg,
        Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project> entry,
        Snapshot.ProjectTreeRequest projectTreeRequest,
        global::Ts.Collections.SyncSet<global::Ts.TsPath.Path> seenProjects,
        global::Ts.Project.Logging.LogTree? logger)
    {
        if (!seenProjects.AddIfAbsent(entry.Key()))
        {
            return;
        }

        Project? project = entry.Value();
        if (project is null)
        {
            return;
        }

        global::Ts.Compiler.Program? program = project.GetProgram();
        if (program is null)
        {
            return;
        }

        // If this project disables child load ignore it
        if (program.CommandLine().CompilerOptions()!.DisableReferencedProjectLoad.IsTrue())
        {
            return;
        }

        global::Ts.TsOptions.ParsedCommandLine[]? children = program.GetResolvedProjectReferences();
        if (children is null)
        {
            return;
        }
        foreach (global::Ts.TsOptions.ParsedCommandLine? childConfig in children)
        {
            if (childConfig is null)
            {
                continue;
            }
            global::Ts.TsOptions.ParsedCommandLine capturedChildConfig = childConfig;
            wg.Queue(() =>
            {
                if (!projectTreeRequest.IsAllProjects() && program.RangeResolvedProjectReferenceInChildConfigForLs(
                    capturedChildConfig,
                    (referencePath, _, _, _) => !projectTreeRequest.IsProjectReferenced(referencePath)))
                {
                    return;
                }

                // Load this child project since this is referenced
                Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? childProjectEntry = FindOrCreateProject(
                    capturedChildConfig.ConfigName(),
                    capturedChildConfig.ConfigFile!.SourceFile.Path(),
                    ProjectLoadKind.Create,
                    logger);
                UpdateProgram(childProjectEntry!, logger);

                // Ensure children for this project
                EnsureProjectTree(wg, childProjectEntry!, projectTreeRequest, seenProjects, logger);
            });
        }
    }

    // projectcollectionbuilder.go:630-673 — DidUpdateATAState.
    internal void DidUpdateATAState(Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> ataChanges, global::Ts.Project.Logging.LogTree? logger)
    {
        void UpdateProject(DirtyValue<Project> project, Snapshot.ATAStateChange ataChange)
        {
            project.ChangeIf(
                p =>
                {
                    if (p is null)
                    {
                        return false;
                    }
                    // Consistency check: the ATA demands (project options, unresolved imports) of this project
                    // has not changed since the time the ATA request was dispatched; the change can still be
                    // applied to this project in its current state.
                    return ataChange.TypingsInfo!.Equals(p.ComputeTypingsInfo());
                },
                p =>
                {
                    // We checked before triggering this change (in Session.triggerATAForUpdatedProjects) that
                    // the set of typings files is actually different.
                    p.installedTypingsInfo = ataChange.TypingsInfo;
                    p.typingsFiles = ataChange.TypingsFiles;
                    PatternsAndIgnored typingsWatchGlobs = WatchGlobMappers.GetTypingsLocationsGlobs(
                        ataChange.TypingsFilesToWatch,
                        sessionOptions.TypingsLocation,
                        sessionOptions.CurrentDirectory,
                        p.CurrentDirectory,
                        fs.fs.UseCaseSensitiveFileNames());
                    p.typingsWatch = p.typingsWatch!.Clone(typingsWatchGlobs);
                    p.IsDirty = true;
                    p.dirtyFilePath = default;
                });
        }

        // map-iter: 序无关 — per-project ATA state application.
        foreach ((global::Ts.TsPath.Path projectPath, Snapshot.ATAStateChange ataChange) in ataChanges)
        {
            logger.Embed(ataChange.Logs!);
            if (projectPath.Value == Project.InferredProjectName)
            {
                UpdateProject(inferredProject, ataChange);
            }
            else if (configuredProjects.Load(projectPath) is { Ok: true } project)
            {
                UpdateProject(project.Entry!, ataChange);
            }

            if (logger is not null)
            {
                logger.Log($"Updated ATA state for project {projectPath.Value}");
            }
        }
    }

    // if customConfigFileName changes, invalidate default projects.
    // projectcollectionbuilder.go:675-684 — DidChangeCustomConfigFileName.
    internal void DidChangeCustomConfigFileName(global::Ts.Project.Logging.LogTree? logger)
    {
        if (!ConfigFileRegistryBuilder.DidChangeCustomConfigFileName(logger!))
        {
            return;
        }

        fileDefaultProjects = null;
        defaultProjectsInvalidated = true;
        programStructureChanged = true;
    }

    // projectcollectionbuilder.go:686-716 — markProjectsAffectedByConfigChanges.
    private bool MarkProjectsAffectedByConfigChanges(
        ConfigFileRegistryBuilder.ChangeFileResult configChangeResult,
        global::Ts.Project.Logging.LogTree? logger)
    {
        if (configChangeResult.affectedProjects is not null)
        {
            // map-iter: 序无关 — per-affected-project dirty marking.
            foreach (global::Ts.TsPath.Path projectPath in configChangeResult.affectedProjects.Keys)
            {
                if (configuredProjects.Load(projectPath) is not { Ok: true } project)
                {
                    throw new InvalidOperationException($"project {projectPath.Value} affected by config change not found");
                }
                project.Entry!.ChangeIf(
                    p => !p!.IsDirty || !string.IsNullOrEmpty(p.dirtyFilePath.Value),
                    p =>
                    {
                        p!.IsDirty = true;
                        p.dirtyFilePath = default;
                        if (logger is not null)
                        {
                            logger.Logf("Marking project {0} as dirty due to change affecting config", projectPath.Value);
                        }
                    });
            }
        }

        // Recompute default projects for open files that now have different config file presence.
        bool hasChanges = false;
        if (configChangeResult.affectedFiles is not null)
        {
            // map-iter: 序无关 — per-affected-file recompute.
            foreach (global::Ts.TsPath.Path path in configChangeResult.affectedFiles.Keys)
            {
                string fileName = fs.overlays[path].FileName();
                _ = EnsureConfiguredProjectAndAncestorsForFile(fileName, path, logger);
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    // projectcollectionbuilder.go:718-733 — findDefaultProject.
    private DirtyValue<Project>? FindDefaultProject(string fileName, global::Ts.TsPath.Path path)
    {
        if (FindDefaultConfiguredProject(fileName, path) is { } configuredProject)
        {
            return configuredProject;
        }
        if (fileDefaultProjects is not null && fileDefaultProjects.TryGetValue(path, out global::Ts.TsPath.Path key) && key.Value == Project.InferredProjectName)
        {
            return inferredProject;
        }
        if (inferredProject.Value() is { } inferred && inferred.ContainsFile(path))
        {
            fileDefaultProjects ??= new Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>();
            fileDefaultProjects[path] = new global::Ts.TsPath.Path(Project.InferredProjectName);
            return inferredProject;
        }
        return null;
    }

    // projectcollectionbuilder.go:735-762 — findDefaultConfiguredProject.
    private Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? FindDefaultConfiguredProject(string fileName, global::Ts.TsPath.Path path)
    {
        if (fileDefaultProjects is not null && fileDefaultProjects.TryGetValue(path, out global::Ts.TsPath.Path key) && key.Value != Project.InferredProjectName)
        {
            if (configuredProjects.Load(key) is { Ok: true } entry)
            {
                return entry.Entry;
            }
        }
        // Sort configured projects so we can use a deterministic "first" as a last resort.
        var configuredProjectPaths = new List<global::Ts.TsPath.Path>();
        var configuredProjectsMap = new Dictionary<global::Ts.TsPath.Path, Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>>();
        configuredProjects.Range(entry =>
        {
            configuredProjectPaths.Add(entry.Key());
            configuredProjectsMap[entry.Key()] = entry;
            return true;
        });
        configuredProjectPaths.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));

        (global::Ts.TsPath.Path project, bool multipleCandidates) = ProjectCollection.FindDefaultConfiguredProjectFromProgramInclusion(
            fileName,
            path,
            configuredProjectPaths.ToArray(),
            p => configuredProjectsMap[p].Value());

        if (multipleCandidates)
        {
            if (FindOrCreateDefaultConfiguredProjectForFile(fileName, path, ProjectLoadKind.Find, null).Project is { } p)
            {
                return p;
            }
        }

        return configuredProjectsMap.GetValueOrDefault(project);
    }

    // projectcollectionbuilder.go:764-770 — ensureConfiguredProjectAndAncestorsForFile.
    private SearchResult EnsureConfiguredProjectAndAncestorsForFile(string fileName, global::Ts.TsPath.Path path, global::Ts.Project.Logging.LogTree? logger)
    {
        SearchResult result = FindOrCreateDefaultConfiguredProjectForFile(fileName, path, ProjectLoadKind.Create, logger);
        if (result.Project is not null && fs.IsOpenFile(path))
        {
            CreateAncestorTree(fileName, path, result, logger);
        }
        return result;
    }

    // projectcollectionbuilder.go:772-809 — createAncestorTree.
    private void CreateAncestorTree(string fileName, global::Ts.TsPath.Path path, SearchResult openResult, global::Ts.Project.Logging.LogTree? logger)
    {
        Project project = openResult.Project!.Value()!;
        while (true)
        {
            // Skip if project is not composite and we are only looking for solution
            if (project.CommandLine is not null &&
                (!project.CommandLine.CompilerOptions()!.Composite.IsTrue() ||
                    project.CommandLine.CompilerOptions()!.DisableSolutionSearching.IsTrue()))
            {
                return;
            }

            // Get config file name
            string ancestorConfigName = ConfigFileRegistryBuilder.GetAncestorConfigFileName(fileName, path, project.Name(), logger!);
            if (ancestorConfigName == "")
            {
                return;
            }

            // find or delay load the project
            global::Ts.TsPath.Path ancestorPath = toPath(ancestorConfigName);
            Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? ancestor = FindOrCreateProject(ancestorConfigName, ancestorPath, ProjectLoadKind.Create, logger);
            if (ancestor is null)
            {
                return;
            }

            openResult.Retain.Add(ancestorPath);

            // If this ancestor is new and was not updated because we are just creating it for future loading
            // eg when invoking find all references or rename that could span multiple projects
            // we would make the current project as its potential project reference
            if (ancestor.Value()!.CommandLine is null &&
                (project.CommandLine is null || project.CommandLine.CompilerOptions()!.Composite.IsTrue()))
            {
                ancestor.Change(ancestorProject =>
                {
                    ancestorProject!.SetPotentialProjectReference(project.configFilePath);
                });
            }

            project = ancestor.Value()!;
        }
    }

    // projectcollectionbuilder.go:811-825 — searchNode / searchNodeKey / searchResult.
    private readonly record struct SearchNode(string ConfigFileName, ProjectLoadKind LoadKind, global::Ts.Project.Logging.LogTree? Logger);

    private readonly record struct SearchNodeKey(string ConfigFileName, ProjectLoadKind LoadKind);

    private sealed class SearchResult
    {
        internal Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? Project;

        internal global::Ts.Collections.Set<global::Ts.TsPath.Path> Retain = new();
    }

    // projectcollectionbuilder.go:827-983 — findOrCreateDefaultConfiguredProjectWorker.
    private SearchResult FindOrCreateDefaultConfiguredProjectWorker(
        string fileName,
        global::Ts.TsPath.Path path,
        string configFileName,
        ProjectLoadKind loadKind,
        global::Ts.Collections.SyncSet<SearchNodeKey>? visited,
        SearchResult? fallback,
        global::Ts.Project.Logging.LogTree? logger)
    {
        var configs = new global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine>();
        visited ??= new global::Ts.Collections.SyncSet<SearchNodeKey>();

        global::Ts.Core.BreadthFirstSearchResult<SearchNode> search = global::Ts.Core.Bfs.BreadthFirstSearchParallelEx(
            new SearchNode(configFileName, loadKind, logger),
            node =>
            {
                if (configs.TryLoad(toPath(node.ConfigFileName), out global::Ts.TsOptions.ParsedCommandLine? config) &&
                    (config.ProjectReferences()?.Length ?? 0) > 0)
                {
                    ProjectLoadKind referenceLoadKind = node.LoadKind;
                    if (config.CompilerOptions()!.DisableReferencedProjectLoad.IsTrue())
                    {
                        referenceLoadKind = ProjectLoadKind.Find;
                    }

                    global::Ts.Project.Logging.LogTree? refLogger = null;
                    string[] references = config.ResolvedProjectReferencePaths() ?? [];
                    if (references.Length > 0 && node.Logger is not null)
                    {
                        refLogger = node.Logger.Fork($"Searching {references.Length} project references of {node.ConfigFileName}");
                    }
                    // map-iter: 序无关 — reference fan-out order is handled by the BFS.
                    return global::Ts.Core.CoreUtil.Map(references, referenceConfigFileName => new SearchNode(
                        referenceConfigFileName,
                        referenceLoadKind,
                        refLogger!.Fork("Searching project reference " + referenceConfigFileName)))!;
                }
                return [];
            },
            node =>
            {
                global::Ts.TsPath.Path configFilePath = toPath(node.ConfigFileName);
                global::Ts.TsOptions.ParsedCommandLine? config = ConfigFileRegistryBuilder.FindOrAcquireConfigForFile(
                    node.ConfigFileName,
                    configFilePath,
                    path,
                    node.LoadKind,
                    node.Logger!.Fork("Acquiring config for open file")!);
                if (config is null)
                {
                    node.Logger.Log("Config file for project does not already exist");
                    return (false, false);
                }
                configs.Store(configFilePath, config);
                if ((config.FileNames()?.Length ?? 0) == 0)
                {
                    // Likely a solution tsconfig.json - the search will fan out to its references.
                    node.Logger.Log("Project does not contain file (no root files)");
                    return (false, false);
                }

                if (config.CompilerOptions()!.Composite == global::Ts.Core.Tristate.True)
                {
                    // For composite projects, we can get an early negative result.
                    // !!! what about declaration files in node_modules? wouldn't it be better to
                    //     check project inclusion if the project is already loaded?
                    if (!config.FileNamesByPath().ContainsKey(path))
                    {
                        node.Logger.Log("Project does not contain file (by composite config inclusion)");
                        return (false, false);
                    }
                }

                Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? project = FindOrCreateProject(node.ConfigFileName, configFilePath, node.LoadKind, node.Logger);
                if (project is null)
                {
                    node.Logger.Log("Project does not already exist");
                    return (false, false);
                }

                if (node.LoadKind == ProjectLoadKind.Create)
                {
                    // Ensure project is up to date before checking for file inclusion
                    UpdateProgram(project, node.Logger);
                }

                if (project.Value()!.ContainsFile(path))
                {
                    bool isDirectInclusion = !project.Value()!.IsSourceFromProjectReference(path);
                    if (node.Logger is not null)
                    {
                        node.Logger.Logf("Project contains file {0}", global::Ts.Core.CoreUtil.IfElse(isDirectInclusion, "directly", "as a source of a referenced project"));
                    }
                    return (true, isDirectInclusion);
                }

                node.Logger.Log("Project does not contain file");
                return (false, false);
            },
            new global::Ts.Core.BreadthFirstSearchOptions<SearchNodeKey, SearchNode>
            {
                Visited = visited,
                PreprocessLevel = level =>
                {
                    level.Range(node =>
                    {
                        if (node.LoadKind == ProjectLoadKind.Find && level.Has(new SearchNodeKey(node.ConfigFileName, ProjectLoadKind.Create)))
                        {
                            // Remove find requests when a create request for the same project is already present.
                            level.Delete(new SearchNodeKey(node.ConfigFileName, node.LoadKind));
                        }
                        return true;
                    });
                },
            },
            node => new SearchNodeKey(node.ConfigFileName, node.LoadKind));

        var retain = new global::Ts.Collections.Set<global::Ts.TsPath.Path>();
        Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? projectResult = null;
        if (search.Path is { Length: > 0 })
        {
            (projectResult, _) = configuredProjects.Load(toPath(search.Path[0].ConfigFileName));
            // If we found a project, we retain each project along the BFS path.
            // We don't want to retain everything we visited since BFS can terminate
            // early, and we don't want to retain nondeterministically.
            // map-iter: 序无关 — path order is the BFS-determined retain order.
            foreach (SearchNode node in search.Path)
            {
                retain.Add(toPath(node.ConfigFileName));
            }
        }

        if (search.Stopped)
        {
            // Found a project that directly contains the file.
            return new SearchResult
            {
                Project = projectResult,
                Retain = retain,
            };
        }

        if (projectResult is not null)
        {
            // If we found a project that contains the file, but it is a source from
            // a project reference, record it as a fallback.
            fallback = new SearchResult
            {
                Project = projectResult,
                Retain = retain,
            };
        }

        // Look for tsconfig.json files higher up the directory tree and do the same. This handles
        // the common case where a higher-level "solution" tsconfig.json contains all projects in a
        // workspace.
        if (configs.TryLoad(toPath(configFileName), out global::Ts.TsOptions.ParsedCommandLine? searchedConfig) &&
            searchedConfig.CompilerOptions()!.DisableSolutionSearching.IsTrue())
        {
            if (fallback is not null)
            {
                return fallback;
            }
        }
        string ancestorConfigName = ConfigFileRegistryBuilder.GetAncestorConfigFileName(fileName, path, configFileName, logger!);
        if (ancestorConfigName != "")
        {
            return FindOrCreateDefaultConfiguredProjectWorker(
                fileName,
                path,
                ancestorConfigName,
                loadKind,
                visited,
                fallback,
                logger!.Fork("Searching ancestor config file at " + ancestorConfigName));
        }
        if (fallback is not null)
        {
            return fallback;
        }
        // If we didn't find anything, we can retain everything we visited,
        // since the whole graph must have been traversed (i.e., the set of
        // retained projects is guaranteed to be deterministic).
        // map-iter: 序无关 — set union over visited nodes; deterministic because the
        // whole graph was traversed.
        visited.Range(node =>
        {
            retain.Add(toPath(node.ConfigFileName));
            return true;
        });
        return new SearchResult { Retain = retain };
    }

    // projectcollectionbuilder.go:985-1027 — findOrCreateDefaultConfiguredProjectForFile.
    private SearchResult FindOrCreateDefaultConfiguredProjectForFile(
        string fileName,
        global::Ts.TsPath.Path path,
        ProjectLoadKind loadKind,
        global::Ts.Project.Logging.LogTree? logger)
    {
        if (fileDefaultProjects is not null && fileDefaultProjects.TryGetValue(path, out global::Ts.TsPath.Path key))
        {
            if (key.Value == Project.InferredProjectName)
            {
                // The file belongs to the inferred project
                return new SearchResult();
            }
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? entry, _) = configuredProjects.Load(key);
            return new SearchResult { Project = entry };
        }
        string configFileName = ConfigFileRegistryBuilder.GetConfigFileNameForFile(fileName, path, logger!);
        if (configFileName != "")
        {
            var startTime = System.Diagnostics.Stopwatch.StartNew();
            SearchResult result = FindOrCreateDefaultConfiguredProjectWorker(
                fileName,
                path,
                configFileName,
                loadKind,
                null,
                null,
                logger.Fork("Searching for default configured project for " + fileName));
            if (result.Project is not null)
            {
                fileDefaultProjects ??= new Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>();
                fileDefaultProjects[path] = result.Project.Value()!.configFilePath;
            }
            if (logger is not null)
            {
                if (result.Project is not null)
                {
                    logger.Log($"Found default configured project for {fileName}: {result.Project.Value()!.Name()} (in {startTime.Elapsed})");
                }
                else
                {
                    logger.Log($"No default configured project found for {fileName} (searched in {startTime.Elapsed})");
                }
            }
            return result;
        }
        return new SearchResult();
    }

    // projectcollectionbuilder.go:1029-1041 — findOrCreateProject.
    private Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? FindOrCreateProject(
        string configFileName,
        global::Ts.TsPath.Path configFilePath,
        ProjectLoadKind loadKind,
        global::Ts.Project.Logging.LogTree? logger)
    {
        if (loadKind == ProjectLoadKind.Find)
        {
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? found, _) = configuredProjects.Load(configFilePath);
            return found;
        }
        (Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? entry, _) = configuredProjects.LoadOrStore(
            configFilePath,
            Project.NewConfiguredProject(configFileName, configFilePath, this, logger));
        return entry;
    }

    // Go: configuredProjects.Load (dirty.SyncMap.Load → C# (Entry, bool) tuple). A plain
    // `is { Ok: true }` pattern cannot deconstruct a tuple, so the lookup goes through here.
    private Dirty.SyncMapEntry<global::Ts.TsPath.Path, Project>? FindConfiguredProject(
        global::Ts.TsPath.Path configFilePath,
        ProjectLoadKind loadKind)
    {
        return FindOrCreateProject("", configFilePath, loadKind, null);
    }

    // projectcollectionbuilder.go:1043-1083 — updateInferredProjectRoots.
    private bool UpdateInferredProjectRoots(string[] rootFileNames, global::Ts.Project.Logging.LogTree? logger)
    {
        if (rootFileNames.Length == 0)
        {
            if (inferredProject.Value() is not null)
            {
                if (logger is not null)
                {
                    logger.Log("Deleting inferred project");
                }
                inferredProject.Delete();
                return true;
            }
            return false;
        }

        Array.Sort(rootFileNames, StringComparer.Ordinal);
        if (inferredProject.Value() is null)
        {
            inferredProject.Set(Project.NewInferredProject(sessionOptions.CurrentDirectory, compilerOptionsForInferredProjects, rootFileNames, this, logger));
        }
        else
        {
            global::Ts.Core.CompilerOptions newCompilerOptions = inferredProject.Value()!.CommandLine!.CompilerOptions()!;
            if (compilerOptionsForInferredProjects is not null)
            {
                newCompilerOptions = compilerOptionsForInferredProjects;
            }
            var newCommandLine = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(newCompilerOptions, rootFileNames, new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = fs.fs.UseCaseSensitiveFileNames(),
                CurrentDirectory = sessionOptions.CurrentDirectory,
            });
            bool changed = inferredProject.ChangeIf(
                p => !MapsEqual(p!.CommandLine!.FileNamesByPath(), newCommandLine.FileNamesByPath()),
                p =>
                {
                    if (logger is not null)
                    {
                        logger.Log($"Updating inferred project config with {rootFileNames.Length} root files");
                    }
                    p!.SetCommandLine(newCommandLine);
                });
            if (!changed)
            {
                return false;
            }
        }
        return true;
    }

    // updateProgram updates the program for the given project entry if necessary. It returns
    // a boolean indicating whether the update could have caused any structure-affecting changes.
    // projectcollectionbuilder.go:1085-1167 — updateProgram.
    private bool UpdateProgram(DirtyValue<Project> entry, global::Ts.Project.Logging.LogTree? logger)
    {
        bool updateProgram = false;
        bool deleteProject = false;
        bool filesChanged = false;
        string configFileName = entry.Value()!.Name();
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        bool notifiedLoading = false;
        string displayName = "";
        entry.Locked(lockedEntry =>
        {
            if (lockedEntry.Value()!.Kind == Kind.Configured)
            {
                global::Ts.TsOptions.ParsedCommandLine? commandLine = ConfigFileRegistryBuilder.AcquireConfigForProject(
                    lockedEntry.Value()!.Name(),
                    lockedEntry.Value()!.configFilePath,
                    lockedEntry.Value()!,
                    logger!.Fork("Acquiring config for project")!);
                if (commandLine is null)
                {
                    deleteProject = true;
                    filesChanged = true;
                    return;
                }
                if (!ReferenceEquals(lockedEntry.Value()!.CommandLine, commandLine))
                {
                    updateProgram = true;
                    lockedEntry.Change(p =>
                    {
                        p!.SetCommandLine(commandLine);
                    });
                }
            }
            if (!updateProgram)
            {
                updateProgram = lockedEntry.Value()!.IsDirty;
            }
            if (updateProgram)
            {
                if (client is not null)
                {
                    displayName = lockedEntry.Value()!.DisplayName(sessionOptions.CurrentDirectory);
                    notifiedLoading = true;
                }
            }
        });
        if (notifiedLoading && client is not null)
        {
            client.ProgressStart(global::Ts.Diagnostics.Diagnostics.Project_0, displayName);
        }
        if (deleteProject)
        {
            DeleteConfiguredProject(entry, logger);
        }
        if (updateProgram)
        {
            entry.Locked(lockedEntry =>
            {
                lockedEntry.Change(project =>
                {
                    ProjectCompilerHost? oldHost = project!.host;
                    global::Ts.Compiler.Program? oldProgram = project.Program;
                    CheckerPool? oldCheckerPool = project.checkerPool;
                    project.host = ProjectCompilerHost.NewCompilerHost(project.CurrentDirectory, project, this, logger!.Fork("CompilerHost"));
                    Project.CreateProgramResult result = project.CreateProgram();
                    project.Program = result.Program;
                    project.checkerPool = (CheckerPool)result.Program.GetCheckerPool();
                    project.ProgramUpdateKind = result.UpdateKind;
                    project.ProgramLastUpdate = newSnapshotID;
                    if (result.UpdateKind == ProgramUpdateKind.Cloned)
                    {
                        project.host.sourceFS.seenFiles = oldHost!.sourceFS.seenFiles;
                    }
                    if (result.UpdateKind == ProgramUpdateKind.NewFiles)
                    {
                        filesChanged = true;
                        project.programFilesWatch = project.CloneWatchers();
                    }
                    project.IsDirty = false;
                    project.dirtyFilePath = default;
                    ReleaseDroppedProjectReferences(oldProgram, result.Program, project.configFilePath);
                    if (oldCheckerPool is not null)
                    {
                        oldCheckerPool.Discard();
                    }
                });
            });
        }
        if (notifiedLoading && client is not null)
        {
            client.ProgressFinish(global::Ts.Diagnostics.Diagnostics.Project_0, displayName);
        }
        if (updateProgram && logger is not null)
        {
            logger.Log($"Program update for {configFileName} completed in {startTime.Elapsed}");
        }
        return filesChanged;
    }

    // projectcollectionbuilder.go:1169-1221 — markFilesChanged.
    private void MarkFilesChanged(DirtyValue<Project> entry, global::Ts.TsPath.Path[] paths, FileChangeType changeType, global::Ts.Project.Logging.LogTree? logger)
    {
        bool dirty = false;
        global::Ts.TsPath.Path dirtyFilePath = default;
        entry.ChangeIf(
            p =>
            {
                if (p!.Program is null || (p.IsDirty && string.IsNullOrEmpty(p.dirtyFilePath.Value)))
                {
                    return false;
                }

                dirtyFilePath = p.dirtyFilePath;
                // map-iter: 序无关 — path scan; first mismatch short-circuits deterministically
                // because the outcome depends only on the set of paths.
                foreach (global::Ts.TsPath.Path path in paths)
                {
                    if (p.ContainsFile(path))
                    {
                        dirty = true;
                        if (changeType == FileChangeType.Deleted)
                        {
                            dirtyFilePath = default;
                            break;
                        }
                        // package.json changes can affect module resolution and package
                        // identity (e.g. dedup decisions), so they must always trigger
                        // a full rebuild rather than a single-file clone.
                        if (global::Ts.TsPath.TsPath.GetBaseFileName(path.Value) == "package.json")
                        {
                            dirtyFilePath = default;
                            break;
                        }
                        if (string.IsNullOrEmpty(dirtyFilePath.Value))
                        {
                            dirtyFilePath = path;
                        }
                        else if (!dirtyFilePath.Equals(path))
                        {
                            dirtyFilePath = default;
                            break;
                        }
                    }
                    else if (p.host is not null &&
                        (changeType == FileChangeType.Created && p.host.sourceFS.SeenFileOrMissingParentDirectory(path) ||
                            changeType != FileChangeType.Created && p.host.sourceFS.SeenFile(path)))
                    {
                        dirty = true;
                        dirtyFilePath = default;
                        break;
                    }
                }
                return dirty || !p.dirtyFilePath.Equals(dirtyFilePath);
            },
            p =>
            {
                p!.IsDirty = true;
                p.dirtyFilePath = dirtyFilePath;
                if (logger is not null)
                {
                    if (!string.IsNullOrEmpty(dirtyFilePath.Value))
                    {
                        logger.Logf("Marking project {0} as dirty due to changes in {1}", p.Name(), dirtyFilePath.Value);
                    }
                    else
                    {
                        logger.Logf("Marking project {0} as dirty", p.Name());
                    }
                }
            });
    }

    // projectcollectionbuilder.go:1223-1236 — deleteConfiguredProject.
    private void DeleteConfiguredProject(DirtyValue<Project> project, global::Ts.Project.Logging.LogTree? logger)
    {
        global::Ts.TsPath.Path projectPath = project.Value()!.configFilePath;
        if (logger is not null)
        {
            logger.Log("Deleting configured project: " + project.Value()!.Name());
        }
        if (project.Value()!.Program is { } program)
        {
            program.RangeResolvedProjectReferenceForLs((referencePath, _, _, _) =>
            {
                ConfigFileRegistryBuilder.ReleaseConfigForProject(referencePath, projectPath);
                return true;
            });
        }
        ConfigFileRegistryBuilder.ReleaseConfigForProject(projectPath, projectPath);
        project.Delete();
    }

    // releaseDroppedProjectReferences releases the config entries for project references
    // that were present in oldProgram but are no longer referenced by newProgram. Creating
    // newProgram already re-acquires the config for every reference it still resolves, so
    // only the dropped references need to be released here.
    // projectcollectionbuilder.go:1238-1259 — releaseDroppedProjectReferences.
    private void ReleaseDroppedProjectReferences(global::Ts.Compiler.Program? oldProgram, global::Ts.Compiler.Program? newProgram, global::Ts.TsPath.Path projectPath)
    {
        if (oldProgram is null || ReferenceEquals(oldProgram, newProgram))
        {
            return;
        }
        var newReferences = new global::Ts.Collections.Set<global::Ts.TsPath.Path>();
        if (newProgram is not null)
        {
            newProgram.RangeResolvedProjectReferenceForLs((referencePath, _, _, _) =>
            {
                newReferences.Add(referencePath);
                return true;
            });
        }
        oldProgram.RangeResolvedProjectReferenceForLs((referencePath, _, _, _) =>
        {
            if (!newReferences.Has(referencePath))
            {
                ConfigFileRegistryBuilder.ReleaseConfigForProject(referencePath, projectPath);
            }
            return true;
        });
    }

    // maps.Equal over Dictionary<Path, Path> (nil == null == absent).
    private static bool MapsEqual(Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>? a, Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        // map-iter: 序无关 — equality is order-independent.
        foreach ((global::Ts.TsPath.Path key, global::Ts.TsPath.Path value) in a)
        {
            if (!b.TryGetValue(key, out global::Ts.TsPath.Path other) || !other.Equals(value))
            {
                return false;
            }
        }
        return true;
    }

    // maps.Equal over Dictionary<Path, string>.
    private static bool MapsEqual(Dictionary<global::Ts.TsPath.Path, string>? a, Dictionary<global::Ts.TsPath.Path, string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        // map-iter: 序无关 — equality is order-independent.
        foreach ((global::Ts.TsPath.Path key, string value) in a)
        {
            if (!b.TryGetValue(key, out string? other) || other != value)
            {
                return false;
            }
        }
        return true;
    }
}
