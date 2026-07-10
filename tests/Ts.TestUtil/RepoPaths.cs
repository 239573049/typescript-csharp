using System;
using System.IO;

namespace Ts.TestUtil;

// Go: internal/repo/paths.go — test/generator-only repo location helpers; must never
// be referenced from product assemblies (06-module-inventory.md repo row).
// Go found the root via runtime.Caller + go.mod; here we walk up from the test
// assembly's directory looking for TypeScript.slnx.
public static class RepoPaths
{
    private static readonly Lazy<string> s_rootPath = new(() =>
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "TypeScript.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
        }
        throw new InvalidOperationException($"could not find TypeScript.slnx above {AppContext.BaseDirectory}");
    });

    private static readonly Lazy<string> s_typeScriptSubmodulePath = new(() =>
        Path.Combine(RootPath, "_submodules", "TypeScript"));

    private static readonly Lazy<string> s_testDataPath = new(() =>
        Path.Combine(RootPath, "testdata"));

    private static readonly Lazy<bool> s_typeScriptSubmoduleExists = new(() =>
        File.Exists(Path.Combine(TypeScriptSubmodulePath, "package.json")));

    public static string RootPath => s_rootPath.Value;

    public static string TypeScriptSubmodulePath => s_typeScriptSubmodulePath.Value;

    public static string TestDataPath => s_testDataPath.Value;

    public static bool TypeScriptSubmoduleExists => s_typeScriptSubmoduleExists.Value;

    // Go: SkipIfNoTypeScriptSubmodule(t) — xunit v3 supports dynamic skip.
    public static void SkipIfNoTypeScriptSubmodule()
    {
        Xunit.Assert.SkipUnless(TypeScriptSubmoduleExists, "TypeScript submodule does not exist");
    }
}
