// Ported from tsgo internal/modulespecifiers/specifiers_test.go — the mock host plus the
// GetEachFileNameOfModule tests.
//
// PORT-NOTES:
//  * mockModuleSpecifierGenerationHost (specifiers_test.go:16-80) is transcribed as
//    MockModuleSpecifierGenerationHost. GetEachFileNameOfModule never reaches FileExists, so the
//    mock's "always true" arm is kept only for shape.
//  * Ported here: TestGetEachFileNameOfModule (specifiers_test.go:82-157) and
//    TestGetEachFileNameOfModuleWithSymlinks (specifiers_test.go:159-188).
//  * NOT ported: TestContainsNodeModules (specifiers_test.go:190-228) — ContainsNodeModules
//    (Specifiers.cs:804) lives in Ts.ModuleSpecifiers, but the same predicate is already covered by
//    Ts.TsPath.Tests / Ts.ModuleSpecifiers.Tests neighbours; TestContainsIgnoredPath (:230-258) is
//    exactly internal/mspath's ignoredpaths_test.go → tests/Ts.Compiler.Tests/TsPath/IgnoredPathsTests.cs;
//    TestTryGetRealFileNameForNonJSDeclarationFileName (:260-292) needs only
//    ModuleSpecifierUtil.TryGetRealFileNameForNonJSDeclarationFileName (Util.cs:146) and is a
//    candidate for a later batch; TestTryGetModuleNameFromExportsOrImports (:294-342) needs
//    Specifiers.TryGetModuleNameFromExportsOrImports (Specifiers.PackageJson.cs:133), also a later
//    batch. They are omitted here to keep this file to what the audit asked for.
//  * t.Parallel() dropped (05-conventions.md §16.1); t.Run subtests stay inline in one [Fact] per Go
//    test function, mirroring tests/Ts.Compiler.Tests/Vfs/VfsMatchTests.cs.
//  * gotest.tools `assert.X` → xUnit Assert with the argument order swapped where relevant.

using System;
using System.Collections.Generic;

using Ts.Core;
using Ts.Module;
using Ts.PackageJson;
using Ts.Symlinks;
using Ts.TsOptions;

using Path = Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;
using Xunit;

namespace Ts.ModuleSpecifiers.Tests;

// specifiers_test.go:16-80 — Mock host for testing.
internal sealed class MockModuleSpecifierGenerationHost : ModuleSpecifierGenerationHost
{
    public string CurrentDir = "";
    public bool UseCaseSensitiveFileNamesField;
    public KnownSymlinks? SymlinkCache;

    // specifiers_test.go:22-24.
    public string GetCurrentDirectory() => CurrentDir;

    // specifiers_test.go:26-28.
    public bool UseCaseSensitiveFileNames() => UseCaseSensitiveFileNamesField;

    // specifiers_test.go:30-32.
    public KnownSymlinks GetSymlinkCache() => SymlinkCache!;

    // specifiers_test.go:38-40.
    public string GetGlobalTypingsCacheLocation() => "";

    // specifiers_test.go:42-44.
    public string CommonSourceDirectory() => CurrentDir;

    // specifiers_test.go:46-48.
    public SourceOutputAndProjectReference? GetProjectReferenceFromSource(Path path) => null;

    // specifiers_test.go:50-52.
    public string[] GetRedirectTargets(Path path) => [];

    // specifiers_test.go:54-56.
    public string GetSourceOfProjectReferenceIfOutputIncluded(Ts.Ast.HasFileName file) => file.FileName();

    // specifiers_test.go:58-60.
    public bool FileExists(string path) => true; // Mock implementation

    // specifiers_test.go:62-64.
    public string GetNearestAncestorDirectoryWithPackageJson(string dirname) => "";

    // specifiers_test.go:66-68.
    public InfoCacheEntry? GetPackageJsonInfo(string pkgJsonPath) => null;

    // specifiers_test.go:70-72.
    // PORT-NOTE: Go's `ResolutionMode` is a global alias (`type ResolutionMode = ModuleKind`,
    // GlobalUsings.cs:9) that does not cross an assembly boundary, so the test spells out ModuleKind.
    public ModuleKind GetDefaultResolutionModeForFile(Ts.Ast.HasFileName file) => ResolutionModeNone;

    // specifiers_test.go:74-76.
    public ResolvedModule? GetResolvedModuleFromModuleSpecifier(Ts.Ast.HasFileName file, Ts.Ast.Node? moduleSpecifier) => null;

