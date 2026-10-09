// Ported from tsgo internal/project/configfileregistrybuilder.go (whole file).
// PORT-NOTEs:
//  * Go `map[tspath.Path]struct{}` → Dictionary<Path, byte> (repo convention, cf.
//    Collections/SyncSet); `core.CopyMapInto`/`maps.Copy`/`maps.Clone` over retaining
//    sets are expressed with the small helpers at the bottom of this file.
//  * PendingReload itself is defined in Project.cs (Go declares it in project.go:46-52).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Collections;
using Ts.Project.Dirty;
using Ts.Project.Logging;
using static global::Ts.LsProto.DocumentUriExtensions;

namespace Ts.Project;

// configfileregistrybuilder.go:18-21 — interface conformance (ParseConfigHost,
// ExtendedConfigCache) is enforced by the class declaration.

// configFileRegistryBuilder tracks changes made on top of a previous
// configFileRegistry, producing a new clone with `finalize()` after
// all changes have been made.
internal sealed class ConfigFileRegistryBuilder : global::Ts.TsOptions.ParseConfigHost, global::Ts.TsOptions.ExtendedConfigCache
{
    internal readonly bool hasRelativePatternCapability;
    internal readonly SourceFS fs;
    internal readonly Func<global::Ts.TsPath.Path, bool> isOpenFile;
    internal readonly ExtendedConfigCache extendedConfigCache;
    internal readonly ulong snapshotID;
    internal readonly SessionOptions sessionOptions;
    internal readonly string customConfigFileName;

    internal readonly ConfigFileRegistry baseRegistry; // Go: base
    internal readonly Dirty.SyncMap<global::Ts.TsPath.Path, ConfigFileEntry> configs;
    internal readonly Map<global::Ts.TsPath.Path, ConfigFileNames> configFileNames;
    internal readonly bool customConfigFileNameChanged;

    // configfileregistrybuilder.go:41-65 — newConfigFileRegistryBuilder.
    internal static ConfigFileRegistryBuilder NewConfigFileRegistryBuilder(
        bool hasRelativePatternCapability,
        SnapshotFSBuilder fs,
        ConfigFileRegistry oldConfigFileRegistry,
        ExtendedConfigCache extendedConfigCache,
        ulong snapshotID,
        SessionOptions sessionOptions,
        string customConfigFileName,
        global::Ts.Project.Logging.LogTree? logger)
    {
        _ = logger; // Go accepts a logger and does not use it.
        return new ConfigFileRegistryBuilder(
            hasRelativePatternCapability,
            fs,
            oldConfigFileRegistry,
            extendedConfigCache,
            snapshotID,
            sessionOptions,
            customConfigFileName);
    }

    private ConfigFileRegistryBuilder(
        bool hasRelativePatternCapability,
        SnapshotFSBuilder fs,
        ConfigFileRegistry oldConfigFileRegistry,
        ExtendedConfigCache extendedConfigCache,
        ulong snapshotID,
        SessionOptions sessionOptions,
        string customConfigFileName)
    {
        this.hasRelativePatternCapability = hasRelativePatternCapability;
        this.fs = SourceFS.NewSourceFS(false, fs, fs.toPath);
        isOpenFile = fs.IsOpenFile;
        baseRegistry = oldConfigFileRegistry;
        this.sessionOptions = sessionOptions;
        this.extendedConfigCache = extendedConfigCache;
        this.snapshotID = snapshotID;
        this.customConfigFileName = customConfigFileName;
        customConfigFileNameChanged = customConfigFileName != oldConfigFileRegistry.customConfigFileName;

        configs = new Dirty.SyncMap<global::Ts.TsPath.Path, ConfigFileEntry>(oldConfigFileRegistry.configs);
        configFileNames = new Map<global::Ts.TsPath.Path, ConfigFileNames>(oldConfigFileRegistry.configFileNames);
    }

    // configfileregistrybuilder.go:67-95 — Finalize creates a new configFileRegistry based
    // on the changes made in the builder. If no changes were made, it returns the original
    // base registry.
    internal ConfigFileRegistry Finalize()
    {
        bool changed = false;
        ConfigFileRegistry newRegistry = baseRegistry;
        void EnsureCloned()
        {
            if (!changed)
            {
                newRegistry = newRegistry.Clone();
                changed = true;
            }
        }

        (Dictionary<global::Ts.TsPath.Path, ConfigFileEntry>? finalizedConfigs, bool changedConfigs) = configs.Finalize();
        if (changedConfigs)
        {
            EnsureCloned();
            newRegistry.configs = finalizedConfigs!;
        }

        (Dictionary<global::Ts.TsPath.Path, ConfigFileNames>? finalizedNames, bool changedNames) = configFileNames.Finalize();
        if (changedNames)
        {
            EnsureCloned();
            newRegistry.configFileNames = finalizedNames!;
        }

        if (customConfigFileNameChanged)
        {
            EnsureCloned();
            newRegistry.customConfigFileName = customConfigFileName;
        }

        return newRegistry;
    }

