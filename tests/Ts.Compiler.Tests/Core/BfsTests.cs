// Ported from tsgo internal/core/bfs_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class BfsTests
{
    // Graph: A -> B, A -> C, B -> D, C -> D
    private static readonly Dictionary<string, string[]> Dag = new()
    {
        ["A"] = ["B", "C"],
        ["B"] = ["D"],
        ["C"] = ["D"],
        ["D"] = [],
    };

    private static string[] Children(string node) => Dag[node];

    [Fact]
    public void BasicFunctionality_FindSpecificNode()
    {
        var result = Bfs.BreadthFirstSearchParallel("A", Children, node => (node == "D", true));
        Assert.True(result.Stopped, "Expected search to stop at D");
        Assert.Equal(new[] { "D", "B", "A" }, result.Path);
    }

    [Fact]
    public void BasicFunctionality_VisitAllNodes()
    {
        var mu = new object();
        var visitedNodes = new List<string>();
        var result = Bfs.BreadthFirstSearchParallel("A", Children, node =>
        {
            lock (mu)
            {
                visitedNodes.Add(node);
            }
            return (false, false); // Never stop early
        });

        // Should return no path since we never return true.
        Assert.False(result.Stopped, "Expected search to not stop early");
        Assert.Null(result.Path);

        // Should visit all nodes exactly once.
        visitedNodes.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "A", "B", "C", "D" }, visitedNodes);
    }

    [Fact]
    public void EarlyTermination_NodesBelowTargetLevelNotVisited()
    {
        var graph = new Dictionary<string, string[]>
        {
            ["Root"] = ["L1A", "L1B"],
            ["L1A"] = ["L2A", "L2B"],
            ["L1B"] = ["L2C"],
            ["L2A"] = ["L3A"],
            ["L2B"] = [],
            ["L2C"] = [],
            ["L3A"] = [],
        };

        var visited = new SyncSet<string>();
        Bfs.BreadthFirstSearchParallelEx<string, string>(
            "Root",
            node => graph[node],
            node => (node == "L2B", true), // Stop at level 2
            new BreadthFirstSearchOptions<string, string> { Visited = visited },
            CoreUtil.Identity);

        Assert.True(visited.Has("Root"), "Expected to visit Root");
        Assert.True(visited.Has("L1A"), "Expected to visit L1A");
        Assert.True(visited.Has("L1B"), "Expected to visit L1B");
        Assert.True(visited.Has("L2A"), "Expected to visit L2A");
        Assert.True(visited.Has("L2B"), "Expected to visit L2B");
        // L2C is non-deterministic.
        Assert.False(visited.Has("L3A"), "Expected not to visit L3A");
    }

    [Fact]
    public void ReturnsFallbackWhenNoOtherResultFound()
    {
        var visited = new SyncSet<string>();
        var result = Bfs.BreadthFirstSearchParallelEx<string, string>(
            "A",
            Children,
            node => (node == "A", false), // Record A as a fallback, but do not stop
            new BreadthFirstSearchOptions<string, string> { Visited = visited },
            CoreUtil.Identity);

        Assert.False(result.Stopped, "Expected search to not stop early");
        Assert.Equal(new[] { "A" }, result.Path);
        Assert.True(visited.Has("B"), "Expected to visit B");
        Assert.True(visited.Has("C"), "Expected to visit C");
        Assert.True(visited.Has("D"), "Expected to visit D");
    }

    [Fact]
    public void ReturnsStopResultOverFallback()
    {
        var result = Bfs.BreadthFirstSearchParallel("A", Children, node => node switch
        {
            "A" => (true, false), // Record fallback
            "D" => (true, true), // Stop at D
            _ => (false, false),
        });

        Assert.True(result.Stopped, "Expected search to stop at D");
        Assert.Equal(new[] { "D", "B", "A" }, result.Path);
    }
}
