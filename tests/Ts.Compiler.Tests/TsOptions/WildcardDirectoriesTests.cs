// Ported from tsgo internal/tsoptions/wildcarddirectories_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTEs:
//  * The Go test is in package tsoptions (internal), so it reaches the unexported
//    getWildcardDirectories directly; the C# equivalent is internal + InternalsVisibleTo
//    (Ts.Compiler.csproj:11-13, 05-conventions.md §16).
//  * Go `assert.DeepEqual(t, result, map[string]bool{...})` → key-wise assertions on the
//    Dictionary<string, bool> the accessor returns (05-conventions.md §16.3).
//  * t.Parallel() → deleted (xUnit collection-level parallelism, §16.1).

using System.Collections.Generic;
using Ts.TsOptions;
using Ts.TsPath;
using Xunit;

namespace Ts.TsOptions.Tests;

public class WildcardDirectoriesTests
{
    // wildcarddirectories_test.go:10-27 — TestGetWildcardDirectories_DotPrefixedIncludeWithDotDirExclude.
    [Fact]
    public void GetWildcardDirectories_DotPrefixedIncludeWithDotDirExclude()
    {
        // https://github.com/microsoft/typescript-go/issues/3733
        // "./"-prefixed include specs must be fully normalized before being tested
        // against exclude patterns; otherwise the leftover literal "." path segment
        // matches dot-directory excludes like "**/.*/", silently dropping every
        // wildcard directory (and with them, root file watching for the config).
        Dictionary<string, bool>? result = WildcardDirectories.getWildcardDirectories(
            new[] { "./app/**/*.ts", "./app/**/*.tsx" },
            new[] { "**/node_modules", "**/.*/", "./build" },
            new ComparePathsOptions
            {
                CurrentDirectory = "/home/projects/monorepo/apps/web",
                UseCaseSensitiveFileNames = true,
            });

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.True(result!["/home/projects/monorepo/apps/web/app"]);
    }

    // wildcarddirectories_test.go:29-85 — TestGetWildcardDirectories_NonASCIICharacters.
    [Fact]
    public void GetWildcardDirectories_NonASCIICharacters()
    {
        var tests = new[]
        {
            (Name: "Norwegian character æ in path",
                Include: new[] { "src/**/*.test.ts", "src/**/*.stories.ts", "src/**/*.mdx" },
                Exclude: new[] { "node_modules" },
                CurrentDirectory: "C:/Users/TobiasLægreid/dev/app/frontend/packages/react",
                UseCaseSensitive: false),
            (Name: "Japanese characters in path",
                Include: new[] { "src/**/*.ts" },
                Exclude: new[] { "テスト" },
                CurrentDirectory: "/Users/ユーザー/プロジェクト",
                UseCaseSensitive: true),
            (Name: "Chinese characters in path",
                Include: new[] { "源代码/**/*.js" },
                Exclude: new[] { "节点模块" },
                CurrentDirectory: "/home/用户/项目",
                UseCaseSensitive: true),
            (Name: "Various Unicode characters",
                Include: new[] { "src/**/*.ts" },
                Exclude: new[] { "node_modules" },
                CurrentDirectory: "/Users/Müller/café/naïve/résumé",
                UseCaseSensitive: false),
        };

        foreach (var tt in tests)
        {
            var comparePathsOptions = new ComparePathsOptions
            {
                CurrentDirectory = tt.CurrentDirectory,
                UseCaseSensitiveFileNames = tt.UseCaseSensitive,
            };

            Dictionary<string, bool>? result = WildcardDirectories.getWildcardDirectories(tt.Include, tt.Exclude, comparePathsOptions);

            Assert.True(result != null, $"[{tt.Name}] expected non-nil result");
        }
    }
}
