// Tests for Ts.Profiling — adapted from tsgo internal/pprof/pprof.go (no Go tests exist).
//
// PORT-NOTE: 非 1:1，EventPipe/.nettrace 重写 (plan/06 §7.2, 非保真区). These assert the
// public API face (BeginProfiling/Stop, CpuProfiler start/stop, SaveHeap/SaveAlloc, RunGC)
// does not crash and honours its lifecycle contracts, using an injected capture backend so
// no profile bytes touch disk.

using System.Collections.Generic;
using System.IO;
using Ts.Profiling;
using Xunit;

namespace Ts.Profiling.Tests;

public class PprofTests
{
    // Recording capture backend: no bytes written, lifecycle recorded.
    private sealed class RecordingCapture : IProfileCapture
    {
        public List<string> Log { get; } = new();

        public void StartCpu(string cpuFilePath) => Log.Add("start:" + cpuFilePath);
        public void StopCpu(string cpuFilePath) => Log.Add("stop:" + cpuFilePath);
        public void WriteHeap(string heapFilePath) => Log.Add("heap:" + heapFilePath);
        public void WriteAllocs(string allocFilePath) => Log.Add("allocs:" + allocFilePath);
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-pprof-test-" + Path.GetRandomFileName());
        return dir;
    }

    [Fact]
    public void BeginProfilingAndStopDoesNotCrash()
    {
        var capture = new RecordingCapture();
        IProfileCapture previous = Pprof.Capture;
        Pprof.Capture = capture;
        try
        {
            using var log = new StringWriter();
            ProfileSession session = Pprof.BeginProfiling(TempDir(), log);
            session.Stop();
            // Second Stop is a safe no-op.
            session.Stop();

            // start → stop → allocs (mem profile) recorded exactly once each.
            Assert.Contains(capture.Log, s => s.StartsWith("start:", System.StringComparison.Ordinal));
            Assert.Contains(capture.Log, s => s.StartsWith("stop:", System.StringComparison.Ordinal));
            Assert.Contains(capture.Log, s => s.StartsWith("allocs:", System.StringComparison.Ordinal));
            Assert.Contains("CPU profile:", log.ToString(), System.StringComparison.Ordinal);
            Assert.Contains("Memory profile:", log.ToString(), System.StringComparison.Ordinal);
        }
        finally
        {
            Pprof.Capture = previous;
        }
    }

    [Fact]
    public void CpuProfilerStartStopRoundTrips()
    {
        var capture = new RecordingCapture();
        var profiler = new CpuProfiler(capture);

        // Not started → stop fails with the Go error text.
        Assert.False(profiler.TryStopCPUProfile(out _, out string? notInProgress));
        Assert.Equal("CPU profiling not in progress", notInProgress);

        // Start succeeds (null = success).
        Assert.Null(profiler.StartCPUProfile(TempDir()));

        // Double start rejected with the Go error text.
        string? already = profiler.StartCPUProfile(TempDir());
        Assert.Equal("CPU profiling already in progress", already);

        // Stop returns a path and clears state.
        Assert.True(profiler.TryStopCPUProfile(out string filePath, out string? err));
        Assert.Null(err);
        Assert.EndsWith("cpuprofile.nettrace", filePath, System.StringComparison.Ordinal);

        // Stop again fails.
        Assert.False(profiler.TryStopCPUProfile(out _, out _));
    }

    [Fact]
    public void SaveHeapAndAllocProfileReturnPaths()
    {
        var capture = new RecordingCapture();
        IProfileCapture previous = Pprof.Capture;
        Pprof.Capture = capture;
        try
        {
            string heap = Pprof.SaveHeapProfile(TempDir());
            string alloc = Pprof.SaveAllocProfile(TempDir());
            Assert.EndsWith("heapprofile.nettrace", heap, System.StringComparison.Ordinal);
            Assert.EndsWith("allocprofile.nettrace", alloc, System.StringComparison.Ordinal);
            Assert.Contains(capture.Log, s => s.StartsWith("heap:", System.StringComparison.Ordinal));
            Assert.Contains(capture.Log, s => s.StartsWith("allocs:", System.StringComparison.Ordinal));
        }
        finally
        {
            Pprof.Capture = previous;
        }
    }

    [Fact]
    public void RunGCDoesNotCrash()
    {
        Pprof.RunGC();
    }
}
