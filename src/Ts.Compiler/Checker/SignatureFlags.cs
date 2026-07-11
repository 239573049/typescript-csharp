// Ported from tsgo internal/checker/types.go:1252-1272 (SignatureFlags).

namespace Ts.Checker;

[System.Flags]
public enum SignatureFlags : uint
{
    None = 0,
    // Propagating flags
    HasRestParameter = 1 << 0, // Indicates last parameter is rest parameter
    HasLiteralTypes = 1 << 1, // Indicates signature is specialized
    Construct = 1 << 2, // Indicates signature is a construct signature
    Abstract = 1 << 3, // Indicates signature comes from an abstract class, abstract construct signature, or abstract constructor type
    // Non-propagating flags
    IsInnerCallChain = 1 << 4, // Indicates signature comes from a CallChain nested in an outer OptionalChain
    IsOuterCallChain = 1 << 5, // Indicates signature comes from a CallChain that is the outermost chain of an optional expression
    IsUntypedSignatureInJSFile = 1 << 6, // Indicates signature is from a js file and has no types
    IsNonInferrable = 1 << 7, // Indicates signature comes from a non-inferrable type
    IsSignatureCandidateForOverloadFailure = 1 << 8,

    PropagatingFlags = HasRestParameter | HasLiteralTypes | Construct | Abstract | IsUntypedSignatureInJSFile | IsSignatureCandidateForOverloadFailure,
    CallChainFlags = IsInnerCallChain | IsOuterCallChain,
}
