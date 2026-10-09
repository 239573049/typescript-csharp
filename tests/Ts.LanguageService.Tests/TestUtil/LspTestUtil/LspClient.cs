// Ported from tsgo internal/testutil/lsptestutil/lspclient.go (whole file).
//
// PORT-NOTEs:
//  * Go `io.Pipe()` → InMemoryPipe below (a blocking in-process byte pipe: reads block
//    until data arrives or the write side closes, after which they report EOF).
//  * Go `t.Context()` → TestContext.Current.CancellationToken; `t.Fatalf` → Assert.Fail.
//  * errgroup.WithContext → a linked CancellationTokenSource plus a first-error slot (the
//    same shape as Server.Run).
//  * Go channels of *ResponseMessage → TaskCompletionSource<ResponseMessage?>.
//  * lsp.ServerOptions is a Go value parameter (copied); the C# ServerOptions is a class,
//    so NewLSPClient assigns In/Out on the caller's instance.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

using Id = global::Ts.JsonRpc.Id;
using LsMessage = global::Ts.LsProto.Message;
using RequestMessage = global::Ts.LsProto.RequestMessage;
using ResponseMessage = global::Ts.LsProto.ResponseMessage;

namespace Ts.TestUtil.LspTestUtil;

// lspclient.go:20-46 — clientTransport wires a test client to a server using real LSP
// "Content-Length"-framed JSON streamed over byte pipes, exactly like
// communication with a real editor. This exercises the full marshal/unmarshal
// round-trip of every protocol data structure. The two directions are:
//
//	client --(clientOut)--> serverIn --> server
//	server --(serverOut)--> clientIn --> client
internal sealed class ClientTransport
{
    public required global::Ts.Lsp.Reader ServerIn { get; init; } // server reads client->server messages
    public required global::Ts.Lsp.Writer ServerOut { get; init; } // server writes server->client messages
    public required global::Ts.Lsp.Reader ClientIn { get; init; } // client reads server->client messages
    public required global::Ts.Lsp.Writer ClientOut { get; init; } // client writes client->server messages
    public required Action CloseClientOut { get; init; } // closes the client->server direction
    public required Action CloseServerOut { get; init; } // closes the server->client direction

    public static ClientTransport New()
    {
        var clientToServer = new InMemoryPipe();
        var serverToClient = new InMemoryPipe();
        return new ClientTransport
        {
            ServerIn = global::Ts.Lsp.LspStreams.ToReader(clientToServer.Reader),
            ServerOut = global::Ts.Lsp.LspStreams.ToWriter(serverToClient.Writer),
            ClientIn = global::Ts.Lsp.LspStreams.ToReader(serverToClient.Reader),
            ClientOut = global::Ts.Lsp.LspStreams.ToWriter(clientToServer.Writer),
            CloseClientOut = clientToServer.CloseWriter,
            CloseServerOut = serverToClient.CloseWriter,
        };
    }
}

// Go io.Pipe stand-in: an unbounded in-process byte pipe. Writes never block; reads block
// until bytes are available, and return 0 (EOF) once the writer is closed and drained.
// Writing after CloseWriter fails with IOException (Go: io.ErrClosedPipe).
internal sealed class InMemoryPipe
{
    private readonly object gate = new();
    private readonly Queue<byte[]> chunks = new();
    private byte[]? current;
    private int currentOffset;
    private bool closed;

    public InMemoryPipe()
    {
        Reader = new ReadSide(this);
        Writer = new WriteSide(this);
    }

    public Stream Reader { get; }

    public Stream Writer { get; }

    public void CloseWriter()
    {
        lock (gate)
        {
            closed = true;
            Monitor.PulseAll(gate);
        }
    }

    private void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }
        lock (gate)
        {
            if (closed)
            {
                throw new IOException("io: read/write on closed pipe");
            }
            chunks.Enqueue(buffer.ToArray());
            Monitor.PulseAll(gate);
        }
    }

    private int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }
        lock (gate)
        {
            while (true)
            {
                if (current is not null && currentOffset < current.Length)
                {
                    int n = Math.Min(buffer.Length, current.Length - currentOffset);
                    current.AsSpan(currentOffset, n).CopyTo(buffer);
                    currentOffset += n;
                    return n;
                }
                if (chunks.Count > 0)
                {
                    current = chunks.Dequeue();
                    currentOffset = 0;
                    continue;
                }
                if (closed)
                {
                    return 0;
                }
                Monitor.Wait(gate);
            }
        }
    }

    private sealed class ReadSide(InMemoryPipe pipe) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => pipe.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => pipe.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WriteSide(InMemoryPipe pipe) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => pipe.Write(buffer);
    }
}

