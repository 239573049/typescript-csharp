// Ported from tsgo internal/scanner/scanner_test.go (TestScanStringPreservesLoneSurrogates)
// plus additional self-authored tokenization coverage for phase 2 (G2.1 full-corpus
// differential fixtures land once the dumptool exporter is ready — see report).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Diagnostics;
using Xunit;

namespace Ts.Scanner.Tests;

using StringUtil = Ts.StringUtil.StringUtil;

public class ScannerTests
{
    private readonly record struct Tok(SyntaxKind Kind, int Start, int End, string Value);

    private static List<Tok> ScanAll(string text, LanguageVariant variant = LanguageVariant.Standard)
    {
        var s = new Scanner();
        s.SetText(text);
        s.SetLanguageVariant(variant);
        var result = new List<Tok>();
        for (; ; )
        {
            var kind = s.Scan();
            if (kind == SyntaxKind.EndOfFile)
            {
                break;
            }
            result.Add(new Tok(kind, s.TokenStart(), s.TokenEnd(), s.TokenValue()));
        }
        return result;
    }

    private sealed record ScanError(int Code, int Start, int Length);

    private static (Scanner Scanner, List<ScanError> Errors) NewScannerWithErrors(string text)
    {
        var errors = new List<ScanError>();
        var s = new Scanner();
        s.SetOnError((Message message, int start, int length, object?[] args) => errors.Add(new ScanError(message.Code, start, length)));
        s.SetText(text);
        return (s, errors);
    }

    // --- Ported test ---

    [Fact]
    public void ScanStringPreservesLoneSurrogates()
    {
        var s = new Scanner();
        s.SetText("\"\U0001F980\\ud7ff\\ud800\\ud801\\uD83E\\uDD80\"");
        Assert.Equal(SyntaxKind.StringLiteral, s.Scan());
        Assert.Equal(
            "\U0001F980" +
            StringUtil.EncodeJSStringRune(0xD7FF) +
            StringUtil.EncodeJSStringRune(0xD800) +
            StringUtil.EncodeJSStringRune(0xD801) +
            "\U0001F980",
            s.TokenValue());
    }

    // --- Keywords and identifiers ---

    [Fact]
    public void KeywordsAndIdentifiers()
    {
        var toks = ScanAll("const x = 1;");
        Assert.Equal(SyntaxKind.ConstKeyword, toks[0].Kind);
        Assert.Equal(SyntaxKind.Identifier, toks[1].Kind);
        Assert.Equal("x", toks[1].Value);
        Assert.Equal(SyntaxKind.EqualsToken, toks[2].Kind);
        Assert.Equal(SyntaxKind.NumericLiteral, toks[3].Kind);
        Assert.Equal("1", toks[3].Value);
        Assert.Equal(SyntaxKind.SemicolonToken, toks[4].Kind);
        Assert.Equal(5, toks.Count);
    }

    [Theory]
    [InlineData("break", SyntaxKind.BreakKeyword)]
    [InlineData("async", SyntaxKind.AsyncKeyword)]
    [InlineData("type", SyntaxKind.TypeKeyword)]
    [InlineData("of", SyntaxKind.OfKeyword)]
    [InlineData("yield", SyntaxKind.YieldKeyword)]
    [InlineData("constructor", SyntaxKind.ConstructorKeyword)]
    [InlineData("notAKeyword", SyntaxKind.Identifier)]
    [InlineData("Const", SyntaxKind.Identifier)] // case-sensitive: only lowercase are keywords
    [InlineData("$id", SyntaxKind.Identifier)]
    [InlineData("_id", SyntaxKind.Identifier)]
    public void GetIdentifierTokenClassification(string text, SyntaxKind expected)
    {
        var toks = ScanAll(text);
        Assert.Single(toks);
        Assert.Equal(expected, toks[0].Kind);
    }

    [Fact]
    public void IdentifierWithUnicodeEscape()
    {
        // a is 'a'; should decode to identifier "abc"
        var toks = ScanAll("\\u0061bc");
        Assert.Single(toks);
        Assert.Equal(SyntaxKind.Identifier, toks[0].Kind);
        Assert.Equal("abc", toks[0].Value);
    }

    [Fact]
    public void PrivateIdentifier()
    {
        var toks = ScanAll("#field");
        Assert.Single(toks);
        Assert.Equal(SyntaxKind.PrivateIdentifier, toks[0].Kind);
        Assert.Equal("#field", toks[0].Value);
    }

    // --- Numbers ---

