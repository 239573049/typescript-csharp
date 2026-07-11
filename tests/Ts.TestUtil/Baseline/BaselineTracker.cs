// Ported from tsgo internal/testutil/baseline/testmain.go.
//
// Tracks every baseline file written during a run so an aggregator (CI's unused-baselines
// job, plan/08-testing-strategy.md §4.9) can flag reference baselines that no test ever
// produced. Enabled only when the tracking dir env var is set.
//
// PORT-NOTES:
//   - Env var renamed TSGO_BASELINE_TRACKING_DIR -> TSCS_BASELINE_TRACKING_DIR (§4.9).
//   - Go hashes the runtime call stack (fnv64a over frame file paths) to get one tracking
//     file per calling *package*. There is no C# call-stack-file equivalent; we hash the
//     calling *assembly* name instead — same intent (one file per test assembly), still a
//     deterministic 16-hex-digit name. Track() therefore takes the caller Assembly.
//   - collections.SyncSet -> Ts.Collections.SyncSet.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Ts.Collections;

namespace Ts.TestUtil;

public static class BaselineTracker
{
    // recordedBaselines tracks all baseline file paths that were written during the test run.
    private static readonly SyncSet<string> s_recordedBaselines = new();

    // trackingInitialized is set to true when Track() is called.
    private static bool s_trackingInitialized;

    // trackingDir is the directory where tracking files should be written.
    // If non-empty, baseline tracking is enabled.
    private static readonly string s_trackingDir =
        Environment.GetEnvironmentVariable("TSCS_BASELINE_TRACKING_DIR") ?? "";

    // Go: Track sets up baseline tracking and returns a cleanup function that writes the
    // tracking file. It should be called from a test-assembly fixture and the returned
    // action invoked on teardown.
    public static Action Track(Assembly caller)
    {
        s_trackingInitialized = true;

        if (s_trackingDir.Length == 0)
        {
            return static () => { };
        }

        // Hash the caller assembly name to create a unique filename per test assembly.
        // (Go hashes the entire call stack's file paths; see PORT-NOTE.)
        ulong h = Fnv64a(caller.GetName().Name ?? caller.FullName ?? "");
        string trackingPath = Path.Combine(s_trackingDir, h.ToString("x16", CultureInfo.InvariantCulture) + ".txt");

        return () => WriteRecordedBaselines(trackingPath);
    }

    // Go: recordBaseline adds a baseline file path to the recorded set.
    // The path should be relative to the baselines/reference directory.
    internal static void RecordBaseline(IBaselineTestContext t, string relativePath)
    {
        if (s_trackingDir.Length != 0)
        {
            if (!s_trackingInitialized)
            {
                t.Error("baseline: package uses baselines but TestMain did not call baseline.Track(). " +
                    "Please add a TestMain function with: defer baseline.Track()()");
                return;
            }
            s_recordedBaselines.Add(relativePath);
        }
    }

    // Go: writeRecordedBaselines writes the list of recorded baseline files to a tracking file.
    private static void WriteRecordedBaselines(string trackingPath)
    {
        if (s_recordedBaselines.Size() == 0)
        {
            return;
        }

        try
        {
            DoWriteRecordedBaselines(trackingPath);
        }
        catch (IOException err)
        {
            Console.Error.WriteLine($"baseline: failed to write tracking file {trackingPath}: {err.Message}");
            Environment.Exit(1);
        }
    }

    private static void DoWriteRecordedBaselines(string trackingPath)
    {
        using var w = new StreamWriter(trackingPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        // map-iter: 序无关 — the tracking file is consumed as a set by the aggregator.
        foreach (string baseline in s_recordedBaselines.Keys())
        {
            w.Write(baseline);
            w.Write('\n');
        }
    }

    private static ulong Fnv64a(string s)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
