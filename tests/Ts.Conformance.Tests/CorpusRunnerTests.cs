using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace Ts.Conformance.Tests;

public sealed class CorpusRunnerTests
{
    // The whole corpus compiles once per test process; theories and the summary fact all read
    // this shared summary (same pattern as CompilerBaselineRunnerTests).
    private static readonly Lazy<ConformanceRunSummary> s_summary = new(
        CorpusRunner.RunCorpus,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, ConformanceCaseResult>> s_resultsByName = new(
        () => s_summary.Value.Results.ToDictionary(
            result => result.Name,
            result => result,
            StringComparer.Ordinal),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IEnumerable<object[]> CorpusCaseNames() =>
        CorpusRunner.Selection.Selected.Select(testCase =>
            new object[] { CompilerBaselineRunner.GetCaseName(testCase.BaselineName) });

    [Theory]
    [MemberData(nameof(CorpusCaseNames))]
    public void CorpusCase_MatchesErrorsBaselineOrIsListedFailing(string name)
    {
        ConformanceCaseResult result = s_resultsByName.Value[name];
        if (result.Matched)
        {
            return;
        }
        if (FailingCases.Names.Contains(name))
        {
            Assert.Skip($"listed in failingCases.txt: {result.Failure ?? "errors.txt baseline mismatch"}");
        }
        if (CorpusRunner.UpdateFailingList)
        {
            // Regeneration run: the summary fact rewrites failingCases.txt from this run's
            // failures; individual theories must not fail on cases the new list will cover.
            Assert.Skip($"TSCS_CONFORMANCE_UPDATE_FAILING run: {result.Failure ?? "errors.txt baseline mismatch"}");
        }
        Assert.Fail(
            $"conformance regression: '{name}' is not listed in failingCases.txt but no longer " +
            $"byte-matches its errors baseline: {result.Failure ?? "errors.txt baseline mismatch"}");
    }

    [Fact]
    public void CorpusSummary_ReportsMatchRateAndAccounting()
    {
        CorpusSelection selection = CorpusRunner.Selection;
        ConformanceRunSummary summary = s_summary.Value;

        // Selection sanity: the first wave must stay in the hundreds (unless a smaller smoke cap
        // was requested via TSCS_CONFORMANCE_WAVE_CAP) and every selected case has a reference
        // baseline by construction of the enumerator.
        Assert.InRange(selection.Selected.Count, Math.Min(300, CorpusRunner.WaveCap), 5000);
        Assert.Equal(summary.Total, selection.Selected.Count);
        Assert.Equal(summary.Total, summary.ReferencesPresent);
        Assert.Equal(selection.TotalEnumerated, selection.Selected.Count + selection.Skipped.Count);

        if (CorpusRunner.UpdateFailingList)
        {
            FailingCases.Save(summary.Results.Where(result => !result.Matched).Select(result => result.Name));
        }

        double percent = summary.Total == 0 ? 0 : 100.0 * summary.Passed / summary.Total;
        Console.WriteLine(
            $"conformance corpus (errors.txt byte-match): {summary.Passed}/{summary.Total} " +
            $"({percent:F1}%) matched; {summary.Failed} failing " +
            $"({(CorpusRunner.FullCorpus ? "full corpus" : $"first-wave sample, cap {CorpusRunner.DefaultWaveCap}")}); " +
            $"{selection.TotalEnumerated} files enumerated.");
        foreach ((string reason, int count) in selection.SkipReasonCounts)
        {
            Console.WriteLine($"  skipped {count}: {reason}");
        }
        foreach ((string cluster, int count) in CorpusRunner.ClusterFailures(summary).Take(15))
        {
            Console.WriteLine($"  failing {count}: {cluster}");
        }
    }
}