    // configfileregistrybuilder.go:97-115 — findOrAcquireConfigForFile.
    internal global::Ts.TsOptions.ParsedCommandLine? FindOrAcquireConfigForFile(
        string configFileName,
        global::Ts.TsPath.Path configFilePath,
        global::Ts.TsPath.Path filePath,
        ProjectLoadKind loadKind,
        global::Ts.Project.Logging.LogTree logger)
    {
        switch (loadKind)
        {
            case ProjectLoadKind.Find:
                if (configs.Load(configFilePath) is { Ok: true } entry)
                {
                    return entry.Entry!.Value().commandLine;
                }
                return null;
            case ProjectLoadKind.Create:
                return AcquireConfigForFile(configFileName, configFilePath, filePath, logger);
            default:
                throw new InvalidOperationException($"unknown project load kind: {loadKind}");
        }
    }

    // configfileregistrybuilder.go:117-136 — reloadIfNeeded updates the command line of the
    // config file entry based on its pending reload state. This function should only be
    // called from within the Change() method of a dirty map entry.
    private void ReloadIfNeeded(ConfigFileEntry entry, string fileName, global::Ts.TsPath.Path path, global::Ts.Project.Logging.LogTree logger)
    {
        switch (entry.pendingReload)
        {
            case PendingReload.FileNames:
                logger.Log("Reloading file names for config: " + fileName);
                entry.commandLine = entry.commandLine!.ReloadFileNamesOfParsedCommandLine(fs);
                break;
            case PendingReload.Full:
                logger.Log("Loading config file: " + fileName);
                global::Ts.TsOptions.ParsedCommandLine? oldCommandLine = entry.commandLine;
                (entry.commandLine, _) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFilePath(
                    fileName,
                    path,
                    options: null,
                    optionsRaw: null,
                    sys: this,
                    extendedConfigCache: this);
                UpdateExtendingConfigs(path, entry.commandLine, oldCommandLine);
                UpdateRootFilesWatch(fileName, entry);
                logger.Log("Finished loading config file");
                break;
            default:
                return;
        }
        entry.pendingReload = PendingReload.None;
    }

    // configfileregistrybuilder.go:138-180 — updateExtendingConfigs.
    private void UpdateExtendingConfigs(global::Ts.TsPath.Path extendingConfigPath, global::Ts.TsOptions.ParsedCommandLine? newCommandLine, global::Ts.TsOptions.ParsedCommandLine? oldCommandLine)
    {
        var newExtendedConfigPaths = new Set<global::Ts.TsPath.Path>();
        if (newCommandLine is not null)
        {
            foreach (string extendedConfig in newCommandLine.ExtendedSourceFiles() ?? [])
            {
                global::Ts.TsPath.Path extendedConfigPath = fs.toPath(extendedConfig);
                newExtendedConfigPaths.Add(extendedConfigPath);
                (Dirty.SyncMapEntry<global::Ts.TsPath.Path, ConfigFileEntry>? entry, bool loaded) = configs.LoadOrStore(extendedConfigPath, ConfigFileEntry.NewExtendedConfigFileEntry(extendedConfig, extendingConfigPath));
                if (loaded && entry is not null)
                {
                    entry.ChangeIf(
                        config => !config.retainingConfigs.Has(extendingConfigPath),
                        config =>
                        {
                            if (config.retainingConfigs is null)
                            {
                                config.retainingConfigs = new Set<global::Ts.TsPath.Path>();
                            }
                            config.retainingConfigs.Add(extendingConfigPath);
                        });
                }
            }
        }
        if (oldCommandLine is not null)
        {
            foreach (string extendedConfig in oldCommandLine.ExtendedSourceFiles() ?? [])
            {
                global::Ts.TsPath.Path extendedConfigPath = fs.toPath(extendedConfig);
                if (newExtendedConfigPaths.Has(extendedConfigPath))
                {
                    continue;
                }
                if (configs.Load(extendedConfigPath) is { Ok: true } entry && entry.Entry is not null)
                {
                    entry.Entry.ChangeIf(
                        config => config.retainingConfigs.Has(extendingConfigPath),
                        config => config.retainingConfigs.Delete(extendingConfigPath));
                }
            }
        }
    }

