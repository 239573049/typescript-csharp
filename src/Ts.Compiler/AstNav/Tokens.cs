using System;
using System.Collections.Generic;
using Ts.Ast;
// PORT-NOTE (plan/05 §1.5): Ts.Scanner and Ts.Core are NOT imported — within namespace Ts.AstNav
// the bare names `Scanner`/`Core` bind to those sibling namespaces (of Ts), not the classes, so
// every cross-package package-level call is fully qualified via global:: (Scanner class,
// Ts.Core.Core.BinarySearchUniqueFunc, Ts.Core.CoreUtil.*).

namespace Ts.AstNav;

// Go: internal/astnav/tokens.go — AST position navigation (GetTokenAtPosition/GetTouchingToken
// family, FindPrecedingToken, FindNextToken, FindChildOfKind), shared by the language service and
// checker. Ported 1:1 in Go source order. Positions are UTF-16 code unit offsets (plan/01);
// positionmap.go is intentionally not ported.
//
// PORT-NOTE (plan/05 §1.5 naming): the package-level functions live in the static class Tokens
// (matching tokens.go, plan/06 §4.3), NOT a class named AstNav — a class named after the last
// namespace segment (Ts.AstNav.AstNav) is exactly the CS0234 alias-shadowing hazard §1.5 forbids.
// Cross-package callers use global::Ts.AstNav.Tokens.GetTouchingToken(...).
public static class Tokens
{
    // tokens.go:11
    private static bool ShouldRescanLessThanLessThanToken(global::Ts.Scanner.Scanner s, Node containingNode, SyntaxKind token)
    {
        return token == SyntaxKind.LessThanLessThanToken && AstUtilities.IsJsxChild(containingNode);
    }

    // tokens.go:15
    private static SyntaxKind ScanNavigationToken(global::Ts.Scanner.Scanner s, Node containingNode)
    {
        var token = s.Token();
        if (ShouldRescanLessThanLessThanToken(s, containingNode, token))
        {
            return s.ReScanJsxToken(true /*allowMultilineJsxText*/);
        }
        return token;
    }

    // tokens.go:23
    public static Node GetTouchingPropertyName(SourceFile sourceFile, int position)
    {
        return GetTokenAtPositionCore(sourceFile, position, false /*allowPositionInLeadingTrivia*/, node =>
            AstUtilities.IsPropertyNameLiteral(node) || NodeTests.IsKeywordKind(node.Kind) || NodeTests.IsPrivateIdentifier(node));
    }

    // tokens.go:29
    public static Node GetTouchingToken(SourceFile sourceFile, int position)
    {
        return GetTokenAtPositionCore(sourceFile, position, false /*allowPositionInLeadingTrivia*/, null);
    }

    // tokens.go:33
    public static Node GetTokenAtPosition(SourceFile sourceFile, int position)
    {
        return GetTokenAtPositionCore(sourceFile, position, true /*allowPositionInLeadingTrivia*/, null);
    }

