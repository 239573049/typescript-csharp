// Ported from tsgo internal/checker/types.go:413-554 (TypeFlags + FormatTypeFlags/String).
// Bit positions and composite masks are behavior-bearing: the numeric ordering of
// TypeFlags determines CompareTypes ordering and union constituent order (types.go:415-419).
// Do not renumber or reorder.

using System.Numerics;

namespace Ts.Checker;

[System.Flags]
public enum TypeFlags : uint
{
    None = 0,
    Any = 1 << 0,
    Unknown = 1 << 1,
    Undefined = 1 << 2,
    Null = 1 << 3,
    Void = 1 << 4,
    String = 1 << 5,
    Number = 1 << 6,
    BigInt = 1 << 7,
    Boolean = 1 << 8,
    ESSymbol = 1 << 9, // Type of symbol primitive introduced in ES6
    StringLiteral = 1 << 10,
    NumberLiteral = 1 << 11,
    BigIntLiteral = 1 << 12,
    BooleanLiteral = 1 << 13,
    UniqueESSymbol = 1 << 14, // unique symbol
    EnumLiteral = 1 << 15, // Always combined with StringLiteral, NumberLiteral, or Union
    Enum = 1 << 16, // Numeric computed enum member value (must be right after EnumLiteral, see getSortOrderFlags)
    NonPrimitive = 1 << 17, // intrinsic object type
    Never = 1 << 18, // Never type
    TypeParameter = 1 << 19, // Type parameter
    Object = 1 << 20, // Object type
    Index = 1 << 21, // keyof T
    TemplateLiteral = 1 << 22, // Template literal type
    StringMapping = 1 << 23, // Uppercase/Lowercase type
    Substitution = 1 << 24, // Type parameter substitution
    IndexedAccess = 1 << 25, // T[K]
    Conditional = 1 << 26, // T extends U ? X : Y
    Union = 1 << 27, // Union (T | U)
    Intersection = 1 << 28, // Intersection (T & U)
    Reserved1 = 1u << 29, // Used by union/intersection type construction
    Reserved2 = 1u << 30, // Used by union/intersection type construction
    Reserved3 = 1u << 31,

    AnyOrUnknown = Any | Unknown,
    Nullable = Undefined | Null,
    Literal = StringLiteral | NumberLiteral | BigIntLiteral | BooleanLiteral,
    Unit = Enum | Literal | UniqueESSymbol | Nullable,
    Freshable = Enum | Literal,
    StringOrNumberLiteral = StringLiteral | NumberLiteral,
    StringOrNumberLiteralOrUnique = StringLiteral | NumberLiteral | UniqueESSymbol,
    DefinitelyFalsy = StringLiteral | NumberLiteral | BigIntLiteral | BooleanLiteral | Void | Undefined | Null,
    PossiblyFalsy = DefinitelyFalsy | String | Number | BigInt | Boolean,
    Intrinsic = Any | Unknown | String | Number | BigInt | ESSymbol | Void | Undefined | Null | Never | NonPrimitive,
    StringLike = String | StringLiteral | TemplateLiteral | StringMapping,
    NumberLike = Number | NumberLiteral | Enum,
    BigIntLike = BigInt | BigIntLiteral,
    BooleanLike = Boolean | BooleanLiteral,
    EnumLike = Enum | EnumLiteral,
    ESSymbolLike = ESSymbol | UniqueESSymbol,
    VoidLike = Void | Undefined,
    Primitive = StringLike | NumberLike | BigIntLike | BooleanLike | EnumLike | ESSymbolLike | VoidLike | Null,
    DefinitelyNonNullable = StringLike | NumberLike | BigIntLike | BooleanLike | EnumLike | ESSymbolLike | Object | NonPrimitive,
    DisjointDomains = NonPrimitive | StringLike | NumberLike | BigIntLike | BooleanLike | ESSymbolLike | VoidLike | Null,
    UnionOrIntersection = Union | Intersection,
    StructuredType = Object | Union | Intersection,
    TypeVariable = TypeParameter | IndexedAccess,
    InstantiableNonPrimitive = TypeVariable | Conditional | Substitution,
    InstantiablePrimitive = Index | TemplateLiteral | StringMapping,
    Instantiable = InstantiableNonPrimitive | InstantiablePrimitive,
    StructuredOrInstantiable = StructuredType | Instantiable,
    ObjectFlagsType = Any | Nullable | Never | Object | Union | Intersection,
    Simplifiable = IndexedAccess | Conditional | Index,
    Singleton = Any | Unknown | String | Number | Boolean | BigInt | ESSymbol | Void | Undefined | Null | Never | NonPrimitive,
    // 'Narrowable' types are types where narrowing actually narrows.
    Narrowable = Any | Unknown | StructuredOrInstantiable | StringLike | NumberLike | BigIntLike | BooleanLike | ESSymbol | UniqueESSymbol | NonPrimitive,
    // The following flags are aggregated during union and intersection type construction
    IncludesMask = Any | Unknown | Primitive | Never | Object | Union | Intersection | NonPrimitive | TemplateLiteral | StringMapping,
    // The following flags are used for different purposes during union and intersection type construction
    IncludesMissingType = TypeParameter,
    IncludesNonWideningType = Index,
    IncludesWildcard = IndexedAccess,
    IncludesEmptyObject = Conditional,
    IncludesInstantiable = Substitution,
    IncludesConstrainedTypeVariable = Reserved1,
    IncludesError = Reserved2,
    NotPrimitiveUnion = Any | Unknown | Void | Never | Object | Intersection | IncludesInstantiable,
}