    // specifiers_test.go:78-80.
    public ModuleKind GetModeForUsageLocation(Ts.Ast.HasFileName file, Ts.Ast.Node? moduleSpecifier) => ResolutionModeNone;

    // Go: core.ResolutionModeNone (compileroptions.go:443) — C# keeps it on the Core class.
    private const ModuleKind ResolutionModeNone = global::Ts.Core.Core.ResolutionModeNone;
}

public sealed class SpecifiersTests
{
    // specifiers_test.go:82-157 — TestGetEachFileNameOfModule.
    [Fact]
    public void GetEachFileNameOfModule()
    {
        var tests = new (string Name, string ImportingFile, string ImportedFile, bool PreferSymlinks, int ExpectedCount, string[]? ExpectedPaths)[]
        {
            ("basic file path", "/project/src/main.ts", "/project/lib/utils.ts", false, 1, ["/project/lib/utils.ts"]),
            ("symlink preference false", "/project/src/main.ts", "/project/lib/utils.ts", false, 1, null),
            ("symlink preference true", "/project/src/main.ts", "/project/lib/utils.ts", true, 1, null),
            // Should return 1 because there's no better option (all paths are ignored)
            ("ignored path with no alternatives", "/project/src/main.ts", "/project/node_modules/.pnpm/file.ts", false, 1, null),
        };

        foreach (var tt in tests)
        {
            var host = new MockModuleSpecifierGenerationHost
            {
                CurrentDir = "/project",
                UseCaseSensitiveFileNamesField = true,
                SymlinkCache = new KnownSymlinks("/project", true),
            };

            ModulePath[] result = Specifiers.GetEachFileNameOfModule(tt.ImportingFile, tt.ImportedFile, host, tt.PreferSymlinks);

            // Go: t.Errorf("Expected %d paths, got %d", tt.expectedCount, len(result))
            Assert.True(tt.ExpectedCount == result.Length, $"[{tt.Name}] expected {tt.ExpectedCount} paths, got {result.Length}");

            if (tt.ExpectedPaths != null)
            {
                for (int i = 0; i < tt.ExpectedPaths.Length; i++)
                {
                    if (i >= result.Length)
                    {
                        // Go: t.Errorf("Expected path %d: %s, but result has only %d paths", ...)
                        Assert.Fail($"[{tt.Name}] expected path {i}: {tt.ExpectedPaths[i]}, but result has only {result.Length} paths");
                        continue;
                    }
                    if (result[i].FileName != tt.ExpectedPaths[i])
                    {
                        // Go: t.Errorf("Expected path %d to be %s, got %s", ...)
                        Assert.Fail($"[{tt.Name}] expected path {i} to be {tt.ExpectedPaths[i]}, got {result[i].FileName}");
                    }
                }
            }

            for (int i = 0; i < result.Length; i++)
            {
                // Go: t.Errorf("Path %d has empty FileName", i)
                Assert.True(result[i].FileName != "", $"[{tt.Name}] path {i} has empty FileName");
            }
        }
    }

    // specifiers_test.go:159-188 — TestGetEachFileNameOfModuleWithSymlinks.
    [Fact]
    public void GetEachFileNameOfModuleWithSymlinks()
    {
        var host = new MockModuleSpecifierGenerationHost
        {
            CurrentDir = "/project",
            UseCaseSensitiveFileNamesField = true,
            SymlinkCache = new KnownSymlinks("/project", true),
        };

        Path symlinkPath = TsPathApi.ToPath("/project/symlink", "/project", true).EnsureTrailingDirectorySeparator();
        var realDirectory = new KnownDirectoryLink
        {
            Real = "/real/path/",
            RealPath = TsPathApi.ToPath("/real/path", "/project", true).EnsureTrailingDirectorySeparator(),
        };
        host.SymlinkCache.SetDirectory("/project/symlink", symlinkPath, realDirectory);

        ModulePath[] result = Specifiers.GetEachFileNameOfModule("/project/src/main.ts", "/real/path/file.ts", host, true);

        // Should find the symlink path
        bool found = false;
        foreach (ModulePath path in result)
        {
            if (path.FileName == "/project/symlink/file.ts")
            {
                found = true;
                break;
            }
        }

        // Go: t.Error("Expected to find symlink path /project/symlink/file.ts")
        Assert.True(found, "Expected to find symlink path /project/symlink/file.ts");
    }
}
