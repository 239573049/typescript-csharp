// Ported from tsgo internal/printer/changetrackerwriter.go (whole file): the emit writer used
// by the language-service change tracker to print synthesized nodes while recording each
// node's non-trivia start/end positions so they can be re-assigned onto cloned nodes.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

// changetrackerwriter.go:12-17 — ChangeTrackerWriter. (Go embeds textWriter; C# composes over
// the sealed TextWriter and forwards the EmitTextWriter surface.)
// changetrackerwriter.go:19-22 — triviaPositionKey (*ast.Node | *ast.NodeList) dissolves into
// reference-keyed object dictionaries.
public sealed class ChangeTrackerWriter : EmitTextWriter
{
    private readonly EmitTextWriter textWriter;
    private int lastNonTriviaPosition;
    private readonly Dictionary<object, int> pos = new(ReferenceEqualityComparer<object>.Instance);
    private readonly Dictionary<object, int> end = new(ReferenceEqualityComparer<object>.Instance);

    // changetrackerwriter.go:24-37 — NewChangeTrackerWriter.
    public ChangeTrackerWriter(string newline, int indentSize)
    {
        // TODO: Callers passing -1 should pass actual indent options once indent-related formatting is ported.
        if (indentSize < 0)
        {
            indentSize = TextWriter.GetDefaultIndentSize();
        }
        textWriter = TextWriter.NewTextWriter(newline, indentSize);
        lastNonTriviaPosition = 0;
        textWriter.Clear();
    }

    // changetrackerwriter.go:39-71 — GetPrintHandlers.
    public PrintHandlers GetPrintHandlers()
    {
        return new PrintHandlers
        {
            OnBeforeEmitNode = nodeOpt =>
            {
                if (nodeOpt != null)
                {
                    SetPos(nodeOpt);
                }
            },
            OnAfterEmitNode = nodeOpt =>
            {
                if (nodeOpt != null)
                {
                    SetEnd(nodeOpt);
                }
            },
            OnBeforeEmitNodeList = nodesOpt =>
            {
                if (nodesOpt != null)
                {
                    SetPos(nodesOpt);
                }
            },
            OnAfterEmitNodeList = nodesOpt =>
            {
                if (nodesOpt != null)
                {
                    SetEnd(nodesOpt);
                }
            },
            OnBeforeEmitToken = nodeOpt =>
            {
                if (nodeOpt != null)
                {
                    SetPos(nodeOpt);
                }
            },
            OnAfterEmitToken = nodeOpt =>
            {
                if (nodeOpt != null)
                {
                    SetEnd(nodeOpt);
                }
            },
        };
    }

    // changetrackerwriter.go:73-79
    private void SetPos(object node) => pos[node] = lastNonTriviaPosition;

    private void SetEnd(object node) => end[node] = lastNonTriviaPosition;

    // changetrackerwriter.go:81-87
    private int GetPos(object node) => pos.GetValueOrDefault(node);

    private int GetEnd(object node) => end.GetValueOrDefault(node);

    // changetrackerwriter.go:89-103 — setLastNonTriviaPosition.
    private void SetLastNonTriviaPosition(string s, bool force)
    {
        if (force || global::Ts.Scanner.Scanner.SkipTrivia(s, 0) != s.Length)
        {
            lastNonTriviaPosition = textWriter.GetTextPos();
            // trim trailing whitespaces
            var trimPos = s.Length;
            while (trimPos > 0)
            {
                int r;
                int size;
                var c = s[trimPos - 1];
                if (char.IsLowSurrogate(c) && trimPos >= 2 && char.IsHighSurrogate(s[trimPos - 2]))
                {
                    r = char.ConvertToUtf32(s[trimPos - 2], c);
                    size = 2;
                }
                else
                {
                    r = c;
                    size = 1;
                }
                if (global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(r))
                {
                    trimPos -= size;
                }
                else
                {
                    break;
                }
            }
            lastNonTriviaPosition -= s.Length - trimPos;
        }
    }

