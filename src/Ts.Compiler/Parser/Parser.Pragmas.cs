// Ported from tsgo internal/parser/parser.go (pragma scanning section, ~6432-6682):
// getCommentPragmas / extractPragmas / processPragmasIntoFields / parseResolutionMode and
// their string helpers (match/skipBlanks/skipNonBlanks/skipTo/lineEndPos/extractName/
// extractQuotedString). These populate SourceFile.Pragmas and, from them,
// ReferencedFiles / TypeReferenceDirectives / LibReferenceDirectives / CheckJsDirective —
// consumed by the file loader (lib/path/types reference following) and checker.
//
// POSITIONS: UTF-16 code units. Go indexes `text` by byte and uses byte len(); here `text`
// is a UTF-16 string, so text[pos] is a char and text.Length is the UTF-16 unit count. Every
// offset produced (commentRange.Pos()+pos+...) is a UTF-16 offset into sourceText, which is
// consistent because the comment substring is a UTF-16 substring of sourceText (05 §8.1).
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Parser;

public sealed partial class Parser
{
    // parser.go:6432-6438
    private Pragma[]? GetCommentPragmas(NodeFactory f, string sourceText)
    {
        List<Pragma>? pragmas = null;
        foreach (CommentRange commentRange in CommentScanner.GetLeadingCommentRanges(f, sourceText, 0))
        {
            string comment = sourceText.Substring(commentRange.Pos, commentRange.End - commentRange.Pos);
            Pragma[]? extracted = ExtractPragmas(commentRange, comment);
            if (extracted != null)
            {
                (pragmas ??= new List<Pragma>()).AddRange(extracted);
            }
        }
        return pragmas?.ToArray();
    }

    // parser.go:6440-6542
    private static Pragma[]? ExtractPragmas(CommentRange commentRange, string text)
    {
        if (commentRange.Kind == SyntaxKind.SingleLineCommentTrivia)
        {
            int pos = 2;
            bool tripleSlash = Match(text, pos, "/");
            if (tripleSlash)
            {
                pos++;
            }
            pos = SkipBlanks(text, pos);
            if (tripleSlash && Match(text, pos, "<"))
            {
                string tagName = ExtractName(text, pos + 1);
                if (tagName != "reference")
                {
                    return null;
                }
                pos += 10;
                Dictionary<string, PragmaArgument> args = new Dictionary<string, PragmaArgument>();
                while (true)
                {
                    pos = SkipBlanks(text, pos);
                    if (Match(text, pos, "/>"))
                    {
                        break;
                    }
                    string argName = ExtractName(text, pos);
                    if (argName == "")
                    {
                        break;
                    }
                    pos = SkipBlanks(text, pos + argName.Length);
                    if (!Match(text, pos, "="))
                    {
                        break;
                    }
                    pos = SkipBlanks(text, pos + 1);
                    if (!ExtractQuotedString(text, pos, out string value))
                    {
                        break;
                    }
                    args[argName] = new PragmaArgument
                    {
                        Name = argName,
                        Value = value,
                        TextRange = new TextRange(commentRange.Pos + pos + 1, commentRange.Pos + pos + 1 + value.Length),
                    };
                    pos += value.Length + 2;
                }
                return new[]
                {
                    new Pragma
                    {
                        CommentRange = commentRange,
                        Name = "reference",
                        Args = args,
                    },
                };
            }
            if (Match(text, pos, "@"))
            {
                pos++;
                string pragmaName = ExtractName(text, pos);
                if (!(pragmaName == "ts-check" || pragmaName == "ts-nocheck"))
                {
                    return null;
                }
                return new[]
                {
                    new Pragma
                    {
                        CommentRange = commentRange,
                        Name = pragmaName,
                    },
                };
            }
        }
        if (commentRange.Kind == SyntaxKind.MultiLineCommentTrivia)
        {
            if (text.EndsWith("*/", System.StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 2);
            }
            int pos = 2;
            List<Pragma>? pragmas = null;
            while (true)
            {
                pos = SkipTo(text, pos, "@");
                if (pos < 0)
                {
                    break;
                }
                // Mirrors the /@(\S+)(\s+(?:\S.*)?)?$/gm pragma regex used by TypeScript: the '@'
                // must be immediately followed by a non-whitespace pragma name, and the remainder
                // of the line is consumed as that pragma's arguments. As a consequence, only the
                // first '@'-token on a line is considered, so an unrelated '@token' earlier on the
                // line (e.g. an email address) prevents a later '@jsx' on the same line from being
                // treated as a pragma.
                int namePos = pos + 1;
                int nameEnd = SkipNonBlanks(text, namePos);
                if (nameEnd == namePos)
                {
                    pos++;
                    continue;
                }
                int lineEnd = LineEndPos(text, pos);
                string pragmaName = ToLower(text.Substring(namePos, nameEnd - namePos));
                if (pragmaName == "jsx" || pragmaName == "jsxfrag" || pragmaName == "jsximportsource" || pragmaName == "jsxruntime")
                {
                    int start = SkipBlanks(text, nameEnd);
                    int argEnd = SkipNonBlanks(text, start);
                    if (argEnd != start)
                    {
                        Dictionary<string, PragmaArgument> args = new Dictionary<string, PragmaArgument>(1);
                        args["factory"] = new PragmaArgument
                        {
                            Name = "factory",
                            Value = text.Substring(start, argEnd - start),
                            TextRange = new TextRange(commentRange.Pos + start, commentRange.Pos + argEnd),
                        };
                        (pragmas ??= new List<Pragma>()).Add(new Pragma
                        {
                            CommentRange = commentRange,
                            Name = pragmaName,
                            Args = args,
                        });
                    }
                }
                pos = lineEnd;
            }
            return pragmas?.ToArray();
        }
        return null;
    }

