using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace Ts.Conformance.Tests;

public sealed class CompilerBaselineRunnerTests
{
    private static readonly Lazy<ConformanceRunSummary> s_summary = new(
        CompilerBaselineRunner.RunCurated,
        LazyThreadSafetyMode.ExecutionAndPublication);

    // The checker is intentionally WIP. These names are known baseline mismatches or currently
    // blocked checker paths; each theory row dynamically skips until its byte baseline matches.
    // Removing a name makes a regression fail instead of silently broadening the gate.
    private static readonly HashSet<string> s_checkerWipAllowlist = new(StringComparer.Ordinal)
    {
        "invalidAssignmentsToVoid",
        "invalidVoidAssignments",
        // boolInsteadOfBoolean now byte-matches: default-lib loading via triple-slash
        // reference directives resolves global types (Boolean/Array/...), so the checker
        // emits the correct TS2304 baseline instead of location-less TS2318 false errors.
        "nonPrimitiveAssignError",
        "assignmentToParenthesizedIdentifiers",
        "assignmentCompatWithObjectMembersOptionality",
        "typeParameterAssignability",
        "callSignatureWithOptionalParameterAndInitializer",
        "trailingCommasInBindingPatterns",
    };

    public static IEnumerable<object[]> CuratedCaseNames() =>
        CompilerBaselineRunner.CuratedCases.Select(testCase =>
            new object[] { CompilerBaselineRunner.GetCaseName(testCase.BaselineName) });

    [Theory]
    [MemberData(nameof(CuratedCaseNames))]
    public void CuratedCase_MatchesErrorsBaselineOrIsCheckerWipSkipped(string name)
    {
        ConformanceCaseResult result = s_summary.Value.Results.Single(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (!result.Matched)
        {
            Assert.Contains(name, s_checkerWipAllowlist);
            Assert.Skip($"checker WIP: {result.Failure ?? "errors.txt baseline mismatch"}");
        }
    }

    [Fact]
    public void CuratedRun_ReportsPassFailCountsAndHasReferenceBaselines()
    {
        ConformanceRunSummary summary = s_summary.Value;
        Assert.InRange(summary.Total, 10, 20);
        Assert.True(summary.Total >= 5, "curated subset must exercise at least five type-error cases");
        Assert.Equal(summary.Total, summary.ReferencesPresent);

        foreach (ConformanceCaseResult mismatch in summary.Results.Where(result => !result.Matched))
        {
            Assert.Contains(mismatch.Name, s_checkerWipAllowlist);
        }

        Console.WriteLine(
            $"Curated conformance errors baselines: {summary.Passed} passed, {summary.Failed} checker-WIP skipped, {summary.Total} total; " +
            $"{summary.ReferencesPresent} reference baselines present.");
    }
}