    // changetrackerwriter.go:105-127 — AssignPositionsToNode.
    public Node AssignPositionsToNode(Node node, global::Ts.Ast.NodeFactory factory)
    {
        NodeVisitor visitor = null!;
        visitor = new NodeVisitor(
            n => AssignPositionsToNodeWorker(n, visitor),
            factory,
            new NodeVisitorHooks
            {
                VisitNode = (n, v) => AssignPositionsToNodeWorker(n!, v),
                VisitNodes = (nodes, v) => AssignPositionsToNodeArray(nodes, v),
                VisitToken = (n, v) => AssignPositionsToNodeWorker(n!, v),
                VisitModifiers = (modifiers, v) =>
                {
                    if (modifiers != null)
                    {
                        var newNodeList = AssignPositionsToNodeArray(modifiers, v)!;
                        // Return a new ModifierList so that VisitEachChild/Update detects the
                        // change and creates a new node with reassigned child positions.
                        return v.Factory.NewModifierList(newNodeList.Nodes);
                    }
                    return modifiers;
                },
            });
        return AssignPositionsToNodeWorker(node, visitor)!;
    }

    // changetrackerwriter.go:129-148 — assignPositionsToNodeWorker.
    private Node? AssignPositionsToNodeWorker(Node? node, NodeVisitor v)
    {
        if (node == null)
        {
            return node;
        }
        var visited = node.VisitEachChild(v);
        // create proxy node for non synthesized nodes
        var newNode = visited;
        if (!AstUtilities.NodeIsSynthesized(visited))
        {
            newNode = visited.Clone(v.Factory)!;
        }
        newNode.ForEachChild(child =>
        {
            child.Parent = newNode;
            return true;
        });
        newNode.Loc = new TextRange(GetPos(node), GetEnd(node));
        return newNode;
    }

    // changetrackerwriter.go:150-171 — assignPositionsToNodeArray.
    private NodeList? AssignPositionsToNodeArray(NodeList? nodes, NodeVisitor v)
    {
        var visited = v.VisitNodes(nodes);
        if (visited == null)
        {
            return visited;
        }
        if (nodes == null)
        {
            // Debug.assert(nodes);
            throw new System.InvalidOperationException("if nodes is nil, visited should not be nil");
        }
        // clone nodearray if necessary
        var nodeArray = visited;
        if (ReferenceEquals(visited, nodes))
        {
            nodeArray = visited.Clone(v.Factory);
        }

        nodeArray.Loc = new TextRange(GetPos(nodes), GetEnd(nodes));
        return nodeArray;
    }

    // changetrackerwriter.go:173-247 — the forwarding EmitTextWriter surface.
    public void Write(string text)
    {
        textWriter.Write(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteTrailingSemicolon(string text)
    {
        textWriter.WriteTrailingSemicolon(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteComment(string text) => textWriter.WriteComment(text);

    public void WriteKeyword(string text)
    {
        textWriter.WriteKeyword(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteOperator(string text)
    {
        textWriter.WriteOperator(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WritePunctuation(string text)
    {
        textWriter.WritePunctuation(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteSpace(string text)
    {
        textWriter.WriteSpace(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteStringLiteral(string text)
    {
        textWriter.WriteStringLiteral(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteParameter(string text)
    {
        textWriter.WriteParameter(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteProperty(string text)
    {
        textWriter.WriteProperty(text);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteSymbol(string text, Symbol symbol)
    {
        textWriter.WriteSymbol(text, symbol);
        SetLastNonTriviaPosition(text, false);
    }

    public void WriteLine() => textWriter.WriteLine();

    public void WriteLineForce(bool force) => textWriter.WriteLineForce(force);

    public void IncreaseIndent() => textWriter.IncreaseIndent();

    public void DecreaseIndent() => textWriter.DecreaseIndent();

    public void Clear()
    {
        textWriter.Clear();
        lastNonTriviaPosition = 0;
    }

    public string String() => textWriter.String();

    public void RawWrite(string s)
    {
        textWriter.RawWrite(s);
        SetLastNonTriviaPosition(s, false);
    }

    public void WriteLiteral(string s)
    {
        textWriter.WriteLiteral(s);
        SetLastNonTriviaPosition(s, true);
    }

    public int GetTextPos() => textWriter.GetTextPos();

    public int GetLine() => textWriter.GetLine();

    public int GetColumn() => textWriter.GetColumn();

    public int GetIndent() => textWriter.GetIndent();

    public bool IsAtStartOfLine() => textWriter.IsAtStartOfLine();

    public bool HasTrailingComment() => textWriter.HasTrailingComment();

    public bool HasTrailingWhitespace() => textWriter.HasTrailingWhitespace();
}