    [Theory]
    // PORT-NOTE: tsgo's scanBigIntSuffix normalizes every non-bigint numeric literal via
    // jsnum.FromString(...).String(), so hex/binary/octal token values become decimal.
    [InlineData("0xFF", "255")]
    [InlineData("0Xff", "255")]
    [InlineData("0b1010", "10")]
    [InlineData("0o17", "15")]
    [InlineData("1_000_000", "1000000")]
    [InlineData("3.14", "3.14")]
    [InlineData("1e3", "1000")]
    [InlineData(".5", "0.5")]
    [InlineData("0xDEAD_BEEF", "3735928559")]
    public void NumericLiterals(string text, string expectedValue)
    {
        var toks = ScanAll(text);
        Assert.Single(toks);
        Assert.Equal(SyntaxKind.NumericLiteral, toks[0].Kind);
        Assert.Equal(expectedValue, toks[0].Value);
    }

    [Theory]
    [InlineData("10n", "10n")]
    [InlineData("0xFFn", "0xffn")]
    [InlineData("0b101n", "5n")]
    public void BigIntLiterals(string text, string expectedValue)
    {
        var toks = ScanAll(text);
        Assert.Single(toks);
        Assert.Equal(SyntaxKind.BigIntLiteral, toks[0].Kind);
        Assert.Equal(expectedValue, toks[0].Value);
    }

    [Fact]
    public void NumericSeparatorSetsFlag()
    {
        var s = new Scanner();
        s.SetText("1_0");
        Assert.Equal(SyntaxKind.NumericLiteral, s.Scan());
        Assert.True((s.TokenFlags() & TokenFlags.ContainsSeparator) != 0);
    }

    // --- Strings ---

    [Fact]
    public void StringWithEscapes()
    {
        var toks = ScanAll("\"a\\nb\\t\\u0063\"");
        Assert.Single(toks);
        Assert.Equal(SyntaxKind.StringLiteral, toks[0].Kind);
        Assert.Equal("a\nb\tc", toks[0].Value);
    }

    [Fact]
    public void SingleQuoteStringSetsFlag()
    {
        var s = new Scanner();
        s.SetText("'abc'");
        Assert.Equal(SyntaxKind.StringLiteral, s.Scan());
        Assert.Equal("abc", s.TokenValue());
        Assert.True((s.TokenFlags() & TokenFlags.SingleQuote) != 0);
    }

    [Fact]
    public void UnterminatedStringReportsError()
    {
        var (s, errors) = NewScannerWithErrors("\"abc");
        Assert.Equal(SyntaxKind.StringLiteral, s.Scan());
        Assert.True((s.TokenFlags() & TokenFlags.Unterminated) != 0);
        Assert.Single(errors);
        Assert.Equal(1002, errors[0].Code); // Unterminated string literal
    }

    [Fact]
    public void NoSubstitutionTemplate()
    {
        var s = new Scanner();
        s.SetText("`hello`");
        Assert.Equal(SyntaxKind.NoSubstitutionTemplateLiteral, s.Scan());
        Assert.Equal("hello", s.TokenValue());
    }

    [Fact]
    public void TemplateHeadMiddleTail()
    {
        var s = new Scanner();
        s.SetText("`a${b}c${d}e`");
        Assert.Equal(SyntaxKind.TemplateHead, s.Scan());
        Assert.Equal("a", s.TokenValue());
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("b", s.TokenValue());
        Assert.Equal(SyntaxKind.CloseBraceToken, s.Scan());
        Assert.Equal(SyntaxKind.TemplateMiddle, s.ReScanTemplateToken(false));
        Assert.Equal("c", s.TokenValue());
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("d", s.TokenValue());
        Assert.Equal(SyntaxKind.CloseBraceToken, s.Scan());
        Assert.Equal(SyntaxKind.TemplateTail, s.ReScanTemplateToken(false));
        Assert.Equal("e", s.TokenValue());
    }

    [Fact]
    public void TemplateNormalizesCrlf()
    {
        var s = new Scanner();
        s.SetText("`a\r\nb`");
        Assert.Equal(SyntaxKind.NoSubstitutionTemplateLiteral, s.Scan());
        Assert.Equal("a\nb", s.TokenValue());
    }

    // --- Rescans ---

    [Fact]
    public void ReScanGreaterThanCombinesShiftOperators()
    {
        // PORT-NOTE: Scan() always yields a single '>' (so nested generics like List<List<T>>
        // close correctly); the parser calls ReScanGreaterThanToken to combine when needed.
        var s = new Scanner();
        s.SetText(">>=");
        Assert.Equal(SyntaxKind.GreaterThanToken, s.Scan());
        Assert.Equal(SyntaxKind.GreaterThanGreaterThanEqualsToken, s.ReScanGreaterThanToken());
        Assert.Equal(SyntaxKind.EndOfFile, s.Scan());
    }

