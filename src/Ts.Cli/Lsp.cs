// Ported from tsgo cmd/tsgo/lsp.go (whole file), including the parent-process watchdog
// (lsp.go:74-108). The isProcessAlive probes live in ProcessAlive.cs (Go:
// isprocessalive_*.go).

using System;
using System.Threading.Tasks;

namespace Ts.Cli;

// Go: cmd/tsgo/lsp.go — package main. The class mirrors the Go file; global:: qualifiers
// keep global::Ts.Lsp.* unambiguous beside this class name (plan/05 §1.5).
internal static class Lsp
{
    // lsp.go:93 — time.NewTicker(5 * time.Second).
    private const int PollIntervalMs = 5000;

    // lsp.go:20-72 — runLSP.
    internal static int RunLSP(string[] args)
    {
        // lsp.go:21-30 — flag.NewFlagSet("lsp", flag.ContinueOnError) with --stdio,
        // --pprofDir, --pipe (parsed, unused) and --socket (parsed, unused). Go flag
        // semantics mirrored below: "-x v" / "-x=v" / "--x=v"; bool flags may omit the
        // value; the first non-flag argument stops parsing; undefined flags (including
        // -h) print to stderr and fail the parse (→ 2).
        bool stdio = false;
        string pprofDir = "";
        string pipe = "";
        string socket = "";
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
                case "stdio":
                    SetBool(ref stdio, name, value);
                    break;
                case "pprofDir":
                    SetString(ref pprofDir, name, value);
                    break;
                case "pipe":
                    SetString(ref pipe, name, value);
                    break;
                case "socket":
                    SetString(ref socket, name, value);
                    break;
                case "h" or "help":
                    // Go flag: -h/-help on a FlagSet without them prints usage, then
                    // Parse fails with flag.ErrHelp.
                    Console.Error.WriteLine("usage of lsp:");
                    Console.Error.WriteLine("  -stdio");
                    Console.Error.WriteLine("    \tuse stdio for communication");
                    Console.Error.WriteLine("  -pprofDir string");
                    Console.Error.WriteLine("    \tGenerate pprof CPU/memory profiles to the given directory.");
                    Console.Error.WriteLine("  -pipe string");
                    Console.Error.WriteLine("    \tuse named pipe for communication");
                    Console.Error.WriteLine("  -socket string");
                    Console.Error.WriteLine("    \tuse socket for communication");
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
            return 2; // lsp.go:28-30 — ContinueOnError parse failure.
        }
        _ = pipe; // lsp.go:25 — Go: `_ = pipe`
        _ = socket; // lsp.go:27 — Go: `_ = socket`

        // lsp.go:32-35 — only the stdio transport is wired.
        if (!stdio)
        {
            Console.Error.WriteLine("only stdio is supported");
            return 1;
        }

        global::Ts.Profiling.ProfileSession? profileSession = null;
        try
        {
            // lsp.go:37-41
            if (pprofDir != "")
            {
                Console.Error.WriteLine($"pprof profiles will be written to: {pprofDir}");
                profileSession = global::Ts.Profiling.Pprof.BeginProfiling(pprofDir, Console.Error);
            }

            // lsp.go:43-45 — bundled FS overlay + library path. Go's typingsLocation
            // (osvfs.GetGlobalTypingsCacheLocation) has no ServerOptions field in the
            // current Ts.Lsp port, so it is not passed yet.
            var fs = global::Ts.Bundled.Bundled.WrapFS(global::Ts.Vfs.OsVfs.OsVfsUtil.FS());
            var defaultLibraryPath = global::Ts.Bundled.Bundled.LibPath();

            // lsp.go:47-48 — SIGINT/SIGTERM cancel the server; stop() runs at scope exit.
            using var signals = global::Ts.Execute.Signals.NotifyContext();

            var server = new global::Ts.Lsp.Server(new global::Ts.Lsp.ServerOptions
            {
                In = Console.OpenStandardInput(),
                Out = Console.OpenStandardOutput(),
                Err = Console.Error,
                Cwd = System.IO.Directory.GetCurrentDirectory(), // lsp.go:54 — core.Must(os.Getwd())
                FileSystem = fs,
                DefaultLibraryPath = defaultLibraryPath,
                SetParentProcessID = NewParentProcessWatchdog(signals), // lsp.go:64
            });

            // lsp.go:67-72 — Run. The C# port's Run returns the exit code directly (its
            // error paths print to stderr inside Run and surface as a non-zero code).
            return server.Run(signals.Token);
        }
        finally
        {
            profileSession?.Stop(); // lsp.go:40 — Go: defer profileSession.Stop()
        }
    }

    // lsp.go:74-83 — newParentProcessWatchdog returns a SetParentProcessID callback if
    // the platform supports process-alive checking, or null otherwise.
    internal static Action<int>? NewParentProcessWatchdog(global::Ts.Execute.SignalNotifyContext signals)
    {
        if (!ProcessAlive.Supported) // isprocessalive_other.go:5
        {
            return null;
        }
        return parentPID => StartParentProcessWatchdog(signals, parentPID);
    }

    // lsp.go:85-108 — startParentProcessWatchdog starts a goroutine that monitors the
    // parent process and cancels the context if the parent dies. This prevents orphaned
    // language server processes when the editor crashes or is killed.
    private static void StartParentProcessWatchdog(global::Ts.Execute.SignalNotifyContext signals, int parentPID)
    {
        if (parentPID <= 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // lsp.go:93-106 — 5-second ticker; ctx.Done() ends the loop (Task.Delay
                // throws OperationCanceledException on cancellation).
                while (true)
                {
                    await Task.Delay(PollIntervalMs, signals.Token).ConfigureAwait(false);
                    if (!ProcessAlive.IsProcessAlive(parentPID)) // lsp.go:100
                    {
                        Console.Error.WriteLine($"Parent process {parentPID} has exited, shutting down."); // lsp.go:101
                        signals.Dispose(); // lsp.go:102 — stop() cancels the context.
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // lsp.go:97-98 — ctx.Done(): the watchdog outlives its usefulness.
            }
        });
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
