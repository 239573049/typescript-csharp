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
            VerifyTypeAndSymbolBaselines(name, result);
            return;
        }
        if (FailingCases.Names.Contains(name))
        {
            Assert.Skip($"listed in failingCases.txt: {result.Failure ?? "errors.txt baseline mismatch"}");
        }
        if (CorpusRunner.IsFailingListUpdateRequested())
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
        Assert.InRange(selection.Selected.Count, 300, 5000);
        Assert.Equal(summary.Total, selection.Selected.Count);
        Assert.Equal(summary.Total, summary.ReferencesPresent);
        Assert.Equal(selection.TotalEnumerated, selection.Selected.Count + selection.Skipped.Count);

        if (CorpusRunner.IsFailingListUpdateRequested())
        {
            FailingCases.Save(summary.Results.Where(result => !result.Matched).Select(result => result.Name));
        }

        double percent = summary.Total == 0 ? 0 : 100.0 * summary.Passed / summary.Total;
        Console.WriteLine(
            $"conformance corpus (errors.txt byte-match): {summary.Passed}/{summary.Total} " +
            $"({percent:F1}%) matched; {summary.Failed} failing " +
            $"(full eligible corpus); " +
            $"{selection.TotalEnumerated} files enumerated.");
        foreach ((string reason, int count) in selection.SkipReasonCounts)
        {
            Console.WriteLine($"  skipped {count}: {reason}");
        }
        foreach ((string cluster, int count) in CorpusRunner.ClusterFailures(summary).Take(15))
        {
            Console.WriteLine($"  failing {count}: {cluster}");
        }

        if (CompilerBaselineRunner.TypesAndSymbolsEnabled)
        {
            List<ConformanceCaseResult> attempted = summary.Results
                .Where(result => result.Matched && result.TypeSymbols?.Types.Attempted == true)
                .ToList();
            if (CorpusRunner.UpdateFailingTypes)
            {
                FailingTypes.Save(attempted
                    .Where(result => result.TypeSymbols is null ||
                        !result.TypeSymbols.Types.Matched ||
                        !result.TypeSymbols.Symbols.Matched)
                    .Select(result => result.Name));
            }

            foreach (ConformanceCaseResult mismatch in attempted.Where(result =>
                result.TypeSymbols is null ||
                !result.TypeSymbols.Types.Matched ||
                !result.TypeSymbols.Symbols.Matched))
            {
                if (!CorpusRunner.UpdateFailingTypes)
                {
                    Assert.Contains(mismatch.Name, FailingTypes.Names);
                }
            }

            int typeMatches = attempted.Count(result => result.TypeSymbols!.Types.Matched);
            int symbolMatches = attempted.Count(result => result.TypeSymbols!.Symbols.Matched);
            int typeReferences = attempted.Count(result => result.TypeSymbols!.Types.ReferencePresent);
            int symbolReferences = attempted.Count(result => result.TypeSymbols!.Symbols.ReferencePresent);
            Console.WriteLine(
                $"conformance corpus (.types byte-match over errors-matching subset): " +
                $"{typeMatches}/{attempted.Count} matched; {typeReferences}/{attempted.Count} references present.");
            Console.WriteLine(
                $"conformance corpus (.symbols byte-match over errors-matching subset): " +
                $"{symbolMatches}/{attempted.Count} matched; {symbolReferences}/{attempted.Count} references present.");
        }
    }

    private static void VerifyTypeAndSymbolBaselines(string name, ConformanceCaseResult result)
    {
        if (!CompilerBaselineRunner.TypesAndSymbolsEnabled || result.TypeSymbols is null)
        {
            return;
        }
        if (result.TypeSymbols.Types.Matched && result.TypeSymbols.Symbols.Matched)
        {
            return;
        }
        string failure =
            $".types: {result.TypeSymbols.Types.Failure ?? "baseline mismatch"}; " +
            $".symbols: {result.TypeSymbols.Symbols.Failure ?? "baseline mismatch"}";
        if (FailingTypes.Names.Contains(name))
        {
            Assert.Skip($"listed in failingTypes.txt: {failure}");
        }
        if (CorpusRunner.UpdateFailingTypes)
        {
            Assert.Skip($"TSCS_CONFORMANCE_UPDATE_TYPES run: {failure}");
        }
        Assert.Fail(
            $"conformance regression: '{name}' is not listed in failingTypes.txt but its " +
            $".types/.symbols baselines do not both byte-match: {failure}");
    }
}
