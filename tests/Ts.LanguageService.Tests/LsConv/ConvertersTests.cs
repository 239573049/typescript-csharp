// Ported from tsgo internal/ls/lsconv/converters_test.go (whole file).
//
// PORT-NOTEs (UTF-16 positions, plan/01 §UTF-16, plan/08 两个垫片):
//  * TestConvertersInvalidUTF8 has no counterpart here. Go's LSPTextPos is a UTF-8 byte
//    offset, and the test pins that a lone continuation byte 0x80 advances the byte
//    position by 1 while advancing the UTF-16 character by 1. In this port an offset *is*
//    a UTF-16 code unit (PositionMap was deleted), so the invalid-UTF-8 text "a\x80b\ncd"
//    is just the five-character string "a\u0080b\ncd": there is no byte/character
//    distinction left to test. The faithful equivalent — the same mapping table and the
//    byte-by-byte round-trip — is ported below as InvalidUtf8PositionMapping over that
//    string, so the clamping/round-trip behaviour it guards is still locked down.
//  * TestConvertersAgainstJSReference shells out to `node` and compares the Go mapping
//    against Node/TextDecoder's UTF-8 byte offsets. This port has no node dependency and
//    no byte offsets to compare, so the test's cases are instead checked against the
//    authoritative line-break mapping computed from the same rules the JS script encodes
//    (\n, \r, \r\n only) and the UTF-16 index of each code point boundary.
//  * TestDocumentURIToFileName / TestFileNameToDocumentURI are ported verbatim.

using System.Collections.Generic;
using System.Text;
using Ts.Core;
using Ts.Ls.LsConv;
using Ts.LsProto;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using Position = global::Ts.LsProto.Position;

namespace Ts.LsConv.Tests;

public sealed class ConvertersTests
{
    // converters_test.go:17-53 — TestDocumentURIToFileName.
    public static TheoryData<string, string> DocumentUriToFileNameData()
    {
        return new TheoryData<string, string>
        {
            { "file:///path/to/file.ts", "/path/to/file.ts" },
            { "file://server/share/file.ts", "//server/share/file.ts" },
            { "file:///d%3A/work/tsgo932/lib/utils.ts", "d:/work/tsgo932/lib/utils.ts" },
            { "file:///D%3A/work/tsgo932/lib/utils.ts", "d:/work/tsgo932/lib/utils.ts" },
            { "file:///d%3A/work/tsgo932/app/%28test%29/comp/comp-test.tsx", "d:/work/tsgo932/app/(test)/comp/comp-test.tsx" },
            { "file:///path/to/file.ts#section", "/path/to/file.ts" },
            { "file:///c:/test/me", "c:/test/me" },
            { "file://shares/files/c%23/p.cs", "//shares/files/c#/p.cs" },
            {
                "file:///c:/Source/Z%C3%BCrich%20or%20Zurich%20(%CB%88zj%CA%8A%C9%99r%C9%AAk,/Code/resources/app/plugins/c%23/plugin.json",
                "c:/Source/Zürich or Zurich (ˈzjʊərɪk,/Code/resources/app/plugins/c#/plugin.json"
            },
            { "file:///c:/test %25/path", "c:/test %/path" },
            // Go: // {"file:?q", "/"}
            { "file:///_:/path", "/_:/path" },
            { "file:///users/me/c%23-projects/", "/users/me/c#-projects/" },
            { "file://localhost/c%24/GitDevelopment/express", "//localhost/c$/GitDevelopment/express" },
            { "file:///c%3A/test%20with%20%2525/c%23code", "c:/test with %25/c#code" },

            { "untitled:Untitled-1", "^/untitled/ts-nul-authority/Untitled-1" },
            { "untitled:Untitled-1#fragment", "^/untitled/ts-nul-authority/Untitled-1#fragment" },
            { "untitled:c:/Users/jrieken/Code/abc.txt", "^/untitled/ts-nul-authority/c:/Users/jrieken/Code/abc.txt" },
            { "untitled:C:/Users/jrieken/Code/abc.txt", "^/untitled/ts-nul-authority/C:/Users/jrieken/Code/abc.txt" },
            { "untitled://wsl%2Bubuntu/home/jabaile/work/TypeScript-go/newfile.ts", "^/untitled/wsl%2Bubuntu/home/jabaile/work/TypeScript-go/newfile.ts" },
        };
    }

