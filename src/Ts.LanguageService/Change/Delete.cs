// Ported from tsgo internal/ls/change/delete.go (whole file): smart node deletion with
// per-kind handling for list items, imports, binding elements and variable declarations.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Ls.Change;

internal static class DeleteHelpers
{
    // delete.go:16-104 — deleteDeclaration: deletes a node with smart handling for different node
    // types. This handles special cases like import specifiers in lists, parameters, etc.
    internal static void DeleteDeclaration(Tracker t, HashSet<Node> deletedNodesInLists, SourceFile sourceFile, Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Parameter:
            {
                var oldFunction = node.Parent!;
                if (oldFunction.Kind == SyntaxKind.ArrowFunction &&
                    oldFunction.AsArrowFunction().Parameters!.Nodes.Length == 1 &&
                    global::Ts.AstNav.Tokens.FindChildOfKind(oldFunction, SyntaxKind.OpenParenToken, sourceFile) == null)
                {
                    // Lambdas with exactly one parameter are special because, after removal, there
                    // must be an empty parameter list (i.e. `()`) and this won't necessarily be the
                    // case if the parameter is simply removed (e.g. in `x => 1`).
                    t.ReplaceRangeWithText(sourceFile, t.GetAdjustedRange(sourceFile, node, node, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include), "()");
                }
                else
                {
                    DeleteNodeInList(t, deletedNodesInLists, sourceFile, node);
                }
                break;
            }

            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            {
                var imports = sourceFile.Imports() ?? [];
                var isFirstImport = imports.Length > 0 && node == imports[0].Parent ||
                    node == CoreUtil.Find(sourceFile.Statements!.Nodes, s => AstUtilities.IsAnyImportSyntax(s));
                // For first import, leave header comment in place, otherwise only delete JSDoc comments
                var leadingTrivia = LeadingTriviaOption.StartLine;
                if (isFirstImport)
                {
                    leadingTrivia = LeadingTriviaOption.Exclude;
                }
                else if (HasJSDocNodes(node))
                {
                    leadingTrivia = LeadingTriviaOption.JSDoc;
                }
                DeleteNode(t, sourceFile, node, leadingTrivia, TrailingTriviaOption.Include);
                break;
            }

            case SyntaxKind.BindingElement:
            {
                var pattern = node.Parent!;
                var preserveComma = pattern.Kind == SyntaxKind.ArrayBindingPattern &&
                    node != pattern.AsBindingPattern().Elements!.Nodes[^1];
                if (preserveComma)
                {
                    DeleteNode(t, sourceFile, node, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Exclude);
                }
                else
                {
                    DeleteNodeInList(t, deletedNodesInLists, sourceFile, node);
                }
                break;
            }

            case SyntaxKind.VariableDeclaration:
                DeleteVariableDeclaration(t, deletedNodesInLists, sourceFile, node);
                break;

            case SyntaxKind.TypeParameter:
                DeleteNodeInList(t, deletedNodesInLists, sourceFile, node);
                break;

            case SyntaxKind.ImportSpecifier:
            {
                var namedImports = node.Parent!;
                if (namedImports.AsNamedImports().Elements!.Nodes.Length == 1)
                {
                    DeleteImportBinding(t, sourceFile, namedImports);
                }
                else
                {
                    DeleteNodeInList(t, deletedNodesInLists, sourceFile, node);
                }
                break;
            }

            case SyntaxKind.NamespaceImport:
                DeleteImportBinding(t, sourceFile, node);
                break;

            case SyntaxKind.SemicolonToken:
                DeleteNode(t, sourceFile, node, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Exclude);
                break;

            case SyntaxKind.TypeKeyword:
                // For type keyword in import clauses, we need to delete the keyword and any trailing space
                // The trailing space is part of the next token's leading trivia, so we include it
                DeleteNode(t, sourceFile, node, LeadingTriviaOption.Exclude, TrailingTriviaOption.Include);
                break;

            case SyntaxKind.FunctionKeyword:
                DeleteNode(t, sourceFile, node, LeadingTriviaOption.Exclude, TrailingTriviaOption.Include);
                break;

            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.FunctionDeclaration:
            {
                var leadingTrivia = LeadingTriviaOption.StartLine;
                if (HasJSDocNodes(node))
                {
                    leadingTrivia = LeadingTriviaOption.JSDoc;
                }
                DeleteNode(t, sourceFile, node, leadingTrivia, TrailingTriviaOption.Include);
                break;
            }

            default:
                if (node.Parent == null)
                {
                    // a misbehaving client can reach here with the SourceFile node
                    DeleteNode(t, sourceFile, node, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
                }
                else if (node.Parent.Kind == SyntaxKind.ImportClause && node.Parent.AsImportClause().Name() == node)
                {
                    DeleteDefaultImport(t, sourceFile, node.Parent);
                }
                else if (node.Parent.Kind == SyntaxKind.CallExpression && Array.IndexOf(node.Parent.AsCallExpression().Arguments!.Nodes, node) >= 0)
                {
                    DeleteNodeInList(t, deletedNodesInLists, sourceFile, node);
                }
                else
                {
                    DeleteNode(t, sourceFile, node, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
                }
                break;
        }
    }

    // delete.go:106-127 — deleteDefaultImport.
    internal static void DeleteDefaultImport(Tracker t, SourceFile sourceFile, Node importClause)
    {
        var clause = importClause.AsImportClause();
        if (clause.NamedBindings == null)
        {
            // Delete the whole import
            DeleteNode(t, sourceFile, importClause.Parent!, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
        }
        else
        {
            // import |d,| * as ns from './file'
            var name = clause.Name()!;
            var start = global::Ts.AstNav.Tokens.GetStartOfNode(name, sourceFile, false);
            var nextToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, name.End);
            if (nextToken != null && nextToken.Kind == SyntaxKind.CommaToken)
            {
                // shift first non-whitespace position after comma to the start position of the node
                var end = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), nextToken.End, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = false, StopAtComments = true });
                var startPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), start);
                var endPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), end);
                t.ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = startPos, End = endPos }, "");
            }
            else
            {
                DeleteNode(t, sourceFile, name, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
            }
        }
    }

    // delete.go:129-148 — deleteImportBinding.
    internal static void DeleteImportBinding(Tracker t, SourceFile sourceFile, Node node)
    {
        var importClause = node.Parent!.AsImportClause();
        if (importClause.Name() != null)
        {
            // Delete named imports while preserving the default import
            // import d|, * as ns| from './file'
            // import d|, { a }| from './file'
            var previousToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, node.Pos - 1);
            global::Ts.Core.Debug.Assert(previousToken != null, "previousToken should not be nil");
            var startPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), global::Ts.AstNav.Tokens.GetStartOfNode(previousToken!, sourceFile, false));
            var endPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), node.End);
            t.ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = startPos, End = endPos }, "");
        }
        else
        {
            // Delete the entire import declaration
            // |import * as ns from './file'|
            // |import { a } from './file'|
            var importDecl = AstUtilities.FindAncestorKind(node, SyntaxKind.ImportDeclaration);
            global::Ts.Core.Debug.Assert(importDecl != null, "importDecl should not be nil");
            DeleteNode(t, sourceFile, importDecl!, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
        }
    }

    // delete.go:150-185 — deleteVariableDeclaration.
    internal static void DeleteVariableDeclaration(Tracker t, HashSet<Node> deletedNodesInLists, SourceFile sourceFile, Node node)
    {
        var parent = node.Parent!;

        if (parent.Kind == SyntaxKind.CatchClause)
        {
            // TODO: There's currently no unused diagnostic for this, could be a suggestion
            var openParen = global::Ts.AstNav.Tokens.FindChildOfKind(parent, SyntaxKind.OpenParenToken, sourceFile);
            var closeParen = global::Ts.AstNav.Tokens.FindChildOfKind(parent, SyntaxKind.CloseParenToken, sourceFile);
            global::Ts.Core.Debug.Assert(openParen != null && closeParen != null, "catch clause should have parens");
            t.DeleteNodeRange(sourceFile, openParen!, closeParen!, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
            return;
        }

        if (parent.AsVariableDeclarationList().Declarations!.Nodes.Length != 1)
        {
            DeleteNodeInList(t, deletedNodesInLists, sourceFile, node);
            return;
        }

        var gp = parent.Parent!;
        switch (gp.Kind)
        {
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.ForInStatement:
                t.ReplaceNode(sourceFile, node, t.NodeFactory.NewObjectLiteralExpression(t.NodeFactory.NewNodeList([]), false), null);
                break;

            case SyntaxKind.ForStatement:
                DeleteNode(t, sourceFile, parent, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
                break;

            case SyntaxKind.VariableStatement:
            {
                var leadingTrivia = LeadingTriviaOption.StartLine;
                if (HasJSDocNodes(gp))
                {
                    leadingTrivia = LeadingTriviaOption.JSDoc;
                }
                DeleteNode(t, sourceFile, gp, leadingTrivia, TrailingTriviaOption.Include);
                break;
            }

            default:
                global::Ts.Core.Debug.Fail("Unexpected grandparent kind: " + gp.Kind);
                break;
        }
    }

    // delete.go:187-195 — deleteNode: deletes a node with the specified trivia options.
    // Warning: This deletes comments too.
    internal static void DeleteNode(Tracker t, SourceFile sourceFile, Node node, LeadingTriviaOption leadingTrivia, TrailingTriviaOption trailingTrivia)
    {
        var startPosition = t.GetAdjustedStartPosition(sourceFile, node, leadingTrivia, false);
        var endPosition = t.GetAdjustedEndPosition(sourceFile, node, trailingTrivia);
        var startPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), startPosition);
        var endPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), endPosition);
        t.ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = startPos, End = endPos }, "");
    }

    // delete.go:197-228 — deleteNodeInList.
    internal static void DeleteNodeInList(Tracker t, HashSet<Node> deletedNodesInLists, SourceFile sourceFile, Node node)
    {
        var containingList = global::Ts.Format.Indent.GetContainingList(node, sourceFile);
        global::Ts.Core.Debug.Assert(containingList != null, "containingList should not be nil");
        var index = Array.IndexOf(containingList!.Nodes, node);
        global::Ts.Core.Debug.Assert(index != -1, "node should be in containing list");

        if (containingList.Nodes.Length == 1)
        {
            DeleteNode(t, sourceFile, node, LeadingTriviaOption.IncludeAll, TrailingTriviaOption.Include);
            return;
        }

        // Note: We will only delete a comma *after* a node. This will leave a trailing comma if we delete the last node.
        // That's handled in the end by finishTrailingCommaAfterDeletingNodesInList.
        global::Ts.Core.Debug.Assert(!deletedNodesInLists.Contains(node), "Deleting a node twice");
        deletedNodesInLists.Add(node);

        var startPos = t.StartPositionToDeleteNodeInList(sourceFile, node);
        int endPos;
        if (index == containingList.Nodes.Length - 1)
        {
            endPos = t.GetAdjustedEndPosition(sourceFile, node, TrailingTriviaOption.None);
        }
        else
        {
            Node? prevNode = null;
            if (index > 0)
            {
                prevNode = containingList.Nodes[index - 1];
            }
            endPos = t.EndPositionToDeleteNodeInList(sourceFile, node, prevNode, containingList.Nodes[index + 1]);
        }

        var startLSPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), startPos);
        var endLSPos = t.converters.PositionToLineAndCharacter(sourceFile.AsScript(), endPos);
        t.ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = startLSPos, End = endLSPos }, "");
    }

    // delete.go:262-264 — positionsAreOnSameLine (shared through the Tracker partial below).

    // delete.go:266-270 — hasJSDocNodes: checks if a node has JSDoc comments.
    internal static bool HasJSDocNodes(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        // nil is ok for JSDoc - it will return empty slice if not available
        var jsdocs = node.JSDoc(null);
        return (jsdocs ?? []).Length > 0;
    }
}

