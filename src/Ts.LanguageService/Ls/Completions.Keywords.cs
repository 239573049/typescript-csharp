// Ported from tsgo internal/ls/completions.go — the keyword-completion cluster (Go CompletionItem/
// CompletionList wrapper types, the all-keywords table with its filter cache, keyword predicates)
// plus ls/utilities.go's typeKeywords set.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;

namespace Ts.Ls;

// completions.go:65-68 — CompletionItem: the LSP item plus the resolved symbol (IncludeSymbols).
public sealed class CompletionItem
{
    public global::Ts.LsProto.CompletionItem? Item;
    public Symbol? Symbol; // non-null for symbol completions when IncludeSymbols is set; null otherwise
}

// completions.go:70-75 — CompletionList.
public sealed class CompletionList
{
    public bool IsIncomplete;
    public global::Ts.LsProto.CompletionItemDefaults? ItemDefaults;
    public object? ApplyKind; // lsproto.CompletionItemApplyKinds — typed when the applyKind capability wave lands
    public CompletionItem[]? Items;
}

public sealed partial class LanguageService
{
    // completions.go:3260-3273 — keywordCompletionsCache + allKeywordCompletions (sync.OnceValue →
    // Lazy<T>; the cache is keyed by filter, with filtered-TS-only entries offset past Last).
    private static readonly SyncMap<KeywordCompletionFilters, global::Ts.LsProto.CompletionItem[]> KeywordCompletionsCache = new();

    private static readonly Lazy<global::Ts.LsProto.CompletionItem[]> AllKeywordCompletions = new(() =>
    {
        var result = new List<global::Ts.LsProto.CompletionItem>((int)(SyntaxKind.LastKeyword - SyntaxKind.FirstKeyword + 1));
        for (var i = SyntaxKind.FirstKeyword; i <= SyntaxKind.LastKeyword; i++)
        {
            result.Add(new global::Ts.LsProto.CompletionItem
            {
                Label = global::Ts.Scanner.Scanner.TokenToString(i),
                Kind = global::Ts.LsProto.CompletionItemKind.Keyword,
                SortText = SortTextGlobalsOrKeywordsValue,
            });
        }
        return result.ToArray();
    });

    // completions.go:3275-3285 — cloneItems.
    internal static CompletionItem[]? CloneItems(global::Ts.LsProto.CompletionItem[]? items)
    {
        if (items == null)
        {
            return null;
        }
        var entries = new CompletionItem[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var itemClone = new global::Ts.LsProto.CompletionItem
            {
                Label = item.Label,
                LabelDetails = item.LabelDetails,
                Kind = item.Kind,
                Tags = item.Tags,
                Detail = item.Detail,
                Documentation = item.Documentation,
                Deprecated = item.Deprecated,
                Preselect = item.Preselect,
                SortText = item.SortText,
                FilterText = item.FilterText,
                InsertText = item.InsertText,
            };
            entries[i] = new CompletionItem { Item = itemClone };
        }
        return entries;
    }

    // completions.go:3287-3304 — getKeywordCompletions.
    internal static CompletionItem[]? GetKeywordCompletions(KeywordCompletionFilters keywordFilter, bool filterOutTsOnlyKeywords)
    {
        if (!filterOutTsOnlyKeywords)
        {
            return CloneItems(GetTypescriptKeywordCompletions(keywordFilter));
        }

        var index = keywordFilter + (int)KeywordCompletionFilters.Last + 1;
        if (KeywordCompletionsCache.TryLoad(index, out var cached))
        {
            return CloneItems(cached);
        }
        var result = GetTypescriptKeywordCompletions(keywordFilter)
            .Where(ci => !IsTypeScriptOnlyKeyword(global::Ts.Scanner.Scanner.StringToToken(ci.Label)))
            .ToArray();
        KeywordCompletionsCache.Store(index, result);
        return CloneItems(result);
    }

    // completions.go:3306-3345 — getTypescriptKeywordCompletions.
    internal static global::Ts.LsProto.CompletionItem[] GetTypescriptKeywordCompletions(KeywordCompletionFilters keywordFilter)
    {
        if (KeywordCompletionsCache.TryLoad(keywordFilter, out var cached))
        {
            return cached!;
        }
        var result = AllKeywordCompletions.Value.Where(entry =>
        {
            var kind = global::Ts.Scanner.Scanner.StringToToken(entry.Label);
            switch (keywordFilter)
            {
                case KeywordCompletionFilters.None:
                    return false;
                case KeywordCompletionFilters.All:
                    return IsFunctionLikeBodyKeyword(kind) ||
                        kind == SyntaxKind.DeclareKeyword ||
                        kind == SyntaxKind.ModuleKeyword ||
                        kind == SyntaxKind.TypeKeyword ||
                        kind == SyntaxKind.NamespaceKeyword ||
                        kind == SyntaxKind.AbstractKeyword ||
                        (IsTypeKeywordKind(kind) && kind != SyntaxKind.UndefinedKeyword);
                case KeywordCompletionFilters.FunctionLikeBodyKeywords:
                    return IsFunctionLikeBodyKeyword(kind);
                case KeywordCompletionFilters.ClassElementKeywords:
                    return IsClassMemberCompletionKeyword(kind);
                case KeywordCompletionFilters.InterfaceElementKeywords:
                    return IsInterfaceOrTypeLiteralCompletionKeyword(kind);
                case KeywordCompletionFilters.ConstructorParameterKeywords:
                    return AstUtilities.IsParameterPropertyModifier(kind);
                case KeywordCompletionFilters.TypeAssertionKeywords:
                    return IsTypeKeywordKind(kind) || kind == SyntaxKind.ConstKeyword;
                case KeywordCompletionFilters.TypeKeywords:
                    return IsTypeKeywordKind(kind);
                case KeywordCompletionFilters.TypeKeyword:
                    return kind == SyntaxKind.TypeKeyword;
                default:
                    throw new InvalidOperationException($"Unknown keyword filter: {keywordFilter}");
            }
        }).ToArray();

        KeywordCompletionsCache.Store(keywordFilter, result);
        return result;
    }

