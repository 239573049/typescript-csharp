// Smoke tests for the phase-5 checker iteration-types wave (plan/03 §4.2 — the
// checkIteratedTypeOrElementType family, checker.go:6074-6713 → Checker.IterationTypes.cs).
//
// The end-to-end for-of / for-await-of / spread / yield* element-type paths route through
// cross-cluster helpers (getReducedType, getSignaturesOfType, getAwaitedType*, checkTypeAssignableTo,
// getIndexTypeOfType, the lazily-resolved global Iterable/Iterator/Generator types …) that either
// depend on lib.d.ts globals being loaded (they resolve to emptyGenericType without a lib) or are
// still NotImplementedException stubs owned by later waves. So this file exercises the wave's PURE,
// stub-free surface:
//   - IsES2015OrLaterIterable (checker.go:6707): the built-in typed-array / NodeList name classifier.
//   - The IterationTypes.HasTypes / GetType record helpers and the IterationTypesResolver
//     .GetResolvedIterationTypes projection (checker.go:6363-6397), carried faithfully from Go.
// The getElementTypeOfArrayType `number[]` → number path is left as a Skip because materialising a
// real `number[]` requires the global Array type, which resolves to emptyGenericType without a lib
// (it does NOT go through the iteration wave — GetElementTypeOfArrayType lives in Checker.TypeNodes.cs).

using Ts.Ast;
using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class IterationTypesTests
{
    [Theory]
    [InlineData("Float32Array")]
    [InlineData("Float64Array")]
    [InlineData("Int16Array")]
    [InlineData("Int32Array")]
    [InlineData("Int8Array")]
    [InlineData("NodeList")]
    [InlineData("Uint16Array")]
    [InlineData("Uint32Array")]
    [InlineData("Uint8Array")]
    [InlineData("Uint8ClampedArray")]
    public void IsES2015OrLaterIterable_ReturnsTrue_ForBuiltinIterableNames(string name)
    {
        Assert.True(Checker.IsES2015OrLaterIterable(name));
    }

    [Theory]
    [InlineData("Array")]
    [InlineData("string")]
    [InlineData("")]
    [InlineData("uint8array")] // case-sensitive: not the built-in name
    [InlineData("Map")]
    public void IsES2015OrLaterIterable_ReturnsFalse_ForOtherNames(string name)
    {
        Assert.False(Checker.IsES2015OrLaterIterable(name));
    }

    // Distinct real Type sentinels via string-literal interning (reference identity is all the
    // HasTypes / GetType / GetResolvedIterationTypes helpers observe here).
    private static Checker NewChecker()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);
        return checker;
    }

    [Fact]
    public void IterationTypes_HasTypes_TracksAnyNonNullMember()
    {
        var c = NewChecker();
        var t = new IterationTypes(null, null, null);
        Assert.False(t.HasTypes());

        var yieldT = c.GetStringLiteralType("y");
        var returnT = c.GetStringLiteralType("r");
        var nextT = c.GetStringLiteralType("n");

        Assert.True(new IterationTypes(yieldT, null, null).HasTypes());
        Assert.True(new IterationTypes(null, returnT, null).HasTypes());
        Assert.True(new IterationTypes(null, null, nextT).HasTypes());
    }

    [Fact]
    public void IterationTypes_GetType_SelectsTheRequestedMember()
    {
        var c = NewChecker();
        var yieldT = c.GetStringLiteralType("y");
        var returnT = c.GetStringLiteralType("r");
        var nextT = c.GetStringLiteralType("n");
        var t = new IterationTypes(yieldT, returnT, nextT);

        Assert.Same(yieldT, t.GetType(IterationTypeKind.Yield));
        Assert.Same(returnT, t.GetType(IterationTypeKind.Return));
        Assert.Same(nextT, t.GetType(IterationTypeKind.Next));
    }

    [Fact]
    public void GetResolvedIterationTypes_AppliesResolver_ToYieldAndReturn_ButNotNext()
    {
        var c = NewChecker();
        var yieldT = c.GetStringLiteralType("y");
        var returnT = c.GetStringLiteralType("r");
        var nextT = c.GetStringLiteralType("n");
        var replacement = c.GetStringLiteralType("replacement");

        // resolveIterationType maps yieldT -> replacement, everything else -> null (=> keep original).
        var resolver = new IterationTypesResolver
        {
            resolveIterationType = (input, _) => input == yieldT ? replacement : null!,
        };

        var result = resolver.GetResolvedIterationTypes(yieldT, returnT, nextT);

        Assert.Same(replacement, result.YieldType); // resolved
        Assert.Same(returnT, result.ReturnType);     // resolver returned null → OrElse keeps original
        Assert.Same(nextT, result.NextType);         // next is never resolved
    }

    [Fact(Skip = "getElementTypeOfArrayType(number[]) → number needs the global Array type, which " +
                 "resolves to emptyGenericType without a lib; not part of the iteration wave surface.")]
    public void GetElementTypeOfArrayType_NumberArray_ReturnsNumber()
    {
        // Intentionally empty: documents the deferred end-to-end assertion.
    }
}
