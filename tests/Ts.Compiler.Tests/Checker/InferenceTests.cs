// Tests for the inference cluster (plan/03 §4.2 — inference.go → Checker.Inference.cs): the
// inferTypes/inferFromTypes core, candidate accumulation, getInferredType/getInferredTypes, the
// inference-context mapper, and cloneInferenceInfo.
//
// These exercise the genuinely reachable surface — inference directly to a naked type variable
// (source → T), which does not bottom out in another cluster's stub. The task's object-property
// (`{a:string}` → `{a:T}`) and array-element cases route through inferFromObjectTypes, which calls
// getUnmatchedProperty / getVariances — both still sibling-wave stubs (relater / variance). So the
// reachable equivalent, `string` → `T`, is asserted here; the object/array paths are validated once
// those waves land. getInferredType uses the signature==null path (getTypeFromInference), avoiding
// the covariant/contravariant machinery that reaches getWidenedLiteralType (a #11 stub).

using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class InferenceTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    // ===== inferTypes core: candidate accumulation =====

    [Fact]
    public void InferTypes_StringToNakedTypeVariable_RecordsStringCandidate()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        c.InferTypes(ctx.inferences, c.stringType!, tp, InferencePriority.None, false);

        var inference = ctx.inferences[0];
        Assert.Same(tp, inference.typeParameter);
        Assert.NotNull(inference.candidates);
        Assert.Single(inference.candidates!);
        Assert.Same(c.stringType, inference.candidates![0]);
        Assert.Equal(new[] { 0 }, inference.candidateDepths);
        Assert.Equal(InferencePriority.None, inference.priority);
        Assert.True(inference.topLevel);
        Assert.False(inference.isFixed);
    }

    [Fact]
    public void InferTypes_TargetWithoutTypeVariable_MakesNoInference()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        // number contains no type variables, so couldContainTypeVariables short-circuits inferFromTypes.
        c.InferTypes(ctx.inferences, c.stringType!, c.numberType!, InferencePriority.None, false);

        Assert.Null(ctx.inferences[0].candidates);
        Assert.Null(ctx.inferences[0].contraCandidates);
    }

    [Fact]
    public void InferTypes_TwoSourcesSamePriority_AccumulatesBothCandidates()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        c.InferTypes(ctx.inferences, c.stringType!, tp, InferencePriority.None, false);
        c.InferTypes(ctx.inferences, c.numberType!, tp, InferencePriority.None, false);

        var candidates = ctx.inferences[0].candidates!;
        Assert.Equal(2, candidates.Length);
        Assert.Contains(c.stringType!, candidates);
        Assert.Contains(c.numberType!, candidates);
    }

    // ===== getInferredType (signature == null path → getTypeFromInference) =====

    [Fact]
    public void GetInferredType_SingleCandidate_ReturnsThatType()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        c.InferTypes(ctx.inferences, c.stringType!, tp, InferencePriority.None, false);

        // source={x: string}, target={x: T} reduces to inferring string for T; here directly string → T.
        Assert.Same(c.stringType, c.GetInferredType(ctx, 0));
    }

    [Fact]
    public void GetInferredType_TwoCandidates_ReturnsUnionOfCandidates()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        c.InferTypes(ctx.inferences, c.stringType!, tp, InferencePriority.None, false);
        c.InferTypes(ctx.inferences, c.numberType!, tp, InferencePriority.None, false);

        // getTypeFromInference unions the candidates: string | number.
        var inferred = c.GetInferredType(ctx, 0);
        Assert.True(inferred.IsUnion(), "two disjoint candidates should union");
        Assert.Contains(c.stringType!, inferred.Types());
        Assert.Contains(c.numberType!, inferred.Types());
    }

    [Fact]
    public void GetInferredType_NoCandidates_DefaultsToUnknown()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        // No inference made and no default type → unknown (the empty-object fallback for unconstrained tp).
        Assert.Same(c.unknownType, c.GetInferredType(ctx, 0));
    }

    [Fact]
    public void GetInferredType_NoCandidates_AnyDefaultFlag_ReturnsAny()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.AnyDefault, null);

        Assert.Same(c.anyType, c.GetInferredType(ctx, 0));
    }

    [Fact]
    public void GetInferredTypes_MultipleParams_InfersEachIndependently()
    {
        var c = NewChecker();
        var t = c.NewTypeParameter(null);
        var u = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([t, u], null, InferenceFlags.None, null);

        // Infer only into T; U gets no candidate → unknown.
        c.InferTypes(ctx.inferences, c.stringType!, t, InferencePriority.None, false);

        var result = c.GetInferredTypes(ctx);
        Assert.Equal(2, result.Length);
        Assert.Same(c.stringType, result[0]);
        Assert.Same(c.unknownType, result[1]);
    }

    // ===== inference-context mapper (fixing) =====

    [Fact]
    public void ContextMapper_FixesInferenceAndSubstitutesInferredType()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);

        c.InferTypes(ctx.inferences, c.stringType!, tp, InferencePriority.None, false);

        // The (fixing) mapper commits the inference and maps T to its inferred type.
        Assert.Same(c.stringType, ctx.mapper!.Map(tp));
        Assert.True(ctx.inferences[0].isFixed, "fixing mapper should mark the inference fixed");
        // A non-type-parameter maps to itself.
        Assert.Same(c.numberType, ctx.mapper!.Map(c.numberType!));
    }

    // ===== cloneInferenceInfo =====

    [Fact]
    public void CloneInferenceInfo_ProducesIndependentCandidateArrays()
    {
        var c = NewChecker();
        var tp = c.NewTypeParameter(null);
        var ctx = c.NewInferenceContext([tp], null, InferenceFlags.None, null);
        c.InferTypes(ctx.inferences, c.stringType!, tp, InferencePriority.None, false);

        var clone = Checker.CloneInferenceInfo(ctx.inferences[0]);

        Assert.Same(tp, clone.typeParameter);
        Assert.NotNull(clone.candidates);
        Assert.Same(c.stringType, clone.candidates![0]);
        // Independent backing array (slices.Clone semantics).
        Assert.NotSame(ctx.inferences[0].candidates, clone.candidates);
    }
}
