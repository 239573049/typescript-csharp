// Ported from tsgo internal/core/bfs.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections;

namespace Ts.Core;

// Go: internal/core/bfs.go — parallel breadth-first search. Levels are OrderedMaps, so
// dedup/iteration order is deterministic (bfs.go depends on this), and result selection
// is by job index via atomics — so the returned path is identical under single-threaded
// and 4-checker parallel execution (plan/05 §11 determinism obligation). The per-level
// fan-out runs on the big-stack WorkGroup (plan/01 §4.5); the default thread pool is
// forbidden (~1 MB stack — plan/01 §4.5 red line, Task.Run/ThreadPool are banned APIs).

public struct BreadthFirstSearchResult<N>
{
    public bool Stopped;
    public N[]? Path;
}

// Go: breadthFirstSearchJob[N] — a node plus a back-pointer to its parent job.
internal sealed class BreadthFirstSearchJob<N>
{
    public N Node = default!;
    public BreadthFirstSearchJob<N>? Parent;
}

public sealed class BreadthFirstSearchLevel<K, N>
    where K : notnull
{
    internal readonly OrderedMap<K, BreadthFirstSearchJob<N>> Jobs;

    internal BreadthFirstSearchLevel(OrderedMap<K, BreadthFirstSearchJob<N>> jobs)
    {
        Jobs = jobs;
    }

    public bool Has(K key) => Jobs.Has(key);

    public void Delete(K key) => Jobs.TryDelete(key, out _);

    public void Range(Func<N, bool> f)
    {
        foreach (var job in Jobs.Values())
        {
            if (!f(job.Node))
            {
                return;
            }
        }
    }
}

public struct BreadthFirstSearchOptions<K, N>
    where K : notnull
{
    // Visited is a set of nodes that have already been visited. If null, a new set is created.
    public SyncSet<K>? Visited;

    // PreprocessLevel, if provided, is called before each level, giving the caller an
    // opportunity to remove nodes.
    public Action<BreadthFirstSearchLevel<K, N>>? PreprocessLevel;
}

// Package-level bfs.go functions (05 §2.1: static class named after the file).
public static class Bfs
{
    // BreadthFirstSearchParallel performs a breadth-first search on a graph starting from
    // the given node. It processes nodes in parallel and returns the path from the first
    // node that satisfies the `visit` function back to the start node.
    public static BreadthFirstSearchResult<N> BreadthFirstSearchParallel<N>(
        N start,
        Func<N, N[]> neighbors,
        Func<N, (bool IsResult, bool Stop)> visit)
        where N : notnull
    {
        return BreadthFirstSearchParallelEx<N, N>(start, neighbors, visit, default, CoreUtil.Identity);
    }

