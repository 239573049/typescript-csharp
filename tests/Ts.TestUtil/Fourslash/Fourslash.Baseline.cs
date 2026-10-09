// Ported from tsgo internal/fourslash/baselineutil.go — the session-side half: the baseline
// accumulators (addResultToBaseline / writeToBaseline, :47-74) and the end-of-test flush
// (fourslash.go:5041-5049 verifyBaselines). The state-baseline branch throws at New() because
// the project-snapshot surface it rides on is not ported (plan/11 known deviation).

#if TS_FOURSLASH_RUNNER

using System;

// Baseline / IBaselineTestContext resolve through the enclosing Ts.TestUtil namespace.
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

    // fourslash.go:5041-5049 — verifyBaselines (called at the end of the test, where Go's
    // deferred done() closure runs).
    private void verifyBaselines(IBaselineTestContext t, string testBaseName)
    {
        if (!testData.IsStateBaseliningEnabled)
        {
            foreach ((FourslashBaselineCommand command, string content) in baselines.Entries())
            {
                Baseline.Run(t, FourslashBaselineUtil.GetBaselineFileName(testBaseName, command), content, FourslashBaselineUtil.GetBaselineOptions(command, testPath: ""));
            }
        }
        else
        {
            // Unreachable: New() rejects statebaselining (see New()). Kept for shape parity
            // with fourslash.go:5046-5048.
            throw new NotSupportedException("state baseline port omitted");
        }
    }
}

#endif
