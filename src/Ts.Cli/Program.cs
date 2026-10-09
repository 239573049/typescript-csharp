using System;

namespace Ts.Cli;

internal static class Program
{
    // Mirrors cmd/tsgo/main.go: one binary dispatching on --lsp / --api / default tsc CLI.
    private static int Main(string[] args)
    {
        // cmd/tsgo/main.go:20-27 — mode dispatch.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--lsp":
                    return Lsp.RunLSP(args[1..]);
                case "--api":
                    return Api.RunAPI(args[1..]);
                default:
                    break;
            }
        }

        // cmd/tsgo/main.go:28-32 — default tsc CLI. SIGINT/SIGTERM cancel the run
        // (Go: signal.NotifyContext); Dispose (Go stop()) runs at scope exit.
        using var signals = global::Ts.Execute.Signals.NotifyContext(); // main.go:28
        var sys = global::Ts.Execute.OsSystem.New();
        var result = global::Ts.Execute.Tsc.CommandLine(sys, args, testing: null, signals.Token); // main.go:30
        return (int)result.Status;
    }
}
