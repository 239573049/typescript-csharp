// Ported from tsgo internal/execute/tsc/diagnostics.go (System interface) and cmd/tsgo/sys.go
// (osSys / newSystem).
//
// §1.3: a regular behavior interface gets the `I` prefix → ISystem (Go `tsc.System`).
// §1.5: sibling-namespace types are fully qualified with global::.

using System;
using System.IO;
using Ts.Vfs;

namespace Ts.Execute;

// diagnostics.go:17-28 — the CLI's host surface. Also satisfies tsoptions.ParseConfigHost (FS +
// GetCurrentDirectory) so the same object can drive ParseCommandLine.
public interface ISystem : global::Ts.TsOptions.ParseConfigHost
{
    TextWriter Writer();

    // ParseConfigHost already declares FS() and GetCurrentDirectory().

    string DefaultLibraryPath();
    bool WriteOutputIsTTY();
    int GetWidthOfTerminal();
    string GetEnvironmentVariable(string name);

    DateTime Now();
    TimeSpan SinceStart();
}

// cmd/tsgo/sys.go:17-76 — the real OS-backed system.
public sealed class OsSystem : ISystem
{
    private readonly TextWriter writer;
    private readonly IFileSystem fs;
    private readonly string defaultLibraryPath;
    private readonly string cwd;
    private readonly DateTime start;

    private OsSystem(TextWriter writer, IFileSystem fs, string defaultLibraryPath, string cwd, DateTime start)
    {
        this.writer = writer;
        this.fs = fs;
        this.defaultLibraryPath = defaultLibraryPath;
        this.cwd = cwd;
        this.start = start;
    }

    // sys.go:25-27
    public TimeSpan SinceStart() => DateTime.UtcNow - start;

    // sys.go:29-31
    public DateTime Now() => DateTime.UtcNow;

    // sys.go:33-35
    public IFileSystem FS() => fs;

    // sys.go:37-39
    public string DefaultLibraryPath() => defaultLibraryPath;

    // sys.go:41-43
    public string GetCurrentDirectory() => cwd;

    // sys.go:45-47
    public TextWriter Writer() => writer;

    // sys.go:49-51 — Console.IsOutputRedirected is the BCL equivalent of term.IsTerminal(stdout).
    public bool WriteOutputIsTTY() => !Console.IsOutputRedirected;

    // sys.go:53-56
    public int GetWidthOfTerminal()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (Exception)
        {
            // No attached console (redirected/CI): 0, matching term.GetSize's error path returning 0.
            return 0;
        }
    }

    // sys.go:58-60
    public string GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    // sys.go:62-76 — construct the system from the real OS: osvfs wrapped with bundled.WrapFS (the
    // bundled:/// lib overlay) and bundled.LibPath() as the default library path (embed.go).
    public static OsSystem New()
    {
        string cwd = global::Ts.TsPath.TsPath.NormalizePath(Directory.GetCurrentDirectory());
        return new OsSystem(
            Console.Out,
            global::Ts.Bundled.Bundled.WrapFS(global::Ts.Vfs.OsVfs.OsVfsUtil.FS()),
            global::Ts.Bundled.Bundled.LibPath(),
            cwd,
            DateTime.UtcNow);
    }
}
