// Ported from tsgo internal/checker/checker.go:100-261 (key structs) + 17289-17575 (CacheHashKey,
// keyBuilder, getXxxKey). plan/03 §4.2 #2, §6, §8. (Go splits these across the file head and tail;
// merged here into one keys file per the §4.2 exception registry.)
//
// Keys follow plan/03 §6.1: Go comparable structs → readonly record struct (compiler-synthesized
// Equals/GetHashCode, AOT-safe, zero boxing); reference members (Symbol/Type/Node/Signature/
// FlowNode) keep default reference equality == Go pointer comparison.

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Ts.Ast;

namespace Ts.Checker;

// checker.go:17313-17317 — type CacheHashKey xxh3.Uint128.
public readonly record struct CacheHashKey(UInt128 Value)
{
    public bool IsZero => Value == 0;
}

// checker.go:17319-17407 — streaming key builder over XXH3-128.
internal sealed class KeyBuilder
{
    private readonly XxHash128 h = new();

    // checker.go:17323-17325
    public CacheHashKey Hash() => new(h.GetCurrentHashAsUInt128());

    // checker.go:17327-17329
    public void WriteByte(byte c)
    {
        Span<byte> b = [c];
        h.Append(b);
    }

    // checker.go:17331-17333 — hashes UTF-8 bytes (plan/03 §8.3).
    public void WriteString(string s) => h.Append(Encoding.UTF8.GetBytes(s));

    // checker.go:17335-17337 → hashWrite64
    public void WriteInt(int value) => WriteUInt64LE((ulong)(long)value);

    // checker.go:17339-17341
    public void WriteSymbol(Symbol s) => WriteUInt64LE(AstUtilities.GetSymbolId(s).Value);

    // checker.go:17343-17345 → hashWrite32
    public void WriteType(Type t) => WriteUInt32LE(t.id.Value);

    // checker.go:17347-17352
    public void WriteTypes(Type[]? types)
    {
        types ??= [];
        WriteInt(types.Length);
        foreach (var t in types)
        {
            WriteType(t);
        }
    }

    // checker.go:17354-17362
    public void WriteAlias(TypeAlias? alias)
    {
        if (alias is not null)
        {
            WriteByte(1);
            WriteSymbol(alias.Symbol()!);
            WriteTypes(alias.TypeArguments());
        }
        else
        {
            WriteByte(0);
        }
    }

    // checker.go:17364-17397
    public bool WriteGenericTypeReferences(Type source, Type target, bool ignoreConstraints)
    {
        bool constrained = false;
        var typeParameters = new List<Type>(8);
        void WriteTypeReference(Type reference, int depth)
        {
            WriteType(reference.Target());
            foreach (var t in reference.AsTypeReference()!.resolvedTypeArguments ?? [])
            {
                if ((t.flags & TypeFlags.TypeParameter) != 0)
                {
                    if (ignoreConstraints || t.checker.GetConstraintOfTypeParameter(t) is null)
                    {
                        int index = typeParameters.IndexOf(t);
                        if (index < 0)
                        {
                            index = typeParameters.Count;
                            typeParameters.Add(t);
                        }
                        WriteByte((byte)'=');
                        WriteInt(index);
                        continue;
                    }
                    constrained = true;
                }
                else if (depth < 4 && Checker.IsTypeReferenceWithGenericArguments(t))
                {
                    WriteByte((byte)'<');
                    WriteTypeReference(t, depth + 1);
                    WriteByte((byte)'>');
                    continue;
                }
                WriteByte((byte)'-');
                WriteType(t);
            }
        }
        WriteTypeReference(source, 0);
        WriteByte((byte)',');
        WriteTypeReference(target, 0);
        return constrained;
    }

    // checker.go:17399-17401
    public void WriteNodeId(NodeId id) => WriteUInt64LE(id.Value);

    // checker.go:17403-17407 — writes nothing for a null node.
    public void WriteNode(Node? node)
    {
        if (node is not null)
        {
            WriteNodeId(AstUtilities.GetNodeId(node));
        }
    }

    // hashWrite32 for ~uint32 flag enums (AccessFlags/IntersectionState); see extensions below.
    public void WriteRawUInt32(uint v) => WriteUInt32LE(v);

    // checker.go:17289-17311 — hashWrite32/hashWrite64 (little-endian).
    private void WriteUInt32LE(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        h.Append(b);
    }

    private void WriteUInt64LE(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        h.Append(b);
    }
}

// checker.go:175-181 — special sentinel keys sharing cachedSignatures with real type-list keys
// (plan/03 §8.4). XXH3 one-shot == streaming for the same input.
internal static class SignatureKeys
{
    public static readonly CacheHashKey Erased = OneShot("-"); // checker.go:176
    public static readonly CacheHashKey Canonical = OneShot("*"); // 177
    public static readonly CacheHashKey Base = OneShot("#"); // 178
    public static readonly CacheHashKey Inner = OneShot("<"); // 179
    public static readonly CacheHashKey Outer = OneShot(">"); // 180

