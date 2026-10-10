// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildProjectsBuilding.

using System;
using System.Collections.Generic;
using System.Globalization;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildProjectsBuildingTests
{
    // tscbuild_test.go:2669-2680 — addPackageFiles.
    private static void AddPackageFiles(Dictionary<string, object> files, int index)
    {
        files[$"/user/username/projects/myproject/pkg{index}/index.ts"] = $"export const pkg{index} = {index};";
        var references = "";
        if (index > 0)
        {
            references = "\"references\": [{ \"path\": \"../pkg0\" }],";
        }
        files[$"/user/username/projects/myproject/pkg{index}/tsconfig.json"] = StringTestUtil.Dedent(
            $$"""
<T><T>{
<T><T><T>"compilerOptions": { "composite": true },
<T><T><T>{{references}}
<T><T>}
""".Replace("<T>", "\t", StringComparison.Ordinal));
    }

    // tscbuild_test.go:2681-2693 — addSolution.
    private static void AddSolution(Dictionary<string, object> files, int count)
    {
        var pkgReferences = new List<string>();
        for (var i = 0; i < count; i++)
        {
            pkgReferences.Add($$"""{ "path": "./pkg{{i}}" }""");
        }
        files["/user/username/projects/myproject/tsconfig.json"] = StringTestUtil.Dedent(
            $$"""
<T><T>{
<T><T><T>"compilerOptions": { "composite": true },
<T><T><T>"references": [
<T><T><T><T>{{string.Join(",\n\t\t\t\t", pkgReferences)}}
<T><T><T>]
<T><T>}
""".Replace("<T>", "\t", StringComparison.Ordinal));
    }

    // tscbuild_test.go:2694-2701 — files.
    private static Dictionary<string, object> GetFiles(int count)
    {
        var files = new Dictionary<string, object>();
        for (var i = 0; i < count; i++)
        {
            AddPackageFiles(files, i);
        }
        AddSolution(files, count);
        return files;
    }

    // tscbuild_test.go:2703-2750 — getTestCases.
    private static TscInput[] GetTestCases(int pkgCount, int builders)
    {
        var edits = new TscEdit[]
        {
            new TscEdit
            {
                caption = "dts doesn't change",
                edit = sys => sys.AppendFileText("/user/username/projects/myproject/pkg0/index.ts", "const someConst2 = 10;"),
            },
            TscEdit.NoChange,
            new TscEdit
            {
                caption = "dts change",
                edit = sys => sys.AppendFileText("/user/username/projects/myproject/pkg0/index.ts", "export const someConst = 10;"),
            },
            TscEdit.NoChange,
        };
        return
        [
            new TscInput
            {
                subScenario = $"when there are {pkgCount} projects in a solution",
                files = GetFiles(pkgCount),
                cwd = "/user/username/projects/myproject",
                commandLineArgs = ["-b", "-v"],
                edits = edits,
            },
            new TscInput
            {
                subScenario = $"when there are {pkgCount} projects in a solution with --builders {builders}",
                files = GetFiles(pkgCount),
                cwd = "/user/username/projects/myproject",
                commandLineArgs = ["-b", "-v", "--builders", builders.ToString(CultureInfo.InvariantCulture)],
                edits = edits,
            },
            new TscInput
            {
                subScenario = $"when there are {pkgCount} projects in a solution",
                files = GetFiles(pkgCount),
                cwd = "/user/username/projects/myproject",
                commandLineArgs = ["-b", "-w", "-v"],
                edits = edits,
            },
            new TscInput
            {
                subScenario = $"when there are {pkgCount} projects in a solution with --builders {builders}",
                files = GetFiles(pkgCount),
                cwd = "/user/username/projects/myproject",
                commandLineArgs = ["-b", "-w", "-v", "--builders", builders.ToString(CultureInfo.InvariantCulture)],
                edits = edits,
            },
        ];
    }

    // tscbuild_test.go:2667-2762 — TestBuildProjectsBuilding.
    [Fact]
    public void BuildProjectsBuilding()
    {
        var testCases = new List<TscInput>();
        testCases.AddRange(GetTestCases(3, 1));
        testCases.AddRange(GetTestCases(5, 2));
        testCases.AddRange(GetTestCases(8, 3));
        testCases.AddRange(GetTestCases(23, 3));

        // tscbuild_test.go:2759-2761
        foreach (var test in testCases)
        {
            test.RunAndAssert("projectsBuilding");
        }
    }
}
