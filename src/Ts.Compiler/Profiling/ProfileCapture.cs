// Capture backend abstraction for the profiling face.
//
// PORT-NOTE: 非 1:1 seam (plan/06 §7.2, "非保真区"). Go binds directly to runtime/pprof;
// there is no capture-backend interface in tsgo. This seam lets the CLI wire a real
// EventPipe capture (or leave the default) and lets tests assert start/stop lifecycle
// without emitting profile bytes.

using System.Diagnostics.Tracing;
using System.IO;

namespace Ts.Profiling;

// IProfileCapture is the pluggable profiling backend.
public interface IProfileCapture
{
    // StartCpu begins CPU sampling, intending to write to cpuFilePath on StopCpu.
    void StartCpu(string cpuFilePath);

    // StopCpu ends CPU sampling and finalizes cpuFilePath.
    void StopCpu(string cpuFilePath);

    // WriteHeap writes a heap snapshot to heapFilePath.
    void WriteHeap(string heapFilePath);

    // WriteAllocs writes an allocation snapshot to allocFilePath.
    void WriteAllocs(string allocFilePath);
}

// EventPipeProfileCapture is the default backend.
//
// PORT-NOTE: 非 1:1，EventPipe/.nettrace 重写 (plan/06 §7.2). Under Native AOT, in-process
// EventPipe self-capture to a file requires Microsoft.Diagnostics.NETCore.Client, which is
// deliberately NOT added to the shared package set here (keeps this assembly self-contained
// and the profiling surface in its registered "非保真区"). This default therefore:
//   - toggles the profiling EventSource so an attached `dotnet-trace` collects the stream,
//   - writes a small placeholder marker file at the intended `.nettrace` path so callers'
//     path-returning contracts (and the "CPU profile: <path>" log line) stay well-defined,
//     never crashing.
// A CLI can replace Pprof.Capture / CpuProfiler(capture) with a real EventPipe backend.
public sealed class EventPipeProfileCapture : IProfileCapture
{
    public static readonly EventPipeProfileCapture Instance = new();

    private EventPipeProfileCapture()
    {
    }

    public void StartCpu(string cpuFilePath)
    {
        _ = cpuFilePath;
        // Signal profiling start on the EventSource so external EventPipe collectors engage.
        ProfilingEventSource.Log.CpuProfileStarted(cpuFilePath);
    }

    public void StopCpu(string cpuFilePath)
    {
        ProfilingEventSource.Log.CpuProfileStopped(cpuFilePath);
        WriteMarker(cpuFilePath, "cpu");
    }

    public void WriteHeap(string heapFilePath)
    {
        ProfilingEventSource.Log.HeapProfileSaved(heapFilePath);
        WriteMarker(heapFilePath, "heap");
    }

    public void WriteAllocs(string allocFilePath)
    {
        ProfilingEventSource.Log.AllocProfileSaved(allocFilePath);
        WriteMarker(allocFilePath, "allocs");
    }

    // Writes a minimal, well-formed placeholder so path contracts hold even without a real
    // EventPipe backend. Best-effort: never throws out (the profiling surface must not crash
    // the compiler on a diagnostics-only path).
    private static void WriteMarker(string path, string kind)
    {
        try
        {
            File.WriteAllText(
                path,
                "// Ts.Profiling placeholder (.nettrace, non-fidelity zone): " + kind + " profile\n");
        }
        catch (IOException)
        {
            // Diagnostics path: swallow write failures (matches "非保真区" tolerance).
        }
        catch (System.UnauthorizedAccessException)
        {
        }
    }
}

// ProfilingEventSource surfaces profiling lifecycle to EventPipe (dotnet-trace).
// PORT-NOTE: 非 1:1 (plan §7.2). All payloads are strings — AOT-safe EventSource usage.
[EventSource(Name = "Ts-Profiling")]
internal sealed class ProfilingEventSource : EventSource
{
    internal static readonly ProfilingEventSource Log = new();

    private ProfilingEventSource()
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    internal void CpuProfileStarted(string path)
    {
        if (IsEnabled())
        {
            WriteEvent(1, path);
        }
    }

    [Event(2, Level = EventLevel.Informational)]
    internal void CpuProfileStopped(string path)
    {
        if (IsEnabled())
        {
            WriteEvent(2, path);
        }
    }

    [Event(3, Level = EventLevel.Informational)]
    internal void HeapProfileSaved(string path)
    {
        if (IsEnabled())
        {
            WriteEvent(3, path);
        }
    }

    [Event(4, Level = EventLevel.Informational)]
    internal void AllocProfileSaved(string path)
    {
        if (IsEnabled())
        {
            WriteEvent(4, path);
        }
    }
}
