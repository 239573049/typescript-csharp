// Ported from tsgo internal/ls/completions.go — completionInfoFromData (:1731-1861) and its
// support tier: contextual keywords (:3405-3441), JS identifier entries (:3443-3470),
// optional replacement span (:3472-3478), setItemDefaults (:4251-4319),
// specificKeywordCompletionInfo (:4321-4342), getJsxClosingTagCompletion (:4344-4424); plus
// the caseClauseTracker (utilities.go:1025-1097).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

// utilities.go:1025-1064 — caseClauseTrackerState (the sole implementation of the
// caseClauseTracker interface; Go's interface collapses to the concrete class).
internal sealed class CaseClauseTracker
{
    private readonly HashSet<string> existingStrings = new(global::System.StringComparer.Ordinal);
    private readonly HashSet<global::Ts.JsNum.Number> existingNumbers = [];
    private readonly HashSet<global::Ts.JsNum.PseudoBigInt> existingBigInts = [];

    // utilities.go:1042-1051 — addValue (string | jsnum.Number).
    internal void AddValue(object value)
    {
        switch (value)
        {
            case string s:
                existingStrings.Add(s);
                break;
            case global::Ts.JsNum.Number n:
                existingNumbers.Add(n);
                break;
            default:
                throw new global::System.InvalidOperationException($"Unsupported type: {value.GetType().Name}");
        }
    }

    // utilities.go:1053-1064 — hasValue (string | jsnum.Number | jsnum.PseudoBigInt).
    internal bool HasValue(object value)
    {
        switch (value)
        {
            case string s:
                return existingStrings.Contains(s);
            case global::Ts.JsNum.Number n:
                return existingNumbers.Contains(n);
            case global::Ts.JsNum.PseudoBigInt p:
                return existingBigInts.Contains(p);
            default:
                throw new global::System.InvalidOperationException($"Unsupported type: {value.GetType().Name}");
        }
    }

    internal void AddString(string value) => existingStrings.Add(value);

    internal void AddNumber(global::Ts.JsNum.Number value) => existingNumbers.Add(value);

    internal void AddBigInt(global::Ts.JsNum.PseudoBigInt value) => existingBigInts.Add(value);

    // utilities.go:1066-1097 — newCaseClauseTracker.
    internal static CaseClauseTracker NewCaseClauseTracker(TsChecker typeChecker, Node[] clauses)
    {
        var c = new CaseClauseTracker();
        foreach (var clause in clauses)
        {
            if (!NodeTests.IsDefaultClause(clause))
            {
                var expression = AstUtilities.SkipParentheses(clause.Expression()!);
                if (AstUtilities.IsLiteralExpression(expression))
                {
                    switch (expression.Kind)
                    {
                        case SyntaxKind.NoSubstitutionTemplateLiteral:
                        case SyntaxKind.StringLiteral:
                            c.AddString(expression.Text());
                            break;
                        case SyntaxKind.NumericLiteral:
                            c.AddNumber(global::Ts.JsNum.JsNum.FromString(expression.Text()));
                            break;
                        case SyntaxKind.BigIntLiteral:
                            c.AddBigInt(global::Ts.JsNum.JsNum.ParseValidBigInt(expression.Text()));
                            break;
                    }
                }
                else
                {
                    var symbol = typeChecker.GetSymbolAtLocation(clause.Expression()!);
                    if (symbol != null && symbol.ValueDeclaration != null && NodeTests.IsEnumMember(symbol.ValueDeclaration))
                    {
                        var enumValue = typeChecker.GetConstantValue(symbol.ValueDeclaration);
                        if (enumValue != null)
                        {
                            c.AddValue(enumValue);
                        }
                    }
                }
            }
        }
        return c;
    }
}