    [Fact]
    public void ReScanLessThanSplitsLeftShift()
    {
        var s = new Scanner();
        s.SetText("<<");
        Assert.Equal(SyntaxKind.LessThanLessThanToken, s.Scan());
        Assert.Equal(SyntaxKind.LessThanToken, s.ReScanLessThanToken());
        Assert.Equal(SyntaxKind.LessThanToken, s.Scan());
    }

    [Fact]
    public void ReScanQuestionSplitsNullishCoalescing()
    {
        var s = new Scanner();
        s.SetText("??");
        Assert.Equal(SyntaxKind.QuestionQuestionToken, s.Scan());
        Assert.Equal(SyntaxKind.QuestionToken, s.ReScanQuestionToken());
        Assert.Equal(SyntaxKind.QuestionToken, s.Scan());
    }

    [Fact]
    public void ReScanHashToken()
    {
        var s = new Scanner();
        s.SetText("#x");
        Assert.Equal(SyntaxKind.PrivateIdentifier, s.Scan());
        Assert.Equal(SyntaxKind.HashToken, s.ReScanHashToken());
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("x", s.TokenValue());
    }

    [Fact]
    public void ReScanAsteriskEquals()
    {
        var s = new Scanner();
        s.SetText("*=");
        Assert.Equal(SyntaxKind.AsteriskEqualsToken, s.Scan());
        Assert.Equal(SyntaxKind.EqualsToken, s.ReScanAsteriskEqualsToken());
    }

    [Theory]
    [InlineData("/abc/", "/abc/")]
    [InlineData("/[a-z]/gi", "/[a-z]/gi")]
    [InlineData("/a\\/b/", "/a\\/b/")]
    [InlineData("/[/]/", "/[/]/")]
    public void ReScanSlashProducesRegex(string text, string expected)
    {
        var s = new Scanner();
        s.SetText(text);
        Assert.Equal(SyntaxKind.SlashToken, s.Scan());
        Assert.Equal(SyntaxKind.RegularExpressionLiteral, s.ReScanSlashToken(true));
        Assert.Equal(expected, s.TokenValue());
    }

    [Fact]
    public void UnknownRegexFlagReportsError()
    {
        var (s, errors) = NewScannerWithErrors("/a/z");
        Assert.Equal(SyntaxKind.SlashToken, s.Scan());
        Assert.Equal(SyntaxKind.RegularExpressionLiteral, s.ReScanSlashToken(true));
        Assert.Contains(errors, e => e.Code == 1499); // Unknown regular expression flag
    }

    // --- JSX ---

    [Fact]
    public void ScanJsxText()
    {
        var s = new Scanner();
        s.SetText("hello<");
        s.SetLanguageVariant(LanguageVariant.Jsx);
        Assert.Equal(SyntaxKind.JsxText, s.ScanJsxToken());
        Assert.Equal("hello", s.TokenValue());
        Assert.Equal(0, s.TokenStart());
        Assert.Equal(5, s.TokenEnd());
    }

    [Fact]
    public void ScanJsxTokenPunctuation()
    {
        var s = new Scanner();
        s.SetText("<");
        s.SetLanguageVariant(LanguageVariant.Jsx);
        Assert.Equal(SyntaxKind.LessThanToken, s.ScanJsxToken());

        s.SetText("</");
        Assert.Equal(SyntaxKind.LessThanSlashToken, s.ScanJsxToken());

        s.SetText("{");
        Assert.Equal(SyntaxKind.OpenBraceToken, s.ScanJsxToken());
    }

    [Fact]
    public void JsxIdentifierAllowsDashes()
    {
        var s = new Scanner();
        s.SetText("data-foo");
        s.SetLanguageVariant(LanguageVariant.Jsx);
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal(SyntaxKind.Identifier, s.ScanJsxIdentifier());
        Assert.Equal("data-foo", s.TokenValue());
    }

    // --- Positions with non-ASCII and supplementary code points ---

    [Fact]
    public void SupplementaryIdentifierPositions()
    {
        // U+20000 (CJK Extension B, category Lo) occupies two UTF-16 code units.
        //  a(0) space(1) 𠀀(2,3) space(4) b(5)
        var toks = ScanAll("a \U00020000 b");
        Assert.Equal(3, toks.Count);
        Assert.Equal(new Tok(SyntaxKind.Identifier, 0, 1, "a"), toks[0]);
        Assert.Equal(SyntaxKind.Identifier, toks[1].Kind);
        Assert.Equal(2, toks[1].Start);
        Assert.Equal(4, toks[1].End);
        Assert.Equal("\U00020000", toks[1].Value);
        Assert.Equal(new Tok(SyntaxKind.Identifier, 5, 6, "b"), toks[2]);
    }