// lspclient.go:48-49 — ServerRequestHandler handles server-initiated requests and returns the response to send back.
public delegate ResponseMessage? ServerRequestHandler(CancellationToken ctx, RequestMessage req);

// lspclient.go:51-52 — ServerNotificationHandler handles server-initiated notifications (e.g., $/progress).
public delegate void ServerNotificationHandler(CancellationToken ctx, RequestMessage req);

// lspclient.go:54-80 — LSPClient provides infrastructure for communicating with an LSP server in tests.
public sealed class LSPClient
{
    public global::Ts.Lsp.Server Server { get; }
    private readonly global::Ts.Lsp.Writer inputWriter;
    private readonly global::Ts.Lsp.Reader outputReader;
    private int id;
    private readonly CancellationToken ctx;

    // inputWriterMu serializes writes to the server. The test goroutine (sending
    // requests/notifications) and the MessageRouter goroutine (sending responses
    // to server-initiated requests) both write to the same stream; a single
    // message is written as multiple underlying writes (header, body, flush), so
    // concurrent writers must not interleave.
    private readonly Lock inputWriterMu = new();

    // OnServerRequest handles server-initiated requests (e.g., workspace/configuration).
    // If nil, all server requests receive a MethodNotFound error.
    private readonly ServerRequestHandler? onServerRequest;

    // OnServerNotification handles server-initiated notifications (e.g., $/progress).
    // If nil, notifications are ignored.
    public ServerNotificationHandler? OnServerNotification { get; set; }

    // Async message handling
    private readonly Dictionary<Id, TaskCompletionSource<ResponseMessage?>> pendingRequests = [];
    private readonly Lock pendingRequestsMu = new();

    private LSPClient(
        global::Ts.Lsp.Server server,
        global::Ts.Lsp.Writer inputWriter,
        global::Ts.Lsp.Reader outputReader,
        ServerRequestHandler? onServerRequest,
        CancellationToken ctx)
    {
        Server = server;
        this.inputWriter = inputWriter;
        this.outputReader = outputReader;
        this.onServerRequest = onServerRequest;
        this.ctx = ctx;
    }

    // lspclient.go:82-87 — writeToServer writes a message to the server, serializing concurrent writers.
    private Exception? WriteToServer(LsMessage msg)
    {
        lock (inputWriterMu)
        {
            return inputWriter.Write(msg);
        }
    }

