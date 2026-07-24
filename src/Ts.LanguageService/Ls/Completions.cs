// Ported from tsgo internal/ls/completions.go (completion foundation subset).

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Ls;

public sealed record CompletionInfo(
    bool IsGlobalCompletion,
    bool IsMemberCompletion,
    bool IsNewIdentifierLocation,
    CompletionEntry[] Entries);

public sealed record CompletionEntry(
    string Name,
    string Kind,
    string SortText,
    bool IsRecommended);

public sealed partial class LanguageService
{
    private const string SortTextLocationPriority = "11";
    private const string SortTextGlobalsOrKeywords = "15";

    public CompletionInfo? GetCompletionsAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(fileName);
        if ((uint)position > (uint)file.End)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            return GetCompletionData(checker, file, position);
        }
        finally
        {
            done();
        }
    }

    // completions.go:getCompletionData (foundation subset)
    private static CompletionInfo? GetCompletionData(
        global::Ts.Checker.Checker typeChecker,
        global::Ts.Ast.SourceFile file,
        int position)
    {
        var (contextToken, previousToken) = GetRelevantTokens(position, file);
        var location = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);
        var isMemberCompletion = IsMemberAccessCompletion(contextToken);
        var entries = new List<CompletionEntry>();

        if (isMemberCompletion)
        {
            // TODO: Complete the faithful getTypeScriptMemberSymbols path when the
            // checker publicly exposes GetTypeAtLocation, GetApparentType,
            // GetPropertiesOfType, and property-access validation. Those APIs are
            // currently absent or internal to Ts.Compiler, and AOT rules forbid
            // reflection as an access workaround.
            _ = typeChecker;
            _ = location;
        }
        else
        {
            // TODO: Append symbols from typeChecker.GetSymbolsInScope(location,
            // SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace |
            // SymbolFlags.Alias) when that tsgo service API is ported and public.
            // The entry pipeline below already preserves symbol-before-keyword
            // deduplication and the Go completion comparison key.
            _ = typeChecker;
            _ = location;

            AddKeywordCompletions(entries);
        }

        // TODO: Port auto-import, JSX, string-literal, snippet, and completion-item
        // detail resolution after their supporting LanguageService layers land.
        DeduplicateEntries(entries);
        StableSortEntries(entries);

        return new CompletionInfo(
            IsGlobalCompletion: !isMemberCompletion,
            IsMemberCompletion: isMemberCompletion,
            IsNewIdentifierLocation: false,
            Entries: entries.ToArray());
    }

    // In a scenario such as `const x = 1 * |`, the context and previous tokens are both `*`.
    // In `const x = 1 * o|`, the context token is *, and the previous token is `o`.
    // `contextToken` and `previousToken` can both be nil if we are at the beginning of the file.
    private static (global::Ts.Ast.Node? ContextToken, global::Ts.Ast.Node? PreviousToken) GetRelevantTokens(
        int position,
        global::Ts.Ast.SourceFile file)
    {
        var previousToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, position);
        if (previousToken is not null &&
            position <= previousToken.End &&
            (global::Ts.Ast.AstUtilities.IsMemberName(previousToken) ||
                global::Ts.Ast.NodeTests.IsKeywordKind(previousToken.Kind)))
        {
            var contextToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, previousToken.Pos);
            return (contextToken, previousToken);
        }

        return (previousToken, previousToken);
    }

    private static bool IsMemberAccessCompletion(global::Ts.Ast.Node? contextToken)
    {
        if (contextToken is null ||
            (contextToken.Kind != global::Ts.Ast.SyntaxKind.DotToken &&
                contextToken.Kind != global::Ts.Ast.SyntaxKind.QuestionDotToken))
        {
            return false;
        }

        var parent = contextToken.Parent;
        if (parent is null)
        {
            return false;
        }

        return parent.Kind is
            global::Ts.Ast.SyntaxKind.PropertyAccessExpression or
            global::Ts.Ast.SyntaxKind.QualifiedName or
            global::Ts.Ast.SyntaxKind.ModuleDeclaration or
            global::Ts.Ast.SyntaxKind.ImportType or
            global::Ts.Ast.SyntaxKind.MetaProperty;
    }

    private static void AddKeywordCompletions(List<CompletionEntry> entries)
    {
        for (var kind = global::Ts.Ast.SyntaxKind.FirstKeyword;
            kind <= global::Ts.Ast.SyntaxKind.LastKeyword;
            kind++)
        {
            if (!IsAllKeywordCompletion(kind))
            {
                continue;
            }

            entries.Add(new CompletionEntry(
                global::Ts.Scanner.Scanner.TokenToString(kind),
                "keyword",
                SortTextGlobalsOrKeywords,
                false));
        }
    }

    // completions.go:getTypescriptKeywordCompletions, KeywordCompletionFiltersAll
    private static bool IsAllKeywordCompletion(global::Ts.Ast.SyntaxKind kind)
    {
        return IsFunctionLikeBodyKeyword(kind) ||
            kind == global::Ts.Ast.SyntaxKind.DeclareKeyword ||
            kind == global::Ts.Ast.SyntaxKind.ModuleKeyword ||
            kind == global::Ts.Ast.SyntaxKind.TypeKeyword ||
            kind == global::Ts.Ast.SyntaxKind.NamespaceKeyword ||
            kind == global::Ts.Ast.SyntaxKind.AbstractKeyword ||
            (IsTypeKeywordKind(kind) && kind != global::Ts.Ast.SyntaxKind.UndefinedKeyword);
    }

    private static void DeduplicateEntries(List<CompletionEntry> entries)
    {
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < entries.Count; readIndex++)
        {
            var entry = entries[readIndex];
            if (!uniqueNames.Add(entry.Name))
            {
                continue;
            }

            entries[writeIndex] = entry;
            writeIndex++;
        }

        if (writeIndex < entries.Count)
        {
            entries.RemoveRange(writeIndex, entries.Count - writeIndex);
        }
    }

    // Editors use sortText and then fall back to name. Keep response-order ties stable,
    // matching fourslash's stable sort with completions.go:CompareCompletionEntries.
    private static void StableSortEntries(List<CompletionEntry> entries)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            var entry = entries[index];
            var insertionIndex = index;
            while (insertionIndex > 0 && CompareCompletionEntries(entries[insertionIndex - 1], entry) > 0)
            {
                entries[insertionIndex] = entries[insertionIndex - 1];
                insertionIndex--;
            }

            entries[insertionIndex] = entry;
        }
    }

    private static int CompareCompletionEntries(CompletionEntry a, CompletionEntry b)
    {
        var result = global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(
            a.SortText,
            b.SortText);
        if (result == 0)
        {
            result = global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(
                a.Name,
                b.Name);
        }
        return result;
    }
}
