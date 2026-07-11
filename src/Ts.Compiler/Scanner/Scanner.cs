// Ported from tsgo internal/scanner/scanner.go
//
// PORT-NOTE (UTF-16 world-view switch, main plan §一.6 / 05-conventions §8.1):
// The Go scanner works on UTF-8 bytes: it steps with utf8.DecodeRuneInString, tests
// utf8.RuneSelf for the ASCII fast path, and treats positions as byte offsets, relying on
// ast/positionmap.go to map UTF-8<->UTF-16. C# `string` is natively UTF-16, so every
// position here is a UTF-16 code-unit offset (`int`), `s[i]` indexes a `char` directly,
// and PositionMap is neither ported nor referenced. Each "byte advance" in Go is
// re-interpreted as a "code-unit advance"; supplementary code points are handled with
// char.IsHighSurrogate / StringUtil.DecodeRuneAt. See CharAndSize/Char below.
//
// SCOPE (phase 2): the Scanner class, all Re/Scan* methods and accessors, keyword/token
// tables, trivia skipping, conflict-marker and shebang helpers are ported here. Functions
// depending on ast.SourceFile / ast.Node / ast.NodeFactory / core.TextPos line maps
// (GetScannerForSourceFile, GetErrorRangeForNode, all GetECMA*/ComputePositionOf* helpers,
// the comment-range iterators) are deferred to phase 3 with the AST/parser.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ts.Ast;
using Ts.Core;
using Ts.Diagnostics;

namespace Ts.Scanner;

// PORT-NOTE: from within `Ts.Scanner`, the bare names `StringUtil`/`Diagnostics`/`JsNum`
// resolve to the sibling namespaces (Ts.StringUtil etc.), shadowing the same-named classes.
// These namespace-scoped aliases pin them to the host static classes (they must be inside the
// namespace to outrank the enclosing `Ts` namespace's sub-namespace members).
using Diagnostics = Ts.Diagnostics.Diagnostics;
using StringUtil = Ts.StringUtil.StringUtil;
using JsNum = Ts.JsNum.JsNum;

[Flags]
internal enum EscapeSequenceScanningFlags
{
    String = 1 << 0,
    ReportErrors = 1 << 1,
    RegularExpression = 1 << 2,
    AnnexB = 1 << 3,
    AnyUnicodeMode = 1 << 4,
    AtomEscape = 1 << 5,
    ReportInvalidEscapeErrors = RegularExpression | ReportErrors,
    AllowExtendedUnicodeEscape = String | AnyUnicodeMode,
}

// Ported from tsgo internal/scanner/scanner.go:34
public delegate void ErrorCallback(Message diagnostic, int start, int length, params object?[] args);

// Ported from tsgo internal/scanner/scanner.go:192 (ScannerState).
// Go embeds this struct in Scanner; here the fields are inlined on Scanner for direct
// access, and this struct is the value captured/restored by Mark/Rewind.
public struct ScannerState
{
    internal int Pos;
    internal int FullStartPos;
    internal int TokenStart;
    internal SyntaxKind Token;
    internal string TokenValue;
    internal TokenFlags TokenFlags;
    internal List<CommentDirective>? CommentDirectives;
    // PORT-NOTE: a Go slice header carries its length; captured here so Rewind can roll
    // back appends made to the (shared) commentDirectives list during speculative scans.
    internal int CommentDirectivesLen;
    internal int SkipJSDocLeadingAsterisks;
}

public sealed partial class Scanner
{
    // Ported from tsgo internal/scanner/scanner.go:36 (textToKeyword)
    private static readonly Dictionary<string, SyntaxKind> textToKeyword = new()
    {
        ["abstract"] = SyntaxKind.AbstractKeyword,
        ["accessor"] = SyntaxKind.AccessorKeyword,
        ["any"] = SyntaxKind.AnyKeyword,
        ["as"] = SyntaxKind.AsKeyword,
        ["asserts"] = SyntaxKind.AssertsKeyword,
        ["assert"] = SyntaxKind.AssertKeyword,
        ["bigint"] = SyntaxKind.BigIntKeyword,
        ["boolean"] = SyntaxKind.BooleanKeyword,
        ["break"] = SyntaxKind.BreakKeyword,
        ["case"] = SyntaxKind.CaseKeyword,
        ["catch"] = SyntaxKind.CatchKeyword,
        ["class"] = SyntaxKind.ClassKeyword,
        ["continue"] = SyntaxKind.ContinueKeyword,
        ["const"] = SyntaxKind.ConstKeyword,
        ["constructor"] = SyntaxKind.ConstructorKeyword,
        ["debugger"] = SyntaxKind.DebuggerKeyword,
        ["declare"] = SyntaxKind.DeclareKeyword,
        ["default"] = SyntaxKind.DefaultKeyword,
        ["defer"] = SyntaxKind.DeferKeyword,
        ["delete"] = SyntaxKind.DeleteKeyword,
        ["do"] = SyntaxKind.DoKeyword,
        ["else"] = SyntaxKind.ElseKeyword,
        ["enum"] = SyntaxKind.EnumKeyword,
        ["export"] = SyntaxKind.ExportKeyword,
        ["extends"] = SyntaxKind.ExtendsKeyword,
        ["false"] = SyntaxKind.FalseKeyword,
        ["finally"] = SyntaxKind.FinallyKeyword,
        ["for"] = SyntaxKind.ForKeyword,
        ["from"] = SyntaxKind.FromKeyword,
        ["function"] = SyntaxKind.FunctionKeyword,
        ["get"] = SyntaxKind.GetKeyword,
        ["if"] = SyntaxKind.IfKeyword,
        ["immediate"] = SyntaxKind.ImmediateKeyword,
        ["implements"] = SyntaxKind.ImplementsKeyword,
        ["import"] = SyntaxKind.ImportKeyword,
        ["in"] = SyntaxKind.InKeyword,
        ["infer"] = SyntaxKind.InferKeyword,
        ["instanceof"] = SyntaxKind.InstanceOfKeyword,
        ["interface"] = SyntaxKind.InterfaceKeyword,
        ["intrinsic"] = SyntaxKind.IntrinsicKeyword,
        ["is"] = SyntaxKind.IsKeyword,
        ["keyof"] = SyntaxKind.KeyOfKeyword,
        ["let"] = SyntaxKind.LetKeyword,
        ["module"] = SyntaxKind.ModuleKeyword,
        ["namespace"] = SyntaxKind.NamespaceKeyword,
        ["never"] = SyntaxKind.NeverKeyword,
        ["new"] = SyntaxKind.NewKeyword,
        ["null"] = SyntaxKind.NullKeyword,
        ["number"] = SyntaxKind.NumberKeyword,
        ["object"] = SyntaxKind.ObjectKeyword,
        ["package"] = SyntaxKind.PackageKeyword,
        ["private"] = SyntaxKind.PrivateKeyword,
        ["protected"] = SyntaxKind.ProtectedKeyword,
        ["public"] = SyntaxKind.PublicKeyword,
        ["override"] = SyntaxKind.OverrideKeyword,
        ["out"] = SyntaxKind.OutKeyword,
        ["readonly"] = SyntaxKind.ReadonlyKeyword,
        ["require"] = SyntaxKind.RequireKeyword,
        ["global"] = SyntaxKind.GlobalKeyword,
        ["return"] = SyntaxKind.ReturnKeyword,
        ["satisfies"] = SyntaxKind.SatisfiesKeyword,
        ["set"] = SyntaxKind.SetKeyword,
        ["static"] = SyntaxKind.StaticKeyword,
        ["string"] = SyntaxKind.StringKeyword,
        ["super"] = SyntaxKind.SuperKeyword,
        ["switch"] = SyntaxKind.SwitchKeyword,
        ["symbol"] = SyntaxKind.SymbolKeyword,
        ["this"] = SyntaxKind.ThisKeyword,
        ["throw"] = SyntaxKind.ThrowKeyword,
        ["true"] = SyntaxKind.TrueKeyword,
        ["try"] = SyntaxKind.TryKeyword,
        ["type"] = SyntaxKind.TypeKeyword,
        ["typeof"] = SyntaxKind.TypeOfKeyword,
        ["undefined"] = SyntaxKind.UndefinedKeyword,
        ["unique"] = SyntaxKind.UniqueKeyword,
        ["unknown"] = SyntaxKind.UnknownKeyword,
        ["using"] = SyntaxKind.UsingKeyword,
        ["var"] = SyntaxKind.VarKeyword,
        ["void"] = SyntaxKind.VoidKeyword,
        ["while"] = SyntaxKind.WhileKeyword,
        ["with"] = SyntaxKind.WithKeyword,
        ["yield"] = SyntaxKind.YieldKeyword,
        ["async"] = SyntaxKind.AsyncKeyword,
        ["await"] = SyntaxKind.AwaitKeyword,
        ["of"] = SyntaxKind.OfKeyword,
    };

    // Ported from tsgo internal/scanner/scanner.go:124 (textToToken)
    private static readonly Dictionary<string, SyntaxKind> textToToken = BuildTextToToken();

    private static Dictionary<string, SyntaxKind> BuildTextToToken()
    {
        var m = new Dictionary<string, SyntaxKind>
        {
            ["{"] = SyntaxKind.OpenBraceToken,
            ["}"] = SyntaxKind.CloseBraceToken,
            ["("] = SyntaxKind.OpenParenToken,
            [")"] = SyntaxKind.CloseParenToken,
            ["["] = SyntaxKind.OpenBracketToken,
            ["]"] = SyntaxKind.CloseBracketToken,
            ["."] = SyntaxKind.DotToken,
            ["..."] = SyntaxKind.DotDotDotToken,
            [";"] = SyntaxKind.SemicolonToken,
            [","] = SyntaxKind.CommaToken,
            ["<"] = SyntaxKind.LessThanToken,
            [">"] = SyntaxKind.GreaterThanToken,
            ["<="] = SyntaxKind.LessThanEqualsToken,
            [">="] = SyntaxKind.GreaterThanEqualsToken,
            ["=="] = SyntaxKind.EqualsEqualsToken,
            ["!="] = SyntaxKind.ExclamationEqualsToken,
            ["==="] = SyntaxKind.EqualsEqualsEqualsToken,
            ["!=="] = SyntaxKind.ExclamationEqualsEqualsToken,
            ["=>"] = SyntaxKind.EqualsGreaterThanToken,
            ["+"] = SyntaxKind.PlusToken,
            ["-"] = SyntaxKind.MinusToken,
            ["**"] = SyntaxKind.AsteriskAsteriskToken,
            ["*"] = SyntaxKind.AsteriskToken,
            ["/"] = SyntaxKind.SlashToken,
            ["%"] = SyntaxKind.PercentToken,
            ["++"] = SyntaxKind.PlusPlusToken,
            ["--"] = SyntaxKind.MinusMinusToken,
            ["<<"] = SyntaxKind.LessThanLessThanToken,
            ["</"] = SyntaxKind.LessThanSlashToken,
            [">>"] = SyntaxKind.GreaterThanGreaterThanToken,
            [">>>"] = SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
            ["&"] = SyntaxKind.AmpersandToken,
            ["|"] = SyntaxKind.BarToken,
            ["^"] = SyntaxKind.CaretToken,
            ["!"] = SyntaxKind.ExclamationToken,
            ["~"] = SyntaxKind.TildeToken,
            ["&&"] = SyntaxKind.AmpersandAmpersandToken,
            ["||"] = SyntaxKind.BarBarToken,
            ["?"] = SyntaxKind.QuestionToken,
            ["??"] = SyntaxKind.QuestionQuestionToken,
            ["?."] = SyntaxKind.QuestionDotToken,
            [":"] = SyntaxKind.ColonToken,
            ["="] = SyntaxKind.EqualsToken,
            ["+="] = SyntaxKind.PlusEqualsToken,
            ["-="] = SyntaxKind.MinusEqualsToken,
            ["*="] = SyntaxKind.AsteriskEqualsToken,
            ["**="] = SyntaxKind.AsteriskAsteriskEqualsToken,
            ["/="] = SyntaxKind.SlashEqualsToken,
            ["%="] = SyntaxKind.PercentEqualsToken,
            ["<<="] = SyntaxKind.LessThanLessThanEqualsToken,
            [">>="] = SyntaxKind.GreaterThanGreaterThanEqualsToken,
            [">>>="] = SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken,
            ["&="] = SyntaxKind.AmpersandEqualsToken,
            ["|="] = SyntaxKind.BarEqualsToken,
            ["^="] = SyntaxKind.CaretEqualsToken,
            ["||="] = SyntaxKind.BarBarEqualsToken,
            ["&&="] = SyntaxKind.AmpersandAmpersandEqualsToken,
            ["??="] = SyntaxKind.QuestionQuestionEqualsToken,
            ["@"] = SyntaxKind.AtToken,
            ["#"] = SyntaxKind.HashToken,
            ["`"] = SyntaxKind.BacktickToken,
        };
        foreach (var kv in textToKeyword)
        {
            m[kv.Key] = kv.Value;
        }
        return m;
    }

