using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class WorkGroupTests
{
    // Go: singleThreadedWorkGroup.pop() takes from the TAIL (workgroup.go:78-89).
    // This test pins the LIFO execution order: queue a,b,c → run c,b,a.
    [Fact]
    public void SingleThreadedRunsQueuedFunctionsInLifoOrder()
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded: true);
        var order = new List<string>();
        wg.Queue(() => order.Add("a"));
        wg.Queue(() => order.Add("b"));
        wg.Queue(() => order.Add("c"));
        wg.RunAndWait();
        Assert.Equal(["c", "b", "a"], order);
    }

    // Functions queued while another function is executing are drained within the
    // same RunAndWait (fileloader's recursive discovery), still LIFO: a runs,
    // queues b then c; c is then popped before b.
    [Fact]
    public void SingleThreadedDrainsFunctionsQueuedDuringExecutionLifo()
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded: true);
        var order = new List<string>();
        wg.Queue(() =>
        {
            order.Add("a");
            wg.Queue(() => order.Add("b"));
            wg.Queue(() => order.Add("c"));
        });
        wg.RunAndWait();
        Assert.Equal(["a", "c", "b"], order);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueueAfterRunAndWaitThrows(bool singleThreaded)
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded);
        wg.Queue(() => { });
        wg.RunAndWait();
        var ex = Assert.Throws<InvalidOperationException>(() => wg.Queue(() => { }));
        Assert.Equal("Queue called after RunAndWait returned", ex.Message); // Go: panic text
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunAndWaitWithNothingQueuedReturns(bool singleThreaded)
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded);
        wg.RunAndWait(); // must not hang or throw
    }

    // Determinism duty (plan/05 §11): index-slotted work — including tasks queued
    // recursively from running tasks — must fill every slot in both modes, and the
    // two implementations must produce identical output.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FillsAllSlotsIncludingRecursivelyQueuedWork(bool singleThreaded)
    {
        int[] slots = RunSlotWorkload(singleThreaded);
        int[] expected = new int[100];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = i * i;
        }
        Assert.Equal(expected, slots);
    }

    [Fact]
    public void ParallelAndSingleThreadedProduceIdenticalOutput()
    {
        Assert.Equal(RunSlotWorkload(singleThreaded: true), RunSlotWorkload(singleThreaded: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaskExceptionPropagatesFromRunAndWait(bool singleThreaded)
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded);
        wg.Queue(() => throw new InvalidOperationException("boom"));
        var ex = Assert.Throws<InvalidOperationException>(wg.RunAndWait);
        Assert.Equal("boom", ex.Message);
        // done is set even on the exception path (Go: defer w.done.Store(true)).
        Assert.Throws<InvalidOperationException>(() => wg.Queue(() => { }));
    }

    // Parallel tasks must execute on LargeStackExecutor big-stack threads: this
    // recursion depth overflows a default ~1 MB stack (same canary as
    // LargeStackExecutorTests) but fits comfortably in 64 MB.
    [Fact]
    public void ParallelTasksRunOnLargeStackThreads()
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded: false);
        long[] results = new long[4];
        for (int i = 0; i < results.Length; i++)
        {
            int slot = i;
            wg.Queue(() => results[slot] = DeepRecurse(200_000, 0, 0, 0));
        }
        wg.RunAndWait();
        Assert.All(results, r => Assert.Equal(200_000L * 5, r));
    }

    private static int[] RunSlotWorkload(bool singleThreaded)
    {
        IWorkGroup wg = WorkGroup.Create(singleThreaded);
        int[] slots = new int[100];
        // Seed tasks 0..9; each running task i queues task i+10 (recursive discovery).
        for (int i = 0; i < 10; i++)
        {
            int start = i;
            wg.Queue(() => Fill(wg, slots, start));
        }
        wg.RunAndWait();
        return slots;
    }

    private static void Fill(IWorkGroup wg, int[] slots, int index)
    {
        slots[index] = index * index;
        int next = index + 10;
        if (next < slots.Length)
        {
            wg.Queue(() => Fill(wg, slots, next));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DeepRecurse(int depth, long a, long b, long c)
    {
        if (depth == 0)
        {
            return a + b + c;
        }
        long r = DeepRecurse(depth - 1, a + 1, b + 2, c + 3);
        return r - 1;
    }
}
