// Ported from tsgo internal/lsp/server_projectinfo_test.go (whole file).
//
// PORT-NOTE: Go's `if !bundled.Embedded { t.Skip(...) }` guards are omitted: Bundled.Embedded
// is `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).

using System;
using System.Collections.Generic;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerProjectInfoTests : IDisposable
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

    // server_projectinfo_test.go:16-50 — initProjectInfoClient.
    private LSPClient InitProjectInfoClient(Dictionary<string, string> files)
    {
        global::Ts.Vfs.IFileSystem fs = ServerTestUtil.WrapMapFS(files);

        var (client, closeClient) = LSPClient.NewLSPClient(
            ServerTestUtil.Options("/home/projects", fs),
            ServerTestUtil.OnServerRequest(null, acceptWorkDoneProgressCreate: true));
        cleanups.Add(closeClient);

        ServerTestUtil.Initialize(client, new LsProtoT.ClientCapabilities());

        return client;
    }

    private static string RequestConfigFilePath(LSPClient client, LsProtoT.DocumentUri uri)
    {
        ServerTestUtil.DidOpen(client, uri, "export const x = 1;");

        var (msg, resp, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.CustomProjectInfoInfo, new LsProtoT.ProjectInfoParams
        {
            TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = uri },
        });
        Assert.True(ok, "expected a response");
        Assert.Null(msg!.AsResponse().Error);
        return resp!.ConfigFilePath;
    }

    // server_projectinfo_test.go:52-75 — TestProjectInfoConfiguredProject.
    [Fact]
    public void TestProjectInfoConfiguredProject()
    {
        LSPClient client = InitProjectInfoClient(new Dictionary<string, string>
        {
            ["/home/projects/tsconfig.json"] = "{}",
            ["/home/projects/index.ts"] = "export const x = 1;",
        });

        var uri = new LsProtoT.DocumentUri("file:///home/projects/index.ts");
        Assert.Equal("/home/projects/tsconfig.json", RequestConfigFilePath(client, uri));
    }

    // server_projectinfo_test.go:77-99 — TestProjectInfoInferredProject.
    [Fact]
    public void TestProjectInfoInferredProject()
    {
        LSPClient client = InitProjectInfoClient(new Dictionary<string, string>
        {
            ["/home/projects/index.ts"] = "export const x = 1;",
        });

        var uri = new LsProtoT.DocumentUri("file:///home/projects/index.ts");
        Assert.Equal("", RequestConfigFilePath(client, uri));
    }
}