    // BreadthFirstSearchParallelEx is an extension of BreadthFirstSearchParallel that lets
    // the caller pass a pre-seeded set of already-visited nodes and a preprocessing function
    // that can remove nodes from each level before parallel processing.
    public static BreadthFirstSearchResult<N> BreadthFirstSearchParallelEx<K, N>(
        N start,
        Func<N, N[]> neighbors,
        Func<N, (bool IsResult, bool Stop)> visit,
        BreadthFirstSearchOptions<K, N> options,
        Func<N, K> getKey)
        where K : notnull
    {
        var visited = options.Visited ?? new SyncSet<K>();

        BreadthFirstSearchJob<N>? fallback = null;

        // processLevel processes each node at the current level in parallel. It produces
        // either the jobs for the next level, or a result if `visit` returns true.
        (bool Stop, BreadthFirstSearchJob<N>? Job, OrderedMap<K, BreadthFirstSearchJob<N>>? Next) ProcessLevel(
            int index, OrderedMap<K, BreadthFirstSearchJob<N>> jobs)
        {
            long lowestFallback = long.MaxValue;
            long lowestGoal = long.MaxValue;
            long nextJobCount = 0;
            options.PreprocessLevel?.Invoke(new BreadthFirstSearchLevel<K, N>(jobs));
            var next = new BreadthFirstSearchJob<N>[jobs.Size()][];
            var wg = WorkGroup.Create(singleThreaded: false);
            int i = 0;
            foreach (var j in jobs.Values())
            {
                int capturedI = i;
                var capturedJob = j;
                wg.Queue(() =>
                {
                    if (capturedI >= Interlocked.Read(ref lowestGoal))
                    {
                        return; // Stop processing if we already found a lower result.
                    }

                    // If we have already visited this node, skip it.
                    if (!visited.AddIfAbsent(getKey(capturedJob.Node)))
                    {
                        // If we are here, we already visited this node at a previous
                        // *level*, which means `visit` must have returned false, so we
                        // don't need to update our result indices. This holds because we
                        // deduplicated jobs before queuing the level.
                        return;
                    }

                    var (isResult, stop) = visit(capturedJob.Node);
                    if (isResult)
                    {
                        // We found a result, so we will stop at this level, but an earlier
                        // job may still find a true result at a lower index.
                        if (stop)
                        {
                            UpdateMin(ref lowestGoal, capturedI);
                            return;
                        }
                        if (fallback == null)
                        {
                            UpdateMin(ref lowestFallback, capturedI);
                        }
                    }

                    if (capturedI >= Interlocked.Read(ref lowestGoal))
                    {
                        // If `visit` is expensive, a different job has likely already found
                        // a lower-index result by now, so we needn't collect next jobs.
                        return;
                    }
                    // Add the next level jobs.
                    var neighborNodes = neighbors(capturedJob.Node);
                    if (neighborNodes.Length > 0)
                    {
                        Interlocked.Add(ref nextJobCount, neighborNodes.Length);
                        var slot = new BreadthFirstSearchJob<N>[neighborNodes.Length];
                        for (int ni = 0; ni < neighborNodes.Length; ni++)
                        {
                            slot[ni] = new BreadthFirstSearchJob<N> { Node = neighborNodes[ni], Parent = capturedJob };
                        }
                        next[capturedI] = slot;
                    }
                });
                i++;
            }
            wg.RunAndWait();

            long goalIndex = Interlocked.Read(ref lowestGoal);
            if (goalIndex != long.MaxValue)
            {
                // If we found a result, return it immediately.
                jobs.TryEntryAt((int)goalIndex, out _, out var job);
                return (true, job, null);
            }
            if (fallback == null)
            {
                long fbIndex = Interlocked.Read(ref lowestFallback);
                if (fbIndex != long.MaxValue)
                {
                    jobs.TryEntryAt((int)fbIndex, out _, out fallback);
                }
            }
            var nextJobs = new OrderedMap<K, BreadthFirstSearchJob<N>>((int)Interlocked.Read(ref nextJobCount));
            foreach (var slot in next)
            {
                if (slot == null)
                {
                    continue;
                }
                foreach (var jb in slot)
                {
                    var key = getKey(jb.Node);
                    if (!nextJobs.Has(key))
                    {
                        // Deduplicate synchronously to avoid messy locks and spawning
                        // unnecessary goroutines.
                        nextJobs.Set(key, jb);
                    }
                }
            }
            return (false, null, nextJobs);
        }

        N[]? CreatePath(BreadthFirstSearchJob<N>? job)
        {
            List<N>? path = null;
            while (job != null)
            {
                (path ??= new List<N>()).Add(job.Node);
                job = job.Parent;
            }
            return path?.ToArray();
        }

        int levelIndex = 0;
        var level = OrderedMap.FromList(new[]
        {
            new MapEntry<K, BreadthFirstSearchJob<N>>(getKey(start), new BreadthFirstSearchJob<N> { Node = start }),
        });
        while (level.Size() > 0)
        {
            var result = ProcessLevel(levelIndex, level);
            if (result.Stop)
            {
                return new BreadthFirstSearchResult<N> { Stopped = true, Path = CreatePath(result.Job) };
            }
            else if (result.Job != null && fallback == null)
            {
                fallback = result.Job;
            }
            level = result.Next!;
            levelIndex++;
        }
        return new BreadthFirstSearchResult<N> { Stopped = false, Path = CreatePath(fallback) };
    }

    // updateMin updates the atomic integer `a` to the candidate value if it is less than the current value.
    private static bool UpdateMin(ref long a, long candidate)
    {
        while (true)
        {
            long current = Interlocked.Read(ref a);
            if (current < candidate)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref a, candidate, current) == current)
            {
                return true;
            }
        }
    }
}
