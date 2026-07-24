// Ported from tsgo internal/ls/completions.go — getCompletionData (completions.go:454-1713),
// the single closure-structured aggregation function behind getCompletionsAtPosition.
// PORT-NOTE: the collectAutoImports arm (completions.go:1131-1160) needs the autoimport package
// (its own unit); until it lands the arm is a structural no-op and `autoImports` stays empty.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using SymbolTable = System.Collections.Generic.Dictionary<string, Ts.Ast.Symbol>;
using CheckerType = Ts.Checker.Type;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:454-1713 — getCompletionData.
    internal ICompletionData? GetCompletionData(
        TsChecker typeChecker,
        SourceFile file,
        int position,
        UserPreferences preferences,
        bool forItemResolve)
    {
        var inCheckedFile = IsCheckedFile(file, GetProgram().Options());

        var currentToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(file, position);

        var insideComment = LsDeps.IsInComment(file, position, currentToken);

        var insideJSDocTagTypeExpression = false;
        var insideJsDocImportTag = false;
        var isInSnippetScope = false;
        if (insideComment != null)
        {
            if (HasDocComment(file, position))
            {
                if (position > 0 && file.Text()[position - 1] == '@')
                {
                    // The current position is next to the '@' sign, when no tag name being provided yet.
                    // Provide a full list of tag names
                    return new CompletionDataJSDocTagName();
                }
                else
                {
                    // When completion is requested without "@", we will have check to make sure that
                    // there are no comments prefix the request position. We will only allow "*" and space.
                    // e.g
                    //   /** |c| /*
                    //
                    //   /**
                    //     |c|
                    //    */
                    //
                    //   /**
                    //    * |c|
                    //    */
                    //
                    //   /**
                    //    *         |c|
                    //    */
                    var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(position, file);
                    var noCommentPrefix = true;
                    for (var i = lineStart; i < position; i++)
                    {
                        var r = file.Text()[i];
                        if (!(global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(r) || r == '*' || r == '/' || r == '(' || r == ')' || r == '|'))
                        {
                            noCommentPrefix = false;
                            break;
                        }
                    }
                    if (noCommentPrefix)
                    {
                        return new CompletionDataJSDocTag();
                    }
                }
            }

            // Completion should work inside certain JSDoc tags. For example:
            //     /** @type {number | string} */
            // Completion should work in the brackets
            if (GetJSDocTagAtPosition(currentToken, position) is { } tag)
            {
                if (tag.TagName()!.Pos <= position && position <= tag.TagName()!.End)
                {
                    return new CompletionDataJSDocTagName();
                }
                if (NodeTests.IsJSDocImportTag(tag))
                {
                    insideJsDocImportTag = true;
                }
                else
                {
                    if (TryGetTypeExpressionFromTag(tag) is { } typeExpression)
                    {
                        currentToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(file, position);
                        if (currentToken == null ||
                            (!AstUtilities.IsDeclarationName(currentToken) &&
                                (currentToken.Parent!.Kind != SyntaxKind.JSDocPropertyTag ||
                                    currentToken.Parent.Name() != currentToken)))
                        {
                            // Use as type location if inside tag's type expression
                            insideJSDocTagTypeExpression = IsCurrentlyEditingNode(typeExpression, file, position);
                        }
                    }
                    if (!insideJSDocTagTypeExpression &&
                        NodeTests.IsJSDocParameterTag(tag) &&
                        (AstUtilities.NodeIsMissing(tag.Name()) || tag.Name()!.Pos <= position && position <= tag.Name()!.End))
                    {
                        return new CompletionDataJSDocParameterName
                        {
                            tag = tag,
                        };
                    }
                }
            }

            if (!insideJSDocTagTypeExpression && !insideJsDocImportTag)
            {
                // Proceed if the current position is in JSDoc tag expression; otherwise it is a normal
                // comment or the plain text part of a JSDoc comment, so no completion should be available
                return null;
            }
        }

        // The decision to provide completion depends on the contextToken, which is determined through the previousToken.
        // Note: 'previousToken' (and thus 'contextToken') can be undefined if we are the beginning of the file
        var isJSOnlyLocation = !insideJSDocTagTypeExpression && !insideJsDocImportTag && AstUtilities.IsSourceFileJS(file);
        var (contextToken, previousToken) = GetRelevantTokens(position, file);

        // Find the node where completion is requested on.
        // Also determine whether we are trying to complete with members of that node
        // or attributes of a JSX tag.
        var node = currentToken;
        Node? propertyAccessToConvert = null;
        var isRightOfDot = false;
        var isRightOfQuestionDot = false;
        var isRightOfOpenTag = false;
        var isStartingCloseTag = false;
        var jsxInitializer = new JsxInitializerInfo();
        var isJsxIdentifierExpected = false;
        ImportStatementCompletionInfo? importStatementCompletion = null;
        var location = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);
        var keywordFilters = KeywordCompletionFilters.None;
        var isNewIdentifierLocation = false;
        // !!! flags := CompletionInfoFlagsNone
        string[]? defaultCommitCharacters = null;

        if (contextToken != null)
        {
            var importStatementCompletionInfo = GetImportStatementCompletionInfo(contextToken, file);
            if (importStatementCompletionInfo.keywordCompletion != SyntaxKind.Unknown)
            {
                if (importStatementCompletionInfo.isKeywordOnlyCompletion)
                {
                    return new CompletionDataKeyword(
                        [new CompletionItem
                        {
                            Item = new global::Ts.LsProto.CompletionItem
                            {
                                Label = global::Ts.Scanner.Scanner.TokenToString(importStatementCompletionInfo.keywordCompletion),
                                Kind = global::Ts.LsProto.CompletionItemKind.Keyword,
                                SortText = SortTextGlobalsOrKeywordsValue,
                            },
                        }],
                        importStatementCompletionInfo.isNewIdentifierLocation);
                }
                keywordFilters = KeywordFiltersFromSyntaxKind(importStatementCompletionInfo.keywordCompletion);
            }
            if (importStatementCompletionInfo.replacementSpan != null && preferences.IncludeCompletionsForImportStatements.IsTrue())
            {
                // !!! flags |= CompletionInfoFlags.IsImportStatementCompletion;
                importStatementCompletion = importStatementCompletionInfo;
                isNewIdentifierLocation = importStatementCompletionInfo.isNewIdentifierLocation;
            }
            // Bail out if this is a known invalid completion location.
            if (IsCompletionListBlocker(contextToken, previousToken, location, file, position, typeChecker))
            {
                if (keywordFilters != KeywordCompletionFilters.None)
                {
                    var (blockedIsNewIdentifierLocation, _) = ComputeCommitCharactersAndIsNewIdentifier(contextToken, file, position);
                    return KeywordCompletionData(keywordFilters, isJSOnlyLocation, blockedIsNewIdentifierLocation);
                }
                return null;
            }

            var parent = contextToken.Parent!;
            if (contextToken.Kind == SyntaxKind.DotToken || contextToken.Kind == SyntaxKind.QuestionDotToken)
            {
                isRightOfDot = contextToken.Kind == SyntaxKind.DotToken;
                isRightOfQuestionDot = contextToken.Kind == SyntaxKind.QuestionDotToken;
                switch (parent.Kind)
                {
                    case SyntaxKind.PropertyAccessExpression:
                        propertyAccessToConvert = parent;
                        node = propertyAccessToConvert.Expression()!;
                        var leftMostAccessExpression = AstUtilities.GetLeftmostAccessExpression(parent);
                        if (AstUtilities.NodeIsMissing(leftMostAccessExpression) ||
                            ((NodeTests.IsCallExpression(node) || AstUtilities.IsFunctionLike(node)) &&
                                node.End == contextToken.Pos &&
                                LsDeps.GetLastChild(node, file)?.Kind != SyntaxKind.CloseParenToken))
                        {
                            // This is likely dot from incorrectly parsed expression and user is starting to write spread
                            // eg: Math.min(./**/)
                            // const x = function (./**/) {}
                            // ({./**/})
                            return null;
                        }
                        break;
                    case SyntaxKind.QualifiedName:
                        node = parent.AsQualifiedName().Left!;
                        break;
                    case SyntaxKind.ModuleDeclaration:
                        node = parent.Name()!;
                        break;
                    case SyntaxKind.ImportType:
                        node = parent;
                        break;
                    case SyntaxKind.MetaProperty:
                        node = LsDeps.GetFirstToken(parent, file)!;
                        if (node.Kind != SyntaxKind.ImportKeyword && node.Kind != SyntaxKind.NewKeyword)
                        {
                            throw new global::System.InvalidOperationException("Unexpected token kind: " + node.Kind);
                        }
                        break;
                    default:
                        // There is nothing that precedes the dot, so this likely just a stray character
                        // or leading into a '...' token. Just bail out instead.
                        return null;
                }
            }
            else
            { // !!! else if (!importStatementCompletion)
                // <UI.Test /* completion position */ />
                // If the tagname is a property access expression, we will then walk up to the top most of property access expression.
                // Then, try to get a JSX container and its associated attributes type.
                if (parent != null && parent.Kind == SyntaxKind.PropertyAccessExpression)
                {
                    contextToken = parent;
                    parent = parent.Parent;
                }

                // Fix location
                if (parent == location)
                {
                    switch (currentToken!.Kind)
                    {
                        case SyntaxKind.GreaterThanToken:
                            if (parent!.Kind == SyntaxKind.JsxElement || parent.Kind == SyntaxKind.JsxOpeningElement)
                            {
                                location = currentToken;
                            }
                            break;
                        case SyntaxKind.LessThanSlashToken:
                            if (parent!.Kind == SyntaxKind.JsxSelfClosingElement)
                            {
                                location = currentToken;
                            }
                            break;
                    }
                }

                switch (parent!.Kind)
                {
                    case SyntaxKind.JsxClosingElement:
                        if (contextToken.Kind == SyntaxKind.LessThanSlashToken)
                        {
                            isStartingCloseTag = true;
                            location = contextToken;
                        }
                        break;
                    case SyntaxKind.BinaryExpression:
                        if (!BinaryExpressionMayBeOpenTag(parent.AsBinaryExpression()))
                        {
                            break;
                        }
                        goto case SyntaxKind.JsxSelfClosingElement;
                    case SyntaxKind.JsxSelfClosingElement:
                    case SyntaxKind.JsxElement:
                    case SyntaxKind.JsxOpeningElement:
                        isJsxIdentifierExpected = true;
                        if (contextToken.Kind == SyntaxKind.LessThanToken)
                        {
                            isRightOfOpenTag = true;
                            location = contextToken;
                        }
                        break;
                    case SyntaxKind.JsxExpression:
                    case SyntaxKind.JsxSpreadAttribute:
                        // First case is for `<div foo={true} [||] />` or `<div foo={true} [||] ></div>`,
                        // `parent` will be `{true}` and `previousToken` will be `}`.
                        // Second case is for `<div foo={true} t[||] ></div>`.
                        // Second case must not match for `<div foo={undefine[||]}></div>`.
                        if (previousToken!.Kind == SyntaxKind.CloseBraceToken ||
                            previousToken.Kind == SyntaxKind.Identifier && previousToken.Parent!.Kind == SyntaxKind.JsxAttribute)
                        {
                            isJsxIdentifierExpected = true;
                        }
                        break;
                    case SyntaxKind.JsxAttribute:
                        // For `<div className="x" [||] ></div>`, `parent` will be JsxAttribute and `previousToken` will be its initializer.
                        if (parent.Initializer() == previousToken && previousToken!.End < position)
                        {
                            isJsxIdentifierExpected = true;
                        }
                        else
                        {
                            switch (previousToken!.Kind)
                            {
                                case SyntaxKind.EqualsToken:
                                    jsxInitializer.isInitializer = true;
                                    break;
                                case SyntaxKind.Identifier:
                                    isJsxIdentifierExpected = true;
                                    // For `<div x=[|f/**/|]`, `parent` will be `x` and `previousToken.parent` will be `f` (which is its own JsxAttribute).
                                    // Note for `<div someBool f>` we don't want to treat this as a jsx inializer, instead it's the attribute name.
                                    if (parent != previousToken.Parent &&
                                        parent.Initializer() == null &&
                                        global::Ts.AstNav.Tokens.FindChildOfKind(parent, SyntaxKind.EqualsToken, file) != null)
                                    {
                                        jsxInitializer.initializer = previousToken;
                                    }
                                    break;
                            }
                        }
                        break;
                }
            }
        }

        var completionKind = CompletionKind.None;
        var hasUnresolvedAutoImports = false;
        // This also gets mutated in nested-functions after the return
        var symbols = new List<Symbol>();
        List<object>? autoImports = null;
        // Keys are indexes of `symbols`.
        var symbolToOriginInfoMap = new Dictionary<int, SymbolOriginInfo>();
        var symbolToSortTextMap = new Dictionary<SymbolId, string>();
        var seenPropertySymbols = new HashSet<SymbolId>();
        var isTypeOnlyLocation = insideJSDocTagTypeExpression || insideJsDocImportTag ||
            importStatementCompletion != null && location.Parent != null && TsChecker.IsTypeOnlyImportOrExportDeclaration(location.Parent) ||
            !IsContextTokenValueLocation(contextToken) &&
                (contextToken != null && IsPossiblyTypeArgumentPosition(contextToken, file, typeChecker) ||
                    AstUtilities.IsPartOfTypeNode(location) ||
                    IsContextTokenTypeLocation(contextToken));

        void AddSymbolOriginInfo(Symbol symbol, bool insertQuestionDot, bool insertAwait)
        {
            var symbolId = AstUtilities.GetSymbolId(symbol);
            if (insertAwait && seenPropertySymbols.Add(symbolId))
            {
                symbolToOriginInfoMap[symbols.Count - 1] = new SymbolOriginInfo { kind = GetNullableSymbolOriginInfoKind(SymbolOriginInfoKind.Promise, insertQuestionDot) };
            }
            else if (insertQuestionDot)
            {
                symbolToOriginInfoMap[symbols.Count - 1] = new SymbolOriginInfo { kind = SymbolOriginInfoKind.Nullable };
            }
        }

        void AddSymbolSortInfo(Symbol symbol)
        {
            var symbolId = AstUtilities.GetSymbolId(symbol);
            if (IsStaticProperty(symbol))
            {
                symbolToSortTextMap[symbolId] = SortTextLocalDeclarationPriorityValue;
            }
        }

        void AddPropertySymbol(Symbol symbol, bool insertAwait, bool insertQuestionDot)
        {
            // For a computed property with an accessible name like `Symbol.iterator`,
            // we'll add a completion for the *name* `Symbol` instead of for the property.
            // If this is e.g. [Symbol.iterator], add a completion for `Symbol`.
            var computedPropertyName = global::Ts.Core.CoreUtil.FirstNonNil(symbol.Declarations, decl =>
            {
                var name = AstUtilities.GetNameOfDeclaration(decl);
                if (name != null && name.Kind == SyntaxKind.ComputedPropertyName)
                {
                    return name;
                }
                return null;
            });

            if (computedPropertyName != null)
            {
                var leftMostName = GetLeftMostName(computedPropertyName.Expression()); // The completion is for `Symbol`, not `iterator`.
                Symbol? nameSymbol = null;
                if (leftMostName != null)
                {
                    nameSymbol = typeChecker.GetSymbolAtLocation(leftMostName);
                }
                // If this is nested like for `namespace N { export const sym = Symbol(); }`, we'll add the completion for `N`.
                Symbol? firstAccessibleSymbol = null;
                if (nameSymbol != null)
                {
                    firstAccessibleSymbol = GetFirstSymbolInChain(nameSymbol, contextToken, typeChecker);
                }
                var firstAccessibleSymbolId = default(SymbolId);
                if (firstAccessibleSymbol != null)
                {
                    firstAccessibleSymbolId = AstUtilities.GetSymbolId(firstAccessibleSymbol);
                }
                if (firstAccessibleSymbolId != default && seenPropertySymbols.Add(firstAccessibleSymbolId))
                {
                    symbols.Add(firstAccessibleSymbol!);
                    symbolToSortTextMap[firstAccessibleSymbolId] = SortTextGlobalsOrKeywordsValue;
                    var moduleSymbol = firstAccessibleSymbol!.Parent;
                    if (moduleSymbol == null ||
                        !TsChecker.IsExternalModuleSymbol(moduleSymbol) ||
                        typeChecker.TryGetMemberInModuleExportsAndProperties(firstAccessibleSymbol.Name, moduleSymbol) != firstAccessibleSymbol)
                    {
                        symbolToOriginInfoMap[symbols.Count - 1] = new SymbolOriginInfo { kind = GetNullableSymbolOriginInfoKind(SymbolOriginInfoKind.SymbolMember, insertQuestionDot) };
                    }
                    else
                    {
                        // !!! auto-import symbol
                    }
                }
                else if (firstAccessibleSymbolId == default || !seenPropertySymbols.Contains(firstAccessibleSymbolId))
                {
                    symbols.Add(symbol);
                    AddSymbolOriginInfo(symbol, insertQuestionDot, insertAwait);
                    AddSymbolSortInfo(symbol);
                }
            }
            else
            {
                symbols.Add(symbol);
                AddSymbolOriginInfo(symbol, insertQuestionDot, insertAwait);
                AddSymbolSortInfo(symbol);
            }
        }

        void AddTypeProperties(CheckerType t, bool insertAwait, bool insertQuestionDot)
        {
            if (typeChecker.GetStringIndexType(t) != null)
            {
                isNewIdentifierLocation = true;
                defaultCommitCharacters = [];
            }
            if (isRightOfQuestionDot && typeChecker.GetCallSignatures(t).Length != 0)
            {
                isNewIdentifierLocation = true;
                defaultCommitCharacters ??= (string[])AllCommitCharacters.Clone(); // Only invalid commit character here would be `(`.
            }

            Node propertyAccess;
            if (node!.Kind == SyntaxKind.ImportType)
            {
                propertyAccess = node;
            }
            else
            {
                propertyAccess = node.Parent!;
            }

            if (inCheckedFile)
            {
                foreach (var symbol in typeChecker.GetApparentProperties(t))
                {
                    if (typeChecker.IsValidPropertyAccessForCompletions(propertyAccess, t, symbol))
                    {
                        AddPropertySymbol(symbol, false /*insertAwait*/, insertQuestionDot);
                    }
                }
            }
            else
            {
                // In javascript files, for union types, we don't just get the members that
                // the individual types have in common, we also include all the members that
                // each individual type has. This is because we're going to add all identifiers
                // anyways. So we might as well elevate the members that were at least part
                // of the individual types to a higher status since we know what they are.
                foreach (var symbol in GetPropertiesForCompletion(t, typeChecker))
                {
                    if (typeChecker.IsValidPropertyAccessForCompletions(propertyAccess, t, symbol))
                    {
                        symbols.Add(symbol);
                    }
                }
            }

            if (insertAwait)
            {
                var promiseType = typeChecker.GetPromisedTypeOfPromise(t);
                if (promiseType != null)
                {
                    foreach (var symbol in typeChecker.GetApparentProperties(promiseType))
                    {
                        if (typeChecker.IsValidPropertyAccessForCompletions(propertyAccess, promiseType, symbol))
                        {
                            AddPropertySymbol(symbol, true /*insertAwait*/, insertQuestionDot);
                        }
                    }
                }
            }
        }

        void GetTypeScriptMemberSymbols()
        {
            // Right of dot member completion list
            completionKind = CompletionKind.PropertyAccess;

            // Since this is qualified name check it's a type node location
            var isImportType = AstUtilities.IsLiteralImportTypeNode(node!);
            var isTypeLocation = (isImportType && !node!.AsImportTypeNode().IsTypeOf) ||
                AstUtilities.IsPartOfTypeNode(node!.Parent!) ||
                (contextToken != null && IsPossiblyTypeArgumentPosition(contextToken, file, typeChecker));
            var isRhsOfImportDeclaration = LsMeaning.IsInRightSideOfInternalImportEqualsDeclaration(node);
            if (AstUtilities.IsEntityName(node) || isImportType || NodeTests.IsPropertyAccessExpression(node))
            {
                var isNamespaceName = NodeTests.IsModuleDeclaration(node.Parent!);
                if (isNamespaceName)
                {
                    isNewIdentifierLocation = true;
                    defaultCommitCharacters = [];
                }
                var symbolAtLocation = typeChecker.GetSymbolAtLocation(node);
                if (symbolAtLocation != null)
                {
                    var symbol = TsChecker.SkipAlias(symbolAtLocation, typeChecker)!;
                    if ((symbol.Flags & (SymbolFlags.Module | SymbolFlags.Enum)) != 0)
                    {
                        Node valueAccessNode;
                        if (isImportType)
                        {
                            valueAccessNode = node;
                        }
                        else
                        {
                            valueAccessNode = node.Parent!;
                        }
                        // Extract module or enum members
                        // Go services.go:121 GetExportsOfModule (array form) = symbolsToArray(getExportsOfModule);
                        // the C# table-form port owns the Go-exported name, so compose explicitly here.
                        var exportedSymbols = TsChecker.SymbolsToArray(typeChecker.GetExportsOfModule(symbol));
                        foreach (var exportedSymbol in exportedSymbols)
                        {
                            if (exportedSymbol == null)
                            {
                                throw new global::System.InvalidOperationException("getExporsOfModule() should all be defined");
                            }
                            bool IsValidValueAccess(Symbol s) => typeChecker.IsValidPropertyAccess(valueAccessNode, s.Name);
                            bool IsValidTypeAccess(Symbol s) => SymbolCanBeReferencedAtTypeLocation(s, typeChecker, []);
                            bool isValidAccess;
                            if (isNamespaceName)
                            {
                                // At `namespace N.M/**/`, if this is the only declaration of `M`, don't include `M` as a completion.
                                isValidAccess = (exportedSymbol.Flags & SymbolFlags.Namespace) != 0 &&
                                    !global::Ts.Core.CoreUtil.Every(exportedSymbol.Declarations, declaration => declaration.Parent == node.Parent);
                            }
                            else if (isRhsOfImportDeclaration)
                            {
                                // Any kind is allowed when dotting off namespace in internal import equals declaration
                                isValidAccess = IsValidTypeAccess(exportedSymbol) || IsValidValueAccess(exportedSymbol);
                            }
                            else if (isTypeLocation || insideJSDocTagTypeExpression)
                            {
                                isValidAccess = IsValidTypeAccess(exportedSymbol);
                            }
                            else
                            {
                                isValidAccess = IsValidValueAccess(exportedSymbol);
                            }
                            if (isValidAccess)
                            {
                                symbols.Add(exportedSymbol);
                            }
                        }

                        // If the module is merged with a value, we must get the type of the class and add its properties (for inherited static methods).
                        if (!isTypeLocation && !insideJSDocTagTypeExpression &&
                            global::Ts.Core.CoreUtil.Some(
                                symbol.Declarations,
                                decl => decl.Kind != SyntaxKind.SourceFile && decl.Kind != SyntaxKind.ModuleDeclaration && decl.Kind != SyntaxKind.EnumDeclaration))
                        {
                            var t = typeChecker.GetNonOptionalType(typeChecker.GetTypeOfSymbolAtLocation(symbol, node));
                            var insertQuestionDot = false;
                            if (typeChecker.IsNullableType(t))
                            {
                                var canCorrectToQuestionDot = isRightOfDot && !isRightOfQuestionDot &&
                                    !preferences.IncludeAutomaticOptionalChainCompletions.IsFalse();
                                if (canCorrectToQuestionDot || isRightOfQuestionDot)
                                {
                                    t = typeChecker.GetNonNullableType(t);
                                    if (canCorrectToQuestionDot)
                                    {
                                        insertQuestionDot = true;
                                    }
                                }
                            }
                            AddTypeProperties(t, (node.Flags & NodeFlags.AwaitContext) != 0, insertQuestionDot);
                        }

                        return;
                    }
                }
            }

            if (!isTypeLocation || TsChecker.IsInTypeQuery(node))
            {
                // microsoft/TypeScript#39946. Pulling on the type of a node inside of a function with a contextual `this` parameter can result in a circularity
                // if the `node` is part of the exprssion of a `yield` or `return`. This circularity doesn't exist at compile time because
                // we will check (and cache) the type of `this` *before* checking the type of the node.
                typeChecker.TryGetThisTypeAtEx(node, false /*includeGlobalThis*/, null);
                var t = typeChecker.GetNonOptionalType(typeChecker.GetTypeAtLocation(node));

                if (!isTypeLocation)
                {
                    var insertQuestionDot = false;
                    if (typeChecker.IsNullableType(t))
                    {
                        var canCorrectToQuestionDot = isRightOfDot && !isRightOfQuestionDot &&
                            !preferences.IncludeAutomaticOptionalChainCompletions.IsFalse();

                        if (canCorrectToQuestionDot || isRightOfQuestionDot)
                        {
                            t = typeChecker.GetNonNullableType(t);
                            if (canCorrectToQuestionDot)
                            {
                                insertQuestionDot = true;
                            }
                        }
                    }
                    AddTypeProperties(t, (node.Flags & NodeFlags.AwaitContext) != 0, insertQuestionDot);
                }
                else
                {
                    AddTypeProperties(typeChecker.GetNonNullableType(t), false /*insertAwait*/, false /*insertQuestionDot*/);
                }
            }
        }

        // Aggregates relevant symbols for completion in object literals in type argument positions.
        GlobalsSearch TryGetObjectTypeLiteralInTypeArgumentCompletionSymbols()
        {
            var typeLiteralNode = TryGetTypeLiteralNode(contextToken);
            if (typeLiteralNode == null)
            {
                return GlobalsSearch.Continue;
            }

            var intersectionTypeNode = NodeTests.IsIntersectionTypeNode(typeLiteralNode.Parent!) ? typeLiteralNode.Parent : null;
            var containerTypeNode = intersectionTypeNode ?? typeLiteralNode;

            var containerExpectedType = GetConstraintOfTypeArgumentProperty(containerTypeNode, typeChecker);
            if (containerExpectedType == null)
            {
                return GlobalsSearch.Continue;
            }

            var containerActualType = typeChecker.GetTypeFromTypeNode(containerTypeNode);

            var members = GetPropertiesForCompletion(containerExpectedType, typeChecker);
            var existingMembers = GetPropertiesForCompletion(containerActualType, typeChecker);

            var existingMemberNames = new HashSet<string>(global::System.StringComparer.Ordinal);
            foreach (var member in existingMembers)
            {
                existingMemberNames.Add(member.Name);
            }

            symbols.AddRange(global::Ts.Core.CoreUtil.Filter(members, member => !existingMemberNames.Contains(member.Name)) ?? []);

            completionKind = CompletionKind.ObjectPropertyDeclaration;
            isNewIdentifierLocation = true;

            return GlobalsSearch.Success;
        }

        // Aggregates relevant symbols for completion in object literals and object binding patterns.
        // Relevant symbols are stored in the captured 'symbols' variable.
        GlobalsSearch TryGetObjectLikeCompletionSymbols()
        {
            if (contextToken != null && contextToken.Kind == SyntaxKind.DotDotDotToken)
            {
                return GlobalsSearch.Continue;
            }
            var objectLikeContainer = TryGetObjectLikeCompletionContainer(contextToken, position, file);
            if (objectLikeContainer == null)
            {
                return GlobalsSearch.Continue;
            }

            // We're looking up possible property names from contextual/inferred/declared type.
            completionKind = CompletionKind.ObjectPropertyDeclaration;

            Symbol[] typeMembers = [];
            Node[] existingMembers = [];

            if (objectLikeContainer.Kind == SyntaxKind.ObjectLiteralExpression)
            {
                var instantiatedType = TryGetObjectLiteralContextualType(objectLikeContainer, typeChecker);

                // Check completions for Object property value shorthand
                if (instantiatedType == null)
                {
                    if ((objectLikeContainer.Flags & NodeFlags.InWithStatement) != 0)
                    {
                        return GlobalsSearch.Fail;
                    }
                    return GlobalsSearch.Continue;
                }
                var completionsType = typeChecker.GetContextualType(objectLikeContainer, global::Ts.Checker.ContextFlags.IgnoreNodeInferences);
                var t = completionsType ?? instantiatedType;
                var stringIndexType = typeChecker.GetStringIndexType(t);
                var numberIndexType = typeChecker.GetNumberIndexType(t);
                isNewIdentifierLocation = stringIndexType != null || numberIndexType != null;
                typeMembers = GetPropertiesForObjectExpression(instantiatedType, completionsType, objectLikeContainer, typeChecker);
                existingMembers = objectLikeContainer.Properties() ?? [];

                if (typeMembers.Length == 0)
                {
                    // Edge case: If NumberIndexType exists
                    if (numberIndexType == null)
                    {
                        return GlobalsSearch.Continue;
                    }
                }
            }
            else
            {
                if (objectLikeContainer.Kind != SyntaxKind.ObjectBindingPattern)
                {
                    throw new global::System.InvalidOperationException("Expected 'objectLikeContainer' to be an object binding pattern.");
                }
                // We are *only* completing on properties from the type being destructured.
                isNewIdentifierLocation = false;
                var rootDeclaration = AstUtilities.GetRootDeclaration(objectLikeContainer.Parent!);
                if (!AstUtilities.IsVariableLike(rootDeclaration))
                {
                    throw new global::System.InvalidOperationException("Root declaration is not variable-like.");
                }

                // We don't want to complete using the type acquired by the shape
                // of the binding pattern; we are only interested in types acquired
                // through type declaration or inference.
                // Also proceed if rootDeclaration is a parameter and if its containing function expression/arrow function is contextually typed -
                // type of parameter will flow in from the contextual type of the function.
                var canGetType = AstUtilities.HasInitializer(rootDeclaration) ||
                    AstUtilities.GetTypeAnnotationNode(rootDeclaration) != null ||
                    rootDeclaration.Parent!.Parent!.Kind == SyntaxKind.ForOfStatement;
                if (!canGetType && rootDeclaration.Kind == SyntaxKind.Parameter)
                {
                    if (AstUtilities.IsExpression(rootDeclaration.Parent!))
                    {
                        canGetType = typeChecker.GetContextualType(rootDeclaration.Parent!, global::Ts.Checker.ContextFlags.None) != null;
                    }
                    else if (rootDeclaration.Parent!.Kind == SyntaxKind.MethodDeclaration ||
                        rootDeclaration.Parent.Kind == SyntaxKind.SetAccessor)
                    {
                        canGetType = AstUtilities.IsExpression(rootDeclaration.Parent.Parent!) &&
                            typeChecker.GetContextualType(rootDeclaration.Parent.Parent!, global::Ts.Checker.ContextFlags.None) != null;
                    }
                }
                if (canGetType)
                {
                    var typeForObject = typeChecker.GetTypeAtLocation(objectLikeContainer);
                    if (typeForObject == null)
                    {
                        return GlobalsSearch.Fail;
                    }
                    typeMembers = global::Ts.Core.CoreUtil.Filter(
                        typeChecker.GetPropertiesOfType(typeForObject),
                        propertySymbol => typeChecker.IsPropertyAccessible(
                            objectLikeContainer,
                            false, /*isSuper*/
                            false, /*isWrite*/
                            typeForObject,
                            propertySymbol)) ?? [];
                    existingMembers = objectLikeContainer.Elements() ?? [];
                }
            }

            if (typeMembers.Length > 0)
            {
                // Add filtered items to the completion list.
                var (filteredMembers, spreadMemberNames) = FilterObjectMembersList(
                    typeMembers,
                    global::Ts.Core.CoreUtil.CheckEachDefined(existingMembers, "object like properties or elements should all be defined"),
                    file,
                    position,
                    typeChecker);
                symbols.AddRange(filteredMembers);

                // Set sort texts.
                var transformObjectLiteralMembers = preferences.IncludeCompletionsWithObjectLiteralMethodSnippets.IsTrue() &&
                    objectLikeContainer.Kind == SyntaxKind.ObjectLiteralExpression;
                foreach (var member in filteredMembers)
                {
                    var symbolId = AstUtilities.GetSymbolId(member);
                    if (spreadMemberNames.Contains(member.Name))
                    {
                        symbolToSortTextMap[symbolId] = SortTextMemberDeclaredBySpreadAssignmentValue;
                    }
                    if ((member.Flags & SymbolFlags.Optional) != 0)
                    {
                        if (!symbolToSortTextMap.ContainsKey(symbolId))
                        {
                            symbolToSortTextMap[symbolId] = SortTextOptionalMemberValue;
                        }
                    }
                    if (transformObjectLiteralMembers)
                    {
                        // !!! object literal member snippet completions
                    }
                }
            }

            return GlobalsSearch.Success;
        }

        bool ShouldOfferImportCompletions()
        {
            if (global::Ts.TsPath.TsPath.IsDynamicFileName(file.FileName()))
            {
                return false;
            }
            // If already typing an import statement, provide completions for it.
            if (importStatementCompletion != null)
            {
                return true;
            }
            // If not already a module, must have modules enabled.
            if (preferences.IncludeCompletionsForModuleExports.IsFalse())
            {
                return false;
            }
            // Always using ES modules in 6.0+
            return true;
        }

        // Mutates `symbols`, `symbolToOriginInfoMap`, and `symbolToSortTextMap`
        void CollectAutoImports()
        {
            // `completionItem/resolve` for auto-import completions should be resolved via the completion item data,
            // so we don't need to collect auto-import entries again.
            if (forItemResolve)
            {
                return;
            }
            if (!ShouldOfferImportCompletions())
            {
                return;
            }

            // PORT-NOTE: completions.go:1149-1159 — l.getPreparedAutoImportView(file).GetCompletions(...)
            // requires the autoimport package (its own porting unit). Until it lands, no auto-import
            // entries are produced and `autoImports` stays null.
        }

        GlobalsSearch TryGetImportCompletionSymbols()
        {
            if (importStatementCompletion == null)
            {
                return GlobalsSearch.Continue;
            }
            isNewIdentifierLocation = true;
            CollectAutoImports();
            return GlobalsSearch.Success;
        }

        // Aggregates relevant symbols for completion in import clauses and export clauses
        // whose declarations have a module specifier; for instance, symbols will be aggregated for
        //
        //      import { | } from "moduleName";
        //      export { a as foo, | } from "moduleName";
        //
        // but not for
        //
        //      export { | };
        //
        // Relevant symbols are stored in the captured 'symbols' variable.
        GlobalsSearch TryGetImportOrExportClauseCompletionSymbols()
        {
            if (contextToken == null)
            {
                return GlobalsSearch.Continue;
            }

            // `import { |` or `import { a as 0, | }` or `import { type | }`
            Node? namedImportsOrExports = null;
            if (contextToken.Kind == SyntaxKind.OpenBraceToken || contextToken.Kind == SyntaxKind.CommaToken)
            {
                namedImportsOrExports = IsNamedImportsOrExports(contextToken.Parent!) ? contextToken.Parent : null;
            }
            else if (IsTypeKeywordTokenOrIdentifier(contextToken))
            {
                namedImportsOrExports = IsNamedImportsOrExports(contextToken.Parent!.Parent!) ? contextToken.Parent.Parent : null;
            }

            if (namedImportsOrExports == null)
            {
                return GlobalsSearch.Continue;
            }

            // We can at least offer `type` at `import { |`
            if (!IsTypeKeywordTokenOrIdentifier(contextToken))
            {
                keywordFilters = KeywordCompletionFilters.TypeKeyword;
            }

            // try to show exported member for imported/re-exported module
            var moduleSpecifier = (namedImportsOrExports.Kind == SyntaxKind.NamedImports
                ? namedImportsOrExports.Parent!.Parent!
                : namedImportsOrExports.Parent!).ModuleSpecifier();
            if (moduleSpecifier == null)
            {
                isNewIdentifierLocation = true;
                if (namedImportsOrExports.Kind == SyntaxKind.NamedImports)
                {
                    return GlobalsSearch.Fail;
                }
                return GlobalsSearch.Continue;
            }

            var moduleSpecifierSymbol = typeChecker.GetSymbolAtLocation(moduleSpecifier);
            if (moduleSpecifierSymbol == null)
            {
                isNewIdentifierLocation = true;
                return GlobalsSearch.Fail;
            }

            completionKind = CompletionKind.MemberLike;
            isNewIdentifierLocation = false;
            var exports = typeChecker.GetExportsAndPropertiesOfModule(moduleSpecifierSymbol);

            var existing = new HashSet<string>(global::System.StringComparer.Ordinal);
            foreach (var element in namedImportsOrExports.Elements() ?? [])
            {
                if (IsCurrentlyEditingNode(element, file, position))
                {
                    continue;
                }
                existing.Add(element.PropertyNameOrName()!.Text());
            }
            var uniques = global::Ts.Core.CoreUtil.Filter(exports, symbol =>
                AstUtilities.SymbolName(symbol) != InternalSymbolName.Default && !existing.Contains(AstUtilities.SymbolName(symbol))) ?? [];

            symbols.AddRange(uniques);
            if (uniques.Length == 0)
            {
                // If there's nothing else to import, don't offer `type` either.
                keywordFilters = KeywordCompletionFilters.None;
            }
            return GlobalsSearch.Success;
        }

        // import { x } from "foo" with { | }
        GlobalsSearch TryGetImportAttributesCompletionSymbols()
        {
            if (contextToken == null)
            {
                return GlobalsSearch.Continue;
            }

            Node? importAttributes = null;
            switch (contextToken.Kind)
            {
                case SyntaxKind.OpenBraceToken:
                case SyntaxKind.CommaToken:
                    importAttributes = contextToken.Parent;
                    break;
                case SyntaxKind.ColonToken:
                    importAttributes = contextToken.Parent!.Parent;
                    break;
            }
            if (importAttributes == null || !NodeTests.IsImportAttributes(importAttributes))
            {
                return GlobalsSearch.Continue;
            }

            Node[] elements = [];
            if (importAttributes.AsImportAttributes().Attributes != null)
            {
                elements = importAttributes.AsImportAttributes().Attributes!.Nodes;
            }
            var existing = new HashSet<string>(global::System.StringComparer.Ordinal);
            foreach (var el in elements)
            {
                existing.Add(el.AsImportAttribute().Name()!.Text());
            }
            var uniques = global::Ts.Core.CoreUtil.Filter(
                typeChecker.GetApparentProperties(typeChecker.GetTypeAtLocation(importAttributes)),
                symbol => !existing.Contains(AstUtilities.SymbolName(symbol))) ?? [];
            symbols.AddRange(uniques);
            return GlobalsSearch.Success;
        }

        // Adds local declarations for completions in named exports:
        //   export { | };
        // Does not check for the absence of a module specifier (`export {} from "./other"`)
        // because `tryGetImportOrExportClauseCompletionSymbols` runs first and handles that,
        // preventing this function from running.
        GlobalsSearch TryGetLocalNamedExportCompletionSymbols()
        {
            if (contextToken == null)
            {
                return GlobalsSearch.Continue;
            }
            Node? namedExports = null;
            if (contextToken.Kind == SyntaxKind.OpenBraceToken || contextToken.Kind == SyntaxKind.CommaToken)
            {
                namedExports = NodeTests.IsNamedExports(contextToken.Parent!) ? contextToken.Parent : null;
            }

            if (namedExports == null)
            {
                return GlobalsSearch.Continue;
            }

            var localsContainer = AstUtilities.FindAncestor(namedExports, n => NodeTests.IsSourceFile(n) || NodeTests.IsModuleDeclaration(n))!;
            completionKind = CompletionKind.None;
            isNewIdentifierLocation = false;
            var localSymbol = localsContainer.Symbol();
            SymbolTable? localExports = null;
            if (localSymbol != null)
            {
                localExports = localSymbol.Exports;
            }
            foreach (var (name, symbol) in localsContainer.Locals() ?? []) // map-iter: 序无关 (completion entries are name-sorted downstream)
            {
                symbols.Add(symbol);
                if (localExports != null && localExports.ContainsKey(name))
                {
                    var symbolId = AstUtilities.GetSymbolId(symbol);
                    symbolToSortTextMap[symbolId] = SortTextOptionalMemberValue;
                }
            }

            return GlobalsSearch.Success;
        }

        GlobalsSearch TryGetConstructorCompletion()
        {
            if (TryGetConstructorLikeCompletionContainer(contextToken) == null)
            {
                return GlobalsSearch.Continue;
            }

            // no members, only keywords
            completionKind = CompletionKind.None;
            // Declaring new property/method/accessor
            isNewIdentifierLocation = true;
            // Has keywords for constructor parameter
            keywordFilters = KeywordCompletionFilters.ConstructorParameterKeywords;
            return GlobalsSearch.Success;
        }

        // Aggregates relevant symbols for completion in class declaration
        // Relevant symbols are stored in the captured 'symbols' variable.
        GlobalsSearch TryGetClassLikeCompletionSymbols()
        {
            var decl = TryGetObjectTypeDeclarationCompletionContainer(file, contextToken, location, position);
            if (decl == null)
            {
                return GlobalsSearch.Continue;
            }

            // We're looking up possible property names from parent type.
            completionKind = CompletionKind.MemberLike;
            // Declaring new property/method/accessor
            isNewIdentifierLocation = true;
            if (contextToken!.Kind == SyntaxKind.AsteriskToken)
            {
                keywordFilters = KeywordCompletionFilters.None;
            }
            else if (AstUtilities.IsClassLike(decl))
            {
                keywordFilters = KeywordCompletionFilters.ClassElementKeywords;
            }
            else
            {
                keywordFilters = KeywordCompletionFilters.InterfaceElementKeywords;
            }

            // If you're in an interface you don't want to repeat things from super-interface. So just stop here.
            if (!AstUtilities.IsClassLike(decl))
            {
                return GlobalsSearch.Success;
            }

            Node classElement;
            if (contextToken.Kind == SyntaxKind.SemicolonToken)
            {
                classElement = contextToken.Parent!.Parent!;
            }
            else
            {
                classElement = contextToken.Parent!;
            }
            var classElementModifierFlags = ModifierFlags.None;
            if (AstUtilities.IsClassElement(classElement))
            {
                classElementModifierFlags = classElement.ModifierFlags();
            }
            // If this is context token is not something we are editing now, consider if this would lead to be modifier.
            if (contextToken.Kind == SyntaxKind.Identifier && !IsCurrentlyEditingNode(contextToken, file, position))
            {
                switch (contextToken.Text())
                {
                    case "private":
                        classElementModifierFlags |= ModifierFlags.Private;
                        break;
                    case "static":
                        classElementModifierFlags |= ModifierFlags.Static;
                        break;
                    case "override":
                        classElementModifierFlags |= ModifierFlags.Override;
                        break;
                }
            }
            if (NodeTests.IsClassStaticBlockDeclaration(classElement))
            {
                classElementModifierFlags |= ModifierFlags.Static;
            }

            // No member list for private methods
            if ((classElementModifierFlags & ModifierFlags.Private) == 0)
            {
                // List of property symbols of base type that are not private and already implemented
                Node[] baseTypeNodes;
                if (AstUtilities.IsClassLike(decl) && (classElementModifierFlags & ModifierFlags.Override) != 0)
                {
                    baseTypeNodes = global::Ts.Core.CoreUtil.SingleElementSlice(AstUtilities.GetClassExtendsHeritageElement(decl)) ?? [];
                }
                else
                {
                    baseTypeNodes = GetAllSuperTypeNodes(decl);
                }
                var baseSymbols = new List<Symbol>();
                foreach (var baseTypeNode in baseTypeNodes)
                {
                    var t = typeChecker.GetTypeAtLocation(baseTypeNode);
                    if ((classElementModifierFlags & ModifierFlags.Static) != 0)
                    {
                        if (t.Symbol != null)
                        {
                            baseSymbols.AddRange(typeChecker.GetPropertiesOfType(typeChecker.GetTypeOfSymbolAtLocation(t.Symbol, decl)));
                        }
                    }
                    else if (t != null)
                    {
                        baseSymbols.AddRange(typeChecker.GetPropertiesOfType(t));
                    }
                }

                symbols.AddRange(FilterClassMembersList(baseSymbols.ToArray(), decl.Members() ?? [], classElementModifierFlags, file, position));
                for (var index = 0; index < symbols.Count; index++)
                {
                    var symbol = symbols[index];
                    var declaration = symbol.ValueDeclaration;
                    if (declaration != null && AstUtilities.IsClassElement(declaration) &&
                        declaration.Name() != null &&
                        NodeTests.IsComputedPropertyName(declaration.Name()!))
                    {
                        var origin = new SymbolOriginInfo
                        {
                            kind = SymbolOriginInfoKind.ComputedPropertyName,
                            data = new SymbolOriginInfoComputedPropertyName { symbolName = typeChecker.SymbolToString(symbol) },
                        };
                        symbolToOriginInfoMap[index] = origin;
                    }
                }
            }

            return GlobalsSearch.Success;
        }

        GlobalsSearch TryGetJsxCompletionSymbols()
        {
            var jsxContainer = TryGetContainingJsxElement(contextToken, file);
            if (jsxContainer == null)
            {
                return GlobalsSearch.Continue;
            }
            // Cursor is inside a JSX self-closing element or opening element.
            var attrsType = typeChecker.GetContextualType(jsxContainer.Attributes()!, global::Ts.Checker.ContextFlags.None);
            if (attrsType == null)
            {
                return GlobalsSearch.Continue;
            }
            var completionsType = typeChecker.GetContextualType(jsxContainer.Attributes()!, global::Ts.Checker.ContextFlags.IgnoreNodeInferences);
            var (filteredSymbols, spreadMemberNames) = FilterJsxAttributes(
                GetPropertiesForObjectExpression(attrsType, completionsType, jsxContainer.Attributes()!, typeChecker),
                jsxContainer.Attributes()!.Properties() ?? [],
                file,
                position,
                typeChecker);

            symbols.AddRange(filteredSymbols);
            // Set sort texts.
            foreach (var symbol in filteredSymbols)
            {
                var symbolId = AstUtilities.GetSymbolId(symbol);
                if (spreadMemberNames.Contains(AstUtilities.SymbolName(symbol)))
                {
                    symbolToSortTextMap[symbolId] = SortTextMemberDeclaredBySpreadAssignmentValue;
                }
                if ((symbol.Flags & SymbolFlags.Optional) != 0)
                {
                    if (!symbolToSortTextMap.ContainsKey(symbolId))
                    {
                        symbolToSortTextMap[symbolId] = SortTextOptionalMemberValue;
                    }
                }
            }

            completionKind = CompletionKind.MemberLike;
            isNewIdentifierLocation = false;
            return GlobalsSearch.Success;
        }

        GlobalsSearch GetGlobalCompletions()
        {
            if (TryGetFunctionLikeBodyCompletionContainer(contextToken) != null)
            {
                keywordFilters = KeywordCompletionFilters.FunctionLikeBodyKeywords;
            }
            else
            {
                keywordFilters = KeywordCompletionFilters.All;
            }
            // Get all entities in the current scope.
            completionKind = CompletionKind.Global;
            (isNewIdentifierLocation, defaultCommitCharacters) = ComputeCommitCharactersAndIsNewIdentifier(contextToken, file, position);

            if (previousToken != contextToken)
            {
                if (previousToken == null)
                {
                    throw new global::System.InvalidOperationException("Expected 'contextToken' to be defined when different from 'previousToken'.");
                }
            }

            // We need to find the node that will give us an appropriate scope to begin
            // aggregating completion candidates. This is achieved in 'getScopeNode'
            // by finding the first node that encompasses a position, accounting for whether a node
            // is "complete" to decide whether a position belongs to the node.
            //
            // However, at the end of an identifier, we are interested in the scope of the identifier
            // itself, but fall outside of the identifier. For instance:
            //
            //      xyz => x$
            //
            // the cursor is outside of both the 'x' and the arrow function 'xyz => x',
            // so 'xyz' is not returned in our results.
            //
            // We define 'adjustedPosition' so that we may appropriately account for
            // being at the end of an identifier. The intention is that if requesting completion
            // at the end of an identifier, it should be effectively equivalent to requesting completion
            // anywhere inside/at the beginning of the identifier. So in the previous case, the
            // 'adjustedPosition' will work as if requesting completion in the following:
            //
            //      xyz => $x
            //
            // If previousToken !== contextToken, then
            //   - 'contextToken' was adjusted to the token prior to 'previousToken'
            //      because we were at the end of an identifier.
            //   - 'previousToken' is defined.
            int adjustedPosition;
            if (previousToken != contextToken)
            {
                adjustedPosition = global::Ts.AstNav.Tokens.GetStartOfNode(previousToken!, file, false /*includeJSDoc*/);
            }
            else
            {
                adjustedPosition = position;
            }

            var scopeNode = GetScopeNode(contextToken, adjustedPosition, file) ?? file;
            isInSnippetScope = IsSnippetScope(scopeNode);

            var symbolMeanings = (isTypeOnlyLocation ? SymbolFlags.None : SymbolFlags.Value) |
                SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias;
            var typeOnlyAliasNeedsPromotion = previousToken != null && !AstUtilities.IsValidTypeOnlyAliasUseSite(previousToken);

            symbols.AddRange(typeChecker.GetSymbolsInScope(scopeNode, symbolMeanings));
            global::Ts.Core.CoreUtil.CheckEachDefined(symbols.ToArray(), "getSymbolsInScope() should all be defined");
            for (var index = 0; index < symbols.Count; index++)
            {
                var symbol = symbols[index];
                var symbolId = AstUtilities.GetSymbolId(symbol);
                if (!typeChecker.IsArgumentsSymbol(symbol) &&
                    !global::Ts.Core.CoreUtil.Some(symbol.Declarations, decl => AstUtilities.GetSourceFileOfNode(decl) == file))
                {
                    symbolToSortTextMap[symbolId] = SortTextGlobalsOrKeywordsValue;
                }
                if (typeOnlyAliasNeedsPromotion && (symbol.Flags & SymbolFlags.Value) == 0)
                {
                    var typeOnlyAliasDeclaration = global::Ts.Core.CoreUtil.Find(symbol.Declarations, TsChecker.IsTypeOnlyImportDeclaration);
                    if (typeOnlyAliasDeclaration != null)
                    {
                        var origin = new SymbolOriginInfo
                        {
                            kind = SymbolOriginInfoKind.TypeOnlyAlias,
                            data = new SymbolOriginInfoTypeOnlyAlias { declaration = typeOnlyAliasDeclaration },
                        };
                        symbolToOriginInfoMap[index] = origin;
                    }
                }
            }

            // Need to insert 'this.' before properties of `this` type.
            if (scopeNode.Kind != SyntaxKind.SourceFile)
            {
                var thisType = typeChecker.TryGetThisTypeAtEx(
                    scopeNode,
                    false, /*includeGlobalThis*/
                    AstUtilities.IsClassLike(scopeNode.Parent!) ? scopeNode : null);
                if (thisType != null && !IsProbablyGlobalType(thisType, file, typeChecker))
                {
                    foreach (var symbol in GetPropertiesForCompletion(thisType, typeChecker))
                    {
                        var symbolId = AstUtilities.GetSymbolId(symbol);
                        symbols.Add(symbol);
                        symbolToOriginInfoMap[symbols.Count - 1] = new SymbolOriginInfo { kind = SymbolOriginInfoKind.ThisType };
                        symbolToSortTextMap[symbolId] = SortTextSuggestedClassMembersValue;
                    }
                }
            }

            CollectAutoImports();
            if (isTypeOnlyLocation)
            {
                if (contextToken != null && AstUtilities.IsAssertionExpression(contextToken.Parent!))
                {
                    keywordFilters = KeywordCompletionFilters.TypeAssertionKeywords;
                }
                else
                {
                    keywordFilters = KeywordCompletionFilters.TypeKeywords;
                }
            }

            return GlobalsSearch.Success;
        }

        bool TryGetGlobalSymbols()
        {
            var result = GlobalsSearch.Continue;
            global::System.Func<GlobalsSearch>[] globalSearchFuncs = [
                TryGetObjectTypeLiteralInTypeArgumentCompletionSymbols,
                TryGetObjectLikeCompletionSymbols,
                TryGetImportCompletionSymbols,
                TryGetImportOrExportClauseCompletionSymbols,
                TryGetImportAttributesCompletionSymbols,
                TryGetLocalNamedExportCompletionSymbols,
                TryGetConstructorCompletion,
                TryGetClassLikeCompletionSymbols,
                TryGetJsxCompletionSymbols,
                GetGlobalCompletions,
            ];
            foreach (var globalSearchFunc in globalSearchFuncs)
            {
                result = globalSearchFunc();
                if (result != GlobalsSearch.Continue)
                {
                    break;
                }
            }
            return result == GlobalsSearch.Success;
        }

        if (isRightOfDot || isRightOfQuestionDot)
        {
            GetTypeScriptMemberSymbols();
        }
        else if (isRightOfOpenTag)
        {
            symbols = [.. typeChecker.GetJsxIntrinsicTagNamesAt(location)];
            global::Ts.Core.CoreUtil.CheckEachDefined(symbols.ToArray(), "GetJsxIntrinsicTagNamesAt() should all be defined");
            TryGetGlobalSymbols();
            completionKind = CompletionKind.Global;
            keywordFilters = KeywordCompletionFilters.None;
        }
        else if (isStartingCloseTag)
        {
            var tagName = contextToken!.Parent!.Parent!.AsJsxElement().OpeningElement!.TagName();
            var tagSymbol = typeChecker.GetSymbolAtLocation(tagName!);
            if (tagSymbol != null)
            {
                symbols = [tagSymbol];
            }
            completionKind = CompletionKind.Global;
            keywordFilters = KeywordCompletionFilters.None;
        }
        else
        {
            // For JavaScript or TypeScript, if we're not after a dot, then just try to get the
            // global symbols in scope.  These results should be valid for either language as
            // the set of symbols that can be referenced from this location.
            if (!TryGetGlobalSymbols())
            {
                if (keywordFilters != KeywordCompletionFilters.None)
                {
                    return KeywordCompletionData(keywordFilters, isJSOnlyLocation, isNewIdentifierLocation);
                }
                return null;
            }
        }

        CheckerType? contextualTypeOrConstraint = null;
        if (previousToken != null)
        {
            contextualTypeOrConstraint = GetContextualType(previousToken, position, file, typeChecker);
            if (contextualTypeOrConstraint == null)
            {
                contextualTypeOrConstraint = GetConstraintOfTypeArgumentProperty(previousToken, typeChecker);
            }
        }

        // exclude literal suggestions after <input type="text" [||] /> microsoft/TypeScript#51667) and after closing quote (microsoft/TypeScript#52675)
        // for strings getStringLiteralCompletions handles completions
        var isLiteralExpected = !(previousToken != null && AstUtilities.IsStringLiteralLike(previousToken)) && !isJsxIdentifierExpected;
        List<object?>? literals = null;
        if (isLiteralExpected)
        {
            CheckerType[] types = [];
            if (contextualTypeOrConstraint != null && contextualTypeOrConstraint.IsUnion())
            {
                types = contextualTypeOrConstraint.Types();
            }
            else if (contextualTypeOrConstraint != null)
            {
                types = [contextualTypeOrConstraint];
            }
            literals = [];
            foreach (var t in types)
            {
                if (IsLiteral(t) && !t.IsEnumLiteral())
                {
                    literals.Add(t.AsLiteralType().Value());
                }
            }
        }

        Symbol? recommendedCompletion = null;
        if (previousToken != null && contextualTypeOrConstraint != null)
        {
            recommendedCompletion = GetRecommendedCompletion(previousToken, contextualTypeOrConstraint, typeChecker);
        }

        defaultCommitCharacters ??= GetDefaultCommitCharacters(isNewIdentifierLocation);

        return new CompletionDataData
        {
            symbols = symbols.ToArray(),
            autoImports = autoImports,
            completionKind = completionKind,
            isInSnippetScope = isInSnippetScope,
            propertyAccessToConvert = propertyAccessToConvert,
            isNewIdentifierLocation = isNewIdentifierLocation,
            location = location,
            keywordFilters = keywordFilters,
            literals = literals?.ToArray(),
            symbolToOriginInfoMap = symbolToOriginInfoMap,
            symbolToSortTextMap = symbolToSortTextMap,
            recommendedCompletion = recommendedCompletion,
            previousToken = previousToken,
            contextToken = contextToken,
            jsxInitializer = jsxInitializer,
            insideJSDocTagTypeExpression = insideJSDocTagTypeExpression,
            isTypeOnlyLocation = isTypeOnlyLocation,
            isJsxIdentifierExpected = isJsxIdentifierExpected,
            isRightOfOpenTag = isRightOfOpenTag,
            isRightOfDotOrQuestionDot = isRightOfDot || isRightOfQuestionDot,
            importStatementCompletion = importStatementCompletion,
            hasUnresolvedAutoImports = hasUnresolvedAutoImports,
            defaultCommitCharacters = defaultCommitCharacters,
        };
    }
}
