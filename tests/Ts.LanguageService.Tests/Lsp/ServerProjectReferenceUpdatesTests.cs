// Ported from tsgo internal/lsp/server_projectreference_updates_test.go (whole file).
//
// PORT-NOTE: Go's `if !bundled.Embedded { t.Skip(...) }` guards are omitted: Bundled.Embedded
// is `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).

using System;
using System.Collections.Generic;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerProjectReferenceUpdatesTests : IDisposable
{
    // Go: t.Cleanup(func() { _ = closeClient() }).
    private readonly List<Func<Exception?>> cleanups = [];

    public void Dispose()
    {
        foreach (var closeClient in cleanups)
        {
            _ = closeClient();
        }
    }

    // server_projectreference_updates_test.go:19-65 — initMutableLSPClient.
    private (LSPClient Client, global::Ts.Vfs.VfsTest.MapFS FS) InitMutableLSPClient(Dictionary<string, string> files, System.Text.Json.JsonElement prefs)
    {
        global::Ts.Vfs.IFileSystem @base = global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(files, false);
        var baseFS = (global::Ts.Vfs.VfsTest.MapFS)((global::Ts.Vfs.IoVfs.IFsWithSys)@base).FSys();
        global::Ts.Vfs.IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(@base);

        var (client, closeClient) = LSPClient.NewLSPClient(
            ServerTestUtil.Options("/root", fs),
            ServerTestUtil.OnServerRequest(prefs, acceptWorkDoneProgressCreate: false));
        cleanups.Add(closeClient);

        ServerTestUtil.Initialize(client, new LsProtoT.ClientCapabilities());

        LSPClient.SendNotification(client, LsProtoT.Infos.WorkspaceDidChangeConfigurationInfo, new LsProtoT.DidChangeConfigurationParams
        {
            Settings = ServerTestUtil.Json("{\"typescript\":" + prefs.GetRawText() + "}"),
        });

        return (client, baseFS);
    }

    // server_projectreference_updates_test.go:67-131 — TestReferencesAfterAncestorProjectConfigDeletion1.
    [Fact]
    public void TestReferencesAfterAncestorProjectConfigDeletion1()
    {
        var (client, fs) = InitMutableLSPClient(new Dictionary<string, string>
        {
            ["/root/tsconfig.json"] = """
                {
                			"files": [],
                			"references": [{ "path": "./project" }]
                		}
                """,
            ["/root/project/tsconfig.json"] = """
                {
                			"compilerOptions": { "composite": true },
                			"include": ["src/**/*.ts"]
                		}
                """,
            ["/root/project/src/main.ts"] = "export function helloWorld() {}\nhelloWorld()\n",
        }, ServerTestUtil.EmptyPrefsConfig);

        LsProtoT.DocumentUri mainURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/root/project/src/main.ts");
        ServerTestUtil.DidOpen(client, mainURI, "export function helloWorld() {}\nhelloWorld()\n");

        // Prime the child project so opening a file creates the ancestor configured-project placeholder.
        var (msg, _, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentDocumentSymbolInfo, new LsProtoT.DocumentSymbolParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = mainURI },
        });
        Assert.True(ok, "expected response");
        Assert.Null(msg!.AsResponse().Error);

        fs.Remove("root/tsconfig.json");
        LSPClient.SendNotification(client, LsProtoT.Infos.WorkspaceDidChangeWatchedFilesInfo, new LsProtoT.DidChangeWatchedFilesParams
        {
            Changes =
            [
                new LsProtoT.FileEvent
                {
                    Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI("/root/tsconfig.json"),
                    Type = LsProtoT.FileChangeType.Deleted,
                },
            ],
        });

        var (refMsg, resp, refOk) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentReferencesInfo, new LsProtoT.ReferenceParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = mainURI },
            Position = new LsProtoT.Position { Line = 1, Character = 3 },
            Context = new LsProtoT.ReferenceContext { IncludeDeclaration = true },
        });
        Assert.True(refOk, "expected response");
        Assert.Null(refMsg!.AsResponse().Error);
        Assert.NotNull(resp?.Locations);
        Assert.Equal(2, resp.Locations.Length);
        Assert.Equal(
            [
                (mainURI.Value, 0u, 16u, 0u, 26u),
                (mainURI.Value, 1u, 0u, 1u, 10u),
            ],
            Array.ConvertAll(resp.Locations, l => (l.Uri.Value, l.Range.Start.Line, l.Range.Start.Character, l.Range.End.Line, l.Range.End.Character)));
    }
}