    // configfileregistrybuilder.go:182-242 — updateRootFilesWatch.
    private void UpdateRootFilesWatch(string fileName, ConfigFileEntry entry)
    {
        if (entry.rootFilesWatch is null)
        {
            return;
        }

        HashSet<string>? ignored = null;
        var globs = new List<string>();
        var externalDirectories = new List<string>();
        bool includeWorkspace = false;
        bool includeTsconfigDir = false;
        string tsconfigDir = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName);
        Dictionary<string, bool>? wildcardDirectories = entry.commandLine!.WildcardDirectories();
        var comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
        {
            CurrentDirectory = sessionOptions.CurrentDirectory,
            UseCaseSensitiveFileNames = FS().UseCaseSensitiveFileNames(),
        };
        // map-iter: 已排序 — globs are sorted below to match Go's slices.Sort.
        foreach (string dir in wildcardDirectories?.Keys ?? Enumerable.Empty<string>())
        {
            if (global::Ts.TsPath.TsPath.ContainsPath(sessionOptions.CurrentDirectory, dir, comparePathsOptions))
            {
                includeWorkspace = true;
            }
            else if (global::Ts.TsPath.TsPath.ContainsPath(tsconfigDir, dir, comparePathsOptions))
            {
                includeTsconfigDir = true;
            }
            else
            {
                externalDirectories.Add(dir);
            }
        }
        foreach (string literalFileName in entry.commandLine.LiteralFileNames() ?? [])
        {
            if (global::Ts.TsPath.TsPath.ContainsPath(sessionOptions.CurrentDirectory, literalFileName, comparePathsOptions))
            {
                includeWorkspace = true;
            }
            else if (global::Ts.TsPath.TsPath.ContainsPath(tsconfigDir, literalFileName, comparePathsOptions))
            {
                includeTsconfigDir = true;
            }
            else
            {
                externalDirectories.Add(global::Ts.TsPath.TsPath.GetDirectoryPath(literalFileName));
            }
        }

        if (includeWorkspace)
        {
            globs.Add(WatchUtil.GetRecursiveGlobPattern(sessionOptions.CurrentDirectory));
        }
        if (includeTsconfigDir)
        {
            globs.Add(WatchUtil.GetRecursiveGlobPattern(tsconfigDir));
        }
        foreach (string extendedFileName in entry.commandLine.ExtendedSourceFiles() ?? [])
        {
            if (includeWorkspace && global::Ts.TsPath.TsPath.ContainsPath(sessionOptions.CurrentDirectory, extendedFileName, comparePathsOptions))
            {
                continue;
            }
            globs.Add(extendedFileName);
        }
        if (externalDirectories.Count > 0)
        {
            (string[]? commonParents, HashSet<string>? ignoredExternalDirs) = global::Ts.TsPath.TsPath.GetCommonParents(
                externalDirectories.ToArray(),
                WatchConstants.MinWatchLocationDepth,
                WatchUtil.GetPathComponentsForWatching,
                comparePathsOptions);
            foreach (string parent in commonParents ?? [])
            {
                globs.Add(WatchUtil.GetRecursiveGlobPattern(parent));
            }
            ignored = ignoredExternalDirs;
        }

