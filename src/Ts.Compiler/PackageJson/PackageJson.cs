// Ported from tsgo internal/packagejson/packagejson.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Text.Json;
using Ts.Collections;

namespace Ts.PackageJson;

public struct HeaderFields
{
    public Expected<string> Name;
    public Expected<string> Version;
    public Expected<string> Type;
}

public struct PathFields
{
    public Expected<string> TSConfig;
    public Expected<string> Main;
    public Expected<string> Types;
    public Expected<string> Typings;
    public JSONValue TypesVersions;
    public ExportsOrImports Imports;
    public ExportsOrImports Exports;
}

public struct DependencyFields
{
    public Expected<Dictionary<string, string>> Dependencies;
    public Expected<Dictionary<string, string>> DevDependencies;
    public Expected<Dictionary<string, string>> PeerDependencies;
    public Expected<Dictionary<string, string>> OptionalDependencies;

    // HasDependency returns true if the package.json has a dependency with the given name
    // under any of the dependency fields.
    public readonly bool HasDependency(string name)
    {
        if (Dependencies.GetValue(out Dictionary<string, string>? deps) && deps!.ContainsKey(name))
        {
            return true;
        }
        if (DevDependencies.GetValue(out Dictionary<string, string>? devDeps) && devDeps!.ContainsKey(name))
        {
            return true;
        }
        if (PeerDependencies.GetValue(out Dictionary<string, string>? peerDeps) && peerDeps!.ContainsKey(name))
        {
            return true;
        }
        if (OptionalDependencies.GetValue(out Dictionary<string, string>? optDeps) && optDeps!.ContainsKey(name))
        {
            return true;
        }
        return false;
    }

    public readonly void RangeDependencies(Func<string, string, string, bool> f)
    {
        if (Dependencies.GetValue(out Dictionary<string, string>? deps))
        {
            // map-iter: 序无关 (consumer f is called for every entry; Go ranges an unordered map).
            foreach (KeyValuePair<string, string> kv in deps!)
            {
                if (!f(kv.Key, kv.Value, "dependencies"))
                {
                    return;
                }
            }
        }
        if (DevDependencies.GetValue(out Dictionary<string, string>? devDeps))
        {
            // map-iter: 序无关
            foreach (KeyValuePair<string, string> kv in devDeps!)
            {
                if (!f(kv.Key, kv.Value, "devDependencies"))
                {
                    return;
                }
            }
        }
        if (PeerDependencies.GetValue(out Dictionary<string, string>? peerDeps))
        {
            // map-iter: 序无关
            foreach (KeyValuePair<string, string> kv in peerDeps!)
            {
                if (!f(kv.Key, kv.Value, "peerDependencies"))
                {
                    return;
                }
            }
        }
        if (OptionalDependencies.GetValue(out Dictionary<string, string>? optDeps))
        {
            // map-iter: 序无关
            foreach (KeyValuePair<string, string> kv in optDeps!)
            {
                if (!f(kv.Key, kv.Value, "optionalDependencies"))
                {
                    return;
                }
            }
        }
    }

    public readonly Set<string> GetRuntimeDependencyNames()
    {
        int count = 0;
        Dependencies.GetValue(out Dictionary<string, string>? deps);
        count += deps?.Count ?? 0;
        PeerDependencies.GetValue(out Dictionary<string, string>? peerDeps);
        count += peerDeps?.Count ?? 0;
        OptionalDependencies.GetValue(out Dictionary<string, string>? optDeps);
        count += optDeps?.Count ?? 0;
        var names = new Set<string>(count);
        if (deps != null)
        {
            // map-iter: 序无关 (building a Set).
            foreach (string name in deps.Keys)
            {
                names.Add(name);
            }
        }
        if (peerDeps != null)
        {
            // map-iter: 序无关
            foreach (string name in peerDeps.Keys)
            {
                names.Add(name);
            }
        }
        if (optDeps != null)
        {
            // map-iter: 序无关
            foreach (string name in optDeps.Keys)
            {
                names.Add(name);
            }
        }
        return names;
    }
}

// Go embeds HeaderFields, PathFields, DependencyFields (packagejson.go:110-114).
public sealed class Fields
{
    public HeaderFields HeaderFields;
    public PathFields PathFields;
    public DependencyFields DependencyFields;
}

// Go: package-level functions of packagejson.go.
public static class PackageJsonParser
{
    // Go: func Parse(data []byte) (Fields, error) (packagejson.go:116-122). Duplicate
    // names are allowed (json.AllowDuplicateNames(true)); Utf8JsonReader does not reject
    // them and last-writer-wins matches Go's struct-field decode.
    public static (Fields Fields, bool Ok) Parse(ReadOnlySpan<byte> data)
    {
        var fields = new Fields();
        try
        {
            var reader = new Utf8JsonReader(data);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (new Fields(), false);
            }
            ReadFields(ref reader, fields);
            return (fields, true);
        }
        catch (JsonException)
        {
            return (new Fields(), false);
        }
    }

    private static void ReadFields(ref Utf8JsonReader reader, Fields fields)
    {
        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }
            // reader.TokenType == PropertyName
            string key = reader.GetString()!;
            reader.Read();
            switch (key)
            {
                case "name":
                    fields.HeaderFields.Name = Expected.ReadString(ref reader);
                    break;
                case "version":
                    fields.HeaderFields.Version = Expected.ReadString(ref reader);
                    break;
                case "type":
                    fields.HeaderFields.Type = Expected.ReadString(ref reader);
                    break;
                case "tsconfig":
                    fields.PathFields.TSConfig = Expected.ReadString(ref reader);
                    break;
                case "main":
                    fields.PathFields.Main = Expected.ReadString(ref reader);
                    break;
                case "types":
                    fields.PathFields.Types = Expected.ReadString(ref reader);
                    break;
                case "typings":
                    fields.PathFields.Typings = Expected.ReadString(ref reader);
                    break;
                case "typesVersions":
                    fields.PathFields.TypesVersions = JSONValue.Read(ref reader);
                    break;
                case "imports":
                    fields.PathFields.Imports = ExportsOrImports.Read(ref reader);
                    break;
                case "exports":
                    fields.PathFields.Exports = ExportsOrImports.Read(ref reader);
                    break;
                case "dependencies":
                    fields.DependencyFields.Dependencies = Expected.ReadStringMap(ref reader);
                    break;
                case "devDependencies":
                    fields.DependencyFields.DevDependencies = Expected.ReadStringMap(ref reader);
                    break;
                case "peerDependencies":
                    fields.DependencyFields.PeerDependencies = Expected.ReadStringMap(ref reader);
                    break;
                case "optionalDependencies":
                    fields.DependencyFields.OptionalDependencies = Expected.ReadStringMap(ref reader);
                    break;
                default:
                    // Unknown key: skip the value (Skip is a no-op for a scalar already positioned).
                    reader.Skip();
                    break;
            }
        }
    }
}
