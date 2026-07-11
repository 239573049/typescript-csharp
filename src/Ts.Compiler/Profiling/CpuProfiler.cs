// Ported from tsgo internal/pprof/pprof.go CPUProfiler (pprof.go:68-121).
//
// PORT-NOTE: 非 1:1，EventPipe 重写 (plan/06 §7.2). On-demand CPU profiling for the LSP:
// start writes to a per-request `.nettrace`, stop returns its path. Go uses a sync.Mutex
// guarding a single session; C# uses a lock. Error handling follows plan/05 §4: these
// (T, error) shapes are consumed locally by the LSP handler and their text is surfaced to
// the client, so we use the Try-with-error-text pattern.
//
// PORT-NOTE naming: Go type `CPUProfiler` → `CpuProfiler` (05-conventions PascalCase for
// acronyms); the plan face (§7.2) lists "CpuProfiler".

using System;
using System.Globalization;
using System.IO;

namespace Ts.Profiling;

// CpuProfiler manages on-demand CPU profiling (pprof.go:68-72).
public sealed class CpuProfiler
{
    private readonly object mu = new();
    private ProfileSession? session;
    private string? sessionCpuPath;

    private readonly IProfileCapture capture;

    public CpuProfiler()
        : this(EventPipeProfileCapture.Instance)
    {
    }

    // PORT-NOTE: 非 1:1 seam — injectable capture backend for tests.
    public CpuProfiler(IProfileCapture capture)
    {
        this.capture = capture;
    }

    // StartCPUProfile starts CPU profiling, writing to the specified directory when
    // stopped (pprof.go:75-105).
    //
    // Go returns error; consumed locally by the LSP. plan/05 §4: `string? StartCPUProfile`
    // returns the error message (null = success), text preserved verbatim.
    public string? StartCPUProfile(string profileDir)
    {
        lock (mu)
        {
            if (session != null)
            {
                return "CPU profiling already in progress";
            }

            try
            {
                Directory.CreateDirectory(profileDir);
            }
            catch (Exception ex)
            {
                return "failed to create profile directory: " + ex.Message;
            }

            // PORT-NOTE: .nettrace extension instead of .pb.gz (format 允许偏离, plan §7.2).
            string cpuProfilePath = Path.Combine(
                profileDir,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-cpuprofile.nettrace"));

            try
            {
                capture.StartCpu(cpuProfilePath);
            }
            catch (Exception ex)
            {
                return "failed to start CPU profile: " + ex.Message;
            }

            // Go builds a ProfileSession with io.Discard as the log writer.
            session = new ProfileSession(cpuProfilePath, "", TextWriter.Null, capture);
            sessionCpuPath = cpuProfilePath;
            return null;
        }
    }

    // StopCPUProfile stops CPU profiling and returns the path to the profile file
    // (pprof.go:108-121).
    //
    // Go returns (string, error); consumed locally. plan/05 §4 Try-with-error-text:
    // `bool TryStopCPUProfile(out string filePath, out string? error)`.
    public bool TryStopCPUProfile(out string filePath, out string? error)
    {
        lock (mu)
        {
            if (session == null)
            {
                filePath = "";
                error = "CPU profiling not in progress";
                return false;
            }

            filePath = sessionCpuPath ?? "";
            session.Stop();
            session = null;
            sessionCpuPath = null;
            error = null;
            return true;
        }
    }
}
