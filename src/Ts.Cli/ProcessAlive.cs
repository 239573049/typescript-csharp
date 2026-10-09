// Ported from tsgo cmd/tsgo/isprocessalive_unix.go, isprocessalive_windows.go and
// isprocessalive_other.go. Go splits the three by build tags; the C# port checks the OS
// at runtime (repo precedent: src/Ts.Compiler/Vfs/OsVfs/OsVfs.cs:62) and uses managed
// process APIs — no P/Invoke anywhere in src yet, and System.Diagnostics.Process is
// AOT-safe.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ts.Cli;

internal static class ProcessAlive
{
    // isprocessalive_unix.go:11 / isprocessalive_windows.go:7 / isprocessalive_other.go:5
    // — Go decides by build tag (processAliveSupported); every OS this runtime supports
    // has a probe here, everything else mirrors the `!unix && !windows` file.
    internal static bool Supported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // isprocessalive_unix.go:18-25 / isprocessalive_windows.go:13-26 — isProcessAlive.
    // Unix Go probes with signal 0 (nil or EPERM ⇒ alive, ESRCH ⇒ gone); Windows Go
    // opens the process with SYNCHRONIZE and waits with a zero timeout (WAIT_TIMEOUT ⇒
    // alive, signaled ⇒ exited). The managed equivalent is Process.GetProcessById +
    // HasExited: on Windows that performs exactly the zero-timeout handle wait, on Unix
    // it reads procfs/sysctl. Go's EPERM branch (exists but unsignallable) has no
    // managed analogue; any lookup or query failure reports "gone" (Go's
    // OpenProcess/FindProcess failure paths do the same).
    internal static bool IsProcessAlive(int pid)
    {
        if (!Supported)
        {
            // isprocessalive_other.go:8 — Go: panic("isProcessAlive is not supported on this platform").
            throw new PlatformNotSupportedException("isProcessAlive is not supported on this platform");
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No live process with this PID (Go: ESRCH / OpenProcess failure).
            return false;
        }
        catch (InvalidOperationException)
        {
            // Handle no longer queryable: the process exited between lookup and probe.
            return false;
        }
    }
}