public sealed partial class LanguageService
{
    // completions.go:1731-1861 — completionInfoFromData.
    internal CompletionList? CompletionInfoFromData(
        TsChecker typeChecker,
        SourceFile file,
        global::Ts.Core.CompilerOptions compilerOptions,
        CompletionDataData data,
        int position,
        global::Ts.LsProto.Range? optionalReplacementSpan,
        bool includeSymbols)
    {
        var keywordFilters = data.keywordFilters;
        var isNewIdentifierLocation = data.isNewIdentifierLocation;
        var contextToken = data.contextToken;
        var literals = data.literals;
        var preferences = UserPreferences();

        // Verify if the file is JSX language variant
        if (file.LanguageVariant == global::Ts.Core.LanguageVariant.Jsx)
        {
            var list = GetJsxClosingTagCompletion(data.location, file, position);
            if (list != null)
            {
                return list;
            }
        }

        // When the completion is for the expression of a case clause (e.g. `case |`),
        // filter literals & enum symbols whose values are already present in existing case clauses.
        var caseClause = AstUtilities.FindAncestor(contextToken, NodeTests.IsCaseClause);
        if (caseClause != null &&
            (contextToken!.Kind == SyntaxKind.CaseKeyword ||
                AstUtilities.IsNodeDescendantOf(contextToken, caseClause.Expression())))
        {
            var tracker = CaseClauseTracker.NewCaseClauseTracker(typeChecker, caseClause.Parent!.AsCaseBlock().Clauses!.Nodes);
            literals = CoreUtil.Filter(literals, literal => literal != null && !tracker.HasValue(literal));
            data.symbols = CoreUtil.Filter(data.symbols, symbol =>
            {
                if (symbol.ValueDeclaration != null && NodeTests.IsEnumMember(symbol.ValueDeclaration))
                {
                    var value = typeChecker.GetConstantValue(symbol.ValueDeclaration);
                    if (value != null && tracker.HasValue(value))
                    {
                        return false;
                    }
                }
                return true;
            });
        }

        var isChecked = IsCheckedFile(file, compilerOptions);
        if (isChecked && !isNewIdentifierLocation && (data.symbols ?? []).Length == 0 && keywordFilters == KeywordCompletionFilters.None)
        {
            return null;
        }

        var (uniqueNames, sortedEntries) = GetCompletionEntriesFromSymbols(
            typeChecker,
            data,
            null, /*replacementToken*/
            position,
            file,
            compilerOptions,
            includeSymbols);

        if (data.keywordFilters != KeywordCompletionFilters.None)
        {
            var keywordCompletions = GetKeywordCompletions(data.keywordFilters, !data.insideJSDocTagTypeExpression && AstUtilities.IsSourceFileJS(file));
            foreach (var keywordEntry in keywordCompletions ?? [])
            {
                if (data.isTypeOnlyLocation && IsTypeKeywordKind(global::Ts.Scanner.Scanner.StringToToken(keywordEntry.Item!.Label)) ||
                    !data.isTypeOnlyLocation && IsContextualKeywordInAutoImportableExpressionSpace(keywordEntry.Item!.Label) ||
                    !uniqueNames.Contains(keywordEntry.Item!.Label))
                {
                    uniqueNames.Add(keywordEntry.Item!.Label);
                    sortedEntries.Add(keywordEntry);
                }
            }
        }

        foreach (var keywordEntry in GetContextualKeywords(file, contextToken, position))
        {
            if (!uniqueNames.Contains(keywordEntry.Label))
            {
                uniqueNames.Add(keywordEntry.Label);
                sortedEntries.Add(new CompletionItem { Item = keywordEntry });
            }
        }

        foreach (var literal in literals ?? [])
        {
            var literalEntry = CreateCompletionItemForLiteral(file, preferences, literal!);
            uniqueNames.Add(literalEntry.Label);
            sortedEntries.Add(new CompletionItem { Item = literalEntry });
        }

        if (!isChecked)
        {
            sortedEntries = GetJSCompletionEntries(
                file,
                position,
                uniqueNames,
                sortedEntries);
        }

        if (contextToken != null && !data.isRightOfOpenTag && !data.isRightOfDotOrQuestionDot)
        {
            if (AstUtilities.FindAncestorKind(contextToken, SyntaxKind.CaseBlock) is { } caseBlock)
            {
                var casesItem = GetExhaustiveCaseSnippets(
                    caseBlock,
                    file,
                    position,
                    compilerOptions,
                    typeChecker);
                if (casesItem != null)
                {
                    sortedEntries.Add(new CompletionItem { Item = casesItem });
                }
            }
        }

        var itemDefaults = SetItemDefaults(
            position,
            file,
            sortedEntries,
            data.defaultCommitCharacters,
            optionalReplacementSpan);

        return new CompletionList
        {
            IsIncomplete = data.hasUnresolvedAutoImports,
            ItemDefaults = itemDefaults,
            Items = sortedEntries.ToArray(),
        };
    }

