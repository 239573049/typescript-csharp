// Ported from tsgo internal/execute/build/graph_test.go — TestBuildOrderGenerator. Declares the
// project graph on disk (composite projects under /home/src/workspaces/project/<name>), builds
// the orchestrator's graph and asserts the resulting build order plus the upstream/downstream
// relations for every project.

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Core;
using Ts.Execute.Build;
using Ts.TestUtil;
using Xunit;

using TestSys = Ts.Cli.Tests.Execute.TestSys;
using TscInput = Ts.Cli.Tests.Execute.TscInput;

namespace Ts.Execute.Build;

public sealed class BuildGraphTests
{
    // graph_test.go:16-32 — the testCase table (name, projects, expected order, circular).
    // graph_test.go:34-49 — buildOrderTestCase.configName / projectName.
    private const string GraphRoot = "/home/src/workspaces/project";

    private static string ConfigName(string project) => $"{GraphRoot}/{project}/tsconfig.json";

    private static string ProjectName(string config) =>
        config.StartsWith(GraphRoot + "/", StringComparison.Ordinal) && config.EndsWith("/tsconfig.json", StringComparison.Ordinal)
            ? config[(GraphRoot.Length + 1)..^"/tsconfig.json".Length]
            : config;

    // graph_test.go:56-64 — the reference graph; G has no references and D/E have none beyond
    // their absence from this map.
    private static readonly Dictionary<string, string[]> Deps = new(StringComparer.Ordinal)
    {
        ["A"] = ["B", "C"],
        ["B"] = ["C", "D"],
        ["C"] = ["D", "E"],
        ["F"] = ["E"],
        ["H"] = ["I"],
        ["I"] = ["J"],
        ["J"] = ["H", "E"],
    };

    public static TheoryData<string, string[], string[], bool> BuildOrderCases() => new()
    {
        { "specify two roots", ["A", "G"], ["D", "E", "C", "B", "A", "G"], false },
        { "multiple parts of the same graph in various orders", ["A"], ["D", "E", "C", "B", "A"], false },
        { "multiple parts of the same graph in various orders", ["A", "C", "D"], ["D", "E", "C", "B", "A"], false },
        { "multiple parts of the same graph in various orders", ["D", "C", "A"], ["D", "E", "C", "B", "A"], false },
        { "other orderings", ["F"], ["E", "F"], false },
        { "other orderings", ["E"], ["E"], false },
        { "other orderings", ["F", "C", "A"], ["E", "F", "D", "C", "B", "A"], false },
        { "returns circular order", ["H"], ["E", "J", "I", "H"], true },
        { "returns circular order", ["A", "H"], ["D", "E", "C", "B", "A", "J", "I", "H"], true },
    };

    [Theory]
    [MemberData(nameof(BuildOrderCases))]
    public void BuildOrderGenerator(string name, string[] projects, string[] expected, bool circular)
    {
        _ = name;
        Run(projects, expected, circular);
    }

    // graph_test.go:51-153 — buildOrderTestCase.run.
    private static void Run(string[] projects, string[] expected, bool circular)
    {
        // graph_test.go:55-70 — files plus the reversed dependency map.
        var files = new Dictionary<string, object>();
        var reverseDeps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (project, projectDeps) in Deps) // map-iter: 序无关 (accumulated, then sorted per project below)
        {
            foreach (var dep in projectDeps)
            {
                if (!reverseDeps.TryGetValue(dep, out var list))
                {
                    list = new List<string>();
                    reverseDeps[dep] = list;
                }
                list.Add(project);
            }
        }