// types.go:502-554 — flag-name table + FormatTypeFlags/String.
public static class TypeFlagsExtensions
{
    private static readonly (TypeFlags Flag, string Name)[] TypeFlagNames =
    [
        (TypeFlags.Any, "Any"),
        (TypeFlags.Unknown, "Unknown"),
        (TypeFlags.Undefined, "Undefined"),
        (TypeFlags.Null, "Null"),
        (TypeFlags.Void, "Void"),
        (TypeFlags.String, "String"),
        (TypeFlags.Number, "Number"),
        (TypeFlags.BigInt, "BigInt"),
        (TypeFlags.Boolean, "Boolean"),
        (TypeFlags.ESSymbol, "ESSymbol"),
        (TypeFlags.StringLiteral, "StringLiteral"),
        (TypeFlags.NumberLiteral, "NumberLiteral"),
        (TypeFlags.BigIntLiteral, "BigIntLiteral"),
        (TypeFlags.BooleanLiteral, "BooleanLiteral"),
        (TypeFlags.UniqueESSymbol, "UniqueESSymbol"),
        (TypeFlags.EnumLiteral, "EnumLiteral"),
        (TypeFlags.Enum, "Enum"),
        (TypeFlags.NonPrimitive, "NonPrimitive"),
        (TypeFlags.Never, "Never"),
        (TypeFlags.TypeParameter, "TypeParameter"),
        (TypeFlags.Object, "Object"),
        (TypeFlags.Index, "Index"),
        (TypeFlags.TemplateLiteral, "TemplateLiteral"),
        (TypeFlags.StringMapping, "StringMapping"),
        (TypeFlags.Substitution, "Substitution"),
        (TypeFlags.IndexedAccess, "IndexedAccess"),
        (TypeFlags.Conditional, "Conditional"),
        (TypeFlags.Union, "Union"),
        (TypeFlags.Intersection, "Intersection"),
    ];

    // FormatTypeFlags returns the individual flag names (types.go:538-549).
    public static string[] FormatTypeFlags(TypeFlags flags)
    {
        var result = new List<string>(BitOperations.PopCount((uint)flags));
        foreach (var fn in TypeFlagNames)
        {
            if ((flags & fn.Flag) != 0)
            {
                result.Add(fn.Name);
            }
        }
        if (result.Count == 0)
        {
            result.Add("None");
        }
        return result.ToArray();
    }

    // types.go:552-554
    public static string ToPipeString(this TypeFlags f) => string.Join("|", FormatTypeFlags(f));
}
