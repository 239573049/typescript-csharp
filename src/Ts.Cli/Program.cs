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
                    var lspSystem = global::Ts.Execute.OsSystem.New();
                    var server = new global::Ts.Lsp.Server(new global::Ts.Lsp.ServerOptions
                    {
                        In = Console.OpenStandardInput(),
                        Out = Console.OpenStandardOutput(),
                        Err = Console.Error,
                        Cwd = lspSystem.GetCurrentDirectory(),
                        FileSystem = lspSystem.FS(),
                        DefaultLibraryPath = lspSystem.DefaultLibraryPath(),
                    });
                    return server.Run();
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
