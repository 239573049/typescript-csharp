// End-to-end server scenarios over the framed LSP transport (no Go counterpart file:
// these are the C#-side regression scenarios from the stdio skeleton era, re-driven
// through the ported lsptestutil client against the full server.go port).

using System.Collections.Generic;
using System.Text.Json;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerReplayTests
{
    private static (LSPClient Client, System.Func<System.Exception?> Close, JsonElement Capabilities) Start(Dictionary<string, string> files)
    {
        var (client, closeClient) = LSPClient.NewLSPClient(
            ServerTestUtil.Options("/src", ServerTestUtil.WrapMapFS(files)),
            ServerTestUtil.OnServerRequest(null, acceptWorkDoneProgressCreate: true));
        var (initMsg, _, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.InitializeInfo, new LsProtoT.InitializeParams
        {
            Capabilities = new LsProtoT.ClientCapabilities(),
        });
        Assert.True(ok && initMsg!.AsResponse().Error is null, "Initialize failed");
        JsonElement capabilities = initMsg!.AsResponse().Result!.Value.GetProperty("capabilities").Clone();
        LSPClient.SendNotification(client, LsProtoT.Infos.InitializedInfo, new LsProtoT.InitializedParams());
        client.Server.InitComplete().Wait(TestContext.Current.CancellationToken);
        return (client, closeClient, capabilities);
    }

    private static LsProtoT.Diagnostic[] PullDiagnostics(LSPClient client, LsProtoT.DocumentUri uri)
    {
        var (msg, report, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentDiagnosticInfo, new LsProtoT.DocumentDiagnosticParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = uri },
        });
        Assert.True(ok, "expected a response");
        Assert.Null(msg!.AsResponse().Error);
        Assert.NotNull(report?.FullDocumentDiagnosticReport);
        return report.FullDocumentDiagnosticReport.Items;
    }

    private static bool HasCode(LsProtoT.Diagnostic diagnostic, int code) =>
        diagnostic.Code?.Integer == code;

    [Fact]
    public void StdioReplayInitializeOpenDiagnosticsAndHover()
    {
        const string source = "const x: number = \"oops\";\nx;\n";
        var (client, closeClient, capabilities) = Start(new Dictionary<string, string>
        {
            ["/src/main.ts"] = source,
        });
        try
        {
            Assert.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString());
            Assert.True(capabilities.GetProperty("textDocumentSync").GetProperty("openClose").GetBoolean());
            Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
            Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());

            var uri = new LsProtoT.DocumentUri("file:///src/main.ts");
            ServerTestUtil.DidOpen(client, uri, source);

            Assert.Contains(PullDiagnostics(client, uri), diagnostic =>
                HasCode(diagnostic, 2322) &&
                (diagnostic.Message.String ?? diagnostic.Message.MarkupContent?.Value ?? "").Contains("not assignable", System.StringComparison.Ordinal));

            var (hoverMsg, _, hoverOk) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentHoverInfo, new LsProtoT.HoverParams
            {
                TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = uri },
                Position = new LsProtoT.Position { Line = 0, Character = 6 },
            });
            Assert.True(hoverOk, "expected a response");
            Assert.Null(hoverMsg!.AsResponse().Error);
            Assert.True(hoverMsg.AsResponse().Result?.ValueKind is null or JsonValueKind.Null or JsonValueKind.Object);
        }
        finally
        {
            Assert.Null(closeClient());
        }
    }

    [Fact]
    public void IncrementalDidChangeAppliesUtf16RangeBeforeRechecking()
    {
        // The emoji occupies two UTF-16 code units, so the numeric literal starts at
        // LSP character 34 (but Unicode-scalar character 33). This catches accidental
        // rune/code-point indexing in the didChange overlay path.
        const string source = "const s = \"😀\"; const n: number = 1;\n";
        var (client, closeClient, _) = Start(new Dictionary<string, string>
        {
            ["/src/change.ts"] = source,
        });
        try
        {
            var uri = new LsProtoT.DocumentUri("file:///src/change.ts");
            ServerTestUtil.DidOpen(client, uri, source);
            LSPClient.SendNotification(client, LsProtoT.Infos.TextDocumentDidChangeInfo, new LsProtoT.DidChangeTextDocumentParams
            {
                TextDocument = new LsProtoT.VersionedTextDocumentIdentifier { Uri = uri, Version = 2 },
                ContentChanges =
                [
                    LsProtoT.TextDocumentContentChangePartialOrWholeDocument.FromPartial(new LsProtoT.TextDocumentContentChangePartial
                    {
                        Range = new LsProtoT.Range
                        {
                            Start = new LsProtoT.Position { Line = 0, Character = 34 },
                            End = new LsProtoT.Position { Line = 0, Character = 35 },
                        },
                        RangeLength = 1,
                        Text = "\"bad\"",
                    }),
                ],
            });

            Assert.Contains(PullDiagnostics(client, uri), diagnostic => HasCode(diagnostic, 2322));
        }
        finally
        {
            Assert.Null(closeClient());
        }
    }
}
