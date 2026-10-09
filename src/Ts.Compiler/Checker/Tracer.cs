// Ported from tsgo internal/checker/tracer.go — the per-checker --generateTrace tracer.
//
// Holds the tracing session plus its own type recorder, stamps `checkerId` onto every
// event so the trace view can attribute work to a checker thread, and adapts checker
// Types to the tracing package's ITracedType surface.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Checker;

// tracer.go:13-18
public sealed class Tracer
{
    // tracer.go:14-16
    private readonly global::Ts.Tracing.Tracing tracing;
    private readonly global::Ts.Tracing.ITracer recorder;
    private readonly int checkerIndex;

    // tracer.go:20-22
    public static Tracer NewTracer(global::Ts.Tracing.Tracing tracing, int checkerIndex) =>
        new(tracing, checkerIndex);

    private Tracer(global::Ts.Tracing.Tracing tracing, int checkerIndex)
    {
        this.tracing = tracing;
        recorder = tracing.NewTypeTracer(checkerIndex);
        this.checkerIndex = checkerIndex;
    }

    // tracer.go:24-26
    public void RecordType(Type typ)
    {
        recorder.RecordType(TracedTypeAdapter.WrapType(typ)!);
    }

    // tracer.go:28-41
    public Action Push(global::Ts.Tracing.Phase phase, string name, Dictionary<string, object?>? args, bool separateBeginAndEnd)
    {
        if (!separateBeginAndEnd)
        {
            return global::Ts.Tracing.TracingExtensions.Push(tracing, phase, name, CopyWithCheckerIndex(args), separateBeginAndEnd);
        }

        var (taggedArgs, restore) = TemporarilyAddCheckerIndex(args);
        var pop = global::Ts.Tracing.TracingExtensions.Push(tracing, phase, name, taggedArgs, true);
        restore();

        return () =>
        {
            var (_, restoreEndArgs) = TemporarilyAddCheckerIndex(taggedArgs);
            try
            {
                pop();
            }
            finally
            {
                restoreEndArgs();
            }
        };
    }

    // tracer.go:43-45
    public void Instant(global::Ts.Tracing.Phase phase, string name, Dictionary<string, object?>? args) =>
        global::Ts.Tracing.TracingExtensions.Instant(tracing, phase, name, CopyWithCheckerIndex(args));

    // tracer.go:47-52
    private Dictionary<string, object?> CopyWithCheckerIndex(Dictionary<string, object?>? args)
    {
        var withCheckerIndex = new Dictionary<string, object?>(args?.Count + 1 ?? 1);
        if (args is not null)
        {
            // map-iter: 序无关 —— copying the caller's key/value pairs; order irrelevant.
            foreach (var kv in args)
            {
                withCheckerIndex[kv.Key] = kv.Value;
            }
        }
        withCheckerIndex["checkerId"] = checkerIndex;
        return withCheckerIndex;
    }

    // tracer.go:54-68
    private (Dictionary<string, object?> args, Action restore) TemporarilyAddCheckerIndex(
        Dictionary<string, object?>? args)
    {
        args ??= new Dictionary<string, object?>();

        bool hadPrevious = args.TryGetValue("checkerId", out object? previous);
        args["checkerId"] = checkerIndex;

        var restoreArgs = args;
        var restorePrevious = previous;
        bool had = hadPrevious;
        return (restoreArgs, () =>
        {
            if (had)
            {
                restoreArgs["checkerId"] = restorePrevious;
            }
            else
            {
                restoreArgs.Remove("checkerId");
            }
        });
    }
}

// tracer.go:70-260 — tracedTypeAdapter adapts a checker Type to the tracing ITracedType surface.
public sealed class TracedTypeAdapter : global::Ts.Tracing.ITracedType
{
    private readonly Type t;
    private readonly Checker? checker;

    internal TracedTypeAdapter(Type t, Checker? checker)
    {
        this.t = t;
        this.checker = checker;
    }

    // tracer.go:76-78
    public uint Id => t.Id.Value;

    // tracer.go:80-82
    public string[]? FormatFlags() => TypeFlagsExtensions.FormatTypeFlags(t.flags);

    // tracer.go:84-86
    public bool IsConditional => (t.flags & TypeFlags.Conditional) != 0;

    // tracer.go:88-90
    public Symbol? Symbol => t.symbol;

    // tracer.go:92-97
    public Symbol? AliasSymbol => t.alias?.Symbol();

    // tracer.go:99-104
    public global::Ts.Tracing.ITracedType[]? AliasTypeArguments() =>
        t.alias is null ? null : WrapTypes(t.alias.TypeArguments());

    // tracer.go:106-112
    public string IntrinsicName =>
        (t.flags & TypeFlags.Intrinsic) == 0 ? "" : t.AsIntrinsicType().IntrinsicName();

    // tracer.go:114-119
    public global::Ts.Tracing.ITracedType[]? UnionTypes() =>
        (t.flags & TypeFlags.Union) == 0 ? null : WrapTypes(t.AsUnionType().types);

    // tracer.go:121-126
    public global::Ts.Tracing.ITracedType[]? IntersectionTypes() =>
        (t.flags & TypeFlags.Intersection) == 0 ? null : WrapTypes(t.AsIntersectionType().types);

    // tracer.go:128-137
    public global::Ts.Tracing.ITracedType? IndexType()
    {
        if ((t.flags & TypeFlags.Index) == 0)
        {
            return null;
        }
        Type target = t.AsIndexType().Target();
        return WrapType(target);
    }

    // tracer.go:139-148
    public global::Ts.Tracing.ITracedType? IndexedAccessObjectType()
    {
        if ((t.flags & TypeFlags.IndexedAccess) == 0)
        {
            return null;
        }
        return WrapType(t.AsIndexedAccessType().objectType);
    }

