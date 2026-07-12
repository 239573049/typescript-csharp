// Smoke tests for the phase-5 literal-widening wave (Checker.Widening.cs):
// getWidenedLiteralType / getWidenedUniqueESSymbolType / mapType distribution.
//
// These exercise the real widening leaves directly on interned literal types built by
// GetNumberLiteralType / GetStringLiteralType + GetFreshTypeOfLiteralType — no binder pipeline,
// so no deferred stub is touched. Object-literal widening (getWidenedTypeOfObjectLiteral /
// getRegularTypeOfObjectLiteral) needs a fully checked object literal type (getPropertiesOfObjectType
// etc.), whose construction reaches not-yet-landed deep stubs, so that case is marked Skip.

using Ts.Ast;
using Ts.Checker;
using Ts.JsNum;
using Xunit;

namespace Ts.Checker.Tests;

public class WideningTests
{
    [Fact]
    public void GetWidenedLiteralType_FreshNumberLiteral_WidensToNumber()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        // The interned literal is regular; its fresh counterpart is the one that widens.
        var fresh = checker.GetFreshTypeOfLiteralType(checker.GetNumberLiteralType(new Number(1)));
        Assert.True(Checker.IsFreshLiteralType(fresh));

        var widened = checker.GetWidenedLiteralType(fresh);

        Assert.Same(checker.numberType, widened);
    }

    [Fact]
    public void GetWidenedLiteralType_FreshStringLiteral_WidensToString()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        var fresh = checker.GetFreshTypeOfLiteralType(checker.GetStringLiteralType("hello"));

        var widened = checker.GetWidenedLiteralType(fresh);

        Assert.Same(checker.stringType, widened);
    }

    [Fact]
    public void GetWidenedLiteralType_RegularLiteral_IsNotWidened()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        // A non-fresh (regular) literal type is preserved by widening (only fresh literals widen).
        var regular = checker.GetNumberLiteralType(new Number(42));
        Assert.False(Checker.IsFreshLiteralType(regular));

        var widened = checker.GetWidenedLiteralType(regular);

        Assert.Same(regular, widened);
    }

    [Fact]
    public void GetWidenedLiteralType_NonLiteral_IsIdentity()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        // numberType itself carries no fresh-literal flag → returned unchanged.
        var widened = checker.GetWidenedLiteralType(checker.numberType!);

        Assert.Same(checker.numberType, widened);
    }

    [Fact]
    public void GetWidenedUniqueESSymbolType_NonSymbolType_IsIdentity()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        // A plain string type is not a unique ES symbol → returned unchanged (no stub path).
        var result = checker.GetWidenedUniqueESSymbolType(checker.stringType!);

        Assert.Same(checker.stringType, result);
    }

    [Fact(Skip = "getWidenedTypeOfObjectLiteral / getRegularTypeOfObjectLiteral need a fully checked object " +
        "literal type; its construction reaches not-yet-landed binder/member-resolution stubs.")]
    public void GetRegularTypeOfObjectLiteral_WidensFreshObjectLiteral()
    {
        // Intentionally empty: covered end-to-end once the object-literal check pipeline fully lands.
    }
}
