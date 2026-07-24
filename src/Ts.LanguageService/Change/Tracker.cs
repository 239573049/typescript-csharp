// Ported from tsgo internal/ls/change/tracker.go (whole file): the text-change tracker that
// accumulates node-level edits and renders them into LSP text edits.
// PORT-NOTE: Go carries format settings in ctx (format.WithFormatCodeSettings); the C# format
// port made that an explicit FormatContext, which the Tracker holds instead.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Ls.Change;

// tracker.go:20-36 — NodeOptions.
public sealed class NodeOptions
{
    // Text to be inserted before the new node
    public string Prefix = "";

    // Text to be inserted after the new node
    public string Suffix = "";

    // Text of inserted node will be formatted with this indentation, otherwise indentation will be inferred from the old node
    internal int? indentation;

    // Text of inserted node will be formatted with this delta, otherwise delta will be inferred from the new node kind
    internal int? delta;

    public LeadingTriviaOption LeadingTriviaOption;
    public TrailingTriviaOption TrailingTriviaOption;
    internal string joiner = "";
}

// tracker.go:38-46 — LeadingTriviaOption.
public enum LeadingTriviaOption
{
    None = 0,
    Exclude = 1,
    IncludeAll = 2,
    JSDoc = 3,
    StartLine = 4,
}

// tracker.go:48-55 — TrailingTriviaOption.
public enum TrailingTriviaOption
{
    None = 0,
    Exclude = 1,
    ExcludeWhitespace = 2,
    Include = 3,
}

// tracker.go:57-64 — trackerEditKind.
internal enum TrackerEditKind
{
    Text = 1,
    Remove = 2,
    ReplaceWithSingleNode = 3,
    ReplaceWithMultipleNodes = 4,
}

// tracker.go:66-75 — trackerEdit.
internal sealed class TrackerEdit
{
    internal TrackerEditKind kind;
    internal global::Ts.LsProto.Range Range = null!;

    internal string NewText = ""; // kind == text

    internal Node? Node; // single
    internal Node[]? nodes; // multiple
    internal NodeOptions options = new();
}

// tracker.go:77-80 — nodesInsertedAtStartState.
internal sealed class NodesInsertedAtStartState
{
    internal Node node = null!;
    internal SourceFile sourceFile = null!;
}

// tracker.go:101-104 — deletedNode.
internal readonly record struct DeletedNode(SourceFile SourceFile, Node Node);

// tracker.go:82-99 — Tracker.
public sealed partial class Tracker
{
    // initialized with
    internal global::Ts.Format.FormatCodeSettings formatSettings = null!;
    internal string newLine = "";
    internal global::Ts.Ls.LsConv.Converters converters = null!;
    internal global::Ts.Format.FormatContext ctx = null!;
    internal global::Ts.Printer.EmitContext EmitContext = null!;

    internal global::Ts.Ast.NodeFactory NodeFactory = null!;

    internal global::Ts.Collections.MultiMap<SourceFile, TrackerEdit> changes = new();
    internal List<DeletedNode> deletedNodes = [];
    internal Dictionary<Node, NodesInsertedAtStartState> nodesWithInsertionsAtStart =
        new(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);

    // created during call to getChanges
    internal global::Ts.Printer.ChangeTrackerWriter? writer;
    // printer

    internal global::Ts.Printer.NodeFactory Factory => EmitContext.Factory;