    // Ported from tsgo internal/scanner/scanner.go:2255 (tokenToText)
    private static readonly string[] tokenToText = BuildTokenToText();

    private static string[] BuildTokenToText()
    {
        var result = new string[(int)SyntaxKind.Count];
        // PORT-NOTE: Go's [KindCount]string zero value is "" for every slot; C# arrays
        // default to null, so pre-fill with "" to match TokenToString's empty result.
        Array.Fill(result, "");
        // map-iter: 序无关 —— each kind maps to a single text, no ordering dependency
        foreach (var kv in textToToken)
        {
            result[(int)kv.Value] = kv.Key;
        }
        return result;
    }

    internal string text = "";
    internal int end;
    private LanguageVariant languageVariant;
    private ScriptTarget scriptTarget;
    private ErrorCallback? onError;
    private bool skipTrivia;

    // Inlined ScannerState fields (Go embeds ScannerState in Scanner).
    internal int pos;
    private int fullStartPos;
    internal int tokenStart;
    private SyntaxKind token;
    internal string tokenValue = "";
    private TokenFlags tokenFlags;
    private List<CommentDirective>? commentDirectives;
    private int skipJSDocLeadingAsterisks;

    private Dictionary<string, string>? numberCache;
    private Dictionary<string, string>? hexNumberCache;
    private Dictionary<string, string>? hexDigitCache;

    // Ported from tsgo internal/scanner/scanner.go:217-227 (defaultScanner / NewScanner)
    public Scanner()
    {
        skipTrivia = true;
    }

    // Ported from tsgo internal/scanner/scanner.go:229 (Reset)
    public void Reset()
    {
        numberCache?.Clear();
        hexNumberCache?.Clear();
        hexDigitCache?.Clear();
        var nc = numberCache;
        var hnc = hexNumberCache;
        var hdc = hexDigitCache;
        ResetToDefault();
        numberCache = nc;
        hexNumberCache = hnc;
        hexDigitCache = hdc;
    }

    // mirrors `*s = defaultScanner()`
    private void ResetToDefault()
    {
        text = "";
        end = 0;
        languageVariant = default;
        scriptTarget = default;
        onError = null;
        skipTrivia = true;
        pos = 0;
        fullStartPos = 0;
        tokenStart = 0;
        token = SyntaxKind.Unknown;
        tokenValue = "";
        tokenFlags = Ast.TokenFlags.None;
        commentDirectives = null;
        skipJSDocLeadingAsterisks = 0;
    }

    public string Text()
    {
        return text;
    }

    public SyntaxKind Token()
    {
        return token;
    }

    public TokenFlags TokenFlags()
    {
        return tokenFlags;
    }

    public int TokenFullStart()
    {
        return fullStartPos;
    }

    public int TokenStart()
    {
        return tokenStart;
    }

    public int TokenEnd()
    {
        return pos;
    }

    public string TokenText()
    {
        return text.Substring(tokenStart, pos - tokenStart);
    }

    public string TokenValue()
    {
        return tokenValue;
    }

    public TextRange TokenRange()
    {
        return new TextRange(tokenStart, pos);
    }

    public List<CommentDirective>? CommentDirectives()
    {
        return commentDirectives;
    }

    // Ported from tsgo internal/scanner/scanner.go:284 (Mark)
    public ScannerState Mark()
    {
        return new ScannerState
        {
            Pos = pos,
            FullStartPos = fullStartPos,
            TokenStart = tokenStart,
            Token = token,
            TokenValue = tokenValue,
            TokenFlags = tokenFlags,
            CommentDirectives = commentDirectives,
            CommentDirectivesLen = commentDirectives?.Count ?? 0,
            SkipJSDocLeadingAsterisks = skipJSDocLeadingAsterisks,
        };
    }

    // Ported from tsgo internal/scanner/scanner.go:288 (Rewind)
    public void Rewind(ScannerState state)
    {
        pos = state.Pos;
        fullStartPos = state.FullStartPos;
        tokenStart = state.TokenStart;
        token = state.Token;
        tokenValue = state.TokenValue;
        tokenFlags = state.TokenFlags;
        commentDirectives = state.CommentDirectives;
        // PORT-NOTE: roll back any appends made after the mark (Go slice header value semantics).
        if (commentDirectives is not null && commentDirectives.Count > state.CommentDirectivesLen)
        {
            commentDirectives.RemoveRange(state.CommentDirectivesLen, commentDirectives.Count - state.CommentDirectivesLen);
        }
        skipJSDocLeadingAsterisks = state.SkipJSDocLeadingAsterisks;
    }

    public void ResetPos(int pos)
    {
        if (pos < 0)
        {
            throw new InvalidOperationException("Cannot reset token state to negative position");
        }
        this.pos = pos;
        fullStartPos = pos;
        tokenStart = pos;
    }

    public void ResetTokenState(int pos)
    {
        ResetPos(pos);
        token = SyntaxKind.Unknown;
        tokenValue = "";
        tokenFlags = Ast.TokenFlags.None;
    }

    public void SetSkipJSDocLeadingAsterisks(bool skip)
    {
        if (skip)
        {
            skipJSDocLeadingAsterisks += 1;
        }
        else
        {
            skipJSDocLeadingAsterisks += -1;
        }
    }

    public void SetSkipTrivia(bool skip)
    {
        skipTrivia = skip;
    }

    public bool HasUnicodeEscape()
    {
        return (tokenFlags & Ast.TokenFlags.UnicodeEscape) != 0;
    }

    public bool HasExtendedUnicodeEscape()
    {
        return (tokenFlags & Ast.TokenFlags.ExtendedUnicodeEscape) != 0;
    }

    public bool HasPrecedingLineBreak()
    {
        return (tokenFlags & Ast.TokenFlags.PrecedingLineBreak) != 0;
    }

    public bool HasPrecedingJSDocComment()
    {
        return (tokenFlags & Ast.TokenFlags.PrecedingJSDocComment) != 0;
    }

    public bool HasPrecedingJSDocLeadingAsterisks()
    {
        return (tokenFlags & Ast.TokenFlags.PrecedingJSDocLeadingAsterisks) != 0;
    }

    public bool HasPrecedingJSDocWithDeprecatedTag()
    {
        return (tokenFlags & Ast.TokenFlags.PrecedingJSDocWithDeprecated) != 0;
    }

    public bool HasPrecedingJSDocWithSeeOrLink()
    {
        return (tokenFlags & Ast.TokenFlags.PrecedingJSDocWithSeeOrLink) != 0;
    }

