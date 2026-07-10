using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ts.StringUtil;

// Go: internal/stringutil/util.go
// Package stringutil Exports common rune utilities for parsing and emitting javascript
//
// PORT-NOTE (plan/05-conventions.md §8.1): Go strings are UTF-8 bytes; here they are
// UTF-16 code units. Every "byte offset"/"byte length" below shifts wholesale to
// UTF-16 units, and rune decode sizes are 1 or 2 units instead of 1–4 bytes.
public static partial class StringUtil
{
    // Go: utf8.RuneError — the Unicode replacement character U+FFFD.
    public const int RuneError = 0xFFFD;

    public static bool IsWhiteSpaceLike(int ch)
    {
        return IsWhiteSpaceSingleLine(ch) || IsLineBreak(ch);
    }

    public static bool IsWhiteSpaceSingleLine(int ch)
    {
        // Note: nextLine is in the Zs space, and should be considered to be a whitespace.
        // It is explicitly not a line-break as it isn't in the exact set specified by EcmaScript.
        switch (ch)
        {
            case ' ':    // space
            case '\t':   // tab
            case '\v':   // verticalTab
            case '\f':   // formFeed
            case 0x0085: // nextLine
            case 0x00A0: // nonBreakingSpace
            case 0x1680: // ogham
            case 0x2000: // enQuad
            case 0x2001: // emQuad
            case 0x2002: // enSpace
            case 0x2003: // emSpace
            case 0x2004: // threePerEmSpace
            case 0x2005: // fourPerEmSpace
            case 0x2006: // sixPerEmSpace
            case 0x2007: // figureSpace
            case 0x2008: // punctuationEmSpace
            case 0x2009: // thinSpace
            case 0x200A: // hairSpace
            case 0x200B: // zeroWidthSpace
            case 0x202F: // narrowNoBreakSpace
            case 0x205F: // mathematicalSpace
            case 0x3000: // ideographicSpace
            case 0xFEFF: // byteOrderMark
                return true;
        }
        return false;
    }

    public static bool IsLineBreak(int ch)
    {
        // ES5 7.3:
        // The ECMAScript line terminator characters are listed in Table 3.
        //     Table 3: Line Terminator Characters
        //     Code Unit Value     Name                    Formal Name
        //     \u000A              Line Feed               <LF>
        //     \u000D              Carriage Return         <CR>
        //     \u2028              Line separator          <LS>
        //     \u2029              Paragraph separator     <PS>
        // Only the characters in Table 3 are treated as line terminators. Other new line or line
        // breaking characters are treated as white space but not as line terminators.
        switch (ch)
        {
            case '\n':   // lineFeed
            case '\r':   // carriageReturn
            case 0x2028: // lineSeparator
            case 0x2029: // paragraphSeparator
                return true;
        }
        return false;
    }

    public static bool IsDigit(int ch)
    {
        return ch >= '0' && ch <= '9';
    }

    public static bool IsOctalDigit(int ch)
    {
        return ch >= '0' && ch <= '7';
    }

    public static bool IsHexDigit(int ch)
    {
        return ch >= '0' && ch <= '9' || ch >= 'A' && ch <= 'F' || ch >= 'a' && ch <= 'f';
    }

    public static bool IsASCIILetter(int ch)
    {
        return ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z';
    }

    public static bool ContainsNonASCII(string s)
    {
        // Go checked bytes >= utf8.RuneSelf; a UTF-16 unit >= 0x80 is the same
        // predicate (every non-ASCII code point contains at least one such unit).
        foreach (char c in s)
        {
            if (c >= 0x80)
            {
                return true;
            }
        }
        return false;
    }

