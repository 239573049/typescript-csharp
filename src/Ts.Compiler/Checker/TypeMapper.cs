// Ported from tsgo internal/checker/mapper.go (plan/03 §11.4).
// Same header+data collapse as Type: TypeMapper{data} + TypeMapperBase → one abstract base
// whose virtuals carry the TypeMapperBase defaults. Factories return the concrete object
// directly (Go returned &m.TypeMapper, the embedded header — no observable difference).

using Ts.Core;

namespace Ts.Checker;

// mapper.go:19-93
public abstract class TypeMapper
{
    public virtual Type Map(Type t) => t; // TypeMapperBase.Map (mapper.go:91)

    public virtual TypeMapperKind Kind() => TypeMapperKind.Unknown; // mapper.go:92

    public virtual bool MapsThisOnly() => false; // mapper.go:93

    // Factory functions (mapper.go:40-73)

    // mapper.go:40-45
    public static TypeMapper NewTypeMapper(Type[] sources, Type[] targets) =>
        sources.Length == 1 ? NewSimpleTypeMapper(sources[0], targets[0]) : NewArrayTypeMapper(sources, targets);

    // mapper.go:54-59
    public static TypeMapper MergeTypeMappers(TypeMapper? m1, TypeMapper m2) =>
        m1 is not null ? NewMergedTypeMapper(m1, m2) : m2;

    // mapper.go:61-66
    public static TypeMapper PrependTypeMapping(Type source, Type target, TypeMapper? mapper) =>
        mapper is null ? NewSimpleTypeMapper(source, target) : NewMergedTypeMapper(NewSimpleTypeMapper(source, target), mapper);

    // mapper.go:68-73
    public static TypeMapper AppendTypeMapping(TypeMapper? mapper, Type source, Type target) =>
        mapper is null ? NewSimpleTypeMapper(source, target) : NewMergedTypeMapper(mapper, NewSimpleTypeMapper(source, target));

    public static TypeMapper NewSimpleTypeMapper(Type source, Type target) => new SimpleTypeMapper { source = source, target = target };

    public static TypeMapper NewArrayTypeMapper(Type[] sources, Type[] targets) => new ArrayTypeMapper { sources = sources, targets = targets };

    public static TypeMapper NewArrayToSingleTypeMapper(Type[] sources, Type target) => new ArrayToSingleTypeMapper { sources = sources, target = target };

    public static TypeMapper NewDeferredTypeMapper(Type[] sources, Func<Type>[] targets) => new DeferredTypeMapper { sources = sources, targets = targets };

    public static TypeMapper NewFunctionTypeMapper(Func<Type, Type> fn) => new FunctionTypeMapper { fn = fn };

    public static TypeMapper NewMergedTypeMapper(TypeMapper m1, TypeMapper m2) => new MergedTypeMapper { m1 = m1, m2 = m2 };
}

// mapper.go:97-124
public sealed class SimpleTypeMapper : TypeMapper
{
    internal Type source = null!;
    internal Type target = null!;

    public override Type Map(Type t) => t == source ? target : t;

    public override TypeMapperKind Kind() => TypeMapperKind.Simple;

    public override bool MapsThisOnly() => Checker.IsThisTypeParameter(source);
}

// mapper.go:128-157
public sealed class ArrayTypeMapper : TypeMapper
{
    internal Type[] sources = [];
    internal Type[] targets = [];

    public override Type Map(Type t)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (t == sources[i])
            {
                return targets[i];
            }
        }
        return t;
    }

    public override TypeMapperKind Kind() => TypeMapperKind.Array;

    public override bool MapsThisOnly() => sources.Length == 1 && Checker.IsThisTypeParameter(sources[0]);
}

// mapper.go:161-184
public sealed class ArrayToSingleTypeMapper : TypeMapper
{
    internal Type[] sources = [];
    internal Type target = null!;

    public override Type Map(Type t) => Array.IndexOf(sources, t) >= 0 ? target : t;

    public override bool MapsThisOnly() => sources.Length == 1 && Checker.IsThisTypeParameter(sources[0]);
}

// mapper.go:188-213
public sealed class DeferredTypeMapper : TypeMapper
{
    internal Type[] sources = [];
    internal Func<Type>[] targets = [];

    public override Type Map(Type t)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (t == sources[i])
            {
                return targets[i]();
            }
        }
        return t;
    }

    public override bool MapsThisOnly() => sources.Length == 1 && Checker.IsThisTypeParameter(sources[0]);
}

// mapper.go:217-231
public sealed class FunctionTypeMapper : TypeMapper
{
    internal Func<Type, Type> fn = null!;

    public override Type Map(Type t) => fn(t);
}

// mapper.go:235-255
public sealed class MergedTypeMapper : TypeMapper
{
    internal TypeMapper m1 = null!;
    internal TypeMapper m2 = null!;

    public override Type Map(Type t) => m2.Map(m1.Map(t));

    public override TypeMapperKind Kind() => TypeMapperKind.Merged;
}

// mapper.go:259-281
public sealed class CompositeTypeMapper : TypeMapper
{
    internal Checker c = null!;
    internal TypeMapper m1 = null!;
    internal TypeMapper m2 = null!;

    public override Type Map(Type t)
    {
        var t1 = m1.Map(t);
        if (t1 != t)
        {
            return c.InstantiateType(t1, m2);
        }
        return m2.Map(t);
    }
}

// mapper.go:285-315
public sealed class InferenceTypeMapper : TypeMapper
{
    internal Checker c = null!;
    internal InferenceContext n = null!;
    internal bool fixing;

    public override Type Map(Type t)
    {
        for (int i = 0; i < n.inferences.Length; i++)
        {
            var inference = n.inferences[i];
            if (t == inference.typeParameter)
            {
                if (fixing && !inference.isFixed)
                {
                    // Before we commit to a particular inference (and thus lock out further
                    // inferences), infer from any intra-expression inference sites collected.
                    c.InferFromIntraExpressionSites(n);
                    Checker.ClearCachedInferences(n.inferences);
                    inference.isFixed = true;
                }
                return c.GetInferredType(n, i);
            }
        }
        return t;
    }
}

// Checker-bound mapper factories (mapper.go:47-52, 77-83, 292-299).
public partial class Checker
{
    // mapper.go:47-52
    internal TypeMapper? CombineTypeMappers(TypeMapper? m1, TypeMapper? m2) =>
        m1 is not null ? new CompositeTypeMapper { c = this, m1 = m1, m2 = m2! } : m2;

    // mapper.go:77-83
    internal TypeMapper NewBackreferenceMapper(InferenceContext context, int index)
    {
        var forwardInferences = context.inferences[index..];
        var typeParameters = CoreUtil.Map(forwardInferences, i => i.typeParameter)!;
        return TypeMapper.NewArrayToSingleTypeMapper(typeParameters, unknownType!);
    }

    // mapper.go:292-299
    internal TypeMapper NewInferenceTypeMapper(InferenceContext n, bool fixing) =>
        new InferenceTypeMapper { c = this, n = n, fixing = fixing };
}