    [Theory]
    [MemberData(nameof(DocumentUriToFileNameData))]
    public void DocumentUriToFileName(string uri, string expectedFileName)
        => Assert.Equal(expectedFileName, new DocumentUri(uri).FileName());

    // converters_test.go:55-89 — TestFileNameToDocumentURI.
    public static TheoryData<string, string> FileNameToDocumentUriData()
    {
        return new TheoryData<string, string>
        {
            { "/path/to/file.ts", "file:///path/to/file.ts" },
            { "//server/share/file.ts", "file://server/share/file.ts" },
            { "d:/work/tsgo932/lib/utils.ts", "file:///d%3A/work/tsgo932/lib/utils.ts" },
            { "d:/work/tsgo932/app/(test)/comp/comp-test.tsx", "file:///d%3A/work/tsgo932/app/%28test%29/comp/comp-test.tsx" },
            { "c:/test/me", "file:///c%3A/test/me" },
            { "//shares/files/c#/p.cs", "file://shares/files/c%23/p.cs" },
            {
                "c:/Source/Zürich or Zurich (ˈzjʊərɪk,/Code/resources/app/plugins/c#/plugin.json",
                "file:///c%3A/Source/Z%C3%BCrich%20or%20Zurich%20%28%CB%88zj%CA%8A%C9%99r%C9%AAk%2C/Code/resources/app/plugins/c%23/plugin.json"
            },
            { "c:/test %/path", "file:///c%3A/test%20%25/path" },
            { "/", "file:///" },
            { "/_:/path", "file:///_%3A/path" },
            { "/users/me/c#-projects/", "file:///users/me/c%23-projects/" },
            { "//localhost/c$/GitDevelopment/express", "file://localhost/c%24/GitDevelopment/express" },
            { "c:/test with %25/c#code", "file:///c%3A/test%20with%20%2525/c%23code" },

            { "^/untitled/ts-nul-authority/Untitled-1", "untitled:Untitled-1" },
            { "^/untitled/ts-nul-authority/c:/Users/jrieken/Code/abc.txt", "untitled:c:/Users/jrieken/Code/abc.txt" },
            { "^/untitled/ts-nul-authority///wsl%2Bubuntu/home/jabaile/work/TypeScript-go/newfile.ts", "untitled://wsl%2Bubuntu/home/jabaile/work/TypeScript-go/newfile.ts" },
        };
    }

    [Theory]
    [MemberData(nameof(FileNameToDocumentUriData))]
    public void FileNameToDocumentURI(string fileName, string expectedUri)
        => Assert.Equal(expectedUri, Converters.FileNameToDocumentURI(fileName).Value);

    // converters_test.go:91-106 — testScript + newTestConverters.
    private sealed class TestScript : Script
    {
        public string FileName() => "test.ts";

        public required string TextValue;

        public string Text() => TextValue;
    }

    private static (Converters Conv, TestScript Script) NewTestConverters(string text)
    {
        var script = new TestScript { TextValue = text };
        var lineMap = LineMap.ComputeLSPLineStarts(text);
        var conv = new Converters(global::Ts.LsProto.PositionEncodingKind.UTF16.Value!, _ => lineMap);
        return (conv, script);
    }