    // tracer.go:150-159
    public global::Ts.Tracing.ITracedType? IndexedAccessIndexType()
    {
        if ((t.flags & TypeFlags.IndexedAccess) == 0)
        {
            return null;
        }
        return WrapType(t.AsIndexedAccessType().indexType);
    }

    // tracer.go:161-170
    public global::Ts.Tracing.ITracedType? ConditionalCheckType()
    {
        if ((t.flags & TypeFlags.Conditional) == 0)
        {
            return null;
        }
        return WrapType(t.AsConditionalType().checkType);
    }

    // tracer.go:172-181
    public global::Ts.Tracing.ITracedType? ConditionalExtendsType()
    {
        if ((t.flags & TypeFlags.Conditional) == 0)
        {
            return null;
        }
        return WrapType(t.AsConditionalType().extendsType);
    }

    // tracer.go:183-192
    public global::Ts.Tracing.ITracedType? ConditionalTrueType()
    {
        if ((t.flags & TypeFlags.Conditional) == 0)
        {
            return null;
        }
        return WrapType(t.AsConditionalType().resolvedTrueType);
    }

    // tracer.go:194-203
    public global::Ts.Tracing.ITracedType? ConditionalFalseType()
    {
        if ((t.flags & TypeFlags.Conditional) == 0)
        {
            return null;
        }
        return WrapType(t.AsConditionalType().resolvedFalseType);
    }

    // tracer.go:205-214
    public global::Ts.Tracing.ITracedType? SubstitutionBaseType()
    {
        if ((t.flags & TypeFlags.Substitution) == 0)
        {
            return null;
        }
        return WrapType(t.AsSubstitutionType().baseType);
    }

    // tracer.go:216-225
    public global::Ts.Tracing.ITracedType? SubstitutionConstraintType()
    {
        if ((t.flags & TypeFlags.Substitution) == 0)
        {
            return null;
        }
        return WrapType(t.AsSubstitutionType().constraint);
    }

    // tracer.go:227-236
    public global::Ts.Tracing.ITracedType? ReferenceTarget()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.Reference) == 0)
        {
            return null;
        }
        return WrapType(t.AsTypeReference()!.target);
    }

    // tracer.go:238-243
    public global::Ts.Tracing.ITracedType[]? ReferenceTypeArguments()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.Reference) == 0)
        {
            return null;
        }
        return WrapTypes(t.AsTypeReference()!.resolvedTypeArguments);
    }

    // tracer.go:245-250
    public Node? ReferenceNode
    {
        get
        {
            if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.Reference) == 0)
            {
                return null;
            }
            return t.AsTypeReference()!.node;
        }
    }

    // tracer.go:252-261
    public global::Ts.Tracing.ITracedType? ReverseMappedSourceType()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.ReverseMapped) == 0)
        {
            return null;
        }
        return WrapType(t.AsReverseMappedType().source);
    }

    // tracer.go:263-272
    public global::Ts.Tracing.ITracedType? ReverseMappedMappedType()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.ReverseMapped) == 0)
        {
            return null;
        }
        return WrapType(t.AsReverseMappedType().mappedType);
    }

    // tracer.go:274-283
    public global::Ts.Tracing.ITracedType? ReverseMappedConstraintType()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.ReverseMapped) == 0)
        {
            return null;
        }
        return WrapType(t.AsReverseMappedType().constraintType);
    }

    // tracer.go:285-294
    public global::Ts.Tracing.ITracedType? EvolvingArrayElementType()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.EvolvingArray) == 0)
        {
            return null;
        }
        return WrapType(t.AsEvolvingArrayType().elementType);
    }

    // tracer.go:296-305
    public global::Ts.Tracing.ITracedType? EvolvingArrayFinalType()
    {
        if ((t.flags & TypeFlags.Object) == 0 || (t.objectFlags & ObjectFlags.EvolvingArray) == 0)
        {
            return null;
        }
        return WrapType(t.AsEvolvingArrayType().finalArrayType);
    }

    // tracer.go:307-309
    public bool IsTuple => (t.objectFlags & ObjectFlags.Tuple) != 0;

    // tracer.go:311-316
    public Node? Pattern => checker?.patternForType.GetValueOrDefault(t);

    // tracer.go:318-320
    public object? RecursionIdentity() => Checker.GetRecursionIdentity(t).Value;

    // tracer.go:322-341
    public string Display()
    {
        // Compute display text for types where it's valuable for trace analysis.
        // TypeScript only does this for Anonymous|Literal types, but we extend to
        // unions, intersections, and template literals since they often lack
        // firstDeclaration and the display text helps identify them.
        // Incomplete types during tracing can cause panics, which we intentionally
        // suppress (returning ""), matching TypeScript's try/catch around typeToString.
        if (checker is null)
        {
            return "";
        }
        if ((t.objectFlags & ObjectFlags.Anonymous) != 0 ||
            (t.flags & (TypeFlags.Literal | TypeFlags.TemplateLiteral | TypeFlags.Union | TypeFlags.Intersection)) != 0)
        {
            try
            {
                return checker.TypeToString(t);
            }
            catch (Exception)
            {
                return ""; // Go: defer recover() — suppressed
            }
        }
        return "";
    }

    // tracer.go:343-348
    internal static global::Ts.Tracing.ITracedType? WrapType(Type? t) =>
        t is null ? null : new TracedTypeAdapter(t, t.checker);

    // tracer.go:350-361
    internal static global::Ts.Tracing.ITracedType[]? WrapTypes(Type[]? types)
    {
        if (types is null || types.Length == 0)
        {
            return null;
        }
        var result = new global::Ts.Tracing.ITracedType[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            result[i] = new TracedTypeAdapter(types[i], types[i].checker);
        }
        return result;
    }
}