    private static CacheHashKey OneShot(string s) => new(XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(s)));
}

// === Key structs (checker.go:100-261) ===

// checker.go:102-105 — value is `any` (plan/03 §6.4: boxed key, EqualityComparer<object>).
internal readonly record struct EnumLiteralKey(Symbol EnumSymbol, object? Value);

// checker.go:109-112
internal readonly record struct EnumRelationKey(SymbolId SourceId, SymbolId TargetId);

// checker.go:145-148
internal readonly record struct CachedTypeKey(CachedTypeKind Kind, TypeId TypeId);

// checker.go:152-157
internal readonly record struct NarrowedTypeKey(Type T, Type Candidate, bool AssumeTrue, bool CheckDerived);

// checker.go:161-166
internal readonly record struct UnionOfUnionKey(TypeId Id1, TypeId Id2, UnionReduction R, CacheHashKey A);

// checker.go:170-173
internal readonly record struct CachedSignatureKey(Signature Sig, CacheHashKey Key);

// checker.go:185-188
internal readonly record struct StringMappingKey(Symbol S, Type T);

// checker.go:192-195
internal readonly record struct AssignmentReducedKey(TypeId Id1, TypeId Id2);

// checker.go:199-202
internal readonly record struct DiscriminatedContextualTypeKey(NodeId NodeId, TypeId TypeId);

// checker.go:206-209
internal readonly record struct InstantiationExpressionKey(NodeId NodeId, TypeId TypeId);

// checker.go:213-216
internal readonly record struct SubstitutionTypeKey(TypeId BaseId, TypeId ConstraintId);

// checker.go:220-224
internal readonly record struct ReverseMappedTypeKey(TypeId SourceId, TypeId TargetId, TypeId ConstraintId);

// checker.go:228-231
internal readonly record struct IterationTypesKey(TypeId TypeId, IterationUse Use);

// checker.go:235-240
internal readonly record struct PropertiesTypesKey(TypeId TypeId, TypeFlags Include, bool IncludeOrigin, bool UnresolvedMembers);

// checker.go:244-248
internal readonly record struct NonExistentPropertyKey(Node PropNode, Type ContainingType, bool IsUncheckedJS);

// checker.go:252-255
internal readonly record struct FlowLoopKey(FlowNode FlowNode, CacheHashKey RefKey);

// checker.go:257-260
internal struct FlowLoopInfo
{
    internal FlowLoopKey key;
    internal Type[]? types;
}

// symbolaccessibility.go:402 — symbolTableID (uint64, high 3 bits kind). Wrapper only for skeleton.
internal readonly record struct SymbolTableId(ulong Value);

// === Key constructors (checker.go:17409-17561) ===

public partial class Checker
{
    // checker.go:17409-17413
    internal static CacheHashKey GetTypeListKey(Type[]? types)
    {
        var b = new KeyBuilder();
        b.WriteTypes(types);
        return b.Hash();
    }

    // checker.go:17415-17419
    internal static CacheHashKey GetAliasKey(TypeAlias? alias)
    {
        var b = new KeyBuilder();
        b.WriteAlias(alias);
        return b.Hash();
    }

    // checker.go:17421-17443
    internal static CacheHashKey GetUnionKey(Type[] types, Type? origin, TypeAlias? alias)
    {
        var b = new KeyBuilder();
        if (origin is null)
        {
            b.WriteTypes(types);
        }
        else if ((origin.flags & TypeFlags.Union) != 0)
        {
            b.WriteByte((byte)'|');
            b.WriteTypes(origin.Types());
        }
        else if ((origin.flags & TypeFlags.Intersection) != 0)
        {
            b.WriteByte((byte)'&');
            b.WriteTypes(origin.Types());
        }
        else if ((origin.flags & TypeFlags.Index) != 0)
        {
            // origin type id alone is insufficient: `keyof x` may resolve to multiple WIP values.
            b.WriteByte((byte)'#');
            b.WriteType(origin);
            b.WriteByte((byte)'|');
            b.WriteTypes(types);
        }
        else
        {
            throw new InvalidOperationException("Unhandled case in getUnionKey");
        }
        b.WriteAlias(alias);
        return b.Hash();
    }

    // checker.go:17445-17454
    internal static CacheHashKey GetIntersectionKey(Type[] types, IntersectionFlags flags, TypeAlias? alias)
    {
        var b = new KeyBuilder();
        b.WriteTypes(types);
        if ((flags & IntersectionFlags.NoConstraintReduction) == 0)
        {
            b.WriteAlias(alias);
        }
        else
        {
            b.WriteByte((byte)'*');
        }
        return b.Hash();
    }

