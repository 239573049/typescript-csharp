// Ported from tsgo internal/packagejson/cache.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;

namespace Ts.PackageJson;

// PORT-NOTE: these aliases are placed INSIDE the file-scoped namespace so they
// outrank the enclosing `Ts` namespace's sibling sub-namespaces (e.g. the bare
// name `Diagnostics` would otherwise resolve to the namespace `Ts.Diagnostics`).
using Diagnostics = Ts.Diagnostics.Diagnostics;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath;
using Path = Ts.TsPath.Path;
using Version = Ts.Semver.Version;
using VersionRange = Ts.Semver.VersionRange;
using SemverApi = Ts.Semver.Semver;

// Go: func(m *diagnostics.Message, args ...any). Used by GetVersionPaths to replay
// version-resolution traces.
public delegate void TraceFunc(Message message, params object?[] args);

// Go: internal/packagejson diagnosticAndArgs (cache.go:23-26).
public readonly struct DiagnosticAndArgs
{
    public readonly Message Message;
    public readonly object?[] Args;

    public DiagnosticAndArgs(Message message, object?[] args)
    {
        Message = message;
        Args = args;
    }
}

public sealed class PackageJson
{
    private static readonly Version TypeScriptVersion = SemverApi.MustParse(TsVersion.Version());

    public Fields Fields = new();
    public bool Parseable;

    // Go: sync.Once (cache.go:20). The traces are accumulated exactly once and then
    // replayed on every call whose trace is non-nil.
    private readonly object gate = new();
    private bool computed;
    private VersionPaths versionPaths;
    private readonly List<DiagnosticAndArgs> versionTraces = new();

    // Go embeds Fields -> HeaderFields/PathFields/DependencyFields; forward the members
    // the resolver reaches through Contents.*.
    public Expected<string> Name => Fields.HeaderFields.Name;
    public Expected<string> Version => Fields.HeaderFields.Version;
    public Expected<string> Type => Fields.HeaderFields.Type;
    public Expected<string> TSConfig => Fields.PathFields.TSConfig;
    public Expected<string> Main => Fields.PathFields.Main;
    public Expected<string> Types => Fields.PathFields.Types;
    public Expected<string> Typings => Fields.PathFields.Typings;
    public JSONValue TypesVersions => Fields.PathFields.TypesVersions;
    public ExportsOrImports Imports => Fields.PathFields.Imports;
    public ExportsOrImports Exports => Fields.PathFields.Exports;
    public DependencyFields DependencyFields => Fields.DependencyFields;
    public Expected<Dictionary<string, string>> PeerDependencies => Fields.DependencyFields.PeerDependencies;

    public VersionPaths GetVersionPaths(TraceFunc? trace)
    {
        lock (gate)
        {
            if (!computed)
            {
                computed = true;
                ComputeVersionPaths();
            }
        }
        if (trace != null)
        {
            foreach (DiagnosticAndArgs msg in versionTraces)
            {
                trace(msg.Message, msg.Args);
            }
        }
        return versionPaths;
    }

    private void ComputeVersionPaths()
    {
        JSONValue typesVersions = Fields.PathFields.TypesVersions;
        if (typesVersions.Type == JSONValueType.JSONValueTypeNotPresent)
        {
            versionTraces.Add(new DiagnosticAndArgs(
                Diagnostics.X_package_json_does_not_have_a_0_field,
                new object?[] { "typesVersions" }));
            return;
        }
        if (typesVersions.Type != JSONValueType.JSONValueTypeObject)
        {
            versionTraces.Add(new DiagnosticAndArgs(
                Diagnostics.Expected_type_of_0_field_in_package_json_to_be_1_got_2,
                new object?[] { "typesVersions", "object", JSONValue.ToDisplayString(typesVersions.Type) }));
            return;
        }

        versionTraces.Add(new DiagnosticAndArgs(
            Diagnostics.X_package_json_has_a_typesVersions_field_with_version_specific_path_mappings,
            new object?[] { "typesVersions" }));

        // map-iter: 已排序 (OrderedMap preserves package.json order; Go ranges the same OrderedMap).
        foreach (KeyValuePair<string, JSONValue> entry in typesVersions.AsObject().Entries())
        {
            string key = entry.Key;
            JSONValue value = entry.Value;
            if (!SemverApi.TryParseVersionRange(key, out VersionRange keyRange))
            {
                versionTraces.Add(new DiagnosticAndArgs(
                    Diagnostics.X_package_json_has_a_typesVersions_entry_0_that_is_not_a_valid_semver_range,
                    new object?[] { key }));
                continue;
            }
            if (keyRange.Test(TypeScriptVersion))
            {
                if (value.Type != JSONValueType.JSONValueTypeObject)
                {
                    versionTraces.Add(new DiagnosticAndArgs(
                        Diagnostics.Expected_type_of_0_field_in_package_json_to_be_1_got_2,
                        new object?[] { "typesVersions['" + key + "']", "object", JSONValue.ToDisplayString(value.Type) }));
                    return;
                }
                versionPaths = new VersionPaths(key, value.AsObject());
                return;
            }
        }

        versionTraces.Add(new DiagnosticAndArgs(
            Diagnostics.X_package_json_does_not_have_a_typesVersions_entry_that_matches_version_0,
            new object?[] { TsVersion.VersionMajorMinor() }));
    }
}

