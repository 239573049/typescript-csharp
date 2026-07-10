// Ported from tsgo internal/core/languagevariant.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

namespace Ts.Core;

// Go: type LanguageVariant int32. Member mapping (05 §1.1 enum prefix drop,
// §1.4 three-letter acronyms Pascal-cased): LanguageVariantStandard→Standard,
// LanguageVariantJSX→Jsx.
public enum LanguageVariant
{
    Standard = 0,
    Jsx = 1,
}
