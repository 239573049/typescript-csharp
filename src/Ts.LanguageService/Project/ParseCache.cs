// Ported from tsgo internal/project/parsecache.go (whole file).

using System;

namespace Ts.Project;

// parsecache.go:12-16 — ParseCacheKey. Go embeds ast.SourceFileParseOptions; the C#
// struct is carried as an explicit field (same layout, same comparability semantics:
// Path is the identity member).
public readonly record struct ParseCacheKey(
    global::Ts.Ast.SourceFileParseOptions SourceFileParseOptions,
    UInt128 Hash,
    global::Ts.Core.ScriptKind ScriptKind);

public static class ParseCache
{
    // parsecache.go:18-24 — NewParseCacheKey.
    public static ParseCacheKey NewParseCacheKey(
        global::Ts.Ast.SourceFileParseOptions options,
        UInt128 hash,
        global::Ts.Core.ScriptKind scriptKind)
    {
        return new ParseCacheKey
        {
            SourceFileParseOptions = options,
            Hash = hash,
            ScriptKind = scriptKind,
        };
    }

    // parsecache.go:26 — type ParseCache = RefCountCache[ParseCacheKey, *ast.SourceFile, FileHandle].
    public static RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> NewParseCache(
        RefCountCacheOptions options)
    {
        return RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle>.NewRefCountCache(
            options,
            (key, fh) =>
            {
                global::Ts.Ast.SourceFile file = global::Ts.Parser.Parser.ParseSourceFile(
                    key.SourceFileParseOptions,
                    fh.Content(),
                    key.ScriptKind);
                file.Hash = fh.Hash();
                return file;
            });
    }
}
