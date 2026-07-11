using System;

namespace Ts.Cli;

internal static class Program
{
    // Mirrors cmd/tsgo/main.go: one binary dispatching on --lsp / --api / default tsc CLI.
    // The tsc path is the type-check-and-report subset (Ts.Execute); --lsp / --api land in later phases.
    private static int Main(string[] args)
    {
        // cmd/tsgo/main.go:20-27 — mode dispatch.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--lsp":
                    Console.Error.WriteLine("tscs: '--lsp' is not implemented yet (language service is a later phase).");
                    return 1;
                case "--api":
                    Console.Error.WriteLine("tscs: '--api' is not implemented yet (API server is a later phase).");
                    return 1;
                default:
                    break;
            }
        }

        // cmd/tsgo/main.go:28-32 — default tsc CLI. Signal-based cancellation (SIGINT/SIGTERM) is not
        // modelled in this wave; the type-check-and-report path is synchronous and short-lived.
        var sys = global::Ts.Execute.OsSystem.New();
        var result = global::Ts.Execute.Tsc.CommandLine(sys, args);
        return (int)result.Status;
    }
}
