// Tests for the type-operations wave (plan/03 §4.2 #17/#18/#19): getUnionType reduction,
// getIntersectionType, getBaseConstraintOfType. Exercises the reachable primitive-type paths
// (no cross-cluster machinery): dedup/never-elision/any-absorption for unions & intersections,
// the boolean-literal→Boolean-flag interning, and the base-constraint stack over primitives/unions.

using Ts.Ast;
using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class TypeOpsTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    // ===== getUnionType reduction (#17) =====

    [Fact]
    public void GetUnionType_StringNumber_ProducesSortedTwoConstituentUnion()
    {
        var c = NewChecker();
        var u = c.GetUnionType([c.stringType!, c.numberType!]);

        Assert.True(u.IsUnion(), "string | number should be a union type");
        var members = u.Types();
        Assert.Equal(2, members.Length);
        Assert.Contains(c.stringType!, members);
        Assert.Contains(c.numberType!, members);
    }

    [Fact]
    public void GetUnionType_DeduplicatesIdenticalConstituents()
    {
        var c = NewChecker();
        // string | string collapses to string (single constituent after dedup).
        Assert.Same(c.stringType, c.GetUnionType([c.stringType!, c.stringType!]));
    }

    [Fact]
    public void GetUnionType_ElidesNever()
    {
        var c = NewChecker();
        // never is ignored inside unions.
        Assert.Same(c.stringType, c.GetUnionType([c.stringType!, c.neverType!]));
    }

    [Fact]
    public void GetUnionType_EmptyIsNever_SingletonIsIdentity()
    {
        var c = NewChecker();
        Assert.Same(c.neverType, c.GetUnionType([]));
        Assert.Same(c.stringType, c.GetUnionType([c.stringType!]));
    }

    [Fact]
    public void GetUnionType_AbsorbsAny()
    {
        var c = NewChecker();
        // any | T reduces to any (AnyOrUnknown short-circuit in getUnionTypeWorker).
        Assert.Same(c.anyType, c.GetUnionType([c.anyType!, c.stringType!]));
    }

    [Fact]
    public void GetUnionType_AbsorbsUnknown()
    {
        var c = NewChecker();
        // unknown | T reduces to unknown.
        Assert.Same(c.unknownType, c.GetUnionType([c.unknownType!, c.stringType!]));
    }

    [Fact]
    public void BooleanType_IsFalseTrueUnionCarryingBooleanFlag()
    {
        var c = NewChecker();
        // booleanType = getUnionType([regularFalse, regularTrue]); a union with the Boolean flag
        // (getUnionTypeFromSortedList, checker.go:25657-25659).
        Assert.True(c.booleanType!.IsUnion(), "boolean should be a union of false | true");
        Assert.True((c.booleanType.Flags & TypeFlags.Boolean) != 0, "boolean union must carry the Boolean flag");
        Assert.Same(c.booleanType, c.GetUnionType([c.regularFalseType!, c.regularTrueType!]));
    }

    // ===== getIntersectionType (#17) =====

    [Fact]
    public void GetIntersectionType_DeduplicatesIdenticalConstituents()
    {
        var c = NewChecker();
        Assert.Same(c.stringType, c.GetIntersectionType([c.stringType!, c.stringType!]));
    }

    [Fact]
    public void GetIntersectionType_WithNever_IsNever()
    {
        var c = NewChecker();
        Assert.Same(c.neverType, c.GetIntersectionType([c.stringType!, c.neverType!]));
    }

    [Fact]
    public void GetIntersectionType_Empty_IsUnknown()
    {
        var c = NewChecker();
        // The empty intersection is unknown (the top type).
        Assert.Same(c.unknownType, c.GetIntersectionType([]));
    }

    [Fact]
    public void GetIntersectionType_AbsorbsAny()
    {
        var c = NewChecker();
        // any & T reduces to any.
        Assert.Same(c.anyType, c.GetIntersectionType([c.anyType!, c.stringType!]));
    }

    // ===== getBaseConstraintOfType (#19) =====

    [Fact]
    public void GetBaseConstraintOfType_PrimitiveHasNoConstraint()
    {
        var c = NewChecker();
        // Primitives are not instantiable/union/etc., so getBaseConstraintOfType returns null.
        Assert.Null(c.GetBaseConstraintOfType(c.stringType!));
        Assert.Null(c.GetBaseConstraintOfType(c.numberType!));
    }

    [Fact]
    public void GetBaseConstraintOfType_UnionOfConstraintlessConstituents_IsTheUnionItself()
    {
        var c = NewChecker();
        // For string | number (neither constituent has a base constraint), the union's base
        // constraint resolves to the union itself (computeBaseConstraint's UnionOrIntersection case).
        var u = c.GetUnionType([c.stringType!, c.numberType!]);
        Assert.Same(u, c.GetBaseConstraintOfType(u));
    }
}
