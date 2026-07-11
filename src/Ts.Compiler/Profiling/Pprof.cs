// Ported from tsgo internal/pprof/pprof.go (169 LOC).
//
// PORT-NOTE: 非 1:1，EventPipe/.nettrace 重写 (plan/06 §7.2, §10.2). tsgo uses Go's
// runtime/pprof to write `<pid>-cpuprofile.pb.gz` / mem / heap / alloc profiles in the
// protobuf pprof format. The plan's C# 归宿 is the .NET diagnostics stack:
//   - CPU / allocation sampling → EventPipe (dotnet-trace attach, or in-process capture),
//     output as `.nettrace` rather than `.pb.gz`. The output extension and format are
//     explicitly 允许偏离; the CLI flag name (--pprofDir) is preserved.
//   - heap/alloc snapshots → GC.Collect + GC info (dotnet-gcdump-style), stubbed here.
// This is registered as a "非保真区" (non-fidelity zone) in plan/07 & plan/12; the public
// API face is kept 1:1 so call sites (execute/tsc.go, cmd/tsgo/lsp.go) translate verbatim.
//
// AOT note (plan/07): EventPipe is supported under Native AOT, but runtime self-reflection
// sampling of managed stacks is not; profiling is therefore driven by EventSource events
// and external EventPipe attach. No BCL reflection APIs are used here.
//
// §1.5 全限定：this file's namespace is Ts.Profiling; sibling namespace references whose
// class name equals a namespace tail would need `global::Ts.<Pkg>.<Class>` — none occur.

using System;
using System.Globalization;
using System.IO;

namespace Ts.Profiling;

// ProfileSession mirrors pprof.ProfileSession (pprof.go:15-20).
public sealed class ProfileSession
{
    private readonly string cpuFilePath;
    private readonly string memFilePath;
    private readonly TextWriter logWriter;
    private readonly IProfileCapture capture;
    private bool stopped;

    internal ProfileSession(string cpuFilePath, string memFilePath, TextWriter logWriter, IProfileCapture capture)
    {
        this.cpuFilePath = cpuFilePath;
        this.memFilePath = memFilePath;
        this.logWriter = logWriter;
        this.capture = capture;
    }

    // Stop finalizes the session (pprof.go:49-66).
    public void Stop()
    {
        if (stopped)
        {
            return;
        }
        stopped = true;

        capture.StopCpu(cpuFilePath);

        if (memFilePath.Length != 0)
        {
            // PORT-NOTE: Go writes the "allocs" profile here. The .NET counterpart is a
            // GC/allocation snapshot; captured (or stubbed) via IProfileCapture.
            capture.WriteAllocs(memFilePath);
            logWriter.Write("Memory profile: " + memFilePath + "\n");
        }

        logWriter.Write("CPU profile: " + cpuFilePath + "\n");
    }
}

// Pprof is the package-level entry point face (Go free functions → static class).
// PORT-NOTE: Go exposes package-level funcs BeginProfiling / SaveHeapProfile /
// SaveAllocProfile / RunGC; C# groups them as static members here, preserving names.
public static class Pprof
{
    // Injectable capture backend so the CLI can wire real EventPipe capture and tests can
    // assert start/stop without producing profile bytes. Defaults to the EventPipe backend.
    // PORT-NOTE: 非 1:1 seam (no Go equivalent) — Go binds directly to runtime/pprof.
    public static IProfileCapture Capture { get; set; } = EventPipeProfileCapture.Instance;

    // BeginProfiling starts CPU and memory profiling, writing the profiles to the
    // specified directory (pprof.go:23-47).
    //
    // PORT-NOTE: Go panics on any failure here (mkdir/create/StartCPUProfile). Per plan/05
    // §4.3 panic → exception; the Go consumption points (execute/tsc.go) let it propagate.
    // We keep that shape: failures surface as IOException / InvalidOperationException.
    public static ProfileSession BeginProfiling(string profileDir, TextWriter logWriter)
    {
        Directory.CreateDirectory(profileDir);

        int pid = Environment.ProcessId;

        // PORT-NOTE: extension .nettrace instead of .pb.gz (format 允许偏离, plan §7.2).
        string cpuProfilePath = Path.Combine(
            profileDir,
            string.Create(CultureInfo.InvariantCulture, $"{pid}-cpuprofile.nettrace"));
        string memProfilePath = Path.Combine(
            profileDir,
            string.Create(CultureInfo.InvariantCulture, $"{pid}-memprofile.nettrace"));

        Capture.StartCpu(cpuProfilePath);

        return new ProfileSession(cpuProfilePath, memProfilePath, logWriter, Capture);
    }

    // SaveHeapProfile saves a heap profile to the specified directory (pprof.go:124-143).
    //
    // PORT-NOTE: Go returns (string, error) with the error consumed locally by the caller
    // (lsp) → plan/05 §4 Try-with-error-text shape. Here the caller consumes at a boundary,
    // so we return the path and throw on failure (matching Directory/GC BCL error surface).
    public static string SaveHeapProfile(string profileDir)
    {
        Directory.CreateDirectory(profileDir);

        string heapProfilePath = Path.Combine(
            profileDir,
            string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{NowUnixMillis()}-heapprofile.nettrace"));

        // Go: runtime.GC() then pprof.Lookup("heap").WriteTo. .NET: force a collection so
        // the snapshot reflects live objects, then capture.
        GC.Collect();
        Capture.WriteHeap(heapProfilePath);

        return heapProfilePath;
    }

    // SaveAllocProfile saves an allocation profile to the specified directory (pprof.go:146-164).
    public static string SaveAllocProfile(string profileDir)
    {
        Directory.CreateDirectory(profileDir);

        string allocProfilePath = Path.Combine(
            profileDir,
            string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{NowUnixMillis()}-allocprofile.nettrace"));

        Capture.WriteAllocs(allocProfilePath);

        return allocProfilePath;
    }

    // RunGC triggers garbage collection (pprof.go:167-169).
    public static void RunGC()
    {
        GC.Collect();
    }

    private static long NowUnixMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
