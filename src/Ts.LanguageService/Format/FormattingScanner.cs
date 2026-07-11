// Ported from tsgo internal/format/scanner.go

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

// Go: type TextRangeWithKind struct { Loc core.TextRange; Kind ast.Kind }
public struct TextRangeWithKind
{
    public TextRange Loc;
    public SyntaxKind Kind;

    // Go: func NewTextRangeWithKind(pos, end, kind) TextRangeWithKind
    public static TextRangeWithKind NewTextRangeWithKind(int pos, int end, SyntaxKind kind)
    {
        return new TextRangeWithKind
        {
            Loc = new TextRange(pos, end),
            Kind = kind,
        };
    }
}

// Go: type tokenInfo struct { leadingTrivia; token; trailingTrivia }
internal sealed class TokenInfo
{
    internal List<TextRangeWithKind> LeadingTrivia = new();
    internal TextRangeWithKind Token;
    internal List<TextRangeWithKind> TrailingTrivia = new();
}

// Go: type scanAction int (iota order).
internal enum ScanAction
{
    Scan = 0,
    RescanGreaterThanToken = 1,
    RescanSlashToken = 2,
    RescanTemplateToken = 3,
    RescanJsxIdentifier = 4,
    RescanJsxText = 5,
    RescanJsxAttributeValue = 6,
}

// Go: type formattingScanner struct { ... }
public sealed partial class FormattingScanner
{
    private readonly global::Ts.Scanner.Scanner s;
    private readonly int startPos;
    private readonly int endPos;
    private int savedPos;
    private bool hasLastTokenInfo;
    private TokenInfo lastTokenInfo = new();
    private ScanAction lastScanAction;
    private List<TextRangeWithKind>? leadingTrivia;
    private List<TextRangeWithKind>? trailingTrivia;
    private bool wasNewLine;

    private FormattingScanner(global::Ts.Scanner.Scanner scan, int startPos, int endPos)
    {
        this.s = scan;
        this.startPos = startPos;
        this.endPos = endPos;
        this.wasNewLine = true;
    }

    // Go: func newFormattingScanner(text, languageVariant, startPos, endPos, worker) []core.TextChange
    public static TextChange[] NewFormattingScanner(
        string text,
        LanguageVariant languageVariant,
        int startPos,
        int endPos,
        FormatSpanWorker worker)
    {
        var scan = new global::Ts.Scanner.Scanner();
        scan.SetSkipTrivia(false);
        scan.SetLanguageVariant(languageVariant);
        scan.SetText(text);
        scan.ResetTokenState(startPos);

        var fmtScn = new FormattingScanner(scan, startPos, endPos);

        TextChange[] res = worker.Execute(fmtScn);

        fmtScn.hasLastTokenInfo = false;
        scan.Reset();

        return res;
    }

    // Go: func (s *formattingScanner) advance()
    internal void Advance()
    {
        hasLastTokenInfo = false;
        bool isStarted = s.TokenFullStart() != startPos;

        if (isStarted)
        {
            wasNewLine = trailingTrivia is not null && trailingTrivia.Count > 0 &&
                         LastOrNil(trailingTrivia).Kind == SyntaxKind.NewLineTrivia;
        }
        else
        {
            s.Scan();
        }

        leadingTrivia = null;
        trailingTrivia = null;

        int pos = s.TokenFullStart();

        // Read leading trivia and token
        while (pos < endPos)
        {
            SyntaxKind t = s.Token();
            if (!NodeTests.IsTriviaKind(t))
            {
                break;
            }

            // consume leading trivia
            s.Scan();
            var item = TextRangeWithKind.NewTextRangeWithKind(pos, s.TokenFullStart(), t);

            pos = s.TokenFullStart();

            leadingTrivia ??= new List<TextRangeWithKind>();
            leadingTrivia.Add(item);
        }

        savedPos = s.TokenFullStart();
    }