        globs.Sort(StringComparer.Ordinal);
        entry.rootFilesWatch = entry.rootFilesWatch.Clone(new PatternsAndIgnored
        {
            patternsInsideWorkspace = globs.ToArray(),
            ignored = ignored,
        });
    }

    // configfileregistrybuilder.go:244-268 — acquireConfigForProject loads a config file
    // entry from the cache, or parses it if not already cached, then adds the project (if
    // provided) to `retainingProjects` to keep it alive in the cache. Each
    // `acquireConfigForProject` call that passes a `project` should be accompanied by an
    // eventual `releaseConfigForProject` call with the same project.
    internal global::Ts.TsOptions.ParsedCommandLine? AcquireConfigForProject(string fileName, global::Ts.TsPath.Path path, Project project, global::Ts.Project.Logging.LogTree logger)
    {
        (Dirty.SyncMapEntry<global::Ts.TsPath.Path, ConfigFileEntry>? entry, _) = configs.LoadOrStore(path, ConfigFileEntry.NewConfigFileEntry(hasRelativePatternCapability, fileName));
        bool needsRetainProject = false;
        entry!.ChangeIf(
            config =>
            {
                bool alreadyRetaining = config.retainingProjects.Has(project.configFilePath);
                needsRetainProject = !alreadyRetaining;
                return needsRetainProject || config.pendingReload != PendingReload.None;
            },
            config =>
            {
                if (needsRetainProject)
                {
                    if (config.retainingProjects is null)
                    {
                        config.retainingProjects = new Set<global::Ts.TsPath.Path>();
                    }
                    config.retainingProjects.Add(project.configFilePath);
                }
                ReloadIfNeeded(config, fileName, path, logger);
            });
        return entry.Value().commandLine;
    }

    // configfileregistrybuilder.go:270-296 — acquireConfigForFile loads a config file entry
    // from the cache, or parses it if not already cached, then adds the open file to
    // `retainingOpenFiles` to keep it alive in the cache. Each `acquireConfigForFile` call
    // that passes an `openFilePath` should be accompanied by an eventual
    // `releaseConfigForOpenFile` call with the same open file.
    internal global::Ts.TsOptions.ParsedCommandLine? AcquireConfigForFile(string configFileName, global::Ts.TsPath.Path configFilePath, global::Ts.TsPath.Path filePath, global::Ts.Project.Logging.LogTree logger)
    {
        (Dirty.SyncMapEntry<global::Ts.TsPath.Path, ConfigFileEntry>? entry, _) = configs.LoadOrStore(configFilePath, ConfigFileEntry.NewConfigFileEntry(hasRelativePatternCapability, configFileName));
        bool needsRetainOpenFile = false;
        entry!.ChangeIf(
            config =>
            {
                if (isOpenFile(filePath))
                {
                    bool alreadyRetaining = config.retainingOpenFiles.Has(filePath);
                    needsRetainOpenFile = !alreadyRetaining;
                }
                return needsRetainOpenFile || config.pendingReload != PendingReload.None;
            },
            config =>
            {
                if (needsRetainOpenFile)
                {
                    if (config.retainingOpenFiles is null)
                    {
                        config.retainingOpenFiles = new Set<global::Ts.TsPath.Path>();
                    }
                    config.retainingOpenFiles.Add(filePath);
                }
                ReloadIfNeeded(config, configFileName, configFilePath, logger);
            });
        return entry.Value().commandLine;
    }

    // configfileregistrybuilder.go:298-312 — releaseConfigForProject removes the project
    // from the config entry. Once no projects or files are associated with the config
    // entry, it will be removed on the next call to `cleanup`.
    internal void ReleaseConfigForProject(global::Ts.TsPath.Path configFilePath, global::Ts.TsPath.Path projectPath)
    {
        if (configs.Load(configFilePath) is { Ok: true } entry && entry.Entry is not null)
        {
            entry.Entry.ChangeIf(
                config => config.retainingProjects.Has(projectPath),
                config => config.retainingProjects.Delete(projectPath));
        }
    }

    // configfileregistrybuilder.go:314-333 — didCloseFile removes the open file from the
    // config entry. Once no projects or files are associated with the config entry, it will
    // be removed on the next call to `cleanup`.
    internal void DidCloseFile(global::Ts.TsPath.Path path)
    {
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(path.Value))
        {
            return;
        }
        configFileNames.Delete(path);
        configs.Range(entry =>
        {
            entry.ChangeIf(
                config => config.retainingOpenFiles.Has(path),
                config => config.retainingOpenFiles.Delete(path));
            return true;
        });
    }

    // configfileregistrybuilder.go:335-342 — changeFileResult.
    internal sealed class ChangeFileResult
    {
        internal Dictionary<global::Ts.TsPath.Path, byte>? affectedProjects;
        internal Dictionary<global::Ts.TsPath.Path, byte>? affectedFiles;

        internal bool IsEmpty() => (affectedProjects?.Count ?? 0) == 0 && (affectedFiles?.Count ?? 0) == 0;
    }

    // configfileregistrybuilder.go:344-351 — DidChangeCustomConfigFileName.
    internal bool DidChangeCustomConfigFileName(global::Ts.Project.Logging.LogTree logger)
    {
        _ = logger;
        if (!customConfigFileNameChanged)
        {
            return false;
        }

        configFileNames.Clear();
        return true;
    }

    // configfileregistrybuilder.go:353-386 — invalidateCache.
    internal ChangeFileResult InvalidateCache(global::Ts.Project.Logging.LogTree logger)
    {
        Dictionary<global::Ts.TsPath.Path, byte>? affectedProjects = null;
        Dictionary<global::Ts.TsPath.Path, byte>? affectedFiles = null;

        logger.Log("Too many files changed; marking all configs for reload");
        configFileNames.Range(entry =>
        {
            affectedFiles ??= new Dictionary<global::Ts.TsPath.Path, byte>();
            affectedFiles[entry.Key()] = 0;
            return true;
        });
        configFileNames.Clear();

        configs.Range(entry =>
        {
            entry.Change(cfg =>
            {
                affectedProjects = RetainingSets.CopyMapInto(affectedProjects, cfg.retainingProjects);
                if (cfg.pendingReload != PendingReload.Full)
                {
                    bool ok = FS().TryReadFile(cfg.fileName, out string text);
                    if (!ok || cfg.commandLine is null || text != cfg.commandLine.ConfigFile!.SourceFile.Text())
                    {
                        cfg.pendingReload = PendingReload.Full;
                    }
                    else
                    {
                        cfg.pendingReload = PendingReload.FileNames;
                    }
                }
            });
            return true;
        });

        return new ChangeFileResult
        {
            affectedProjects = affectedProjects,
            affectedFiles = affectedFiles,
        };
    }

    // configfileregistrybuilder.go:388-391 — isConfigBaseName.
    private bool IsConfigBaseName(string baseName)
    {
        return baseName == "tsconfig.json" || baseName == "jsconfig.json" ||
            (customConfigFileName != "" && baseName == customConfigFileName);
    }

    // configfileregistrybuilder.go:393-565 — DidChangeFiles.
    internal ChangeFileResult DidChangeFiles(FileChangeSummary summary, global::Ts.Project.Logging.LogTree logger)
    {
        Dictionary<global::Ts.TsPath.Path, byte>? affectedProjects = null;
        Dictionary<global::Ts.TsPath.Path, byte>? affectedFiles = null;
        bool shouldInvalidateCache = false;

        logger.Log("Summarizing file changes");
        bool hasExcessiveChanges = summary.HasExcessiveWatchEvents() && summary.IncludesWatchChangeOutsideNodeModules;
        var createdFiles = new Dictionary<global::Ts.TsPath.Path, string>(summary.Created.Len());
        var deletedFiles = new Dictionary<global::Ts.TsPath.Path, string>(summary.Deleted.Len());
        var createdOrDeletedConfigFiles = new Dictionary<global::Ts.TsPath.Path, byte>();
        var createdOrChangedOrDeletedFiles = new Dictionary<global::Ts.TsPath.Path, byte>(summary.Changed.Len() + summary.Deleted.Len());
        // map-iter: 序无关 — per-URI classification accumulates into keyed dictionaries.
        foreach (global::Ts.LsProto.DocumentUri uri in summary.Changed.Keys())
        {
            if (global::Ts.TsPath.TsPath.ContainsIgnoredPath(uri.Value))
            {
                continue;
            }
            string fileName = uri.FileName();
            global::Ts.TsPath.Path path = fs.toPath(fileName);
            string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(path.Value);
            if (IsConfigBaseName(baseName))
            {
                createdOrDeletedConfigFiles[path] = 0;
            }
            createdOrChangedOrDeletedFiles[path] = 0;
        }
        foreach (global::Ts.LsProto.DocumentUri uri in summary.Deleted.Keys())
        {
            if (global::Ts.TsPath.TsPath.ContainsIgnoredPath(uri.Value))
            {
                continue;
            }
            string fileName = uri.FileName();
            global::Ts.TsPath.Path path = fs.toPath(fileName);
            deletedFiles[path] = fileName;
            string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(path.Value);
            if (IsConfigBaseName(baseName))
            {
                createdOrDeletedConfigFiles[path] = 0;
            }
            createdOrChangedOrDeletedFiles[path] = 0;
        }
        foreach (global::Ts.LsProto.DocumentUri uri in summary.Created.Keys())
        {
            if (global::Ts.TsPath.TsPath.ContainsIgnoredPath(uri.Value))
            {
                continue;
            }
            string fileName = uri.FileName();
            global::Ts.TsPath.Path path = fs.toPath(fileName);
            createdFiles[path] = fileName;
            string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(path.Value);
            if (IsConfigBaseName(baseName))
            {
                createdOrDeletedConfigFiles[path] = 0;
            }
            createdOrChangedOrDeletedFiles[path] = 0;
        }

        // Handle closed files - this ranges over config entries and could be combined
        // with the file change handling, but a separate loop is simpler and a snapshot
        // change with both closing and watch changes seems rare.
        foreach (global::Ts.LsProto.DocumentUri uri in summary.Closed.Keys())
        {
            string fileName = uri.FileName();
            global::Ts.TsPath.Path path = fs.toPath(fileName);
            DidCloseFile(path);
        }

        // Handle changes to stored config files
        logger.Log("Checking if any changed files are config files");
        // map-iter: 序无关 — each changed config path is handled independently.
        foreach (global::Ts.TsPath.Path path in createdOrChangedOrDeletedFiles.Keys)
        {
            if (configs.Load(path) is { Ok: true } entry && entry.Entry is not null)
            {
                if (hasExcessiveChanges)
                {
                    return InvalidateCache(logger);
                }

                affectedProjects = RetainingSets.CopyMapInto(affectedProjects, HandleConfigChange(entry.Entry!, logger));
                // map-iter: 序无关 — each extending config is handled independently.
                foreach (global::Ts.TsPath.Path extendingConfigPath in entry.Entry.Value().retainingConfigs.Keys())
                {
                    if (configs.Load(extendingConfigPath) is { Ok: true } extendingConfigEntry && extendingConfigEntry.Entry is not null)
                    {
                        affectedProjects = RetainingSets.CopyMapInto(affectedProjects, HandleConfigChange(extendingConfigEntry.Entry!, logger));
                    }
                }
                // This was a config file, so assume it's not also a root file
                createdFiles.Remove(path);
            }
        }

        // Handle created/deleted files named "tsconfig.json" or "jsconfig.json"
        // map-iter: 序无关 — each created/deleted config path prunes affected lookups.
        foreach (global::Ts.TsPath.Path path in createdOrDeletedConfigFiles.Keys)
        {
            if (hasExcessiveChanges)
            {
                return InvalidateCache(logger);
            }
            global::Ts.TsPath.Path directoryPath = path.GetDirectoryPath();
            configFileNames.Range(entry =>
            {
                if (directoryPath.ContainsPath(entry.Key()))
                {
                    affectedFiles ??= new Dictionary<global::Ts.TsPath.Path, byte>();
                    affectedFiles[entry.Key()] = 0;
                    entry.Delete();
                }
                return true;
            });
        }

        // Handle deletions of wildcard-included root files
        // map-iter: 序无关 — per deleted path; the invalidation short-circuit is per path.
        foreach ((global::Ts.TsPath.Path path, string fileName) in deletedFiles)
        {
            configs.Range(entry =>
            {
                bool changed = entry.ChangeIf(
                    config =>
                    {
                        if (config.pendingReload != PendingReload.None || config.commandLine is null)
                        {
                            return false;
                        }
                        if (config.commandLine.FileNamesByPath().ContainsKey(path))
                        {
                            // If the file is included in FileNames() but not matched by literal "files", it must be
                            // included via wildcard, which means a reload of filenames will remove it from the list.
                            // (Files explicitly specified in "files" are always included in the ParsedCommandLine,
                            // triggering a missing root file error during program construction.)
                            return config.commandLine.GetMatchedFileSpec(fileName) == "";
                        }
                        return false;
                    },
                    config =>
                    {
                        config.pendingReload = PendingReload.FileNames;
                        affectedProjects = RetainingSets.CopyMapInto(affectedProjects, config.retainingProjects);
                        logger.Logf("Root files for config {0} changed", entry.Key());
                        shouldInvalidateCache = hasExcessiveChanges;
                    });
                _ = changed;
                return !shouldInvalidateCache;
            });
            if (shouldInvalidateCache)
            {
                return InvalidateCache(logger);
            }
        }

        // Handle possible root file creation
        if (createdFiles.Count > 0)
        {
            configs.Range(entry =>
            {
                entry.ChangeIf(
                    config =>
                    {
                        if (config.commandLine is null || config.rootFilesWatch is null || config.pendingReload != PendingReload.None)
                        {
                            return false;
                        }
                        logger.Logf("Checking if any of {0} created files match root files for config {1}", createdFiles.Count, entry.Key());
                        // map-iter: 序无关 — first match wins, order-independent.
                        foreach ((global::Ts.TsPath.Path createdPath, string createdFileName) in createdFiles)
                        {
                            if (config.commandLine.PossiblyMatchesFileName(createdFileName))
                            {
                                return true;
                            }
                            if (config.commandLine.PossiblyMatchesDirectoryName(createdPath) && fs.DirectoryExists(createdFileName))
                            {
                                // If we got a creation event for a directory, it's probably a symlink. We don't need to
                                // test realpath here; this is enough confidence to trigger a filename reload.
                                return true;
                            }
                        }
                        return false;
                    },
                    config =>
                    {
                        config.pendingReload = PendingReload.FileNames;
                        affectedProjects = RetainingSets.CopyMapInto(affectedProjects, config.retainingProjects);
                        logger.Logf("Root files for config {0} changed", entry.Key());
                        shouldInvalidateCache = hasExcessiveChanges;
                    });
                return !shouldInvalidateCache;
            });
            if (shouldInvalidateCache)
            {
                return InvalidateCache(logger);
            }
        }

        return new ChangeFileResult
        {
            affectedProjects = affectedProjects,
            affectedFiles = affectedFiles,
        };
    }

    // configfileregistrybuilder.go:567-579 — handleConfigChange.
    private Dictionary<global::Ts.TsPath.Path, byte>? HandleConfigChange(Dirty.SyncMapEntry<global::Ts.TsPath.Path, ConfigFileEntry> entry, global::Ts.Project.Logging.LogTree logger)
    {
        Dictionary<global::Ts.TsPath.Path, byte>? affectedProjects = null;
        bool changed = entry.ChangeIf(
            config => config.pendingReload != PendingReload.Full,
            config => config.pendingReload = PendingReload.Full);
        if (changed)
        {
            logger.Logf("Config file {0} changed", entry.Key());
            affectedProjects = RetainingSets.CloneToDictionary(entry.Value().retainingProjects);
        }

        return affectedProjects;
    }

    // configfileregistrybuilder.go:581-632 — computeConfigFileName.
    internal string ComputeConfigFileName(string fileName, bool skipSearchInDirectoryOfFile, global::Ts.Project.Logging.LogTree logger)
    {
        string searchPath = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName);
        // Prefer custom config file if provided; search ancestors with correct skip behavior.
        if (customConfigFileName != "")
        {
            bool skip = skipSearchInDirectoryOfFile;
            (string result, _) = global::Ts.TsPath.TsPath.ForEachAncestorDirectory(searchPath, directory =>
            {
                if (!skip)
                {
                    string customPath = global::Ts.TsPath.TsPath.CombinePaths(directory, customConfigFileName);
                    if (FS().FileExists(customPath))
                    {
                        return (customPath, true);
                    }
                }
                if (directory.EndsWith("/node_modules", StringComparison.Ordinal))
                {
                    return ("", true);
                }
                skip = false;
                return ("", false);
            });
            // Go's ForEachAncestorDirectory zero value is "" — C# default(string) is null,
            // so coalesce before the "" comparison below (configfileregistrybuilder.go:586).
            result ??= "";
            if (result != "")
            {
                logger.Logf("computeConfigFileName:: File: {0}:: Result: {1}", fileName, result);
                return result;
            }
        }

        // When searching for ancestor of a config file, determine which config types to skip
        // in the starting directory. This matches TSServer's forEachConfigFileLocation behavior:
        // - For ancestor of tsconfig.json: skip tsconfig.json but still check jsconfig.json
        // - For ancestor of jsconfig.json: skip both tsconfig.json and jsconfig.json
        bool skipTsconfig = skipSearchInDirectoryOfFile;
        bool skipJsconfig = skipSearchInDirectoryOfFile && !fileName.EndsWith("/tsconfig.json", StringComparison.Ordinal);
        (string result2, _) = global::Ts.TsPath.TsPath.ForEachAncestorDirectory(searchPath, directory =>
        {
            if (!skipTsconfig)
            {
                string tsconfigPath = global::Ts.TsPath.TsPath.CombinePaths(directory, "tsconfig.json");
                if (FS().FileExists(tsconfigPath))
                {
                    return (tsconfigPath, true);
                }
            }
            if (!skipJsconfig)
            {
                string jsconfigPath = global::Ts.TsPath.TsPath.CombinePaths(directory, "jsconfig.json");
                if (FS().FileExists(jsconfigPath))
                {
                    return (jsconfigPath, true);
                }
            }
            if (directory.EndsWith("/node_modules", StringComparison.Ordinal))
            {
                return ("", true);
            }
            skipTsconfig = false;
            skipJsconfig = false;
            return ("", false);
        });
        // Go's ForEachAncestorDirectory zero value is "" — C# default(string) is null,
        // so coalesce before the "" comparison below (configfileregistrybuilder.go:630).
        result2 ??= "";
        logger.Logf("computeConfigFileName:: File: {0}:: Result: {1}", fileName, result2);
        return result2;
    }

    // configfileregistrybuilder.go:634-650 — getConfigFileNameForFile.
    internal string GetConfigFileNameForFile(string fileName, global::Ts.TsPath.Path path, global::Ts.Project.Logging.LogTree logger)
    {
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
        {
            return "";
        }

        if (configFileNames.Get(path) is { Ok: true } entry)
        {
            return entry.Entry!.Value().nearestConfigFileName;
        }

        string configName = ComputeConfigFileName(fileName, false, logger);
        if (isOpenFile(path))
        {
            configFileNames.Add(path, new ConfigFileNames
            {
                nearestConfigFileName = configName,
            });
        }
        return configName;
    }

    // configfileregistrybuilder.go:652-668 — forEachConfigFileNameFor.
    internal void ForEachConfigFileNameFor(global::Ts.TsPath.Path path, Action<string> cb)
    {
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(path.Value))
        {
            return;
        }

        if (configFileNames.Get(path) is { Ok: true } entry)
        {
            ConfigFileNames entryValue = entry.Entry!.Value();
            string configFileName = entryValue.nearestConfigFileName;
            while (configFileName != "")
            {
                cb(configFileName);
                if (entryValue.ancestors.TryGetValue(configFileName, out string? ancestorConfigName))
                {
                    configFileName = ancestorConfigName;
                }
                else
                {
                    return;
                }
            }
        }
    }

    // configfileregistrybuilder.go:670-696 — getAncestorConfigFileName.
    internal string GetAncestorConfigFileName(string fileName, global::Ts.TsPath.Path path, string configFileName, global::Ts.Project.Logging.LogTree logger)
    {
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
        {
            return "";
        }

        if (configFileNames.Get(path) is not { Ok: true } entry)
        {
            return "";
        }

        ConfigFileNames entryValue = entry.Entry!.Value();
        if (entryValue.ancestors.TryGetValue(configFileName, out string? ancestorConfigName))
        {
            return ancestorConfigName;
        }

        // Look for config in parent folders of config file
        string result = ComputeConfigFileName(configFileName, true, logger);

        if (isOpenFile(path))
        {
            entry.Entry.Change(value =>
            {
                value.ancestors[configFileName] = result;
            });
        }
        return result;
    }

    // configfileregistrybuilder.go:698-701 — FS implements tsoptions.ParseConfigHost.
    public global::Ts.Vfs.IFileSystem FS()
    {
        return fs;
    }

    // configfileregistrybuilder.go:703-706 — GetCurrentDirectory implements
    // tsoptions.ParseConfigHost.
    public string GetCurrentDirectory()
    {
        return sessionOptions.CurrentDirectory;
    }

    // configfileregistrybuilder.go:708-724 — GetExtendedConfig implements
    // tsoptions.ExtendedConfigCache.
    public global::Ts.TsOptions.ExtendedConfigCacheEntry GetExtendedConfig(string fileName, global::Ts.TsPath.Path path, global::Ts.TsPath.Path[] resolutionStack, global::Ts.TsOptions.ParseConfigHost host)
    {
        string content = "";
        FileHandle? fh = fs.GetFileByPath(fileName, path);
        if (fh is not null)
        {
            content = fh.Content();
        }

        return extendedConfigCache.LoadAndAcquire(path, snapshotID, new ExtendedConfigParseArgs
        {
            FileName = fileName,
            Content = content,
            FS = fs.source,
            ResolutionStack = resolutionStack,
            Host = host,
            Cache = this,
        }).Entry;
    }

    // configfileregistrybuilder.go:726-733 — Cleanup.
    internal void Cleanup()
    {
        configs.Range(entry =>
        {
            entry.DeleteIf(value => value.retainingProjects.Len() == 0 && value.retainingOpenFiles.Len() == 0 && value.retainingConfigs.Len() == 0);
            return true;
        });
    }
}

