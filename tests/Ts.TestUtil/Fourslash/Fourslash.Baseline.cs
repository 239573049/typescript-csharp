// Ported from tsgo internal/fourslash/baselineutil.go — the session-side half: the baseline
// accumulators (addResultToBaseline / writeToBaseline, :47-74) and the end-of-test flush
// (fourslash.go:5041-5049 verifyBaselines). The state-baseline branch throws at New() because
// the project-snapshot surface it rides on is not ported (plan/11 known deviation).
//
// PORT-NOTE: the baseline comparison itself is the shared tests/Ts.TestUtil/Baseline/Baseline.cs
// port of internal/testutil/baseline. The per-command DiffFixupOld/DiffFixupNew closures of
// getBaselineOptions (baselineutil.go:108-491 — Strada-format text fixups keyed to the
// TypeScript submodule reference) live in FourslashBaselineUtil. This partial deliberately does
// NOT go through FourslashBaselineUtil.GetBaselineOptions: that takes the isSubmoduleTest branch
// and diffs against the TypeScript submodule reference, which is unreachable for the C# port
// (the emitted corpus's own expected baselines live under testdata/baselines/expected, not in
// _submodules/TypeScript). The plain comparison against the local reference tree is the shape
// the port's producers target. See plan/11 D-021.

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
        // file, which lives under internal/fourslash/tests/gen/. The C# corpus is generated from
        // exactly that tree; the submodule branch it selects is deliberately not taken here (see
        // the header). The expected-local path is
        // baselines/expected/fourslash/<normalizedCommand>/<name> — the port's stand-in for the
        // submodule baselines the C# tree does not carry, and the files the ported producers
        // target.
        var context = new BaselineTestContext();
        foreach ((FourslashBaselineCommand command, string content) in baselines.Entries())
        {
            string subfolder = "fourslash/" + FourslashBaselineUtil.NormalizeCommandName(command.Value);
            Baseline.Run(
                context,
                FourslashBaselineUtil.GetBaselineFileName(testBaseName, command),
                content,
                new Baseline.Options { Subfolder = subfolder });
        }
        if (context.Failed)
        {
            Assert.Fail(string.Join("\n", context.Errors));
        }
    }
}

#endif
