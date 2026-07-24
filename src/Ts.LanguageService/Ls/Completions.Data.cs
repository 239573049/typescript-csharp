// Ported from tsgo internal/ls/completions.go — the completionData payload family
// (completions.go:88-134) and the symbol-chain/property helpers the getCompletionData
// accumulators use (completions.go:2820-2876).

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Ls;

// completions.go:88-89 — completionData is a union:
// CompletionDataData | CompletionDataKeyword | CompletionDataJSDocTagName | CompletionDataJSDocTag
// | CompletionDataJSDocParameterName. C#: a marker interface (Go `any`).
internal interface ICompletionData
{
}

// completions.go:96-124 — completionDataData.
internal sealed class CompletionDataData : ICompletionData
{
    internal Symbol[]? symbols;
    // PORT-NOTE: Go []*autoimport.FixAndExport — the autoimport package lands as its own unit;
    // until then the auto-import arms are deferred and this list stays empty.
    internal List<object>? autoImports;
    internal CompletionKind completionKind;
    internal bool isInSnippetScope;
    // Note that the presence of this alone doesn't mean that we need a conversion. Only do that if the completion is not an ordinary identifier.
    internal Node? propertyAccessToConvert;
    internal bool isNewIdentifierLocation;
    internal Node? location;
    internal KeywordCompletionFilters keywordFilters;
    internal object?[]? literals; // string | jsnum.Number | PseudoBigInt
    internal Dictionary<int, SymbolOriginInfo>? symbolToOriginInfoMap; // keys are indexes of `symbols`
    internal Dictionary<SymbolId, string>? symbolToSortTextMap;
    internal Symbol? recommendedCompletion;
    internal Node? previousToken;
    internal Node? contextToken;
    internal JsxInitializerInfo jsxInitializer;
    internal bool insideJSDocTagTypeExpression;
    internal bool isTypeOnlyLocation;
    // In JSX tag name and attribute names, identifiers like "my-tag" or "aria-name" is valid identifier.
    internal bool isJsxIdentifierExpected;
    internal bool isRightOfOpenTag;
    internal bool isRightOfDotOrQuestionDot;
    internal ImportStatementCompletionInfo? importStatementCompletion; // !!!
    internal bool hasUnresolvedAutoImports; // !!!
    internal string[]? defaultCommitCharacters;
}

// completions.go:126 — completionDataKeyword is CompletionDataKeyword (Completions.Keywords.cs);
// mark it as a completionData member here.

// completions.go:136-139
internal sealed class CompletionDataJSDocTagName : ICompletionData
{
}

internal sealed class CompletionDataJSDocTag : ICompletionData
{
}

// completions.go:141-143
internal sealed class CompletionDataJSDocParameterName : ICompletionData
{
    internal Node? tag; // *ast.JSDocParameterOrPropertyTag
}

// completions.go:145-149 — jsxInitializer: if we're after the `=` sign but no identifier has been
// typed yet, isInitializer will be true but initializer will be null.
internal struct JsxInitializerInfo
{
    internal bool isInitializer;
    internal Node? initializer; // IdentifierNode
}

public sealed partial class LanguageService
{
    // completions.go:2820-2827 — getPropertiesForCompletion.
    internal static Symbol[] GetPropertiesForCompletion(global::Ts.Checker.Type t, global::Ts.Checker.Checker typeChecker)
    {
        if ((t.Flags & global::Ts.Checker.TypeFlags.Union) != 0)
        {
            return typeChecker.GetAllPossiblePropertiesOfTypes(t.Types());
        }
        return typeChecker.GetApparentProperties(t);
    }

    // completions.go:2829-2837 — getLeftMostName: given 'a.b.c', returns 'a'.
    internal static Node? GetLeftMostName(Node? e)
    {
        if (e == null)
        {
            return null;
        }
        if (NodeTests.IsIdentifier(e))
        {
            return e;
        }
        if (NodeTests.IsPropertyAccessExpression(e))
        {
            return GetLeftMostName(e.Expression());
        }
        return null;
    }

    // completions.go:2839-2856 — getFirstSymbolInChain.
    internal static Symbol? GetFirstSymbolInChain(Symbol symbol, Node? enclosingDeclaration, global::Ts.Checker.Checker typeChecker)
    {
        var chain = typeChecker.GetAccessibleSymbolChain(
            symbol,
            enclosingDeclaration,
            SymbolFlags.All /*meaning*/,
            false /*useOnlyExternalAliasing*/);
        if (chain is { Count: > 0 })
        {
            return chain[0];
        }
        if (symbol.Parent != null)
        {
            if (IsModuleSymbol(symbol.Parent))
            {
                return symbol;
            }
            return GetFirstSymbolInChain(symbol.Parent, enclosingDeclaration, typeChecker);
        }
        return null;
    }

    // completions.go:2858-2860 — isModuleSymbol.
    internal static bool IsModuleSymbol(Symbol symbol)
    {
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (decl.Kind == SyntaxKind.SourceFile)
            {
                return true;
            }
        }
        return false;
    }

    // completions.go:2862-2867 — getNullableSymbolOriginInfoKind.
    internal static SymbolOriginInfoKind GetNullableSymbolOriginInfoKind(SymbolOriginInfoKind kind, bool insertQuestionDot)
    {
        if (insertQuestionDot)
        {
            kind |= SymbolOriginInfoKind.Nullable;
        }
        return kind;
    }

    // completions.go:2869-2873 — isStaticProperty.
    internal static bool IsStaticProperty(Symbol symbol)
    {
        return symbol.ValueDeclaration != null &&
            (symbol.ValueDeclaration.ModifierFlags() & ModifierFlags.Static) != 0 &&
            AstUtilities.IsClassLike(symbol.ValueDeclaration.Parent!);
    }

    // completions.go:2798-2807 — symbolCanBeReferencedAtTypeLocation: true if symbol is a type or a
    // module containing at least one type.
    internal static bool SymbolCanBeReferencedAtTypeLocation(Symbol symbol, global::Ts.Checker.Checker typeChecker, HashSet<SymbolId> seenModules)
    {
        // Since an alias can be merged with a local declaration, we need to test both the alias and its target.
        // This code used to just test the result of `skipAlias`, but that would ignore any locally introduced meanings.
        return NonAliasCanBeReferencedAtTypeLocation(symbol, typeChecker, seenModules) ||
            NonAliasCanBeReferencedAtTypeLocation(
                global::Ts.Checker.Checker.SkipAlias(symbol.ExportSymbol ?? symbol, typeChecker)!,
                typeChecker,
                seenModules);
    }

    // completions.go:2809-2817 — nonAliasCanBeReferencedAtTypeLocation.
    internal static bool NonAliasCanBeReferencedAtTypeLocation(Symbol symbol, global::Ts.Checker.Checker typeChecker, HashSet<SymbolId> seenModules)
    {
        if ((symbol.Flags & SymbolFlags.Type) != 0 || typeChecker.IsUnknownSymbol(symbol))
        {
            return true;
        }
        if ((symbol.Flags & SymbolFlags.Module) != 0 && seenModules.Add(AstUtilities.GetSymbolId(symbol)))
        {
            foreach (var (_, e) in typeChecker.GetExportsOfModule(symbol)) // map-iter: 序无关 (existence check)
            {
                if (SymbolCanBeReferencedAtTypeLocation(e, typeChecker, seenModules))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
