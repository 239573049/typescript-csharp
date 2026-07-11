// Ported from tsgo internal/checker/types.go (Type hierarchy, §1-§2 of plan/03).
//
// plan/03 §1.1: the Go header+data+TypeBase intrusive three-layer collapses to a single
// abstract C# base class `Type`. The `TypeData` interface, `TypeBase` value-embed, the
// `AsType()` indirection and the per-type `data` pointer all disappear. `newType`
// (checker.go:24911) no longer takes a `data` argument — callers `new` the concrete class,
// then `newType` fills the common header (Checker.TypeCreation.cs).
//
// plan/03 §2: 17 panic hard-casts → `(X)this` (InvalidCastException == panic); 6 nil-tolerant
// embedded casts → `this as X`.

using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

// types.go:156-159 — TypeId/SignatureId are uint32. Bare-uint wrappers so `>` comparisons
// (getRelationKey) and record-struct key equality both work (plan/03 §1.1 latitude).
public readonly record struct TypeId(uint Value);

public readonly record struct SignatureId(uint Value);

// types.go:645-662
public sealed class TypeAlias
{
    internal Symbol? symbol;
    internal Type[]? typeArguments;
}

// types.go:650-662 — nil-receiver methods → null-tolerant extension methods (plan iron law 6).
public static class TypeAliasExtensions
{
    public static Symbol? Symbol(this TypeAlias? a) => a?.symbol;

    public static Type[]? TypeArguments(this TypeAlias? a) => a?.typeArguments;
}

// types.go:666-863 — collapsed Type base (was Type header + TypeData + TypeBase).
public abstract class Type
{
    internal TypeFlags flags; // types.go:667
    internal ObjectFlags objectFlags; // types.go:668
    internal TypeId id; // types.go:669
    internal Symbol? symbol; // types.go:670
    internal TypeAlias? alias; // types.go:671
    internal Checker checker = null!; // types.go:672 — back-pointer; types never cross checkers

    // Exported accessors (types.go:676-688)
    public TypeId Id => id;

    public TypeFlags Flags => flags;

    public ObjectFlags ObjectFlags => objectFlags;

    // Casts for concrete struct types (types.go:690-708) — 17 panic hard-casts.
    public IntrinsicType AsIntrinsicType() => (IntrinsicType)this;

    public LiteralType AsLiteralType() => (LiteralType)this;

    public UniqueESSymbolType AsUniqueESSymbolType() => (UniqueESSymbolType)this;

    public TupleType AsTupleType() => (TupleType)this;

    public InstantiationExpressionType AsInstantiationExpressionType() => (InstantiationExpressionType)this;

    public MappedType AsMappedType() => (MappedType)this;

    public ReverseMappedType AsReverseMappedType() => (ReverseMappedType)this;

    public EvolvingArrayType AsEvolvingArrayType() => (EvolvingArrayType)this;

    public TypeParameter AsTypeParameter() => (TypeParameter)this;

    public UnionType AsUnionType() => (UnionType)this;

    public IntersectionType AsIntersectionType() => (IntersectionType)this;

    public IndexType AsIndexType() => (IndexType)this;

    public IndexedAccessType AsIndexedAccessType() => (IndexedAccessType)this;

    public TemplateLiteralType AsTemplateLiteralType() => (TemplateLiteralType)this;

    public StringMappingType AsStringMappingType() => (StringMappingType)this;

    public SubstitutionType AsSubstitutionType() => (SubstitutionType)this;

    public ConditionalType AsConditionalType() => (ConditionalType)this;

    // Casts for embedded struct types (types.go:712-719) — 6 nil-tolerant casts.
    public ConstrainedType? AsConstrainedType() => this as ConstrainedType;

    public StructuredType? AsStructuredType() => this as StructuredType;

    public ObjectType? AsObjectType() => this as ObjectType;

    public TypeReference? AsTypeReference() => this as TypeReference;

