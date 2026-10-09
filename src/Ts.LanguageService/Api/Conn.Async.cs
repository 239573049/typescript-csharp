// Ported from tsgo internal/api/conn_async.go (whole file).
//
// PORT-NOTE (concurrency): Go spawns a goroutine per incoming request/notification;
// C# uses Task.Run. Go's `panic(...)` on write failures inside a request goroutine
// crashes the process; C# lets the exception surface on the next Run loop pass via
// the faulted-request check (a deliberate, registered deviation — a crashed worker
// must not go silently unobserved). Pending server→client calls map to a
// TaskCompletionSource keyed by the same struct equality Go's map[jsonrpc.ID] uses.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ts.JsonRpc;

namespace Ts.Api;

// AsyncConn manages bidirectional JSON-RPC communication with async request handling.
// Each incoming request is handled in its own goroutine, allowing concurrent processing.
// This is the standard implementation for LSP-style JSON-RPC protocols.
// (conn_async.go:17-34)
public sealed class AsyncConn : Conn
{
    private readonly System.IO.Stream rwc;
    private readonly Protocol protocol;
    private readonly Handler handler;

    // timing, when non-nil, accumulates the wall-clock time spent handling each
    // request. Clients retrieve the collected data via a getServerTiming request.
    private TimingCollector? timing;

    // For server→client requests
    private long seq;

    private readonly Dictionary<PendingKey, TaskCompletionSource<Message>> pending = [];
    private readonly object pendingMu = new();
    private readonly object writeMu = new();

    // Faulted request tasks are surfaced on the next Run pass (see file header).
    private readonly List<Task> inflight = [];

    // NewAsyncConn creates a new async connection with the given transport and handler.
    // It uses JSONRPCProtocol (LSP-style Content-Length framing) by default.
    // (conn_async.go:38-40)
    public AsyncConn(System.IO.Stream rwc, Handler handler)
        : this(rwc, new JsonRpcProtocol(rwc), handler)
    {
    }

    // NewAsyncConnWithProtocol creates a new async connection with a custom protocol.
    // (conn_async.go:43-50)
    public AsyncConn(System.IO.Stream rwc, Protocol protocol, Handler handler)
    {
        this.rwc = rwc;
        this.protocol = protocol;
        this.handler = handler;
    }

    // Go's map key: jsonrpc.ID is a comparable struct {str, int}. (conn_async.go:31)
    private readonly record struct PendingKey(string Str, int Int);

    private static PendingKey KeyOf(global::Ts.JsonRpc.Id id)
    {
        return new PendingKey(id.str, id.intValue);
    }

    // SetCollectTiming enables or disables per-request server processing-time
    // measurement. When enabled, the connection accumulates timing that clients can
    // retrieve via a getServerTiming request. (conn_async.go:55-61)
    public void SetCollectTiming(bool enabled)
    {
        timing = enabled ? new TimingCollector() : null;
    }

    // Run starts processing messages on the connection.
    // It blocks until the context is cancelled or an error occurs.
    // (conn_async.go:65-87)
    public Exception? Run(global::Ts.Core.RequestContext ctx)
    {
        while (true)
        {
            if (ctx.Cancellation.IsCancellationRequested)
            {
                return new OperationCanceledException(ctx.Cancellation);
            }

            SweepInflight();

            (Message? msg, bool ok) = protocol.ReadMessage();
            if (!ok)
            {
                // Go: errors.Is(err, io.EOF) → return nil.
                return null;
            }

            if (msg!.IsResponse())
            {
                HandleResponse(msg);
            }
            else if (msg.IsRequest())
            {
                Task task = Task.Run(() => HandleRequest(ctx, msg));
                lock (inflight)
                {
                    inflight.Add(task);
                }
            }
            else if (msg.IsNotification())
            {
                Task.Run(() => HandleNotification(ctx, msg));
            }
        }
    }

    private void SweepInflight()
    {
        lock (inflight)
        {
            for (int i = inflight.Count - 1; i >= 0; i--)
            {
                Task task = inflight[i];
                if (!task.IsCompleted)
                {
                    continue;
                }
                inflight.RemoveAt(i);
                if (task.IsFaulted)
                {
                    throw task.Exception!.InnerException ?? task.Exception;
                }
            }
        }
    }

