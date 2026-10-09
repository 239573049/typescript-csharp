// Ported from tsgo internal/lsp/server_progress_test.go (whole file).
//
// PORT-NOTE: Go's `if !bundled.Embedded { t.Skip(...) }` guards are omitted: Bundled.Embedded
// is `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerProgressTests
{
    // server_progress_test.go:17-127 — TestProgressNotificationsEndToEnd.
    [Fact]
    public async Task TestProgressNotificationsEndToEnd()
    {
        global::Ts.Vfs.IFileSystem fs = ServerTestUtil.WrapMapFS(new Dictionary<string, string>
        {
            ["/home/projects/tsconfig.json"] = "{}",
            ["/home/projects/index.ts"] = "export const x = 1;",
        });

        // Collect $/progress notifications. Signal when "end" arrives.
        var mu = new Lock();
        var progressNotifications = new List<LsProtoT.ProgressParams>();
        var endReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (client, closeClient) = LSPClient.NewLSPClient(
            ServerTestUtil.Options("/home/projects", fs),
            ServerTestUtil.OnServerRequest(null, acceptWorkDoneProgressCreate: true));
        try
        {
            client.OnServerNotification = (_, req) =>
            {
                if (req.Method == LsProtoT.Methods.Progress)
                {
                    LsProtoT.ProgressParams? @params;
                    try
                    {
                        @params = LsProtoT.LsProtoUnmarshal.UnmarshalParams(req.Params, LsProtoT.Infos.ProgressInfo.ParamsTypeInfo);
                    }
                    catch (LsProtoT.LspErrorException)
                    {
                        @params = null;
                    }
                    if (@params is not null)
                    {
                        bool isEnd;
                        lock (mu)
                        {
                            progressNotifications.Add(@params);
                            isEnd = @params.Value.End is not null;
                        }
                        if (isEnd)
                        {
                            // Signaled, or already signaled.
                            endReceived.TrySetResult();
                        }
                    }
                }
            };

            ServerTestUtil.Initialize(client, new LsProtoT.ClientCapabilities
            {
                Window = new LsProtoT.WindowClientCapabilities
                {
                    WorkDoneProgress = true,
                },
            });

            var uri = new LsProtoT.DocumentUri("file:///home/projects/index.ts");
            ServerTestUtil.DidOpen(client, uri, "export const x = 1;");

            // Send a request to ensure the server has processed the didOpen and loaded the project.
            var (msg, resp, ok) = LSPClient.SendRequest(client, LsProtoT.Infos.CustomProjectInfoInfo, new LsProtoT.ProjectInfoParams
            {
                TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = uri },
            });
            Assert.True(ok, "expected a response");
            Assert.Null(msg!.AsResponse().Error);
            Assert.Equal("/home/projects/tsconfig.json", resp!.ConfigFilePath);

            // Wait for the "end" progress notification before reading.
            try
            {
                await endReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                Assert.Fail("timed out waiting for progress end notification");
            }

            List<LsProtoT.ProgressParams> notifications;
            lock (mu)
            {
                notifications = [.. progressNotifications];
            }

            Assert.True(notifications.Count >= 2, $"expected at least begin+end progress notifications, got {notifications.Count}");

            // First notification should be a "begin".
            Assert.True(notifications[0].Value.Begin is not null, "expected first progress notification to be 'begin'");
            Assert.Equal("Loading", notifications[0].Value.Begin!.Title);

            // Last notification should be an "end".
            LsProtoT.ProgressParams last = notifications[^1];
            Assert.True(last.Value.End is not null, "expected last progress notification to be 'end'");

            // All notifications should share the same token.
            string firstToken = TokenString(notifications[0].Token);
            Assert.True(firstToken != "", "expected non-empty progress token");
            for (int i = 0; i < notifications.Count; i++)
            {
                Assert.True(TokenString(notifications[i].Token) == firstToken, $"notification {i} has different token");
            }
        }
        finally
        {
            Assert.Null(closeClient());
        }
    }

    // server_progress_test.go:129-134 — tokenString.
    private static string TokenString(LsProtoT.IntegerOrString t)
    {
        if (t.String is not null)
        {
            return t.String;
        }
        return "";
    }
}
