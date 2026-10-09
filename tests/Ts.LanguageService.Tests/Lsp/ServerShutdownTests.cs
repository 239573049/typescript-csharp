// Ported from tsgo internal/lsp/server_shutdown_test.go (whole file).
//
// PORT-NOTE: Go's `if !bundled.Embedded { t.Skip(...) }` guards are omitted: Bundled.Embedded
// is `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

using LsMessage = global::Ts.LsProto.Message;
using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerShutdownTests
{
    // server_shutdown_test.go:14-16 — shutdownTestReader.
    private sealed class ShutdownTestReader : Reader
    {
        public (LsMessage? Message, Exception? Error) Read() => (null, LsProtoT.EofException.Instance);
    }

    // server_shutdown_test.go:18-20 — shutdownTestWriter.
    private sealed class ShutdownTestWriter : Writer
    {
        public Exception? Write(LsMessage msg) => null;
    }

    // server_shutdown_test.go:22-90 — TestServerShutdownNoDeadlock verifies that operations after shutdown
    // don't block.
    [Fact]
    public async Task TestServerShutdownNoDeadlock()
    {
        global::Ts.Vfs.IFileSystem fs = ServerTestUtil.WrapMapFS(new Dictionary<string, string>
        {
            ["/test/tsconfig.json"] = "{}",
            ["/test/index.ts"] = "const x = 1;",
        });

        Server server = Server.NewServer(new ServerOptions
        {
            In = new ShutdownTestReader(),
            Out = new ShutdownTestWriter(),
            Err = System.IO.TextWriter.Null,
            Cwd = "/test",
            FS = fs,
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
        });

        using var cancel = new CancellationTokenSource();
        var ctx = new global::Ts.Core.RequestContext { Cancellation = cancel.Token };
        server.backgroundCtx = ctx;

        // Start write loop to drain queue
        Task writeLoopDone = Task.Factory.StartNew(
            () => { _ = server.WriteLoop(cancel.Token); },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Create session with the server's lifecycle context
        Volatile.Write(ref server.initStarted, 1);
        server.session = global::Ts.Project.Session.NewSession(new global::Ts.Project.SessionInit
        {
            BackgroundCtx = ctx,
            Options = new global::Ts.Project.SessionOptions
            {
                CurrentDirectory = "/test",
                DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
                PositionEncoding = LsProtoT.PositionEncodingKind.UTF8,
                WatchEnabled = false,
                LoggingEnabled = true,
            },
            FS = fs,
            Logger = server.logger,
        });

        // Open a file to establish a project
        server.session.DidOpenFile(ctx, new LsProtoT.DocumentUri("file:///test/index.ts"), 1, "const x = 1;", LsProtoT.LanguageKind.TypeScript.Value!);
        server.session.WaitForBackgroundTasks();

        // Shutdown (cancel context and wait for write loop to exit)
        await cancel.CancelAsync();
        await writeLoopDone;

        // Trigger operations that would log (these should not block)
        server.session.DidChangeFile(ctx, new LsProtoT.DocumentUri("file:///test/index.ts"), 2,
        [
            LsProtoT.TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new LsProtoT.TextDocumentContentChangeWholeDocument
            {
                Text = "const x = 2;",
            }),
        ]);
        _ = server.session.GetLanguageService(ctx, new LsProtoT.DocumentUri("file:///test/index.ts"));
        server.session.WaitForBackgroundTasks();

        server.session.Close();
    }

    // server_shutdown_test.go:92-127 — TestServerOutgoingQueueDoesNotBlockWithoutWriter.
    [Fact]
    public async Task TestServerOutgoingQueueDoesNotBlockWithoutWriter()
    {
        Server server = Server.NewServer(new ServerOptions
        {
            In = new ShutdownTestReader(),
            Out = new ShutdownTestWriter(),
            Err = System.IO.TextWriter.Null,
            Cwd = "/test",
        });
        server.backgroundCtx = new global::Ts.Core.RequestContext { Cancellation = TestContext.Current.CancellationToken };

        LsMessage msg = LsProtoT.RequestMessage.New(LsProtoT.Infos.WindowLogMessageInfo, new LsProtoT.LogMessageParams
        {
            Type = LsProtoT.MessageType.Info,
            Message = "queued",
        }).Message();

        Task<Exception?> done = Task.Factory.StartNew<Exception?>(
            () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    if (server.Send(msg) is { } err)
                    {
                        return err;
                    }
                }
                return null;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Exception? result;
        try
        {
            result = await done.WaitAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("sending outgoing messages blocked without a writer");
            return;
        }
        if (result is not null)
        {
            Assert.Fail(result.Message);
        }
    }
}
