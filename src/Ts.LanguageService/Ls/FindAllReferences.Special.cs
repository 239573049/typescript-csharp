// Ported from tsgo internal/ls/findallreferences.go (getReferencedSymbolsSpecial and the
// string-literal reference families) plus the rename.go/shared helper
// getContextualTypeFromParentOrAncestorTypeNode (ls/utilities.go:1355/1374).

using System;
using System.Collections.Generic;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerLsExports extension methods

namespace Ts.Ls;

internal static class FindRefsSpecial
{
    // findallreferences.go:1313-1367 — getReferencedSymbolsSpecial.
    internal static List<SymbolAndEntries>? GetReferencedSymbolsSpecial(Node node, SourceFile[] sourceFiles)
    {
        if (FindRefsSupport.IsTypeKeyword(node.Kind))
        {
            // A void expression (i.e., `void foo()`) is not special, but the `void` type is.
            if (node.Kind == SyntaxKind.VoidKeyword && node.Parent!.Kind == SyntaxKind.VoidExpression)
            {
                return null;
            }

            // A modifier readonly (like on a property declaration) is not special;
            // a readonly type keyword (like `readonly string[]`) is.
            if (node.Kind == SyntaxKind.ReadonlyKeyword && !FindRefsSupport.IsReadonlyTypeOperator(node))
            {
                return null;
            }
            // Likewise, when we *are* looking for a special keyword, make sure we
            // *don't* include readonly member modifiers.
            return GetAllReferencesForKeyword(
                sourceFiles,
                node.Kind,
                // cancellationToken,
                node.Kind == SyntaxKind.ReadonlyKeyword);
        }

        if (AstUtilities.IsImportMeta(node.Parent!) && node.Parent!.Name() == node)
        {
            return GetAllReferencesForImportMeta(sourceFiles);
        }

        if (node.Kind == SyntaxKind.StaticKeyword && node.Parent!.Kind == SyntaxKind.ClassStaticBlockDeclaration)
        {
            return
            [
                new SymbolAndEntries(
                    new FarDefinition { Kind = DefinitionKind.Keyword, Node = node },
                    [FindRefsSupport.NewNodeEntry(node)]),
            ];
        }

        // Labels
        if (LsDeps.IsJumpStatementTarget(node))
        {
            // if we have a label definition, look within its statement for references, if not, then
            // the label is undefined and we have no results..
            if (FindRefsSupport.GetTargetLabel(node.Parent!, node.Text()) is { } labelDefinition)
            {
                return GetLabelReferencesInNode(labelDefinition.Parent!, labelDefinition);
            }
            return null;
        }

        if (LsDeps.IsLabelOfLabeledStatement(node))
        {
            // it is a label definition and not a target, search within the parent labeledStatement
            return GetLabelReferencesInNode(node.Parent!, node);
        }

        if (FindRefsSupport.IsThis(node))
        {
            return GetReferencesForThisKeyword(node, sourceFiles /*, cancellationToken*/);
        }

        if (node.Kind == SyntaxKind.SuperKeyword)
        {
            return GetReferencesForSuperKeyword(node);
        }

        return null;
    }

