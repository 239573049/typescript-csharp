// Ported from tsgo internal/ls/documenthighlights.go.
//
// PORT-NOTE (single-file subset): the Go entry point takes an LSP line/character
// position and an optional filesToSearch list (ProvideMultiDocumentHighlights, the
// custom multi-file request). The C# LS foundation's position currency is the raw
// UTF-16 offset (see LanguageService.GetNodeAtPosition), and the multi-file search
// resolution (documenthighlights.go:73-87) collapses to the current file — matching
// what Go's ProvideDocumentHighlights extracts from the worker result. The semantic
// branch rides the FindAllReferences foundation subset (emit-resolver identity
// comparison) instead of getReferencedSymbolsForNode, whose per-symbol search state
// is deferred with checker.GetSymbolAtLocation; the entry-kind plumbing
// (resolveEntry/getRangeOfEntry, entryKindRange) collapses to node entries.
// Positions are UTF-16 code units throughout.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using DocumentHighlight = Ts.LsProto.DocumentHighlight;
using DocumentHighlightKind = Ts.LsProto.DocumentHighlightKind;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // documenthighlights.go:18 — ProvideDocumentHighlights (worker fused per the header note).
    public DocumentHighlight[] ProvideDocumentHighlights(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var node = GetNodeAtPosition(fileName, position, out var sourceFile);

        // Cheap JSX check before resolving files to search.
        if (node.Parent != null &&
            (node.Parent.Kind == SyntaxKind.JsxClosingElement ||
                (node.Parent.Kind == SyntaxKind.JsxOpeningElement && node.Parent.TagName() == node)))
        {
            Node? openingElement = null;
            Node? closingElement = null;
            if (NodeTests.IsJsxElement(node.Parent.Parent!)) // Go unconditional dereference.
            {
                openingElement = node.Parent.Parent!.AsJsxElement().OpeningElement;
                closingElement = node.Parent.Parent!.AsJsxElement().ClosingElement;
            }
            var jsxHighlights = new List<DocumentHighlight>();
            const DocumentHighlightKind jsxKind = DocumentHighlightKind.Read;
            if (openingElement != null)
            {
                jsxHighlights.Add(new DocumentHighlight
                {
                    Range = CreateLspRangeFromNode(openingElement, sourceFile),
                    Kind = jsxKind,
                });
            }
            if (closingElement != null)
            {
                jsxHighlights.Add(new DocumentHighlight
                {
                    Range = CreateLspRangeFromNode(closingElement, sourceFile),
                    Kind = jsxKind,
                });
            }
            return jsxHighlights.ToArray();
        }

        var highlights = GetSemanticDocumentHighlights(node, sourceFile, cancellationToken);
        if (highlights == null || highlights.Count == 0)
        {
            // Fall back to syntactic highlights for the current file only.
            highlights = GetSyntacticDocumentHighlights(node, sourceFile);
        }
        return highlights == null ? [] : highlights.ToArray();
    }

    // documenthighlights.go:102 — getSemanticDocumentHighlights, on the
    // FindAllReferences foundation subset (see file header): identifier use-sites in
    // the current file whose emit-resolver declaration matches the queried node's.
    // documenthighlights.go:130 — toDocumentHighlight's write-access classification
    // is inlined at the single node-entry construction site.
    private List<DocumentHighlight>? GetSemanticDocumentHighlights(
        Node node,
        SourceFile sourceFile,
        CancellationToken cancellationToken)
    {
        if (node.Kind != SyntaxKind.Identifier && node.Kind != SyntaxKind.PrivateIdentifier)
        {
            // Go dispatches the special reference families (labels, this/super,
            // keywords, string literals) through getReferencedSymbolsForNode.
            // Deferred with the FindAllReferences deep path.
            return null;
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, sourceFile);
        try
        {
            var emitResolver = checker.GetEmitResolver();
            var targetDeclaration = GetReferencedDeclaration(emitResolver, node);
            if (targetDeclaration is null)
            {
                return null;
            }

            var searchName = node.Text();
            var highlights = new List<DocumentHighlight>();
            foreach (var reference in GetPossibleSymbolReferenceNodes(sourceFile, searchName, null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsValidReferencePosition(reference, searchName))
                {
                    continue;
                }
                if (!ReferenceEquals(GetReferencedDeclaration(emitResolver, reference), targetDeclaration))
                {
                    continue;
                }
                // Determine write access for node references.
                var kind = LsDeps.IsWriteAccessForReference(reference)
                    ? DocumentHighlightKind.Write
                    : DocumentHighlightKind.Read;
                var span = GetRangeOfNode(reference, sourceFile, null);
                highlights.Add(new DocumentHighlight
                {
                    Range = CreateLspRangeFromBounds(span.Pos, span.End, sourceFile),
                    Kind = kind,
                });
            }
            return highlights;
        }
        finally
        {
            done();
        }
    }

    // documenthighlights.go:154 — getSyntacticDocumentHighlights.
    private List<DocumentHighlight>? GetSyntacticDocumentHighlights(Node node, SourceFile sourceFile)
    {
        switch (node.Kind)
        {
            case SyntaxKind.IfKeyword:
            case SyntaxKind.ElseKeyword:
                if (NodeTests.IsIfStatement(node.Parent!))
                {
                    return GetIfElseOccurrences(node.Parent!.AsIfStatement(), sourceFile);
                }
                return null;
            case SyntaxKind.ReturnKeyword:
                return UseParent(node.Parent!, NodeTests.IsReturnStatement, GetReturnOccurrences, sourceFile);
            case SyntaxKind.ThrowKeyword:
                return UseParent(node.Parent!, NodeTests.IsThrowStatement, GetThrowOccurrences, sourceFile);
            case SyntaxKind.TryKeyword:
            case SyntaxKind.CatchKeyword:
            case SyntaxKind.FinallyKeyword:
                Node tryStatement;
                if (node.Kind == SyntaxKind.CatchKeyword)
                {
                    tryStatement = node.Parent!.Parent!;
                }
                else
                {
                    tryStatement = node.Parent!;
                }
                return UseParent(tryStatement, NodeTests.IsTryStatement, GetTryCatchFinallyOccurrences, sourceFile);
            case SyntaxKind.SwitchKeyword:
                return UseParent(node.Parent!, NodeTests.IsSwitchStatement, GetSwitchCaseDefaultOccurrences, sourceFile);
            case SyntaxKind.CaseKeyword:
            case SyntaxKind.DefaultKeyword:
                if (NodeTests.IsDefaultClause(node.Parent!) || NodeTests.IsCaseClause(node.Parent!))
                {
                    return UseParent(node.Parent!.Parent!.Parent!, NodeTests.IsSwitchStatement, GetSwitchCaseDefaultOccurrences, sourceFile);
                }
                return null;
            case SyntaxKind.BreakKeyword:
            case SyntaxKind.ContinueKeyword:
                return UseParent(node.Parent!, LsDeps.IsBreakOrContinueStatement, GetBreakOrContinueStatementOccurrences, sourceFile);
            case SyntaxKind.ForKeyword:
            case SyntaxKind.WhileKeyword:
            case SyntaxKind.DoKeyword:
                return UseParent(node.Parent!, n => AstUtilities.IsIterationStatement(n, true), GetLoopBreakContinueOccurrences, sourceFile);
            case SyntaxKind.ConstructorKeyword:
                return GetFromAllDeclarations(NodeTests.IsConstructorDeclaration, [SyntaxKind.ConstructorKeyword], node, sourceFile);
            case SyntaxKind.GetKeyword:
            case SyntaxKind.SetKeyword:
                return GetFromAllDeclarations(AstUtilities.IsAccessor, [SyntaxKind.GetKeyword, SyntaxKind.SetKeyword], node, sourceFile);
            case SyntaxKind.AwaitKeyword:
                return UseParent(node.Parent!, NodeTests.IsAwaitExpression, GetAsyncAndAwaitOccurrences, sourceFile);
            case SyntaxKind.AsyncKeyword:
                return HighlightSpans(GetAsyncAndAwaitOccurrences(node, sourceFile), sourceFile);
            case SyntaxKind.YieldKeyword:
                return HighlightSpans(GetYieldOccurrences(node, sourceFile), sourceFile);
            case SyntaxKind.InKeyword:
            case SyntaxKind.OutKeyword:
                return null;
            default:
                if (NodeTests.IsModifierKind(node.Kind) && (AstUtilities.IsDeclaration(node.Parent!) || NodeTests.IsVariableStatement(node.Parent!)))
                {
                    return HighlightSpans(GetModifierOccurrences(node.Kind, node.Parent!, sourceFile), sourceFile);
                }
                return null;
        }
    }

    // documenthighlights.go:206 — useParent.
    private List<DocumentHighlight>? UseParent(
        Node node,
        Func<Node, bool> nodeTest,
        Func<Node, SourceFile, List<Node>?> getNodes,
        SourceFile sourceFile)
    {
        if (nodeTest(node))
        {
            return HighlightSpans(getNodes(node, sourceFile), sourceFile);
        }
        return null;
    }

    // documenthighlights.go:213 — highlightSpans.
    private List<DocumentHighlight>? HighlightSpans(List<Node>? nodes, SourceFile sourceFile)
    {
        if (nodes == null || nodes.Count == 0)
        {
            return null;
        }
        var highlights = new List<DocumentHighlight>();
        const DocumentHighlightKind kind = DocumentHighlightKind.Read;
        foreach (var node in nodes)
        {
            if (node != null)
            {
                highlights.Add(new DocumentHighlight
                {
                    Range = CreateLspRangeFromNode(node, sourceFile),
                    Kind = kind,
                });
            }
        }
        return highlights;
    }

    // documenthighlights.go:230 — getFromAllDeclarations.
    private List<DocumentHighlight>? GetFromAllDeclarations(
        Func<Node, bool> nodeTest,
        SyntaxKind[] keywords,
        Node node,
        SourceFile sourceFile)
    {
        return UseParent(node.Parent!, nodeTest, (decl, sf) =>
        {
            var symbolDecls = new List<Node>();
            if (AstUtilities.CanHaveSymbol(decl))
            {
                var symbol = decl.Symbol();
                if (symbol != null)
                {
                    foreach (var d in symbol.Declarations ?? [])
                    {
                        if (nodeTest(d))
                        {
                            foreach (var c in LsDeps.GetChildrenFromNonJSDocNode(d, sf))
                            {
                                var matched = false;
                                foreach (var k in keywords)
                                {
                                    if (c.Kind == k)
                                    {
                                        symbolDecls.Add(c);
                                        matched = true;
                                        break;
                                    }
                                }
                                if (matched)
                                {
                                    break; // Go `break outer`
                                }
                            }
                        }
                    }
                }
            }
            return symbolDecls;
        }, sourceFile);
    }

    // documenthighlights.go:254 — getIfElseOccurrences.
    private List<DocumentHighlight> GetIfElseOccurrences(IfStatement ifStatement, SourceFile sourceFile)
    {
        var keywords = GetIfElseKeywords(ifStatement, sourceFile);
        const DocumentHighlightKind kind = DocumentHighlightKind.Read;
        var highlights = new List<DocumentHighlight>();

        // We'd like to highlight else/ifs together if they are only separated by whitespace
        // (i.e. the keywords are separated by no comments, no newlines).
        for (var i = 0; i < keywords.Count; i++)
        {
            if (keywords[i].Kind == SyntaxKind.ElseKeyword && i < keywords.Count - 1)
            {
                var elseKeyword = keywords[i];
                var ifKeyword = keywords[i + 1]; // this *should* always be an 'if' keyword.
                var shouldCombine = true;

                // Avoid recalculating getStart() by iterating backwards.
                var ifTokenStart = global::Ts.Scanner.Scanner.GetTokenPosOfNode(ifKeyword, sourceFile, false);
                if (ifTokenStart < 0)
                {
                    ifTokenStart = ifKeyword.Pos;
                }
                for (var j = ifTokenStart - 1; j >= elseKeyword.End; j--)
                {
                    if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(sourceFile.Text()[j]))
                    {
                        shouldCombine = false;
                        break;
                    }
                }
                if (shouldCombine)
                {
                    highlights.Add(new DocumentHighlight
                    {
                        Range = CreateLspRangeFromBounds(
                            global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), elseKeyword.Pos),
                            ifKeyword.End,
                            sourceFile),
                        Kind = kind,
                    });
                    i++; // skip the next keyword
                    continue;
                }
            }
            // Ordinary case: just highlight the keyword.
            highlights.Add(new DocumentHighlight
            {
                Range = CreateLspRangeFromNode(keywords[i], sourceFile),
                Kind = kind,
            });
        }
        return highlights;
    }

    // documenthighlights.go:296 — getIfElseKeywords.
    private static List<Node> GetIfElseKeywords(IfStatement ifStatement, SourceFile sourceFile)
    {
        // We may be at an if statement like those in the range below:
        //
        //   ```
        //   if (...) {
        //   } else [|if (...) {}|]
        //   ````
        //
        // Traverse upwards through all parent if-statements linked by their else-branches.
        while (NodeTests.IsIfStatement(ifStatement.Parent!))
        {
            // See if the parent's `else` is actually the current `if` statement.
            var parentingIf = ifStatement.Parent!.AsIfStatement();
            var parentElseStatement = parentingIf.ElseStatement;
            if (parentElseStatement != ifStatement.AsNode())
            {
                break;
            }
            ifStatement = parentingIf;
        }

        var keywords = new List<Node>();

        // Traverse back down through the else branches, aggregating if/else keywords of if-statements.
        while (true)
        {
            var children = LsDeps.GetChildrenFromNonJSDocNode(ifStatement.AsNode(), sourceFile);
            if (children.Count > 0 && children[0].Kind == SyntaxKind.IfKeyword)
            {
                keywords.Add(children[0]);
            }
            // Generally the 'else' keyword is second-to-last, so traverse backwards.
            for (var i = children.Count - 1; i >= 0; i--)
            {
                if (children[i].Kind == SyntaxKind.ElseKeyword)
                {
                    keywords.Add(children[i]);
                    break;
                }
            }
            var elseStatement = ifStatement.ElseStatement;
            if (elseStatement == null || !NodeTests.IsIfStatement(elseStatement))
            {
                break;
            }
            ifStatement = elseStatement.AsIfStatement();
        }
        return keywords;
    }

    // documenthighlights.go:339 — getReturnOccurrences.
    private static List<Node>? GetReturnOccurrences(Node node, SourceFile sourceFile)
    {
        var funcNode = AstUtilities.FindAncestor(node.Parent, AstUtilities.IsFunctionLike);
        if (funcNode == null)
        {
            return null;
        }

        var keywords = new List<Node>();
        var body = funcNode.Body();
        if (body != null)
        {
            AstUtilities.ForEachReturnStatement(body, ret =>
            {
                var keyword = global::Ts.AstNav.Tokens.FindChildOfKind(ret, SyntaxKind.ReturnKeyword, sourceFile);
                if (keyword != null)
                {
                    keywords.Add(keyword);
                }
                return false; // continue traversal
            });

            // Get all throw statements not in a try block
            var throwStatements = AggregateOwnedThrowStatements(body, sourceFile);
            foreach (var throwStatement in throwStatements)
            {
                var keyword = global::Ts.AstNav.Tokens.FindChildOfKind(throwStatement, SyntaxKind.ThrowKeyword, sourceFile);
                if (keyword != null)
                {
                    keywords.Add(keyword);
                }
            }
        }
        return keywords;
    }

    // documenthighlights.go:368 — aggregateOwnedThrowStatements.
    private static List<Node> AggregateOwnedThrowStatements(Node node, SourceFile sourceFile)
    {
        if (NodeTests.IsThrowStatement(node))
        {
            return [node];
        }
        if (NodeTests.IsTryStatement(node))
        {
            // Exceptions thrown within a try block lacking a catch clause are "owned" in the current context.
            var statement = node.AsTryStatement();
            var tryBlock = statement.TryBlock;
            var catchClause = statement.CatchClause;
            var finallyBlock = statement.FinallyBlock;

            var result = new List<Node>();
            if (catchClause != null)
            {
                result = AggregateOwnedThrowStatements(catchClause, sourceFile);
            }
            else if (tryBlock != null)
            {
                result = AggregateOwnedThrowStatements(tryBlock, sourceFile);
            }
            if (finallyBlock != null)
            {
                result.AddRange(AggregateOwnedThrowStatements(finallyBlock, sourceFile));
            }
            return result;
        }
        // Do not cross function boundaries.
        if (AstUtilities.IsFunctionLike(node))
        {
            return [];
        }
        return FlatMapChildren(node, sourceFile, AggregateOwnedThrowStatements);
    }

    // documenthighlights.go:397 — flatMapChildren.
    private static List<T> FlatMapChildren<T>(Node node, SourceFile sourceFile, Func<Node, SourceFile, List<T>?> cb)
    {
        var result = new List<T>();

        node.ForEachChild(child =>
        {
            var value = cb(child, sourceFile);
            if (value != null)
            {
                result.AddRange(value);
            }
            return false; // continue traversal
        });
        return result;
    }

    // documenthighlights.go:410 — getThrowOccurrences.
    private static List<Node>? GetThrowOccurrences(Node node, SourceFile sourceFile)
    {
        var owner = GetThrowStatementOwner(node);
        if (owner == null)
        {
            return null;
        }

        var keywords = new List<Node>();

        // Aggregate all throw statements "owned" by this owner.
        var throwStatements = AggregateOwnedThrowStatements(owner, sourceFile);
        foreach (var throwStatement in throwStatements)
        {
            var keyword = global::Ts.AstNav.Tokens.FindChildOfKind(throwStatement, SyntaxKind.ThrowKeyword, sourceFile);
            if (keyword != null)
            {
                keywords.Add(keyword);
            }
        }

        // If the "owner" is a function, then we equate 'return' and 'throw' statements in their
        // ability to "jump out" of the function, and include occurrences for both
        if (AstUtilities.IsFunctionBlock(owner))
        {
            AstUtilities.ForEachReturnStatement(owner, ret =>
            {
                var keyword = global::Ts.AstNav.Tokens.FindChildOfKind(ret, SyntaxKind.ReturnKeyword, sourceFile);
                if (keyword != null)
                {
                    keywords.Add(keyword);
                }
                return false; // continue traversal
            });
        }

        return keywords;
    }

    // documenthighlights.go:445 — getThrowStatementOwner.
    // For lack of a better name, this function takes a throw statement and returns the
    // nearest ancestor that is a try-block (whose try statement has a catch clause),
    // function-block, or source file.
    private static Node? GetThrowStatementOwner(Node throwStatement)
    {
        var child = throwStatement;
        while (child.Parent != null)
        {
            var parent = child.Parent;

            if (AstUtilities.IsFunctionBlock(parent) || parent.Kind == SyntaxKind.SourceFile)
            {
                return parent;
            }

            // A throw-statement is only owned by a try-statement if the try-statement has
            // a catch clause, and if the throw-statement occurs within the try block.
            if (NodeTests.IsTryStatement(parent))
            {
                var tryStatement = parent.AsTryStatement();
                if (tryStatement.TryBlock == child && tryStatement.CatchClause != null)
                {
                    return child;
                }
            }

            child = parent;
        }
        return null;
    }

    // documenthighlights.go:468 — getTryCatchFinallyOccurrences.
    private static List<Node>? GetTryCatchFinallyOccurrences(Node node, SourceFile sourceFile)
    {
        var tryStatement = node.AsTryStatement();

        var keywords = new List<Node>();
        var token = LsDeps.GetFirstToken(node, sourceFile);
        if (token != null && token.Kind == SyntaxKind.TryKeyword)
        {
            keywords.Add(token);
        }

        if (tryStatement.CatchClause != null)
        {
            var catchToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.CatchKeyword, sourceFile);
            if (catchToken != null)
            {
                keywords.Add(catchToken);
            }
        }

        if (tryStatement.FinallyBlock != null)
        {
            var finallyKeyword = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.FinallyKeyword, sourceFile);
            if (finallyKeyword != null)
            {
                keywords.Add(finallyKeyword);
            }
        }

        return keywords;
    }

    // documenthighlights.go:492 — getSwitchCaseDefaultOccurrences.
    private static List<Node>? GetSwitchCaseDefaultOccurrences(Node node, SourceFile sourceFile)
    {
        var switchStatement = node.AsSwitchStatement();

        var keywords = new List<Node>();
        var token = LsDeps.GetFirstToken(node, sourceFile);
        if (token!.Kind == SyntaxKind.SwitchKeyword) // Go unconditional dereference.
        {
            keywords.Add(token);
        }

        var clauses = switchStatement.CaseBlock!.AsCaseBlock().Clauses!;
        foreach (var clause in clauses.Nodes)
        {
            var clauseToken = LsDeps.GetFirstToken(clause.AsNode(), sourceFile);
            if (clauseToken!.Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword) // Go unconditional dereference.
            {
                keywords.Add(clauseToken);
            }

            var breakAndContinueStatements = AggregateAllBreakAndContinueStatements(clause, sourceFile);
            foreach (var statement in breakAndContinueStatements)
            {
                if (statement.Kind == SyntaxKind.BreakStatement && OwnsBreakOrContinueStatement(switchStatement.AsNode(), statement))
                {
                    keywords.Add(LsDeps.GetFirstToken(statement, sourceFile)!); // Go appends unconditionally.
                }
            }
        }

        return keywords;
    }

    // documenthighlights.go:519 — aggregateAllBreakAndContinueStatements.
    private static List<Node> AggregateAllBreakAndContinueStatements(Node node, SourceFile sourceFile)
    {
        if (LsDeps.IsBreakOrContinueStatement(node))
        {
            return [node];
        }
        if (AstUtilities.IsFunctionLike(node))
        {
            return [];
        }
        return FlatMapChildren(node, sourceFile, AggregateAllBreakAndContinueStatements);
    }

    // documenthighlights.go:529 — ownsBreakOrContinueStatement.
    private static bool OwnsBreakOrContinueStatement(Node owner, Node statement)
    {
        var actualOwner = GetBreakOrContinueOwner(statement);
        if (actualOwner == null)
        {
            return false;
        }
        return actualOwner == owner;
    }

    // documenthighlights.go:537 — getBreakOrContinueOwner.
    private static Node? GetBreakOrContinueOwner(Node statement)
    {
        return AstUtilities.FindAncestorOrQuit(statement, node =>
        {
            switch (node.Kind)
            {
                case SyntaxKind.SwitchStatement:
                    if (statement.Kind == SyntaxKind.ContinueStatement)
                    {
                        return FindAncestorResult.False;
                    }
                    goto case SyntaxKind.ForStatement; // Go fallthrough
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForInStatement:
                case SyntaxKind.ForOfStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.DoStatement:
                    // If the statement is labeled, check if the node is labeled by the statement's label.
                    if (statement.Label() == null || IsLabeledBy(node, statement.Label()!.Text()))
                    {
                        return FindAncestorResult.True;
                    }
                    return FindAncestorResult.False;
                default:
                    // Don't cross function boundaries.
                    if (AstUtilities.IsFunctionLike(node))
                    {
                        return FindAncestorResult.Quit;
                    }
                    return FindAncestorResult.False;
            }
        });
    }

    // documenthighlights.go:567 — isLabeledBy.
    // Whether or not a 'node' is preceded by a label of the given string.
    // Note: 'node' cannot be a SourceFile.
    private static bool IsLabeledBy(Node node, string labelName)
    {
        return AstUtilities.FindAncestorOrQuit(node.Parent, owner =>
        {
            if (!NodeTests.IsLabeledStatement(owner))
            {
                return FindAncestorResult.Quit;
            }
            if (owner.Label()!.Text() == labelName)
            {
                return FindAncestorResult.True;
            }
            return FindAncestorResult.False;
        }) != null;
    }

    // documenthighlights.go:579 — getBreakOrContinueStatementOccurrences.
    private static List<Node>? GetBreakOrContinueStatementOccurrences(Node node, SourceFile sourceFile)
    {
        var owner = GetBreakOrContinueOwner(node);
        if (owner != null)
        {
            switch (owner.Kind)
            {
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForInStatement:
                case SyntaxKind.ForOfStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.WhileStatement:
                    return GetLoopBreakContinueOccurrences(owner, sourceFile);
                case SyntaxKind.SwitchStatement:
                    return GetSwitchCaseDefaultOccurrences(owner, sourceFile);
            }
        }
        return null;
    }

    // documenthighlights.go:591 — getLoopBreakContinueOccurrences.
    private static List<Node>? GetLoopBreakContinueOccurrences(Node node, SourceFile sourceFile)
    {
        var keywords = new List<Node>();

        var token = LsDeps.GetFirstToken(node, sourceFile);
        if (token!.Kind is SyntaxKind.ForKeyword or SyntaxKind.DoKeyword or SyntaxKind.WhileKeyword) // Go unconditional dereference.
        {
            keywords.Add(token);
            if (node.Kind == SyntaxKind.DoStatement)
            {
                var loopTokens = LsDeps.GetChildrenFromNonJSDocNode(node, sourceFile);
                for (var i = loopTokens.Count - 1; i >= 0; i--)
                {
                    if (loopTokens[i].Kind == SyntaxKind.WhileKeyword)
                    {
                        keywords.Add(loopTokens[i]);
                        break;
                    }
                }
            }
        }

        var breakAndContinueStatements = AggregateAllBreakAndContinueStatements(node, sourceFile);
        foreach (var statement in breakAndContinueStatements)
        {
            var statementToken = LsDeps.GetFirstToken(statement, sourceFile);
            if (OwnsBreakOrContinueStatement(node, statement) &&
                (statementToken!.Kind is SyntaxKind.BreakKeyword or SyntaxKind.ContinueKeyword)) // Go unconditional dereference.
            {
                keywords.Add(statementToken);
            }
        }

        return keywords;
    }

    // documenthighlights.go:619 — getAsyncAndAwaitOccurrences.
    private static List<Node>? GetAsyncAndAwaitOccurrences(Node node, SourceFile sourceFile)
    {
        var fun = LsDeps.GetContainingFunction(node);
        if (fun == null)
        {
            return null;
        }

        var keywords = new List<Node>();

        foreach (var modifier in fun.ModifierNodes() ?? [])
        {
            if (modifier.Kind == SyntaxKind.AsyncKeyword)
            {
                keywords.Add(modifier);
            }
        }

        fun.ForEachChild(child =>
        {
            TraverseWithoutCrossingFunction(child, sourceFile, descendant =>
            {
                if (NodeTests.IsAwaitExpression(descendant))
                {
                    var token = LsDeps.GetFirstToken(descendant, sourceFile);
                    if (token!.Kind == SyntaxKind.AwaitKeyword) // Go unconditional dereference.
                    {
                        keywords.Add(token);
                    }
                }
            });
            return false; // continue traversal
        });

        return keywords;
    }

    // documenthighlights.go:648 — getYieldOccurrences.
    private static List<Node>? GetYieldOccurrences(Node node, SourceFile sourceFile)
    {
        var parentFunc = AstUtilities.FindAncestor(node.Parent, AstUtilities.IsFunctionLike);
        if (parentFunc == null)
        {
            return null;
        }

        var keywords = new List<Node>();

        parentFunc.ForEachChild(child =>
        {
            TraverseWithoutCrossingFunction(child, sourceFile, descendant =>
            {
                if (NodeTests.IsYieldExpression(descendant))
                {
                    var token = LsDeps.GetFirstToken(descendant, sourceFile);
                    if (token!.Kind == SyntaxKind.YieldKeyword) // Go unconditional dereference.
                    {
                        keywords.Add(token);
                    }
                }
            });
            return false; // continue traversal
        });

        return keywords;
    }

    // documenthighlights.go:671 — traverseWithoutCrossingFunction.
    private static void TraverseWithoutCrossingFunction(Node node, SourceFile sourceFile, Action<Node> cb)
    {
        cb(node);
        if (!AstUtilities.IsFunctionLike(node) &&
            !AstUtilities.IsClassLike(node) &&
            !NodeTests.IsInterfaceDeclaration(node) &&
            !NodeTests.IsModuleDeclaration(node) &&
            !NodeTests.IsTypeAliasDeclaration(node) &&
            !AstUtilities.IsTypeNode(node))
        {
            node.ForEachChild(child =>
            {
                TraverseWithoutCrossingFunction(child, sourceFile, cb);
                return false; // continue traversal
            });
        }
    }

    // documenthighlights.go:681 — getModifierOccurrences.
    private static List<Node>? GetModifierOccurrences(SyntaxKind kind, Node node, SourceFile sourceFile)
    {
        var result = new List<Node>();

        var nodesToSearch = GetNodesToSearchForModifier(node, AstUtilities.ModifierToFlag(kind));
        foreach (var n in nodesToSearch ?? [])
        {
            var modifier = FindModifier(n, kind);
            if (modifier != null)
            {
                result.Add(modifier);
            }
        }
        return result;
    }

    // documenthighlights.go:694 — getNodesToSearchForModifier.
    private static List<Node>? GetNodesToSearchForModifier(Node declaration, ModifierFlags modifierFlag)
    {
        var result = new List<Node>();

        var container = declaration.Parent;
        if (container == null)
        {
            return null;
        }

        // Types of node whose children might have modifiers.
        switch (container.Kind)
        {
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.SourceFile:
            case SyntaxKind.Block:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                // Container is either a class declaration or the declaration is a classDeclaration
                if ((modifierFlag & ModifierFlags.Abstract) != 0 && NodeTests.IsClassDeclaration(declaration))
                {
                    result.AddRange(declaration.Members() ?? []);
                    result.Add(declaration);
                    return result;
                }
                else
                {
                    result.AddRange(container.Statements() ?? []);
                    return result;
                }
            case SyntaxKind.Constructor:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.FunctionDeclaration:
                // Parameters and, if inside a class, also class members
                result.AddRange(container.Parameters());
                if (AstUtilities.IsClassLike(container.Parent!))
                {
                    result.AddRange(container.Parent!.Members() ?? []);
                }
                return result;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeLiteral:
                var nodes = container.Members() ?? [];
                result.AddRange(nodes);
                // If we're an accessibility modifier, we're in an instance member and should search
                // the constructor's parameter list for instance members as well.
                if ((modifierFlag & (ModifierFlags.AccessibilityModifier | ModifierFlags.Readonly)) != 0)
                {
                    Node? constructor = null;

                    foreach (var member in nodes)
                    {
                        if (NodeTests.IsConstructorDeclaration(member))
                        {
                            constructor = member;
                            break;
                        }
                    }
                    if (constructor != null)
                    {
                        result.AddRange(constructor.Parameters());
                    }
                }
                else if ((modifierFlag & ModifierFlags.Abstract) != 0)
                {
                    result.Add(container);
                }
                return result;
            default:
                // Syntactically invalid positions or unsupported containers
                return null;
        }
    }

    // documenthighlights.go:745 — findModifier.
    private static Node? FindModifier(Node node, SyntaxKind kind)
    {
        foreach (var modifier in node.ModifierNodes() ?? [])
        {
            if (modifier.Kind == kind)
            {
                return modifier;
            }
        }
        return null;
    }
}
