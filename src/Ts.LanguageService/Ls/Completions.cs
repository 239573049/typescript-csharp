// Ported from tsgo internal/ls/completions.go — the completion entry points:
// CompletionList.toLSP (:309-325), getCompletionsAtPosition (:327-451), the label-statement
// completions (:4520-4593) and getRelevantTokens (:2646-2660).

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Diagnostics;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:309-325 — (l *CompletionList) toLSP.
    internal static global::Ts.LsProto.CompletionList? CompletionListToLSP(CompletionList? l)
    {
        if (l == null)
        {
            return null;
        }
        var items = new List<global::Ts.LsProto.CompletionItem>(l.Items?.Length ?? 0);
        foreach (var entry in l.Items ?? [])
        {
            if (entry != null && entry.Item != null)
            {
                items.Add(entry.Item);
            }
        }
        return new global::Ts.LsProto.CompletionList
        {
            IsIncomplete = l.IsIncomplete,
            ItemDefaults = l.ItemDefaults,
            Items = items.ToArray(),
        };
    }

    // completions.go:327-451 — getCompletionsAtPosition. (Go's exported LSP entry ProvideCompletion
    // wraps this with converters; the raw API surfaces the CompletionList directly.)
    public CompletionList? GetCompletionsAtPosition(
        string fileName,
        int position,
        string? triggerCharacter = null,
        bool includeSymbols = false,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(fileName);
        if ((uint)position > (uint)file.End)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        return GetCompletionsAtPositionWorker(file, position, triggerCharacter, includeSymbols, cancellationToken);
    }

    internal CompletionList? GetCompletionsAtPositionWorker(
        SourceFile file,
        int position,
        string? triggerCharacter,
        bool includeSymbols,
        CancellationToken cancellationToken)
    {
        var (_, previousToken) = GetRelevantTokens(position, file);
        if (triggerCharacter != null && !LsDeps.IsInString(file, position, previousToken) && !IsValidTrigger(file, triggerCharacter, previousToken, position))
        {
            return null;
        }

        if (triggerCharacter == " ")
        {
            // `isValidTrigger` ensures we are at `import |`
            if (UserPreferences().IncludeCompletionsForImportStatements.IsTrue())
            {
                return new CompletionList { IsIncomplete = true };
            }
            return null;
        }

        if (GetJSDocSnippetCompletion(file, position) is { } jsDocSnippetCompletion)
        {
            return jsDocSnippetCompletion;
        }

        var compilerOptions = GetProgram().Options();

        // !!! see if incomplete completion list and continue or clean

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var stringCompletions = GetStringLiteralCompletions(
                file,
                position,
                previousToken,
                checker,
                compilerOptions,
                includeSymbols);
            if (stringCompletions != null)
            {
                return stringCompletions;
            }

            if (previousToken != null && (previousToken.Kind == SyntaxKind.BreakKeyword ||
                previousToken.Kind == SyntaxKind.ContinueKeyword ||
                previousToken.Kind == SyntaxKind.Identifier) &&
                AstUtilities.IsBreakOrContinueStatement(previousToken.Parent!))
            {
                return GetLabelCompletionsAtPosition(
                    previousToken.Parent!,
                    file,
                    position,
                    GetOptionalReplacementSpan(previousToken, file));
            }

            var preferences = UserPreferences();
            var data = GetCompletionData(checker, file, position, preferences, false /*forItemResolve*/);
            if (data == null)
            {
                return null;
            }

            switch (data)
            {
                case CompletionDataData dataData:
                {
                    var optionalReplacementSpan = GetOptionalReplacementSpan(dataData.location, file);
                    return CompletionInfoFromData(
                        checker,
                        file,
                        compilerOptions,
                        dataData,
                        position,
                        optionalReplacementSpan,
                        includeSymbols);
                }
                case CompletionDataKeyword keyword:
                {
                    var optionalReplacementSpan = GetOptionalReplacementSpan(previousToken, file);
                    return SpecificKeywordCompletionInfo(
                        position,
                        file,
                        new List<CompletionItem>(keyword.KeywordCompletions ?? []),
                        keyword.IsNewIdentifierLocation,
                        optionalReplacementSpan);
                }
                case CompletionDataJSDocTagName:
                {
                    // If the current position is a jsDoc tag name, only tag names should be provided for completion
                    var items = GetJSDocTagNameCompletions();
                    items.AddRange(GetJSDocParameterCompletions(
                        file,
                        position,
                        checker,
                        compilerOptions,
                        preferences,
                        true /*tagNameOnly*/));
                    return JsDocCompletionInfo(position, file, items);
                }
                case CompletionDataJSDocTag:
                {
                    // If the current position is a jsDoc tag, only tags should be provided for completion
                    var items = GetJSDocTagCompletions();
                    items.AddRange(GetJSDocParameterCompletions(
                        file,
                        position,
                        checker,
                        compilerOptions,
                        preferences,
                        false /*tagNameOnly*/));
                    return JsDocCompletionInfo(position, file, items);
                }
                case CompletionDataJSDocParameterName parameterName:
                    return JsDocCompletionInfo(position, file, GetJSDocParameterNameCompletions(parameterName.tag!));
                default:
                    throw new InvalidOperationException("getCompletionData() returned unexpected type: " + data.GetType().Name);
            }
        }
        finally
        {
            done();
        }
    }

    // jsdoc_snippet.go:29-72 — getJSDocSnippetCompletion.
    // jsdoc_snippet.go:29-72 — getJSDocSnippetCompletion.
    internal CompletionList? GetJSDocSnippetCompletion(SourceFile file, int position)
    {
        if (UserPreferences().EnableJSDocCompletions.IsFalse())
        {
            return null;
        }
        if (!JsDocSnippet.IsPotentiallyValidJSDocSnippetCompletionPosition(file, position))
        {
            return null;
        }
        var newLine = FormatOptions().NewLineCharacter;
        if (newLine.Length == 0)
        {
            newLine = "\n";
        }
        var template = JsDocSnippet.GetDocCommentTemplateAtPosition(file, position, UserPreferences().GenerateReturnInDocTemplate.IsTrue(), newLine);
        if (template == null)
        {
            return null;
        }

        var insertText = template.newText;
        global::Ts.LsProto.InsertTextFormat? insertTextFormat = null;
        if (ClientSupportsItemSnippet())
        {
            insertText = JsDocSnippet.TemplateToSnippet(insertText, newLine);
            insertTextFormat = global::Ts.LsProto.InsertTextFormat.Snippet;
        }

        var editRange = GetJSDocSnippetCompletionRange(file, position, insertText);
        string[]? commitCharacters = null;
        if (ClientSupportsItemCommitCharacters())
        {
            commitCharacters = [];
        }
        var item = new CompletionItem
        {
            Item = new global::Ts.LsProto.CompletionItem
            {
                Label = "/** */",
                Kind = global::Ts.LsProto.CompletionItemKind.Text,
                Detail = global::Ts.Diagnostics.Diagnostics.JSDoc_comment.Localize(global::Ts.Locale.Locale.Default),
                SortText = "\x00",
                InsertTextFormat = insertTextFormat,
                TextEdit = editRange,
                CommitCharacters = commitCharacters,
            },
        };
        return new CompletionList
        {
            IsIncomplete = false,
            Items = [item],
        };
    }

    // jsdoc_snippet.go:87-119 — getJSDocSnippetCompletionRange.
    internal global::Ts.LsProto.TextEditOrInsertReplaceEdit GetJSDocSnippetCompletionRange(SourceFile file, int position, string newText)
    {
        var text = file.Text();
        var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(position, file);
        var prefix = text[lineStart..position];
        var start = position;
        var (prefixStart, okPrefix) = JsDocSnippet.GetJSDocSnippetPrefixStart(prefix);
        if (okPrefix)
        {
            start = lineStart + prefixStart;
        }

        var lineEnd = LsDeps.GetLineEndOfPosition(file, position);
        var suffix = text[position..lineEnd];
        var end = position;
        var (suffixEnd, okSuffix) = JsDocSnippet.GetJSDocSnippetSuffixEnd(suffix);
        if (okSuffix)
        {
            end += suffixEnd;
        }

        var replacementRange = CreateLspRangeFromBounds(start, end, file);
        if (ClientSupportsItemInsertReplace())
        {
            return new global::Ts.LsProto.TextEditOrInsertReplaceEdit
            {
                InsertReplaceEdit = new global::Ts.LsProto.InsertReplaceEdit
                {
                    NewText = newText,
                    Insert = replacementRange,
                    Replace = replacementRange,
                },
            };
        }
        return new global::Ts.LsProto.TextEditOrInsertReplaceEdit
        {
            TextEdit = new global::Ts.LsProto.TextEdit
            {
                NewText = newText,
                Range = replacementRange,
            },
        };
    }

    // string_completions.go:53-89 — getStringLiteralCompletions (type-side half live in
    // Completions.StringLiterals.cs; the module-specifier/path half is PORT-NOTEd there).
    internal CompletionList? GetStringLiteralCompletions(
        SourceFile file,
        int position,
        Node? previousToken,
        TsChecker checker,
        global::Ts.Core.CompilerOptions compilerOptions,
        bool includeSymbols)
    {
        return GetStringLiteralCompletionsWorker(file, position, previousToken, checker, compilerOptions, includeSymbols);
    }

    // completions.go:4520-4544 — getLabelCompletionsAtPosition.
    internal CompletionList? GetLabelCompletionsAtPosition(
        Node node,
        SourceFile file,
        int position,
        global::Ts.LsProto.Range? optionalReplacementSpan)
    {
        var items = GetLabelStatementCompletions(node, file, position);
        if (items.Count == 0)
        {
            return null;
        }
        var defaultCommitCharacters = GetDefaultCommitCharacters(false /*isNewIdentifierLocation*/);
        var itemDefaults = SetItemDefaults(
            position,
            file,
            items,
            defaultCommitCharacters,
            optionalReplacementSpan);
        return new CompletionList
        {
            IsIncomplete = false,
            ItemDefaults = itemDefaults,
            Items = items.ToArray(),
        };
    }

    // completions.go:4546-4593 — getLabelStatementCompletions.
    internal List<CompletionItem> GetLabelStatementCompletions(
        Node node,
        SourceFile file,
        int position)
    {
        var uniques = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<CompletionItem>();
        var current = (Node?)node;
        while (current != null)
        {
            if (AstUtilities.IsFunctionLike(current))
            {
                break;
            }
            if (NodeTests.IsLabeledStatement(current))
            {
                var name = current.Label()!.Text();
                if (!uniques.Contains(name))
                {
                    uniques.Add(name);
                    var lspItem = CreateLSPCompletionItem(
                        name,
                        "", /*insertText*/
                        "", /*filterText*/
                        SortTextLocationPriorityValue,
                        ScriptElementKind.Label,
                        ScriptElementKindModifier.None, /*kindModifiers*/
                        null,                           /*replacementSpan*/
                        null,                           /*commitCharacters*/
                        null,                           /*labelDetails*/
                        file,
                        position,
                        false, /*isMemberCompletion*/
                        false, /*isSnippet*/
                        false, /*hasAction*/
                        false, /*preselect*/
                        "",    /*source*/
                        null,  /*autoImportEntryData*/
                        null /*detail*/);
                    items.Add(new CompletionItem
                    {
                        Item = lspItem,
                    });
                }
            }
            current = current.Parent;
        }
        return items;
    }

    // completions.go:2646-2660 — getRelevantTokens. In a scenario such as `const x = 1 * |`, the
    // context and previous tokens are both `*`. In `const x = 1 * o|`, the context token is *, and
    // the previous token is `o`. `contextToken` and `previousToken` can both be nil if we are at
    // the beginning of the file.
    private static (Node? ContextToken, Node? PreviousToken) GetRelevantTokens(
        int position,
        SourceFile file)
    {
        var previousToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, position);
        if (previousToken is not null &&
            position <= previousToken.End &&
            (AstUtilities.IsMemberName(previousToken) ||
                NodeTests.IsKeywordKind(previousToken.Kind)))
        {
            var contextToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, previousToken.Pos);
            return (contextToken, previousToken);
        }

        return (previousToken, previousToken);
    }
}
