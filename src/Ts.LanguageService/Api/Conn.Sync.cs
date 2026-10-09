// Ported from tsgo internal/api/conn_sync.go (whole file).
//
// PORT-NOTE (concurrency): Go's sync.Mutex over all protocol operations maps to a
// C# lock; Call holds it across the write/read pair exactly like Go (conn_sync.go:173).

using System;
using System.Diagnostics;
using System.IO;
using Ts.JsonRpc;

namespace Ts.Api;

// SyncConn manages bidirectional communication with synchronous request handling.
// Requests are handled one at a time inline, and outgoing calls are serialized.
// (conn_sync.go:16-31)
public sealed class SyncConn : Conn
{
    private readonly System.IO.Stream rwc;
    private readonly Protocol protocol;
    private readonly Handler handler;

    // timing, when non-nil, accumulates the wall-clock time spent handling each
    // request. Clients retrieve the collected data via a getServerTiming request.
    private TimingCollector? timing;

    // mu serializes all protocol operations (reads and writes).
    // This ensures that concurrent calls from handler goroutines (e.g., project code
    // spawning goroutines that invoke filesystem callbacks) don't corrupt the stream.
    private readonly object mu = new();

    // NewSyncConn creates a new sync connection with the given transport and handler.
    // (conn_sync.go:34-40)
    public SyncConn(System.IO.Stream rwc, Protocol protocol, Handler handler)
    {
        this.rwc = rwc;
        this.protocol = protocol;
        this.handler = handler;
    }

    // SetCollectTiming enables or disables per-request server processing-time
    // measurement. When enabled, the connection accumulates timing that clients can
    // retrieve via a getServerTiming request. (conn_sync.go:45-51)
    public void SetCollectTiming(bool enabled)
    {
        timing = enabled ? new TimingCollector() : null;
    }

    // Run starts processing messages on the connection.
    // It blocks until the context is cancelled or an error occurs.
    // (conn_sync.go:55-81)
    public Exception? Run(global::Ts.Core.RequestContext ctx)
    {
        while (true)
        {
            if (ctx.Cancellation.IsCancellationRequested)
            {
                return new OperationCanceledException(ctx.Cancellation);
            }

            Message? msg;
            lock (mu)
            {
                (msg, bool ok) = protocol.ReadMessage();
                if (!ok)
                {
                    // Go: errors.Is(err, io.EOF) → return nil.
                    return null;
                }
            }

            if (msg!.IsRequest())
            {
                HandleRequest(ctx, msg);
            }
            else if (msg.IsNotification())
            {
                HandleNotification(ctx, msg);
            }
            else
            {
                // Responses are not expected in the main loop - they are read inline by Call().
                throw new InvalidOperationException("api: unexpected response message in sync connection");
            }
        }
    }