    public InterfaceType? AsInterfaceType() => this as InterfaceType;

    public UnionOrIntersectionType? AsUnionOrIntersectionType() => this as UnionOrIntersectionType;

    // types.go:721-729
    public Type[] Distributed()
    {
        if ((flags & TypeFlags.Union) != 0)
        {
            return AsUnionType().types;
        }
        if ((flags & TypeFlags.Never) != 0)
        {
            return [];
        }
        return [this];
    }

    // types.go:733-747 — NB: the Mapped case (743) is unreachable, shadowed by the first
    // Object case (735). Preserved verbatim per plan/03 §1.3 (do not "fix").
    public Type Target()
    {
        if ((flags & TypeFlags.Object) != 0)
        {
            return AsObjectType()!.target!;
        }
        if ((flags & TypeFlags.TypeParameter) != 0)
        {
            return AsTypeParameter().target!;
        }
        if ((flags & TypeFlags.Index) != 0)
        {
            return AsIndexType().target;
        }
        if ((flags & TypeFlags.StringMapping) != 0)
        {
            return AsStringMappingType().target;
        }
        if ((flags & TypeFlags.Object) != 0 && (objectFlags & ObjectFlags.Mapped) != 0)
        {
            return AsMappedType().target!;
        }
        throw new InvalidOperationException("Unhandled case in Type.Target");
    }

    // types.go:749-759
    public TypeMapper? Mapper()
    {
        if ((flags & TypeFlags.Object) != 0)
        {
            return AsObjectType()!.mapper;
        }
        if ((flags & TypeFlags.TypeParameter) != 0)
        {
            return AsTypeParameter().mapper;
        }
        if ((flags & TypeFlags.Conditional) != 0)
        {
            return AsConditionalType().mapper;
        }
        throw new InvalidOperationException("Unhandled case in Type.Mapper");
    }

    // types.go:761-769
    public Type[] Types()
    {
        if ((flags & TypeFlags.UnionOrIntersection) != 0)
        {
            return AsUnionOrIntersectionType()!.types;
        }
        if ((flags & TypeFlags.TemplateLiteral) != 0)
        {
            return AsTemplateLiteralType().types;
        }
        throw new InvalidOperationException("Unhandled case in Type.Types");
    }

    // types.go:771-777
    public InterfaceType TargetInterfaceType() => AsTypeReference()!.target!.AsInterfaceType()!;

    public TupleType TargetTupleType() => AsTypeReference()!.target!.AsTupleType();

    // types.go:779-785
    public Symbol? Symbol => symbol;

    public TypeAlias? Alias => alias;

    // types.go:787-837 — Is* predicates.
    public bool IsUnion() => (flags & TypeFlags.Union) != 0;

    public bool IsString() => (flags & TypeFlags.String) != 0;

    public bool IsIntersection() => (flags & TypeFlags.Intersection) != 0;

    public bool IsStringLiteral() => (flags & TypeFlags.StringLiteral) != 0;

    public bool IsNumberLiteral() => (flags & TypeFlags.NumberLiteral) != 0;

    public bool IsBigIntLiteral() => (flags & TypeFlags.BigIntLiteral) != 0;

    public bool IsEnumLiteral() => (flags & TypeFlags.EnumLiteral) != 0;

    public bool IsBooleanLike() => (flags & TypeFlags.BooleanLike) != 0;

    public bool IsStringLike() => (flags & TypeFlags.StringLike) != 0;

    public bool IsClass() => (objectFlags & ObjectFlags.Class) != 0;

    public bool IsTypeParameter() => (flags & TypeFlags.TypeParameter) != 0;

    public bool IsIndex() => (flags & TypeFlags.Index) != 0;

    public bool IsTupleType() => Checker.IsTupleType(this); // free function (Checker.Stubs.cs)
}

// types.go:867-872
public sealed class IntrinsicType : Type
{
    internal string intrinsicName = "";

