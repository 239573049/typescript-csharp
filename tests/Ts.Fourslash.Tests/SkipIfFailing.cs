// Ported from tsgo internal/fourslash/skip_if_failing.go (whole file).
//
// The failing-test list is the Go repo's internal/fourslash/_scripts/failingTests.txt, mirrored
// into this project as Scripts/failingTests.txt and embedded (plan/08 §7.4). SkipIfFailing
// checks if the current test is in that file and skips it unless the
// TSGO_FOURSLASH_IGNORE_FAILING environment variable is set. This allows tests to be marked as
// failing without modifying the test files themselves.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace Ts.Fourslash.Tests;

public static class SkipIfFailing
{
    // skip_if_failing.go:13-38 — the sync.OnceValue failingTests set → Lazy + embedded resource
    // (the Go original resolves the file relative to the source tree via runtime.Caller; the C#
    // mirror ships it as an EmbeddedResource so the runner has no on-disk dependency).
    private static readonly Lazy<HashSet<string>> FailingTests = new(LoadFailingTests);

    private static HashSet<string> LoadFailingTests()
    {
        var failingTestsSet = new HashSet<string>(StringComparer.Ordinal);

        Assembly assembly = typeof(SkipIfFailing).Assembly;
        Stream? stream = assembly.GetManifestResourceStream("Ts.Fourslash.Tests.Scripts.failingTests.txt");
        if (stream == null)
        {
            return failingTestsSet;
        }
        try
        {
            using (stream)
            using (var reader = new StreamReader(stream))
            {
                while (reader.ReadLine() is { } line)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length != 0)
                    {
                        failingTestsSet.Add(trimmed);
                    }
                }
            }
        }
        catch (IOException)
        {
            return [];
        }

        return failingTestsSet;
    }

    // skip_if_failing.go:43-53 — SkipIfFailing.
    public static void Check()
    {
        if (Environment.GetEnvironmentVariable("TSGO_FOURSLASH_IGNORE_FAILING") != "")
        {
            return;
        }

        string? testMethodName = TestContext.Current.TestCase?.TestMethodName;
        if (testMethodName != null && FailingTests.Value.Contains(testMethodName))
        {
            Assert.Skip("Test is in failingTests.txt");
        }
    }
}
