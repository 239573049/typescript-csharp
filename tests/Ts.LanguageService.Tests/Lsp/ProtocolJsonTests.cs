using System.Text.Json;
using Xunit;

namespace Ts.Lsp.Tests;

public sealed class ProtocolJsonTests
{
    [Fact]
    public void PositionJsonMatchesGeneratedGoTags()
    {
        var value = new global::Ts.LsProto.Position { Line = 3, Character = 5 };

        var json = JsonSerializer.Serialize(value, global::Ts.LsProto.LspJsonContext.Default.Position);

        Assert.Equal("{\"line\":3,\"character\":5}", json);
    }

    [Fact]
    public void CompletionItemOmitsOptionalNullMembers()
    {
        var value = new global::Ts.LsProto.CompletionItem { Label = "answer" };

        var json = JsonSerializer.Serialize(value, global::Ts.LsProto.LspJsonContext.Default.CompletionItem);

        Assert.Equal("{\"label\":\"answer\"}", json);
    }

    [Fact]
    public void DidOpenJsonUsesLanguageIdAndTextDocumentCasing()
    {
        var value = new global::Ts.LsProto.DidOpenTextDocumentParams
        {
            TextDocument = new global::Ts.LsProto.TextDocumentItem
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///src/main.ts"),
                LanguageId = "typescript",
                Version = 7,
                Text = "const x = 1;",
            },
        };

        var json = JsonSerializer.Serialize(value, global::Ts.LsProto.LspJsonContext.Default.DidOpenTextDocumentParams);

        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///src/main.ts\",\"languageId\":\"typescript\",\"version\":7,\"text\":\"const x = 1;\"}}",
            json);
    }

    [Fact]
    public void ServerCapabilitiesSerializeUnionMembersInWireShape()
    {
        var value = new global::Ts.LsProto.InitializeResult
        {
            Capabilities = new global::Ts.LsProto.ServerCapabilities
            {
                PositionEncoding = "utf-16",
                TextDocumentSync = global::Ts.LsProto.TextDocumentSyncOptionsOrKind.FromOptions(
                    new global::Ts.LsProto.TextDocumentSyncOptions
                    {
                        OpenClose = true,
                        Change = global::Ts.LsProto.TextDocumentSyncKind.Incremental,
                    }),
                HoverProvider = global::Ts.LsProto.BooleanOrHoverOptions.FromBoolean(true),
            },
        };

        var json = JsonSerializer.Serialize(value, global::Ts.LsProto.LspJsonContext.Default.InitializeResult);
        using var document = JsonDocument.Parse(json);
        var capabilities = document.RootElement.GetProperty("capabilities");

        Assert.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString());
        Assert.True(capabilities.GetProperty("textDocumentSync").GetProperty("openClose").GetBoolean());
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());
    }
}