    // tokens.go:37 (getTokenAtPosition)
    //
    // getTokenAtPosition returns a token at the given position in the source file.
    // The token can be a real node in the AST, or a synthesized token constructed
    // with information from the scanner. Synthesized tokens are only created when
    // needed, and they are stored in the source file's token cache such that multiple
    // calls to getTokenAtPosition with the same position will return the same object
    // in memory. If there is no token at the given position (possible when
    // `allowPositionInLeadingTrivia` is false), the lowest node that encloses the
    // position is returned.
    private static Node GetTokenAtPositionCore(
        SourceFile sourceFile,
        int position,
        bool allowPositionInLeadingTrivia,
        Func<Node, bool>? includePrecedingTokenAtEndPosition)
    {
        // `next` tracks the node whose children will be visited on the next iteration.
        // `prevSubtree` is a node whose end position is equal to the target position,
        // only if `includePrecedingTokenAtEndPosition` is provided. Once set, the next
        // iteration of the loop will test the rightmost token of `prevSubtree` to see
        // if it should be returned.
        Node? next = null;
        Node? prevSubtree = null;
        var current = sourceFile.AsNode();
        // `left` tracks the lower boundary of the node/token that could be returned,
        // and is eventually the scanner's start position, if the scanner is used.
        var left = 0;
        // `nodeAfterLeft` tracks the first node we visit after visiting the node that advances `left`.
        // When scanning in between nodes for token, we should only scan up to the start of `nodeAfterLeft`.
        Node? nodeAfterLeft = null;

        int TestNode(Node node)
        {
            if (node.Kind != SyntaxKind.EndOfFile && node.End == position &&
                includePrecedingTokenAtEndPosition != null && (node.Flags & NodeFlags.Reparsed) == 0)
            {
                prevSubtree = node;
            }

            // A node "contains" the position if position < end, except nodes at the file end
            // treat end as inclusive (there's nowhere else to look). This applies to the EOF
            // token itself, and to JSDoc nodes reaching EOF (e.g. unterminated JSDoc comments).
            if (node.End < position || node.End == position &&
                node.Kind != SyntaxKind.EndOfFile &&
                (!AstUtilities.IsJSDocKind(node.Kind) || node.End != sourceFile.EndOfFileToken!.End))
            {
                return -1;
            }
            var nodePos = GetPosition(node, sourceFile, allowPositionInLeadingTrivia);
            if (nodePos > position)
            {
                return 1;
            }
            return 0;
        }

        // We zero in on the node that contains the target position by visiting each
        // child and JSDoc comment of the current node. Node children are walked in
        // order, while node lists are binary searched.
        Node? VisitNode(Node? node, NodeVisitor _)
        {
            // We can't abort visiting children, so once a match is found, we set `next`
            // and do nothing on subsequent visits.
            if (node == null || (node.Flags & NodeFlags.Reparsed) != 0)
            {
                return null;
            }
            if (nodeAfterLeft == null)
            {
                nodeAfterLeft = node;
            }
            if (next == null)
            {
                var result = TestNode(node);
                switch (result)
                {
                    case -1:
                        if (!AstUtilities.IsJSDocKind(node.Kind))
                        {
                            // We can't move the left boundary into or beyond JSDoc,
                            // because we may end up returning the token after this JSDoc,
                            // constructing it with the scanner, and we need to include
                            // all its leading trivia in its position.
                            left = node.End;
                        }
                        nodeAfterLeft = null;
                        break;
                    case 0:
                        next = node;
                        break;
                }
            }
            return node;
        }

        NodeList? VisitNodeList(NodeList? nodeList, NodeVisitor _)
        {
            if (nodeList == null || nodeList.Nodes.Length == 0)
            {
                return nodeList;
            }
            if (nodeAfterLeft == null)
            {
                foreach (var node in nodeList.Nodes)
                {
                    if ((node.Flags & NodeFlags.Reparsed) == 0)
                    {
                        nodeAfterLeft = node;
                        break;
                    }
                }
            }
            if (next == null)
            {
                if (nodeList.End == position && includePrecedingTokenAtEndPosition != null)
                {
                    left = nodeList.End;
                    nodeAfterLeft = null;
                    for (int i = nodeList.Nodes.Length - 1; i >= 0; i--)
                    {
                        if ((nodeList.Nodes[i].Flags & NodeFlags.Reparsed) == 0)
                        {
                            prevSubtree = nodeList.Nodes[i];
                            break;
                        }
                    }
                }
                else if (nodeList.End <= position)
                {
                    left = nodeList.End;
                    nodeAfterLeft = null;
                }
                else if (nodeList.Pos <= position)
                {
                    var nodes = nodeList.Nodes;
                    var (index, match) = global::Ts.Core.Core.BinarySearchUniqueFunc(nodes, (middle, node) =>
                    {
                        if ((node.Flags & NodeFlags.Reparsed) != 0)
                        {
                            return 0;
                        }
                        var cmp = TestNode(node);
                        if (cmp < 0)
                        {
                            left = node.End;
                            nodeAfterLeft = null;
                            for (int i = middle + 1; i < nodes.Length; i++)
                            {
                                if ((nodes[i].Flags & NodeFlags.Reparsed) == 0)
                                {
                                    nodeAfterLeft = nodes[i];
                                    break;
                                }
                            }
                        }
                        return cmp;
                    });
                    if (match && (nodes[index].Flags & NodeFlags.Reparsed) != 0)
                    {
                        // filter and search again
                        nodes = global::Ts.Core.CoreUtil.Filter(nodes, node => (node.Flags & NodeFlags.Reparsed) == 0)!;
                        (index, match) = global::Ts.Core.Core.BinarySearchUniqueFunc(nodes, (middle, node) =>
                        {
                            var cmp = TestNode(node);
                            if (cmp < 0)
                            {
                                left = node.End;
                                if (middle + 1 < nodes.Length)
                                {
                                    nodeAfterLeft = nodes[middle + 1];
                                }
                                else
                                {
                                    nodeAfterLeft = null;
                                }
                            }
                            return cmp;
                        });
                    }
                    if (match)
                    {
                        next = nodes[index];
                    }
                }
            }
            return nodeList;
        }

        while (true)
        {
            VisitEachChildAndJSDoc(current, sourceFile, VisitNode, VisitNodeList);
            // If prevSubtree was set on the last iteration, it ends at the target position.
            // Check if the rightmost token of prevSubtree should be returned based on the
            // `includePrecedingTokenAtEndPosition` callback.
            if (prevSubtree != null)
            {
                var child = FindPrecedingTokenEx(sourceFile, position, prevSubtree, false /*excludeJSDoc*/);
                if (child != null && child.End == position && includePrecedingTokenAtEndPosition!(child))
                {
                    // Optimization: includePrecedingTokenAtEndPosition only ever returns true
                    // for real AST nodes, so we don't run the scanner here.
                    return child;
                }
                prevSubtree = null;
            }

            // No node was found that contains the target position, so we've gone as deep as
            // we can in the AST. We've either found a token, or we need to run the scanner
            // to construct one that isn't stored in the AST.
            if (next == null)
            {
                if (NodeTests.IsTokenKind(current.Kind) || ShouldSkipChild(current))
                {
                    return current;
                }
                var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, left);
                var end = current.End;
                // We should only scan up to the start of the next node in the AST after the node ending at position `left`.
                // It is necessary to enforce this invariant in cases where `position` occurs in between two node/tokens,
                // such that we would not find a token in the loop below before we reach the next node.
                // We can fall into this case when `allowPositionInLeadingTrivia` is false and `position` is in a leading trivia,
                // or when `position` would be in the leading trivia of a node but this node is inside JSDoc.
                if (nodeAfterLeft != null)
                {
                    end = nodeAfterLeft.Pos;
                }
                while (left < end)
                {
                    var token = ScanNavigationToken(scanner, current);
                    var tokenFullStart = scanner.TokenFullStart();
                    var tokenStart = global::Ts.Core.CoreUtil.IfElse(allowPositionInLeadingTrivia, tokenFullStart, scanner.TokenStart());
                    var tokenEnd = scanner.TokenEnd();
                    var flags = scanner.TokenFlags();
                    if (tokenEnd > end)
                    {
                        break;
                    }
                    if (tokenStart <= position && (position < tokenEnd))
                    {
                        if (token == SyntaxKind.Identifier || !NodeTests.IsTokenKind(token))
                        {
                            if (AstUtilities.IsJSDocKind(current.Kind))
                            {
                                return current;
                            }
                            throw new InvalidOperationException($"did not expect {current.Kind.ToString()} to have {token.ToString()} in its trivia");
                        }
                        return sourceFile.GetOrCreateToken(token, tokenFullStart, tokenEnd, current, flags);
                    }
                    if (includePrecedingTokenAtEndPosition != null && tokenEnd == position)
                    {
                        var prevToken = sourceFile.GetOrCreateToken(token, tokenFullStart, tokenEnd, current, flags);
                        if (includePrecedingTokenAtEndPosition(prevToken))
                        {
                            return prevToken;
                        }
                    }
                    left = tokenEnd;
                    scanner.Scan();
                }
                return current;
            }
            current = next;
            left = current.Pos;
            nodeAfterLeft = null;
            next = null;
        }
    }

    // tokens.go:265
    private static int GetPosition(Node node, SourceFile sourceFile, bool allowPositionInLeadingTrivia)
    {
        if (allowPositionInLeadingTrivia)
        {
            return node.Pos;
        }
        return global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, true /*includeJSDoc*/);
    }

    // tokens.go:272
    private static Node FindRightmostNode(Node node)
    {
        Node? next = null;
        var current = node;
        Node? VisitNode(Node? n, NodeVisitor _)
        {
            if (n != null)
            {
                next = n;
            }
            return n;
        }
        NodeList? VisitNodes(NodeList? nodeList, NodeVisitor visitor)
        {
            if (nodeList != null)
            {
                var rightmost = AstUtilities.FindLastVisibleNode(nodeList.Nodes);
                if (rightmost != null)
                {
                    next = rightmost;
                }
            }
            return nodeList;
        }
        var visitor = GetNodeVisitor(VisitNode, VisitNodes);

        while (true)
        {
            current.VisitEachChild(visitor);
            if (next == null)
            {
                return current;
            }
            current = next;
            next = null;
        }
    }

    // tokens.go:301
    public static void VisitEachChildAndJSDoc(
        Node node,
        SourceFile sourceFile,
        Func<Node?, NodeVisitor, Node?> visitNode,
        Func<NodeList?, NodeVisitor, NodeList?> visitNodes)
    {
        var visitor = GetNodeVisitor(visitNode, visitNodes);
        foreach (var jsdoc in node.JSDoc(sourceFile) ?? [])
        {
            if (visitor.Hooks.VisitNode != null)
            {
                visitor.Hooks.VisitNode(jsdoc, visitor);
            }
            else
            {
                visitor.VisitNode(jsdoc);
            }
        }
        node.VisitEachChild(visitor);
    }

    // tokens.go:318
    private const int ComparisonLessThan = -1;
    private const int ComparisonEqualTo = 0;
    private const int ComparisonGreaterThan = 1;

    // tokens.go:328
    // Finds the leftmost token satisfying `position < token.End()`.
    // If the leftmost token satisfying `position < token.End()` is invalid, or if position
    // is in the trivia of that leftmost token,
    // we will find the rightmost valid token with `token.End() <= position`.
    public static Node? FindPrecedingToken(SourceFile sourceFile, int position)
    {
        return FindPrecedingTokenEx(sourceFile, position, null, false);
    }

    // tokens.go:332
    public static Node? FindPrecedingTokenEx(SourceFile sourceFile, int position, Node? startNode, bool excludeJSDoc)
    {
        Node? Find(Node n)
        {
            if (AstUtilities.IsNonWhitespaceToken(n) && n.Kind != SyntaxKind.EndOfFile)
            {
                return n;
            }

            // `foundChild` is the leftmost node that contains the target position.
            // `prevChild` is the last visited child of the current node.
            Node? foundChild = null;
            Node? prevChild = null;
            Node? VisitNode(Node? node, NodeVisitor _)
            {
                // skip synthesized nodes (that will exist now because of jsdoc handling)
                if (node == null || (node.Flags & NodeFlags.Reparsed) != 0)
                {
                    return node;
                }
                if (foundChild != null) // We cannot abort visiting children, so once the desired child is found, we do nothing.
                {
                    return node;
                }
                if (position < node.End && (prevChild == null || prevChild.End <= position))
                {
                    foundChild = node;
                }
                else
                {
                    prevChild = node;
                }
                return node;
            }
            NodeList? VisitNodes(NodeList? nodeList, NodeVisitor _)
            {
                if (foundChild != null)
                {
                    return nodeList;
                }
                if (nodeList != null && nodeList.Nodes.Length > 0)
                {
                    var nodes = nodeList.Nodes;
                    var (index, match) = global::Ts.Core.Core.BinarySearchUniqueFunc(nodes, (middle, _) =>
                    {
                        // synthetic jsdoc nodes should have jsdocNode.End() <= n.Pos()
                        if ((nodes[middle].Flags & NodeFlags.Reparsed) != 0)
                        {
                            return ComparisonLessThan;
                        }
                        if (position < nodes[middle].End)
                        {
                            if (middle == 0 || position >= nodes[middle - 1].End)
                            {
                                return ComparisonEqualTo;
                            }
                            return ComparisonGreaterThan;
                        }
                        return ComparisonLessThan;
                    });

                    if (match)
                    {
                        foundChild = nodes[index];
                    }

                    var validLookupIndex = global::Ts.Core.CoreUtil.IfElse(match, index - 1, nodes.Length - 1);
                    for (int i = validLookupIndex; i >= 0; i--)
                    {
                        if ((nodes[i].Flags & NodeFlags.Reparsed) != 0)
                        {
                            continue;
                        }
                        if (prevChild == null)
                        {
                            prevChild = nodes[i];
                        }
                    }
                }
                return nodeList;
            }
            VisitEachChildAndJSDoc(n, sourceFile, VisitNode, VisitNodes);

            if (foundChild != null)
            {
                // Note that the span of a node's tokens is [getStartOfNode(node, ...), node.end).
                // Given that `position < child.end` and child has constituent tokens, we distinguish these cases:
                // 1) `position` precedes `child`'s tokens or `child` has no tokens (ie: in a comment or whitespace preceding `child`):
                // we need to find the last token in a previous child node or child tokens.
                // 2) `position` is within the same span: we recurse on `child`.
                var start = GetStartOfNode(foundChild, sourceFile, !excludeJSDoc /*includeJSDoc*/);
                var lookInPreviousChild = start >= position || // cursor in the leading trivia or preceding tokens
                    !IsValidPrecedingNode(foundChild, sourceFile);
                if (lookInPreviousChild)
                {
                    if (position >= foundChild.Pos)
                    {
                        // Find jsdoc preceding the foundChild.
                        Node? jsDoc = null;
                        var nodeJSDoc = n.JSDoc(sourceFile);
                        for (int i = (nodeJSDoc?.Length ?? 0) - 1; i >= 0; i--)
                        {
                            if (nodeJSDoc![i].Pos >= foundChild.Pos)
                            {
                                jsDoc = nodeJSDoc[i];
                                break;
                            }
                        }
                        if (jsDoc != null)
                        {
                            if (!excludeJSDoc && position < jsDoc.End)
                            {
                                return Find(jsDoc);
                            }
                            else
                            {
                                return FindRightmostValidToken(jsDoc.End, sourceFile, n, position, excludeJSDoc);
                            }
                        }
                        return FindRightmostValidToken(foundChild.Pos, sourceFile, n, -1 /*position*/, excludeJSDoc);
                    }
                    else // Answer is in tokens between two visited children.
                    {
                        return FindRightmostValidToken(foundChild.Pos, sourceFile, n, position, excludeJSDoc);
                    }
                }
                else
                {
                    // position is in [foundChild.getStart(), foundChild.End): recur.
                    return Find(foundChild);
                }
            }

            // We have two cases here: either the position is at the end of the file,
            // or the desired token is in the unvisited trailing tokens of the current node.
            if (position >= n.End)
            {
                return FindRightmostValidToken(n.End, sourceFile, n, -1 /*position*/, excludeJSDoc);
            }
            else
            {
                return FindRightmostValidToken(n.End, sourceFile, n, position, excludeJSDoc);
            }
        }

        Node node = startNode ?? sourceFile.AsNode();
        var result = Find(node);
        if (result != null && AstUtilities.IsWhitespaceOnlyJsxText(result))
        {
            throw new InvalidOperationException("Expected result to be a non-whitespace token.");
        }
        return result;
    }

    // tokens.go:454
    private static bool IsValidPrecedingNode(Node node, SourceFile sourceFile)
    {
        if (node.Kind == SyntaxKind.EndOfFile)
        {
            return (node.JSDoc(sourceFile)?.Length ?? 0) > 0;
        }
        var start = GetStartOfNode(node, sourceFile, false /*includeJSDoc*/);
        var width = node.End - start;
        return !(AstUtilities.IsWhitespaceOnlyJsxText(node) || width == 0);
    }

    // tokens.go:463
    public static int GetStartOfNode(Node node, SourceFile file, bool includeJSDoc)
    {
        return global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, file, includeJSDoc);
    }

    // tokens.go:469
    // Looks for rightmost valid token in the range [startPos, endPos).
    // If position is >= 0, looks for rightmost valid token that precedes or touches that position.
    private static Node? FindRightmostValidToken(int endPos, SourceFile sourceFile, Node containingNode, int position, bool excludeJSDoc)
    {
        if (position == -1)
        {
            position = containingNode.End;
        }
        Node? Find(Node? n, int endPos)
        {
            if (n == null)
            {
                return null;
            }
            if (AstUtilities.IsNonWhitespaceToken(n))
            {
                return n;
            }

            Node? rightmostValidNode = null;
            var rightmostVisitedNodes = new List<Node>(1); // Nodes after the last valid node.
            var hasChildren = false;
            bool ShouldVisitNode(Node node)
            {
                // Node is synthetic or out of the desired range: don't visit it.
                return !((node.Flags & NodeFlags.Reparsed) != 0 ||
                    node.End > endPos || GetStartOfNode(node, sourceFile, !excludeJSDoc /*includeJSDoc*/) >= position);
            }
            Node? VisitNode(Node? node, NodeVisitor _)
            {
                if (node == null || (node.Flags & NodeFlags.Reparsed) != 0)
                {
                    return node;
                }
                hasChildren = true;
                if (!ShouldVisitNode(node))
                {
                    return node;
                }
                rightmostVisitedNodes.Add(node);
                if (IsValidPrecedingNode(node, sourceFile))
                {
                    rightmostValidNode = node;
                    rightmostVisitedNodes.Clear();
                }
                return node;
            }
            NodeList? VisitNodes(NodeList? nodeList, NodeVisitor _)
            {
                if (nodeList != null && nodeList.Nodes.Length > 0)
                {
                    hasChildren = true;
                    var (index, _) = global::Ts.Core.Core.BinarySearchUniqueFunc(nodeList.Nodes, (middle, node) =>
                    {
                        if (node.End > endPos)
                        {
                            return ComparisonGreaterThan;
                        }
                        return ComparisonLessThan;
                    });
                    var validIndex = -1;
                    for (int i = index - 1; i >= 0; i--)
                    {
                        if (!ShouldVisitNode(nodeList.Nodes[i]))
                        {
                            continue;
                        }
                        if (IsValidPrecedingNode(nodeList.Nodes[i], sourceFile))
                        {
                            validIndex = i;
                            rightmostValidNode = nodeList.Nodes[i];
                            break;
                        }
                    }
                    for (int i = validIndex + 1; i < index; i++)
                    {
                        if (!ShouldVisitNode(nodeList.Nodes[i]))
                        {
                            continue;
                        }
                        rightmostVisitedNodes.Add(nodeList.Nodes[i]);
                    }
                }
                return nodeList;
            }
            VisitEachChildAndJSDoc(n, sourceFile, VisitNode, VisitNodes);

            // Three cases:
            // 1. The answer is a token of `rightmostValidNode`.
            // 2. The answer is one of the unvisited tokens that occur after the rightmost valid node.
            // 3. The current node is a childless, token-less node. The answer is the current node.

            // Case 2: Look at unvisited trailing tokens that occur in between the rightmost visited nodes.
            if (!ShouldSkipChild(n)) // JSDoc nodes don't include trivia tokens as children.
            {
                int startPos;
                if (rightmostValidNode != null)
                {
                    startPos = rightmostValidNode.End;
                }
                else
                {
                    startPos = n.Pos;
                }
                var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, startPos);
                List<Node>? tokens = null;
                foreach (var visitedNode in rightmostVisitedNodes)
                {
                    // Trailing tokens that occur before this node.
                    while (startPos < Math.Min(visitedNode.Pos, position))
                    {
                        var token = ScanNavigationToken(scanner, n);
                        var tokenStart = scanner.TokenStart();
                        if (tokenStart >= position)
                        {
                            break;
                        }
                        var tokenFullStart = scanner.TokenFullStart();
                        var tokenEnd = scanner.TokenEnd();
                        startPos = tokenEnd;
                        var flags = scanner.TokenFlags();
                        (tokens ??= []).Add(sourceFile.GetOrCreateToken(token, tokenFullStart, tokenEnd, n, flags));
                        scanner.Scan();
                    }
                    startPos = visitedNode.End;
                    scanner.ResetPos(startPos);
                    scanner.Scan();
                }
                // Trailing tokens after last visited node.
                while (startPos < Math.Min(endPos, position))
                {
                    var token = ScanNavigationToken(scanner, n);
                    var tokenStart = scanner.TokenStart();
                    if (tokenStart >= position)
                    {
                        break;
                    }
                    var tokenFullStart = scanner.TokenFullStart();
                    var tokenEnd = scanner.TokenEnd();
                    startPos = tokenEnd;
                    var flags = scanner.TokenFlags();
                    (tokens ??= []).Add(sourceFile.GetOrCreateToken(token, tokenFullStart, tokenEnd, n, flags));
                    scanner.Scan();
                }

                var lastToken = (tokens?.Count ?? 0) - 1;
                // Find preceding valid token.
                for (int i = lastToken; i >= 0; i--)
                {
                    if (!AstUtilities.IsWhitespaceOnlyJsxText(tokens![i]))
                    {
                        return tokens[i];
                    }
                }
            }

            // Case 3: childless node.
            if (!hasChildren)
            {
                if (!ReferenceEquals(n, containingNode))
                {
                    return n;
                }
                return null;
            }
            // Case 1: recur on rightmostValidNode.
            if (rightmostValidNode != null)
            {
                endPos = rightmostValidNode.End;
            }
            return Find(rightmostValidNode, endPos);
        }

        return Find(containingNode, endPos);
    }

    // tokens.go:611
    public static Node? FindNextToken(Node previousToken, Node parent, SourceFile file)
    {
        Node? Find(Node n)
        {
            if (NodeTests.IsTokenKind(n.Kind) && n.Pos == previousToken.End)
            {
                // this is token that starts at the end of previous token - return it
                return n;
            }
            // Node that contains `previousToken` or occurs immediately after it.
            Node? foundNode = null;
            Node? VisitNode(Node? node, NodeVisitor _)
            {
                if (node != null && (node.Flags & NodeFlags.Reparsed) == 0 &&
                    node.Pos <= previousToken.End && node.End > previousToken.End)
                {
                    foundNode = node;
                }
                return node;
            }
            NodeList? VisitNodes(NodeList? nodeList, NodeVisitor _)
            {
                if (nodeList != null && nodeList.Nodes.Length > 0 && foundNode == null)
                {
                    var nodes = nodeList.Nodes;
                    var (index, match) = global::Ts.Core.Core.BinarySearchUniqueFunc(nodes, (_, node) =>
                    {
                        if ((node.Flags & NodeFlags.Reparsed) != 0)
                        {
                            return ComparisonLessThan;
                        }
                        if (node.Pos > previousToken.End)
                        {
                            return ComparisonGreaterThan;
                        }
                        if (node.End <= previousToken.Pos)
                        {
                            return ComparisonLessThan;
                        }
                        return ComparisonEqualTo;
                    });
                    if (match)
                    {
                        foundNode = nodes[index];
                    }
                }
                return nodeList;
            }
            VisitEachChildAndJSDoc(n, file, VisitNode, VisitNodes);
            // Cases:
            // 1. no answer exists
            // 2. answer is an unvisited token
            // 3. answer is in the visited found node

            // Case 3: look for the next token inside the found node.
            if (foundNode != null)
            {
                return Find(foundNode);
            }
            var startPos = previousToken.End;
            // Case 2: look for the next token directly.
            if (startPos >= n.Pos && startPos < n.End)
            {
                var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(file, startPos);
                var token = scanner.Token();
                var tokenFullStart = scanner.TokenFullStart();
                var tokenEnd = scanner.TokenEnd();
                var flags = scanner.TokenFlags();
                // Use tokenFullStart (which includes leading trivia) to match TS's
                // findNextToken behavior where `n.pos === previousToken.end` is checked
                // (TS's pos includes trivia, same as Go's Pos()/tokenFullStart).
                if (tokenFullStart == previousToken.End)
                {
                    return file.GetOrCreateToken(token, tokenFullStart, tokenEnd, n, flags);
                }
                throw new InvalidOperationException($"Expected to find next token at {previousToken.End}, got token {token.ToString()} at {tokenFullStart}");
            }
            // Case 3: no answer.
            return null;
        }
        return Find(parent);
    }

    // tokens.go:680
    private static NodeVisitor GetNodeVisitor(
        Func<Node?, NodeVisitor, Node?>? visitNode,
        Func<NodeList?, NodeVisitor, NodeList?>? visitNodes)
    {
        Func<Node?, NodeVisitor, Node?>? wrappedVisitNode = null;
        Func<NodeList?, NodeVisitor, NodeList?>? wrappedVisitNodes = null;
        if (visitNode != null)
        {
            wrappedVisitNode = (n, v) =>
            {
                if (AstUtilities.IsJSDocSingleCommentNodeComment(n))
                {
                    return n;
                }
                return visitNode(n, v);
            };
        }

        if (visitNodes != null)
        {
            wrappedVisitNodes = (n, v) =>
            {
                if (AstUtilities.IsJSDocSingleCommentNodeList(n))
                {
                    return n;
                }
                return visitNodes(n, v);
            };
        }

        return new NodeVisitor(global::Ts.Core.CoreUtil.Identity, null, new NodeVisitorHooks
        {
            VisitNode = wrappedVisitNode,
            VisitToken = wrappedVisitNode,
            VisitNodes = wrappedVisitNodes,
            VisitModifiers = (modifiers, visitor) =>
            {
                if (modifiers != null)
                {
                    wrappedVisitNodes!(modifiers, visitor);
                }
                return modifiers;
            },
        });
    }

    // tokens.go:717
    private static bool ShouldSkipChild(Node node)
    {
        return node.Kind == SyntaxKind.JSDoc ||
            node.Kind == SyntaxKind.JSDocText ||
            node.Kind == SyntaxKind.JSDocTypeLiteral ||
            node.Kind == SyntaxKind.JSDocSignature ||
            AstUtilities.IsJSDocLinkLike(node) ||
            AstUtilities.IsJSDocTag(node);
    }

    // tokens.go:728
    // FindChildOfKind searches for a child node or token of the specified kind within a containing node.
    // This function scans through both AST nodes and intervening tokens to find the first match.
    public static Node? FindChildOfKind(Node containingNode, SyntaxKind kind, SourceFile sourceFile)
    {
        var lastNodePos = containingNode.Pos;
        var scan = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, lastNodePos);

        Node? foundChild = null;
        bool VisitNode(Node node)
        {
            if (node == null || (node.Flags & NodeFlags.Reparsed) != 0)
            {
                return false;
            }
            // Look for child in preceding tokens.
            var startPos = lastNodePos;
            while (startPos < node.Pos)
            {
                var tokenKind = scan.Token();
                var tokenEnd = scan.TokenEnd();
                if (tokenKind == kind)
                {
                    var tokenFullStart = scan.TokenFullStart();
                    var flags = scan.TokenFlags();
                    foundChild = sourceFile.GetOrCreateToken(tokenKind, tokenFullStart, tokenEnd, containingNode, flags);
                    return true;
                }
                startPos = tokenEnd;
                scan.Scan();
            }

            if (node.Kind == kind)
            {
                foundChild = node;
                return true;
            }

            lastNodePos = node.End;
            scan.ResetPos(lastNodePos);
            return false;
        }

        AstUtilities.ForEachChildAndJSDoc(containingNode, sourceFile, VisitNode);

        if (foundChild != null)
        {
            return foundChild;
        }

        // Look for child in trailing tokens.
        {
            var startPos = lastNodePos;
            while (startPos < containingNode.End)
            {
                var tokenKind = scan.Token();
                var tokenEnd = scan.TokenEnd();
                if (tokenKind == kind)
                {
                    var tokenFullStart = scan.TokenFullStart();
                    var flags = scan.TokenFlags();
                    var token = sourceFile.GetOrCreateToken(tokenKind, tokenFullStart, tokenEnd, containingNode, flags);
                    return token;
                }
                startPos = tokenEnd;
                scan.Scan();
            }
        }
        return null;
    }
}