    public static string[] SplitLines(string text)
    {
        var lines = new List<string>(text.AsSpan().Count('\n') + 1); // preallocate
        int start = 0;
        int pos = 0;
        while (pos < text.Length)
        {
            switch (text[pos])
            {
                case '\r':
                    if (pos + 1 < text.Length && text[pos + 1] == '\n')
                    {
                        lines.Add(text[start..pos]);
                        pos += 2;
                        start = pos;
                        continue;
                    }
                    goto case '\n'; // Go: fallthrough
                case '\n':
                    lines.Add(text[start..pos]);
                    pos++;
                    start = pos;
                    continue;
            }
            pos++;
        }
        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }
        return lines.ToArray();
    }

    public static int GuessIndentation(string[] lines)
    {
        const int MAX_SMI_X86 = 0x3fff_ffff;
        int indentation = MAX_SMI_X86;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }
            int i = 0;
            while (i < line.Length && i < indentation)
            {
                var (ch, size) = DecodeRuneAt(line, i);
                if (!IsWhiteSpaceLike(ch))
                {
                    break;
                }
                i += size;
            }
            if (i < indentation)
            {
                indentation = i;
            }
            if (indentation == 0)
            {
                return 0;
            }
        }
        if (indentation == MAX_SMI_X86)
        {
            return 0;
        }
        return indentation;
    }

    // https://tc39.es/ecma262/multipage/global-object.html#sec-encodeuri-uri
    public static string EncodeURI(string s)
    {
        // Go walked the UTF-8 bytes directly; here each code point is re-encoded to
        // the same bytes (WTF-8 for a lone surrogate, matching the sentinel bytes Go
        // would have seen), so the output is byte-for-byte identical.
        var builder = new StringBuilder();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c < 0x80)
            {
                if (!ShouldEscapeForEncodeURI((byte)c))
                {
                    builder.Append(c);
                }
                else
                {
                    AppendPercentEscaped(builder, (byte)c);
                }
                i++;
                continue;
            }

            var (r, size) = DecodeJSStringRune(s, i);
            i += size;
            if (r < 0x800)
            {
                AppendPercentEscaped(builder, (byte)(0xC0 | (r >> 6)));
                AppendPercentEscaped(builder, (byte)(0x80 | (r & 0x3F)));
            }
            else if (r < 0x10000)
            {
                AppendPercentEscaped(builder, (byte)(0xE0 | (r >> 12)));
                AppendPercentEscaped(builder, (byte)(0x80 | ((r >> 6) & 0x3F)));
                AppendPercentEscaped(builder, (byte)(0x80 | (r & 0x3F)));
            }
            else
            {
                AppendPercentEscaped(builder, (byte)(0xF0 | (r >> 18)));
                AppendPercentEscaped(builder, (byte)(0x80 | ((r >> 12) & 0x3F)));
                AppendPercentEscaped(builder, (byte)(0x80 | ((r >> 6) & 0x3F)));
                AppendPercentEscaped(builder, (byte)(0x80 | (r & 0x3F)));
            }
        }
        return builder.ToString();
    }

    private const string UpperHex = "0123456789ABCDEF"; // Go: upperhex

    private static void AppendPercentEscaped(StringBuilder builder, byte escaped)
    {
        builder.Append('%');
        builder.Append(UpperHex[escaped >> 4]);
        builder.Append(UpperHex[escaped & 0x0F]);
    }

    private static bool ShouldEscapeForEncodeURI(byte b)
    {
        switch (b)
        {
            case >= (byte)'A' and <= (byte)'Z':
                return false;
            case >= (byte)'a' and <= (byte)'z':
                return false;
            case >= (byte)'0' and <= (byte)'9':
                return false;
        }

        switch ((char)b)
        {
            case ';' or '/' or '?' or ':' or '@' or '&' or '=' or '+' or '$' or ',' or '#' or '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')':
                return false;
            default:
                return true;
        }
    }

    // PORT-NOTE: Go recognized the raw UTF-8 (EF BB BF) and UTF-16 (FE FF / FF FE)
    // BOM byte patterns because its strings carried raw file bytes. In this port
    // text is always a decoded UTF-16 string (the vfs decodes files and consumes
    // encoding BOMs at read time — tsgo internal/vfs/internal/internal.go
    // decodeBytes), so the only representable BOM is the single code unit U+FEFF;
    // the raw UTF-16-byte states the other branches detected cannot exist here.
    private static int GetByteOrderMarkLength(string text)
    {
        if (text.Length >= 1 && text[0] == '\uFEFF')
        {
            return 1;
        }
        return 0;
    }

    public static string RemoveByteOrderMark(string text)
    {
        int length = GetByteOrderMarkLength(text);
        if (length > 0)
        {
            return text[length..];
        }
        return text;
    }

    // Go prepended the raw UTF-8 BOM bytes "\xEF\xBB\xBF"; prepending U+FEFF here
    // produces exactly those bytes once the emitted text is written as UTF-8.
    public static string AddUTF8ByteOrderMark(string text)
    {
        if (GetByteOrderMarkLength(text) == 0)
        {
            return "\uFEFF" + text;
        }
        return text;
    }

    public static string StripQuotes(string name)
    {
        if (name.Length < 2)
        {
            return name;
        }
        // Go decoded the first/last rune; the quote candidates are all ASCII, so
        // comparing single code units is equivalent.
        char firstChar = name[0];
        char lastChar = name[^1];
        if (firstChar == lastChar && (firstChar == '\'' || firstChar == '"' || firstChar == '`'))
        {
            return name[1..^1];
        }
        return name;
    }

    // Go: var matchSlashSomething = regexp.MustCompile(`\\.`) + matchSlashReplacer.
    [GeneratedRegex(@"\\.")]
    private static partial Regex MatchSlashSomething();

    public static string UnquoteString(string str)
    {
        // strconv.Unquote is insufficient as that only handles a single character inside single quotes, as those are character literals in go
        string inner = StripQuotes(str);
        // In strada we do str.replace(/\\./g, s => s.substring(1)) - which is to say, replace all backslash-something with just something
        // That's replicated here faithfully, but it seems wrong! This should probably be an actual unquote operation?
        return MatchSlashSomething().Replace(inner, static m => m.Value[1..]);
    }

    public static string LowerFirstChar(string str)
    {
        // Go: utf8.DecodeRuneInString + unicode.ToLower. PORT-NOTE: on a leading lone
        // surrogate Go decoded RuneError over one sentinel byte, producing U+FFFD plus
        // two stray bytes; here the whole (one-unit) surrogate becomes U+FFFD via
        // DecodeRuneAt with no stray remainder — the Go behavior was an encoding
        // accident, not a semantic.
        var (ch, size) = DecodeRuneAt(str, 0);
        if (size > 0)
        {
            return EncodeJSStringRune(ToLowerRune(ch)) + str[size..];
        }
        return str;
    }

    public static string TruncateByRunes(string str, int maxLength)
    {
        // Go compared the byte length against the rune budget as a cheap early out;
        // the UTF-16 unit count plays the same role here (length >= rune count).
        if (str.Length < maxLength)
        {
            return str;
        }
        if (maxLength <= 0)
        {
            return "";
        }
        int runeCount = 0;
        for (int i = 0; i < str.Length;)
        {
            runeCount++;
            if (runeCount > maxLength)
            {
                return str[..i];
            }
            var (_, size) = DecodeJSStringRune(str, i);
            i += size;
        }
        return str;
    }

    // SurrogateLowStart is the boundary between the high and low halves of the
    // UTF-16 surrogate range. unicode/utf16 only exposes IsSurrogate for the
    // whole range, so this split point is defined here to distinguish the two.
    public const int SurrogateLowStart = 0xDC00;

    public static bool IsHighSurrogate(int ch)
    {
        return IsSurrogate(ch) && ch < SurrogateLowStart;
    }

    public static bool IsLowSurrogate(int ch)
    {
        return IsSurrogate(ch) && ch >= SurrogateLowStart;
    }

    public static bool IsSurrogate(int ch)
    {
        // Go: utf16.IsSurrogate.
        return ch is >= 0xD800 and <= 0xDFFF;
    }

    public static int SurrogatePairToCodePoint(int high, int low)
    {
        // Go: utf16.DecodeRune — yields U+FFFD unless given a valid high+low pair.
        if (!IsHighSurrogate(high) || !IsLowSurrogate(low))
        {
            return RuneError;
        }
        return 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00);
    }

    public static (int High, int Low) CodePointToSurrogatePair(int ch)
    {
        // Go: utf16.EncodeRune — yields (U+FFFD, U+FFFD) unless ch is a valid
        // supplementary code point.
        if (ch < 0x10000 || ch > 0x10FFFF)
        {
            return (RuneError, RuneError);
        }
        ch -= 0x10000;
        return (0xD800 + (ch >> 10), 0xDC00 + (ch & 0x3FF));
    }

    // PORT-NOTE (semantic re-audit of Go's UTF-16-in-UTF-8 sentinel scheme): Go
    // cannot hold a lone surrogate in valid UTF-8, so EncodeJSStringRune spelled
    // out a 3-byte WTF-8 sentinel and DecodeJSStringRune recognized it. C# strings
    // are UTF-16, where a lone surrogate is an ordinary code unit — the sentinel
    // byte math dissolves. The functions survive so call sites (scanner, checker,
    // printer) still port 1:1.
    public static string EncodeJSStringRune(int ch)
    {
        if (IsSurrogate(ch))
        {
            return ((char)ch).ToString();
        }
        if ((uint)ch > 0x10FFFF)
        {
            // Go: string(ch) maps out-of-range runes to U+FFFD rather than failing.
            return "\uFFFD";
        }
        return char.ConvertFromUtf32(ch);
    }

    // DecodeJSStringRune decodes the code point starting at pos, treating a lone
    // surrogate as its own code point (Go decoded the WTF-8 sentinel to the same
    // value). Size is in UTF-16 code units; an exhausted input yields
    // (RuneError, 0), matching utf8.DecodeRuneInString on an empty string.
    // Contrast DecodeRuneAt (Runes.cs), which — like Go's utf8 decoder — treats a
    // lone surrogate as undecodable and yields (RuneError, 1).
    public static (int Rune, int Size) DecodeJSStringRune(string s, int pos = 0)
    {
        if (pos >= s.Length)
        {
            return (RuneError, 0);
        }
        char c = s[pos];
        if (char.IsHighSurrogate(c) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
        {
            return (char.ConvertToUtf32(c, s[pos + 1]), 2);
        }
        return (c, 1);
    }

    // CombineSurrogatePairs canonicalizes a JS-string value produced by
    // concatenation, merging any adjacent high+low surrogate sentinel pair (as
    // written by EncodeJSStringRune) into the single supplementary code point they
    // represent. This mirrors how concatenating two UTF-16 code units forms a
    // surrogate pair in a JavaScript string. It must be applied wherever separately
    // scanned string values are joined, since each half is only a lone surrogate
    // until it meets its partner. Strings without a lone-surrogate sentinel (the
    // common case) are returned unchanged.
    //
    // PORT-NOTE: in UTF-16 an adjacent high+low pair IS the supplementary code
    // point — there is no distinguishable uncombined representation to normalize,
    // so this is the identity function. It survives so call sites (checker
    // template-literal/relater paths) still port 1:1.
    public static string CombineSurrogatePairs(string s)
    {
        return s;
    }
}
