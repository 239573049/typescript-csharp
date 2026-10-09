// Ported from tsgo internal/project/background/queue_test.go (whole file).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ts.Project.Background;
using Xunit;

namespace Ts.Project.Tests.Background;

public sealed class QueueTests
{
    // queue_test.go:15-28 — TestQueue/BasicEnqueue.
    [Fact]
    public void Queue_BasicEnqueue()
    {
        var q = Queue.NewQueue();

        var executed = false;
        q.Enqueue(CancellationToken.None, ctx => executed = true);

        q.Wait();

        Assert.True(executed);
    }

    // queue_test.go:30-47 — TestQueue/MultipleTasksExecution.
    [Fact]
    public void Queue_MultipleTasksExecution()
    {
        var q = Queue.NewQueue();

        long counter = 0;
        const int numTasks = 10;

        for (int i = 0; i < numTasks; i++)
        {
            q.Enqueue(CancellationToken.None, ctx => Interlocked.Increment(ref counter));
        }

        q.Wait();

        Assert.Equal((long)numTasks, Interlocked.Read(ref counter));
    }

    // queue_test.go:49-75 — TestQueue/NestedEnqueue.
    [Fact]
    public void Queue_NestedEnqueue()
    {
        var q = Queue.NewQueue();

        var executed = new List<string>();
        var mu = new object();

        q.Enqueue(CancellationToken.None, ctx =>
        {
            lock (mu)
            {
                executed.Add("parent");
            }

            q.Enqueue(ctx, childCtx =>
            {
                lock (mu)
                {
                    executed.Add("child");
                }
            });
        });

        q.Wait();

        lock (mu)
        {
            Assert.Equal(2, executed.Count);
        }
    }

    // queue_test.go:77-90 — TestQueue/ClosedQueueRejectsNewTasks.
    [Fact]
    public void Queue_ClosedQueueRejectsNewTasks()
    {
        var q = Queue.NewQueue();
        q.Close();

        var executed = false;
        q.Enqueue(CancellationToken.None, ctx => executed = true);

        q.Wait();

        Assert.False(executed, "Task should not execute after queue is closed");
    }
}