    // parser.go:6544-6546 — strings.HasPrefix(text[pos:], s).
    private static bool Match(string text, int pos, string s)
    {
        if (text.Length - pos < s.Length)
        {
            return false;
        }
        return string.CompareOrdinal(text, pos, s, 0, s.Length) == 0;
    }

    // parser.go:6548-6553
    private static int SkipBlanks(string text, int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t'))
        {
            pos++;
        }
        return pos;
    }

    // parser.go:6555-6560
    private static int SkipNonBlanks(string text, int pos)
    {
        while (pos < text.Length && (text[pos] != ' ' && text[pos] != '\t' && text[pos] != '\r' && text[pos] != '\n'))
        {
            pos++;
        }
        return pos;
    }

    // parser.go:6562-6571
    private static int SkipTo(string text, int pos, string s)
    {
        if (pos >= text.Length)
        {
            return -1;
        }
        int i = text.IndexOf(s, pos, System.StringComparison.Ordinal);
        if (i < 0)
        {
            return -1;
        }
        return i;
    }

    // parser.go:6573-6582
    private static int LineEndPos(string text, int pos)
    {
        while (pos < text.Length)
        {
            (int ch, int size) = DecodeRune(text, pos);
            if (global::Ts.StringUtil.StringUtil.IsLineBreak(ch))
            {
                return pos;
            }
            pos += size;
        }
        return text.Length;
    }

    // parser.go:6584-6590
    private static string ExtractName(string text, int pos)
    {
        int start = pos;
        while (pos < text.Length && (text[pos] >= 'A' && text[pos] <= 'Z' || text[pos] >= 'a' && text[pos] <= 'z' || text[pos] == '-'))
        {
            pos++;
        }
        return ToLower(text.Substring(start, pos - start));
    }

    // parser.go:6592-6609
    private static bool ExtractQuotedString(string text, int pos, out string result)
    {
        if (pos == text.Length)
        {
            result = "";
            return false;
        }
        char quote = text[pos];
        if (quote != '\'' && quote != '"')
        {
            result = "";
            return false;
        }
        pos++;
        int start = pos;
        while (pos < text.Length && text[pos] != quote)
        {
            pos++;
        }
        if (pos == text.Length)
        {
            result = "";
            return false;
        }
        result = text.Substring(start, pos - start);
        return true;
    }

