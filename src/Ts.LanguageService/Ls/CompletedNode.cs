// Ported from tsgo internal/ls/lsutil/completednode.go (whole file).

using Ts.Ast;

namespace Ts.Ls;

internal static class CompletedNode
{
    // completednode.go:10-17 — PositionBelongsToNode returns true if the position belongs to the node.
    // Assumes `candidate.Pos() <= position` holds.
    internal static bool PositionBelongsToNode(Node candidate, int position, SourceFile file)
    {
        if (candidate.Pos > position)
        {
            throw new global::System.InvalidOperationException("Expected candidate.pos <= position");
        }
        return position < candidate.End || !IsCompletedNode(candidate, file);
    }

    // completednode.go:19-158 — IsCompletedNode.
    internal static bool IsCompletedNode(Node? n, SourceFile sourceFile)
    {
        if (n == null || AstUtilities.NodeIsMissing(n))
        {
            return false;
        }

        switch (n.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.CaseBlock:
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
                return NodeEndsWith(n, SyntaxKind.CloseBraceToken, sourceFile);

            case SyntaxKind.CatchClause:
                return IsCompletedNode(n.AsCatchClause().Block, sourceFile);

            case SyntaxKind.NewExpression:
                if (n.ArgumentList() == null)
                {
                    return true;
                }
                goto case SyntaxKind.CallExpression;

            case SyntaxKind.CallExpression:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.ParenthesizedType:
                return NodeEndsWith(n, SyntaxKind.CloseParenToken, sourceFile);

            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
                return IsCompletedNode(n.Type(), sourceFile);

            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ArrowFunction:
                if (n.Body() != null)
                {
                    return IsCompletedNode(n.Body(), sourceFile);
                }
                if (n.Type() != null)
                {
                    return IsCompletedNode(n.Type(), sourceFile);
                }
                // Even though type parameters can be unclosed, we can get away with
                // having at least a closing paren.
                return HasChildOfKind(n, SyntaxKind.CloseParenToken, sourceFile);

            case SyntaxKind.ModuleDeclaration:
                return n.Body() != null && IsCompletedNode(n.Body(), sourceFile);

            case SyntaxKind.IfStatement:
                if (n.AsIfStatement().ElseStatement != null)
                {
                    return IsCompletedNode(n.AsIfStatement().ElseStatement, sourceFile);
                }
                return IsCompletedNode(n.AsIfStatement().ThenStatement, sourceFile);

            case SyntaxKind.ExpressionStatement:
                return IsCompletedNode(n.Expression(), sourceFile) ||
                    HasChildOfKind(n, SyntaxKind.SemicolonToken, sourceFile);

            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ArrayBindingPattern:
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.ComputedPropertyName:
            case SyntaxKind.TupleType:
                return NodeEndsWith(n, SyntaxKind.CloseBracketToken, sourceFile);

            case SyntaxKind.IndexSignature:
                if (n.AsIndexSignatureDeclaration().Type != null)
                {
                    return IsCompletedNode(n.AsIndexSignatureDeclaration().Type, sourceFile);
                }
                return HasChildOfKind(n, SyntaxKind.CloseBracketToken, sourceFile);

            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                // there is no such thing as terminator token for CaseClause/DefaultClause so for simplicity always consider them non-completed
                return false;

            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.WhileStatement:
                return IsCompletedNode(n.Statement(), sourceFile);
            case SyntaxKind.DoStatement:
                // rough approximation: if DoStatement has While keyword - then if node is completed is checking the presence of ')';
                if (HasChildOfKind(n, SyntaxKind.WhileKeyword, sourceFile))
                {
                    return NodeEndsWith(n, SyntaxKind.CloseParenToken, sourceFile);
                }
                return IsCompletedNode(n.Statement(), sourceFile);

            case SyntaxKind.TypeQuery:
                return IsCompletedNode(n.AsTypeQueryNode().ExprName, sourceFile);

            case SyntaxKind.TypeOfExpression:
            case SyntaxKind.DeleteExpression:
            case SyntaxKind.VoidExpression:
            case SyntaxKind.YieldExpression:
            case SyntaxKind.SpreadElement:
                return IsCompletedNode(n.Expression(), sourceFile);

            case SyntaxKind.TaggedTemplateExpression:
                return IsCompletedNode(n.AsTaggedTemplateExpression().Template, sourceFile);

            case SyntaxKind.TemplateExpression:
                if (n.AsTemplateExpression().TemplateSpans == null)
                {
                    return false;
                }
                var lastSpan = global::Ts.Core.CoreUtil.LastOrNil(n.AsTemplateExpression().TemplateSpans!.Nodes);
                return IsCompletedNode(lastSpan, sourceFile);

            case SyntaxKind.TemplateSpan:
                return AstUtilities.NodeIsPresent(n.AsTemplateSpan().Literal);

            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ImportDeclaration:
                return AstUtilities.NodeIsPresent(n.ModuleSpecifier());

            case SyntaxKind.PrefixUnaryExpression:
                return IsCompletedNode(n.AsPrefixUnaryExpression().Operand, sourceFile);

            case SyntaxKind.BinaryExpression:
                return IsCompletedNode(n.AsBinaryExpression().Right, sourceFile);

            case SyntaxKind.ConditionalExpression:
                return IsCompletedNode(n.AsConditionalExpression().WhenFalse, sourceFile);

            default:
                return true;
        }
    }

    // completednode.go:160-190 — Checks if node ends with 'expectedLastToken'.
    // If child at position 'length - 1' is 'SemicolonToken' it is skipped and 'expectedLastToken' is compared with child at position 'length - 2'.
    private static bool NodeEndsWith(Node n, SyntaxKind expectedLastToken, SourceFile sourceFile)
    {
        var lastChildNode = LsDeps.GetLastVisitedChild(n, sourceFile);
        var lastNodeAndTokens = new global::System.Collections.Generic.List<Node>();
        int tokenStartPos;
        if (lastChildNode != null)
        {
            lastNodeAndTokens.Add(lastChildNode);
            tokenStartPos = lastChildNode.End;
        }
        else
        {
            tokenStartPos = n.Pos;
        }
        var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, tokenStartPos);
        for (var startPos = tokenStartPos; startPos < n.End;)
        {
            var tokenKind = scanner.Token();
            var tokenFullStart = scanner.TokenFullStart();
            var tokenEnd = scanner.TokenEnd();
            var token = sourceFile.GetOrCreateToken(tokenKind, tokenFullStart, tokenEnd, n, scanner.TokenFlags());
            lastNodeAndTokens.Add(token);
            startPos = tokenEnd;
            scanner.Scan();
        }
        if (lastNodeAndTokens.Count == 0)
        {
            return false;
        }
        var lastChild = lastNodeAndTokens[^1];
        if (lastChild.Kind == expectedLastToken)
        {
            return true;
        }
        else if (lastChild.Kind == SyntaxKind.SemicolonToken && lastNodeAndTokens.Count > 1)
        {
            return lastNodeAndTokens[^2].Kind == expectedLastToken;
        }
        return false;
    }

    // completednode.go:192-194 — hasChildOfKind.
    private static bool HasChildOfKind(Node containingNode, SyntaxKind kind, SourceFile sourceFile)
    {
        return global::Ts.AstNav.Tokens.FindChildOfKind(containingNode, kind, sourceFile) != null;
    }
}
