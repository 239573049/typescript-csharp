// Ported from tsgo internal/ls/format_test.go (whole file).
//
// PORT-NOTE: Go builds a bare `&LanguageService{}` with a nil program and calls the
// unexported getFormattingEditsAfterKeystroke / getFormattingEditsForRange. The C# type
// has no parameterless form (its constructors dereference the host's program), so each
// test builds a minimal program over an in-memory FS that already contains the file under
// test — the same file GetProgramAndFile resolves. The formatting methods stay private
// upstream and are reached through their public LSP-facing entry points
// (ProvideFormatDocumentOnType / ProvideFormatDocumentRange); SourceFile.Text() is what the
// formatter reads, exactly as in Go.
//
// PORT-NOTE: Go passes byte offsets; this port's positions are UTF-16 code units, so the
// range offsets are converted to (line, character) with the same LSP line-break rules.
// PORT-NOTE: Go's context.Background() maps to RequestContext.Background.

using System.Collections.Generic;
using Ts.Core;
using Ts.Ls;
using Ts.LsProto;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class FormatTests
{
    private static LanguageService NewLanguageService(string fileName, string text)
    {
        var files = new Dictionary<string, string> { [fileName] = text };
        IFileSystem fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        global::Ts.Compiler.CompilerHost host = global::Ts.Compiler.CompilerHosts.NewCompilerHost("/", fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            new CompilerOptions { NoLib = Tristate.True },
            [fileName],
            new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = "/" });
        var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Host = host,
            Config = config,
            SingleThreaded = Tristate.True,
        });
        return LanguageService.NewLanguageService(program);
    }

    private static FormattingOptions DefaultOptions() => new() { TabSize = 4u, InsertSpaces = true };

    // The LSP position (UTF-16 code units) for a code-unit offset, using the LSP line-break
    // rules (\n, \r, \r\n only).
    private static Position LspPosition(string text, int offset)
    {
        int line = 0;
        int lineStart = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                line++;
                lineStart = i + 1;
            }
            else if (c == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return new Position { Line = (uint)line, Character = (uint)(offset - lineStart) };
    }

    // format_test.go:15-42 — TestGetFormattingEditsAfterKeystroke_EmptyFile.
    // Regression test for issue: Panic Handling textDocument/onTypeFormatting.
    [Fact]
    public void GetFormattingEditsAfterKeystroke_EmptyFile()
    {
        const string text = "";
        LanguageService langService = NewLanguageService("/index.ts", text);

        // This should not panic.
        langService.ProvideFormatDocumentOnType(
            RequestContext.Background,
            new DocumentUri("file:///index.ts"),
            DefaultOptions(),
            new Position { Line = 0, Character = 0 },
            "\n");
    }

    // format_test.go:44-72 — TestGetFormattingEditsAfterKeystroke_SimpleStatement.
    [Fact]
    public void GetFormattingEditsAfterKeystroke_SimpleStatement()
    {
        const string text = "const x = 1";
        LanguageService langService = NewLanguageService("/index.ts", text);

        // This should not panic (position at end of file).
        langService.ProvideFormatDocumentOnType(
            RequestContext.Background,
            new DocumentUri("file:///index.ts"),
            DefaultOptions(),
            new Position { Line = 0, Character = (uint)text.Length },
            "\n");
    }

    // format_test.go:76-135 — TestGetFormattingEditsForRange_FunctionBody.
    public static TheoryData<string, string, int, int> FunctionBodyData()
    {
        return new TheoryData<string, string, int, int>
        {
            { "return statement in function", "function foo() {\n    return (1  + 2);\n}", 21, 38 },
            { "function with newline after keyword", "function\nf() {\n}", 9, 13 },
            { "empty function body", "function f() {\n  \n}", 15, 17 },
            { "after function closing brace", "function f() {\n}", 15, 15 },
        };
    }

    [Theory]
    [MemberData(nameof(FunctionBodyData))]
    public void GetFormattingEditsForRange_FunctionBody(string name, string text, int startPos, int endPos)
    {
        _ = name; // Go's t.Run name.
        LanguageService langService = NewLanguageService("/test.ts", text);

        // This should not panic.
        langService.ProvideFormatDocumentRange(
            RequestContext.Background,
            new DocumentUri("file:///test.ts"),
            DefaultOptions(),
            new Range
            {
                Start = LspPosition(text, startPos),
                End = LspPosition(text, endPos),
            });
    }
}
