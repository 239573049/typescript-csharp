using System;

namespace Ts.Ast;

// Go: internal/ast/checkflags.go — `type CheckFlags uint32`. Members drop the Go
// `CheckFlags` type-name prefix (plan/05-conventions.md §1.1 enum rule).
[Flags]
public enum CheckFlags : uint
{
    None = 0,
    Instantiated = 1 << 0, // Instantiated symbol
    SyntheticProperty = 1 << 1, // Property in union or intersection type
    SyntheticMethod = 1 << 2, // Method in union or intersection type
    Readonly = 1 << 3, // Readonly transient symbol
    ReadPartial = 1 << 4, // Synthetic property present in some but not all constituents
    WritePartial = 1 << 5, // Synthetic property present in some but only satisfied by an index signature in others
    HasNonUniformType = 1 << 6, // Synthetic property with non-uniform type in constituents
    HasLiteralType = 1 << 7, // Synthetic property with at least one literal type in constituents
    ContainsPublic = 1 << 8, // Synthetic property with public constituent(s)
    ContainsProtected = 1 << 9, // Synthetic property with protected constituent(s)
    ContainsPrivate = 1 << 10, // Synthetic property with private constituent(s)
    ContainsStatic = 1 << 11, // Synthetic property with static constituent(s)
    Late = 1 << 12, // Late-bound symbol for a computed property with a dynamic name
    ReverseMapped = 1 << 13, // Property of reverse-inferred homomorphic mapped type
    OptionalParameter = 1 << 14, // Optional parameter
    RestParameter = 1 << 15, // Rest parameter
    DeferredType = 1 << 16, // Calculation of the type of this symbol is deferred due to processing costs, should be fetched with `getTypeOfSymbolWithDeferredType`
    HasNeverType = 1 << 17, // Synthetic property with at least one never type in constituents
    Mapped = 1 << 18, // Property of mapped type
    StripOptional = 1 << 19, // Strip optionality in mapped property
    Unresolved = 1 << 20, // Unresolved type alias symbol
    IsDiscriminantComputed = 1 << 21, // IsDiscriminant flags has been computed
    IsDiscriminant = 1 << 22, // Discriminant property
    IndexSymbol = 1 << 23, // Synthetic property created from index signature
    Synthetic = SyntheticProperty | SyntheticMethod,
    NonUniformAndLiteral = HasNonUniformType | HasLiteralType,
    Partial = ReadPartial | WritePartial,
}