    // handleRequest processes an incoming request. (conn_sync.go:84-158)
    private void HandleRequest(global::Ts.Core.RequestContext ctx, Message msg)
    {
        // Intercept the meta-requests for collected server timing before dispatching
        // to the handler, so they are answered directly and not themselves recorded.
        if (msg.Method == Method.GetServerTiming.Value)
        {
            Exception? writeErr;
            lock (mu)
            {
                writeErr = WriteResponseLocked(msg.Id, TimingFunctions.ServerTimingSnapshot(timing));
            }
            if (writeErr is not null)
            {
                throw new InvalidOperationException($"api: failed to write server timing response: {writeErr.Message}");
            }
            return;
        }
        if (msg.Method == Method.ResetServerTiming.Value)
        {
            if (timing is not null)
            {
                timing.Reset();
            }
            Exception? writeErr;
            lock (mu)
            {
                writeErr = WriteResponseLocked(msg.Id, null);
            }
            if (writeErr is not null)
            {
                throw new InvalidOperationException($"api: failed to write reset server timing response: {writeErr.Message}");
            }
            return;
        }

        long start = 0;
        if (timing is not null)
        {
            start = Stopwatch.GetTimestamp();
        }

        // Recover from panics and convert to error response with stack trace
        // (conn_sync.go:118-134). See Conn.Async.cs for the boundary mapping.
        try
        {
            object? result;
            try
            {
                result = handler.HandleRequest(ctx, msg.Method, msg.Params);
            }
            catch (ApiRequestException e)
            {
                if (timing is not null)
                {
                    timing.Record(msg.Method, Stopwatch.GetElapsedTime(start));
                }
                Exception? writeErr;
                lock (mu)
                {
                    writeErr = WriteErrorLocked(msg.Id, new global::Ts.JsonRpc.ResponseError
                    {
                        Code = global::Ts.JsonRpc.ErrorCodes.CodeInternalError,
                        Message = e.Message,
                    });
                }
                if (writeErr is not null)
                {
                    throw new InvalidOperationException($"api: failed to write response: {writeErr.Message}");
                }
                return;
            }

            if (timing is not null)
            {
                timing.Record(msg.Method, Stopwatch.GetElapsedTime(start));
            }

            lock (mu)
            {
                Exception? writeErr = WriteResponseLocked(msg.Id, result);
                if (writeErr is not null)
                {
                    throw new InvalidOperationException($"api: failed to write response: {writeErr.Message}");
                }
            }
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("api: failed to write", System.StringComparison.Ordinal))
        {
            // Go panics (crashing the process); C# propagates to the Run caller.
            throw;
        }
        catch (Exception e)
        {
            // Panic path: no timing record, stack trace appended like Go's
            // fmt.Errorf("panic: %v\n%s", r, stack).
            string message = $"panic: {e.Message}\n{e.StackTrace}";

            Exception? writeErr;
            lock (mu)
            {
                writeErr = WriteErrorLocked(msg.Id, new global::Ts.JsonRpc.ResponseError
                {
                    Code = global::Ts.JsonRpc.ErrorCodes.CodeInternalError,
                    Message = message,
                });
            }
            if (writeErr is not null)
            {
                throw new InvalidOperationException($"api: failed to write panic error response: {writeErr.Message} (original panic: {e.Message})");
            }
        }
    }

    // handleNotification processes an incoming notification. (conn_sync.go:161-163)
    private void HandleNotification(global::Ts.Core.RequestContext ctx, Message msg)
    {
        try
        {
            handler.HandleNotification(ctx, msg.Method, msg.Params);
        }
        catch
        {
            // Go: `_ = c.handler.HandleNotification(ctx, msg.Method, msg.Params)`.
        }
    }

    // Call sends a request to the client and waits for a response.
    // This method is safe to call from multiple goroutines - calls are serialized.
    // (conn_sync.go:167-201)
    public byte[]? Call(global::Ts.Core.RequestContext ctx, string method, object? @params)
    {
        // Serialize all Call operations. This is critical because:
        // 1. The msgpack protocol uses method names as response IDs
        // 2. The handler code (project internals) may spawn goroutines that call
        //    filesystem callbacks concurrently
        // 3. We need to ensure write/read pairs are atomic
        lock (mu)
        {
            global::Ts.JsonRpc.Id id = global::Ts.JsonRpc.Id.NewIdString(method);

            try
            {
                protocol.WriteRequest(id, method, @params);
            }
            catch (Exception e)
            {
                throw new IOException(e.Message, e);
            }

            if (ctx.Cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(ctx.Cancellation);
            }

            // Read the response inline.
            (Message? msg, bool ok) = protocol.ReadMessage();
            if (!ok)
            {
                throw new IOException("EOF");
            }

            if (msg!.IsResponse() && msg.Id is not null && msg.Id.ToString() == method)
            {
                if (msg.Error is not null)
                {
                    throw new ApiRequestException($"api: remote error [{msg.Error.Code}]: {msg.Error.Message}");
                }
                return msg.Result;
            }

            // Unexpected message while waiting for response
            throw new ApiRequestException($"api: unexpected message while waiting for {method} response");
        }
    }

    // Notify sends a notification to the client (no response expected).
    // (conn_sync.go:204-208)
    public void Notify(global::Ts.Core.RequestContext ctx, string method, object? @params)
    {
        lock (mu)
        {
            protocol.WriteNotification(method, @params);
        }
    }

    private Exception? WriteResponseLocked(global::Ts.JsonRpc.Id? id, object? result)
    {
        try
        {
            protocol.WriteResponse(id, result);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private Exception? WriteErrorLocked(global::Ts.JsonRpc.Id? id, global::Ts.JsonRpc.ResponseError err)
    {
        try
        {
            protocol.WriteError(id, err);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