    public string IntrinsicName() => intrinsicName;
}

// types.go:876-897
public sealed class LiteralType : Type
{
    internal object? value; // string | jsnum.Number | bool | PseudoBigInt | null (computed enum)
    internal Type? freshType;
    internal Type? regularType;

    public object? Value() => value;

    public Type? FreshType() => freshType;

    public Type? RegularType() => regularType;

    public override string ToString() => value?.ToString() ?? "";
}

// types.go:901-904
public sealed class UniqueESSymbolType : Type
{
    internal string name = "";
}

// types.go:908-913 — abstract, never directly instantiated.
public abstract class ConstrainedType : Type
{
    internal Type? resolvedBaseConstraint;
}

// types.go:917-940
public abstract class StructuredType : ConstrainedType
{
    internal SymbolTable? members;
    internal Symbol[]? properties;
    internal Signature[]? signatures; // Signatures (call + construct)
    internal int callSignatureCount; // Count of call signatures
    internal IndexInfo[]? indexInfos;

    internal Type? objectTypeWithoutAbstractConstructSignatures;

    // types.go:930-936 — array-range slice views (plan/03 §1.2).
    public Signature[] CallSignatures() => signatures is null ? [] : signatures[..callSignatureCount];

    public Signature[] ConstructSignatures() => signatures is null ? [] : signatures[callSignatureCount..];

    public Symbol[] Properties() => properties ?? [];
}

// types.go:975-982 — non-abstract (ObjectFlagsAnonymous instantiated directly).
public class ObjectType : StructuredType
{
    internal Type? target; // Target of instantiated type
    internal TypeMapper? mapper; // Type mapper for instantiated type
    internal Dictionary<CacheHashKey, Type>? instantiations; // Map of type instantiations
}

// types.go:986-992 — non-abstract (ObjectFlagsReference instantiated directly).
public class TypeReference : ObjectType
{
    internal Node? node; // TypeReferenceNode | ArrayTypeNode | TupleTypeNode when deferred, else null
    internal Type[]? resolvedTypeArguments;
}

// types.go:996-1032 — non-abstract; a generic InterfaceType IS-A reference to its own
// instantiation (types.go:994). Inheritance order (InterfaceType : TypeReference) is
// load-bearing (plan/03 §1.2 row 9).
public class InterfaceType : TypeReference
{
    internal Type[]? allTypeParameters; // Type parameters (outer + local + thisType)
    internal int outerTypeParameterCount; // Count of outer type parameters
    internal Type? thisType; // The "this" type (null if none)
    internal bool baseTypesResolved;
    internal bool declaredMembersResolved;
    internal Type? resolvedBaseConstructorType;
    internal Type[]? resolvedBaseTypes;
    internal SymbolTable? declaredMembers;
    internal Signature[]? declaredCallSignatures;
    internal Signature[]? declaredConstructSignatures;
    internal IndexInfo[]? declaredIndexInfos;

    // types.go:1013-1031
    public Type[]? OuterTypeParameters() =>
        allTypeParameters is null || allTypeParameters.Length == 0 ? null : allTypeParameters[..outerTypeParameterCount];

    public Type[]? LocalTypeParameters() =>
        allTypeParameters is null || allTypeParameters.Length == 0 ? null : allTypeParameters[outerTypeParameterCount..^1];

    public Type[]? TypeParameters() =>
        allTypeParameters is null || allTypeParameters.Length == 0 ? null : allTypeParameters[..^1];
}

// types.go:1050-1056
public readonly struct TupleElementInfo
{
    internal readonly ElementFlags flags;
    internal readonly Node? labeledDeclaration; // NamedTupleMember | ParameterDeclaration | null

    public TupleElementInfo(ElementFlags flags, Node? labeledDeclaration)
    {
        this.flags = flags;
        this.labeledDeclaration = labeledDeclaration;
    }

    public ElementFlags TupleElementFlags() => flags;

    public Node? LabeledDeclaration() => labeledDeclaration;
}