    // Go: func shouldRescanGreaterThanToken(node *ast.Node) bool
    private static bool ShouldRescanGreaterThanToken(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.GreaterThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
            case SyntaxKind.GreaterThanGreaterThanToken:
                return true;
        }
        return false;
    }

    // Go: func shouldRescanJsxIdentifier(node *ast.Node) bool
    private static bool ShouldRescanJsxIdentifier(Node node)
    {
        if (node.Parent is not null)
        {
            switch (node.Parent.Kind)
            {
                case SyntaxKind.JsxAttribute:
                case SyntaxKind.JsxOpeningElement:
                case SyntaxKind.JsxClosingElement:
                case SyntaxKind.JsxSelfClosingElement:
                case SyntaxKind.JsxNamespacedName:
                    // May parse an identifier like `module-layout`...
                    return NodeTests.IsKeywordKind(node.Kind) || node.Kind == SyntaxKind.Identifier;
                case SyntaxKind.PropertyAccessExpression:
                    // The leftmost name of a dotted JSX tag name may contain hyphens...
                    return (NodeTests.IsKeywordKind(node.Kind) || node.Kind == SyntaxKind.Identifier) && IsLeftmostJsxTagName(node);
            }
        }
        return false;
    }

    // Go: func isLeftmostJsxTagName(node *ast.Node) bool
    private static bool IsLeftmostJsxTagName(Node node)
    {
        return AstUtilities.FindAncestorOrQuit(node, static n =>
        {
            if (n.Parent is null)
            {
                return FindAncestorResult.Quit;
            }
            if (IsJsxTagName(n))
            {
                return FindAncestorResult.True;
            }
            if (NodeTests.IsPropertyAccessExpression(n.Parent) && n.Parent.Expression() == n)
            {
                return FindAncestorResult.False;
            }
            return FindAncestorResult.Quit;
        }) is not null;
    }

    // Go: func (s *formattingScanner) shouldRescanJsxText(node *ast.Node) bool
    private bool ShouldRescanJsxText(Node node)
    {
        if (NodeTests.IsJsxText(node))
        {
            return true;
        }
        if (!NodeTests.IsJsxElement(node) || hasLastTokenInfo == false)
        {
            return false;
        }

        return lastTokenInfo.Token.Kind == SyntaxKind.JsxText;
    }

    // Go: func shouldRescanSlashToken(container *ast.Node) bool
    private static bool ShouldRescanSlashToken(Node container)
    {
        return container.Kind == SyntaxKind.RegularExpressionLiteral;
    }

    // Go: func shouldRescanTemplateToken(container *ast.Node) bool
    private static bool ShouldRescanTemplateToken(Node container)
    {
        return container.Kind == SyntaxKind.TemplateMiddle ||
               container.Kind == SyntaxKind.TemplateTail;
    }

    // Go: func shouldRescanJsxAttributeValue(node *ast.Node) bool
    private static bool ShouldRescanJsxAttributeValue(Node node)
    {
        return node.Parent is not null && NodeTests.IsJsxAttribute(node.Parent) && node.Parent.Initializer() == node;
    }

    // Go: func startsWithSlashToken(t ast.Kind) bool
    private static bool StartsWithSlashToken(SyntaxKind t)
    {
        return t == SyntaxKind.SlashToken || t == SyntaxKind.SlashEqualsToken;
    }

    // Go: func fixTokenKind(tokenInfo tokenInfo, container *ast.Node) tokenInfo
    private static TokenInfo FixTokenKind(TokenInfo tokenInfo, Node container)
    {
        if (NodeTests.IsTokenKind(container.Kind) && tokenInfo.Token.Kind != container.Kind)
        {
            tokenInfo.Token.Kind = container.Kind;
        }
        return tokenInfo;
    }

    // Go: func (s *formattingScanner) readTokenInfo(n *ast.Node) tokenInfo
    internal TokenInfo ReadTokenInfo(Node n)
    {
        global::Ts.Core.Debug.Assert(IsOnToken());

        // normally scanner returns the smallest available token
        ScanAction expectedScanAction;
        if (ShouldRescanGreaterThanToken(n))
        {
            expectedScanAction = ScanAction.RescanGreaterThanToken;
        }
        else if (ShouldRescanSlashToken(n))
        {
            expectedScanAction = ScanAction.RescanSlashToken;
        }
        else if (ShouldRescanTemplateToken(n))
        {
            expectedScanAction = ScanAction.RescanTemplateToken;
        }
        else if (ShouldRescanJsxIdentifier(n))
        {
            expectedScanAction = ScanAction.RescanJsxIdentifier;
        }
        else if (ShouldRescanJsxText(n))
        {
            expectedScanAction = ScanAction.RescanJsxText;
        }
        else if (ShouldRescanJsxAttributeValue(n))
        {
            expectedScanAction = ScanAction.RescanJsxAttributeValue;
        }
        else
        {
            expectedScanAction = ScanAction.Scan;
        }

        if (hasLastTokenInfo && expectedScanAction == lastScanAction)
        {
            // readTokenInfo was called before with the same expected scan action...
            lastTokenInfo = FixTokenKind(lastTokenInfo, n);
            return lastTokenInfo;
        }

        if (s.TokenFullStart() != savedPos)
        {
            // readTokenInfo was called before but scan action differs - rescan text
            s.ResetTokenState(savedPos);
            s.Scan();
        }

        SyntaxKind currentToken = GetNextToken(n, expectedScanAction);

        var token = TextRangeWithKind.NewTextRangeWithKind(
            s.TokenFullStart(),
            s.TokenEnd(),
            currentToken);

        // consume trailing trivia
        trailingTrivia = null;
        while (s.TokenFullStart() < endPos)
        {
            currentToken = s.Scan();
            if (!NodeTests.IsTriviaKind(currentToken))
            {
                break;
            }
            var trivia = TextRangeWithKind.NewTextRangeWithKind(
                s.TokenFullStart(),
                s.TokenEnd(),
                currentToken);

            trailingTrivia ??= new List<TextRangeWithKind>();
            trailingTrivia.Add(trivia);

            if (currentToken == SyntaxKind.NewLineTrivia)
            {
                // move past new line
                s.Scan();
                break;
            }
        }

        hasLastTokenInfo = true;
        lastTokenInfo = new TokenInfo
        {
            LeadingTrivia = CloneList(leadingTrivia),
            Token = token,
            TrailingTrivia = CloneList(trailingTrivia),
        };
        lastTokenInfo = FixTokenKind(lastTokenInfo, n);

        return lastTokenInfo;
    }

    // Go: func (s *formattingScanner) getNextToken(n *ast.Node, expectedScanAction scanAction) ast.Kind
    private SyntaxKind GetNextToken(Node n, ScanAction expectedScanAction)
    {
        SyntaxKind token = s.Token();
        lastScanAction = ScanAction.Scan;
        switch (expectedScanAction)
        {
            case ScanAction.RescanGreaterThanToken:
                if (token == SyntaxKind.GreaterThanToken)
                {
                    lastScanAction = ScanAction.RescanGreaterThanToken;
                    SyntaxKind newToken = s.ReScanGreaterThanToken();
                    global::Ts.Core.Debug.Assert(n.Kind == newToken);
                    return newToken;
                }
                break;
            case ScanAction.RescanSlashToken:
                if (StartsWithSlashToken(token))
                {
                    lastScanAction = ScanAction.RescanSlashToken;
                    SyntaxKind newToken = s.ReScanSlashToken();
                    global::Ts.Core.Debug.Assert(n.Kind == newToken);
                    return newToken;
                }
                break;
            case ScanAction.RescanTemplateToken:
                if (token == SyntaxKind.CloseBraceToken)
                {
                    lastScanAction = ScanAction.RescanTemplateToken;
                    return s.ReScanTemplateToken(/*isTaggedTemplate*/ false);
                }
                break;
            case ScanAction.RescanJsxIdentifier:
                lastScanAction = ScanAction.RescanJsxIdentifier;
                return s.ScanJsxIdentifier();
            case ScanAction.RescanJsxText:
                lastScanAction = ScanAction.RescanJsxText;
                return s.ReScanJsxToken(/*allowMultilineJsxText*/ false);
            case ScanAction.RescanJsxAttributeValue:
                lastScanAction = ScanAction.RescanJsxAttributeValue;
                return s.ReScanJsxAttributeValue();
            case ScanAction.Scan:
                break;
            default:
                global::Ts.Core.Debug.AssertNever(expectedScanAction, "unhandled scan action kind");
                break;
        }
        return token;
    }

    // Go: func (s *formattingScanner) readEOFTokenRange() TextRangeWithKind
    internal TextRangeWithKind ReadEofTokenRange()
    {
        global::Ts.Core.Debug.Assert(IsOnEof());
        return TextRangeWithKind.NewTextRangeWithKind(
            s.TokenFullStart(),
            s.TokenEnd(),
            SyntaxKind.EndOfFile);
    }

    // Go: func (s *formattingScanner) isOnToken() bool
    internal bool IsOnToken()
    {
        SyntaxKind current = s.Token();
        if (hasLastTokenInfo)
        {
            current = lastTokenInfo.Token.Kind;
        }
        return current != SyntaxKind.EndOfFile && !NodeTests.IsTriviaKind(current);
    }

    // Go: func (s *formattingScanner) isOnEOF() bool
    internal bool IsOnEof()
    {
        SyntaxKind current = s.Token();
        if (hasLastTokenInfo)
        {
            current = lastTokenInfo.Token.Kind;
        }
        return current == SyntaxKind.EndOfFile;
    }

    // Go: func (s *formattingScanner) skipToEndOf(r *core.TextRange)
    internal void SkipToEndOf(TextRange r)
    {
        s.ResetTokenState(r.End);
        savedPos = s.TokenFullStart();
        lastScanAction = ScanAction.Scan;
        hasLastTokenInfo = false;
        wasNewLine = false;
        leadingTrivia = null;
        trailingTrivia = null;
    }

    // Go: func (s *formattingScanner) skipToStartOf(r *core.TextRange)
    internal void SkipToStartOf(TextRange r)
    {
        s.ResetTokenState(r.Pos);
        savedPos = s.TokenFullStart();
        lastScanAction = ScanAction.Scan;
        hasLastTokenInfo = false;
        wasNewLine = false;
        leadingTrivia = null;
        trailingTrivia = null;
    }

    // Go: func (s *formattingScanner) getCurrentLeadingTrivia() []TextRangeWithKind
    internal List<TextRangeWithKind>? GetCurrentLeadingTrivia()
    {
        return leadingTrivia;
    }

    // Go: func (s *formattingScanner) lastTrailingTriviaWasNewLine() bool
    internal bool LastTrailingTriviaWasNewLine()
    {
        return wasNewLine;
    }

    // Go: func (s *formattingScanner) getTokenFullStart() int
    internal int GetTokenFullStart()
    {
        if (hasLastTokenInfo)
        {
            return lastTokenInfo.Token.Loc.Pos;
        }
        return s.TokenFullStart();
    }

    // Go: func (s *formattingScanner) getStartPos() int
    internal int GetStartPos()
    {
        return GetTokenFullStart();
    }

    // PORT-NOTE: ast.IsJsxTagName (tsgo internal/ast/utilities.go:1319) is not yet
    // exported by Ts.Compiler's Ts.Ast. Reproduced locally verbatim here until it
    // lands in Ts.Ast.AstUtilities (flagged in the dispatch report for later export).
    private static bool IsJsxTagName(Node node)
    {
        Node? parent = node.Parent;
        if (parent is null)
        {
            return false;
        }
        switch (parent.Kind)
        {
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxClosingElement:
            case SyntaxKind.JsxSelfClosingElement:
                return parent.TagName() == node;
        }
        return false;
    }

    // Helpers mirroring core.LastOrNil / slices.Clone over List<T>.
    private static TextRangeWithKind LastOrNil(List<TextRangeWithKind> list)
    {
        return list.Count == 0 ? default : list[^1];
    }

    private static List<TextRangeWithKind> CloneList(List<TextRangeWithKind>? list)
    {
        return list is null ? new List<TextRangeWithKind>() : new List<TextRangeWithKind>(list);
    }
}
