// Ported from tsgo internal/lsp/server_completion_test.go (whole file).

using System.Collections.Generic;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerCompletionTests : System.IDisposable
{
    // Go: t.Cleanup(func() { _ = closeClient() }).
    private readonly List<System.Func<System.Exception?>> cleanups = [];

    public void Dispose()
    {
        foreach (var closeClient in cleanups)
        {
            _ = closeClient();
        }
    }

    // server_completion_test.go:19-63 — initCompletionClient.
    private LSPClient InitCompletionClient(Dictionary<string, string> files, System.Text.Json.JsonElement prefs)
    {
        global::Ts.Vfs.IFileSystem fs = ServerTestUtil.WrapMapFS(files);

        var (client, closeClient) = LSPClient.NewLSPClient(
            ServerTestUtil.Options("/home/projects", fs),
            ServerTestUtil.OnServerRequest(prefs, acceptWorkDoneProgressCreate: false));
        cleanups.Add(closeClient);

        ServerTestUtil.Initialize(client, new LsProtoT.ClientCapabilities());

        LSPClient.SendNotification(client, LsProtoT.Infos.WorkspaceDidChangeConfigurationInfo, new LsProtoT.DidChangeConfigurationParams
        {
            Settings = ServerTestUtil.Json("{\"typescript\":" + prefs.GetRawText() + "}"),
        });

        return client;
    }

    // server_completion_test.go:65-73 — completionItems.
    private static LsProtoT.CompletionItem[]? CompletionItems(LsProtoT.CompletionItemsOrListOrNull? resp)
    {
        if (resp?.List is not null)
        {
            return resp.List.Items;
        }
        if (resp?.Items is not null)
        {
            return resp.Items;
        }
        return null;
    }

    // server_completion_test.go:75-82 — findCompletionItem.
    private static LsProtoT.CompletionItem? FindCompletionItem(LsProtoT.CompletionItem[]? items, string label)
    {
        foreach (LsProtoT.CompletionItem item in items ?? [])
        {
            if (item.Label == label)
            {
                return item;
            }
        }
        return null;
    }

    private static Dictionary<string, string> AutoImportFiles(string bName, string bText) => new()
    {
        ["/home/projects/tsconfig.json"] = """{"compilerOptions": {"module": "esnext", "target": "esnext"}}""",
        ["/home/projects/a.ts"] = "export const someVar = 10;",
        [bName] = bText,
    };

    private static void AssertAutoImportSomeVar(LsProtoT.Message? msg, LsProtoT.CompletionItemsOrListOrNull? resp, bool ok)
    {
        Assert.True(ok, "expected a response");
        Assert.Null(msg!.AsResponse().Error);
        LsProtoT.CompletionItem? item = FindCompletionItem(CompletionItems(resp), "someVar");
        Assert.NotNull(item);
        Assert.True(item.Data is not null && item.Data.AutoImport is not null);
        Assert.Equal("./a", item.Data.AutoImport.ModuleSpecifier);
    }

    // server_completion_test.go:84-127 — Verifies that completion succeeds on a file that was already closed
    // by the time the server processes the completion request.
    [Fact]
    public void TestCompletionAfterFileClose()
    {
        if (!global::Ts.Bundled.Bundled.Embedded)
        {
            Assert.Skip("bundled files are not embedded");
        }

        LSPClient client = InitCompletionClient(AutoImportFiles("/home/projects/b.ts", "s"), ServerTestUtil.AutoImportPrefsConfig);

        LsProtoT.DocumentUri aURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/a.ts");
        LsProtoT.DocumentUri bURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/b.ts");
        ServerTestUtil.DidOpen(client, aURI, "export const someVar = 10;");
        ServerTestUtil.DidOpen(client, bURI, "s");

        LSPClient.SendNotification(client, LsProtoT.Infos.TextDocumentDidCloseInfo, new LsProtoT.DidCloseTextDocumentParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = bURI },
        });

        var (msg, resp, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentCompletionInfo, new LsProtoT.CompletionParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = bURI },
            Position = new LsProtoT.Position { Line = 0, Character = 1 },
            Context = new LsProtoT.CompletionContext(),
        });
        AssertAutoImportSomeVar(msg, resp, ok);
    }

    // server_completion_test.go:129-172 — Completion request is enqueued first, then a close notification is sent.
    // This guarantees the completion enters the input channel before the close.
    [Fact]
    public void TestCompletionWithConcurrentFileClose()
    {
        if (!global::Ts.Bundled.Bundled.Embedded)
        {
            Assert.Skip("bundled files are not embedded");
        }

        LSPClient client = InitCompletionClient(AutoImportFiles("/home/projects/b.ts", "s"), ServerTestUtil.AutoImportPrefsConfig);

        LsProtoT.DocumentUri aURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/a.ts");
        LsProtoT.DocumentUri bURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/b.ts");
        ServerTestUtil.DidOpen(client, aURI, "export const someVar = 10;");
        ServerTestUtil.DidOpen(client, bURI, "s");

        var waitForCompletion = LSPClient.SendRequestAsync(client, LsProtoT.Infos.TextDocumentCompletionInfo, new LsProtoT.CompletionParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = bURI },
            Position = new LsProtoT.Position { Line = 0, Character = 1 },
            Context = new LsProtoT.CompletionContext(),
        });

        LSPClient.SendNotification(client, LsProtoT.Infos.TextDocumentDidCloseInfo, new LsProtoT.DidCloseTextDocumentParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = bURI },
        });

        var (msg, resp, ok) = waitForCompletion();
        AssertAutoImportSomeVar(msg, resp, ok);
    }

    // server_completion_test.go:174-196 — TestCompletionForUnopenedFile.
    [Fact]
    public void TestCompletionForUnopenedFile()
    {
        if (!global::Ts.Bundled.Bundled.Embedded)
        {
            Assert.Skip("bundled files are not embedded");
        }

        LSPClient client = InitCompletionClient(new Dictionary<string, string>
        {
            ["/home/projects/tsconfig.json"] = """{"compilerOptions": {"module": "esnext", "target": "esnext"}}""",
            ["/home/projects/c.ts"] = "let xyz = 1;\nxy",
        }, ServerTestUtil.EmptyPrefsConfig);

        LsProtoT.DocumentUri cURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/c.ts");
        var (msg, resp, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentCompletionInfo, new LsProtoT.CompletionParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = cURI },
            Position = new LsProtoT.Position { Line = 1, Character = 2 },
            Context = new LsProtoT.CompletionContext(),
        });
        Assert.True(ok, "expected a response");
        Assert.Null(msg!.AsResponse().Error);
        Assert.NotNull(FindCompletionItem(CompletionItems(resp), "xyz"));
    }

    // server_completion_test.go:198-229 — TestAutoImportCompletionForUnopenedFile.
    [Fact]
    public void TestAutoImportCompletionForUnopenedFile()
    {
        if (!global::Ts.Bundled.Bundled.Embedded)
        {
            Assert.Skip("bundled files are not embedded");
        }

        LSPClient client = InitCompletionClient(AutoImportFiles("/home/projects/c.ts", "s"), ServerTestUtil.AutoImportPrefsConfig);

        LsProtoT.DocumentUri cURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/c.ts");
        var (msg, resp, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentCompletionInfo, new LsProtoT.CompletionParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = cURI },
            Position = new LsProtoT.Position { Line = 0, Character = 1 },
            Context = new LsProtoT.CompletionContext(),
        });
        AssertAutoImportSomeVar(msg, resp, ok);
    }

    // server_completion_test.go:231-281 — TestCompletionSnapshotFreezing verifies that the auto-import retry uses the
    // snapshot captured in the sync phase, not a newer one that includes a
    // concurrent DidChange. Without snapshot freezing the retry would flush the
    // pending change, making position/prefix inconsistent with the request.
    [Fact]
    public void TestCompletionSnapshotFreezing()
    {
        if (!global::Ts.Bundled.Bundled.Embedded)
        {
            Assert.Skip("bundled files are not embedded");
        }

        LSPClient client = InitCompletionClient(AutoImportFiles("/home/projects/b.ts", "someV"), ServerTestUtil.AutoImportPrefsConfig);

        LsProtoT.DocumentUri aURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/a.ts");
        LsProtoT.DocumentUri bURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/home/projects/b.ts");
        ServerTestUtil.DidOpen(client, aURI, "export const someVar = 10;");
        ServerTestUtil.DidOpen(client, bURI, "someV");

        var waitForCompletion = LSPClient.SendRequestAsync(client, LsProtoT.Infos.TextDocumentCompletionInfo, new LsProtoT.CompletionParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = bURI },
            Position = new LsProtoT.Position { Line = 0, Character = 5 },
            Context = new LsProtoT.CompletionContext(),
        });

        LSPClient.SendNotification(client, LsProtoT.Infos.TextDocumentDidChangeInfo, new LsProtoT.DidChangeTextDocumentParams
        {
            TextDocument = new LsProtoT.VersionedTextDocumentIdentifier { Uri = bURI, Version = 2 },
            ContentChanges =
            [
                LsProtoT.TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new LsProtoT.TextDocumentContentChangeWholeDocument { Text = "notMatching" }),
            ],
        });

        var (msg, resp, ok) = waitForCompletion();
        Assert.True(ok, "expected a response");
        Assert.Null(msg!.AsResponse().Error);
        LsProtoT.CompletionItem? item = FindCompletionItem(CompletionItems(resp), "someVar");
        Assert.True(item is not null, "expected someVar in completions (snapshot freezing should preserve original content)");
        Assert.True(item.Data is not null && item.Data.AutoImport is not null);
        Assert.Equal("./a", item.Data.AutoImport.ModuleSpecifier);
    }
}