// types.go:1058-1076
public sealed class TupleType : InterfaceType
{
    internal TupleElementInfo[]? elementInfos;
    internal int minLength; // Number of required or variadic elements
    internal int fixedLength; // Number of initial required or optional elements
    internal ElementFlags combinedFlags;
    internal bool readonlyElements; // Go field name `readonly`

    public int FixedLength() => fixedLength;

    public bool IsReadonly() => readonlyElements;

    public ElementFlags[] ElementFlags()
    {
        var infos = elementInfos ?? [];
        var elementFlags = new ElementFlags[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            elementFlags[i] = infos[i].flags;
        }
        return elementFlags;
    }

    public TupleElementInfo[] ElementInfos() => elementInfos ?? [];
}

// types.go:1080-1083
public sealed class InstantiationExpressionType : ObjectType
{
    internal Node? node;
}

// types.go:1087-1097
public sealed class MappedType : ObjectType
{
    internal MappedTypeNode? declaration;
    internal Type? typeParameter;
    internal Type? constraintType;
    internal Type? nameType;
    internal Type? templateType;
    internal Type? modifiersType;
    internal Type? resolvedApparentType;
    internal bool containsError;
}

// types.go:1101-1106
public sealed class ReverseMappedType : ObjectType
{
    internal Type? source;
    internal Type? mappedType;
    internal Type? constraintType;
}

// types.go:1110-1114
public sealed class EvolvingArrayType : ObjectType
{
    internal Type? elementType;
    internal Type? finalArrayType;
}

// types.go:1118-1130 — abstract.
public abstract class UnionOrIntersectionType : StructuredType
{
    internal Type[] types = [];
    internal SymbolTable? propertyCache;
    internal SymbolTable? propertyCacheWithoutFunctionPropertyAugment;
    internal Symbol[]? resolvedProperties;

    public new Type[] Types() => types;
}

// types.go:1134-1141
public sealed class UnionType : UnionOrIntersectionType
{
    internal Type? resolvedReducedType;
    internal Type? regularType;
    internal Type? origin; // Denormalized union, intersection, or index type in which union originates
    internal string keyPropertyName = ""; // Property with unique unit type in every constituent
    internal Dictionary<Type, Type>? constituentMap; // Constituents keyed by unit type discriminants (reference keys)
}

// types.go:1145-1149
public sealed class IntersectionType : UnionOrIntersectionType
{
    internal Type? resolvedApparentType;
    internal Type? uniqueLiteralFilledInstantiation; // Instantiation with type parameters mapped to never
}

// types.go:1153-1162
public sealed class TypeParameter : ConstrainedType
{
    internal Type? constraint;
    internal Type? target;
    internal TypeMapper? mapper;
    internal bool isThisType;
    internal Type? resolvedDefaultType;

    public bool IsThisType() => isThisType;
}

// types.go:1177-1183
public sealed class IndexType : ConstrainedType
{
    internal Type target = null!;
    internal IndexFlags indexFlags;

    public new Type Target() => target;
}

// types.go:1187-1195
public sealed class IndexedAccessType : ConstrainedType
{
    internal Type objectType = null!;
    internal Type indexType = null!;
    internal AccessFlags accessFlags; // Only includes AccessFlags.Persistent

    public Type ObjectType() => objectType;

    public Type IndexType() => indexType;
}

// types.go:1197-1204
public sealed class TemplateLiteralType : ConstrainedType
{
    internal string[] texts = []; // Always one element longer than types
    internal Type[] types = []; // Always at least one element

    public string[] Texts() => texts;

    public new Type[] Types() => types;
}

// types.go:1206-1211
public sealed class StringMappingType : ConstrainedType
{
    internal Type target = null!;

    public new Type Target() => target;
}

// types.go:1213-1220
public sealed class SubstitutionType : ConstrainedType
{
    internal Type baseType = null!; // Target type
    internal Type constraint = null!; // Constraint that target type is known to satisfy

