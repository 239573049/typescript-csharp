using System;

namespace Ts.Cli;

internal static class Program
{
    // Mirrors cmd/tsgo/main.go: one binary dispatching on --lsp / --api / default tsc CLI.
    // The real drivers land in later phases; this scaffolding build only proves the
    // solution shape and the Native AOT publish pipeline.
    private static int Main(string[] args)
    {
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--version":
                case "-v":
                    Console.WriteLine($"Version {Ts.Core.TsVersion.Value}");
                    return 0;
                case "--lsp":
                case "--api":
                    Console.Error.WriteLine($"tscs: '{arg}' is not implemented yet (scaffolding build).");
                    return 1;
                default:
                    break;
            }
        }

        Console.WriteLine("tscs — C#/.NET port of typescript-go (scaffolding build; compiler pipeline lands in later phases).");
        return 0;
    }
}