    // completions.go:3347-3379 — isTypeScriptOnlyKeyword.
    internal static bool IsTypeScriptOnlyKeyword(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.AbstractKeyword:
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.GlobalKeyword:
            case SyntaxKind.ImplementsKeyword:
            case SyntaxKind.InferKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.IsKeyword:
            case SyntaxKind.KeyOfKeyword:
            case SyntaxKind.ModuleKeyword:
            case SyntaxKind.NamespaceKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.OverrideKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.ReadonlyKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.TypeKeyword:
            case SyntaxKind.UniqueKeyword:
            case SyntaxKind.UnknownKeyword:
                return true;
            default:
                return false;
        }
    }

    // completions.go:3381-3389 — isFunctionLikeBodyKeyword.
    internal static bool IsFunctionLikeBodyKeyword(SyntaxKind kind)
    {
        return kind == SyntaxKind.AsyncKeyword ||
            kind == SyntaxKind.AwaitKeyword ||
            kind == SyntaxKind.UsingKeyword ||
            kind == SyntaxKind.AsKeyword ||
            kind == SyntaxKind.SatisfiesKeyword ||
            kind == SyntaxKind.TypeKeyword ||
            (!AstUtilities.IsContextualKeyword(kind) && !IsClassMemberCompletionKeyword(kind));
    }

    // completions.go:3391-3399 — isClassMemberCompletionKeyword.
    internal static bool IsClassMemberCompletionKeyword(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.AbstractKeyword:
            case SyntaxKind.AccessorKeyword:
            case SyntaxKind.ConstructorKeyword:
            case SyntaxKind.GetKeyword:
            case SyntaxKind.SetKeyword:
            case SyntaxKind.AsyncKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.OverrideKeyword:
                return true;
            default:
                return AstUtilities.IsClassMemberModifier(kind);
        }
    }

    // completions.go:3401-3403 — isInterfaceOrTypeLiteralCompletionKeyword.
    internal static bool IsInterfaceOrTypeLiteralCompletionKeyword(SyntaxKind kind) =>
        kind == SyntaxKind.ReadonlyKeyword;

    // completions.go:3405-3415 — isContextualKeywordInAutoImportableExpressionSpace.
    internal static bool IsContextualKeywordInAutoImportableExpressionSpace(string keyword)
    {
        return keyword is "abstract" or "async" or "await" or "declare" or "module" or "namespace"
            or "type" or "satisfies" or "as";
    }

    // ls/utilities.go:317-338 — typeKeywords set + isTypeKeyword. (Named *Kind to avoid colliding
    // with the KeywordCompletionFilters member.)
    private static readonly HashSet<SyntaxKind> TypeKeywords =
    [
        SyntaxKind.AnyKeyword,
        SyntaxKind.AssertsKeyword,
        SyntaxKind.BigIntKeyword,
        SyntaxKind.BooleanKeyword,
        SyntaxKind.FalseKeyword,
        SyntaxKind.InferKeyword,
        SyntaxKind.KeyOfKeyword,
        SyntaxKind.NeverKeyword,
        SyntaxKind.NullKeyword,
        SyntaxKind.NumberKeyword,
        SyntaxKind.ObjectKeyword,
        SyntaxKind.ReadonlyKeyword,
        SyntaxKind.StringKeyword,
        SyntaxKind.SymbolKeyword,
        SyntaxKind.TypeOfKeyword,
        SyntaxKind.TrueKeyword,
        SyntaxKind.VoidKeyword,
        SyntaxKind.UndefinedKeyword,
        SyntaxKind.UniqueKeyword,
        SyntaxKind.UnknownKeyword,
    ];

    internal static bool IsTypeKeywordKind(SyntaxKind kind) => TypeKeywords.Contains(kind);

    // completions.go:1713-1722 — keywordCompletionData.
    internal sealed record CompletionDataKeyword(CompletionItem[]? KeywordCompletions, bool IsNewIdentifierLocation) : ICompletionData;

    internal static CompletionDataKeyword KeywordCompletionData(
        KeywordCompletionFilters keywordFilters,
        bool filterOutTSOnlyKeywords,
        bool isNewIdentifierLocation)
    {
        return new CompletionDataKeyword(
            GetKeywordCompletions(keywordFilters, filterOutTSOnlyKeywords),
            isNewIdentifierLocation);
    }
}
