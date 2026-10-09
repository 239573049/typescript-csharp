// Ported from tsgo internal/project/configfileregistry.go (whole file).

using System.Collections.Generic;
using Ts.Collections;

namespace Ts.Project;

// configfileregistry.go:13-23 — ConfigFileRegistry.
public sealed class ConfigFileRegistry
{
    // configs is a map of config file paths to their entries.
    internal Dictionary<global::Ts.TsPath.Path, ConfigFileEntry> configs = [];

    // configFileNames is a map of open file paths to information
    // about their ancestor config file names. It is only used as
    // a cache during
    internal Dictionary<global::Ts.TsPath.Path, ConfigFileNames> configFileNames = [];

    // customConfigFileName is the custom config file name preference that was
    // used when building this registry's configFileNames cache.
    internal string customConfigFileName = "";

    // Cross-type accessors (Go: same-package field access).
    internal Dictionary<global::Ts.TsPath.Path, ConfigFileEntry> Configs => configs;

    // configfileregistry.go:87-92 — GetConfig.
    public global::Ts.TsOptions.ParsedCommandLine? GetConfig(global::Ts.TsPath.Path path)
    {
        if (configs.TryGetValue(path, out ConfigFileEntry? entry))
        {
            return entry.commandLine;
        }
        return null;
    }

    // configfileregistry.go:94-99 — GetConfigFileName.
    public string GetConfigFileName(global::Ts.TsPath.Path path)
    {
        if (configFileNames.TryGetValue(path, out ConfigFileNames? entry))
        {
            return entry.nearestConfigFileName;
        }
        return "";
    }

    // configfileregistry.go:101-106 — GetAncestorConfigFileName.
    public string GetAncestorConfigFileName(global::Ts.TsPath.Path path, string higherThanConfig)
    {
        if (configFileNames.TryGetValue(path, out ConfigFileNames? entry))
        {
            return entry.ancestors.GetValueOrDefault(higherThanConfig, "");
        }
        return "";
    }

    // configfileregistry.go:108-115 — clone creates a shallow copy of the configFileRegistry.
    internal ConfigFileRegistry Clone()
    {
        return new ConfigFileRegistry
        {
            configs = new Dictionary<global::Ts.TsPath.Path, ConfigFileEntry>(configs),
            configFileNames = new Dictionary<global::Ts.TsPath.Path, ConfigFileNames>(configFileNames),
            customConfigFileName = customConfigFileName,
        };
    }

    // configfileregistry.go:117-123 — TestConfigEntry (for testing). Go's iter.Seq fields
    // become materialized path arrays here (order is Go map iteration order — undefined).
    public sealed class TestConfigEntry
    {
        public string FileName = "";

        public global::Ts.TsPath.Path[] RetainingProjects = [];

        public global::Ts.TsPath.Path[] RetainingOpenFiles = [];

        public global::Ts.TsPath.Path[] RetainingConfigs = [];
    }

    // configfileregistry.go:126-137 — ForEachTestConfigEntry (for testing).
    public void ForEachTestConfigEntry(System.Action<global::Ts.TsPath.Path, TestConfigEntry> cb)
    {
        // map-iter: 序无关 — test-only enumeration of registry entries.
        foreach ((global::Ts.TsPath.Path path, ConfigFileEntry entry) in configs)
        {
            cb(path, new TestConfigEntry
            {
                FileName = entry.fileName,
                RetainingProjects = KeysOf(entry.retainingProjects),
                RetainingOpenFiles = KeysOf(entry.retainingOpenFiles),
                RetainingConfigs = KeysOf(entry.retainingConfigs),
            });
        }
    }

    // configfileregistry.go:140-152 — GetTestConfigEntry (for testing).
    public TestConfigEntry? GetTestConfigEntry(global::Ts.TsPath.Path path)
    {
        if (configs.TryGetValue(path, out ConfigFileEntry? entry))
        {
            return new TestConfigEntry
            {
                FileName = entry.fileName,
                RetainingProjects = KeysOf(entry.retainingProjects),
                RetainingOpenFiles = KeysOf(entry.retainingOpenFiles),
                RetainingConfigs = KeysOf(entry.retainingConfigs),
            };
        }
        return null;
    }

    // configfileregistry.go:154-157 — TestConfigFileNamesEntry (for testing).
    public sealed class TestConfigFileNamesEntry
    {
        public string NearestConfigFileName = "";

        public Dictionary<string, string> Ancestors = [];
    }

    // configfileregistry.go:160-169 — ForEachTestConfigFileNamesEntry (for testing).
    public void ForEachTestConfigFileNamesEntry(System.Action<global::Ts.TsPath.Path, TestConfigFileNamesEntry> cb)
    {
        // map-iter: 序无关 — test-only enumeration of file-names cache entries.
        foreach ((global::Ts.TsPath.Path path, ConfigFileNames entry) in configFileNames)
        {
            cb(path, new TestConfigFileNamesEntry
            {
                NearestConfigFileName = entry.nearestConfigFileName,
                Ancestors = entry.ancestors,
            });
        }
    }

