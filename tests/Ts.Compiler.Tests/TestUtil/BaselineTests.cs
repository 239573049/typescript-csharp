// Ported from tsgo internal/testutil/baseline/baseline_test.go (the two "files exist" checks
// over submoduleAccepted.txt / submoduleTriaged.txt), plus C#-side coverage for the diff
// pipeline (GetBaselineDiff, DiffText) and the compare/accept core (WriteComparison) that
// baseline.go's tests do not exercise directly.

using System;
using System.Globalization;
using System.IO;
using Ts.Collections;
using Ts.TestUtil;
using Xunit;
// Class name collides with the last segment of its namespace; alias to reach the type.

namespace Ts.TestUtil.Tests;

public class BaselineTests
{
    // Go: TestSubmoduleAcceptedFilesExist
    [Fact]
    public void SubmoduleAcceptedFilesExist()
    {
        foreach (string name in Baseline.SubmoduleAcceptedFileNames.Keys())
        {
            string path = Path.Combine(Baseline.ReferenceRoot, "submoduleAccepted", name);
            Assert.True(
                File.Exists(path),
                $"submoduleAccepted.txt references \"{name}\", but the baseline file does not exist");
        }
    }

    // Go: TestSubmoduleTriagedFilesExist
    [Fact]
    public void SubmoduleTriagedFilesExist()
    {
        foreach (string name in Baseline.SubmoduleTriagedFileNames.Keys())
        {
            string path = Path.Combine(Baseline.ReferenceRoot, "submoduleTriaged", name);
            Assert.True(
                File.Exists(path),
                $"submoduleTriaged.txt references \"{name}\", but the baseline file does not exist");
        }
    }

    [Fact]
    public void GetBaselineDiffReturnsNoContentWhenEqual()
    {
        Assert.Equal(Baseline.NoContent, Baseline.GetBaselineDiff("same", "same", "x", null, null));
    }

    [Fact]
    public void GetBaselineDiffAppliesFixups()
    {
        // fixupNew rewrites actual to match expected -> no diff.
        Assert.Equal(
            Baseline.NoContent,
            Baseline.GetBaselineDiff("b", "a", "x", fixupOld: null, fixupNew: _ => "a"));
    }

    [Fact]
    public void GetBaselineDiffRewritesHunkHeaders()
    {
        string expected = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10";
        string actual = "l1\nl2\nl3\nl4\nl5b\nl6\nl7\nl8\nl9\nl10";

        string diff = Baseline.GetBaselineDiff(actual, expected, "x", null, null);

        // The literal "@@ -N,M +.." header is rewritten to the line-number-free skipped form.
        Assert.Equal(
            "--- old.x\n+++ new.x\n@@= skipped -1, +1 lines =@@\n l2\n l3\n l4\n-l5\n+l5b\n l6\n l7\n l8",
            diff);
    }

    [Fact]
    public void DiffTextEmitsHeaders()
    {
        string diff = Baseline.DiffText("old.x", "new.x", "a\nb\nc", "a\nB\nc");
        Assert.StartsWith("--- old.x\n+++ new.x\n@@", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteComparisonNewBaselineReportsAndWritesLocal()
    {
        RunInTempDir((local, reference) =>
        {
            var t = new BaselineTestContext();
            Baseline.WriteComparison(t, "hello", local, reference, comparingAgainstSubmodule: false);

            Assert.True(File.Exists(local));
            Assert.Equal("hello", File.ReadAllText(local));
            Assert.Single(t.Errors);
            Assert.Contains("new baseline created at", t.Errors[0], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WriteComparisonMatchingReferenceReportsNothing()
    {
        RunInTempDir((local, reference) =>
        {
            File.WriteAllText(reference, "hello");
            var t = new BaselineTestContext();
            Baseline.WriteComparison(t, "hello", local, reference, comparingAgainstSubmodule: false);

            Assert.False(t.Failed);
            Assert.False(File.Exists(local));
        });
    }

    [Fact]
    public void WriteComparisonChangedReferenceReportsAndWritesLocal()
    {
        RunInTempDir((local, reference) =>
        {
            File.WriteAllText(reference, "hello");
            var t = new BaselineTestContext();
            Baseline.WriteComparison(t, "world", local, reference, comparingAgainstSubmodule: false);

            Assert.True(File.Exists(local));
            Assert.Equal("world", File.ReadAllText(local));
            Assert.Single(t.Errors);
            Assert.Contains("has changed", t.Errors[0], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WriteComparisonNoContentWithExistingReferenceWritesDeleteMarker()
    {
        RunInTempDir((local, reference) =>
        {
            File.WriteAllText(reference, "hello");
            var t = new BaselineTestContext();
            Baseline.WriteComparison(t, Baseline.NoContent, local, reference, comparingAgainstSubmodule: false);

            Assert.True(File.Exists(local + ".delete"));
            Assert.False(File.Exists(local));
            Assert.Single(t.Errors);
        });
    }

    [Fact]
    public void WriteComparisonEmptyContentThrows()
    {
        RunInTempDir((local, reference) =>
        {
            var t = new BaselineTestContext();
            Assert.Throws<InvalidOperationException>(
                () => Baseline.WriteComparison(t, "", local, reference, comparingAgainstSubmodule: false));
        });
    }

    private static void RunInTempDir(Action<string, string> body)
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "tscs-baseline-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string local = Path.Combine(dir, "local", "sub", "file.txt");
            string reference = Path.Combine(dir, "reference", "sub", "file.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
            body(local, reference);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