    // completions.go:3417-3441 — getContextualKeywords.
    internal static List<global::Ts.LsProto.CompletionItem> GetContextualKeywords(SourceFile file, Node? contextToken, int position)
    {
        var entries = new List<global::Ts.LsProto.CompletionItem>();
        // An `AssertClause` can come after an import declaration:
        //  import * from "foo" |
        //  import "foo" |
        // or after a re-export declaration that has a module specifier:
        //  export { foo } from "foo" |
        // Source: https://tc39.es/proposal-import-assertions/
        if (contextToken != null)
        {
            var parent = contextToken.Parent!;
            var tokenLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, contextToken.End);
            var currentLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, position);
            if ((NodeTests.IsImportDeclaration(parent) ||
                NodeTests.IsExportDeclaration(parent) && parent.ModuleSpecifier() != null) &&
                contextToken == parent.ModuleSpecifier() &&
                tokenLine == currentLine)
            {
                entries.Add(new global::Ts.LsProto.CompletionItem
                {
                    Label = global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.AssertKeyword),
                    Kind = global::Ts.LsProto.CompletionItemKind.Keyword,
                    SortText = SortTextGlobalsOrKeywordsValue,
                });
            }
        }
        return entries;
    }

    // completions.go:3443-3470 — getJSCompletionEntries.
    internal List<CompletionItem> GetJSCompletionEntries(
        SourceFile file,
        int position,
        HashSet<string> uniqueNames,
        List<CompletionItem> sortedEntries)
    {
        var nameTable = file.GetNameTable();
        foreach (var (name, pos) in nameTable) // map-iter: 序无关 (client sorts entries by sortText/label)
        {
            // Skip identifiers produced only from the current location
            if (pos == position)
            {
                continue;
            }
            if (!uniqueNames.Contains(name) && global::Ts.Scanner.Scanner.IsIdentifierText(name, global::Ts.Core.LanguageVariant.Standard))
            {
                uniqueNames.Add(name);
                sortedEntries.Add(new CompletionItem
                {
                    Item = new global::Ts.LsProto.CompletionItem
                    {
                        Label = name,
                        Kind = global::Ts.LsProto.CompletionItemKind.Text,
                        SortText = SortTextJavascriptIdentifiersValue,
                        CommitCharacters = [],
                    },
                });
            }
        }
        return sortedEntries;
    }

    // completions.go:3472-3478 — getOptionalReplacementSpan.
    internal global::Ts.LsProto.Range? GetOptionalReplacementSpan(Node? location, SourceFile file)
    {
        // StringLiteralLike locations are handled separately in stringCompletions.ts
        if (location != null && (location.Kind == SyntaxKind.Identifier || location.Kind == SyntaxKind.PrivateIdentifier))
        {
            var start = global::Ts.AstNav.Tokens.GetStartOfNode(location, file, false /*includeJSDoc*/);
            return CreateLspRangeFromBounds(start, location.End, file);
        }
        return null;
    }

    // completions.go:4251-4319 — setItemDefaults.
    internal global::Ts.LsProto.CompletionItemDefaults? SetItemDefaults(
        int position,
        SourceFile file,
        List<CompletionItem> items,
        string[]? defaultCommitCharacters,
        global::Ts.LsProto.Range? optionalReplacementSpan)
    {
        global::Ts.LsProto.CompletionItemDefaults? itemDefaults = null;
        if (defaultCommitCharacters != null)
        {
            var supportsItemCommitCharacters = ClientSupportsItemCommitCharacters();
            if (ClientSupportsDefaultCommitCharacters() && supportsItemCommitCharacters)
            {
                itemDefaults = new global::Ts.LsProto.CompletionItemDefaults
                {
                    CommitCharacters = defaultCommitCharacters,
                };
            }
            else if (supportsItemCommitCharacters)
            {
                foreach (var item in items)
                {
                    if (item.Item!.CommitCharacters == null)
                    {
                        item.Item.CommitCharacters = defaultCommitCharacters;
                    }
                }
            }
        }
        if (optionalReplacementSpan != null)
        {
            // Ported from vscode ts extension.
            var insertRange = new global::Ts.LsProto.Range
            {
                Start = optionalReplacementSpan.Start,
                End = CreateLspPosition(position, file),
            };
            if (ClientSupportsDefaultEditRange())
            {
                itemDefaults ??= new global::Ts.LsProto.CompletionItemDefaults();
                itemDefaults.EditRange = new global::Ts.LsProto.RangeOrEditRangeWithInsertReplace
                {
                    EditRangeWithInsertReplace = new global::Ts.LsProto.EditRangeWithInsertReplace
                    {
                        Insert = insertRange,
                        Replace = optionalReplacementSpan,
                    },
                };
                foreach (var item in items)
                {
                    // If `editRange` is set, `insertText` is ignored by the client, so we need to
                    // provide `textEdit` instead.
                    if (item.Item!.InsertText != null && item.Item.TextEdit == null)
                    {
                        item.Item.TextEdit = new global::Ts.LsProto.TextEditOrInsertReplaceEdit
                        {
                            InsertReplaceEdit = new global::Ts.LsProto.InsertReplaceEdit
                            {
                                NewText = item.Item.InsertText,
                                Insert = insertRange,
                                Replace = optionalReplacementSpan,
                            },
                        };
                        item.Item.InsertText = null;
                    }
                }
            }
            else if (ClientSupportsItemInsertReplace())
            {
                foreach (var item in items)
                {
                    if (item.Item!.TextEdit == null)
                    {
                        item.Item.TextEdit = new global::Ts.LsProto.TextEditOrInsertReplaceEdit
                        {
                            InsertReplaceEdit = new global::Ts.LsProto.InsertReplaceEdit
                            {
                                NewText = item.Item.InsertText ?? item.Item.Label,
                                Insert = insertRange,
                                Replace = optionalReplacementSpan,
                            },
                        };
                    }
                }
            }
        }

        return itemDefaults;
    }

    // completions.go:4321-4342 — specificKeywordCompletionInfo.
    internal CompletionList SpecificKeywordCompletionInfo(
        int position,
        SourceFile file,
        List<CompletionItem> items,
        bool isNewIdentifierLocation,
        global::Ts.LsProto.Range? optionalReplacementSpan)
    {
        var defaultCommitCharacters = GetDefaultCommitCharacters(isNewIdentifierLocation);
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

    // completions.go:4344-4424 — getJsxClosingTagCompletion.
    internal CompletionList? GetJsxClosingTagCompletion(
        Node? location,
        SourceFile file,
        int position)
    {
        // We wanna walk up the tree till we find a JSX closing element.
        var jsxClosingElement = AstUtilities.FindAncestorOrQuit(location, node =>
        {
            switch (node.Kind)
            {
                case SyntaxKind.JsxClosingElement:
                    return FindAncestorResult.True;
                case SyntaxKind.LessThanSlashToken:
                case SyntaxKind.GreaterThanToken:
                case SyntaxKind.Identifier:
                case SyntaxKind.PropertyAccessExpression:
                    return FindAncestorResult.False;
                default:
                    return FindAncestorResult.Quit;
            }
        });

        if (jsxClosingElement == null)
        {
            return null;
        }

        // In the TypeScript JSX element, if such element is not defined. When users query for completion at closing tag,
        // instead of simply giving unknown value, the completion will return the tag-name of an associated opening-element.
        // For example:
        //     var x = <div> </ /*1*/
        // The completion list at "1" will contain "div>" with type any
        // And at `<div> </ /*1*/ >` (with a closing `>`), the completion list will contain "div".
        // And at property access expressions `<MainComponent.Child> </MainComponent. /*1*/ >` the completion will
        // return full closing tag with an optional replacement span
        // For example:
        //     var x = <MainComponent.Child> </     MainComponent /*1*/  >
        //     var y = <MainComponent.Child> </   /*2*/   MainComponent >
        // the completion list at "1" and "2" will contain "MainComponent.Child" with a replacement span of closing tag name
        var hasClosingAngleBracket = global::Ts.AstNav.Tokens.FindChildOfKind(jsxClosingElement, SyntaxKind.GreaterThanToken, file) != null;
        var tagName = jsxClosingElement.Parent!.AsJsxElement().OpeningElement!.TagName();
        var closingTag = global::Ts.Scanner.Scanner.GetTextOfNode(tagName!);
        var fullClosingTag = closingTag + (hasClosingAngleBracket ? "" : ">");
        var optionalReplacementSpan = CreateLspRangeFromNode(jsxClosingElement.TagName()!, file);
        var defaultCommitCharacters = GetDefaultCommitCharacters(false /*isNewIdentifierLocation*/);

        var lspItem = CreateLSPCompletionItem(
            fullClosingTag, /*name*/
            "",             /*insertText*/
            "",             /*filterText*/
            SortTextLocationPriorityValue,
            ScriptElementKind.ClassElement,
            ScriptElementKindModifier.None, /*kindModifiers*/
            null,                           /*replacementSpan*/
            null,                           /*commitCharacters*/
            null,                           /*labelDetails*/
            file,
            position,
            true,  /*isMemberCompletion*/
            false, /*isSnippet*/
            false, /*hasAction*/
            false, /*preselect*/
            "",    /*source*/
            null,  /*autoImportEntryData*/ // !!! jsx autoimports
            null /*detail*/);
        var item = new CompletionItem
        {
            Item = lspItem,
        };
        var items = new List<CompletionItem> { item };
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

    // completions.go:5949-6081 — getExhaustiveCaseSnippets.
    internal global::Ts.LsProto.CompletionItem? GetExhaustiveCaseSnippets(
        Node caseBlock,
        SourceFile file,
        int position,
        global::Ts.Core.CompilerOptions options,
        TsChecker c)
    {
        var clauses = caseBlock.AsCaseBlock().Clauses!.Nodes;
        var switchType = c.GetTypeAtLocation(caseBlock.Parent!.Expression()!);
        if (switchType != null && switchType.IsUnion() && global::Ts.Core.CoreUtil.Every(switchType.Types(), IsLiteral))
        {
            // Collect constant values in existing clauses.
            var tracker = CaseClauseTracker.NewCaseClauseTracker(c, clauses);
            var target = options.GetEmitScriptTarget();
            var quotePreference = LsDeps.GetQuotePreference(file, UserPreferences());
            // Tolerate a nil import adder in untitled files.
            global::Ts.Ls.AutoImport.ImportAdder? importAdder = null;
            if (!global::Ts.TsPath.TsPath.IsDynamicFileName(file.FileName()))
            {
                var (view, err) = GetPreparedAutoImportView(file);
                if (err != null)
                {
                    throw err;
                }
                if (view != null)
                {
                    importAdder = global::Ts.Ls.AutoImport.AutoImportApi.NewImportAdder(
                        GetProgram(),
                        c,
                        file,
                        view,
                        FormatOptions(),
                        Converters(),
                        UserPreferences());
                }
            }

            var elements = new List<Node>();
            var factory = new global::Ts.Ast.NodeFactory(new NodeFactoryHooks());
            foreach (var t in switchType.Types())
            {
                // Enums
                if (t.IsEnumLiteral())
                {
                    Debug.Assert(t.Symbol != null, "An enum member type should have a symbol");
                    Debug.Assert(t.Symbol!.Parent != null, "An enum member type should have a parent symbol (the enum symbol)");
                    // Filter existing enums by their values
                    object? enumValue = null;
                    if (t.Symbol!.ValueDeclaration != null)
                    {
                        enumValue = c.GetConstantValue(t.Symbol!.ValueDeclaration!);
                    }
                    if (enumValue != null)
                    {
                        if (tracker.HasValue(enumValue))
                        {
                            continue;
                        }
                        tracker.AddValue(enumValue);
                    }
                    var typeNode = global::Ts.Ls.AutoImport.AutoImportApi.TypeToAutoImportableTypeNode(c, importAdder!, t, caseBlock);
                    if (typeNode == null)
                    {
                        return null;
                    }
                    var expr = TypeNodeToExpression(typeNode, target, quotePreference, factory);
                    if (expr == null)
                    {
                        return null;
                    }
                    elements.Add(expr);
                }
                else
                {
                    var value = t.AsLiteralType().Value();
                    if (value != null && !tracker.HasValue(value))
                    {
                        switch (value)
                        {
                            case global::Ts.JsNum.PseudoBigInt v:
                            {
                                Node bigInt;
                                if (v.Negative)
                                {
                                    var abs = v with { Negative = false };
                                    bigInt = factory.NewPrefixUnaryExpression(SyntaxKind.MinusToken, factory.NewBigIntLiteral(abs.ToString() + "n", TokenFlags.None));
                                }
                                else
                                {
                                    bigInt = factory.NewBigIntLiteral(v.ToString() + "n", TokenFlags.None);
                                }
                                elements.Add(bigInt);
                                break;
                            }
                            case global::Ts.JsNum.Number v:
                            {
                                Node number;
                                if (v < 0)
                                {
                                    number = factory.NewPrefixUnaryExpression(SyntaxKind.MinusToken, factory.NewNumericLiteral(v.Abs().ToString(), TokenFlags.None));
                                }
                                else
                                {
                                    number = factory.NewNumericLiteral(v.ToString(), TokenFlags.None);
                                }
                                elements.Add(number);
                                break;
                            }
                            case string v:
                            {
                                var literal = factory.NewStringLiteral(v, quotePreference == QuotePreference.Single ? TokenFlags.SingleQuote : TokenFlags.None);
                                elements.Add(literal);
                                break;
                            }
                        }
                    }
                }
            }
            if (elements.Count == 0)
            {
                return null;
            }

            var newClauses = global::Ts.Core.CoreUtil.Map(elements.ToArray(), element =>
                factory.NewCaseOrDefaultClause(SyntaxKind.CaseClause, element, factory.NewNodeList([])))!;
            var newLineChar = FormatOptions().NewLineCharacter;
            var printer = SnippetPrinter.CreateSnippetPrinter(new global::Ts.Printer.PrinterOptions
            {
                RemoveComments = true,
                NewLine = global::Ts.Core.Core.GetNewLineKind(newLineChar),
            });
            var formatCtx = global::Ts.Format.FormatContext.WithFormatCodeSettings(FormatOptions(), newLineChar);
            global::System.Func<Node, string> printNode = node => printer.PrintAndFormatNode(formatCtx, node, file);
            var insertText = string.Join(newLineChar, global::Ts.Core.CoreUtil.MapIndex(newClauses, (clause, i) =>
            {
                if (ClientSupportsItemSnippet())
                {
                    return printNode(clause) + "$" + (i + 1);
                }
                return printer.PrintUnescapedNode(clause);
            })!);

            var firstClause = printer.PrintUnescapedNode(newClauses[0]);
            var name = firstClause + " ...";

            global::Ts.LsProto.TextEdit[]? additionalTextEdits = null;
            if (importAdder != null)
            {
                additionalTextEdits = importAdder.Edits();
            }

            return new global::Ts.LsProto.CompletionItem
            {
                Label = name,
                Kind = global::Ts.LsProto.CompletionItemKind.Snippet,
                SortText = SortTextGlobalsOrKeywordsValue,
                InsertText = insertText,
                AdditionalTextEdits = additionalTextEdits,
                InsertTextFormat = ClientSupportsItemSnippet() ? global::Ts.LsProto.InsertTextFormat.Snippet : null,
                Data = new global::Ts.LsProto.CompletionItemData
                {
                    FileName = file.FileName(),
                    Position = position,
                    Name = name,
                    Source = CompletionSource.SwitchCases,
                },
            };
        }
        return null;
    }
}
