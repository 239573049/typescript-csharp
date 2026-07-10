using System;

namespace Ts.Ast;

// Go: internal/ast/modifierflags.go — `type ModifierFlags uint32`. Members drop the
// Go `ModifierFlags` type-name prefix (plan/05-conventions.md §1.1 enum rule).
[Flags]
public enum ModifierFlags : uint
{
    None = 0,
    // Syntactic/JSDoc modifiers
    Public = 1 << 0, // Property/Method
    Private = 1 << 1, // Property/Method
    Protected = 1 << 2, // Property/Method
    Readonly = 1 << 3, // Property/Method
    Override = 1 << 4, // Override method
    // Syntactic-only modifiers
    Export = 1 << 5, // Declarations
    Abstract = 1 << 6, // Class/Method/ConstructSignature
    Ambient = 1 << 7, // Declarations (declare keyword)
    Static = 1 << 8, // Property/Method
    Accessor = 1 << 9, // Property
    Async = 1 << 10, // Property/Method/Function
    Default = 1 << 11, // Function/Class (export default declaration)
    Const = 1 << 12, // Const enum
    In = 1 << 13, // Contravariance modifier
    Out = 1 << 14, // Covariance modifier
    Decorator = 1 << 15, // Contains a decorator
    // JSDoc-only modifiers
    Deprecated = 1 << 16, // Deprecated tag
    // Cache-only JSDoc-modifiers. Should match order of Syntactic/JSDoc modifiers, above.
    JSDocPublic = 1 << 23, // if this value changes, `selectEffectiveModifierFlags` must change accordingly
    JSDocPrivate = 1 << 24,
    JSDocProtected = 1 << 25,
    JSDocReadonly = 1 << 26,
    JSDocOverride = 1 << 27,
    HasComputedJSDocModifiers = 1 << 28, // Indicates the computed modifier flags include modifiers from JSDoc.
    HasComputedFlags = 1 << 29, // Modifier flags have been computed

    SyntacticOrJSDocModifiers = Public | Private | Protected | Readonly | Override,
    SyntacticOnlyModifiers = Export | Ambient | Abstract | Static | Accessor | Async | Default | Const | In | Out | Decorator,
    SyntacticModifiers = SyntacticOrJSDocModifiers | SyntacticOnlyModifiers,
    JSDocCacheOnlyModifiers = JSDocPublic | JSDocPrivate | JSDocProtected | JSDocReadonly | JSDocOverride,
    JSDocOnlyModifiers = Deprecated,
    NonCacheOnlyModifiers = SyntacticOrJSDocModifiers | SyntacticOnlyModifiers | JSDocOnlyModifiers,

    AccessibilityModifier = Public | Private | Protected,
    // Accessibility modifiers and 'readonly' can be attached to a parameter in a constructor to make it a property.
    ParameterPropertyModifier = AccessibilityModifier | Readonly | Override,
    NonPublicAccessibilityModifier = Private | Protected,

    TypeScriptModifier = Ambient | Public | Private | Protected | Readonly | Abstract | Const | Override | In | Out,
    ExportDefault = Export | Default,
    All = Export | Ambient | Public | Private | Protected | Static | Readonly | Abstract | Accessor | Async | Default | Const | Deprecated | Override | In | Out | Decorator,
    Modifier = All & ~Decorator,
    JavaScript = Export | Static | Accessor | Async | Default,
}
