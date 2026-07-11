// Tests for plan/03 §4.2 #14 (Instantiation) cluster — the instantiateType family and its
// load-bearing depth guards (checker.go:22016: instantiationDepth==100 / instantiationCount>=5,000,000).
//
// These exercise the genuinely reachable surface: the TypeParameter worker case (m.Map), the top
// guard, the active-mapper stack, the instantiation counters, and identity preservation — all real
// ported code that does not bottom out in another cluster's stub. The depth/count guards reach the
// `error(...)` reporter, which is still a NotImplementedException stub (#9 name-lookup wave), so the
// guard's "excessively deep" branch is observed by the exception it raises there.

using System;
using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class InstantiationTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    [Fact]
    public void InstantiateType_TypeParameter_SubstitutesViaMapper()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var target = c.NewTypeParameter(null);
        var mapper = TypeMapper.NewSimpleTypeMapper(tp, target);

        // Worker's TypeParameter case returns m.Map(tp) == target.
        Assert.Same(target, c.InstantiateType(tp, mapper));
    }

    [Fact]
    public void InstantiateType_NullMapper_ReturnsInputUnchanged()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);

        // Top guard (checker.go:22013): m == nil short-circuits to the input type.
        Assert.Same(tp, c.InstantiateType(tp, null));
    }

    [Fact]
    public void InstantiateType_PushesAndPopsActiveMapper()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var target = c.NewTypeParameter(null);
        var mapper = TypeMapper.NewSimpleTypeMapper(tp, target);

        Assert.Empty(c.activeMappers);
        c.InstantiateType(tp, mapper);
        // pushActiveMapper then popActiveMapper leaves the stack empty again.
        Assert.Empty(c.activeMappers);
    }

    [Fact]
    public void InstantiateType_BelowLimit_IncrementsCountsAndRestoresDepth()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var target = c.NewTypeParameter(null);
        var mapper = TypeMapper.NewSimpleTypeMapper(tp, target);

        c.instantiationDepth = 99;
        var beforeCount = c.instantiationCount;
        var beforeTotal = c.TotalInstantiationCount;

        var result = c.InstantiateType(tp, mapper);

        Assert.Same(target, result);
        Assert.Equal(99u, c.instantiationDepth);          // incremented to 100 during worker, then restored
        Assert.Equal(beforeCount + 1, c.instantiationCount);
        Assert.Equal(beforeTotal + 1, c.TotalInstantiationCount);
    }

    [Fact]
    public void InstantiateType_AtDepthLimit_TriggersExcessivelyDeepGuard()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var target = c.NewTypeParameter(null);
        var mapper = TypeMapper.NewSimpleTypeMapper(tp, target);

        // instantiationDepth == 100 (checker.go:22016) takes the guard branch, which calls
        // error(...) — still a stub that throws. Reaching it confirms the guard fired.
        c.instantiationDepth = 100;
        Assert.Throws<NotImplementedException>(() => c.InstantiateType(tp, mapper));
    }

    [Fact]
    public void InstantiateType_AtCountLimit_TriggersExcessivelyDeepGuard()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var target = c.NewTypeParameter(null);
        var mapper = TypeMapper.NewSimpleTypeMapper(tp, target);

        // instantiationCount >= 5,000,000 (checker.go:22016) takes the same guard branch.
        c.instantiationCount = 5_000_000;
        Assert.Throws<NotImplementedException>(() => c.InstantiateType(tp, mapper));
    }

    [Fact]
    public void InstantiateTypes_NoChange_ReturnsSameArrayReference()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var other = c.NewTypeParameter(null);
        // Mapper touches only tp; `other` maps to itself, so the list is returned by identity
        // (instantiateList / SameMap semantics — plan/05 §6.2).
        var mapper = TypeMapper.NewSimpleTypeMapper(tp, c.NewTypeParameter(null));
        var input = new[] { other };

        Assert.Same(input, c.InstantiateTypes(input, mapper));
    }
}