    public Type BaseType() => baseType;

    public Type SubstConstraint() => constraint;
}

// types.go:1222-1231
public sealed class ConditionalRoot
{
    internal ConditionalTypeNode? node;
    internal Type? checkType;
    internal Type? extendsType;
    internal bool isDistributive;
    internal Type[]? inferTypeParameters;
    internal Type[]? outerTypeParameters;
    internal Dictionary<CacheHashKey, Type>? instantiations;
    internal TypeAlias? alias;
}

// types.go:1233-1248
public sealed class ConditionalType : ConstrainedType
{
    internal ConditionalRoot root = null!;
    internal Type? checkType;
    internal Type? extendsType;
    internal Type? resolvedTrueType;
    internal Type? resolvedFalseType;
    internal Type? resolvedInferredTrueType;
    internal Type? resolvedDefaultConstraint;
    internal Type? resolvedConstraintOfDistributive;
    internal TypeMapper? mapper;
    internal TypeMapper? combinedMapper;

    public Type? CheckType() => checkType;

    public Type? ExtendsType() => extendsType;
}

// types.go:1410
public delegate Ternary TypeComparer(Type s, Type t, bool reportErrors);

// types.go:1412-1456
public sealed class LanguageFeatureMinimumTargetMap
{
    public ScriptTarget Exponentiation;
    public ScriptTarget AsyncFunctions;
    public ScriptTarget ForAwaitOf;
    public ScriptTarget AsyncGenerators;
    public ScriptTarget AsyncIteration;
    public ScriptTarget ObjectSpreadRest;
    public ScriptTarget RegularExpressionFlagsDotAll;
    public ScriptTarget BindinglessCatch;
    public ScriptTarget BigInt;
    public ScriptTarget NullishCoalesce;
    public ScriptTarget OptionalChaining;
    public ScriptTarget LogicalAssignment;
    public ScriptTarget TopLevelAwait;
    public ScriptTarget ClassFields;
    public ScriptTarget PrivateNamesAndClassStaticBlocks;
    public ScriptTarget RegularExpressionFlagsHasIndices;
    public ScriptTarget ShebangComments;
    public ScriptTarget UsingAndAwaitUsing;
    public ScriptTarget ClassAndClassElementDecorators;
    public ScriptTarget RegularExpressionFlagsUnicodeSets;
}

public static class LanguageFeatureMinimum
{
    // types.go:1435-1456
    public static readonly LanguageFeatureMinimumTargetMap LanguageFeatureMinimumTarget = new()
    {
        Exponentiation = ScriptTarget.ES2016,
        AsyncFunctions = ScriptTarget.ES2017,
        ForAwaitOf = ScriptTarget.ES2018,
        AsyncGenerators = ScriptTarget.ES2018,
        AsyncIteration = ScriptTarget.ES2018,
        ObjectSpreadRest = ScriptTarget.ES2018,
        RegularExpressionFlagsDotAll = ScriptTarget.ES2018,
        BindinglessCatch = ScriptTarget.ES2019,
        BigInt = ScriptTarget.ES2020,
        NullishCoalesce = ScriptTarget.ES2020,
        OptionalChaining = ScriptTarget.ES2020,
        LogicalAssignment = ScriptTarget.ES2021,
        TopLevelAwait = ScriptTarget.ES2022,
        ClassFields = ScriptTarget.ES2022,
        PrivateNamesAndClassStaticBlocks = ScriptTarget.ES2022,
        RegularExpressionFlagsHasIndices = ScriptTarget.ES2022,
        ShebangComments = ScriptTarget.ESNext,
        UsingAndAwaitUsing = ScriptTarget.ESNext,
        ClassAndClassElementDecorators = ScriptTarget.ESNext,
        RegularExpressionFlagsUnicodeSets = ScriptTarget.ESNext,
    };
}
