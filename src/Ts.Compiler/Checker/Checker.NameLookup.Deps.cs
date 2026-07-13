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
    // ---- spelling-suggestion machinery (checker.go:1749/1786; suggestion NameResolver pass) ----
    // getPrimitiveTypeAliasSuggestions (checker.go:1749) — yields the six primitive type-alias symbols for
    // global spelling suggestions. Reached only from GetSuggestionForSymbolNameLookup (the suggestion
    // resolver's Lookup hook), which the NoLib pipeline does not drive. Deferred to the
    // identifier-resolution diagnostics wave.
    internal System.Collections.Generic.IEnumerable<Symbol> GetPrimitiveTypeAliasSuggestions(SymbolTable? symbols) => throw new NotImplementedException("getPrimitiveTypeAliasSuggestions");

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
    private static bool IsValidTypeOnlyAliasUseSiteCore(Node useSite) => throw new NotImplementedException("ast.IsValidTypeOnlyAliasUseSite");
    private static bool IsTypeOnlyImportDeclarationCore(Node node) => throw new NotImplementedException("ast.IsTypeOnlyImportDeclaration");
}
