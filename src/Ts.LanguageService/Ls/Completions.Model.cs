// Ported from tsgo internal/ls/completions.go — the self-contained model layer (sort-text ladder,
// completion kinds, keyword filters, symbol-origin family with kind predicates, trigger/commit
// character tables, relevant-token lookup). The completionDataData record and the getCompletionData
// pipeline land with the autoimport-dependent waves.

using System;
using Ts.Ast;

namespace Ts.Ls;

// completions.go:169-181 — KeywordCompletionFilters.
public enum KeywordCompletionFilters
{
    None = 0,                         // No keywords
    All,                              // Every possible keyword
    ClassElementKeywords,             // Keywords inside class body
    InterfaceElementKeywords,         // Keywords inside interface body
    ConstructorParameterKeywords,     // Keywords at constructor parameter
    FunctionLikeBodyKeywords,         // Keywords at function like body
    TypeAssertionKeywords,
    TypeKeywords,
    TypeKeyword,                      // Literally just `type`
    Last = TypeKeyword,
}

// completions.go:196-205 — CompletionKind.
public enum CompletionKind
{
    None = 0,
    ObjectPropertyDeclaration,
    Global,
    PropertyAccess,
    MemberLike,
    String,
}

// completions.go:222-231 — symbolOriginInfoKind (flags).
[Flags]
internal enum SymbolOriginInfoKind
{
    ThisType = 1 << 0,
    SymbolMember = 1 << 1,
    Promise = 1 << 2,
    Nullable = 1 << 3,
    TypeOnlyAlias = 1 << 4,
    ObjectLiteralMethod = 1 << 5,
    Ignore = 1 << 6,
    ComputedPropertyName = 1 << 7,
}

// completions.go:233-266 — symbolOriginInfo and its data payloads.
internal sealed class SymbolOriginInfo
{
    internal SymbolOriginInfoKind kind;
    internal bool isDefaultExport;
    internal bool isFromPackageJson;
    internal string fileName = "";
    internal object? data;

    // completions.go:242-249
    internal string SymbolName()
    {
        if (data is SymbolOriginInfoComputedPropertyName computed)
        {
            return computed.symbolName;
        }
        throw new InvalidOperationException($"symbolOriginInfo: unknown data type for symbolName(): {data?.GetType().Name}");
    }

    // completions.go:257-259
    internal SymbolOriginInfoObjectLiteralMethod AsObjectLiteralMethod() => (SymbolOriginInfoObjectLiteralMethod)data!;
}

// completions.go:251-255
internal sealed class SymbolOriginInfoObjectLiteralMethod
{
    internal string insertText = "";
    internal object? labelDetails; // lsproto.CompletionItemLabelDetails — typed when Protocol.g.cs lands it
    internal bool isSnippet;
}

// completions.go:261-263
internal sealed class SymbolOriginInfoTypeOnlyAlias
{
    internal Node? declaration; // *ast.TypeOnlyImportDeclaration
}

// completions.go:265-267
internal sealed class SymbolOriginInfoComputedPropertyName
{
    internal string symbolName = "";
}

// completions.go:269-292 — special values for `CompletionInfo['source']` used to disambiguate
// completion items with the same `name`.
internal static class CompletionSource
{
    // Completions that require `this.` insertion text.
    internal const string ThisProperty = "ThisProperty/";
    // Auto-import that comes attached to a class member snippet.
    internal const string ClassMemberSnippet = "ClassMemberSnippet/";
    // A type-only import that needs to be promoted in order to be used at the completion location.
    internal const string TypeOnlyAlias = "TypeOnlyAlias/";
    // Auto-import that comes attached to an object literal method snippet.
    internal const string ObjectLiteralMethodSnippet = "ObjectLiteralMethodSnippet/";
    // Case completions for switch statements.
    internal const string SwitchCases = "SwitchCases/";
    // Completions for an object literal expression.
    internal const string ObjectLiteralMemberWithComma = "ObjectLiteralMemberWithComma/";
}

// completions.go:302-308 — globalsSearch.
internal enum GlobalsSearch
{
    Continue = 0,
    Success,
    Fail,
}

public sealed partial class LanguageService
{
    // completions.go:183-185 — trigger characters.
    public static readonly string[] TriggerCharacters = [".", "\"", "'", "`", "/", "@", "<", "#", " ", "*"];

    // completions.go:187-193 — commit character tables.
    internal static readonly string[] AllCommitCharacters = [".", ",", ";"];
    internal static readonly string[] NoCommaCommitCharacters = [".", ";"];
    internal static readonly string[] EmptyCommitCharacters = [];

    // completions.go:195-211 — SortText ladder (string-typed in Go; ordinal string compare drives sorting).
    internal const string SortTextLocalDeclarationPriorityValue = "10";
    internal const string SortTextLocationPriorityValue = "11";
    internal const string SortTextOptionalMemberValue = "12";
    internal const string SortTextMemberDeclaredBySpreadAssignmentValue = "13";
    internal const string SortTextSuggestedClassMembersValue = "14";
    internal const string SortTextGlobalsOrKeywordsValue = "15";
    internal const string SortTextAutoImportSuggestionsValue = "16";
    internal const string SortTextClassMemberSnippetsValue = "17";
    internal const string SortTextJavascriptIdentifiersValue = "18";

    // completions.go:213-215
    public static string DeprecateSortText(string original) => "z" + original;

    // completions.go:217-219
    internal static string SortBelow(string original) => original + "1";

    // completions.go:169-176 — keywordFiltersFromSyntaxKind.
    internal static KeywordCompletionFilters KeywordFiltersFromSyntaxKind(SyntaxKind keywordCompletion)
    {
        switch (keywordCompletion)
        {
            case SyntaxKind.TypeKeyword:
                return KeywordCompletionFilters.TypeKeyword;
            default:
                throw new InvalidOperationException($"Unknown mapping from ast.Kind `{keywordCompletion}` to KeywordCompletionFilters");
        }
    }

    // completions.go:1724-1729 — getDefaultCommitCharacters.
    internal static string[] GetDefaultCommitCharacters(bool isNewIdentifierLocation)
    {
        if (isNewIdentifierLocation)
        {
            return EmptyCommitCharacters;
        }
        return AllCommitCharacters;
    }

    // completions.go:2644-2680 — origin predicates.
    internal static bool OriginIsIgnore(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.Ignore) != 0;

    internal static bool OriginIncludesSymbolName(SymbolOriginInfo? origin) => OriginIsComputedPropertyName(origin);

    internal static bool OriginIsComputedPropertyName(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.ComputedPropertyName) != 0;

    internal static bool OriginIsObjectLiteralMethod(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.ObjectLiteralMethod) != 0;

    internal static bool OriginIsThisTypeNode(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.ThisType) != 0;

    internal static bool OriginIsTypeOnlyAlias(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.TypeOnlyAlias) != 0;

    internal static bool OriginIsSymbolMember(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.SymbolMember) != 0;

    internal static bool OriginIsNullableMember(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.Nullable) != 0;

    internal static bool OriginIsPromise(SymbolOriginInfo? origin) => origin != null && (origin.kind & SymbolOriginInfoKind.Promise) != 0;

    // completions.go:2681-2691 — getSourceFromOrigin.
    internal static string GetSourceFromOrigin(SymbolOriginInfo? origin)
    {
        if (OriginIsThisTypeNode(origin))
        {
            return CompletionSource.ThisProperty;
        }
        if (OriginIsTypeOnlyAlias(origin))
        {
            return CompletionSource.TypeOnlyAlias;
        }
        return "";
    }

}
