// Single dedup surface for the deep-leaf callees of the name-resolution family (Checker.NameLookup.cs)
// that are NOT reached by ordinary identifier resolution and belong to unported subsystems. Each throws
// NotImplementedException so the trunk call sites compile; they run only once the owning wave lands the
// real body, at which point that wave DELETES the corresponding stub here.
//
// Trunk-reachability: onFailedToResolveSymbol's diagnostic-refinement helpers only fire when a name FAILS
// to resolve WITH a nameNotFoundMessage — not on the successful-resolution or message-less lookups the
// current pipeline exercises. onSuccessfullyResolvedSymbol's block-scoped-var / UMD / type-only-alias /
// isolated-modules branches likewise gate on rare flag combinations. The `const x: number = "hello"` e2e
// probe never reaches any of these — its name-resolution calls all resolve or early-return before them.
//
// **Do NOT implement whole missing subsystems here.** Every method is a single stub.
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified where referenced.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ---- spelling-suggestion machinery (checker.go:1731/1786; suggestion NameResolver pass) ----
    // checker.go:1731-1747 — the six primitive type-alias suggestion symbols, keyed by builtin name.
    // Go wraps the table in sync.OnceValue; the C# static readonly field initializer is the
    // thread-safe once analogue.
    private static readonly SymbolTable primitiveTypeAliasSuggestions = CreatePrimitiveTypeAliasSuggestions();

    private static SymbolTable CreatePrimitiveTypeAliasSuggestions()
    {
        var result = new SymbolTable(6);
        foreach (var (primitive, builtin) in new[]
        {
            ("string", "String"),
            ("number", "Number"),
            ("boolean", "Boolean"),
            ("object", "Object"),
            ("bigint", "BigInt"),
            ("symbol", "Symbol"),
        })
        {
            var sym = new Symbol
            {
                Flags = SymbolFlags.TypeAlias | SymbolFlags.Transient,
                Name = primitive,
            };
            result[builtin] = sym;
        }
        return result;
    }

    // checker.go:1749-1759 — Go yields lazily over the OnceValue map; the candidates feed
    // GetSpellingSuggestion, whose equal-distance ties are decided by the compare callback, so the
    // result is iteration-order independent.
    internal static System.Collections.Generic.IEnumerable<Symbol> GetPrimitiveTypeAliasSuggestions(SymbolTable? symbols)
    {
        if (symbols == null)
        {
            yield break;
        }
        // map-iter: 序无关
        foreach (var (builtinName, suggestion) in primitiveTypeAliasSuggestions)
        {
            if (symbols.ContainsKey(builtinName))
            {
                yield return suggestion;
            }
        }
    }

    // checker.go:1786
    internal Symbol? GetSpellingSuggestionForName(string name, System.Collections.Generic.IEnumerable<Symbol> symbols, SymbolFlags meaning)
    {
        string GetCandidateName(Symbol candidate)
        {
            var candidateName = AstUtilities.SymbolName(candidate);
            if (candidateName.Length == 0 || candidateName[0] == '"' || candidateName[0] == '\xFE')
            {
                return "";
            }
            if ((candidate.Flags & meaning) != 0)
            {
                return candidateName;
            }
            if ((candidate.Flags & SymbolFlags.Alias) != 0)
            {
                var alias = TryResolveAlias(candidate);
                if (alias != null && (alias.Flags & meaning) != 0)
                {
                    return candidateName;
                }
            }
            return "";
        }
        return CoreUtil.GetSpellingSuggestion(name, symbols, GetCandidateName, compareSymbols);
    }

    // ---- block-scoped-variable use-before-declaration (checker.go:1874) ----
    // checkResolvedBlockScopedVariable: reports "used before its declaration" for let/const/class/enum. Only
    // reached from onSuccessfullyResolvedSymbol's block-scoped branch (errorLocation present + variable/class/
    // CheckResolvedBlockScopedVariable is now REAL in Checker.NameLookup.cs (checker.go:1874).
}

// AST-layer predicates that Go places in internal/ast (utilities.go) but the Ts.Ast port has not yet landed.
// They are reached only on onSuccessfullyResolvedSymbol's type-only-alias / isolated-modules deep leaves.
// Stubbed here as Checker-local wrappers so those call sites compile; when the AST layer lands them, replace
// these with direct AstUtilities.X calls in Checker.NameLookup.cs.
public partial class Checker
{
    // Forwarder: real body landed in Ast/Utilities.Binder.cs (ast/utilities.go:3105).
    private static bool IsValidTypeOnlyAliasUseSiteCore(Node useSite) => AstUtilities.IsValidTypeOnlyAliasUseSite(useSite);
    // Forwarder: real body landed in Checker.SymbolResolution.cs (ast/utilities.go:2881).
    private static bool IsTypeOnlyImportDeclarationCore(Node node) => IsTypeOnlyImportDeclaration(node);
}
