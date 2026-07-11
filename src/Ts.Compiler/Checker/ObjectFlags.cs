// Ported from tsgo internal/checker/types.go:581-641 (ObjectFlags).
// Some ObjectFlags reuse the same bit position with a meaning gated on a TypeFlags value
// (types.go:583-585, 617-641). Members keep Go's exact values; do NOT merge or renumber.

namespace Ts.Checker;

[System.Flags]
public enum ObjectFlags : uint
{
    None = 0,
    Class = 1 << 0, // Class
    Interface = 1 << 1, // Interface
    Reference = 1 << 2, // Generic type reference
    Tuple = 1 << 3, // Synthesized generic tuple type
    Anonymous = 1 << 4, // Anonymous
    Mapped = 1 << 5, // Mapped
    Instantiated = 1 << 6, // Instantiated anonymous or mapped type
    ObjectLiteral = 1 << 7, // Originates in an object literal
    EvolvingArray = 1 << 8, // Evolving array type
    ObjectLiteralPatternWithComputedProperties = 1 << 9, // Object literal pattern with computed properties
    ReverseMapped = 1 << 10, // Object contains a property from a reverse-mapped type
    JsxAttributes = 1 << 11, // Jsx attributes type
    JSLiteral = 1 << 12, // Object type declared in JS - disables errors on read/write of nonexisting members
    FreshLiteral = 1 << 13, // Fresh object literal
    ArrayLiteral = 1 << 14, // Originates in an array literal
    PrimitiveUnion = 1 << 15, // Union of only primitive types
    ContainsWideningType = 1 << 16, // Type is or contains undefined or null widening type
    ContainsObjectOrArrayLiteral = 1 << 17, // Type is or contains object literal type
    NonInferrableType = 1 << 18, // Type is or contains anyFunctionType or silentNeverType
    CouldContainTypeVariablesComputed = 1 << 19, // CouldContainTypeVariables flag has been computed
    CouldContainTypeVariables = 1 << 20, // Type could contain a type variable
    MembersResolved = 1 << 21, // Members have been resolved

    ClassOrInterface = Class | Interface,
    RequiresWidening = ContainsWideningType | ContainsObjectOrArrayLiteral,
    PropagatingFlags = ContainsWideningType | ContainsObjectOrArrayLiteral | NonInferrableType,
    InstantiatedMapped = Mapped | Instantiated,
    // Object flags that uniquely identify the kind of ObjectType
    ObjectTypeKindMask = ClassOrInterface | Reference | Tuple | Anonymous | Mapped | ReverseMapped | EvolvingArray | InstantiationExpressionType | SingleSignatureType,
    // Flags that require TypeFlags.Object
    ContainsSpread = 1 << 22, // Object literal contains spread operation
    ObjectRestType = 1 << 23, // Originates in object rest declaration
    InstantiationExpressionType = 1 << 24, // Originates in instantiation expression
    SingleSignatureType = 1 << 25, // A single signature type extracted from a potentially broader type
    IsClassInstanceClone = 1 << 26, // Type is a clone of a class instance type
    // Flags that require TypeFlags.Object and ObjectFlags.Reference
    IdenticalBaseTypeCalculated = 1u << 27, // has had `getSingleBaseForNonAugmentingSubtype` invoked on it already
    IdenticalBaseTypeExists = 1u << 28, // has a defined cachedEquivalentBaseType member
    UnresolvedMembers = 1u << 29, // Member resolution in process
    FromTypeNode = 1u << 30, // Originates in resolution of AST type node
    // Flags that require TypeFlags.UnionOrIntersection or TypeFlags.Substitution
    IsGenericTypeComputed = 1 << 22, // IsGenericObjectType flag has been computed
    IsGenericObjectType = 1 << 23, // Union or intersection contains generic object type
    IsGenericIndexType = 1 << 24, // Union or intersection contains generic index type
    IsGenericType = IsGenericObjectType | IsGenericIndexType,
    // Flags that require TypeFlags.Union
    ContainsIntersections = 1 << 25, // Union contains intersections
    IsUnknownLikeUnionComputed = 1 << 26, // IsUnknownLikeUnion flag has been computed
    IsUnknownLikeUnion = 1u << 27, // Union of null, undefined, and empty object type
    // Flags that require TypeFlags.Intersection
    IsNeverIntersectionComputed = 1 << 25, // IsNeverLike flag has been computed
    IsNeverIntersection = 1 << 26, // Intersection reduces to never
    IsConstrainedTypeVariable = 1u << 27, // T & C, where T's constraint and C are primitives, object, or {}
}