// Helpers for Go's `core.CopyMapInto` / `maps.Copy` / `maps.Clone` over the retaining
// sets (`map[tspath.Path]struct{}` → Dictionary<Path, byte>; collections.Set carries its
// backing map in the public M field).
internal static class RetainingSets
{
    internal static Dictionary<global::Ts.TsPath.Path, byte>? CopyMapInto(Dictionary<global::Ts.TsPath.Path, byte>? dst, Set<global::Ts.TsPath.Path>? src)
    {
        // map-iter: 序无关 — set union; the result is only iterated/logged/sampled by key.
        if (src is not null)
        {
            dst ??= new Dictionary<global::Ts.TsPath.Path, byte>();
            foreach (global::Ts.TsPath.Path key in src.Keys())
            {
                dst[key] = 0;
            }
        }
        return dst;
    }

    // Go core.CopyMapInto(dst, src map[K]V) over the struct{}-set encoding
    // (configfileregistrybuilder.go:460,463 — handleConfigChange results).
    internal static Dictionary<global::Ts.TsPath.Path, byte>? CopyMapInto(Dictionary<global::Ts.TsPath.Path, byte>? dst, Dictionary<global::Ts.TsPath.Path, byte>? src)
    {
        // map-iter: 序无关 — map copy; the result is only iterated/logged/sampled by key.
        if (dst is null)
        {
            return src is null ? null : new Dictionary<global::Ts.TsPath.Path, byte>(src); // Go maps.Clone
        }
        if (src is not null)
        {
            foreach ((global::Ts.TsPath.Path key, byte value) in src)
            {
                dst[key] = value;
            }
        }
        return dst;
    }

    internal static Dictionary<global::Ts.TsPath.Path, byte>? CloneToDictionary(Set<global::Ts.TsPath.Path>? src)
    {
        if (src is null || src.M is null)
        {
            return null; // Go maps.Clone(nil) == nil
        }
        var result = new Dictionary<global::Ts.TsPath.Path, byte>(src.M.Count);
        foreach (global::Ts.TsPath.Path key in src.Keys())
        {
            result[key] = 0;
        }
        return result;
    }
}
