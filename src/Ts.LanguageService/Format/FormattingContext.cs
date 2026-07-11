// Ported from tsgo internal/format/context.go

using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

public sealed class FormattingContext
{
    // Go fields (unexported in Go → internal here; the Go methods read them directly).
    internal TextRangeWithKind CurrentTokenSpan;
    internal TextRangeWithKind NextTokenSpan;
    internal Node? ContextNode;
    internal Node? CurrentTokenParent;
    internal Node? NextTokenParent;

    internal Tristate ContextNodeAllOnSameLineCache;
    internal Tristate NextNodeAllOnSameLineCache;
    internal Tristate TokensAreOnSameLineCache;
    internal Tristate ContextNodeBlockIsOnOneLineCache;
    internal Tristate NextNodeBlockIsOnOneLineCache;

    // Go: exported fields.
    public SourceFile SourceFile;
    public FormatRequestKind FormattingRequestKind;
    public FormatCodeSettings Options;

    // Go: func NewFormattingContext(file *ast.SourceFile, kind FormatRequestKind, options lsutil.FormatCodeSettings) *FormattingContext
    public FormattingContext(SourceFile file, FormatRequestKind kind, FormatCodeSettings options)
    {
        SourceFile = file;
        FormattingRequestKind = kind;
        Options = options;
    }

    // Go: func (this *FormattingContext) UpdateContext(cur, curParent, next, nextParent, commonParent)
    public void UpdateContext(TextRangeWithKind cur, Node? curParent, TextRangeWithKind next, Node? nextParent, Node? commonParent)
    {
        if (curParent is null)
        {
            throw new global::Ts.Core.DebugFailureException("nil current range node parent in update context");
        }
        if (nextParent is null)
        {
            throw new global::Ts.Core.DebugFailureException("nil next range node parent in update context");
        }
        if (commonParent is null)
        {
            throw new global::Ts.Core.DebugFailureException("nil common parent node in update context");
        }
        CurrentTokenSpan = cur;
        CurrentTokenParent = curParent;
        NextTokenSpan = next;
        NextTokenParent = nextParent;
        ContextNode = commonParent;

        // drop cached results
        ContextNodeAllOnSameLineCache = Tristate.Unknown;
        NextNodeAllOnSameLineCache = Tristate.Unknown;
        TokensAreOnSameLineCache = Tristate.Unknown;
        ContextNodeBlockIsOnOneLineCache = Tristate.Unknown;
        NextNodeBlockIsOnOneLineCache = Tristate.Unknown;
    }

    // Go: func (this *FormattingContext) rangeIsOnOneLine(node core.TextRange) core.Tristate
    private Tristate RangeIsOnOneLine(TextRange node)
    {
        if (Util.RangeIsOnOneLine(node, SourceFile))
        {
            return Tristate.True;
        }
        return Tristate.False;
    }

    // Go: func (this *FormattingContext) nodeIsOnOneLine(node *ast.Node) core.Tristate
    private Tristate NodeIsOnOneLine(Node node)
    {
        return RangeIsOnOneLine(WithTokenStart(node, SourceFile));
    }

    // Go: func withTokenStart(loc *ast.Node, file *ast.SourceFile) core.TextRange
    internal static TextRange WithTokenStart(Node loc, SourceFile file)
    {
        int startPos = global::Ts.Scanner.Scanner.GetTokenPosOfNode(loc, file, false);
        return new TextRange(startPos, loc.End);
    }

    // Go: func (this *FormattingContext) blockIsOnOneLine(node *ast.Node) core.Tristate
    private Tristate BlockIsOnOneLine(Node node)
    {
        Node? openBrace = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.OpenBraceToken, SourceFile);
        Node? closeBrace = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.CloseBraceToken, SourceFile);
        if (openBrace is not null && closeBrace is not null)
        {
            int closeBraceStart = global::Ts.Scanner.Scanner.GetTokenPosOfNode(closeBrace, SourceFile, false);
            return RangeIsOnOneLine(new TextRange(openBrace.End, closeBraceStart));
        }
        return Tristate.False;
    }

    // Go: func (this *FormattingContext) ContextNodeAllOnSameLine() bool
    public bool ContextNodeAllOnSameLine()
    {
        if (ContextNodeAllOnSameLineCache == Tristate.Unknown)
        {
            ContextNodeAllOnSameLineCache = NodeIsOnOneLine(ContextNode!);
        }
        return ContextNodeAllOnSameLineCache == Tristate.True;
    }

    // Go: func (this *FormattingContext) NextNodeAllOnSameLine() bool
    public bool NextNodeAllOnSameLine()
    {
        if (NextNodeAllOnSameLineCache == Tristate.Unknown)
        {
            NextNodeAllOnSameLineCache = NodeIsOnOneLine(NextTokenParent!);
        }
        return NextNodeAllOnSameLineCache == Tristate.True;
    }

    // Go: func (this *FormattingContext) TokensAreOnSameLine() bool
    public bool TokensAreOnSameLine()
    {
        if (TokensAreOnSameLineCache == Tristate.Unknown)
        {
            TokensAreOnSameLineCache = RangeIsOnOneLine(new TextRange(CurrentTokenSpan.Loc.Pos, NextTokenSpan.Loc.End));
        }
        return TokensAreOnSameLineCache == Tristate.True;
    }

    // Go: func (this *FormattingContext) ContextNodeBlockIsOnOneLine() bool
    public bool ContextNodeBlockIsOnOneLine()
    {
        if (ContextNodeBlockIsOnOneLineCache == Tristate.Unknown)
        {
            ContextNodeBlockIsOnOneLineCache = BlockIsOnOneLine(ContextNode!);
        }
        return ContextNodeBlockIsOnOneLineCache == Tristate.True;
    }

    // Go: func (this *FormattingContext) NextNodeBlockIsOnOneLine() bool
    public bool NextNodeBlockIsOnOneLine()
    {
        if (NextNodeBlockIsOnOneLineCache == Tristate.Unknown)
        {
            NextNodeBlockIsOnOneLineCache = BlockIsOnOneLine(NextTokenParent!);
        }
        return NextNodeBlockIsOnOneLineCache == Tristate.True;
    }
}