    // Ported from tsgo internal/scanner/scanner.go:350 (scanJSDocCommentForTags)
    private void ScanJSDocCommentForTags(string commentText)
    {
        for (; ; )
        {
            int i = commentText.IndexOf('@');
            if (i < 0)
            {
                return;
            }
            commentText = commentText.Substring(i + 1);
            if ((tokenFlags & Ast.TokenFlags.PrecedingJSDocWithDeprecated) == 0 && HasJSDocTag(commentText, "deprecated"))
            {
                tokenFlags |= Ast.TokenFlags.PrecedingJSDocWithDeprecated;
            }
            if ((tokenFlags & Ast.TokenFlags.PrecedingJSDocWithSeeOrLink) == 0 && HasJSDocTag(commentText, "see", "link", "linkcode", "linkplain"))
            {
                tokenFlags |= Ast.TokenFlags.PrecedingJSDocWithSeeOrLink;
            }
            if ((tokenFlags & (Ast.TokenFlags.PrecedingJSDocWithDeprecated | Ast.TokenFlags.PrecedingJSDocWithSeeOrLink)) ==
                (Ast.TokenFlags.PrecedingJSDocWithDeprecated | Ast.TokenFlags.PrecedingJSDocWithSeeOrLink))
            {
                return;
            }
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:372 (hasJSDocTag)
    private static bool HasJSDocTag(string text, params string[] tags)
    {
        foreach (var tag in tags)
        {
            if (!text.StartsWith(tag, StringComparison.Ordinal))
            {
                continue;
            }
            if (text.Length == tag.Length)
            {
                return true;
            }
            char ch = text[tag.Length];
            if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '}' || ch == '*')
            {
                return true;
            }
        }
        return false;
    }

    public void SetText(string text)
    {
        this.text = text;
        end = text.Length;
        // ScannerState{} reset
        pos = 0;
        fullStartPos = 0;
        tokenStart = 0;
        token = SyntaxKind.Unknown;
        tokenValue = "";
        tokenFlags = Ast.TokenFlags.None;
        commentDirectives = null;
        skipJSDocLeadingAsterisks = 0;
    }

    public void SetOnError(ErrorCallback errorCallback)
    {
        onError = errorCallback;
    }

    public void SetLanguageVariant(LanguageVariant languageVariant)
    {
        this.languageVariant = languageVariant;
    }

    public void SetScriptTarget(ScriptTarget scriptTarget)
    {
        this.scriptTarget = scriptTarget;
    }

    internal ScriptTarget LanguageVersion()
    {
        if (scriptTarget == ScriptTarget.None)
        {
            return ScriptTarget.Latest;
        }
        return scriptTarget;
    }

    private void Error(Message diagnostic)
    {
        ErrorAt(diagnostic, pos, 0);
    }

    internal void ErrorAt(Message diagnostic, int pos, int length, params object?[] args)
    {
        onError?.Invoke(diagnostic, pos, length, args);
    }

    // NOTE: even though this returns a rune, it only decodes the current UTF-16 code unit.
    // It must be checked against 0x80 to verify that a call to CharAndSize is not needed.
    // Ported from tsgo internal/scanner/scanner.go:426 (char)
    internal int Char()
    {
        if (pos < end)
        {
            return text[pos];
        }
        return -1;
    }

    // Ported from tsgo internal/scanner/scanner.go:434 (charAt)
    internal int CharAt(int offset)
    {
        if (pos + offset < end)
        {
            return text[pos + offset];
        }
        return -1;
    }

    // Ported from tsgo internal/scanner/scanner.go:441 (charAndSize)
    internal (int Ch, int Size) CharAndSize()
    {
        // Fast path: a single BMP code unit below 0x80 (the vast majority of source).
        // PORT-NOTE: Go tested utf8.RuneSelf on the byte and stepped byte counts; here the
        // unit is a `char` and Size is in UTF-16 code units (1 or 2 via DecodeRuneAt).
        if (pos < end)
        {
            char b = text[pos];
            if (b < 0x80)
            {
                return (b, 1);
            }
        }
        return StringUtil.DecodeRuneAt(text, pos);
    }

    // Ported from tsgo internal/scanner/scanner.go:456 (scanASCIIWhile)
    private void ScanASCIIWhile(Func<char, bool> pred)
    {
        int i = pos;
        while (i < end)
        {
            char b = text[i];
            if (b >= 0x80 || !pred(b))
            {
                break;
            }
            i++;
        }
        pos = i;
    }

    // Ported from tsgo internal/scanner/scanner.go:469 (Scan)
    public SyntaxKind Scan()
    {
        fullStartPos = pos;
        tokenFlags = Ast.TokenFlags.None;
        for (; ; )
        {
            int ch = Char();
            tokenStart = pos;

            switch (ch)
            {
                case '\t':
                case '\v':
                case '\f':
                case ' ':
                    pos++;
                    if (skipTrivia)
                    {
                        continue;
                    }
                    for (; ; )
                    {
                        var (c2, size) = CharAndSize();
                        if (!StringUtil.IsWhiteSpaceSingleLine(c2))
                        {
                            break;
                        }
                        pos += size;
                    }
                    token = SyntaxKind.WhitespaceTrivia;
                    break;
                case '\n':
                case '\r':
                    tokenFlags |= Ast.TokenFlags.PrecedingLineBreak;
                    if (skipTrivia)
                    {
                        pos++;
                        ScanASCIIWhile(static b => b == ' ' || (b >= '\t' && b <= '\r'));
                        continue;
                    }
                    if (ch == '\r' && CharAt(1) == '\n')
                    {
                        pos += 2;
                    }
                    else
                    {
                        pos++;
                    }
                    token = SyntaxKind.NewLineTrivia;
                    break;
                case '!':
                    if (CharAt(1) == '=')
                    {
                        if (CharAt(2) == '=')
                        {
                            pos += 3;
                            token = SyntaxKind.ExclamationEqualsEqualsToken;
                        }
                        else
                        {
                            pos += 2;
                            token = SyntaxKind.ExclamationEqualsToken;
                        }
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.ExclamationToken;
                    }
                    break;
                case '"':
                case '\'':
                    tokenValue = ScanString(false /*jsxAttributeString*/);
                    token = SyntaxKind.StringLiteral;
                    break;
                case '`':
                    token = ScanTemplateAndSetTokenValue(false /*shouldEmitInvalidEscapeError*/);
                    break;
                case '%':
                    if (CharAt(1) == '=')
                    {
                        pos += 2;
                        token = SyntaxKind.PercentEqualsToken;
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.PercentToken;
                    }
                    break;
                case '&':
                    {
                        int next = CharAt(1);
                        if (next == '&')
                        {
                            if (CharAt(2) == '=')
                            {
                                pos += 3;
                                token = SyntaxKind.AmpersandAmpersandEqualsToken;
                            }
                            else
                            {
                                pos += 2;
                                token = SyntaxKind.AmpersandAmpersandToken;
                            }
                        }
                        else if (next == '=')
                        {
                            pos += 2;
                            token = SyntaxKind.AmpersandEqualsToken;
                        }
                        else
                        {
                            pos++;
                            token = SyntaxKind.AmpersandToken;
                        }
                        break;
                    }
                case '(':
                    pos++;
                    token = SyntaxKind.OpenParenToken;
                    break;
                case ')':
                    pos++;
                    token = SyntaxKind.CloseParenToken;
                    break;
                case '*':
                    {
                        int next = CharAt(1);
                        if (next == '=')
                        {
                            pos += 2;
                            token = SyntaxKind.AsteriskEqualsToken;
                        }
                        else if (next == '*')
                        {
                            if (CharAt(2) == '=')
                            {
                                pos += 3;
                                token = SyntaxKind.AsteriskAsteriskEqualsToken;
                            }
                            else
                            {
                                pos += 2;
                                token = SyntaxKind.AsteriskAsteriskToken;
                            }
                        }
                        else
                        {
                            pos++;
                            if (skipJSDocLeadingAsterisks != 0 &&
                                (tokenFlags & Ast.TokenFlags.PrecedingJSDocLeadingAsterisks) == 0 &&
                                (tokenFlags & Ast.TokenFlags.PrecedingLineBreak) != 0)
                            {
                                tokenFlags |= Ast.TokenFlags.PrecedingJSDocLeadingAsterisks;
                                continue;
                            }
                            token = SyntaxKind.AsteriskToken;
                        }
                        break;
                    }
                case '+':
                    {
                        int next = CharAt(1);
                        if (next == '=')
                        {
                            pos += 2;
                            token = SyntaxKind.PlusEqualsToken;
                        }
                        else if (next == '+')
                        {
                            pos += 2;
                            token = SyntaxKind.PlusPlusToken;
                        }
                        else
                        {
                            pos++;
                            token = SyntaxKind.PlusToken;
                        }
                        break;
                    }
                case ',':
                    pos++;
                    token = SyntaxKind.CommaToken;
                    break;
                case '-':
                    {
                        int next = CharAt(1);
                        if (next == '=')
                        {
                            pos += 2;
                            token = SyntaxKind.MinusEqualsToken;
                        }
                        else if (next == '-')
                        {
                            pos += 2;
                            token = SyntaxKind.MinusMinusToken;
                        }
                        else
                        {
                            pos++;
                            token = SyntaxKind.MinusToken;
                        }
                        break;
                    }
                case '.':
                    {
                        int next = CharAt(1);
                        if (StringUtil.IsDigit(next))
                        {
                            token = ScanNumber();
                        }
                        else if (next == '.' && CharAt(2) == '.')
                        {
                            pos += 3;
                            token = SyntaxKind.DotDotDotToken;
                        }
                        else
                        {
                            pos++;
                            token = SyntaxKind.DotToken;
                        }
                        break;
                    }
                case '/':
                    // Single-line comment
                    if (CharAt(1) == '/')
                    {
                        pos += 2;

                        for (; ; )
                        {
                            ScanASCIIWhile(static b => b != '\n' && b != '\r');
                            var (ch1, size) = CharAndSize();
                            if (size == 0 || StringUtil.IsLineBreak(ch1))
                            {
                                break;
                            }
                            pos += size;
                        }

                        ProcessCommentDirective(tokenStart, pos, false);

                        if (skipTrivia)
                        {
                            continue;
                        }
                        token = SyntaxKind.SingleLineCommentTrivia;
                        return token;
                    }
                    // Multi-line comment
                    if (CharAt(1) == '*')
                    {
                        pos += 2;
                        bool isJSDoc = Char() == '*' && CharAt(1) != '/';

                        bool commentClosed = false;
                        int lastLineStart = tokenStart;
                        for (; ; )
                        {
                            ScanASCIIWhile(static b => b != '*' && b != '\n' && b != '\r');
                            var (ch1, size) = CharAndSize();
                            if (size == 0)
                            {
                                break;
                            }

                            if (ch1 == '*' && CharAt(1) == '/')
                            {
                                pos += 2;
                                commentClosed = true;
                                break;
                            }

                            pos += size;

                            if (StringUtil.IsLineBreak(ch1))
                            {
                                lastLineStart = pos;
                                tokenFlags |= Ast.TokenFlags.PrecedingLineBreak;
                            }
                        }

                        if (isJSDoc)
                        {
                            tokenFlags |= Ast.TokenFlags.PrecedingJSDocComment;
                            ScanJSDocCommentForTags(text.Substring(tokenStart, pos - tokenStart));
                        }

                        ProcessCommentDirective(lastLineStart, pos, true);

                        if (!commentClosed)
                        {
                            Error(Diagnostics.Asterisk_Slash_expected);
                        }

                        if (skipTrivia)
                        {
                            continue;
                        }

                        if (!commentClosed)
                        {
                            tokenFlags |= Ast.TokenFlags.Unterminated;
                        }
                        token = SyntaxKind.MultiLineCommentTrivia;
                        return token;
                    }
                    if (CharAt(1) == '=')
                    {
                        pos += 2;
                        token = SyntaxKind.SlashEqualsToken;
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.SlashToken;
                    }
                    break;
                case '0':
                    if (CharAt(1) == 'X' || CharAt(1) == 'x')
                    {
                        int start = pos;
                        pos += 2;
                        string digits = ScanHexDigits(1, true, true);
                        if (digits.Length == 0)
                        {
                            Error(Diagnostics.Hexadecimal_digit_expected);
                            digits = "0";
                        }
                        hexNumberCache ??= new Dictionary<string, string>();
                        if (hexNumberCache.TryGetValue(digits, out var cachedValue))
                        {
                            tokenValue = cachedValue;
                        }
                        else
                        {
                            string rawText = text.Substring(start, pos - start);
                            if (rawText.StartsWith("0x", StringComparison.Ordinal) && string.CompareOrdinal(rawText.Substring(2), digits) == 0)
                            {
                                tokenValue = rawText;
                            }
                            else
                            {
                                tokenValue = "0x" + digits;
                            }
                            hexNumberCache[digits] = tokenValue;
                        }
                        tokenFlags |= Ast.TokenFlags.HexSpecifier;
                        token = ScanBigIntSuffix();
                        break;
                    }
                    if (CharAt(1) == 'B' || CharAt(1) == 'b')
                    {
                        pos += 2;
                        string digits = ScanBinaryOrOctalDigits(2);
                        if (digits.Length == 0)
                        {
                            Error(Diagnostics.Binary_digit_expected);
                            digits = "0";
                        }
                        tokenValue = "0b" + digits;
                        tokenFlags |= Ast.TokenFlags.BinarySpecifier;
                        token = ScanBigIntSuffix();
                        break;
                    }
                    if (CharAt(1) == 'O' || CharAt(1) == 'o')
                    {
                        pos += 2;
                        string digits = ScanBinaryOrOctalDigits(8);
                        if (digits.Length == 0)
                        {
                            Error(Diagnostics.Octal_digit_expected);
                            digits = "0";
                        }
                        tokenValue = "0o" + digits;
                        tokenFlags |= Ast.TokenFlags.OctalSpecifier;
                        token = ScanBigIntSuffix();
                        break;
                    }
                    // fallthrough to decimal number scanning
                    token = ScanNumber();
                    break;
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    token = ScanNumber();
                    break;
                case ':':
                    pos++;
                    token = SyntaxKind.ColonToken;
                    break;
                case ';':
                    pos++;
                    token = SyntaxKind.SemicolonToken;
                    break;
                case '<':
                    if (CharAt(1) == '<' && IsConflictMarkerTrivia(text, pos))
                    {
                        pos = ScanConflictMarkerTrivia(text, pos, ErrorAt);
                        if (skipTrivia)
                        {
                            continue;
                        }
                        else
                        {
                            token = SyntaxKind.ConflictMarkerTrivia;
                            return token;
                        }
                    }
                    if (CharAt(1) == '<')
                    {
                        if (CharAt(2) == '=')
                        {
                            pos += 3;
                            token = SyntaxKind.LessThanLessThanEqualsToken;
                        }
                        else
                        {
                            pos += 2;
                            token = SyntaxKind.LessThanLessThanToken;
                        }
                    }
                    else if (CharAt(1) == '=')
                    {
                        pos += 2;
                        token = SyntaxKind.LessThanEqualsToken;
                    }
                    else if (languageVariant == LanguageVariant.Jsx && CharAt(1) == '/' && CharAt(2) != '*')
                    {
                        pos += 2;
                        token = SyntaxKind.LessThanSlashToken;
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.LessThanToken;
                    }
                    break;
                case '=':
                    if (CharAt(1) == '=' && IsConflictMarkerTrivia(text, pos))
                    {
                        pos = ScanConflictMarkerTrivia(text, pos, ErrorAt);
                        if (skipTrivia)
                        {
                            continue;
                        }
                        else
                        {
                            token = SyntaxKind.ConflictMarkerTrivia;
                            return token;
                        }
                    }
                    if (CharAt(1) == '=')
                    {
                        if (CharAt(2) == '=')
                        {
                            pos += 3;
                            token = SyntaxKind.EqualsEqualsEqualsToken;
                        }
                        else
                        {
                            pos += 2;
                            token = SyntaxKind.EqualsEqualsToken;
                        }
                    }
                    else if (CharAt(1) == '>')
                    {
                        pos += 2;
                        token = SyntaxKind.EqualsGreaterThanToken;
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.EqualsToken;
                    }
                    break;
                case '>':
                    if (CharAt(1) == '>' && IsConflictMarkerTrivia(text, pos))
                    {
                        pos = ScanConflictMarkerTrivia(text, pos, ErrorAt);
                        if (skipTrivia)
                        {
                            continue;
                        }
                        else
                        {
                            token = SyntaxKind.ConflictMarkerTrivia;
                            return token;
                        }
                    }
                    pos++;
                    token = SyntaxKind.GreaterThanToken;
                    break;
                case '?':
                    if (CharAt(1) == '.' && !StringUtil.IsDigit(CharAt(2)))
                    {
                        pos += 2;
                        token = SyntaxKind.QuestionDotToken;
                    }
                    else if (CharAt(1) == '?')
                    {
                        if (CharAt(2) == '=')
                        {
                            pos += 3;
                            token = SyntaxKind.QuestionQuestionEqualsToken;
                        }
                        else
                        {
                            pos += 2;
                            token = SyntaxKind.QuestionQuestionToken;
                        }
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.QuestionToken;
                    }
                    break;
                case '[':
                    pos++;
                    token = SyntaxKind.OpenBracketToken;
                    break;
                case ']':
                    pos++;
                    token = SyntaxKind.CloseBracketToken;
                    break;
                case '^':
                    if (CharAt(1) == '=')
                    {
                        pos += 2;
                        token = SyntaxKind.CaretEqualsToken;
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.CaretToken;
                    }
                    break;
                case '{':
                    pos++;
                    token = SyntaxKind.OpenBraceToken;
                    break;
                case '|':
                    if (CharAt(1) == '|' && IsConflictMarkerTrivia(text, pos))
                    {
                        pos = ScanConflictMarkerTrivia(text, pos, ErrorAt);
                        if (skipTrivia)
                        {
                            continue;
                        }
                        else
                        {
                            token = SyntaxKind.ConflictMarkerTrivia;
                            return token;
                        }
                    }
                    if (CharAt(1) == '|')
                    {
                        if (CharAt(2) == '=')
                        {
                            pos += 3;
                            token = SyntaxKind.BarBarEqualsToken;
                        }
                        else
                        {
                            pos += 2;
                            token = SyntaxKind.BarBarToken;
                        }
                    }
                    else if (CharAt(1) == '=')
                    {
                        pos += 2;
                        token = SyntaxKind.BarEqualsToken;
                    }
                    else
                    {
                        pos++;
                        token = SyntaxKind.BarToken;
                    }
                    break;
                case '}':
                    pos++;
                    token = SyntaxKind.CloseBraceToken;
                    break;
                case '~':
                    pos++;
                    token = SyntaxKind.TildeToken;
                    break;
                case '@':
                    pos++;
                    token = SyntaxKind.AtToken;
                    break;
                case '\\':
                    {
                        int cp = PeekUnicodeEscape();
                        if (cp >= 0 && IsIdentifierStart(cp))
                        {
                            tokenValue = RuneToString(ScanUnicodeEscape(true)) + ScanIdentifierParts();
                            token = GetIdentifierToken(tokenValue);
                        }
                        else
                        {
                            ScanInvalidCharacter();
                        }
                        break;
                    }
                case '#':
                    if (CharAt(1) == '!')
                    {
                        if (pos == 0)
                        {
                            pos += 2;
                            for (var (c, size) = CharAndSize(); size > 0 && !StringUtil.IsLineBreak(c); (c, size) = CharAndSize())
                            {
                                pos += size;
                            }
                            continue;
                        }
                        ErrorAt(Diagnostics.X_can_only_be_used_at_the_start_of_a_file, pos, 2);
                        pos++;
                        token = SyntaxKind.Unknown;
                        break;
                    }
                    if (CharAt(1) == '\\')
                    {
                        pos++;
                        int cp = PeekUnicodeEscape();
                        if (cp >= 0 && IsIdentifierStart(cp))
                        {
                            tokenValue = "#" + RuneToString(ScanUnicodeEscape(true)) + ScanIdentifierParts();
                            token = SyntaxKind.PrivateIdentifier;
                            break;
                        }
                        pos--;
                    }
                    if (!ScanIdentifier(1))
                    {
                        ErrorAt(Diagnostics.Invalid_character, pos - 1, 1);
                        tokenValue = "#";
                    }
                    token = SyntaxKind.PrivateIdentifier;
                    break;
                default:
                    {
                        if (ch < 0)
                        {
                            token = SyntaxKind.EndOfFile;
                            break;
                        }
                        if (ScanIdentifier(0))
                        {
                            token = GetIdentifierToken(tokenValue);
                            break;
                        }
                        var (ch2, size) = CharAndSize();
                        if (ch2 == StringUtil.RuneError)
                        {
                            ErrorAt(Diagnostics.File_appears_to_be_binary, 0, 0);
                            pos = text.Length;
                            token = SyntaxKind.NonTextFileMarkerTrivia;
                            break;
                        }
                        if (StringUtil.IsWhiteSpaceSingleLine(ch2))
                        {
                            pos += size;

                            // If we get here and it's not 0x0085 (nextLine), then we're handling non-ASCII whitespace.
                            // Handle skipTrivia like we do in the space case above.
                            if (ch2 == 0x0085 || skipTrivia)
                            {
                                continue;
                            }

                            for (; ; )
                            {
                                (ch2, size) = CharAndSize();
                                if (!StringUtil.IsWhiteSpaceSingleLine(ch2))
                                {
                                    break;
                                }
                                pos += size;
                            }
                            token = SyntaxKind.WhitespaceTrivia;
                            return token;
                        }
                        if (StringUtil.IsLineBreak(ch2))
                        {
                            tokenFlags |= Ast.TokenFlags.PrecedingLineBreak;
                            pos += size;
                            continue;
                        }
                        ScanInvalidCharacter();
                        break;
                    }
            }
            return token;
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:972 (processCommentDirective)
    private void ProcessCommentDirective(int start, int end, bool multiline)
    {
        // Skip starting slashes and whitespace
        int pos = start;
        if (multiline)
        {
            // Skip whitespace
            while (pos < end && (text[pos] == ' ' || text[pos] == '\t'))
            {
                pos++;
            }
            // Skip combinations of / and *
            while (pos < end && (text[pos] == '/' || text[pos] == '*'))
            {
                pos++;
            }
        }
        else
        {
            // Skip opening //
            pos += 2;
            // Skip another / if present
            while (pos < end && text[pos] == '/')
            {
                pos++;
            }
        }
        // Skip whitespace
        while (pos < end && (text[pos] == ' ' || text[pos] == '\t'))
        {
            pos++;
        }
        // Directive must start with '@'
        if (!(pos < end && text[pos] == '@'))
        {
            return;
        }
        pos++;
        CommentDirectiveKind kind;
        if (text.AsSpan(pos).StartsWith("ts-expect-error", StringComparison.Ordinal))
        {
            kind = CommentDirectiveKind.ExpectError;
        }
        else if (text.AsSpan(pos).StartsWith("ts-ignore", StringComparison.Ordinal))
        {
            kind = CommentDirectiveKind.Ignore;
        }
        else
        {
            return;
        }
        (commentDirectives ??= new List<CommentDirective>()).Add(new CommentDirective(new TextRange(start, end), kind));
    }

    // Ported from tsgo internal/scanner/scanner.go:1013 (ReScanLessThanToken)
    public SyntaxKind ReScanLessThanToken()
    {
        if (token == SyntaxKind.LessThanLessThanToken)
        {
            pos = tokenStart + 1;
            token = SyntaxKind.LessThanToken;
        }
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1021 (ReScanGreaterThanToken)
    public SyntaxKind ReScanGreaterThanToken()
    {
        if (token == SyntaxKind.GreaterThanToken)
        {
            ReScanGreaterThanTokenInner();
        }
        return token;
    }

    private void ReScanGreaterThanTokenInner()
    {
        pos = tokenStart + 1;
        if (Char() == '>')
        {
            if (CharAt(1) == '>')
            {
                if (CharAt(2) == '=')
                {
                    pos += 3;
                    token = SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken;
                }
                else
                {
                    pos += 2;
                    token = SyntaxKind.GreaterThanGreaterThanGreaterThanToken;
                }
            }
            else if (CharAt(1) == '=')
            {
                pos += 2;
                token = SyntaxKind.GreaterThanGreaterThanEqualsToken;
            }
            else
            {
                pos++;
                token = SyntaxKind.GreaterThanGreaterThanToken;
            }
        }
        else if (Char() == '=')
        {
            pos++;
            token = SyntaxKind.GreaterThanEqualsToken;
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:1052 (ReScanTemplateToken)
    public SyntaxKind ReScanTemplateToken(bool isTaggedTemplate)
    {
        pos = tokenStart;
        token = ScanTemplateAndSetTokenValue(!isTaggedTemplate);
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1058 (ReScanAsteriskEqualsToken)
    public SyntaxKind ReScanAsteriskEqualsToken()
    {
        if (token != SyntaxKind.AsteriskEqualsToken)
        {
            throw new InvalidOperationException("'ReScanAsteriskEqualsToken' should only be called on a '*='");
        }
        pos = tokenStart + 1;
        token = SyntaxKind.EqualsToken;
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1067 (ReScanSlashToken)
    // PORT-NOTE: Go's variadic `reportErrors ...bool` → an optional bool parameter.
    public SyntaxKind ReScanSlashToken(bool reportErrors = false)
    {
        bool shouldReportErrors = reportErrors;
        if (token == SyntaxKind.SlashToken || token == SyntaxKind.SlashEqualsToken)
        {
            // Quickly get to the end of regex such that we know the flags
            int startOfRegExpBody = tokenStart + 1;
            int p = startOfRegExpBody;
            bool inEscape = false;
            bool namedCaptureGroups = false;
            // Although nested character classes are allowed in Unicode Sets mode,
            // an unescaped slash is nevertheless invalid even in a character class in any Unicode mode.
            // This is indicated by Section 12.9.5 Regular Expression Literals of the specification,
            // where nested character classes are not considered at all. (A `[` RegularExpressionClassChar
            // does nothing in a RegularExpressionClass, and a `]` always closes the class.)
            // Additionally, parsing nested character classes will misinterpret regexes like `/[[]/`
            // as unterminated, consuming characters beyond the slash. (This even applies to `/[[]/v`,
            // which should be parsed as a well-terminated regex with an incomplete character class.)
            // Thus we must not handle nested character classes in the first pass.
            bool inCharacterClass = false;
            for (; ; )
            {
                // If we reach the end of a file, or hit a newline, then this is an unterminated
                // regex. Report error and return what we have so far.
                if (p >= end)
                {
                    tokenFlags |= Ast.TokenFlags.Unterminated;
                    goto endScan;
                }
                // PORT-NOTE: Go read one UTF-8 byte here; we read one UTF-16 code unit. This means
                // a raw LS/PS (U+2028/U+2029) is treated as a line break (matching Strada), whereas
                // tsgo's byte-level read would not — a spec ambiguity flagged for the G2.1 diff.
                char ch = text[p];
                if (StringUtil.IsLineBreak(ch))
                {
                    tokenFlags |= Ast.TokenFlags.Unterminated;
                    goto endScan;
                }
                else if (inEscape)
                {
                    // Parsing an escape character; reset the flag and just advance to the next char.
                    inEscape = false;
                }
                else if (ch == '/' && !inCharacterClass)
                {
                    // A slash within a character class is permissible, but in general it signals
                    // the end of the regexp literal.
                    goto endScan;
                }
                else if (ch == '[')
                {
                    inCharacterClass = true;
                }
                else if (ch == '\\')
                {
                    inEscape = true;
                }
                else if (ch == ']')
                {
                    inCharacterClass = false;
                }
                else if (!inCharacterClass && ch == '(' &&
                    p + 1 < end && text[p + 1] == '?' &&
                    p + 2 < end && text[p + 2] == '<' &&
                    (p + 3 >= end || (text[p + 3] != '=' && text[p + 3] != '!')))
                {
                    namedCaptureGroups = true;
                }
                p++;
            }
        endScan:

            int endOfRegExpBody = p;
            if ((tokenFlags & Ast.TokenFlags.Unterminated) != 0)
            {
                // Search for the nearest unbalanced bracket for better recovery. Since the expression is
                // invalid anyways, we take nested square brackets into consideration for the best guess.
                p = startOfRegExpBody;
                inEscape = false;
                int characterClassDepth = 0;
                bool inDecimalQuantifier = false;
                int groupDepth = 0;
                while (p < endOfRegExpBody)
                {
                    char ch = text[p];
                    if (inEscape)
                    {
                        inEscape = false;
                    }
                    else if (ch == '\\')
                    {
                        inEscape = true;
                    }
                    else if (ch == '[')
                    {
                        characterClassDepth++;
                    }
                    else if (ch == ']' && characterClassDepth != 0)
                    {
                        characterClassDepth--;
                    }
                    else if (characterClassDepth == 0)
                    {
                        if (ch == '{')
                        {
                            inDecimalQuantifier = true;
                        }
                        else if (ch == '}' && inDecimalQuantifier)
                        {
                            inDecimalQuantifier = false;
                        }
                        else if (!inDecimalQuantifier)
                        {
                            if (ch == '(')
                            {
                                groupDepth++;
                            }
                            else if (ch == ')' && groupDepth != 0)
                            {
                                groupDepth--;
                            }
                            else if (ch == ')' || ch == ']' || ch == '}')
                            {
                                // We encountered an unbalanced bracket outside a character class. Treat this position as the end of regex.
                                break;
                            }
                        }
                    }
                    p++;
                }
                // Whitespaces and semicolons at the end are not likely to be part of the regex
                while (p > startOfRegExpBody)
                {
                    var (ch, size) = DecodeLastRune(text, p);
                    if (StringUtil.IsWhiteSpaceLike(ch) || ch == ';')
                    {
                        p -= size;
                    }
                    else
                    {
                        break;
                    }
                }
                ErrorAt(Diagnostics.Unterminated_regular_expression_literal, tokenStart, p - tokenStart);
            }
            else
            {
                // Consume the slash character
                p++;
                RegularExpressionFlags regExpFlags = RegularExpressionFlags.None;
                while (p < end)
                {
                    var (ch, size) = StringUtil.DecodeRuneAt(text, p);
                    if (ch == StringUtil.RuneError || !IsIdentifierPart(ch))
                    {
                        break;
                    }
                    if (shouldReportErrors)
                    {
                        if (!RegExpParser.CharCodeToRegExpFlag.TryGetValue(ch, out var flag))
                        {
                            ErrorAt(Diagnostics.Unknown_regular_expression_flag, p, size);
                        }
                        else if ((regExpFlags & flag) != 0)
                        {
                            ErrorAt(Diagnostics.Duplicate_regular_expression_flag, p, size);
                        }
                        else if (((regExpFlags | flag) & RegularExpressionFlags.AnyUnicodeMode) == RegularExpressionFlags.AnyUnicodeMode)
                        {
                            ErrorAt(Diagnostics.The_Unicode_u_flag_and_the_Unicode_Sets_v_flag_cannot_be_set_simultaneously, p, size);
                        }
                        else
                        {
                            regExpFlags |= flag;
                            CheckRegularExpressionFlagAvailability(flag, p, size);
                        }
                    }
                    p += size;
                }
                if (shouldReportErrors)
                {
                    pos = startOfRegExpBody;
                    int saveEnd = end;
                    int saveTokenPos = tokenStart;
                    var saveTokenFlags = tokenFlags;
                    end = endOfRegExpBody;
                    var parser = new RegExpParser(this)
                    {
                        End = endOfRegExpBody,
                        RegExpFlags = regExpFlags,
                        AnyUnicodeMode = (regExpFlags & RegularExpressionFlags.AnyUnicodeMode) != 0,
                        UnicodeSetsMode = (regExpFlags & RegularExpressionFlags.UnicodeSets) != 0,
                        AnnexB = true,
                        NamedCaptureGroups = namedCaptureGroups,
                    };
                    parser.Run();
                    end = saveEnd;
                    pos = p;
                    tokenStart = saveTokenPos;
                    tokenFlags = saveTokenFlags;
                }
                else
                {
                    pos = p;
                }
            }

            pos = p;
            tokenValue = text.Substring(tokenStart, pos - tokenStart);
            token = SyntaxKind.RegularExpressionLiteral;
        }
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1225 (ReScanJsxToken)
    public SyntaxKind ReScanJsxToken(bool allowMultilineJsxText)
    {
        pos = fullStartPos;
        tokenStart = fullStartPos;
        token = ScanJsxTokenEx(allowMultilineJsxText);
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1232 (ReScanHashToken)
    public SyntaxKind ReScanHashToken()
    {
        if (token == SyntaxKind.PrivateIdentifier)
        {
            pos = tokenStart + 1;
            token = SyntaxKind.HashToken;
        }
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1240 (ReScanQuestionToken)
    public SyntaxKind ReScanQuestionToken()
    {
        if (token != SyntaxKind.QuestionQuestionToken)
        {
            throw new InvalidOperationException("'reScanQuestionToken' should only be called on a '??'");
        }
        pos = tokenStart + 1;
        token = SyntaxKind.QuestionToken;
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1249 (ScanJsxToken)
    public SyntaxKind ScanJsxToken()
    {
        return ScanJsxTokenEx(true /*allowMultilineJsxText*/);
    }

    // Ported from tsgo internal/scanner/scanner.go:1253 (ScanJsxTokenEx)
    public SyntaxKind ScanJsxTokenEx(bool allowMultilineJsxText)
    {
        fullStartPos = pos;
        tokenStart = pos;
        int ch = Char();
        if (ch < 0)
        {
            token = SyntaxKind.EndOfFile;
        }
        else if (ch == '<')
        {
            if (CharAt(1) == '/')
            {
                pos += 2;
                token = SyntaxKind.LessThanSlashToken;
            }
            else
            {
                pos++;
                token = SyntaxKind.LessThanToken;
            }
        }
        else if (ch == '{')
        {
            pos++;
            token = SyntaxKind.OpenBraceToken;
        }
        else
        {
            // First non-whitespace character on this line.
            int firstNonWhitespace = 0;
            // These initial values are special because the first line is:
            // firstNonWhitespace = 0 to indicate that we want leading whitespace
            for (; ; )
            {
                var (c, size) = CharAndSize();
                if (size == 0 || c == '{')
                {
                    break;
                }
                if (c == '<')
                {
                    if (IsConflictMarkerTrivia(text, pos))
                    {
                        pos = ScanConflictMarkerTrivia(text, pos, ErrorAt);
                        token = SyntaxKind.ConflictMarkerTrivia;
                        return token;
                    }
                    break;
                }
                if (c == '>')
                {
                    ErrorAt(Diagnostics.Unexpected_token_Did_you_mean_or_gt, pos, 1);
                }
                else if (c == '}')
                {
                    ErrorAt(Diagnostics.Unexpected_token_Did_you_mean_or_rbrace, pos, 1);
                }
                // FirstNonWhitespace is 0, then we only see whitespaces so far. If we see a linebreak, we want to ignore that whitespaces.
                // i.e (- : whitespace)
                //      <div>----
                //      </div> becomes <div></div>
                //
                //      <div>----</div> becomes <div>----</div>
                if (StringUtil.IsLineBreak(c) && firstNonWhitespace == 0)
                {
                    firstNonWhitespace = -1;
                }
                else if (!allowMultilineJsxText && StringUtil.IsLineBreak(c) && firstNonWhitespace > 0)
                {
                    // Stop JsxText on each line during formatting. This allows the formatter to
                    // indent each line correctly.
                    break;
                }
                else if (!StringUtil.IsWhiteSpaceLike(c))
                {
                    firstNonWhitespace = pos;
                }
                pos += size;
            }
            tokenValue = text.Substring(fullStartPos, pos - fullStartPos);
            token = SyntaxKind.JsxText;
            if (firstNonWhitespace == -1)
            {
                token = SyntaxKind.JsxTextAllWhiteSpaces;
            }
        }
        return token;
    }

    // Scans a JSX identifier; these differ from normal identifiers in that they allow dashes
    // Ported from tsgo internal/scanner/scanner.go:1321 (ScanJsxIdentifier)
    public SyntaxKind ScanJsxIdentifier()
    {
        if (TokenIsIdentifierOrKeyword(token))
        {
            // An identifier or keyword has already been parsed - check for a `-` or a single instance of `:` and then append it and
            // everything after it to the token
            // Do note that this means that `scanJsxIdentifier` effectively _mutates_ the visible token without advancing to a new token
            // Any caller should be expecting this behavior and should only read the pos or token value after calling it.
            for (; ; )
            {
                int ch = Char();
                if (ch < 0)
                {
                    break;
                }
                if (ch == '-')
                {
                    tokenValue += "-";
                    pos++;
                    continue;
                }
                int oldPos = pos;
                tokenValue += ScanIdentifierParts(); // reuse `scanIdentifierParts` so unicode escapes are handled
                if (pos == oldPos)
                {
                    break;
                }
            }
            token = GetIdentifierToken(tokenValue);
        }
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1348 (ScanJsxAttributeValue)
    public SyntaxKind ScanJsxAttributeValue()
    {
        fullStartPos = pos;
        // Skip whitespace between '=' and the value so tokenStart lands on the
        // opening quote, not on trivia.
        for (var (ch, size) = CharAndSize(); size > 0 && StringUtil.IsWhiteSpaceLike(ch); (ch, size) = CharAndSize())
        {
            pos += size;
        }
        tokenStart = pos;
        switch (Char())
        {
            case '"':
            case '\'':
                tokenValue = ScanString(true /*jsxAttributeString*/);
                token = SyntaxKind.StringLiteral;
                return token;
            default:
                // If this scans anything other than `{`, it's a parse error.
                return Scan();
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:1367 (ReScanJsxAttributeValue)
    public SyntaxKind ReScanJsxAttributeValue()
    {
        pos = fullStartPos;
        tokenStart = fullStartPos;
        return ScanJsxAttributeValue();
    }

    /// In addition to the usual JSDoc SyntaxKinds, can also return SyntaxKind.JSDocCommentTextToken
    // Ported from tsgo internal/scanner/scanner.go:1374 (ScanJSDocCommentTextToken)
    public SyntaxKind ScanJSDocCommentTextToken(bool inBackticks)
    {
        fullStartPos = pos;
        tokenFlags = Ast.TokenFlags.None;
        if (pos >= text.Length)
        {
            token = SyntaxKind.EndOfFile;
            return token;
        }
        tokenStart = pos;
        for (var (ch, size) = CharAndSize(); pos < text.Length && !StringUtil.IsLineBreak(ch) && ch != '`'; (ch, size) = CharAndSize())
        {
            if (!inBackticks)
            {
                if (ch == '{')
                {
                    break;
                }
                else if (ch == '@' && pos >= 0)
                {
                    // @ doesn't start a new tag inside ``, and elsewhere, only after whitespace and before identifier
                    var (previous, _) = DecodeLastRune(text, pos);
                    if (StringUtil.IsWhiteSpaceSingleLine(previous))
                    {
                        var (next, _) = StringUtil.DecodeRuneAt(text, pos + size);
                        if (IsIdentifierStart(next))
                        {
                            break;
                        }
                    }
                }
            }
            pos += size;
        }
        if (pos == tokenStart)
        {
            return ScanJSDocToken();
        }
        tokenValue = text.Substring(tokenStart, pos - tokenStart);
        token = SyntaxKind.JSDocCommentTextToken;
        return token;
    }

    // Peek at the character at the current scanner position (expected to be right after '@')
    // and return true if a JSDoc tag can follow. Identifier starts indicate a tag name.
    // Whitespace, newlines, and EOF are also accepted to support incomplete tags for code completion.
    // Ported from tsgo internal/scanner/scanner.go:1410 (CanFollowJSDocAt)
    public bool CanFollowJSDocAt()
    {
        if (pos >= text.Length)
        {
            return true;
        }
        var (ch, _) = StringUtil.DecodeRuneAt(text, pos);
        return IsIdentifierStart(ch) || StringUtil.IsWhiteSpaceSingleLine(ch) || StringUtil.IsLineBreak(ch);
    }

    // Ported from tsgo internal/scanner/scanner.go:1418 (ScanJSDocToken)
    public SyntaxKind ScanJSDocToken()
    {
        fullStartPos = pos;
        tokenFlags = Ast.TokenFlags.None;
        if (pos >= text.Length)
        {
            token = SyntaxKind.EndOfFile;
            return token;
        }

        tokenStart = pos;
        var (ch, size) = CharAndSize();
        pos += size;
        switch (ch)
        {
            case '\t':
            case '\v':
            case '\f':
            case ' ':
                for (var (ch2, size2) = CharAndSize(); size2 > 0 && StringUtil.IsWhiteSpaceSingleLine(ch2); (ch2, size2) = CharAndSize())
                {
                    pos += size2;
                }
                token = SyntaxKind.WhitespaceTrivia;
                return token;
            case '@':
                token = SyntaxKind.AtToken;
                return token;
            case '\r':
                if (Char() == '\n')
                {
                    pos++;
                }
                goto case '\n';
            case '\n':
                tokenFlags |= Ast.TokenFlags.PrecedingLineBreak;
                token = SyntaxKind.NewLineTrivia;
                return token;
            case '*':
                token = SyntaxKind.AsteriskToken;
                return token;
            case '{':
                token = SyntaxKind.OpenBraceToken;
                return token;
            case '}':
                token = SyntaxKind.CloseBraceToken;
                return token;
            case '[':
                token = SyntaxKind.OpenBracketToken;
                return token;
            case ']':
                token = SyntaxKind.CloseBracketToken;
                return token;
            case '(':
                token = SyntaxKind.OpenParenToken;
                return token;
            case ')':
                token = SyntaxKind.CloseParenToken;
                return token;
            case '<':
                token = SyntaxKind.LessThanToken;
                return token;
            case '>':
                token = SyntaxKind.GreaterThanToken;
                return token;
            case '=':
                token = SyntaxKind.EqualsToken;
                return token;
            case ',':
                token = SyntaxKind.CommaToken;
                return token;
            case '.':
                token = SyntaxKind.DotToken;
                return token;
            case '`':
                token = SyntaxKind.BacktickToken;
                return token;
            case '#':
                token = SyntaxKind.HashToken;
                return token;
            case '\\':
                pos--;
                int cp = PeekUnicodeEscape();
                if (cp >= 0 && IsIdentifierStart(cp))
                {
                    tokenValue = RuneToString(ScanUnicodeEscape(true)) + ScanIdentifierParts();
                    token = GetIdentifierToken(tokenValue);
                }
                else
                {
                    pos++;
                    token = SyntaxKind.Unknown;
                }
                return token;
        }

        if (IsIdentifierStart(ch))
        {
            int c = ch;
            for (; ; )
            {
                if (pos >= text.Length)
                {
                    break;
                }
                (c, size) = CharAndSize();
                if (!IsIdentifierPart(c) && c != '-')
                {
                    break;
                }
                pos += size;
            }
            tokenValue = text.Substring(tokenStart, pos - tokenStart);
            if (c == '\\')
            {
                tokenValue += ScanIdentifierParts();
            }
            token = GetIdentifierToken(tokenValue);
            return token;
        }
        else
        {
            token = SyntaxKind.Unknown;
            return token;
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:1527 (scanIdentifier)
    internal bool ScanIdentifier(int prefixLength)
    {
        int start = pos;
        pos += prefixLength;
        int ch = Char();
        // Fast path for simple ASCII identifiers
        if (StringUtil.IsASCIILetter(ch) || ch == '_' || ch == '$')
        {
            pos++;
            ScanASCIIWhile(static b => (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '_' || b == '$');
            ch = Char();
            if (ch < 0x80 && ch != '\\')
            {
                tokenValue = text.Substring(start, pos - start);
                return true;
            }
            pos = start + prefixLength;
        }
        var (ch2, size) = CharAndSize();
        if (IsIdentifierStart(ch2))
        {
            for (; ; )
            {
                pos += size;
                (ch2, size) = CharAndSize();
                if (!IsIdentifierPart(ch2))
                {
                    break;
                }
            }
            tokenValue = text.Substring(start, pos - start);
            if (ch2 == '\\')
            {
                tokenValue += ScanIdentifierParts();
            }
            return true;
        }
        return false;
    }

    // Ported from tsgo internal/scanner/scanner.go:1562 (scanIdentifierParts)
    internal string ScanIdentifierParts()
    {
        var sb = new StringBuilder();
        int start = pos;
        for (; ; )
        {
            var (ch, size) = CharAndSize();
            if (IsIdentifierPart(ch))
            {
                pos += size;
                continue;
            }
            if (ch == '\\')
            {
                int escaped = PeekUnicodeEscape();
                if (escaped >= 0 && IsIdentifierPart(escaped))
                {
                    sb.Append(text, start, pos - start);
                    sb.Append(RuneToString(ScanUnicodeEscape(true)));
                    start = pos;
                    continue;
                }
            }
            break;
        }
        sb.Append(text, start, pos - start);
        return sb.ToString();
    }

    // Ported from tsgo internal/scanner/scanner.go:1586 (scanString)
    private string ScanString(bool jsxAttributeString)
    {
        int quote = Char();
        if (quote == '\'')
        {
            tokenFlags |= Ast.TokenFlags.SingleQuote;
        }
        pos++;
        // Fast path for simple strings without escape sequences.
        // PORT-NOTE: Go strings.IndexByte over the UTF-8 tail → IndexOf of the (ASCII) quote
        // char in the UTF-16 tail; both yield the same relative offset.
        int qi = text.IndexOf((char)quote, pos);
        int strLen = qi < 0 ? -1 : qi - pos;
        if (strLen == 0)
        {
            pos++;
            return "";
        }
        if (strLen > 0)
        {
            string str = text.Substring(pos, strLen);
            if (jsxAttributeString ||
                (str.IndexOf('\\') < 0 && str.IndexOf('\r') < 0 && str.IndexOf('\n') < 0))
            {
                pos += strLen + 1;
                return str;
            }
        }
        var sb = new StringBuilder();
        int start = pos;
        for (; ; )
        {
            int ch = Char();
            if (ch < 0)
            {
                sb.Append(text, start, pos - start);
                tokenFlags |= Ast.TokenFlags.Unterminated;
                Error(Diagnostics.Unterminated_string_literal);
                break;
            }
            if (ch == quote)
            {
                sb.Append(text, start, pos - start);
                pos++;
                break;
            }
            if (ch == '\\' && !jsxAttributeString)
            {
                sb.Append(text, start, pos - start);
                sb.Append(ScanEscapeSequence(EscapeSequenceScanningFlags.String | EscapeSequenceScanningFlags.ReportErrors));
                start = pos;
                continue;
            }
            if ((ch == '\n' || ch == '\r') && !jsxAttributeString)
            {
                sb.Append(text, start, pos - start);
                tokenFlags |= Ast.TokenFlags.Unterminated;
                Error(Diagnostics.Unterminated_string_literal);
                break;
            }
            pos++;
        }
        return sb.ToString();
    }

    // Ported from tsgo internal/scanner/scanner.go:1638 (scanTemplateAndSetTokenValue)
    private SyntaxKind ScanTemplateAndSetTokenValue(bool shouldEmitInvalidEscapeError)
    {
        bool startedWithBacktick = Char() == '`';
        pos++;
        int start = pos;
        var parts = new List<string>(4);
        SyntaxKind token;
        for (; ; )
        {
            ScanASCIIWhile(static b => b != '`' && b != '$' && b != '\\' && b != '\r');
            int ch = Char();
            if (ch < 0 || ch == '`')
            {
                parts.Add(text.Substring(start, pos - start));
                if (ch == '`')
                {
                    pos++;
                }
                else
                {
                    tokenFlags |= Ast.TokenFlags.Unterminated;
                    Error(Diagnostics.Unterminated_template_literal);
                }
                token = CoreUtil.IfElse(startedWithBacktick, SyntaxKind.NoSubstitutionTemplateLiteral, SyntaxKind.TemplateTail);
                break;
            }
            if (ch == '$' && CharAt(1) == '{')
            {
                parts.Add(text.Substring(start, pos - start));
                pos += 2;
                token = CoreUtil.IfElse(startedWithBacktick, SyntaxKind.TemplateHead, SyntaxKind.TemplateMiddle);
                break;
            }
            if (ch == '\\')
            {
                parts.Add(text.Substring(start, pos - start));
                parts.Add(ScanEscapeSequence(EscapeSequenceScanningFlags.String | CoreUtil.IfElse(shouldEmitInvalidEscapeError, EscapeSequenceScanningFlags.ReportErrors, default)));
                start = pos;
                continue;
            }
            // Speculated ECMAScript 6 Spec 11.8.6.1:
            // <CR><LF> and <CR> LineTerminatorSequences are normalized to <LF> for Template Values
            if (ch == '\r')
            {
                parts.Add(text.Substring(start, pos - start));
                pos++;
                if (Char() == '\n')
                {
                    pos++;
                }
                parts.Add("\n");
                start = pos;
                continue;
            }
            pos++;
        }
        tokenValue = string.Concat(parts);
        return token;
    }

    // Ported from tsgo internal/scanner/scanner.go:1690 (scanEscapeSequence)
    internal string ScanEscapeSequence(EscapeSequenceScanningFlags flags)
    {
        int start = pos;
        pos++;
        int ch = Char();
        if (ch < 0)
        {
            Error(Diagnostics.Unexpected_end_of_text);
            return "";
        }
        pos++;
        switch (ch)
        {
            case '0':
                // Although '0' preceding any digit is treated as LegacyOctalEscapeSequence,
                // '\08' should separately be interpreted as '\0' + '8'.
                if (!StringUtil.IsDigit(Char()))
                {
                    return "\x00";
                }
                // '\01', '\011'
                goto case '1';
            case '1':
            case '2':
            case '3':
                // '\1', '\17', '\177'
                if (StringUtil.IsOctalDigit(Char()))
                {
                    pos++;
                }
                // '\17', '\177'
                goto case '4';
            case '4':
            case '5':
            case '6':
            case '7':
                // '\4', '\47' but not '\477'
                if (StringUtil.IsOctalDigit(Char()))
                {
                    pos++;
                }
                // '\47'
                tokenFlags |= Ast.TokenFlags.ContainsInvalidEscape;
                if ((flags & EscapeSequenceScanningFlags.ReportInvalidEscapeErrors) != 0)
                {
                    long code = ParseInt(text.Substring(start + 1, pos - start - 1), 8, 32);
                    if ((flags & EscapeSequenceScanningFlags.RegularExpression) != 0 && (flags & EscapeSequenceScanningFlags.AtomEscape) == 0 && ch != '0')
                    {
                        ErrorAt(Diagnostics.Octal_escape_sequences_and_backreferences_are_not_allowed_in_a_character_class_If_this_was_intended_as_an_escape_sequence_use_the_syntax_0_instead, start, pos - start, $"\\x{code:x2}");
                    }
                    else
                    {
                        ErrorAt(Diagnostics.Octal_escape_sequences_are_not_allowed_Use_the_syntax_0, start, pos - start, $"\\x{code:x2}");
                    }
                    return RuneToString((int)code);
                }
                return text.Substring(start, pos - start);
            case '8':
            case '9':
                // the invalid '\8' and '\9'
                tokenFlags |= Ast.TokenFlags.ContainsInvalidEscape;
                if ((flags & EscapeSequenceScanningFlags.ReportInvalidEscapeErrors) != 0)
                {
                    if ((flags & EscapeSequenceScanningFlags.RegularExpression) != 0 && (flags & EscapeSequenceScanningFlags.AtomEscape) == 0)
                    {
                        ErrorAt(Diagnostics.Decimal_escape_sequences_and_backreferences_are_not_allowed_in_a_character_class, start, pos - start);
                    }
                    else
                    {
                        ErrorAt(Diagnostics.Escape_sequence_0_is_not_allowed, start, pos - start, text.Substring(start, pos - start));
                    }
                    return RuneToString(ch);
                }
                return text.Substring(start, pos - start);
            case 'b':
                return "\b";
            case 't':
                return "\t";
            case 'n':
                return "\n";
            case 'v':
                return "\v";
            case 'f':
                return "\f";
            case 'r':
                return "\r";
            case '\'':
                return "'";
            case '"':
                return "\"";
            case 'u':
                {
                    // '\uDDDD' and '\u{DDDDDD}'
                    bool extended = Char() == '{';
                    pos -= 2;
                    int codePoint = ScanUnicodeEscape((flags & EscapeSequenceScanningFlags.ReportInvalidEscapeErrors) != 0);
                    if (extended)
                    {
                        if ((flags & EscapeSequenceScanningFlags.AllowExtendedUnicodeEscape) == 0)
                        {
                            tokenFlags |= Ast.TokenFlags.ContainsInvalidEscape;
                            if ((flags & EscapeSequenceScanningFlags.ReportInvalidEscapeErrors) != 0)
                            {
                                ErrorAt(Diagnostics.Unicode_escape_sequences_are_only_available_when_the_Unicode_u_flag_or_the_Unicode_Sets_v_flag_is_set, start, pos - start);
                            }
                        }
                        if (codePoint < 0)
                        {
                            return text.Substring(start, pos - start);
                        }
                        // In string literals, a high surrogate \u{...} followed by a low
                        // surrogate escape forms a single code point, exactly as adjacent
                        // UTF-16 code units would in a JavaScript string.
                        if ((flags & EscapeSequenceScanningFlags.RegularExpression) == 0 && StringUtil.IsHighSurrogate(codePoint))
                        {
                            var (combined, ok) = ScanLowSurrogateEscape(codePoint);
                            if (ok)
                            {
                                return RuneToString(combined);
                            }
                        }
                        return StringUtil.EncodeJSStringRune(codePoint);
                    }
                    if (codePoint < 0)
                    {
                        return text.Substring(start, pos - start);
                    }
                    else if (StringUtil.IsHighSurrogate(codePoint))
                    {
                        if ((flags & EscapeSequenceScanningFlags.RegularExpression) == 0)
                        {
                            // Combine \uHigh followed by any low surrogate escape (\uLow or
                            // \u{Low}) into a single code point in string literals, matching
                            // how adjacent UTF-16 code units pair in a JavaScript string.
                            var (combined, ok) = ScanLowSurrogateEscape(codePoint);
                            if (ok)
                            {
                                return RuneToString(combined);
                            }
                        }
                        else if ((flags & EscapeSequenceScanningFlags.AnyUnicodeMode) != 0 &&
                            Char() == '\\' && CharAt(1) == 'u' && CharAt(2) != '{')
                        {
                            // In regex AnyUnicodeMode, combine \uHigh\uLow so scanClassRanges
                            // can compare the pair numerically. In non-unicode regex mode they
                            // are separate atoms, and extended \u{...} escapes never combine.
                            int savedPos = pos;
                            int nextCodePoint = ScanUnicodeEscape((flags & EscapeSequenceScanningFlags.ReportInvalidEscapeErrors) != 0);
                            if (StringUtil.IsLowSurrogate(nextCodePoint))
                            {
                                return RuneToString(StringUtil.SurrogatePairToCodePoint(codePoint, nextCodePoint));
                            }
                            pos = savedPos;
                        }
                    }
                    // Lone surrogate: encode as CESU-8 so it survives losslessly. In a
                    // non-unicode regex this also lets scanClassRanges compare it numerically.
                    return StringUtil.EncodeJSStringRune(codePoint);
                }
            case 'x':
                {
                    // '\xDD'
                    for (; pos < start + 4; pos++)
                    {
                        if (!StringUtil.IsHexDigit(Char()))
                        {
                            tokenFlags |= Ast.TokenFlags.ContainsInvalidEscape;
                            if ((flags & EscapeSequenceScanningFlags.ReportInvalidEscapeErrors) != 0)
                            {
                                Error(Diagnostics.Hexadecimal_digit_expected);
                            }
                            return text.Substring(start, pos - start);
                        }
                    }
                    tokenFlags |= Ast.TokenFlags.HexEscape;
                    long escapedValue = ParseInt(text.Substring(start + 2, pos - start - 2), 16, 32);
                    return RuneToString((int)escapedValue);
                }
            case '\r':
                // when encountering a LineContinuation (i.e. a backslash and a line terminator sequence),
                // the line terminator is interpreted to be "the empty code unit sequence".
                if (Char() == '\n')
                {
                    pos++;
                }
                goto case '\n';
            case '\n':
                return "";
            default:
                {
                    // ch was read as a single UTF-16 unit; for supplementary characters,
                    // we need to decode the full code point and advance past its units.
                    if (ch >= 0x80)
                    {
                        pos--; // back up past the single-unit advance
                        var (chFull, size) = CharAndSize();
                        ch = chFull;
                        pos += size;
                    }
                    // LineContinuation: a backslash followed by a line terminator is "the empty code unit sequence".
                    if (ch == 0x2028 || ch == 0x2029)
                    {
                        return "";
                    }
                    if ((flags & EscapeSequenceScanningFlags.AnyUnicodeMode) != 0 || ((flags & EscapeSequenceScanningFlags.RegularExpression) != 0 && (flags & EscapeSequenceScanningFlags.AnnexB) == 0 && IsIdentifierPart(ch)))
                    {
                        ErrorAt(Diagnostics.This_character_cannot_be_escaped_in_a_regular_expression, start, pos - start);
                    }
                    return RuneToString(ch);
                }
        }
    }

    // Known to be at \u
    // Ported from tsgo internal/scanner/scanner.go:1855 (scanUnicodeEscape)
    internal int ScanUnicodeEscape(bool shouldEmitInvalidEscapeError)
    {
        pos += 2;
        int start = pos;
        bool extended = Char() == '{';
        string hexDigits;
        if (extended)
        {
            pos++;
            hexDigits = ScanHexDigits(1, true, false);
        }
        else
        {
            tokenFlags |= Ast.TokenFlags.UnicodeEscape;
            hexDigits = ScanHexDigits(4, false, false);
        }
        if (hexDigits.Length == 0)
        {
            tokenFlags |= Ast.TokenFlags.ContainsInvalidEscape;
            if (shouldEmitInvalidEscapeError)
            {
                Error(Diagnostics.Hexadecimal_digit_expected);
            }
            return -1;
        }
        long hexValue = ParseInt(hexDigits, 16, 32);
        if (extended)
        {
            bool isInvalidExtendedEscape = false;
            if (hexValue > 0x10FFFF)
            {
                if (shouldEmitInvalidEscapeError)
                {
                    ErrorAt(Diagnostics.An_extended_Unicode_escape_value_must_be_between_0x0_and_0x10FFFF_inclusive, start + 1, pos - start - 1);
                }
                isInvalidExtendedEscape = true;
            }
            if (pos >= end)
            {
                if (shouldEmitInvalidEscapeError)
                {
                    Error(Diagnostics.Unexpected_end_of_text);
                }
                isInvalidExtendedEscape = true;
            }
            else if (Char() == '}')
            {
                pos++;
            }
            else
            {
                if (shouldEmitInvalidEscapeError)
                {
                    Error(Diagnostics.Unterminated_Unicode_escape_sequence);
                }
                isInvalidExtendedEscape = true;
            }
            if (isInvalidExtendedEscape)
            {
                tokenFlags |= Ast.TokenFlags.ContainsInvalidEscape;
                return -1;
            }
            tokenFlags |= Ast.TokenFlags.ExtendedUnicodeEscape;
        }
        return (int)hexValue;
    }

    // Ported from tsgo internal/scanner/scanner.go:1912 (scanLowSurrogateEscape)
    private (int Combined, bool Ok) ScanLowSurrogateEscape(int high)
    {
        if (Char() != '\\' || CharAt(1) != 'u')
        {
            return (0, false);
        }
        int savedPos = pos;
        var savedTokenFlags = tokenFlags;
        // Speculatively scan the escape with diagnostics suppressed: if it isn't a
        // low surrogate we rewind below, and the caller re-scans the same escape and
        // reports any error then, so reporting here would duplicate diagnostics.
        int low = ScanUnicodeEscape(false);
        if (StringUtil.IsLowSurrogate(low))
        {
            return (StringUtil.SurrogatePairToCodePoint(high, low), true);
        }
        pos = savedPos;
        tokenFlags = savedTokenFlags;
        return (0, false);
    }

    // Current character is known to be a backslash. Check for Unicode escape of the form '\uXXXX'
    // or '\u{XXXXXX}' and return code point value if valid Unicode escape is found. Otherwise return -1.
    // Ported from tsgo internal/scanner/scanner.go:1932 (peekUnicodeEscape)
    private int PeekUnicodeEscape()
    {
        if (CharAt(1) == 'u')
        {
            int savePos = pos;
            var saveTokenFlags = tokenFlags;
            int codePoint = ScanUnicodeEscape(false);
            pos = savePos;
            tokenFlags = saveTokenFlags;
            return codePoint;
        }
        return -1;
    }

    // Ported from tsgo internal/scanner/scanner.go:1944 (scanNumber)
    private SyntaxKind ScanNumber()
    {
        int start = pos;
        string fixedPart;
        if (Char() == '0')
        {
            pos++;
            if (Char() == '_')
            {
                tokenFlags |= Ast.TokenFlags.ContainsSeparator | Ast.TokenFlags.ContainsInvalidSeparator;
                ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos, 1);
                pos = start;
                fixedPart = ScanNumberFragment();
            }
            else
            {
                var (digits, isOctal) = ScanDigits();
                if (digits.Length == 0)
                {
                    fixedPart = "0";
                }
                else if (!isOctal)
                {
                    tokenFlags |= Ast.TokenFlags.ContainsLeadingZero;
                    fixedPart = digits;
                }
                else
                {
                    long val = ParseInt(digits, 8, 64);
                    tokenValue = val.ToString(CultureInfo.InvariantCulture);
                    tokenFlags |= Ast.TokenFlags.Octal;
                    bool withMinus = token == SyntaxKind.MinusToken;
                    string literal = CoreUtil.IfElse(withMinus, "-", "") + "0o" + Convert.ToString(val, 8);
                    if (withMinus)
                    {
                        start--;
                    }
                    ErrorAt(Diagnostics.Octal_literals_are_not_allowed_Use_the_syntax_0, start, pos - start, literal);
                    return SyntaxKind.NumericLiteral;
                }
            }
        }
        else
        {
            fixedPart = ScanNumberFragment();
        }
        int fixedPartEnd = pos;
        string fractionalPart = "";
        string exponentPreamble = "";
        string exponentPart = "";
        if (Char() == '.')
        {
            pos++;
            fractionalPart = ScanNumberFragment();
        }
        int end = pos;
        if (Char() == 'E' || Char() == 'e')
        {
            pos++;
            tokenFlags |= Ast.TokenFlags.Scientific;
            if (Char() == '+' || Char() == '-')
            {
                pos++;
            }
            int startNumericPart = pos;
            exponentPart = ScanNumberFragment();
            if (exponentPart.Length == 0)
            {
                Error(Diagnostics.Digit_expected);
            }
            else
            {
                exponentPreamble = text.Substring(end, startNumericPart - end);
                end = pos;
            }
        }
        if ((tokenFlags & Ast.TokenFlags.ContainsSeparator) != 0)
        {
            tokenValue = fixedPart;
            if (fractionalPart.Length != 0)
            {
                tokenValue += "." + fractionalPart;
            }
            if (exponentPart.Length != 0)
            {
                tokenValue += exponentPreamble + exponentPart;
            }
        }
        else
        {
            tokenValue = text.Substring(start, end - start);
        }
        if ((tokenFlags & Ast.TokenFlags.ContainsLeadingZero) != 0)
        {
            ErrorAt(Diagnostics.Decimals_with_leading_zeros_are_not_allowed, start, pos - start);
            tokenValue = JsNum.FromString(tokenValue).ToString();
            return SyntaxKind.NumericLiteral;
        }
        SyntaxKind result;
        if (fixedPartEnd == pos)
        {
            result = ScanBigIntSuffix();
        }
        else
        {
            tokenValue = JsNum.FromString(tokenValue).ToString();
            result = SyntaxKind.NumericLiteral;
        }
        var (chId, _) = CharAndSize();
        if (IsIdentifierStart(chId))
        {
            int idStart = pos;
            string id = ScanIdentifierParts();
            if (result != SyntaxKind.BigIntLiteral && id.Length == 1 && text[idStart] == 'n')
            {
                if ((tokenFlags & Ast.TokenFlags.Scientific) != 0)
                {
                    ErrorAt(Diagnostics.A_bigint_literal_cannot_use_exponential_notation, start, pos - start);
                    return result;
                }
                if (fixedPartEnd < idStart)
                {
                    ErrorAt(Diagnostics.A_bigint_literal_must_be_an_integer, start, pos - start);
                    return result;
                }
            }
            ErrorAt(Diagnostics.An_identifier_or_keyword_cannot_immediately_follow_a_numeric_literal, idStart, pos - idStart);
            pos = idStart;
        }
        return result;
    }

    // Ported from tsgo internal/scanner/scanner.go:2044 (scanNumberFragment)
    private string ScanNumberFragment()
    {
        int start = pos;
        bool allowSeparator = false;
        bool isPreviousTokenSeparator = false;
        var result = new StringBuilder();
        for (; ; )
        {
            int before = pos;
            ScanASCIIWhile(static b => b >= '0' && b <= '9');
            if (pos > before)
            {
                allowSeparator = true;
                isPreviousTokenSeparator = false;
            }
            int ch = Char();
            if (ch == '_')
            {
                tokenFlags |= Ast.TokenFlags.ContainsSeparator;
                if (allowSeparator)
                {
                    allowSeparator = false;
                    isPreviousTokenSeparator = true;
                    result.Append(text, start, pos - start);
                }
                else
                {
                    tokenFlags |= Ast.TokenFlags.ContainsInvalidSeparator;
                    if (isPreviousTokenSeparator)
                    {
                        ErrorAt(Diagnostics.Multiple_consecutive_numeric_separators_are_not_permitted, pos, 1);
                    }
                    else
                    {
                        ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos, 1);
                    }
                }
                pos++;
                start = pos;
                continue;
            }
            break;
        }
        if (isPreviousTokenSeparator)
        {
            tokenFlags |= Ast.TokenFlags.ContainsInvalidSeparator;
            ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos - 1, 1);
        }
        if (result.Length == 0)
        {
            return text.Substring(start, pos - start);
        }
        result.Append(text, start, pos - start);
        return result.ToString();
    }

    // Ported from tsgo internal/scanner/scanner.go:2090 (scanDigits)
    private (string, bool) ScanDigits()
    {
        int start = pos;
        bool isOctal = true;
        while (StringUtil.IsDigit(Char()))
        {
            if (!StringUtil.IsOctalDigit(Char()))
            {
                isOctal = false;
            }
            pos++;
        }
        return (text.Substring(start, pos - start), isOctal);
    }

    // Ported from tsgo internal/scanner/scanner.go:2102 (scanHexDigits)
    private string ScanHexDigits(int minCount, bool scanAsManyAsPossible, bool canHaveSeparators)
    {
        int digitCount = 0;
        int start = pos;
        bool allowSeparator = false;
        bool isPreviousTokenSeparator = false;
        while (digitCount < minCount || scanAsManyAsPossible)
        {
            int ch = Char();
            if (StringUtil.IsHexDigit(ch))
            {
                allowSeparator = canHaveSeparators;
                isPreviousTokenSeparator = false;
                digitCount++;
            }
            else if (canHaveSeparators && ch == '_')
            {
                tokenFlags |= Ast.TokenFlags.ContainsSeparator;
                if (allowSeparator)
                {
                    allowSeparator = false;
                    isPreviousTokenSeparator = true;
                }
                else if (isPreviousTokenSeparator)
                {
                    ErrorAt(Diagnostics.Multiple_consecutive_numeric_separators_are_not_permitted, pos, 1);
                }
                else
                {
                    ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos, 1);
                }
            }
            else
            {
                break;
            }
            pos++;
        }
        if (isPreviousTokenSeparator)
        {
            ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos - 1, 1);
        }
        if (digitCount < minCount)
        {
            return "";
        }
        string digits = text.Substring(start, pos - start);
        hexDigitCache ??= new Dictionary<string, string>();
        if (hexDigitCache.TryGetValue(digits, out var cached))
        {
            return cached;
        }
        else
        {
            string original = digits;
            if ((tokenFlags & Ast.TokenFlags.ContainsSeparator) != 0)
            {
                digits = digits.Replace("_", "", StringComparison.Ordinal);
            }
            digits = StringUtil.ToLowerJS(digits); // standardize hex literals to lowercase
            hexDigitCache[original] = digits;
            return digits;
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:2151 (scanBinaryOrOctalDigits)
    private string ScanBinaryOrOctalDigits(int @base)
    {
        var sb = new StringBuilder();
        bool allowSeparator = false;
        bool isPreviousTokenSeparator = false;
        for (; ; )
        {
            int ch = Char();
            if (StringUtil.IsDigit(ch) && ch - '0' < @base)
            {
                sb.Append((char)ch);
                allowSeparator = true;
                isPreviousTokenSeparator = false;
            }
            else if (ch == '_')
            {
                tokenFlags |= Ast.TokenFlags.ContainsSeparator;
                if (allowSeparator)
                {
                    allowSeparator = false;
                    isPreviousTokenSeparator = true;
                }
                else if (isPreviousTokenSeparator)
                {
                    ErrorAt(Diagnostics.Multiple_consecutive_numeric_separators_are_not_permitted, pos, 1);
                }
                else
                {
                    ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos, 1);
                }
            }
            else
            {
                break;
            }
            pos++;
        }
        if (isPreviousTokenSeparator)
        {
            ErrorAt(Diagnostics.Numeric_separators_are_not_allowed_here, pos - 1, 1);
        }
        return sb.ToString();
    }

    // Ported from tsgo internal/scanner/scanner.go:2182 (scanBigIntSuffix)
    private SyntaxKind ScanBigIntSuffix()
    {
        if (Char() == 'n')
        {
            tokenValue += "n";
            if ((tokenFlags & Ast.TokenFlags.BinaryOrOctalSpecifier) != 0)
            {
                tokenValue = JsNum.ParsePseudoBigInt(tokenValue) + "n";
            }
            pos++;
            return SyntaxKind.BigIntLiteral;
        }
        numberCache ??= new Dictionary<string, string>();
        if (numberCache.TryGetValue(tokenValue, out var cached))
        {
            tokenValue = cached;
        }
        else
        {
            string key = tokenValue;
            string tv = JsNum.FromString(tokenValue).ToString();
            if (tv == tokenValue)
            {
                tv = tokenValue; // PORT-NOTE: Go reuses the original string object when equal
            }
            numberCache[key] = tv;
            tokenValue = tv;
        }
        return SyntaxKind.NumericLiteral;
    }

    // Ported from tsgo internal/scanner/scanner.go:2207 (scanInvalidCharacter)
    private void ScanInvalidCharacter()
    {
        var (_, size) = CharAndSize();
        ErrorAt(Diagnostics.Invalid_character, pos, size);
        pos += size;
        token = SyntaxKind.Unknown;
    }

    // Ported from tsgo internal/scanner/scanner.go:2214 (GetIdentifierToken)
    public static SyntaxKind GetIdentifierToken(string str)
    {
        // PORT-NOTE: Go used len(str) (UTF-8 bytes); keywords are pure ASCII so the UTF-16
        // length yields the same keyword-vs-identifier decision for every non-ASCII input.
        if (str.Length >= 2 && str.Length <= 12 && str[0] >= 'a' && str[0] <= 'z')
        {
            if (textToKeyword.TryGetValue(str, out var keyword) && keyword != SyntaxKind.Unknown)
            {
                return keyword;
            }
        }
        return SyntaxKind.Identifier;
    }

    // Ported from tsgo internal/scanner/scanner.go:2224 (IsValidIdentifier)
    public static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }
        bool first = true;
        foreach (var r in s.EnumerateRunes())
        {
            if ((first && !IsIdentifierStart(r.Value)) || (!first && !IsIdentifierPart(r.Value)))
            {
                return false;
            }
            first = false;
        }
        return true;
    }

    // Section 6.1.4
    // Ported from tsgo internal/scanner/scanner.go:2237 (isWordCharacter)
    internal static bool IsWordCharacter(int ch)
    {
        return StringUtil.IsASCIILetter(ch) || StringUtil.IsDigit(ch) || ch == '_';
    }

    // Ported from tsgo internal/scanner/scanner.go:2241 (IsIdentifierStart)
    public static bool IsIdentifierStart(int ch)
    {
        return StringUtil.IsASCIILetter(ch) || ch == '_' || ch == '$' || (ch >= 0x80 && StringUtil.IsUnicodeIdentifierStart(ch));
    }

    // Ported from tsgo internal/scanner/scanner.go:2245 (IsIdentifierPart)
    public static bool IsIdentifierPart(int ch)
    {
        return IsIdentifierPartEx(ch, LanguageVariant.Standard);
    }

    // Ported from tsgo internal/scanner/scanner.go:2249 (IsIdentifierPartEx)
    public static bool IsIdentifierPartEx(int ch, LanguageVariant languageVariant)
    {
        return IsWordCharacter(ch) || ch == '$' ||
            (ch >= 0x80 && StringUtil.IsUnicodeIdentifierPart(ch)) ||
            (languageVariant == LanguageVariant.Jsx && (ch == '-' || ch == ':')); // "-" and ":" are valid in JSX Identifiers
    }

    // Ported from tsgo internal/scanner/scanner.go:2263 (TokenToString)
    public static string TokenToString(SyntaxKind token)
    {
        return tokenToText[(int)token];
    }

    // Ported from tsgo internal/scanner/scanner.go:2267 (StringToToken)
    public static SyntaxKind StringToToken(string s)
    {
        if (textToToken.TryGetValue(s, out var kind))
        {
            return kind;
        }
        return SyntaxKind.Unknown;
    }

    // Ported from tsgo internal/scanner/scanner.go:2275 (GetViableKeywordSuggestions)
    public static string[] GetViableKeywordSuggestions()
    {
        var result = new List<string>(textToKeyword.Count);
        // map-iter: 序无关 —— result is an unordered set of keyword suggestions
        foreach (var text in textToKeyword.Keys)
        {
            if (text.Length > 2)
            {
                result.Add(text);
            }
        }
        return result.ToArray();
    }

    // Ported from tsgo internal/scanner/scanner.go:2285 (couldStartTrivia)
    internal static bool CouldStartTrivia(string text, int pos)
    {
        // Keep in sync with skipTrivia
        char ch = text[pos];
        switch (ch)
        {
            // Characters that could start normal trivia
            case '\r':
            case '\n':
            case '\t':
            case '\v':
            case '\f':
            case ' ':
            case '/':
            // Characters that could start conflict marker trivia
            case '<':
            case '|':
            case '=':
            case '>':
                return true;
            case '#':
                // Only if its the beginning can we have #! trivia
                return pos == 0;
            default:
                return ch > maxAsciiCharacter;
        }
    }

    // Ported from tsgo internal/scanner/scanner.go:2301 (SkipTriviaOptions)
    public sealed class SkipTriviaOptions
    {
        public bool StopAfterLineBreak;
        public bool StopAtComments;
        public bool InJSDoc;
    }

    // Ported from tsgo internal/scanner/scanner.go:2307 (SkipTrivia)
    public static int SkipTrivia(string text, int pos)
    {
        return SkipTriviaEx(text, pos, null);
    }

    // Ported from tsgo internal/scanner/scanner.go:2311 (SkipTriviaEx)
    public static int SkipTriviaEx(string text, int pos, SkipTriviaOptions? options)
    {
        // PORT-NOTE: ast.PositionIsSynthesized(pos) is (pos < 0); ast/utilities lands in phase 3.
        if (pos < 0)
        {
            return pos;
        }
        options ??= new SkipTriviaOptions();

        int textLen = text.Length;
        bool canConsumeStar = false;
        // Keep in sync with couldStartTrivia
        for (; ; )
        {
            if (pos >= textLen)
            {
                return pos;
            }
            var (ch, size) = StringUtil.DecodeRuneAt(text, pos);
            switch (ch)
            {
                case '\r':
                    if (pos + 1 < textLen && text[pos + 1] == '\n')
                    {
                        pos++;
                    }
                    goto case '\n';
                case '\n':
                    pos++;
                    if (options.StopAfterLineBreak)
                    {
                        return pos;
                    }
                    canConsumeStar = options.InJSDoc;
                    continue;
                case '\t':
                case '\v':
                case '\f':
                case ' ':
                    pos++;
                    continue;
                case '/':
                    if (options.StopAtComments)
                    {
                        break;
                    }
                    if (pos + 1 < textLen)
                    {
                        if (text[pos + 1] == '/')
                        {
                            pos += 2;
                            while (pos < textLen)
                            {
                                var (c, s) = StringUtil.DecodeRuneAt(text, pos);
                                if (StringUtil.IsLineBreak(c))
                                {
                                    break;
                                }
                                pos += s;
                            }
                            canConsumeStar = false;
                            continue;
                        }
                        if (text[pos + 1] == '*')
                        {
                            pos += 2;
                            while (pos < textLen)
                            {
                                if (text[pos] == '*' && pos + 1 < textLen && text[pos + 1] == '/')
                                {
                                    pos += 2;
                                    break;
                                }
                                var (_, s) = StringUtil.DecodeRuneAt(text, pos);
                                pos += s;
                            }
                            canConsumeStar = false;
                            continue;
                        }
                    }
                    break;
                case '<':
                case '|':
                case '=':
                case '>':
                    if (IsConflictMarkerTrivia(text, pos))
                    {
                        pos = ScanConflictMarkerTrivia(text, pos, null);
                        canConsumeStar = false;
                        continue;
                    }
                    break;
                case '#':
                    if (pos == 0 && IsShebangTrivia(text, pos))
                    {
                        pos = ScanShebangTrivia(text, pos);
                        canConsumeStar = false;
                        continue;
                    }
                    break;
                case '*':
                    if (canConsumeStar)
                    {
                        pos++;
                        canConsumeStar = false;
                        continue;
                    }
                    break;
                default:
                    if (ch > maxAsciiCharacter && StringUtil.IsWhiteSpaceLike(ch))
                    {
                        pos += size;
                        continue;
                    }
                    break;
            }
            return pos;
        }
    }

    // All conflict markers consist of the same character repeated seven times.  If it is
    // a <<<<<<< or >>>>>>> marker then it is also followed by a space.
    // Ported from tsgo internal/scanner/scanner.go:2404-2407
    private const int mergeConflictMarkerLength = 7; // len("<<<<<<<")
    private const int maxAsciiCharacter = 127;

    // Ported from tsgo internal/scanner/scanner.go:2409 (isConflictMarkerTrivia)
    internal static bool IsConflictMarkerTrivia(string text, int pos)
    {
        if (pos < 0)
        {
            throw new InvalidOperationException("pos < 0");
        }

        // Fast reject: a conflict marker is the same unit repeated seven times. If the
        // second unit differs (the common case for `<`, `>`, `=`, `|` tokens), it cannot
        // be a marker, so skip the line-start check entirely.
        if (pos + 1 >= text.Length || text[pos + 1] != text[pos])
        {
            return false;
        }

        // Conflict markers must be at the start of a line.
        bool atLineStart = pos == 0 || StringUtil.IsLineBreak(text[pos - 1]);
        if (!atLineStart && pos >= 2)
        {
            var (prev, _) = DecodeLastRune(text, pos - 2);
            atLineStart = StringUtil.IsLineBreak(prev);
        }
        if (atLineStart)
        {
            char ch = text[pos];

            if (pos + mergeConflictMarkerLength < text.Length)
            {
                for (int i = 0; i < mergeConflictMarkerLength; i++)
                {
                    if (text[pos + i] != ch)
                    {
                        return false;
                    }
                }

                return ch == '=' || text[pos + mergeConflictMarkerLength] == ' ';
            }
        }

        return false;
    }

    // Ported from tsgo internal/scanner/scanner.go:2444 (scanConflictMarkerTrivia)
    internal static int ScanConflictMarkerTrivia(string text, int pos, ErrorCallback? reportError)
    {
        reportError?.Invoke(Diagnostics.Merge_conflict_marker_encountered, pos, mergeConflictMarkerLength);
        var (ch, size) = StringUtil.DecodeRuneAt(text, pos);
        int length = text.Length;

        if (ch == '<' || ch == '>')
        {
            while (pos < length && !StringUtil.IsLineBreak(ch))
            {
                pos += size;
                (ch, size) = StringUtil.DecodeRuneAt(text, pos);
            }
        }
        else
        {
            if (ch != '|' && ch != '=')
            {
                throw new InvalidOperationException("Assertion failed: ch must be either '|' or '='");
            }
            // Consume everything from the start of a ||||||| or ======= marker to the start
            // of the next ======= or >>>>>>> marker.
            while (pos < length)
            {
                char currentChar = text[pos];
                if ((currentChar == '=' || currentChar == '>') && currentChar != ch && IsConflictMarkerTrivia(text, pos))
                {
                    break;
                }

                pos++;
            }
        }

        return pos;
    }

    // Ported from tsgo internal/scanner/scanner.go:2475 (isShebangTrivia)
    internal static bool IsShebangTrivia(string text, int pos)
    {
        if (text.Length < 2)
        {
            return false;
        }
        if (pos != 0)
        {
            throw new InvalidOperationException("Shebangs check must only be done at the start of the file");
        }
        return text[0] == '#' && text[1] == '!';
    }

    // Ported from tsgo internal/scanner/scanner.go:2485 (scanShebangTrivia)
    internal static int ScanShebangTrivia(string text, int pos)
    {
        pos += 2;
        while (pos < text.Length)
        {
            var (ch, size) = StringUtil.DecodeRuneAt(text, pos);
            if (StringUtil.IsLineBreak(ch))
            {
                break;
            }
            pos += size;
        }
        return pos;
    }

    // Ported from tsgo internal/scanner/scanner.go:2497 (GetShebang)
    public static string GetShebang(string text)
    {
        if (!IsShebangTrivia(text, 0))
        {
            return "";
        }

        int end = ScanShebangTrivia(text, 0);
        return text.Substring(0, end);
    }

    // PORT-NOTE: utf8.DecodeLastRuneInString over UTF-8 bytes → decode the UTF-16 code
    // unit(s) ending at `end` (Size in UTF-16 units, 1 or 2). Used where Go walked backward.
    internal static (int Rune, int Size) DecodeLastRune(string text, int end)
    {
        if (end <= 0)
        {
            return (StringUtil.RuneError, 0);
        }
        char c = text[end - 1];
        if (char.IsLowSurrogate(c) && end >= 2 && char.IsHighSurrogate(text[end - 2]))
        {
            return (StringUtil.SurrogatePairToCodePoint(text[end - 2], c), 2);
        }
        return (c, 1);
    }

    // PORT-NOTE: Go's `string(rune)` conversion — a lone surrogate or out-of-range code point
    // encodes as U+FFFD. (Literals that must preserve lone surrogates call EncodeJSStringRune.)
    internal static string RuneToString(int cp)
    {
        if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
        {
            return "�";
        }
        return char.ConvertFromUtf32(cp);
    }

    // PORT-NOTE: mirrors Go strconv.ParseInt(s, base, bitSize) with the error discarded at
    // every call site — clamps to the signed max of bitSize on overflow.
    internal static long ParseInt(string s, int fromBase, int bitSize)
    {
        long max = bitSize >= 64 ? long.MaxValue : int.MaxValue;
        long v = 0;
        foreach (char c in s)
        {
            int d;
            if (c >= '0' && c <= '9')
            {
                d = c - '0';
            }
            else if (c >= 'a' && c <= 'f')
            {
                d = c - 'a' + 10;
            }
            else if (c >= 'A' && c <= 'F')
            {
                d = c - 'A' + 10;
            }
            else
            {
                break; // callers pre-validate; ignore trailing junk like Go's parser stops
            }
            if (d >= fromBase)
            {
                break;
            }
            if (v > (max - d) / fromBase)
            {
                return max; // overflow → clamp like Go
            }
            v = v * fromBase + d;
        }
        return v;
    }
}