    // findallreferences.go:1369-1380 — getLabelReferencesInNode.
    internal static List<SymbolAndEntries> GetLabelReferencesInNode(Node container, Node targetLabel)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(container)!;
        var labelName = targetLabel.Text();
        var references = new List<ReferenceEntry>();
        foreach (var node in GetPossibleSymbolReferenceNodesStatic(sourceFile, labelName, container))
        {
            // Only pick labels that are either the target label, or have a target that is the target label
            if (node == targetLabel || (LsDeps.IsJumpStatementTarget(node) && FindRefsSupport.GetTargetLabel(node, labelName) == targetLabel))
            {
                references.Add(FindRefsSupport.NewNodeEntry(node));
            }
        }
        return [SymbolAndEntries.NewSymbolAndEntries(DefinitionKind.Label, targetLabel, null, references)];
    }

    // findallreferences.go:1382-1458 — getReferencesForThisKeyword.
    internal static List<SymbolAndEntries> GetReferencesForThisKeyword(Node thisOrSuperKeyword, SourceFile[] sourceFiles)
    {
        var searchSpaceNode = AstUtilities.GetThisContainer(thisOrSuperKeyword, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);

        // Whether 'this' occurs in a static context within a class.
        var staticFlag = ModifierFlags.Static;
        static bool IsParameterName(Node node)
        {
            return node.Kind == SyntaxKind.Identifier && node.Parent!.Kind == SyntaxKind.Parameter && node.Parent!.Name() == node;
        }

        switch (searchSpaceNode.Kind)
        {
            case SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature or
                SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature or SyntaxKind.Constructor or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor:
                if ((searchSpaceNode.Kind == SyntaxKind.MethodDeclaration || searchSpaceNode.Kind == SyntaxKind.MethodSignature) && AstUtilities.IsObjectLiteralMethod(searchSpaceNode))
                {
                    staticFlag &= searchSpaceNode.ModifierFlags();
                    searchSpaceNode = searchSpaceNode.Parent!; // re-assign to be the owning object literals
                    break;
                }
                staticFlag &= searchSpaceNode.ModifierFlags();
                searchSpaceNode = searchSpaceNode.Parent!; // re-assign to be the owning class
                break;
            case SyntaxKind.SourceFile:
                if (AstUtilities.IsExternalModule(searchSpaceNode.AsSourceFile()) || IsParameterName(thisOrSuperKeyword))
                {
                    return [];
                }
                break;
            case SyntaxKind.FunctionDeclaration or SyntaxKind.FunctionExpression:
                // Computed properties in classes are not handled here because references to this are illegal,
                // so there is no point finding references to them.
                break;
            default:
                return [];
        }

        var filesToSearch = new List<SourceFile>();
        if (searchSpaceNode.Kind != SyntaxKind.SourceFile)
        {
            filesToSearch.Add(AstUtilities.GetSourceFileOfNode(searchSpaceNode)!);
        }
        else
        {
            filesToSearch.AddRange(sourceFiles);
        }

        var references = new List<ReferenceEntry>();
        foreach (var sourceFile in filesToSearch)
        {
            // cancellationToken.throwIfCancellationRequested();
            var container = searchSpaceNode.Kind == SyntaxKind.SourceFile ? sourceFile.AsNode() : searchSpaceNode;
            foreach (var node in GetPossibleSymbolReferenceNodesStatic(sourceFile, "this", container))
            {
                if (!FindRefsSupport.IsThis(node))
                {
                    continue;
                }
                var innerContainer = AstUtilities.GetThisContainer(node /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/, false);
                if (!AstUtilities.CanHaveSymbol(innerContainer))
                {
                    continue;
                }
                var keep = searchSpaceNode.Kind switch
                {
                    SyntaxKind.FunctionExpression or SyntaxKind.FunctionDeclaration =>
                        searchSpaceNode.Symbol() == innerContainer.Symbol(),
                    SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature =>
                        AstUtilities.IsObjectLiteralMethod(searchSpaceNode) && searchSpaceNode.Symbol() == innerContainer.Symbol(),
                    SyntaxKind.ClassExpression or SyntaxKind.ClassDeclaration or SyntaxKind.ObjectLiteralExpression =>
                        // Make sure the container belongs to the same class/object literals
                        // and has the appropriate static modifier from the original container.
                        innerContainer.Parent != null && AstUtilities.CanHaveSymbol(innerContainer.Parent) &&
                        searchSpaceNode.Symbol() == innerContainer.Parent.Symbol() &&
                        AstUtilities.IsStatic(innerContainer) == (staticFlag != ModifierFlags.None),
                    SyntaxKind.SourceFile =>
                        innerContainer.Kind == SyntaxKind.SourceFile && !AstUtilities.IsExternalModule(innerContainer.AsSourceFile()) && !IsParameterName(node),
                    _ => false,
                };
                if (keep)
                {
                    references.Add(FindRefsSupport.NewNodeEntry(node));
                }
            }
        }

        Node? thisParameter = null;
        foreach (var @ref in references)
        {
            if (@ref.Node!.Parent!.Kind == SyntaxKind.Parameter)
            {
                thisParameter = @ref.Node;
                break;
            }
        }
        thisParameter ??= thisOrSuperKeyword;
        return [SymbolAndEntries.NewSymbolAndEntries(DefinitionKind.This, thisParameter, searchSpaceNode.Symbol(), references)];
    }

    // findallreferences.go:1460-1494 — getReferencesForSuperKeyword.
    internal static List<SymbolAndEntries>? GetReferencesForSuperKeyword(Node superKeyword)
    {
        var searchSpaceNode = CheckerFindRefsLsExports.GetSuperContainerForFindRefs(superKeyword, false /*stopOnFunctions*/);
        if (searchSpaceNode == null)
        {
            return null;
        }
        // Whether 'super' occurs in a static context within a class.
        var staticFlag = ModifierFlags.Static;

        switch (searchSpaceNode.Kind)
        {
            case SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature or SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature or SyntaxKind.Constructor or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor:
                staticFlag &= searchSpaceNode.ModifierFlags();
                searchSpaceNode = searchSpaceNode.Parent!; // re-assign to be the owning class
                break;
            default:
                return null;
        }

        var sourceFile = AstUtilities.GetSourceFileOfNode(searchSpaceNode)!;
        var references = new List<ReferenceEntry>();
        foreach (var node in GetPossibleSymbolReferenceNodesStatic(sourceFile, "super", searchSpaceNode))
        {
            if (node.Kind != SyntaxKind.SuperKeyword)
            {
                continue;
            }

            var container = CheckerFindRefsLsExports.GetSuperContainerForFindRefs(node, false /*stopOnFunctions*/);

            // If we have a 'super' container, we must have an enclosing class.
            // Now make sure the owning class is the same as the search-space
            // and has the same static qualifier as the original 'super's owner.
            if (container != null && AstUtilities.IsStatic(container) == (staticFlag != ModifierFlags.None) && container.Parent!.Symbol() == searchSpaceNode.Symbol())
            {
                references.Add(FindRefsSupport.NewNodeEntry(node));
            }
        }

        return [SymbolAndEntries.NewSymbolAndEntries(DefinitionKind.Symbol, null, searchSpaceNode.Symbol(), references)];
    }

    // findallreferences.go:1496-1510 — getAllReferencesForImportMeta.
    internal static List<SymbolAndEntries>? GetAllReferencesForImportMeta(SourceFile[] sourceFiles)
    {
        var references = new List<ReferenceEntry>();
        foreach (var sourceFile in sourceFiles)
        {
            foreach (var node in GetPossibleSymbolReferenceNodesStatic(sourceFile, "meta", sourceFile.AsNode()))
            {
                var parent = node.Parent!;
                if (AstUtilities.IsImportMeta(parent))
                {
                    references.Add(FindRefsSupport.NewNodeEntry(parent));
                }
            }
        }
        if (references.Count == 0)
        {
            return null;
        }
        return
        [
            new SymbolAndEntries(
                new FarDefinition { Kind = DefinitionKind.Keyword, Node = references[0].Node },
                references),
        ];
    }

    // findallreferences.go:1512-1527 — getAllReferencesForKeyword.
    internal static List<SymbolAndEntries>? GetAllReferencesForKeyword(SourceFile[] sourceFiles, SyntaxKind keywordKind, bool filterReadOnlyTypeOperator)
    {
        // references is a list of NodeEntry
        var references = new List<ReferenceEntry>();
        foreach (var sourceFile in sourceFiles)
        {
            // cancellationToken.throwIfCancellationRequested();
            foreach (var referenceLocation in GetPossibleSymbolReferenceNodesStatic(sourceFile, global::Ts.Scanner.Scanner.TokenToString(keywordKind), sourceFile.AsNode()))
            {
                if (referenceLocation.Kind == keywordKind && (!filterReadOnlyTypeOperator || FindRefsSupport.IsReadonlyTypeOperator(referenceLocation)))
                {
                    references.Add(FindRefsSupport.NewNodeEntry(referenceLocation));
                }
            }
        }
        if (references.Count == 0)
        {
            return null;
        }
        return [SymbolAndEntries.NewSymbolAndEntries(DefinitionKind.Keyword, references[0].Node, null, references)];
    }

    // findallreferences.go:1529-1536 — getPossibleSymbolReferenceNodes (static twin of the
    // LanguageService member in FindAllReferences.cs for the free special-family functions).
    internal static List<Node> GetPossibleSymbolReferenceNodesStatic(SourceFile sourceFile, string symbolName, Node? container)
    {
        var nodes = new List<Node>();
        foreach (var pos in GetPossibleSymbolReferencePositionsStatic(sourceFile, symbolName, container))
        {
            var referenceLocation = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, pos);
            if (referenceLocation.Kind != SyntaxKind.SourceFile)
            {
                nodes.Add(referenceLocation);
            }
        }
        return nodes;
    }

    // findallreferences.go:1538-1581 — getPossibleSymbolReferencePositions (static twin).
    internal static List<int> GetPossibleSymbolReferencePositionsStatic(SourceFile sourceFile, string symbolName, Node? container)
    {
        var positions = new List<int>();

        /// TODO: Cache symbol existence for files to save text search
        // Also, need to make this work for unicode escapes.

        // Be resilient in the face of a symbol with no name or zero length name
        if (symbolName.Length == 0)
        {
            return positions;
        }

        var text = sourceFile.Text();
        var sourceLength = text.Length;
        var symbolNameLength = symbolName.Length;

        container ??= sourceFile.AsNode();

        // Go slices the text at container.Pos() and searches relative to it; all offsets here
        // are UTF-16 code units (the repository position currency). The absolute IndexOf
        // overload keeps the loop in one currency.
        var containerPos = container.Pos;
        var endPos = container.End;

        var position = text.IndexOf(symbolName, containerPos, StringComparison.Ordinal);
        while (position >= 0 && position < endPos)
        {
            // We found a match.  Make sure it's not part of a larger word (i.e. the char
            // before and after it have to be a non-identifier char).
            var endPosition = position + symbolNameLength;

            if ((position == 0 || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[position - 1])) &&
                (endPosition == sourceLength || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[endPosition])))
            {
                // Found a real match.  Keep searching.
                positions.Add(position);
            }
            var startIndex = position + symbolNameLength + 1;
            if (startIndex > text.Length)
            {
                break;
            }
            position = text.IndexOf(symbolName, startIndex, StringComparison.Ordinal);
        }

        return positions;
    }

    // findallreferences.go:1246-1284 — getReferencesForStringLiteral.
    internal static List<SymbolAndEntries> GetReferencesForStringLiteral(
        System.Threading.CancellationToken ctx,
        Node node,
        SourceFile[] sourceFiles,
        global::Ts.Checker.Checker checker)
    {
        var t = GetContextualTypeFromParentOrAncestorTypeNode(node, checker);
        var references = new List<ReferenceEntry>();
        foreach (var sourceFile in sourceFiles)
        {
            if (ctx.IsCancellationRequested)
            {
                continue;
            }
            var possibleReferences = GetPossibleSymbolReferenceNodesStatic(sourceFile, node.Text(), null /*container*/);
            foreach (var @ref in possibleReferences)
            {
                if (AstUtilities.IsStringLiteralLike(@ref) && @ref.Text() == node.Text())
                {
                    if (t != null)
                    {
                        var refType = GetContextualTypeFromParentOrAncestorTypeNode(@ref, checker);
                        if (!ReferenceEquals(t, checker.GetStringType()) &&
                            (ReferenceEquals(t, refType) || IsStringLiteralPropertyReference(@ref, checker)))
                        {
                            references.Add(FindRefsSupport.NewNodeEntryWithKind(@ref, EntryKind.StringLiteral));
                        }
                    }
                    else
                    {
                        if (NodeTests.IsNoSubstitutionTemplateLiteral(@ref) && !CheckerFindRefsLsExports.RangeIsOnSingleLine(@ref.Loc, sourceFile))
                        {
                            continue;
                        }
                        references.Add(FindRefsSupport.NewNodeEntryWithKind(@ref, EntryKind.StringLiteral));
                    }
                }
            }
        }

        return
        [
            new SymbolAndEntries(
                new FarDefinition { Kind = DefinitionKind.String, Node = node },
                references),
        ];
    }

    // findallreferences.go:1286-1291 — isStringLiteralPropertyReference.
    internal static bool IsStringLiteralPropertyReference(Node node, global::Ts.Checker.Checker checker)
    {
        if (NodeTests.IsPropertySignatureDeclaration(node.Parent!))
        {
            return checker.GetPropertyOfType(checker.GetTypeAtLocation(node.Parent!.Parent!), node.Text()) != null;
        }
        return false;
    }

    // ls/utilities.go:1355-1372 — getContextualTypeFromParent.
    internal static global::Ts.Checker.Type? GetContextualTypeFromParent(Node node, global::Ts.Checker.Checker typeChecker, global::Ts.Checker.ContextFlags contextFlags)
    {
        var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent!)!;
        switch (parent.Kind)
        {
            case SyntaxKind.NewExpression:
                return typeChecker.GetContextualType(parent, contextFlags);
            case SyntaxKind.BinaryExpression:
                if (IsEqualityOperatorKind(parent.AsBinaryExpression().OperatorToken!.Kind))
                {
                    return typeChecker.GetTypeAtLocation(
                        node == parent.AsBinaryExpression().Right ? parent.AsBinaryExpression().Left! : parent.AsBinaryExpression().Right!);
                }
                return typeChecker.GetContextualType(node, contextFlags);
            case SyntaxKind.CaseClause:
                return GetSwitchedType(parent, typeChecker);
            default:
                return typeChecker.GetContextualType(node, contextFlags);
        }
    }

    // ls/utilities.go:1374-1392 — getContextualTypeFromParentOrAncestorTypeNode.
    internal static global::Ts.Checker.Type? GetContextualTypeFromParentOrAncestorTypeNode(Node node, global::Ts.Checker.Checker typeChecker)
    {
        if ((node.Flags & NodeFlags.JSDoc) != 0 && (node.Flags & NodeFlags.JavaScriptFile) == 0)
        {
            return null;
        }

        var contextualType = GetContextualTypeFromParent(node, typeChecker, global::Ts.Checker.ContextFlags.None);
        if (contextualType != null)
        {
            return contextualType;
        }

        if (GetAncestorTypeNode(node) is { } ancestorTypeNode)
        {
            return typeChecker.GetTypeAtLocation(ancestorTypeNode);
        }

        return null;
    }

    // ls/utilities.go:1381-1390 — getAncestorTypeNode.
    internal static Node? GetAncestorTypeNode(Node node)
    {
        Node? lastTypeNode = null;
        AstUtilities.FindAncestor(node, n =>
        {
            if (AstUtilities.IsTypeNode(n))
            {
                lastTypeNode = n;
            }
            return !NodeTests.IsQualifiedName(n.Parent!) && !AstUtilities.IsTypeNode(n.Parent!) && !AstUtilities.IsTypeElement(n.Parent!);
        });
        return lastTypeNode;
    }

    // ls/completions.go:2979-2981 — getSwitchedType.
    private static global::Ts.Checker.Type? GetSwitchedType(Node caseClause, global::Ts.Checker.Checker typeChecker)
    {
        return typeChecker.GetTypeAtLocation(caseClause.Parent!.Parent!.Expression()!);
    }

    // ls/completions.go:2983-2990 — isEqualityOperatorKind.
    private static bool IsEqualityOperatorKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.EqualsEqualsToken or
                SyntaxKind.ExclamationEqualsEqualsToken or SyntaxKind.ExclamationEqualsToken:
                return true;
            default:
                return false;
        }
    }
}