        // graph_test.go:96-109 — every project A..J, each with its own tsconfig.
        foreach (var project in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" })
        {
            files[$"{GraphRoot}/{project}/{project}.ts"] = "export {}";
            var referencesStr = "";
            if (Deps.TryGetValue(project, out var projectDeps))
            {
                var refs = new StringBuilder();
                for (int i = 0; i < projectDeps.Length; i++)
                {
                    if (i > 0)
                    {
                        refs.Append(',');
                    }
                    refs.Append($"{{ \"path\": \"../{projectDeps[i]}\" }}");
                }
                referencesStr = $", \"references\": [{refs}]";
            }
            files[ConfigName(project)] = StringTestUtil.Dedent($$"""
                {
                                "compilerOptions": { "composite": true },
                                "files": ["./{{project}}.ts"],
                                {{referencesStr}}
                            }
""");
        }

        // graph_test.go:111-121
        var sys = TestSys.NewTscSystem(files, true, GraphRoot);
        var orchestrator = NewOrchestrator(sys, ["--build", "--dry", .. projects]);
        orchestrator.GenerateGraph(null);
        var buildOrder = CoreUtil.Map(orchestrator.Order(), ProjectName)!;
        Assert.Equal(expected, buildOrder);
        VerifyDeps(orchestrator, buildOrder, hasDownStream: false);

        // graph_test.go:123-137 — children must be built after their parents.
        if (!circular)
        {
            foreach (var (project, projectDeps) in Deps) // map-iter: 序无关 (independent assertions)
            {
                var child = ConfigName(project);
                var childIndex = Array.IndexOf(buildOrder, child);
                if (childIndex == -1)
                {
                    continue;
                }
                foreach (var dep in projectDeps)
                {
                    var parent = ConfigName(dep);
                    var parentIndex = Array.IndexOf(buildOrder, parent);
                    Assert.True(childIndex > parentIndex, $"Expecting child {project} to be built after parent {dep}");
                }
            }
        }

        // graph_test.go:139-141 — regenerating while reusing old tasks keeps the order.
        orchestrator.GenerateGraphReusingOldTasks();
        var buildOrder2 = CoreUtil.Map(orchestrator.Order(), ProjectName)!;
        Assert.Equal(expected, buildOrder2);

        // graph_test.go:143-151 — the watch graph carries the downstream edges.
        var orchestratorWatch = NewOrchestrator(sys, ["--build", "--watch", .. projects]);
        orchestratorWatch.GenerateGraph(null);
        var buildOrder3 = CoreUtil.Map(orchestratorWatch.Order(), ProjectName)!;
        VerifyDeps(orchestratorWatch, buildOrder3, hasDownStream: true);
    }

    // graph_test.go:113-118
    private static Orchestrator NewOrchestrator(TestSys sys, string[] args) =>
        Orchestrator.NewOrchestrator(new BuildOptionsBag
        {
            Sys = sys,
            Command = global::Ts.TsOptions.CommandLineParser.ParseBuildCommandLine(args, sys),
        });

    // graph_test.go:71-95 — verifyDeps.
    private static void VerifyDeps(Orchestrator orchestrator, string[] buildOrder, bool hasDownStream)
    {
        for (int index = 0; index < buildOrder.Length; index++)
        {
            var project = buildOrder[index];
            var upstream = CoreUtil.Map(orchestrator.Upstream(ConfigName(project)), ProjectName)!;
            var expectedUpstream = Deps.TryGetValue(project, out var deps) ? deps : Array.Empty<string>();
            Assert.True(upstream.Length <= expectedUpstream.Length,
                $"Expected upstream for {project} to be at most {expectedUpstream.Length}, got {upstream.Length}");
            foreach (var exp in expectedUpstream)
            {
                if (Array.IndexOf(buildOrder, exp, 0, index) >= 0)
                {
                    Assert.True(Array.IndexOf(upstream, exp) >= 0, $"Expected upstream for {project} to contain {exp}");
                }
                else
                {
                    Assert.True(Array.IndexOf(upstream, exp) < 0, $"Expected upstream for {project} to not contain {exp}");
                }
            }

            var downstream = CoreUtil.Map(orchestrator.Downstream(ConfigName(project)), ProjectName)!;
            var expectedDownstream = hasDownStream
                ? ReverseDeps(project)
                : Array.Empty<string>();
            Assert.True(downstream.Length <= expectedDownstream.Length,
                $"Expected downstream for {project} to be at most {expectedDownstream.Length}, got {downstream.Length}");
            foreach (var exp in expectedDownstream)
            {
                if (Array.IndexOf(buildOrder, exp, index + 1, buildOrder.Length - (index + 1)) >= 0)
                {
                    Assert.True(Array.IndexOf(downstream, exp) >= 0, $"Expected downstream for {project} to contain {exp}");
                }
                else
                {
                    Assert.True(Array.IndexOf(downstream, exp) < 0, $"Expected downstream for {project} to not contain {exp}");
                }
            }
        }
    }

    // graph_test.go:65-70 — reverseDeps[dep] = the projects that depend on dep. Go map iteration
    // is unordered, so the list is sorted for determinism (map-iter: 已排序).
    private static string[] ReverseDeps(string project)
    {
        var result = new List<string>();
        foreach (var (other, otherDeps) in Deps) // map-iter: 已排序 (result sorted below)
        {
            if (Array.IndexOf(otherDeps, project) >= 0)
            {
                result.Add(other);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }
}