    [Fact]
    public void SupplementaryCharInStringPositions()
    {
        // "𠀀" — quote(0) 𠀀(1,2) quote(3) => end position 4 (UTF-16 code units)
        var s = new Scanner();
        s.SetText("\"\U00020000\"");
        Assert.Equal(SyntaxKind.StringLiteral, s.Scan());
        Assert.Equal(0, s.TokenStart());
        Assert.Equal(4, s.TokenEnd());
        Assert.Equal("\U00020000", s.TokenValue());
    }

    [Fact]
    public void BmpNonAsciiIdentifier()
    {
        // 'é' (U+00E9) is a single UTF-16 code unit and a valid identifier char.
        var toks = ScanAll("café");
        Assert.Single(toks);
        Assert.Equal(SyntaxKind.Identifier, toks[0].Kind);
        Assert.Equal("café", toks[0].Value);
        Assert.Equal(0, toks[0].Start);
        Assert.Equal(4, toks[0].End);
    }

    // --- Trivia, comments, directives ---

    [Fact]
    public void SkipsWhitespaceAndComments()
    {
        var toks = ScanAll("  a /* c */ b // trailing\n c");
        Assert.Equal(3, toks.Count);
        Assert.Equal("a", toks[0].Value);
        Assert.Equal("b", toks[1].Value);
        Assert.Equal("c", toks[2].Value);
    }

    [Fact]
    public void PrecedingLineBreakFlag()
    {
        var s = new Scanner();
        s.SetText("a\nb");
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.False(s.HasPrecedingLineBreak());
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.True(s.HasPrecedingLineBreak());
    }

    [Fact]
    public void CommentDirectiveTsIgnore()
    {
        var s = new Scanner();
        s.SetText("// @ts-ignore\nx");
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        var dirs = s.CommentDirectives();
        Assert.NotNull(dirs);
        Assert.Single(dirs);
        Assert.Equal(CommentDirectiveKind.Ignore, dirs[0].Kind);
    }

    [Fact]
    public void CommentDirectiveTsExpectError()
    {
        var s = new Scanner();
        s.SetText("/* @ts-expect-error */ x");
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        var dirs = s.CommentDirectives();
        Assert.NotNull(dirs);
        Assert.Single(dirs);
        Assert.Equal(CommentDirectiveKind.ExpectError, dirs[0].Kind);
    }

    [Fact]
    public void JSDocCommentSetsFlag()
    {
        var s = new Scanner();
        s.SetText("/** doc */ x");
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.True(s.HasPrecedingJSDocComment());
    }

    // --- Conflict markers and shebang ---

    [Fact]
    public void ConflictMarkerTrivia()
    {
        var (s, errors) = NewScannerWithErrors("<<<<<<< HEAD\nx");
        s.SetSkipTrivia(false);
        Assert.Equal(SyntaxKind.ConflictMarkerTrivia, s.Scan());
        Assert.Contains(errors, e => e.Code == 1185); // Merge conflict marker encountered
    }

    [Fact]
    public void ShebangIsSkippedTrivia()
    {
        var toks = ScanAll("#!/usr/bin/env node\nconst x = 1;");
        Assert.Equal(SyntaxKind.ConstKeyword, toks[0].Kind);
        Assert.Equal(SyntaxKind.Identifier, toks[1].Kind);
        Assert.Equal("x", toks[1].Value);
    }

    [Fact]
    public void GetShebangReturnsLine()
    {
        Assert.Equal("#!/usr/bin/env node", Scanner.GetShebang("#!/usr/bin/env node\nrest"));
        Assert.Equal("", Scanner.GetShebang("not a shebang"));
    }

    // --- Mark / Rewind speculative rollback ---

    [Fact]
    public void MarkAndRewind()
    {
        var s = new Scanner();
        s.SetText("a b c");
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("a", s.TokenValue());
        var mark = s.Mark();
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("b", s.TokenValue());
        s.Rewind(mark);
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("b", s.TokenValue());
        Assert.Equal(SyntaxKind.Identifier, s.Scan());
        Assert.Equal("c", s.TokenValue());
    }

    // --- Static helpers ---

    [Theory]
    [InlineData("valid", true)]
    [InlineData("_x$", true)]
    [InlineData("café", true)]
    [InlineData("1abc", false)]
    [InlineData("", false)]
    [InlineData("a-b", false)]
    public void IsValidIdentifier(string text, bool expected)
    {
        Assert.Equal(expected, Scanner.IsValidIdentifier(text));
    }

    [Fact]
    public void TokenToStringRoundTrips()
    {
        Assert.Equal("=>", Scanner.TokenToString(SyntaxKind.EqualsGreaterThanToken));
        Assert.Equal("const", Scanner.TokenToString(SyntaxKind.ConstKeyword));
        Assert.Equal(SyntaxKind.EqualsGreaterThanToken, Scanner.StringToToken("=>"));
        Assert.Equal(SyntaxKind.Unknown, Scanner.StringToToken("not-a-token"));
    }
}
