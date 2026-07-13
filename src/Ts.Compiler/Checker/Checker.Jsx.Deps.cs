// Single dedup surface for the jsx wave (plan/03 §4.2 jsx). The JSX family (Checker.Jsx.cs) calls a
// number of surrounding-subsystem helpers that are not yet landed in any wave: contextual-type
// fetchers, spread-type construction, iteration types, call resolution, external-module resolution,
// a couple of JSX-runtime AST helpers, and the JSX-alias-reference marker. Each is stubbed here with
// its faithful C# signature (Go signature preserved in the comment) so the assembly compiles and the
// intrinsic-tag / element / fragment / attributes trunk resolves for real where its dependencies
// already exist. The owning wave DELETES the corresponding stub when it lands the real body.
//
// Stubs already declared by other dedup surfaces are intentionally NOT redeclared here (CS0111):
//   GetContextualType (Stubs), GetResolvedSignature (Flow.Deps), ResolveSymbol (Stubs),
//   CheckExpressionForMutableLocation / CreateTypeReference / IsValidSpreadType (MembersSignatures.Deps),
//   GetRegularTypeOfObjectLiteral / GetTypeOfPropertyOrIndexSignatureOfType (Relater.Deps),
//   IsTypeAny (SymbolTypes), IsContextSensitive (TypeNodes.Stubs), RemoveMissingType (SymbolTypes),
//   NewDiagnosticForNode (TypeOps.Deps), GetSpellingSuggestionForName (NameLookup.Deps),
//   GetSymbolAtLocation (Statements.Deps), IsDeprecatedSymbol / AddDeprecatedSuggestion /
//   EntityNameToString / GetFirstIdentifier (SymbolResolution).
//
// §1.5: diagnostics via the fully-qualified `global::Ts.Diagnostics.Message` in signatures; no
// culture-sensitive APIs. Do NOT implement whole missing subsystems here.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // ---- contextual typing (contextual wave: checker.go:29xxx-30xxx) ----

    // CheckExpressionWithContextualType + GetContextNode are now REAL in Checker.Expressions.cs (checker.go:7461/7482).

    // getApparentTypeOfContextualType (30443), getTypeOfPropertyOfContextualType (30308),
    // findContextualNode (30643), getContextualTypeForArgumentAtIndex (29529): real bodies landed in
    // Checker.ContextualType.cs (call-resolution + contextual-type wave).

    // checker.go:30559
    internal bool IsPossiblyDiscriminantValue(Node node) => throw new NotImplementedException("isPossiblyDiscriminantValue");

    // ---- type mapping / spread (checker.go union/spread wave) ----

    // MapTypeEx is now REAL in Checker.Widening.cs (checker.go:25473).

    // checker.go:13319
    internal Type GetSpreadType(Type left, Type right, Symbol? symbol, ObjectFlags objectFlags, bool @readonly) => throw new NotImplementedException("getSpreadType");

    // checker.go:13462 — deferred; only reached from the object-literal spread path (getSpreadType wave).
    internal Type TryMergeUnionOfObjectTypeAndEmptyObject(Type t, bool @readonly) => throw new NotImplementedException("tryMergeUnionOfObjectTypeAndEmptyObject");

    // checker.go:13520 — pushContextualType(node, getContextualType(node, None), isCache: true).
    internal void PushCachedContextualType(Node node) => PushContextualType(node, GetContextualType(node, ContextFlags.None), true /*isCache*/);

    // checker.go:13303
    internal void CheckSpreadPropOverrides(Type t, SymbolTable props, Node spread) => throw new NotImplementedException("checkSpreadPropOverrides");

    // ---- signatures / first-parameter fallback (checker.go signature wave) ----

    // checker.go:28762 — getTypeOfFirstParameterOfSignatureWithFallback: real body landed in
    // Checker.AwaitedTypes.cs (#22 awaited-types wave).

    // checker.go:21017
    internal Signature[] GetUnionSignatures(Signature[][] signatureLists) => throw new NotImplementedException("getUnionSignatures");

    // ---- index symbol (checker.go:31839) ----

    // checker.go:31839
    internal Symbol? GetApplicableIndexSymbol(Type t, Type keyType) => throw new NotImplementedException("getApplicableIndexSymbol");

    // ---- iteration types (checker.go iteration wave) ----

    // checker.go:1088 — lazy global-type resolver `getGlobalIterableType func() *Type`.
    internal Type GetGlobalIterableType() => throw new NotImplementedException("getGlobalIterableType");

    // getIterationTypeOfIterable (6217): real body landed in Checker.IterationTypes.cs (iteration wave).

    // ---- inference context (checker.go:30722) ----

    // PushInferenceContext / PopInferenceContext / GetInferenceContext are now REAL in
    // Checker.ContextualType.cs (checker.go:30712-30729).

    // ---- call resolution (checker.go call wave: 8xxx-9xxx) ----
    // resolveCall (8819), resolveErrorCall (9899), resolveUntypedCall (9878), isUntypedFunctionCall
    // (9909): real bodies landed in Checker.CallResolution.cs / Checker.CallResolution.Applicability.cs.

    // checkDeprecatedSignature 8340: real body landed in Checker.Deprecation.cs.

    // ---- external module resolution (checker.go:15067) ----
    // resolveExternalModule: real body landed in Checker.SymbolResolution.cs (trunk; deep branches
    // remain `// !!!` stubs there).

    // ---- jsx alias reference marking (markJsxAliasReferenced is now REAL in Checker.AliasReferences.cs) ----

    // ---- JSX-runtime AST helpers (ast.GetPragmaFromSourceFile / GetSemanticJsxChildren) ----
    // Live in the transformer adapters today; the AST layer owns their permanent home. Stubbed here
    // (JSX-specific) so the pragma/children paths compile until the AST helper lands cleanly.

    // ast/utilities.go:2782 — ast.GetPragmaFromSourceFile: real body landed in
    // Ast/Utilities.Binder.cs (AstUtilities.GetPragmaFromSourceFile); call sites use it directly.

    // ast — ast.GetSemanticJsxChildren.
    internal static Node[] GetSemanticJsxChildren(Node[]? children) => throw new NotImplementedException("ast.GetSemanticJsxChildren");
}
