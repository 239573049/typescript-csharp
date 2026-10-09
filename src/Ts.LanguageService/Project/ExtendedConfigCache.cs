// Ported from tsgo internal/project/extendedconfigcache.go (whole file).
// PORT-NOTE: Go's `type ExtendedConfigCache = OwnerCache[...]` alias becomes a
// subclass here (C# has no cross-file type aliases); the API surface is identical.

using System;
using System.IO.Hashing;
using System.Text;

namespace Ts.Project;

// extendedconfigcache.go:10-17 — ExtendedConfigParseArgs.
public sealed class ExtendedConfigParseArgs
{
    public required string FileName { get; init; }

    public required string Content { get; init; }

    public required FileSource FS { get; init; }

    public required global::Ts.TsPath.Path[] ResolutionStack { get; init; }

    public required global::Ts.TsOptions.ParseConfigHost Host { get; init; }

    public required global::Ts.TsOptions.ExtendedConfigCache Cache { get; init; }
}

// extendedconfigcache.go:19-22 — ExtendedConfigCacheEntry (embeds the tsoptions entry + hash).
public sealed class ExtendedConfigCacheEntry
{
    public required global::Ts.TsOptions.ExtendedConfigCacheEntry Entry { get; init; }

    public UInt128 Hash { get; set; }
}

// extendedconfigcache.go:24 — type ExtendedConfigCache = OwnerCache[tspath.Path, *ExtendedConfigCacheEntry, ExtendedConfigParseArgs].
public sealed class ExtendedConfigCache : OwnerCache<global::Ts.TsPath.Path, ExtendedConfigCacheEntry, ExtendedConfigParseArgs>
{
    private ExtendedConfigCache(
        Func<global::Ts.TsPath.Path, ExtendedConfigParseArgs, ExtendedConfigCacheEntry> parse,
        Func<global::Ts.TsPath.Path, ExtendedConfigCacheEntry, ExtendedConfigParseArgs, bool> isExpired)
        : base(parse, isExpired)
    {
    }

    // extendedconfigcache.go:26-39 — NewExtendedConfigCache.
    public static ExtendedConfigCache NewExtendedConfigCache()
    {
        return new ExtendedConfigCache(
            (path, args) =>
            {
                var result = new ExtendedConfigCacheEntry
                {
                    Entry = global::Ts.TsOptions.TsConfigParsing.ParseExtendedConfig(
                        args.FileName,
                        path,
                        args.ResolutionStack,
                        args.Host,
                        args.Cache),
                };
                result.Hash = Hash(result.Entry, args);
                return result;
            },
            (path, entry, args) =>
            {
                return entry.Hash == UInt128.MinValue || entry.Hash != Hash(entry.Entry, args);
            });
    }

    // extendedconfigcache.go:41-53 — hash.
    private static UInt128 Hash(global::Ts.TsOptions.ExtendedConfigCacheEntry entry, ExtendedConfigParseArgs args)
    {
        var hasher = new XxHash128();
        hasher.Append(Encoding.UTF8.GetBytes(args.Content));
        if (entry.ExtendedFileNames() is { } extendedFileNames)
        {
            foreach (string fileName in extendedFileNames)
            {
                FileHandle? fh = args.FS.GetFile(fileName);
                if (fh is null)
                {
                    return UInt128.MinValue;
                }
                hasher.Append(Encoding.UTF8.GetBytes(fh.Content()));
            }
        }
        return hasher.GetCurrentHashAsUInt128();
    }
}