    // handleResponse matches a response to a pending request. (conn_async.go:90-102)
    private void HandleResponse(Message msg)
    {
        TaskCompletionSource<Message>? ch;
        lock (pendingMu)
        {
            PendingKey key = KeyOf(msg.Id!);
            if (pending.TryGetValue(key, out ch))
            {
                pending.Remove(key);
            }
        }

        if (ch is not null)
        {
            ch.TrySetResult(msg);
        }
    }

    // handleRequest processes an incoming request. (conn_async.go:105-179)
    private void HandleRequest(global::Ts.Core.RequestContext ctx, Message msg)
    {
        // Intercept the meta-requests for collected server timing before dispatching
        // to the handler, so they are answered directly and not themselves recorded.
        if (msg.Method == Method.GetServerTiming.Value)
        {
            Exception? writeErr = WriteLocked(() => protocol.WriteResponse(msg.Id, TimingFunctions.ServerTimingSnapshot(timing)));
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
            Exception? writeErr = WriteLocked(() => protocol.WriteResponse(msg.Id, null));
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

        // Go records timing between the handler call and the response write, on both
        // the success and the error-return path; the panic path never reaches the
        // record. The catch-all below realizes Go's deferred recover() at the same
        // boundary (conn_async.go:139-155).
        try
        {
            object? result;
            try
            {
                result = handler.HandleRequest(ctx, msg.Method, msg.Params);
            }
            catch (ApiRequestException e)
            {
                // Error-return path: timing is recorded, then the error is written.
                if (timing is not null)
                {
                    timing.Record(msg.Method, Stopwatch.GetElapsedTime(start));
                }
                Exception? writeErr;
                lock (writeMu)
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

            lock (writeMu)
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
            // Go panics on write failure inside the request goroutine (crashing the
            // process); here the exception surfaces through the Run loop's inflight
            // sweep. See the file-header PORT-NOTE.
            throw;
        }
        catch (Exception e)
        {
            // Panic path: no timing record, stack trace appended like Go's
            // fmt.Errorf("panic: %v\n%s", r, stack).
            string message = $"panic: {e.Message}\n{e.StackTrace}";

            Exception? writeErr;
            lock (writeMu)
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

    private Exception? WriteLocked(Action write)
    {
        lock (writeMu)
        {
            try
            {
                write();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }

    // handleNotification processes an incoming notification. (conn_async.go:182-184)
    // Go discards the error return (`_ =`); C# swallows the thrown realization.
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

    // Call sends a request to the client and waits for a response. (conn_async.go:187-224)
    public byte[]? Call(global::Ts.Core.RequestContext ctx, string method, object? @params)
    {
        // Create unique request ID
        long n = Interlocked.Add(ref seq, 1);
        global::Ts.JsonRpc.Id id = global::Ts.JsonRpc.Id.NewIdString($"api{n}");

        // Register response channel BEFORE sending request to avoid race
        var ch = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingMu)
        {
            pending[KeyOf(id)] = ch;
        }

        try
        {
            // Send the request
            Exception? sendErr;
            lock (writeMu)
            {
                try
                {
                    protocol.WriteRequest(id, method, @params);
                    sendErr = null;
                }
                catch (Exception e)
                {
                    sendErr = e;
                }
            }
            if (sendErr is not null)
            {
                throw sendErr;
            }

            Message resp;
            try
            {
                resp = ch.Task.WaitAsync(ctx.Cancellation).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Go: return nil, ctx.Err().
                throw;
            }

            if (resp.Error is not null)
            {
                throw new ApiRequestException($"api: remote error [{resp.Error.Code}]: {resp.Error.Message}");
            }
            return resp.Result;
        }
        finally
        {
            lock (pendingMu)
            {
                PendingKey key = KeyOf(id);
                if (pending.TryGetValue(key, out TaskCompletionSource<Message>? leftover))
                {
                    leftover.TrySetCanceled();
                    pending.Remove(key);
                }
            }
        }
    }

    // Notify sends a notification to the client (no response expected).
    // (conn_async.go:227-231)
    public void Notify(global::Ts.Core.RequestContext ctx, string method, object? @params)
    {
        lock (writeMu)
        {
            protocol.WriteNotification(method, @params);
        }
    }
}