    // configfileregistry.go:172-182 — GetTestConfigFileNamesEntry (for testing).
    public TestConfigFileNamesEntry? GetTestConfigFileNamesEntry(global::Ts.TsPath.Path path)
    {
        if (configFileNames.TryGetValue(path, out ConfigFileNames? entry))
        {
            return new TestConfigFileNamesEntry
            {
                NearestConfigFileName = entry.nearestConfigFileName,
                Ancestors = entry.ancestors,
            };
        }
        return null;
    }

    private static global::Ts.TsPath.Path[] KeysOf(Set<global::Ts.TsPath.Path>? set)
    {
        if (set is null || set.M is null)
        {
            return [];
        }
        var result = new List<global::Ts.TsPath.Path>(set.M);
        return result.ToArray();
    }
}

// configfileregistry.go:25-50 — configFileEntry. (Implements dirty.Cloneable — Go's
// exported Clone satisfies the dirty.SyncMap value constraint.)
internal sealed class ConfigFileEntry : global::Ts.Project.Dirty.Cloneable<ConfigFileEntry>
{
    internal string fileName = "";
    internal PendingReload pendingReload;
    internal global::Ts.TsOptions.ParsedCommandLine? commandLine;

    // retainingProjects is the set of projects that have called acquireConfig
    // without releasing it. A config file entry may be acquired by a project
    // either because it is the config for that project or because it is the
    // config for a referenced project.
    internal Set<global::Ts.TsPath.Path> retainingProjects = new();

    // retainingOpenFiles is the set of open files that caused this config to
    // load during project collection building. This config file may or may not
    // end up being the config for the default project for these files, but
    // determining the default project loaded this config as a candidate, so
    // subsequent calls to `projectCollectionBuilder.findDefaultConfiguredProject`
    // will use this config as part of the search, so it must be retained.
    internal Set<global::Ts.TsPath.Path> retainingOpenFiles = new();

    // retainingConfigs is the set of config files that extend this one. This
    // provides a cheap reverse mapping for a project config's
    // `commandLine.ExtendedSourceFiles()` that can be used to notify the
    // extending projects when this config changes. An extended config file may
    // or may not also be used directly by a project, so it's possible that
    // when this is set, no other fields will be used.
    internal Set<global::Ts.TsPath.Path> retainingConfigs = new();

    // rootFilesWatch is a watch for the root files of this config file.
    internal WatchedFiles<PatternsAndIgnored>? rootFilesWatch;

    // configfileregistry.go:52-63 — newConfigFileEntry.
    internal static ConfigFileEntry NewConfigFileEntry(bool hasRelativePatternCapability, string fileName)
    {
        return new ConfigFileEntry
        {
            fileName = fileName,
            pendingReload = PendingReload.Full,
            rootFilesWatch = WatchedFiles<PatternsAndIgnored>.NewWatchedFiles(
                "root files for " + fileName,
                WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                hasRelativePatternCapability,
                static input => input), // core.Identity
        };
    }

    // configfileregistry.go:65-71 — newExtendedConfigFileEntry.
    internal static ConfigFileEntry NewExtendedConfigFileEntry(string fileName, global::Ts.TsPath.Path extendingConfigPath)
    {
        var entry = new ConfigFileEntry
        {
            fileName = fileName,
            pendingReload = PendingReload.Full,
        };
        entry.retainingConfigs = new Set<global::Ts.TsPath.Path>();
        entry.retainingConfigs.Add(extendingConfigPath);
        return entry;
    }

    // configfileregistry.go:73-85 — Clone.
    public ConfigFileEntry Clone()
    {
        return new ConfigFileEntry
        {
            fileName = fileName,
            pendingReload = pendingReload,
            commandLine = commandLine,
            // !!! eagerly cloning these maps makes everything more convenient,
            // but it could be avoided if needed.
            retainingProjects = retainingProjects.Clone() ?? new Set<global::Ts.TsPath.Path>(),
            retainingOpenFiles = retainingOpenFiles.Clone() ?? new Set<global::Ts.TsPath.Path>(),
            retainingConfigs = retainingConfigs.Clone() ?? new Set<global::Ts.TsPath.Path>(),
            rootFilesWatch = rootFilesWatch,
        };
    }
}

// configfileregistry.go:184-202 — configFileNames. (Implements dirty.Cloneable for the
// dirty.Map value constraint.)
internal sealed class ConfigFileNames : global::Ts.Project.Dirty.Cloneable<ConfigFileNames>
{
    // nearestConfigFileName is the file name of the nearest ancestor config file.
    internal string nearestConfigFileName = "";

    // ancestors is a map from one ancestor config file path to the next.
    // For example, if `/a`, `/a/b`, and `/a/b/c` all contain config files,
    // the fully loaded map will look like:
    //		{
    //			"/a/b/c/tsconfig.json": "/a/b/tsconfig.json",
    //			"/a/b/tsconfig.json": "/a/tsconfig.json"
    //		}
    internal Dictionary<string, string> ancestors = [];

    // configfileregistry.go:197-202 — Clone.
    public ConfigFileNames Clone()
    {
        return new ConfigFileNames
        {
            nearestConfigFileName = nearestConfigFileName,
            ancestors = new Dictionary<string, string>(ancestors),
        };
    }
}