    // converters_test.go:108-149 — TestConvertersInvalidUTF8 (see file header PORT-NOTE:
    // Go's byte offsets collapse into UTF-16 code-unit offsets here).
    [Fact]
    public void InvalidUtf8PositionMapping()
    {
        // Text with invalid UTF-8 byte 0x80 (continuation byte without start byte).
        var (conv, script) = NewTestConverters("a\u0080b\ncd");

        (uint Line, uint Character, int Position)[] mappings =
        [
            (0, 0, 0), // 'a'
            (0, 1, 1), // invalid byte 0x80
            (0, 2, 2), // 'b'
            (0, 3, 3), // newline (line end)
            (1, 0, 4), // 'c'
            (1, 1, 5), // 'd'
            (1, 2, 6), // EOF
        ];

        foreach ((uint line, uint character, int position) in mappings)
        {
            var lc = new Position { Line = line, Character = character };
            Assert.Equal(position, conv.LineAndCharacterToPosition(script, lc));
            Assert.Equal(lc.Line, conv.PositionToLineAndCharacter(script, position).Line);
            Assert.Equal(lc.Character, conv.PositionToLineAndCharacter(script, position).Character);
        }

        // Byte-by-byte (code-unit-by-code-unit) round-trip across the entire text.
        for (int position = 0; position <= script.Text().Length; position++)
        {
            Position lc = conv.PositionToLineAndCharacter(script, position);
            Assert.Equal(position, conv.LineAndCharacterToPosition(script, lc));
        }
    }

    // converters_test.go:277-329 — TestConvertersAgainstJSReference. The reference values
    // are the (position, line, character) triples the Node script would produce: position
    // runs over UTF-16 code units, and lines break on \n, \r and \r\n only.
    public static TheoryData<string, string> JsReferenceData()
    {
        return new TheoryData<string, string>
        {
            { "empty", "" },
            { "ascii", "hello\nworld" },
            { "ascii_crlf", "hello\r\nworld\r\n!" },
            { "ascii_cr_only", "a\rb\rc" },
            { "trailing_newline", "abc\n" },
            { "bmp_em_dash", "ab\u2014cd\nef" },
            { "bmp_multi", "α\nβ\nγδε\nzz" },
            { "supplementary_emoji", "x\U0001F600y\nz" }, // 😀 is 2 UTF-16 units
            { "supplementary_at_lineend", "ab\U0001F600\ncd\U0001F60A" },
            { "supplementary_only", "\U0001F600\U0001F601\U0001F602" },
            { "mixed", "α — \U0001F600\r\nβ\nγ\r" },
            { "long_mixed_ws", "  \tαβ\n\t\U0001F600  end\n" },
            { "zwj_emoji", "\U0001F468\u200D\U0001F4BB\nnext" },
            { "only_newlines", "\n\n\r\n\r" },
        };
    }

    [Theory]
    [MemberData(nameof(JsReferenceData))]
    public void ConvertersAgainstJSReference(string name, string text)
    {
        _ = name; // Go's t.Run name.
        var (conv, script) = NewTestConverters(text);

        int[] lineStarts = ComputeJsLineStarts(text);
        foreach ((int position, int line, int character) in CodePointBoundaries(text, lineStarts))
        {
            var expected = new Position { Line = (uint)line, Character = (uint)character };

            Position gotLc = conv.PositionToLineAndCharacter(script, position);
            Assert.Equal(expected.Line, gotLc.Line);
            Assert.Equal(expected.Character, gotLc.Character);

            Assert.Equal(position, conv.LineAndCharacterToPosition(script, expected));
        }
    }

    // The LSP line-break rules the JS reference encodes: \n, \r, \r\n only.
    private static int[] ComputeJsLineStarts(string text)
    {
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                lineStarts.Add(i + 1);
            }
            else if (c == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }
        return lineStarts.ToArray();
    }

    // The JS script's boundary walk: every UTF-16 code unit position that starts a code
    // point, plus the end of the text, with the containing (line, character) pair.
    private static List<(int Position, int Line, int Character)> CodePointBoundaries(string text, int[] lineStarts)
    {
        var result = new List<(int, int, int)>();
        int i = 0;
        while (true)
        {
            result.Add((i, LineOf(lineStarts, i), i - lineStarts[LineOf(lineStarts, i)]));
            if (i >= text.Length)
            {
                break;
            }
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }
        return result;
    }

    private static int LineOf(int[] lineStarts, int utf16Index)
    {
        int lo = 0;
        int hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (lineStarts[mid] <= utf16Index)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo;
    }
}
