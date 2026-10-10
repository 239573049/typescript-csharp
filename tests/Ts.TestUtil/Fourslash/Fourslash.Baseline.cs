// Ported from tsgo internal/fourslash/baselineutil.go — the session-side half: the baseline
// accumulators (addResultToBaseline / writeToBaseline, :47-74) and the end-of-test flush
// (fourslash.go:5041-5049 verifyBaselines). The state-baseline branch throws at New() because
// the project-snapshot surface it rides on is not ported (plan/11 known deviation).
//
// PORT-NOTE: the baseline comparison itself is the shared tests/Ts.TestUtil/Baseline/Baseline.cs
// port of internal/testutil/baseline. The per-command DiffFixupOld/DiffFixupNew closures of
// getBaselineOptions (baselineutil.go:108-491 — Strada-format text fixups keyed to the
// TypeScript submodule reference) live in FourslashBaselineUtil and are reached through
// GetBaselineOptions, exactly as Go routes them.

#if TS_FOURSLASH_RUNNER

using System;
using Xunit;

namespace Ts.TestUtil.Fourslash;

public sealed partial class FourslashTest
{
    // baselineutil.go:47-65 — addResultToBaseline. (The state-baselining branch reroutes to the
    // single state builder in Go; the state baseline port is omitted, so the session throws at
    // construction when statebaseline=true and this method always takes the per-command path.)
    private void addResultToBaseline(FourslashBaselineCommand command, string actual)
    {
        baselines.AddResult(command, actual);
    }

    // baselineutil.go:67-74 — writeToBaseline.
    private void writeToBaseline(FourslashBaselineCommand command, string content)
    {
        baselines.WriteTo(command, content);
    }

    // fourslash.go:5041-5049 — verifyBaselines (called from Dispose, where Go's deferred done()
    // closure runs). xUnit has no soft assert: the collecting context records every baseline
    // difference and they are surfaced as one test failure.
    private void verifyBaselines(string testBaseName)
    {
        if (testData.IsStateBaseliningEnabled)
        {
            // Unreachable: New() rejects statebaselining. Kept for shape parity with
            // fourslash.go:5046-5048.
            throw new NotSupportedException("state baseline port omitted");
        }

        // Go recovers `testPath` with runtime.Caller(1) — the path of the generated *_test.go
        // file, which lives under internal/fourslash/tests/gen/. Every routed decision downstream
        // keys off that substring (isSubmoduleTest, baselineutil.go:493-495), so the C# session
        // reconstructs the same path shape from the emitted test base name: per-command baselines
        // land in baselines/{local,reference}/submodule/fourslash/<command>/… while the responses
        // are diffed against the Strada reference in the TypeScript submodule. The
        // non-submodule branch (baselines/…/fourslash/<command>/…) is only for the manual tests
        // that live under fourslash/tests/manual.
        string testPath = "fourslash/tests/gen/" + testBaseName + "_test.go";

        var context = new BaselineTestContext();
        foreach ((FourslashBaselineCommand command, string content) in baselines.Entries())
        {
            Baseline.Run(
                context,
                FourslashBaselineUtil.GetBaselineFileName(testBaseName, command),
                content,
                FourslashBaselineUtil.GetBaselineOptions(command, testPath));
        }
        if (context.Failed)
        {
            Assert.Fail(string.Join("\n", context.Errors));
        }
    }
}

#endif