    // parser.go:6611-6669
    private void ProcessPragmasIntoFields(SourceFile context)
    {
        context.CheckJsDirective = null;
        context.ReferencedFiles = null;
        context.TypeReferenceDirectives = null;
        context.LibReferenceDirectives = null;
        // context.AmdDependencies = null;
        List<FileReference>? referencedFiles = null;
        List<FileReference>? typeReferenceDirectives = null;
        List<FileReference>? libReferenceDirectives = null;
        if (context.Pragmas == null)
        {
            return;
        }
        foreach (Pragma pragma in context.Pragmas)
        {
            switch (pragma.Name)
            {
                case "reference":
                    // Go reads the (nil-safe) args map; reference pragmas always carry a non-null Args.
                    Dictionary<string, PragmaArgument> pragmaArgs = pragma.Args ?? new Dictionary<string, PragmaArgument>();
                    bool typesOk = pragmaArgs.TryGetValue("types", out PragmaArgument types);
                    bool libOk = pragmaArgs.TryGetValue("lib", out PragmaArgument lib);
                    bool pathOk = pragmaArgs.TryGetValue("path", out PragmaArgument path);
                    bool resolutionModeOk = pragmaArgs.TryGetValue("resolution-mode", out PragmaArgument resolutionMode);
                    bool preserveOk = pragmaArgs.TryGetValue("preserve", out PragmaArgument preserve);
                    bool noDefaultLibOk = pragmaArgs.TryGetValue("no-default-lib", out PragmaArgument noDefaultLib);
                    if (noDefaultLibOk && noDefaultLib.Value == "true")
                    {
                        // Ignored.
                    }
                    else if (typesOk)
                    {
                        ResolutionMode parsed = default;
                        if (resolutionModeOk)
                        {
                            parsed = ParseResolutionMode(resolutionMode.Value, resolutionMode.TextRange.Pos, resolutionMode.TextRange.End);
                        }
                        (typeReferenceDirectives ??= new List<FileReference>()).Add(new FileReference
                        {
                            TextRange = types.TextRange,
                            FileName = types.Value,
                            ResolutionMode = parsed,
                            Preserve = preserveOk && preserve.Value == "true",
                        });
                    }
                    else if (libOk)
                    {
                        (libReferenceDirectives ??= new List<FileReference>()).Add(new FileReference
                        {
                            TextRange = lib.TextRange,
                            FileName = lib.Value,
                            Preserve = preserveOk && preserve.Value == "true",
                        });
                    }
                    else if (pathOk)
                    {
                        (referencedFiles ??= new List<FileReference>()).Add(new FileReference
                        {
                            TextRange = path.TextRange,
                            FileName = path.Value,
                            Preserve = preserveOk && preserve.Value == "true",
                        });
                    }
                    else
                    {
                        ParseErrorAtRange(pragma.CommentRange.TextRange, Diag.Invalid_reference_directive_syntax);
                    }
                    break;
                case "ts-check":
                case "ts-nocheck":
                    // _last_ of either nocheck or check in a file is the "winner"
                    if (context.CheckJsDirective == null || pragma.CommentRange.Pos > context.CheckJsDirective.Range.Pos)
                    {
                        context.CheckJsDirective = new CheckJsDirective
                        {
                            Enabled = pragma.Name == "ts-check",
                            Range = pragma.CommentRange,
                        };
                    }
                    break;
                case "jsx":
                case "jsxfrag":
                case "jsximportsource":
                case "jsxruntime":
                    // Nothing to do here
                    break;
                default:
                    throw new System.InvalidOperationException("Unhandled pragma kind: " + pragma.Name);
            }
        }
        context.ReferencedFiles = referencedFiles?.ToArray();
        context.TypeReferenceDirectives = typeReferenceDirectives?.ToArray();
        context.LibReferenceDirectives = libReferenceDirectives?.ToArray();
    }

    // Go strings.ToLower on the ASCII-restricted pragma/argument names collected above.
    // PORT-NOTE: extractName yields only [A-Za-z-]; the multiline path lowers a \S+ token
    // solely to compare against ASCII jsx* names. ASCII lowering therefore reproduces Go's
    // simple per-rune lowering for every input that can affect a match result (a non-ASCII
    // token can never equal an ASCII pragma name either way). Avoids CultureInfo (07 §7).
    private static string ToLower(string s)
    {
        char[]? buf = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 'A' && c <= 'Z')
            {
                buf ??= s.ToCharArray();
                buf[i] = (char)(c + ('a' - 'A'));
            }
        }
        return buf == null ? s : new string(buf);
    }

    // Decodes the UTF-16 code point at pos; size is in UTF-16 code units (1 or 2).
    private static (int Ch, int Size) DecodeRune(string text, int pos)
    {
        char c = text[pos];
        if (char.IsHighSurrogate(c) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
        {
            return (char.ConvertToUtf32(c, text[pos + 1]), 2);
        }
        return (c, 1);
    }
}
