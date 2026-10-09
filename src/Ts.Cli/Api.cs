// Ported from tsgo cmd/tsgo/api.go (whole file).

using System;
using System.IO;

namespace Ts.Cli;

// Go: cmd/tsgo/api.go — package main. The class mirrors the Go file; global:: qualifiers
// keep global::Ts.Api.* unambiguous beside this class name (plan/05 §1.5).
internal static class Api
{
    // api.go:17-61 — runAPI.
    internal static int RunAPI(string[] args)
    {
        // api.go:18-26 — flag.NewFlagSet("api", flag.ContinueOnError) with --cwd,
        // --pipe, --callbacks, --async and --timing. Go flag semantics mirrored below:
        // "-x v" / "-x=v" / "--x=v"; bool flags may omit the value; the first non-flag
        // argument stops parsing; undefined flags (including -h) print to stderr and
        // fail the parse (→ 2).
        string cwd = Directory.GetCurrentDirectory(); // api.go:19 — default core.Must(os.Getwd())
        string pipePath = "";
        string callbacks = "";
        bool async = false;
        bool timing = false;
        int i = 0;
        bool failed = false;

        bool SetBool(ref bool slot, string name, string? value)
        {
            if (value == null)
            {
                slot = true; // Go flag: a bool flag without "=value" is true.
                return true;
            }
            if (!TryParseGoBool(value, out var parsed))
            {
                Console.Error.WriteLine($"invalid boolean value \"{value}\" for -{name}"); // Go flag message
                failed = true;
                return false;
            }
            slot = parsed;
            return true;
        }

        bool SetString(ref string slot, string name, string? value)
        {
            if (value != null)
            {
                slot = value;
                return true;
            }
            if (i >= args.Length)
            {
                Console.Error.WriteLine($"flag needs an argument: -{name}"); // Go flag message
                failed = true;
                return false;
            }
            slot = args[i++];
            return true;
        }

        while (i < args.Length && !failed)
        {
            string arg = args[i++];
            if (arg == "--")
            {
                break; // Go flag: "--" terminates the flag list.
            }
            if (arg.Length < 2 || arg[0] != '-')
            {
                break; // Go flag: the first non-flag argument stops flag parsing.
            }
            if (arg.StartsWith("---", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"bad flag syntax: {arg}"); // Go flag message
                failed = true;
                break;
            }
            string name = arg.TrimStart('-');
            string? value = null;
            int eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            switch (name)
            {
                case "cwd":
                    SetString(ref cwd, name, value);
                    break;
                case "pipe":
                    SetString(ref pipePath, name, value);
                    break;
                case "callbacks":
                    SetString(ref callbacks, name, value);
                    break;
                case "async":
                    SetBool(ref async, name, value);
                    break;
                case "timing":
                    SetBool(ref timing, name, value);
                    break;
                case "h" or "help":
                    // Go flag: -h/-help on a FlagSet without them prints usage, then
                    // Parse fails with flag.ErrHelp.
                    Console.Error.WriteLine("usage of api:");
                    Console.Error.WriteLine("  -cwd string");
                    Console.Error.WriteLine("    \tcurrent working directory");
                    Console.Error.WriteLine("  -pipe string");
                    Console.Error.WriteLine("    \tuse named pipe or Unix domain socket for communication instead of stdio");
                    Console.Error.WriteLine("  -callbacks string");
                    Console.Error.WriteLine("    \tcomma-separated list of FS callbacks to enable (readFile,fileExists,directoryExists,getAccessibleEntries,realpath)");
                    Console.Error.WriteLine("  -async");
                    Console.Error.WriteLine("    \tuse JSON-RPC protocol instead of MessagePack (for async API)");
                    Console.Error.WriteLine("  -timing");
                    Console.Error.WriteLine("    \tcollect per-request server processing time, folded into the client's timing snapshot");
                    failed = true;
                    break;
                default:
                    Console.Error.WriteLine($"flag provided but not defined: -{name}"); // Go flag message
                    failed = true;
                    break;
            }
        }
        if (failed)
        {
            return 2; // api.go:24-26 — ContinueOnError parse failure.
        }

        var defaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(); // api.go:28

        // api.go:30-34 — parse callbacks list.
        string[] callbacksList = callbacks != "" ? callbacks.Split(',') : Array.Empty<string>();

        var options = new global::Ts.Api.StdioServerOptions // api.go:36-43
        {
            Err = Console.Error,
            Cwd = cwd,
            DefaultLibraryPath = defaultLibraryPath,
            Callbacks = callbacksList,
            Async = async,
            CollectTiming = timing,
        };
        if (pipePath != "") // api.go:44-49
        {
            options.PipePath = pipePath;
        }
        else
        {
            options.In = Console.OpenStandardInput();
            options.Out = Console.OpenStandardOutput();
        }

        var s = new global::Ts.Api.StdioServer(options); // api.go:51

        // api.go:53-54 — SIGINT/SIGTERM cancel the server; stop() runs at scope exit.
        // The Go ctx becomes Ts.Core.RequestContext (plan/01 §4.4) carrying the token.
        using var signals = global::Ts.Execute.Signals.NotifyContext();
        var ctx = new global::Ts.Core.RequestContext { Cancellation = signals.Token };

        var runErr = s.Run(ctx); // api.go:56
        if (runErr != null)
        {
            Console.Error.WriteLine(runErr);
            return 1;
        }
        return 0;
    }

    // Go strconv.ParseBool's accepted set — the flag package uses it for bool values.
    private static bool TryParseGoBool(string value, out bool parsed)
    {
        switch (value)
        {
            case "1" or "t" or "T" or "true" or "TRUE" or "True":
                parsed = true;
                return true;
            case "0" or "f" or "F" or "false" or "FALSE" or "False":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }
}