public struct VersionPaths
{
    public string Version;
    private readonly OrderedMap<string, JSONValue>? pathsJSON;
    private OrderedMap<string, string[]>? paths;

    internal VersionPaths(string version, OrderedMap<string, JSONValue> pathsJSON)
    {
        Version = version;
        this.pathsJSON = pathsJSON;
        paths = null;
    }

    public readonly bool Exists()
    {
        return Version != "" && Version != null && pathsJSON != null;
    }

    public OrderedMap<string, string[]>? GetPaths()
    {
        if (!Exists())
        {
            return null;
        }
        if (paths != null)
        {
            return paths;
        }
        var result = new OrderedMap<string, string[]>(pathsJSON!.Size());
        // map-iter: 已排序 (OrderedMap iteration matches Go's pathsJSON.Entries()).
        foreach (KeyValuePair<string, JSONValue> entry in pathsJSON.Entries())
        {
            JSONValue value = entry.Value;
            if (value.Type != JSONValueType.JSONValueTypeArray)
            {
                continue;
            }
            List<JSONValue> array = value.AsArray();
            var slice = new string[array.Count];
            // Go: make([]string, n) zero-fills with "" — mirror that (C# default is null).
            Array.Fill(slice, "");
            for (int i = 0; i < array.Count; i++)
            {
                JSONValue path = array[i];
                if (path.Type != JSONValueType.JSONValueTypeString)
                {
                    continue;
                }
                slice[i] = (string)path.Value!;
            }
            result.Set(entry.Key, slice);
        }
        paths = result;
        return paths;
    }
}

public sealed class InfoCacheEntry
{
    public string PackageDirectory = "";
    public bool DirectoryExists;
    public PackageJson? Contents;

    // WithPackageDirectory returns an entry whose PackageDirectory matches the
    // caller's value. See https://github.com/microsoft/TypeScript/pull/50740.
    public InfoCacheEntry WithPackageDirectory(string packageDirectory)
    {
        if (PackageDirectory == packageDirectory)
        {
            return this;
        }
        return new InfoCacheEntry
        {
            PackageDirectory = packageDirectory,
            DirectoryExists = DirectoryExists,
            Contents = Contents,
        };
    }
}

// Go nil-receiver methods (cache.go:129-145) → nil-tolerant extension methods.
public static class InfoCacheEntryExtensions
{
    public static bool Exists(this InfoCacheEntry? p)
    {
        return p != null && p.Contents != null;
    }

    public static PackageJson? GetContents(this InfoCacheEntry? p)
    {
        if (p == null || p.Contents == null)
        {
            return null;
        }
        return p.Contents;
    }

    public static string GetDirectory(this InfoCacheEntry? p)
    {
        if (p == null)
        {
            return "";
        }
        return p.PackageDirectory;
    }
}

public sealed class InfoCache
{
    private readonly SyncMap<Path, InfoCacheEntry> cache = new();
    private readonly string currentDirectory;
    private readonly bool useCaseSensitiveFileNames;

    public InfoCache(string currentDirectory, bool useCaseSensitiveFileNames)
    {
        this.currentDirectory = currentDirectory;
        this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
    }

    public InfoCacheEntry? Get(string packageJsonPath)
    {
        Path key = TsPathApi.ToPath(packageJsonPath, currentDirectory, useCaseSensitiveFileNames);
        if (cache.TryLoad(key, out InfoCacheEntry? value))
        {
            return value;
        }
        return null;
    }

    public InfoCacheEntry Set(string packageJsonPath, InfoCacheEntry info)
    {
        Path key = TsPathApi.ToPath(packageJsonPath, currentDirectory, useCaseSensitiveFileNames);
        (InfoCacheEntry actual, _) = cache.LoadOrStore(key, info);
        return actual;
    }

    public void Range(Func<Path, InfoCacheEntry, bool> f)
    {
        cache.Range(f);
    }
}
