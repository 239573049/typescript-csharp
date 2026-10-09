// Smoke tests for the phase-5 call-resolution + contextual-type wave
// (src/Ts.Compiler/Checker/Checker.CallResolution*.cs, Checker.ContextualType.cs).
//
// The overload-resolution trunk (checkCallExpression → getResolvedSignature → resolveCallExpression →
// resolveCall → chooseOverload → hasCorrectArity / isSignatureApplicable) is ported for real, but its
// applicability leaf still routes to deferred stubs (checkExpressionWithContextualType) for any argument
// that must be assignability-checked. These end-to-end tests therefore exercise the FULL program pipeline
// (parse → bind → check) and assert on the produced diagnostics; where a call path reaches a
// not-yet-landed stub the test is marked Skip with the reason so it stays honest rather than falsely
// green. The E2E harness mirrors Compiler/CheckerEndToEndTests.cs.

using System.Collections.Generic;
using System.Threading;
using Ts.Core;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Compiler.Tests;

public sealed class CallResolutionTests
{
    private const string CurrentDirectory = "/src";

    private static Program BuildProgram(IReadOnlyDictionary<string, string> files, string[] roots, CompilerOptions options)
    {
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var host = CompilerHostImpl.NewCompilerHost(CurrentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            roots,
            new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = CurrentDirectory });
        return new Program(new ProgramOptions { Host = host, Config = config, SingleThreaded = Tristate.True });
    }

    private static global::Ts.Ast.Diagnostic[] CheckFile(string source)
    {
        var files = new Dictionary<string, string> { ["/src/only.ts"] = source };
        var program = BuildProgram(files, ["/src/only.ts"], new CompilerOptions { NoLib = Tristate.True, Types = [] });
        program.BindSourceFiles();
        var sf = program.GetSourceFile("/src/only.ts")!;
        var (checker, release) = program.GetTypeChecker(CancellationToken.None);
        try
        {
            return checker.GetDiagnostics(sf, TestContext.Current.CancellationToken);
        }
        finally
        {
            release();
        }
    }

    // Calling a non-callable value (a `number`) must reach resolveCallExpression, find no call
    // signatures, and produce the "not callable" invocation error via invocationError/invocationErrorDetails.
    // This path exercises the trunk without needing argument assignability (no args), so it should not hit
    // the checkExpressionWithContextualType stub.
    //
    // The fixture declares the required global interfaces because the harness runs with NoLib. Without
    // them tsgo's isUntypedFunctionCall sees globalFunctionType == emptyObjectType, `number` is assignable
    // to `{}`, and the call is treated as an untyped call producing NO file diagnostic (verified against
    // tsgo: `tsgo --noLib --noEmit` on the bare two-line fixture emits only global TS2318s). With the
    // globals declared, tsgo emits exactly one file diagnostic:
    //   TS2349 This expression is not callable. / Type 'Number' has no call signatures.
    // Chain progress: checkInheritedPropertiesAreIdentical ✓ (compareProperties/isPropertyIdenticalTo
    // ported) → invocationErrorDetails ✓ — getAwaitedType and the full awaited chain landed in
    // Checker.AwaitedTypes.cs (#22 awaited-types wave), and NewDiagnosticChainForNode is real, so the
    // TS2349 head + TS2757 chain detail are produced end-to-end.
    [Fact]
    public void CallResolutionTrunk_RunsEndToEnd_AndRoutesThroughResolveCall()
    {
        // Exercise the call-resolution trunk end-to-end over a real bound program: `n()` where `n` is a
        // `number` drives checkCallExpression → getResolvedSignature → resolveCallExpression →
        // resolveErrorCall, and invocationError must report TS2349 for real (no stub-catch masking).
        var diags = CheckFile(
            "interface Array<T> {}\n" +
            "interface Boolean {}\n" +
            "interface CallableFunction {}\n" +
            "interface Function { prototype: object; }\n" +
            "interface IArguments {}\n" +
            "interface NewableFunction {}\n" +
            "interface Number {}\n" +
            "interface Object {}\n" +
            "interface RegExp {}\n" +
            "interface String { charAt(pos: number): string; }\n" +
            "const n: number = 0;\n" +
            "n();\n");
        var diag = Assert.Single(diags);
        Assert.Equal(2349, diag.Code()); // This expression is not callable.
        var chain = diag.MessageChain();
        Assert.NotNull(chain);
        var detail = Assert.Single(chain);
        Assert.Equal(2757, detail.Code()); // Type '{0}' has no call signatures.
    }

    // A single-signature zero-argument function call resolves and yields its return type. Kept Skip for
    // the same reason: the resolveCall path reaches getEffectiveCallArguments/isSignatureApplicable which,
    // for the return-type check, still traverses deferred leaves. This documents intended coverage.
    [Fact(Skip = "single-signature call return-type resolution — pending expression-checking wave; the " +
        "resolveCall trunk is ported and builds 0/0.")]
    public void SingleSignatureCall_ResolvesReturnType()
    {
        var diags = CheckFile("function f(): number { return 0; }\nconst x: string = f();\n");
        Assert.NotEmpty(diags); // number not assignable to string
    }
}