    // checker.go:17456-17477
    internal static CacheHashKey GetTupleKey(TupleElementInfo[] elementInfos, bool readonlyElements)
    {
        var b = new KeyBuilder();
        foreach (var e in elementInfos)
        {
            if ((e.flags & ElementFlags.Required) != 0)
            {
                b.WriteByte((byte)'#');
            }
            else if ((e.flags & ElementFlags.Optional) != 0)
            {
                b.WriteByte((byte)'?');
            }
            else if ((e.flags & ElementFlags.Rest) != 0)
            {
                b.WriteByte((byte)'.');
            }
            else
            {
                b.WriteByte((byte)'*');
            }
            if (e.labeledDeclaration is not null)
            {
                b.WriteNode(e.labeledDeclaration);
            }
        }
        if (readonlyElements)
        {
            b.WriteByte((byte)'!');
        }
        return b.Hash();
    }

    // checker.go:17479-17481
    internal static CacheHashKey GetTypeAliasInstantiationKey(Type[]? typeArguments, TypeAlias? alias) =>
        GetTypeInstantiationKey(typeArguments, alias, false);

    // checker.go:17483-17491
    internal static CacheHashKey GetTypeInstantiationKey(Type[]? typeArguments, TypeAlias? alias, bool singleSignature)
    {
        var b = new KeyBuilder();
        b.WriteTypes(typeArguments);
        b.WriteAlias(alias);
        if (singleSignature)
        {
            b.WriteByte((byte)'!');
        }
        return b.Hash();
    }

    // checker.go:17493-17500
    internal static CacheHashKey GetIndexedAccessKey(Type objectType, Type indexType, AccessFlags accessFlags, TypeAlias? alias)
    {
        var b = new KeyBuilder();
        b.WriteType(objectType);
        b.WriteType(indexType);
        b.WriteUInt32(accessFlags);
        b.WriteAlias(alias);
        return b.Hash();
    }

    // checker.go:17502-17514
    internal static CacheHashKey GetTemplateTypeKey(string[] texts, Type[] types)
    {
        var b = new KeyBuilder();
        b.WriteTypes(types);
        b.WriteByte((byte)'|');
        foreach (var s in texts)
        {
            b.WriteInt(Encoding.UTF8.GetByteCount(s));
        }
        b.WriteByte((byte)'|');
        foreach (var s in texts)
        {
            b.WriteString(s);
        }
        return b.Hash();
    }

    // checker.go:17516-17524
    internal static CacheHashKey GetConditionalTypeKey(Type[]? typeArguments, TypeAlias? alias, bool forConstraint)
    {
        var b = new KeyBuilder();
        b.WriteTypes(typeArguments);
        b.WriteAlias(alias);
        if (forConstraint)
        {
            b.WriteByte((byte)'!');
        }
        return b.Hash();
    }

    // checker.go:17526-17542 — returns (key, constrained).
    internal static (CacheHashKey Key, bool Constrained) GetRelationKey(Type source, Type target, IntersectionState intersectionState, bool isIdentity, bool ignoreConstraints)
    {
        if (isIdentity && source.id.Value > target.id.Value)
        {
            (source, target) = (target, source);
        }
        var b = new KeyBuilder();
        bool constrained = false;
        if (IsTypeReferenceWithGenericArguments(source) && IsTypeReferenceWithGenericArguments(target))
        {
            b.WriteByte((byte)'g');
            constrained = b.WriteGenericTypeReferences(source, target, ignoreConstraints);
        }
        else
        {
            b.WriteByte((byte)'s');
            b.WriteType(source);
            b.WriteType(target);
        }
        b.WriteUInt32(intersectionState);
        return (b.Hash(), constrained);
    }

    // checker.go:17544-17551
    internal static CacheHashKey GetNodeListKey(Node[] nodes)
    {
        var b = new KeyBuilder();
        b.WriteInt(nodes.Length);
        foreach (var n in nodes)
        {
            b.WriteNode(n);
        }
        return b.Hash();
    }

    // checker.go:17553-17557
    internal static bool IsTypeReferenceWithGenericArguments(Type t) =>
        IsNonDeferredTypeReference(t) && CoreUtilSome(t.checker.GetTypeArguments(t), a => (a.flags & TypeFlags.TypeParameter) != 0 || IsTypeReferenceWithGenericArguments(a));

    // checker.go:17559-17561
    internal static bool IsNonDeferredTypeReference(Type t) =>
        (t.objectFlags & ObjectFlags.Reference) != 0 && t.AsTypeReference()!.node is null;

    private static bool CoreUtilSome(Type[]? items, Func<Type, bool> predicate)
    {
        if (items is null)
        {
            return false;
        }
        foreach (var i in items)
        {
            if (predicate(i))
            {
                return true;
            }
        }
        return false;
    }
}

// AccessFlags/IntersectionState hashing helpers (hashWrite32 with ~uint32 constraint).
internal static class KeyBuilderExtensions
{
    public static void WriteUInt32(this KeyBuilder b, AccessFlags v) => b.WriteRawUInt32((uint)v);

    public static void WriteUInt32(this KeyBuilder b, IntersectionState v) => b.WriteRawUInt32((uint)v);
}
