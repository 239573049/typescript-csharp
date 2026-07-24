// Ported from tsgo internal/ls/completions.go — createCompletionItem (:2037-2298) and
// getCompletionEntriesFromSymbols (:1863-2005), the per-symbol completion item construction
// pipeline between completionInfoFromData and createLSPCompletionItem.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:2037-2298 — createCompletionItem.
    internal global::Ts.LsProto.CompletionItem? CreateCompletionItem(
        TsChecker typeChecker,
        Symbol symbol,
        string sortText,
        Node? replacementToken,
        CompletionDataData data,
        int position,
        SourceFile file,
        string name,
        bool needsConvertPropertyAccess,
        SymbolOriginInfo? origin,
        bool useSemicolons,
        global::Ts.Core.CompilerOptions compilerOptions,
        bool isMemberCompletion)
    {
        var contextToken = data.contextToken;
        var insertText = "";
        var filterText = "";
        var replacementSpan = GetReplacementRangeForContextToken(file, replacementToken, position);
        bool isSnippet = false, hasAction = false;
        var source = GetSourceFromOrigin(origin);
        global::Ts.LsProto.CompletionItemLabelDetails? labelDetails = null;
        var preferences = UserPreferences();
        var insertQuestionDot = OriginIsNullableMember(origin);
        var useBraces = OriginIsSymbolMember(origin) || needsConvertPropertyAccess;
        if (OriginIsThisTypeNode(origin))
        {
            if (needsConvertPropertyAccess)
            {
                insertText = $"this{(insertQuestionDot ? "?." : "")}[{QuotePropertyName(file, preferences, name)}]";
            }
            else
            {
                insertText = $"this{(insertQuestionDot ? "?." : ".")}{name}";
            }
        }
        else if (data.propertyAccessToConvert != null && (useBraces || insertQuestionDot))
        {
            // We should only have needsConvertPropertyAccess if there's a property access to convert. But see microsoft/TypeScript#21790.
            // Somehow there was a global with a non-identifier name. Hopefully someone will complain about getting a "foo bar" global completion and provide a repro.
            if (useBraces)
            {
                if (needsConvertPropertyAccess)
                {
                    insertText = $"[{QuotePropertyName(file, preferences, name)}]";
                }
                else
                {
                    insertText = $"[{name}]";
                }
            }
            else
            {
                insertText = name;
            }

            if (insertQuestionDot || data.propertyAccessToConvert.QuestionDotToken() != null)
            {
                insertText = "?." + insertText;
            }

            var dot = global::Ts.AstNav.Tokens.FindChildOfKind(data.propertyAccessToConvert, SyntaxKind.DotToken, file);
            if (dot == null)
            {
                dot = global::Ts.AstNav.Tokens.FindChildOfKind(data.propertyAccessToConvert, SyntaxKind.QuestionDotToken, file);
            }

            if (dot == null)
            {
                return null;
            }

            // If the text after the '.' starts with this name, write over it. Else, add new text.
            int end;
            if (name.StartsWith(data.propertyAccessToConvert.Name()!.Text(), global::System.StringComparison.Ordinal))
            {
                end = data.propertyAccessToConvert.End;
            }
            else
            {
                end = dot.End;
            }
            replacementSpan = CreateLspRangeFromBounds(global::Ts.AstNav.Tokens.GetStartOfNode(dot, file, false /*includeJSDoc*/), end, file);
        }

        if (data.jsxInitializer.isInitializer)
        {
            if (insertText.Length == 0)
            {
                insertText = name;
            }
            insertText = $"{{{insertText}}}";
            if (data.jsxInitializer.initializer != null)
            {
                replacementSpan = CreateLspRangeFromNode(data.jsxInitializer.initializer, file);
            }
        }

        if (OriginIsPromise(origin) && data.propertyAccessToConvert != null)
        {
            if (insertText.Length == 0)
            {
                insertText = name;
            }
            var precedingToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, data.propertyAccessToConvert.Pos);
            var awaitText = "";
            if (precedingToken != null && Asi.PositionIsASICandidate(precedingToken.End, precedingToken.Parent!, file))
            {
                awaitText = ";";
            }

            awaitText += "(await " + global::Ts.Scanner.Scanner.GetTextOfNode(data.propertyAccessToConvert.Expression()!) + ")";
            if (needsConvertPropertyAccess)
            {
                insertText = awaitText + insertText;
            }
            else
            {
                var dotStr = insertQuestionDot ? "?." : ".";
                insertText = awaitText + dotStr + insertText;
            }
            var isInAwaitExpression = NodeTests.IsAwaitExpression(data.propertyAccessToConvert.Parent!);
            var wrapNode = isInAwaitExpression
                ? data.propertyAccessToConvert.Parent!
                : data.propertyAccessToConvert.Expression()!;
            replacementSpan = CreateLspRangeFromBounds(
                global::Ts.AstNav.Tokens.GetStartOfNode(wrapNode, file, false /*includeJSDoc*/),
                data.propertyAccessToConvert.End,
                file);
        }

        if (OriginIsTypeOnlyAlias(origin))
        {
            hasAction = true;
        }

        // Provide object member completions when missing commas, and insert missing commas.
        // For example:
        //
        //    interface I {
        //        a: string;
        //        b: number
        //     }
        //
        //     const cc: I = { a: "red" | }
        //
        // Completion should add a comma after "red" and provide completions for b
        if (data.completionKind == CompletionKind.ObjectPropertyDeclaration &&
            contextToken != null &&
            !AstUtilities.NodeHasKind(global::Ts.AstNav.Tokens.FindPrecedingTokenEx(file, contextToken.Pos, contextToken, false /*excludeJSDoc*/), SyntaxKind.CommaToken))
        {
            if (NodeTests.IsMethodDeclaration(contextToken.Parent!.Parent!) ||
                NodeTests.IsGetAccessorDeclaration(contextToken.Parent.Parent!) ||
                NodeTests.IsSetAccessorDeclaration(contextToken.Parent.Parent!) ||
                NodeTests.IsSpreadAssignment(contextToken.Parent) ||
                LsDeps.GetLastToken(AstUtilities.FindAncestor(contextToken.Parent, NodeTests.IsPropertyAssignment), file) == contextToken ||
                NodeTests.IsShorthandPropertyAssignment(contextToken.Parent) &&
                    LsDeps.GetLineOfPosition(file, contextToken.End) != LsDeps.GetLineOfPosition(file, position))
            {
                source = CompletionSource.ObjectLiteralMemberWithComma;
                hasAction = true;
            }
        }

        if (preferences.IncludeCompletionsWithClassMemberSnippets.IsTrue() &&
            data.completionKind == CompletionKind.MemberLike &&
            IsClassLikeMemberCompletion(symbol, data.location!, file))
        {
            // !!! class member completions
        }

        if (OriginIsObjectLiteralMethod(origin))
        {
            insertText = origin!.AsObjectLiteralMethod().insertText;
            isSnippet = origin.AsObjectLiteralMethod().isSnippet;
            labelDetails = (global::Ts.LsProto.CompletionItemLabelDetails?)origin.AsObjectLiteralMethod().labelDetails; // !!! check if this can conflict with case above where we set label details
            if (!ClientSupportsItemLabelDetails())
            {
                name += labelDetails!.Detail;
                labelDetails = null;
            }
            source = CompletionSource.ObjectLiteralMethodSnippet;
            sortText = SortBelow(sortText);
        }

        if (data.isJsxIdentifierExpected &&
            !data.isRightOfOpenTag &&
            ClientSupportsItemSnippet() &&
            preferences.JsxAttributeCompletionStyle != "none" &&
            !(data.location!.Parent != null && NodeTests.IsJsxAttribute(data.location.Parent) && data.location.Parent.Initializer() != null))
        {
            var useJsxBraces = preferences.JsxAttributeCompletionStyle == "braces";
            var t = typeChecker.GetTypeOfSymbolAtLocation(symbol, data.location);

            // If is boolean like or undefined, don't return a snippet, we want to return just the completion.
            if (preferences.JsxAttributeCompletionStyle == "auto" &&
                !t.IsBooleanLike() &&
                !(t.IsUnion() && global::Ts.Core.CoreUtil.Some(t.Types(), u => u.IsBooleanLike())))
            {
                if (t.IsStringLike() ||
                    t.IsUnion() &&
                        global::Ts.Core.CoreUtil.Every(
                            t.Types(),
                            u => (u.Flags & (global::Ts.Checker.TypeFlags.StringLike | global::Ts.Checker.TypeFlags.Undefined)) != 0 ||
                                IsStringAndEmptyAnonymousObjectIntersection(typeChecker, u)))
                {
                    // If type is string-like or undefined, use quotes.
                    insertText = $"{EscapeSnippetText(name)}={Quote(file, preferences, "$1")}";
                    isSnippet = true;
                }
                else
                {
                    // Use braces for everything else.
                    useJsxBraces = true;
                }
            }

            if (useJsxBraces)
            {
                insertText = EscapeSnippetText(name) + "={$1}";
                isSnippet = true;
            }
        }

        var parentNamedImportOrExport = AstUtilities.FindAncestor(data.location!, IsNamedImportsOrExports);
        if (parentNamedImportOrExport != null)
        {
            if (!global::Ts.Scanner.Scanner.IsIdentifierText(name, global::Ts.Core.LanguageVariant.Standard))
            {
                insertText = QuotePropertyName(file, preferences, name);

                if (parentNamedImportOrExport.Kind == SyntaxKind.NamedImports)
                {
                    // Check if it is `import { ^here as name } from '...'``.
                    // We have to access the scanner here to check if it is `{ ^here as name }`` or `{ ^here, as, name }`.
                    var scanner = new global::Ts.Scanner.Scanner();
                    scanner.SetText(file.Text());
                    scanner.ResetPos(position);
                    if (!(scanner.Scan() == SyntaxKind.AsKeyword && scanner.Scan() == SyntaxKind.Identifier))
                    {
                        insertText += " as " + GenerateIdentifierForArbitraryString(name);
                    }
                }
            }
            else if (parentNamedImportOrExport.Kind == SyntaxKind.NamedImports)
            {
                var possibleToken = global::Ts.Scanner.Scanner.StringToToken(name);
                if (possibleToken != SyntaxKind.Unknown &&
                    (possibleToken == SyntaxKind.AwaitKeyword || LsDeps.IsNonContextualKeyword(possibleToken)))
                {
                    insertText = $"{name} as {name}_";
                }
            }
        }

        // Commit characters

        var elementKind = SymbolDisplay.GetSymbolKind(typeChecker, symbol, data.location!);
        string[]? commitCharacters = null;
        if (ClientSupportsItemCommitCharacters())
        {
            if (elementKind == ScriptElementKind.Warning || elementKind == ScriptElementKind.String)
            {
                commitCharacters = [];
            }
            else if (!ClientSupportsDefaultCommitCharacters())
            {
                commitCharacters = data.defaultCommitCharacters;
            }
            // Otherwise use the completion list default.
        }

        var preselect = IsRecommendedCompletionMatch(symbol, data.recommendedCompletion, typeChecker);
        var kindModifiers = SymbolDisplay.GetSymbolModifiers(typeChecker, symbol);

        return CreateLSPCompletionItem(
            name,
            insertText,
            filterText,
            sortText,
            elementKind,
            kindModifiers,
            replacementSpan,
            commitCharacters,
            labelDetails,
            file,
            position,
            isMemberCompletion,
            isSnippet,
            hasAction,
            preselect,
            source,
            null, /*autoImportFix*/
            null /*detail*/);
    }

    // completions.go:1863-2005 — getCompletionEntriesFromSymbols.
    internal (HashSet<string> UniqueNames, List<CompletionItem> SortedEntries) GetCompletionEntriesFromSymbols(
        TsChecker typeChecker,
        CompletionDataData data,
        Node? replacementToken,
        int position,
        SourceFile file,
        global::Ts.Core.CompilerOptions compilerOptions,
        bool includeSymbols)
    {
        var sortedEntries = new List<CompletionItem>();
        var closestSymbolDeclaration = GetClosestSymbolDeclaration(data.contextToken, data.location!);
        var useSemicolons = Asi.ProbablyUsesSemicolons(file);
        var isMemberCompletion = IsMemberCompletionKind(data.completionKind);
        // Tracks unique names.
        // Value is set to false for global variables or completions from external module exports, because we can have multiple of those;
        // true otherwise. Based on the order we add things we will always see locals first, then globals, then module exports.
        // So adding a completion for a local will prevent us from adding completions for external module exports sharing the same name.
        var uniques = new Dictionary<string, bool>(global::System.StringComparer.Ordinal);
        var symbols = data.symbols ?? [];
        for (var index = 0; index < symbols.Length; index++)
        {
            var symbol = symbols[index];
            SymbolOriginInfo? origin = null;
            data.symbolToOriginInfoMap?.TryGetValue(index, out origin);
            var (name, needsConvertPropertyAccess) = GetCompletionEntryDisplayNameForSymbol(
                symbol,
                origin,
                data.completionKind,
                data.isJsxIdentifierExpected);
            if (name.Length == 0 ||
                uniques.TryGetValue(name, out var isUnique) && isUnique && (origin == null || !OriginIsObjectLiteralMethod(origin)) ||
                data.completionKind == CompletionKind.Global &&
                    !ShouldIncludeSymbol(symbol, data, closestSymbolDeclaration, file, typeChecker, compilerOptions))
            {
                continue;
            }

            // When in a value location in a JS file, ignore symbols that definitely seem to be type-only.
            if (!data.isTypeOnlyLocation && AstUtilities.IsSourceFileJS(file) && SymbolAppearsToBeTypeOnly(symbol, typeChecker))
            {
                continue;
            }

            var originalSortText = "";
            data.symbolToSortTextMap?.TryGetValue(AstUtilities.GetSymbolId(symbol), out originalSortText);
            if (string.IsNullOrEmpty(originalSortText))
            {
                originalSortText = SortTextLocationPriorityValue;
            }

            string sortText;
            if (IsDeprecated(symbol, typeChecker))
            {
                sortText = DeprecateSortText(originalSortText!);
            }
            else
            {
                sortText = originalSortText!;
            }
            var entry = CreateCompletionItem(
                typeChecker,
                symbol,
                sortText,
                replacementToken,
                data,
                position,
                file,
                name,
                needsConvertPropertyAccess,
                origin,
                useSemicolons,
                compilerOptions,
                isMemberCompletion);
            if (entry == null)
            {
                continue;
            }

            // True for locals; false for globals, module exports from other files, `this.` completions.
            var shouldShadowLaterSymbols = (origin == null || OriginIsTypeOnlyAlias(origin)) &&
                !(symbol.Parent == null &&
                    !global::Ts.Core.CoreUtil.Some(symbol.Declarations, d => AstUtilities.GetSourceFileOfNode(d) == file));
            uniques[name] = shouldShadowLaterSymbols;
            Symbol? sym = null;
            if (includeSymbols)
            {
                sym = symbol;
            }
            sortedEntries.Add(new CompletionItem { Item = entry, Symbol = sym });
        }

        // completions.go:1945-1998 — auto-import entries.
        foreach (var autoImport in data.autoImports ?? [])
        {
            // !!! check for type-only in JS
            // !!! deprecation

            if (data.importStatementCompletion != null)
            {
                // !!!
                continue;
            }

            // Non-contextual keywords (e.g., `function`, `class`, `const`) cannot be used as identifiers,
            // so auto-imports with these names should not shadow keyword completions.
            var token = global::Ts.Scanner.Scanner.StringToToken(autoImport.Fix.Name);
            if (token != SyntaxKind.Unknown && AstUtilities.IsNonContextualKeyword(token))
            {
                continue;
            }

            if (!autoImport.Export.IsUnresolvedAlias())
            {
                if (data.isTypeOnlyLocation)
                {
                    if ((autoImport.Export.Flags & SymbolFlags.Type) == 0 && (autoImport.Export.Flags & SymbolFlags.Module) == 0)
                    {
                        continue;
                    }
                }
                else if ((autoImport.Export.Flags & SymbolFlags.Value) == 0)
                {
                    continue;
                }
            }

            var autoImportEntry = CreateLSPCompletionItem(
                autoImport.Fix.Name,
                "",
                "",
                SortTextAutoImportSuggestionsValue,
                autoImport.Export.ScriptElementKind,
                autoImport.Export.ScriptElementKindModifiers,
                null,
                null,
                new global::Ts.LsProto.CompletionItemLabelDetails
                {
                    Description = autoImport.Fix.ModuleSpecifier,
                },
                file,
                position,
                false, /*isMemberCompletion*/
                false, /*isSnippet*/
                true,  /*hasAction*/
                false, /*preselect*/
                autoImport.Fix.ModuleSpecifier,
                autoImport.Fix.AutoImportFix,
                null /*detail*/);

            if (!uniques.TryGetValue(autoImport.Fix.Name, out var isShadowed) || !isShadowed)
            {
                uniques[autoImport.Fix.Name] = false;
                sortedEntries.Add(new CompletionItem { Item = autoImportEntry });
            }
        }

        var uniqueSet = new HashSet<string>(uniques.Count, global::System.StringComparer.Ordinal);
        foreach (var (uniqueName, _) in uniques) // map-iter: 序无关 (set materialization)
        {
            uniqueSet.Add(uniqueName);
        }
        return (uniqueSet, sortedEntries);
    }
}