    // tracker.go:106-122 — NewTracker.
    public static Tracker NewTracker(CompilerOptions compilerOptions, global::Ts.Format.FormatCodeSettings formatOptions, global::Ts.Ls.LsConv.Converters converters)
    {
        var emitContext = global::Ts.Printer.EmitContext.NewEmitContext();
        var newLine = compilerOptions.NewLine.GetNewLineCharacter();
        var ctx = global::Ts.Format.FormatContext.WithFormatCodeSettings(formatOptions, newLine); // !!! formatSettings in context?
        return new Tracker
        {
            EmitContext = emitContext,
            NodeFactory = emitContext.Factory.AsNodeFactory(),
            changes = new global::Ts.Collections.MultiMap<SourceFile, TrackerEdit>(),
            ctx = ctx,
            converters = converters,
            formatSettings = formatOptions,
            newLine = newLine,
            nodesWithInsertionsAtStart = new Dictionary<Node, NodesInsertedAtStartState>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance),
        };
    }

    // tracker.go:124-131 — GetChanges: returns the accumulated text edits.
    // Note: after calling this, the Tracker object must be discarded!
    public Dictionary<string, List<global::Ts.LsProto.TextEdit>> GetChanges()
    {
        FinishDeleteDeclarations();
        FinishNodesWithInsertionsAtStart();
        var changesOut = GetTextChangesFromChanges();
        // !!! changes for new files
        return changesOut;
    }

    // tracker.go:133-142 — ReplaceNode.
    public void ReplaceNode(SourceFile sourceFile, Node oldNode, Node newNode, NodeOptions? options)
    {
        if (options == null)
        {
            // defaults to `useNonAdjustedPositions`
            options = new NodeOptions
            {
                LeadingTriviaOption = LeadingTriviaOption.Exclude,
                TrailingTriviaOption = TrailingTriviaOption.Exclude,
            };
        }
        ReplaceRange(sourceFile, GetAdjustedRange(sourceFile, oldNode, oldNode, options.LeadingTriviaOption, options.TrailingTriviaOption), newNode, options);
    }

    // tracker.go:144-152 — ReplaceNodeWithNodes.
    public void ReplaceNodeWithNodes(SourceFile sourceFile, Node oldNode, Node[] newNodes, NodeOptions? options)
    {
        if (options == null)
        {
            options = new NodeOptions
            {
                LeadingTriviaOption = LeadingTriviaOption.Exclude,
                TrailingTriviaOption = TrailingTriviaOption.Exclude,
            };
        }
        ReplaceRangeWithNodes(sourceFile, GetAdjustedRange(sourceFile, oldNode, oldNode, options.LeadingTriviaOption, options.TrailingTriviaOption), newNodes, options);
    }

    // tracker.go:153-155 — ReplaceRange.
    public void ReplaceRange(SourceFile sourceFile, global::Ts.LsProto.Range lsprotoRange, Node newNode, NodeOptions options)
    {
        changes.Add(sourceFile, new TrackerEdit { kind = TrackerEditKind.ReplaceWithSingleNode, Range = lsprotoRange, options = options, Node = newNode });
    }

    // tracker.go:157-159 — ReplaceRangeWithText.
    public void ReplaceRangeWithText(SourceFile sourceFile, global::Ts.LsProto.Range lsprotoRange, string text)
    {
        changes.Add(sourceFile, new TrackerEdit { kind = TrackerEditKind.Text, Range = lsprotoRange, NewText = text });
    }

    // tracker.go:161-167 — ReplaceRangeWithNodes.
    public void ReplaceRangeWithNodes(SourceFile sourceFile, global::Ts.LsProto.Range lsprotoRange, Node[] newNodes, NodeOptions options)
    {
        if (newNodes.Length == 1)
        {
            ReplaceRange(sourceFile, lsprotoRange, newNodes[0], options);
            return;
        }
        changes.Add(sourceFile, new TrackerEdit { kind = TrackerEditKind.ReplaceWithMultipleNodes, Range = lsprotoRange, nodes = newNodes, options = options });
    }

    // tracker.go:169-171 — InsertText.
    public void InsertText(SourceFile sourceFile, global::Ts.LsProto.Position pos, string text)
    {
        ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = pos, End = pos }, text);
    }

    // tracker.go:173-176 — InsertNodeAt.
    public void InsertNodeAt(SourceFile sourceFile, int pos, Node newNode, NodeOptions options)
    {
        var lsPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), pos);
        ReplaceRange(sourceFile, new global::Ts.LsProto.Range { Start = lsPos, End = lsPos }, newNode, options);
    }

    // tracker.go:178-181 — InsertNodesAt.
    public void InsertNodesAt(SourceFile sourceFile, int pos, Node[] newNodes, NodeOptions options)
    {
        var lsPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), pos);
        ReplaceRangeWithNodes(sourceFile, new global::Ts.LsProto.Range { Start = lsPos, End = lsPos }, newNodes, options);
    }

    // tracker.go:183-186 — InsertNodeAfter.
    public void InsertNodeAfter(SourceFile sourceFile, Node after, Node newNode)
    {
        var endPosition = EndPosForInsertNodeAfter(sourceFile, after, newNode);
        InsertNodeAt(sourceFile, endPosition, newNode, GetInsertNodeAfterOptions(sourceFile, after));
    }

    // tracker.go:188-191 — InsertNodesAfter.
    public void InsertNodesAfter(SourceFile sourceFile, Node after, Node[] newNodes)
    {
        var endPosition = EndPosForInsertNodeAfter(sourceFile, after, newNodes[0]);
        InsertNodesAt(sourceFile, endPosition, newNodes, GetInsertNodeAfterOptions(sourceFile, after));
    }

    // tracker.go:193-196 — InsertNodeBefore.
    public void InsertNodeBefore(SourceFile sourceFile, Node before, Node newNode, bool blankLineBetween, LeadingTriviaOption leadingTriviaOption)
    {
        InsertNodeAt(sourceFile, GetAdjustedStartPosition(sourceFile, before, leadingTriviaOption, false), newNode, GetOptionsForInsertNodeBefore(before, newNode, blankLineBetween));
    }

    // tracker.go:198-236 — TryInsertTypeAnnotation: inserts a type annotation after the appropriate
    // position on a node (after the close paren for function-like, after the
    // name/exclamation/question for variable-like). Returns true if successful.
    public bool TryInsertTypeAnnotation(SourceFile sourceFile, Node node, Node typeNode)
    {
        Node? endNode;
        if (AstUtilities.IsFunctionLike(node))
        {
            endNode = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.CloseParenToken, sourceFile);
            if (endNode == null)
            {
                if (!NodeTests.IsArrowFunction(node))
                {
                    return false;
                }
                // If no `)`, is an arrow function `x => x`, so use the end of the first parameter
                var funcParams = node.Parameters() ?? [];
                if (funcParams.Length == 0)
                {
                    return false;
                }
                endNode = funcParams[0];
            }
        }
        else
        {
            switch (node.Kind)
            {
                case SyntaxKind.VariableDeclaration:
                    endNode = node.AsVariableDeclaration().ExclamationToken;
                    break;
                case SyntaxKind.PropertySignature:
                    endNode = node.AsPropertySignatureDeclaration().PostfixToken;
                    break;
                case SyntaxKind.PropertyDeclaration:
                    endNode = node.AsPropertyDeclaration().PostfixToken;
                    break;
                case SyntaxKind.Parameter:
                    endNode = node.AsParameterDeclaration().QuestionToken;
                    break;
                default:
                    endNode = null;
                    break;
            }
            endNode ??= node.Name();
        }
        if (endNode == null)
        {
            return false;
        }
        InsertNodeAt(sourceFile, endNode.End, typeNode, new NodeOptions { Prefix = ": " });
        return true;
    }

    // tracker.go:238-253 — ParenthesizeArrowParameters: wraps the parameters of a paren-less arrow
    // function in `(` and `)`. This is a no-op if the arrow function already has parens.
    public void ParenthesizeArrowParameters(SourceFile sourceFile, Node arrowFunc)
    {
        if (global::Ts.AstNav.Tokens.FindChildOfKind(arrowFunc, SyntaxKind.CloseParenToken, sourceFile) != null)
        {
            return;
        }
        var arrowParams = arrowFunc.Parameters() ?? [];
        if (arrowParams.Length == 0)
        {
            return;
        }
        var firstParam = arrowParams[0];
        var lastParam = arrowParams[^1];
        var startPos = global::Ts.AstNav.Tokens.GetStartOfNode(firstParam, sourceFile, false);
        InsertText(sourceFile, converters.PositionToLineAndCharacter(sourceFile.AsScript(), startPos), "(");
        InsertText(sourceFile, converters.PositionToLineAndCharacter(sourceFile.AsScript(), lastParam.End), ")");
    }

    // tracker.go:255-262 — InsertModifierBefore: inserts a modifier token (like 'type') before a
    // node with a trailing space.
    public void InsertModifierBefore(SourceFile sourceFile, SyntaxKind modifier, Node before)
    {
        var pos = global::Ts.AstNav.Tokens.GetStartOfNode(before, sourceFile, false);
        var token = NodeFactory.NewToken(modifier);
        token.Loc = new TextRange(pos, pos);
        token.Parent = before.Parent;
        InsertNodeAt(sourceFile, pos, token, new NodeOptions { Suffix = " " });
    }

    // tracker.go:264-268 — Delete: queues a node for deletion with smart handling of list items,
    // imports, etc. The actual deletion happens in finishDeleteDeclarations during GetChanges.
    public void Delete(SourceFile sourceFile, Node node)
    {
        deletedNodes.Add(new DeletedNode(sourceFile, node));
    }

    // tracker.go:270-274 — DeleteRange: deletes a text range from the source file.
    public void DeleteRange(SourceFile sourceFile, TextRange textRange)
    {
        var lspRange = converters.ToLSPRange(sourceFile.AsScript(), textRange);
        ReplaceRangeWithText(sourceFile, lspRange, "");
    }

    // tracker.go:276-281 — DeleteNode: deletes a node immediately with specified trivia options.
    // Stop! Consider using Delete instead, which has logic for deleting nodes from delimited lists.
    public void DeleteNode(SourceFile sourceFile, Node node, LeadingTriviaOption leadingTrivia, TrailingTriviaOption trailingTrivia)
    {
        var rng = GetAdjustedRange(sourceFile, node, node, leadingTrivia, trailingTrivia);
        ReplaceRangeWithText(sourceFile, rng, "");
    }

    // tracker.go:283-290 — DeleteNodeRange: deletes a range of nodes with specified trivia options.
    public void DeleteNodeRange(SourceFile sourceFile, Node startNode, Node endNode, LeadingTriviaOption leadingTrivia, TrailingTriviaOption trailingTrivia)
    {
        var startPosition = GetAdjustedStartPosition(sourceFile, startNode, leadingTrivia, false);
        var endPosition = GetAdjustedEndPosition(sourceFile, endNode, trailingTrivia);
        var startPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), startPosition);
        var endPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), endPosition);
        ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = startPos, End = endPos }, "");
    }

    // tracker.go:292-334 — finishDeleteDeclarations: processes all queued deletions with smart
    // handling for lists and trailing commas.
    internal void FinishDeleteDeclarations()
    {
        var deletedNodesInLists = new HashSet<Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);

        foreach (var deleted in deletedNodes)
        {
            // Skip if this node is contained within another deleted node
            var isContained = false;
            foreach (var other in deletedNodes)
            {
                if (other.SourceFile == deleted.SourceFile && other.Node != deleted.Node &&
                    RangeContainsRangeExclusive(other.Node, deleted.Node))
                {
                    isContained = true;
                    break;
                }
            }
            if (isContained)
            {
                continue;
            }

            DeleteHelpers.DeleteDeclaration(this, deletedNodesInLists, deleted.SourceFile, deleted.Node);
        }

        // Handle trailing commas for last elements in lists
        foreach (var node in deletedNodesInLists) // map-iter: 序无关 (per-list trailing-comma fixups target disjoint ranges; edits sorted by position later)
        {
            var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
            var list = global::Ts.Format.Indent.GetContainingList(node, sourceFile);
            if (list == null || node != list.Nodes[^1])
            {
                continue;
            }

            var lastNonDeletedIndex = -1;
            for (var i = list.Nodes.Length - 2; i >= 0; i--)
            {
                if (!deletedNodesInLists.Contains(list.Nodes[i]))
                {
                    lastNonDeletedIndex = i;
                    break;
                }
            }

            if (lastNonDeletedIndex != -1)
            {
                var startPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), list.Nodes[lastNonDeletedIndex].End);
                var endPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), StartPositionToDeleteNodeInList(sourceFile, list.Nodes[lastNonDeletedIndex + 1]));
                ReplaceRangeWithText(sourceFile, new global::Ts.LsProto.Range { Start = startPos, End = endPos }, "");
            }
        }
    }

    // tracker.go:336-353 — endPosForInsertNodeAfter.
    private int EndPosForInsertNodeAfter(SourceFile sourceFile, Node after, Node newNode)
    {
        if (TrackerImplHelpers.NeedSemicolonBetween(after, newNode) && sourceFile.Text()[after.End - 1] != ';')
        {
            // check if previous statement ends with semicolon
            // if not - insert semicolon to preserve the code from changing the meaning due to ASI
            var endPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), after.End);
            var semicolon = NodeFactory.NewToken(SyntaxKind.SemicolonToken);
            semicolon.Loc = new TextRange(after.End, after.End);
            semicolon.Parent = after.Parent;
            ReplaceRange(
                sourceFile,
                new global::Ts.LsProto.Range { Start = endPos, End = endPos },
                semicolon,
                new NodeOptions());
        }
        return GetAdjustedEndPosition(sourceFile, after, TrailingTriviaOption.None);
    }

    // tracker.go:355-459 — InsertNodeInListAfter.
    // This function should be used to insert nodes in lists when nodes don't carry separators as
    // the part of the node range, i.e. arguments in arguments lists, parameters in parameter lists
    // etc. Note that separators are part of the node in statements and class elements.
    public void InsertNodeInListAfter(SourceFile sourceFile, Node after, Node newNode, NodeList? containingList)
    {
        containingList ??= global::Ts.Format.Indent.GetContainingList(after, sourceFile);
        if (containingList == null)
        {
            // Debug.fail("node is not a list element")
            return;
        }
        var index = Array.IndexOf(containingList.Nodes, after);
        if (index < 0)
        {
            return;
        }
        var end = after.End;
        if (index != containingList.Nodes.Length - 1)
        {
            // any element except the last one
            // use next sibling as an anchor
            var nextToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, after.End);
            if (nextToken != null && IsSeparator(after, nextToken))
            {
                // for list
                // a, b, c
                // create change for adding 'e' after 'a' as
                // - find start of next element after a (it is b)
                // - use next element start as start and end position in final change
                // - build text of change by formatting the text of node + whitespace trivia of b

                // in multiline case it will work as
                //   a,
                //   b,
                //   c,
                // result - '*' denotes leading trivia that will be inserted after new text (displayed as '#')
                //   a,
                //   insertedtext<separator>#
                // ###b,
                //   c,
                var nextNode = containingList.Nodes[index + 1];
                var startPos = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), nextNode.Pos, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = false, StopAtComments = true });

                // write separator and leading trivia of the next element as suffix
                var suffix = global::Ts.Scanner.Scanner.TokenToString(nextToken.Kind) + sourceFile.Text()[nextToken.End..startPos];
                InsertNodesAt(sourceFile, startPos, [newNode], new NodeOptions { Suffix = suffix });
            }
            return;
        }

        var afterStart = global::Ts.AstNav.Tokens.GetStartOfNode(after, sourceFile, false);
        var afterStartLinePosition = global::Ts.Format.Util.GetLineStartPositionForPosition(afterStart, sourceFile);

        // insert element after the last element in the list that has more than one item
        // pick the element preceding the after element to:
        // - pick the separator
        // - determine if list is a multiline
        var multilineList = false;

        // if list has only one element then we'll format is as multiline if node has comment in trailing trivia, or as singleline otherwise
        // i.e. var x = 1 // this is x
        //     | new element will be inserted at this position
        var separator = SyntaxKind.CommaToken; // SyntaxKind.CommaToken | SyntaxKind.SemicolonToken
        if (containingList.Nodes.Length != 1)
        {
            // otherwise, if list has more than one element, pick separator from the list
            var tokenBeforeInsertPosition = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, after.Pos);
            separator = IsSeparator(after, tokenBeforeInsertPosition) ? tokenBeforeInsertPosition!.Kind : SyntaxKind.CommaToken;
            // determine if list is multiline by checking lines of after element and element that precedes it.
            var afterMinusOneStartLinePosition = global::Ts.Format.Util.GetLineStartPositionForPosition(global::Ts.AstNav.Tokens.GetStartOfNode(containingList.Nodes[index - 1], sourceFile, false), sourceFile);
            multilineList = afterMinusOneStartLinePosition != afterStartLinePosition;
        }
        if (TrackerImplHelpers.HasCommentsBeforeLineBreak(sourceFile.Text(), after.End) || !PositionsAreOnSameLine(containingList.Pos, containingList.End, sourceFile))
        {
            // in this case we'll always treat containing list as multiline
            multilineList = true;
        }
        if (multilineList)
        {
            // insert separator immediately following the 'after' node to preserve comments in trailing trivia
            var separatorToken = NodeFactory.NewToken(separator);
            var separatorString = global::Ts.Scanner.Scanner.TokenToString(separator);
            separatorToken.Loc = new TextRange(end, end + separatorString.Length);
            separatorToken.Parent = after.Parent;
            var endLsPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), end);
            ReplaceRange(sourceFile, new global::Ts.LsProto.Range { Start = endLsPos, End = endLsPos }, separatorToken, new NodeOptions());
            // use the same indentation as 'after' item
            var indentation = global::Ts.Format.Indent.FindFirstNonWhitespaceColumn(afterStartLinePosition, afterStart, sourceFile, formatSettings);
            // insert element before the line break on the line that contains 'after' element
            var insertPos = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), end, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = true, StopAtComments = false });
            // find position before "\n" or "\r\n"
            while (insertPos != end && global::Ts.StringUtil.StringUtil.IsLineBreak(sourceFile.Text()[insertPos - 1]))
            {
                insertPos--;
            }
            var insertLSPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), insertPos);
            ReplaceRange(
                sourceFile,
                new global::Ts.LsProto.Range { Start = insertLSPos, End = insertLSPos },
                newNode,
                new NodeOptions
                {
                    indentation = indentation,
                    Prefix = newLine,
                });
        }
        else
        {
            var separatorString = global::Ts.Scanner.Scanner.TokenToString(separator);
            var endLsPos = converters.PositionToLineAndCharacter(sourceFile.AsScript(), end);
            ReplaceRange(sourceFile, new global::Ts.LsProto.Range { Start = endLsPos, End = endLsPos }, newNode, new NodeOptions { Prefix = separatorString + " " });
        }
    }

    // tracker.go:461-480 — InsertImportSpecifierAtIndex: inserts a new import specifier at the
    // specified index in a NamedImports list.
    public void InsertImportSpecifierAtIndex(SourceFile sourceFile, Node newSpecifier, Node namedImports, int index)
    {
        var elements = namedImports.AsNamedImports().Elements!.Nodes;

        Node? prevSpecifier = null;
        if (index > 0 && index - 1 < elements.Length)
        {
            prevSpecifier = elements[index - 1];
        }
        if (prevSpecifier != null)
        {
            InsertNodeInListAfter(sourceFile, prevSpecifier, newSpecifier, null);
        }
        else
        {
            InsertNodeBefore(
                sourceFile,
                elements[0],
                newSpecifier,
                !PositionsAreOnSameLine(global::Ts.AstNav.Tokens.GetStartOfNode(elements[0], sourceFile, false), global::Ts.AstNav.Tokens.GetStartOfNode(namedImports.Parent!.Parent!, sourceFile, false), sourceFile),
                LeadingTriviaOption.None);
        }
    }

    // tracker.go:482-504 — InsertAtTopOfFile.
    public void InsertAtTopOfFile(SourceFile sourceFile, Node[] insert, bool blankLineBetween)
    {
        if (insert.Length == 0)
        {
            return;
        }

        var pos = GetInsertionPositionAtSourceFileTop(sourceFile);
        var options = new NodeOptions();
        if (pos != 0)
        {
            options.Prefix = newLine;
        }
        if (sourceFile.Text().Length == 0 || !global::Ts.StringUtil.StringUtil.IsLineBreak(sourceFile.Text()[pos]))
        {
            options.Suffix = newLine;
        }
        if (blankLineBetween)
        {
            options.Suffix += newLine;
        }

        if (insert.Length == 1)
        {
            InsertNodeAt(sourceFile, pos, insert[0], options);
        }
        else
        {
            InsertNodesAt(sourceFile, pos, insert, options);
        }
    }

    // tracker.go:506-508 — InsertMemberAtStart.
    public void InsertMemberAtStart(SourceFile sourceFile, Node node, Node newElement)
    {
        InsertNodeAtStartWorker(sourceFile, node, newElement);
    }

    // tracker.go:510-522 — insertNodeAtStartWorker.
    private void InsertNodeAtStartWorker(SourceFile sourceFile, Node node, Node newElement)
    {
        var indentation = TryComputeIndentationFromExistingMembers(sourceFile, node);
        if (indentation < 0)
        {
            indentation = TryComputeIndentationForNewMember(sourceFile, node);
        }

        var members = GetMembersOrProperties(node);
        if (members == null)
        {
            return;
        }

        InsertNodeAt(sourceFile, members.Pos, newElement, GetInsertNodeAtStartInsertOptions(sourceFile, node, indentation));
    }

    // tracker.go:524-538 — tryComputeIndentationForNewMember.
    private int TryComputeIndentationForNewMember(SourceFile sourceFile, Node node)
    {
        var nodeStart = global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false);
        var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(nodeStart, sourceFile);

        var tabSize = formatSettings.TabSize;
        if (tabSize <= 0)
        {
            tabSize = 4;
        }

        var indentSize = formatSettings.IndentSize;
        if (indentSize <= 0)
        {
            indentSize = 4;
        }
        return Math.Max(FindIndentationColumn(sourceFile.Text(), lineStart, nodeStart, tabSize), 0) + indentSize;
    }

    // tracker.go:540-583 — tryComputeIndentationFromExistingMembers.
    private int TryComputeIndentationFromExistingMembers(SourceFile sourceFile, Node node)
    {
        var members = GetMembersOrProperties(node);
        if (members == null)
        {
            return -1;
        }

        var indentation = -1;
        var text = sourceFile.Text();
        var tabSize = formatSettings.TabSize;
        var last = node;

        if (tabSize <= 0)
        {
            tabSize = 4;
        }

        foreach (var member in members.Nodes)
        {
            if (member == null)
            {
                continue;
            }
            if (global::Ts.Printer.PrinterUtilities.RangeStartPositionsAreOnSameLine(last.Loc, member.Loc, sourceFile))
            {
                return -1;
            }

            var memberStart = global::Ts.AstNav.Tokens.GetStartOfNode(member, sourceFile, false);
            var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(memberStart, sourceFile);
            var column = FindIndentationColumn(text, lineStart, memberStart, tabSize);
            if (column < 0)
            {
                return -1;
            }

            if (indentation >= 0)
            {
                if (indentation != column)
                {
                    return -1;
                }
                last = member;
                continue;
            }

            indentation = column;
            last = member;
        }

        return indentation;
    }

    // tracker.go:585-616 — getInsertNodeAfterOptions.
    private NodeOptions GetInsertNodeAfterOptions(SourceFile sourceFile, Node node)
    {
        var newLineChar = newLine;
        NodeOptions options;
        switch (node.Kind)
        {
            case SyntaxKind.Parameter:
                // default opts
                options = new NodeOptions();
                break;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ModuleDeclaration:
                options = new NodeOptions { Prefix = newLineChar, Suffix = newLineChar };
                break;

            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.Identifier:
                options = new NodeOptions { Prefix = ", " };
                break;

            case SyntaxKind.PropertyAssignment:
                options = new NodeOptions { Suffix = "," + newLineChar };
                break;

            case SyntaxKind.ExportKeyword:
                options = new NodeOptions { Prefix = " " };
                break;

            default:
                if (!(AstUtilities.IsStatement(node) || AstUtilities.IsClassOrTypeElement(node)))
                {
                    // Else we haven't handled this kind of node yet -- add it
                    throw new InvalidOperationException("unimplemented node type " + node.Kind + " in changeTracker.getInsertNodeAfterOptions");
                }
                options = new NodeOptions { Suffix = newLineChar };
                break;
        }
        if (node.End == sourceFile.End && AstUtilities.IsStatement(node))
        {
            options.Prefix = newLine + options.Prefix;
        }

        return options;
    }

    // tracker.go:618-645 — getOptionsForInsertNodeBefore.
    private NodeOptions GetOptionsForInsertNodeBefore(Node before, Node inserted, bool blankLineBetween)
    {
        if (AstUtilities.IsStatement(before) || AstUtilities.IsClassOrTypeElement(before))
        {
            if (blankLineBetween)
            {
                return new NodeOptions { Suffix = newLine + newLine };
            }
            return new NodeOptions { Suffix = newLine };
        }
        else if (before.Kind == SyntaxKind.VariableDeclaration)
        {
            // insert `x = 1, ` into `const x = 1, y = 2;
            return new NodeOptions { Suffix = ", " };
        }
        else if (before.Kind == SyntaxKind.Parameter)
        {
            if (inserted.Kind == SyntaxKind.Parameter)
            {
                return new NodeOptions { Suffix = ", " };
            }
            return new NodeOptions();
        }
        else if ((before.Kind == SyntaxKind.StringLiteral && before.Parent != null && before.Parent.Kind == SyntaxKind.ImportDeclaration) || before.Kind == SyntaxKind.NamedImports)
        {
            return new NodeOptions { Suffix = ", " };
        }
        else if (before.Kind == SyntaxKind.ImportSpecifier)
        {
            var suffix = ",";
            if (blankLineBetween)
            {
                suffix += newLine;
            }
            else
            {
                suffix += " ";
            }
            return new NodeOptions { Suffix = suffix };
        }
        // We haven't handled this kind of node yet -- add it
        throw new InvalidOperationException("unimplemented node type " + before.Kind + " in changeTracker.getOptionsForInsertNodeBefore");
    }

    // tracker.go:647-680 — getInsertNodeAtStartInsertOptions.
    private NodeOptions GetInsertNodeAtStartInsertOptions(SourceFile sourceFile, Node node, int indentation)
    {
        nodesWithInsertionsAtStart.TryGetValue(node, out var state);
        var hasPreviousInsertion = state != null;
        if (state == null)
        {
            state = new NodesInsertedAtStartState
            {
                node = node,
                sourceFile = sourceFile,
            };
            nodesWithInsertionsAtStart[node] = state;
        }

        var members = GetMembersOrProperties(node);
        var isObjectLiteral = NodeTests.IsObjectLiteralExpression(node);
        var isJSON = AstUtilities.IsJsonSourceFile(sourceFile);

        var hasMembers = members != null && members.Nodes.Length > 0;

        var insertTrailingComma = isObjectLiteral && (hasMembers || !isJSON);
        var insertLeadingComma = isObjectLiteral && isJSON && !hasMembers && hasPreviousInsertion;

        var suffix = "";
        if (insertTrailingComma)
        {
            suffix = ",";
        }
        else if (NodeTests.IsInterfaceDeclaration(node) && !hasMembers)
        {
            suffix = ";";
        }

        var prefix = newLine;
        if (insertLeadingComma)
        {
            prefix = "," + prefix;
        }

        return new NodeOptions { indentation = indentation, Prefix = prefix, Suffix = suffix };
    }

    // tracker.go:682-710 — finishNodesWithInsertionsAtStart.
    internal void FinishNodesWithInsertionsAtStart()
    {
        foreach (var (_, state) in nodesWithInsertionsAtStart) // map-iter: 序无关 (per-node fixups target disjoint brace ranges; edits sorted by position later)
        {
            if (state == null)
            {
                continue;
            }

            var openBrace = global::Ts.AstNav.Tokens.FindChildOfKind(state.node, SyntaxKind.OpenBraceToken, state.sourceFile);
            if (openBrace == null)
            {
                continue;
            }

            var closeBrace = global::Ts.AstNav.Tokens.FindChildOfKind(state.node, SyntaxKind.CloseBraceToken, state.sourceFile);
            if (closeBrace == null)
            {
                continue;
            }

            var members = GetMembersOrProperties(state.node);
            var isEmpty = members == null || members.Nodes.Length == 0;
            var isSingleLine = PositionsAreOnSameLine(openBrace.End, closeBrace.End, state.sourceFile);

            if (isEmpty && isSingleLine && openBrace.End != closeBrace.End - 1)
            {
                DeleteRange(state.sourceFile, new TextRange(openBrace.End, closeBrace.End - 1));
            }

            if (isSingleLine)
            {
                InsertText(state.sourceFile, converters.PositionToLineAndCharacter(state.sourceFile.AsScript(), closeBrace.End - 1), newLine);
            }
        }
    }

    // tracker.go:712-717 — getMembersOrProperties.
    internal static NodeList? GetMembersOrProperties(Node node)
    {
        if (NodeTests.IsObjectLiteralExpression(node))
        {
            return node.PropertyList();
        }
        return node.MemberList();
    }

    // tracker.go:719-721 — rangeContainsRangeExclusive.
    internal static bool RangeContainsRangeExclusive(Node outer, Node inner)
    {
        return outer.Pos < inner.Pos && inner.End < outer.End;
    }

    // tracker.go:723-725 — isSeparator.
    internal static bool IsSeparator(Node node, Node? candidate)
    {
        return candidate != null && node.Parent != null && (candidate.Kind == SyntaxKind.CommaToken || (candidate.Kind == SyntaxKind.SemicolonToken && node.Parent.Kind == SyntaxKind.ObjectLiteralExpression));
    }

    // tracker.go:727-744 — findIndentationColumn.
    internal static int FindIndentationColumn(string text, int lineStart, int memberStart, int tabSize)
    {
        var column = 0;

        for (var i = lineStart; i < memberStart && i < text.Length; i++)
        {
            var ch = text[i];

            if (global::Ts.StringUtil.StringUtil.IsLineBreak(ch))
            {
                return -1;
            }
            if (global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                column = AdvanceIndentationColumn(column, ch, tabSize);
                continue;
            }
            return column;
        }

        return column;
    }

    // tracker.go:746-751 — advanceIndentationColumn.
    internal static int AdvanceIndentationColumn(int column, char ch, int tabSize)
    {
        if (ch == '\t')
        {
            return column + tabSize - (column % tabSize);
        }
        return column + 1;
    }
}

// The Converters Script adapter for ast.SourceFile (Go passes *ast.SourceFile structurally).
public static class SourceFileScriptExtensions
{
    private sealed class SourceFileScript : global::Ts.Ls.LsConv.Script
    {
        internal SourceFile file = null!;

        public string FileName() => file.FileName();

        public string Text() => file.Text();
    }

    public static global::Ts.Ls.LsConv.Script AsScript(this SourceFile file) => new SourceFileScript { file = file };
}
