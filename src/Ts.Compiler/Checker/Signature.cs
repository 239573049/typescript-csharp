// Ported from tsgo internal/checker/types.go:1276-1408 (Signature / TypePredicate / IndexInfo
// / IterationTypes). Arenas deleted (plan §一.5): newSignature/newIndexInfo just `new`.

using Ts.Ast;

namespace Ts.Checker;

// types.go:1276-1327
public sealed class Signature
{
    internal SignatureId id;
    internal SignatureFlags flags;
    internal int minArgumentCount; // Go int32
    internal int resolvedMinArgumentCount; // Go int32
    internal Node? declaration;
    internal Type[]? typeParameters;
    internal Symbol[]? parameters;
    internal Symbol? thisParameter;
    internal Type? resolvedReturnType;
    internal TypePredicate? resolvedTypePredicate;
    internal Signature? target;
    internal TypeMapper? mapper;
    internal Type? isolatedSignatureType;
    internal CompositeSignature? composite;

    public SignatureId Id() => id;

    public SignatureFlags Flags() => flags;

    public Type[]? TypeParameters() => typeParameters;

    public Node? Declaration() => declaration;

    public Signature? Target() => target;

    public Symbol? ThisParameter() => thisParameter;

    public Symbol[] Parameters() => parameters ?? [];

    public bool HasRestParameter() => (flags & SignatureFlags.HasRestParameter) != 0;

    public int MinArgumentCount() => minArgumentCount;
}

// types.go:1329-1332
public sealed class CompositeSignature
{
    internal bool isUnion; // True for union, false for intersection
    internal Signature[] signatures = []; // Individual signatures
}

// types.go:1343-1364
public sealed class TypePredicate
{
    internal TypePredicateKind kind;
    internal int parameterIndex; // Go int32
    internal string parameterName = "";
    internal Type? t; // field name `t` per Go; public accessor Type

    public Type? Type() => t;

    public TypePredicateKind Kind() => kind;

    public int ParameterIndex() => parameterIndex;

    public string ParameterName() => parameterName;
}

// types.go:1368-1391
public sealed class IndexInfo
{
    internal Type keyType = null!;
    internal Type valueType = null!;
    internal bool isReadonly;
    internal Node? declaration; // IndexSignatureDeclaration
    internal Symbol? indexSymbol; // Synthetic property symbol for this index signature
    internal Node[]? components; // ElementWithComputedPropertyName

    public Type KeyType() => keyType;

    public Type ValueType() => valueType;

    public bool IsReadonly() => isReadonly;

    public Node? Declaration() => declaration;
}

// checker.go:507-511 — value struct (plan/03 §6.2: keep as record struct, not class).
public readonly record struct IterationTypes(Type? YieldType, Type? ReturnType, Type? NextType);