    // lspclient.go:89-127 — NewLSPClient creates an LSPClient wrapping the given server and pipes.
    public static (LSPClient Client, Func<Exception?> Close) NewLSPClient(global::Ts.Lsp.ServerOptions serverOpts, ServerRequestHandler? onServerRequest)
    {
        ClientTransport transport = ClientTransport.New();
        serverOpts.In = transport.ServerIn;
        serverOpts.Out = transport.ServerOut;

        global::Ts.Lsp.Server server = global::Ts.Lsp.Server.NewServer(serverOpts);

        // Go: ctx, cancel := context.WithCancel(t.Context()); g, ctx := errgroup.WithContext(ctx)
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var group = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        CancellationToken ctx = group.Token;
        Exception? firstErr = null;
        var errMu = new Lock();
        void GroupFail(Exception? err)
        {
            if (err is null)
            {
                return;
            }
            lock (errMu)
            {
                firstErr ??= err;
            }
            group.Cancel();
        }

        var client = new LSPClient(server, transport.ClientOut, transport.ClientIn, onServerRequest, ctx);

        // Start server goroutine
        Task serverTask = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    GroupFail(server.Run(ctx));
                }
                finally
                {
                    transport.CloseServerOut();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Start async message router
        Task routerTask = Task.Factory.StartNew(
            () => GroupFail(client.MessageRouter(ctx)),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return (client, () =>
        {
            cancel.Cancel();
            transport.CloseClientOut();
            Task.WaitAll(serverTask, routerTask);
            Exception? err;
            lock (errMu)
            {
                err = firstErr;
            }
            if (err is not null && !global::Ts.LsProto.LspErrors.IsCanceled(err))
            {
                return err;
            }
            return null;
        });
    }

    // lspclient.go:129-134 — NextID returns the next request ID.
    public int NextID()
    {
        int next = id;
        id++;
        return next;
    }

    // lspclient.go:136-181 — MessageRouter runs in a goroutine and routes incoming messages from the server.
    // It handles responses to client requests and server-initiated requests.
    // It continues draining the output channel until it is closed (EOF), even after
    // context cancellation, to prevent the server's writeLoop from blocking on a send.
    public Exception? MessageRouter(CancellationToken ctx)
    {
        while (true)
        {
            var (msg, err) = outputReader.Read();
            if (err is not null)
            {
                if (global::Ts.LsProto.LspErrors.IsEof(err))
                {
                    return null;
                }
                if (ctx.IsCancellationRequested)
                {
                    return null;
                }
                return new InvalidOperationException("failed to read message: " + err.Message, err);
            }

            // After context cancellation, keep draining but don't process messages.
            if (ctx.IsCancellationRequested)
            {
                continue;
            }

            // Validate message can be marshaled
            Exception? marshalErr = ValidateMarshal(msg!);
            if (marshalErr is not null)
            {
                if (ctx.IsCancellationRequested)
                {
                    continue;
                }

                return new InvalidOperationException("failed to encode message as JSON: " + marshalErr.Message, marshalErr);
            }

            switch (msg!.Kind)
            {
                case global::Ts.JsonRpc.MessageKind.Response:
                    HandleResponse(ctx, msg.AsResponse());
                    break;
                case global::Ts.JsonRpc.MessageKind.Request:
                    Exception? handleErr = HandleServerRequest(ctx, msg.AsRequest());
                    if (handleErr is not null)
                    {
                        return handleErr;
                    }
                    break;
                case global::Ts.JsonRpc.MessageKind.Notification:
                    OnServerNotification?.Invoke(ctx, msg.AsRequest());
                    break;
            }
        }
    }

    // Go: json.MarshalWrite(io.Discard, msg).
    private static Exception? ValidateMarshal(LsMessage msg)
    {
        try
        {
            using var json = new Utf8JsonWriter(Stream.Null);
            msg.WriteTo(json);
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
        {
            return e;
        }
    }

    // lspclient.go:183-203 — handleResponse routes a response message to the waiting request goroutine.
    private void HandleResponse(CancellationToken ctx, ResponseMessage resp)
    {
        if (resp.Id is null)
        {
            return;
        }

        TaskCompletionSource<ResponseMessage?>? respChan;
        bool ok;
        lock (pendingRequestsMu)
        {
            ok = pendingRequests.Remove(resp.Id, out respChan);
        }

        if (ok)
        {
            // Go: select { case respChan <- resp: case <-ctx.Done(): } — the channel is
            // buffered (cap 1), so the send never blocks.
            respChan!.TrySetResult(resp);
        }
    }

    // lspclient.go:205-237 — handleServerRequest handles requests initiated by the server (e.g., workspace/configuration).
    private Exception? HandleServerRequest(CancellationToken ctx, RequestMessage req)
    {
        ResponseMessage? response = null;

        if (onServerRequest is not null)
        {
            response = onServerRequest(ctx, req);
        }

        // Default: unknown server request
        response ??= new ResponseMessage
        {
            Id = req.Id,
            Error = new global::Ts.JsonRpc.ResponseError
            {
                Code = (int)global::Ts.LsProto.ErrorCode.MethodNotFound,
                Message = $"Unknown method: {req.Method.Value}",
            },
        };

        // Send response back to server
        if (ctx.IsCancellationRequested)
        {
            return null;
        }

        Exception? err = WriteToServer(response.Message());
        if (err is not null)
        {
            if (ctx.IsCancellationRequested)
            {
                return null;
            }
            return new InvalidOperationException("failed to write server request response: " + err.Message, err);
        }
        return null;
    }

    // lspclient.go:239-246 — WriteMsg validates and sends a message to the server.
    // This is an untyped low-level method; prefer SendRequest and SendNotification for typed interactions.
    public void WriteMsg(LsMessage msg)
    {
        Exception? marshalErr = ValidateMarshal(msg);
        Assert.True(marshalErr is null, "failed to encode message as JSON");
        Exception? err = WriteToServer(msg);
        if (err is not null)
        {
            Assert.Fail($"failed to write message: {err.Message}");
        }
    }

    // lspclient.go:248-262 — SendRequest sends a typed request and waits for the response.
    public static (LsMessage? Message, TResp Result, bool Ok) SendRequest<TParams, TResp>(LSPClient c, global::Ts.LsProto.RequestInfo<TParams, TResp> info, TParams @params)
    {
        int nextId = c.NextID();
        Id reqID = global::Ts.LsProto.LsProtoNewId.NewId(global::Ts.LsProto.IntegerOrString.FromInteger(nextId));
        RequestMessage req = RequestMessage.New(info, reqID, @params);

        var (resp, ok) = c.SendRequestWorker(req, reqID);
        if (!ok)
        {
            return (null, default!, false);
        }
        // The result arrives as a raw json.Value; decode it into Resp.
        var (result, err) = UnmarshalResult(info, resp!);
        return (resp!.Message(), result, err is null);
    }

    // lspclient.go:264-280 — SendRequestAsync sends a typed request and returns a waiter for its response.
    public static Func<(LsMessage? Message, TResp Result, bool Ok)> SendRequestAsync<TParams, TResp>(LSPClient c, global::Ts.LsProto.RequestInfo<TParams, TResp> info, TParams @params)
    {
        int nextId = c.NextID();
        Id reqID = global::Ts.LsProto.LsProtoNewId.NewId(global::Ts.LsProto.IntegerOrString.FromInteger(nextId));
        RequestMessage req = RequestMessage.New(info, reqID, @params);

        TaskCompletionSource<ResponseMessage?> responseChan = c.StartRequestWorker(req, reqID);
        return () =>
        {
            var (resp, ok) = c.WaitForResponse(reqID, responseChan);
            if (!ok)
            {
                return (null, default!, false);
            }
            var (result, err) = UnmarshalResult(info, resp!);
            return (resp!.Message(), result, err is null);
        };
    }

    // Go: info.UnmarshalResult(resp.Result) — (Resp, error).
    private static (TResp Result, Exception? Error) UnmarshalResult<TParams, TResp>(global::Ts.LsProto.RequestInfo<TParams, TResp> info, ResponseMessage resp)
    {
        try
        {
            return (global::Ts.LsProto.LsProtoUnmarshal.UnmarshalResult(resp.Result, info.ResultTypeInfo), null);
        }
        catch (global::Ts.LsProto.LspErrorException e)
        {
            return (default!, e);
        }
    }

    // lspclient.go:282-286 — This is an untyped version of SendRequest. Prefer to use SendRequest when possible.
    public (ResponseMessage? Response, bool Ok) SendRequestWorker(RequestMessage req, Id reqID)
    {
        TaskCompletionSource<ResponseMessage?> responseChan = StartRequestWorker(req, reqID);
        return WaitForResponse(reqID, responseChan);
    }

    // lspclient.go:288-296 — startRequestWorker.
    private TaskCompletionSource<ResponseMessage?> StartRequestWorker(RequestMessage req, Id reqID)
    {
        var responseChan = new TaskCompletionSource<ResponseMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingRequestsMu)
        {
            pendingRequests[reqID] = responseChan;
        }

        WriteMsg(req.Message());
        return responseChan;
    }

    // lspclient.go:298-316 — waitForResponse.
    private (ResponseMessage? Response, bool Ok) WaitForResponse(Id reqID, TaskCompletionSource<ResponseMessage?> responseChan)
    {
        CancellationToken testCtx = TestContext.Current.CancellationToken;
        ResponseMessage? resp;
        try
        {
            responseChan.Task.Wait(testCtx);
            resp = responseChan.Task.Result;
        }
        catch (OperationCanceledException e)
        {
            lock (pendingRequestsMu)
            {
                pendingRequests.Remove(reqID);
            }
            Assert.Fail($"Request cancelled: {e.Message}");
            return (null, false);
        }
        if (resp is null)
        {
            return (null, false);
        }

        return (resp, true);
    }

    // lspclient.go:318-324 — SendNotification sends a typed notification.
    public static void SendNotification<TParams>(LSPClient c, global::Ts.LsProto.NotificationInfo<TParams> info, TParams @params)
    {
        RequestMessage notification = RequestMessage.New(info, @params);
        c.WriteMsg(notification.Message());
    }

    // lspclient.go:326-328 — SetCompilerOptionsForInferredProjects.
    public void SetCompilerOptionsForInferredProjects(global::Ts.Core.CompilerOptions options)
    {
        Server.SetCompilerOptionsForInferredProjects(new global::Ts.Core.RequestContext { Cancellation = ctx }, options);
    }
}
