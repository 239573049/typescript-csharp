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


    // completions.go:2743-2750 — isStringLiteralOrTemplate.
    internal static bool IsStringLiteralOrTemplate(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.TaggedTemplateExpression:
                return true;
        }
        return false;
    }

    // completions.go:2752-2754 — binaryExpressionMayBeOpenTag.
    internal static bool BinaryExpressionMayBeOpenTag(BinaryExpression binaryExpression) =>
        AstUtilities.NodeIsMissing(binaryExpression.Left);

    // completions.go:2756-2758 — isCheckedFile.
    internal static bool IsCheckedFile(SourceFile file, global::Ts.Core.CompilerOptions compilerOptions) =>
        !AstUtilities.IsSourceFileJS(file) || AstUtilities.IsCheckJSEnabledForFile(file, compilerOptions);

    // completions.go:2707-2741 — isValidTrigger.
    internal static bool IsValidTrigger(SourceFile file, string triggerCharacter, Node? contextToken, int position)
    {
        switch (triggerCharacter)
        {
            case ".":
            case "@":
                return true;
            case "\"":
            case "'":
            case "`":
                // Only automatically bring up completions if this is an opening quote.
                return contextToken != null &&
                    IsStringLiteralOrTemplate(contextToken) &&
                    position == global::Ts.AstNav.Tokens.GetStartOfNode(contextToken, file, false /*includeJSDoc*/) + 1;
            case "#":
                return contextToken != null &&
                    NodeTests.IsPrivateIdentifier(contextToken) &&
                    AstUtilities.GetContainingClass(contextToken) != null;
            case "<":
                // Opening JSX tag
                return contextToken != null &&
                    contextToken.Kind == SyntaxKind.LessThanToken &&
                    (!NodeTests.IsBinaryExpression(contextToken.Parent!) || BinaryExpressionMayBeOpenTag(contextToken.Parent!.AsBinaryExpression()));
            case "/":
                if (contextToken == null)
                {
                    return false;
                }
                if (AstUtilities.IsStringLiteralLike(contextToken))
                {
                    return AstUtilities.TryGetImportFromModuleSpecifier(contextToken) != null;
                }
                return contextToken.Kind == SyntaxKind.LessThanSlashToken && NodeTests.IsJsxClosingElement(contextToken.Parent!);
            case " ":
                return contextToken != null && contextToken.Kind == SyntaxKind.ImportKeyword && contextToken.Parent!.Kind == SyntaxKind.SourceFile;
            case "*":
                return JsDocSnippet.IsPotentiallyValidJSDocSnippetCompletionPosition(file, position);
            default:
                throw new System.InvalidOperationException("Unknown trigger character: " + triggerCharacter);
        }
    }

    // completions.go:2759-2763 — isContextTokenValueLocation.
    internal static bool IsContextTokenValueLocation(Node? contextToken)
    {
        return contextToken != null && ((contextToken.Kind == SyntaxKind.TypeOfKeyword &&
            (contextToken.Parent!.Kind == SyntaxKind.TypeQuery || NodeTests.IsTypeOfExpression(contextToken.Parent))) ||
            (contextToken.Kind == SyntaxKind.AssertsKeyword && contextToken.Parent!.Kind == SyntaxKind.TypePredicate));
    }

    // completions.go:2772-2796 — isContextTokenTypeLocation.
    internal static bool IsContextTokenTypeLocation(Node? contextToken)
    {
        if (contextToken != null)
        {
            var parentKind = contextToken.Parent!.Kind;
            switch (contextToken.Kind)
            {
                case SyntaxKind.ColonToken:
                    return parentKind == SyntaxKind.PropertyDeclaration ||
                        parentKind == SyntaxKind.PropertySignature ||
                        parentKind == SyntaxKind.Parameter ||
                        parentKind == SyntaxKind.VariableDeclaration ||
                        AstUtilities.IsFunctionLikeKind(parentKind);
                case SyntaxKind.EqualsToken:
                    return parentKind == SyntaxKind.TypeAliasDeclaration || parentKind == SyntaxKind.TypeParameter;
                case SyntaxKind.AsKeyword:
                    return parentKind == SyntaxKind.AsExpression;
                case SyntaxKind.LessThanToken:
                    return parentKind == SyntaxKind.TypeReference || parentKind == SyntaxKind.TypeAssertionExpression;
                case SyntaxKind.ExtendsKeyword:
                    return parentKind == SyntaxKind.TypeParameter;
                case SyntaxKind.SatisfiesKeyword:
                    return parentKind == SyntaxKind.SatisfiesExpression;
            }
        }
        return false;
    }
}