public sealed partial class Tracker
{
    // delete.go:230-235 — startPositionToDeleteNodeInList: finds the first non-whitespace position
    // in the leading trivia of the node.
    internal int StartPositionToDeleteNodeInList(SourceFile sourceFile, Node node)
    {
        var start = GetAdjustedStartPosition(sourceFile, node, LeadingTriviaOption.IncludeAll, false);
        return global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), start, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = false, StopAtComments = true });
    }

    // delete.go:237-260 — endPositionToDeleteNodeInList.
    internal int EndPositionToDeleteNodeInList(SourceFile sourceFile, Node node, Node? prevNode, Node nextNode)
    {
        var end = StartPositionToDeleteNodeInList(sourceFile, nextNode);
        if (prevNode == null || PositionsAreOnSameLine(GetAdjustedEndPosition(sourceFile, node, TrailingTriviaOption.Include), end, sourceFile))
        {
            return end;
        }
        var token = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, global::Ts.AstNav.Tokens.GetStartOfNode(nextNode, sourceFile, false));
        if (IsSeparator(node, token))
        {
            var prevToken = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false));
            if (IsSeparator(prevNode, prevToken))
            {
                var pos = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), token!.End, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = true, StopAtComments = true });
                if (PositionsAreOnSameLine(global::Ts.AstNav.Tokens.GetStartOfNode(prevToken!, sourceFile, false), global::Ts.AstNav.Tokens.GetStartOfNode(token, sourceFile, false), sourceFile))
                {
                    if (pos > 0 && global::Ts.StringUtil.StringUtil.IsLineBreak(sourceFile.Text()[pos - 1]))
                    {
                        return pos - 1;
                    }
                    return pos;
                }
                if (global::Ts.StringUtil.StringUtil.IsLineBreak(sourceFile.Text()[pos]))
                {
                    return pos;
                }
            }
        }
        return end;
    }

    // delete.go:262-264 — positionsAreOnSameLine.
    internal static bool PositionsAreOnSameLine(int pos1, int pos2, SourceFile sourceFile)
    {
        return global::Ts.Format.Util.GetLineStartPositionForPosition(pos1, sourceFile) == global::Ts.Format.Util.GetLineStartPositionForPosition(pos2, sourceFile);
    }
}
