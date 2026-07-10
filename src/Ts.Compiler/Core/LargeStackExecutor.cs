using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Ts.Core;

/// <summary>
/// Runs work on a dedicated thread with a large fixed stack.
///
/// Go grows goroutine stacks on demand and the compiler recurses extremely deep
/// (parser, checker relation comparison, printer); .NET thread stacks are fixed
/// (~1 MB default), so every parse/bind/check/emit entry point must go through
/// this executor — the moral equivalent of running tsc.js with --stack-size.
/// WorkGroup's parallel and single-threaded implementations both build on it.
/// </summary>
public static class LargeStackExecutor
{
    public const int StackSizeBytes = 64 * 1024 * 1024;

    [ThreadStatic]
    private static bool t_onLargeStack;

    public static void Run(Action action)
    {
        _ = Run<object?>(() =>
        {
            action();
            return null;
        });
    }

    public static T Run<T>(Func<T> func)
    {
        if (t_onLargeStack)
        {
            return func();
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            t_onLargeStack = true;
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, StackSizeBytes)
        {
            IsBackground = true,
            Name = "ts-large-stack",
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
