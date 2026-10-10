// Ported from tsgo internal/execute/tsctests/watcher_race_test.go — the seven concurrent
// Watcher/Orchestrator stress tests. Go's `go func()` + sync.WaitGroup become LongRunning
// Tasks (each body runs inside LargeStackExecutor.Run so the compiler can recurse: the
// executor's marker is [ThreadStatic], so a Task body never carries it into a plain thread);
// the tests await the tasks instead of blocking, which is also what xUnit1031 requires.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ts.Core;
using Ts.Execute;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class WatcherRaceTests
{
    // watcher_race_test.go:15-38 — createTestWatcher.
    private static (Watcher Watcher, TestSys Sys) CreateTestWatcher()
    {
        var sys = TestSys.NewTestSys(new TscInput
        {
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a.ts"] = "const a: number = 1;",
                ["/home/src/workspaces/project/b.ts"] = "import { a } from \"./a\"; export const b = a;",
                ["/home/src/workspaces/project/tsconfig.json"] = "{}",
            },
            commandLineArgs = ["--watch"],
        }, false);
        var result = RunOnLargeStack(sys, ["--watch"]);
        Assert.NotNull(result.Watcher); // watcher_race_test.go:30-32
        var watcher = Assert.IsType<Watcher>(result.Watcher); // watcher_race_test.go:33-36
        return (watcher, sys);
    }

    // Go's test goroutines enter the compiler through execute.CommandLine on their own stack.
    // Here the entry goes through LargeStackExecutor.Run (Tsc.CommandLine's own wrapper already
    // did, on this thread) because the compile paths recurse far past a default thread stack.
    private static CommandLineResult RunOnLargeStack(TestSys sys, string[] commandLineArgs) =>
        LargeStackExecutor.Run(() => Tsc.CommandLine(sys, commandLineArgs, sys));

    // Go: `go func() { ... }` + wg.Add(1)/wg.Done(). The compiler call inside runs through
    // LargeStackExecutor.Run on this Task's own thread.
    private static Task RunConcurrent(Action body) =>
        LargeStackExecutor.Run(() => Task.Factory.StartNew(
            () => LargeStackExecutor.Run(body),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));

    // Same seam for a goroutine whose result the test needs (Go's result channel:
    // watcher_race_test.go:280-283).
    private static Task<T> RunConcurrentWithResult<T>(Func<T> body) =>
        LargeStackExecutor.Run(() => Task.Factory.StartNew(
            () => LargeStackExecutor.Run(body),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));

    // watcher_race_test.go:40-65 — TestWatcherConcurrentDoCycle. DoCycle from 8 concurrent
    // callers while each one modifies a source file.
    [Fact]
    public async Task ConcurrentDoCycle()
    {
        var (w, sys) = CreateTestWatcher();

        var tasks = new List<Task>(8);
        for (int i = 0; i < 8; i++)
        {
            int worker = i;
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    sys.FsFromFileMap().WriteFile("/home/src/workspaces/project/a.ts", $"const a: number = {worker * 10 + j};");
                    w.DoCycle();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // watcher_race_test.go:67-104 — TestWatcherDoCycleWithConcurrentStateReads. Four modifying
    // DoCycle callers plus eight cycles-only callers.
    [Fact]
    public async Task DoCycleWithConcurrentStateReads()
    {
        var (w, sys) = CreateTestWatcher();

        var tasks = new List<Task>(12);

        // DoCycle goroutines
        for (int i = 0; i < 4; i++)
        {
            int worker = i;
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 15; j++)
                {
                    sys.FsFromFileMap().WriteFile("/home/src/workspaces/project/a.ts", $"const a: number = {worker * 15 + j};");
                    w.DoCycle();
                }
            }));
        }

        // State reader goroutines
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    w.DoCycle();
                    w.DoCycle();
                    w.DoCycle();
                    w.DoCycle();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // watcher_race_test.go:106-146 — TestWatcherConcurrentFileChangesAndDoCycle. Creating,
    // deleting and cycling concurrently.
    [Fact]
    public async Task ConcurrentFileChangesAndDoCycle()
    {
        var (w, sys) = CreateTestWatcher();

        var tasks = new List<Task>(9);

        // File creators
        for (int i = 0; i < 4; i++)
        {
            int worker = i;
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 20; j++)
                {
                    sys.FsFromFileMap().WriteFile(
                        $"/home/src/workspaces/project/gen_{worker}_{j}.ts",
                        $"export const x{worker}_{j} = {j};");
                }
            }));
        }

        // File deleters
        tasks.Add(RunConcurrent(() =>
        {
            for (int j = 0; j < 20; j++)
            {
                sys.FsFromFileMap().Remove($"/home/src/workspaces/project/gen_0_{j}.ts");
            }
        }));

        // DoCycle callers
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    w.DoCycle();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // watcher_race_test.go:148-206 — TestWatcherRapidConfigChanges. Races on configModified,
    // configHasErrors, configFilePaths, config and extendedConfigCache.
    [Fact]
    public async Task RapidConfigChanges()
    {
        var (w, sys) = CreateTestWatcher();

        string[] configs =
        [
            "{}",
            "{\"compilerOptions\": {\"strict\": true}}",
            "{\"compilerOptions\": {\"target\": \"ES2020\"}}",
            "{\"compilerOptions\": {\"noEmit\": true}}",
        ];

        var tasks = new List<Task>(9);

        // Config modifiers + DoCycle
        for (int i = 0; i < 3; i++)
        {
            int worker = i;
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    sys.FsFromFileMap().WriteFile(
                        "/home/src/workspaces/project/tsconfig.json",
                        configs[(worker + j) % configs.Length]);
                    w.DoCycle();
                }
            }));
        }

        // Concurrent source file modifications
        for (int i = 0; i < 2; i++)
        {
            int worker = i;
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 15; j++)
                {
                    sys.FsFromFileMap().WriteFile(
                        "/home/src/workspaces/project/a.ts",
                        $"const a: number = {worker * 15 + j};");
                    w.DoCycle();
                }
            }));
        }

        // State readers
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 30; j++)
                {
                    w.DoCycle();
                    w.DoCycle();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // watcher_race_test.go:208-226 — TestWatcherConcurrentDoCycleNoChanges. Exercises the
    // early-return path where the watch state is read and no changes exist.
    [Fact]
    public async Task ConcurrentDoCycleNoChanges()
    {
        var (w, _) = CreateTestWatcher();

        var tasks = new List<Task>(16);
        for (int i = 0; i < 16; i++)
        {
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    w.DoCycle();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // watcher_race_test.go:228-266 — TestWatcherAlternatingModifyAndDoCycle.
    [Fact]
    public async Task AlternatingModifyAndDoCycle()
    {
        var (w, sys) = CreateTestWatcher();

        var tasks = new List<Task>(9);

        // Writer goroutine: continuously modifies files
        tasks.Add(RunConcurrent(() =>
        {
            for (int j = 0; j < 100; j++)
            {
                sys.FsFromFileMap().WriteFile("/home/src/workspaces/project/a.ts", $"const a: number = {j};");
            }
        }));

        // Multiple DoCycle goroutines
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 25; j++)
                {
                    w.DoCycle();
                }
            }));
        }

        // State reader goroutines
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(RunConcurrent(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    w.DoCycle();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // watcher_race_test.go:268-292 — TestBuildWatchStopsWhenContextIsCancelled.
    [Fact]
    public async Task BuildWatchStopsWhenContextIsCancelled()
    {
        var sys = TestSys.NewTestSys(new TscInput
        {
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = "{\"compilerOptions\":{\"composite\":true},\"files\":[\"index.ts\"]}",
                ["/home/src/workspaces/project/index.ts"] = "export const x = 1;",
            },
        }, false);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // watcher_race_test.go:277-278

        var resultTask = RunConcurrentWithResult(() =>
            Tsc.CommandLine(sys, ["--build", "--watch", "--watchInterval", "60000"], sys, cts.Token));

        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(ReferenceEquals(completed, resultTask), "build watch did not stop after context cancellation");

        var result = await resultTask;
        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.NotNull(result.Watcher);
    }
}
