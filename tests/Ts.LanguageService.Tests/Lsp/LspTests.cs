// Ported from tsgo internal/lsp/lsproto/lsp_test.go (whole file).
//
// PORT-NOTE: Go's json.Unmarshal fills the generated struct's optional members that are
// present and leaves omitted ones at their zero value. The C# port uses STJ source
// generation (plan/04-codegen.md §5); the union member TextEdit rides the generated
// TextEditOrInsertReplaceEditConverter. Generated Position/Range are reference classes
// without value equality, so the range leaves are compared field by field.

using System.Text.Json;
using Ts.LsProto;
using Xunit;

namespace Ts.LsProto.Tests;

public sealed class LspTests
{
    // lsp_test.go:10-84 — TestUnmarshalCompletionItem.
    [Fact]
    public void UnmarshalCompletionItem()
    {
        const string message = """
        {
            "label": "pageXOffset",
            "insertTextFormat": 1,
            "textEdit": {
                "newText": "pageXOffset",
                "insert": {
                    "start": {
                        "line": 4,
                        "character": 0
                    },
                    "end": {
                        "line": 4,
                        "character": 4
                    }
                },
                "replace": {
                    "start": {
                        "line": 4,
                        "character": 0
                    },
                    "end": {
                        "line": 4,
                        "character": 4
                    }
                }
            },
            "kind": 6,
            "sortText": "15",
            "commitCharacters": [
                ".",
                ",",
                ";"
            ]
        }
        """;

        CompletionItem? result = JsonSerializer.Deserialize(message, LspJsonContext.Default.CompletionItem);
        Assert.NotNull(result);

        Assert.Equal("pageXOffset", result.Label);
        Assert.Equal(InsertTextFormat.PlainText, result.InsertTextFormat!.Value);

        Assert.NotNull(result.TextEdit);
        InsertReplaceEdit? edit = result.TextEdit!.InsertReplaceEdit;
        Assert.NotNull(edit);
        Assert.Equal("pageXOffset", edit!.NewText);
        Assert.Equal(4u, edit.Insert.Start.Line);
        Assert.Equal(0u, edit.Insert.Start.Character);
        Assert.Equal(4u, edit.Insert.End.Line);
        Assert.Equal(4u, edit.Insert.End.Character);
        Assert.Equal(4u, edit.Replace.Start.Line);
        Assert.Equal(0u, edit.Replace.Start.Character);
        Assert.Equal(4u, edit.Replace.End.Line);
        Assert.Equal(4u, edit.Replace.End.Character);

        Assert.Equal(CompletionItemKind.Variable, result.Kind!.Value);
        Assert.Equal("15", result.SortText);
        Assert.Equal(new[] { ".", ",", ";" }, result.CommitCharacters);
    }
}
