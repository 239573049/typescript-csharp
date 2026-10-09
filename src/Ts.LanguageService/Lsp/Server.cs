// Ported from tsgo internal/lsp/server.go (whole file).
//
// PORT-NOTEs:
//  * Go `context.Context` → Ts.Core.RequestContext for request/session surfaces and
//    CancellationToken for the loops and the project.Client surface (plan/01 §4.4).
//    errgroup.WithContext → a linked CancellationTokenSource plus a first-error slot;
//    context.WithCancelCause → a linked CTS (the cause is never read back in Go —
//    dispatchLoop returns ctx.Err(), i.e. Canceled — so only the cancellation is kept).
//  * Go `(T, error)` handler returns map to `(T, Exception?)` tuples (plan/05 §4). The
//    language service throws instead of returning errors: Go error values surface as
//    LspErrorException / UserFacingRequestFailedException / OperationCanceledException /
//    Ts.Ls.NeedsAutoImportsException (see IsErrorValue) and are folded back into the
//    tuple channel; any other exception is a Go panic and follows recover semantics.
//  * lsproto.WithClientCapabilities(ctx, ...) has no C# carrier (RequestContext has no
//    context values): the resolved capabilities travel on SessionOptions.ClientCapabilities
//    (registered deviation, Project/LsProto.Missing.cs) and the server reads its own copy.
//  * Go `sync.Mutex` → System.Threading.Lock; atomics → Interlocked/Volatile.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Id = global::Ts.JsonRpc.Id;
using LsMessage = global::Ts.LsProto.Message;
using RequestMessage = global::Ts.LsProto.RequestMessage;
using ResponseMessage = global::Ts.LsProto.ResponseMessage;
using RequestContext = global::Ts.Core.RequestContext;
using ErrorCode = global::Ts.LsProto.ErrorCode;
using ParseCacheT = global::Ts.Project.RefCountCache<global::Ts.Project.ParseCacheKey, global::Ts.Ast.SourceFile, global::Ts.Project.FileHandle>;
using TelemetryEvent = global::Ts.LsProto.RequestFailureTelemetryEventOrPerformanceStatsTelemetryEventOrProjectInfoTelemetryEventOrNull;

namespace Ts.Lsp;

// server.go:38-50 — ServerOptions.
public sealed class ServerOptions
{
    public Reader In { get; set; } = null!;
    public Writer Out { get; set; } = null!;
    public TextWriter Err { get; set; } = TextWriter.Null;

    public string Cwd { get; set; } = "";
    public global::Ts.Vfs.IFileSystem FS { get; set; } = null!;
    public string DefaultLibraryPath { get; set; } = "";
    public string TypingsLocation { get; set; } = "";
    public ParseCacheT? ParseCache { get; set; }
    public Func<string, string[], (byte[] Output, Exception? Error)>? NpmInstall { get; set; }
    // delay before showing progress UI; 0 means no delay
    public TimeSpan ProgressDelay { get; set; }
    public Action<int>? SetParentProcessID { get; set; }
}

// server.go:94-96 — pendingClientRequest.
internal readonly record struct PendingClientRequest(RequestMessage Req, CancellationTokenSource Cancel);

// server.go:98-104 — Reader / Writer. Go's error returns map to the (value, Exception?)
// channel; io.EOF is EofException.Instance.
public interface Reader
{
    (LsMessage? Message, Exception? Error) Read();
}

public interface Writer
{
    Exception? Write(LsMessage msg);
}

// server.go:106-145 — lspReader / lspWriter / ToReader / ToWriter.
public static class LspStreams
{
    private sealed class LspReader(global::Ts.JsonRpc.Reader r) : Reader
    {
        public (LsMessage? Message, Exception? Error) Read()
        {
            byte[] data;
            try
            {
                var (bytes, ok) = r.Read();
                if (!ok)
                {
                    return (null, global::Ts.LsProto.EofException.Instance);
                }
                data = bytes!; // ok=true asserts a frame body.
            }
            catch (global::Ts.JsonRpc.JsonRpcFramingException e)
            {
                return (null, e);
            }
            catch (IOException e)
            {
                return (null, e);
            }

            // Go: json.Unmarshal(data, req) — an InvalidParams decode error returns the
            // partially decoded request. The C# Message.Parse defers params decoding to
            // UnmarshalParams, so only the InvalidRequest arm can occur here.
            try
            {
                return (LsMessage.Parse(data), null);
            }
            catch (global::Ts.LsProto.LspErrorException e)
            {
                return (null, e);
            }
        }
    }

    private sealed class LspWriter(global::Ts.JsonRpc.Writer w) : Writer
    {
        public Exception? Write(LsMessage msg)
        {
            var buffer = new ArrayBufferWriter<byte>();
            try
            {
                using (var json = new Utf8JsonWriter(buffer))
                {
                    msg.WriteTo(json);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
            {
                return new InvalidOperationException("failed to marshal message: " + e.Message, e);
            }
            try
            {
                w.Write(buffer.WrittenSpan.ToArray());
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return e;
            }
            return null;
        }
    }

    public static Reader ToReader(Stream r) => new LspReader(new global::Ts.JsonRpc.Reader(r));

    public static Writer ToWriter(Stream w) => new LspWriter(new global::Ts.JsonRpc.Writer(w));
}

public sealed class Server : global::Ts.Project.Client, global::Ts.Project.Ata.NpmExecutor
{
    // server.go:83-92 — fileRenameFilters.
    private static readonly LsProto.FileOperationFilter[] fileRenameFilters =
    [
        new LsProto.FileOperationFilter
        {
            Scheme = "file",
            Pattern = new LsProto.FileOperationPattern
            {
                Glob = "**/*.{ts,tsx,js,jsx,cts,cjs,mts,mjs,json}",
            },
        },
    ];

    private readonly Reader r;
    private readonly Writer w;
    internal RequestContext backgroundCtx;

    private readonly TextWriter stderr;

    internal readonly Logger logger;
    internal int initStarted; // Go: atomic.Bool
    private int clientSeq; // Go: atomic.Int32
    private readonly DynamicQueue<RequestMessage> requestQueue = new();
    private readonly DynamicQueue<LsMessage> outgoingQueue = new();
    private readonly Dictionary<Id, PendingClientRequest> pendingClientRequests = [];
    private readonly Lock pendingClientRequestsMu = new();
    private readonly Dictionary<Id, TaskCompletionSource<ResponseMessage>> pendingServerRequests = [];
    private readonly Lock pendingServerRequestsMu = new();

    private readonly string cwd;
    private readonly global::Ts.Vfs.IFileSystem fs;
    private readonly string defaultLibraryPath;
    private readonly string typingsLocation;

    private LsProto.InitializeParams? initializeParams;
    private LsProto.InitializationOptions initializationOptions = new();
    private LsProto.ResolvedClientCapabilities clientCapabilities = new();
    private LsProto.PositionEncodingKind positionEncoding;
    private global::Ts.Locale.Locale locale;

    private bool watchEnabled;
    private bool telemetryEnabled;
    private readonly global::Ts.Collections.SyncSet<global::Ts.Project.WatcherID> watchers = new();
    // builtinWatcher is non-null when the server is running its own
    // in-process file watcher instead of using LSP-based watching. It
    // is enabled when the client lacks DynamicRegistration for
    // workspace/didChangeWatchedFiles and the builtin watcher backend
    // supports efficient recursive watching (Windows or FSEvents).
    private LspWatcher.Watcher? builtinWatcher;

    private long lastRequestTimeMs; // Go: atomic.Int64

    internal global::Ts.Project.Session? session;

    // apiSessions holds active API sessions keyed by their ID
    private Dictionary<string, global::Ts.Api.Session>? apiSessions;
    private readonly Lock apiSessionsMu = new();

    // initComplete is completed when handleInitialized completes.
    // Used by tests to wait for full initialization.
    private readonly TaskCompletionSource initComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // !!! temporary; remove when we have `handleDidChangeConfiguration`/implicit project config support
    private global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects;
    // parseCache can be passed in so separate tests can share ASTs
    private readonly ParseCacheT? parseCache;

    private readonly Func<string, string[], (byte[] Output, Exception? Error)>? npmInstall;

    private readonly global::Ts.Profiling.CpuProfiler cpuProfiler = new();

    private readonly TimeSpan progressDelay;
    private ProjectLoadingProgress? projectProgress;

    private readonly Action<int>? startWatchdog;

    // server.go:52-80 — NewServer.
    public Server(ServerOptions opts)
    {
        if (opts.Cwd == "")
        {
            throw new InvalidOperationException("Cwd is required");
        }

        r = opts.In;
        w = opts.Out;
        stderr = opts.Err;
        cwd = opts.Cwd;
        fs = opts.FS;
        defaultLibraryPath = opts.DefaultLibraryPath;
        typingsLocation = opts.TypingsLocation;
        parseCache = opts.ParseCache;
        npmInstall = opts.NpmInstall;
        startWatchdog = opts.SetParentProcessID;
        progressDelay = opts.ProgressDelay;
        logger = new Logger(this);
    }

    public static Server NewServer(ServerOptions opts) => new(opts);

    public global::Ts.Project.Session? Session() => session;

    // InitComplete returns a task that is completed when the server has finished
    // processing the initialized notification, including the initial configuration
    // exchange with the client.
    public Task InitComplete() => initComplete.Task;

    // Members consumed by Logger.cs / Progress.cs (Go reads the unexported fields directly).
    internal bool InitStarted => Volatile.Read(ref initStarted) != 0;

    internal TextWriter Stderr => stderr;

    internal CancellationToken BackgroundCancellation => backgroundCtx.Cancellation;

    internal global::Ts.Locale.Locale Locale => locale;

    // WatchFiles implements project.Client.
    public Exception? WatchFiles(CancellationToken ctx, global::Ts.Project.WatcherID id, LsProto.FileSystemWatcher[] fileWatchers)
    {
        if (builtinWatcher is not null)
        {
            Exception? watchErr = builtinWatcher.WatchFiles(id.Value, fileWatchers);
            if (watchErr is not null)
            {
                return Wrap("failed to register file watcher", watchErr);
            }
            watchers.Add(id);
            return null;
        }
        var (_, err) = SendClientRequest(ctx, LsProto.Infos.ClientRegisterCapabilityInfo, new LsProto.RegistrationParams
        {
            Registrations =
            [
                new LsProto.Registration
                {
                    Id = id.Value,
                    RegisterOptions = new LsProto.RegisterOptions
                    {
                        WorkspaceDidChangeWatchedFiles = new LsProto.DidChangeWatchedFilesRegistrationOptions
                        {
                            Watchers = fileWatchers,
                        },
                    },
                },
            ],
        });
        if (err is not null)
        {
            return Wrap("failed to register file watcher", err);
        }

        watchers.Add(id);
        return null;
    }

    // UnwatchFiles implements project.Client.
    public Exception? UnwatchFiles(CancellationToken ctx, global::Ts.Project.WatcherID id)
    {
        if (builtinWatcher is not null)
        {
            if (!watchers.Has(id))
            {
                return new InvalidOperationException($"no file watcher exists with ID {id.Value}");
            }
            Exception? unwatchErr = builtinWatcher.UnwatchFiles(id.Value);
            if (unwatchErr is not null)
            {
                return Wrap("failed to unregister file watcher", unwatchErr);
            }
            watchers.Delete(id);
            return null;
        }
        if (watchers.Has(id))
        {
            var (_, err) = SendClientRequest(ctx, LsProto.Infos.ClientUnregisterCapabilityInfo, new LsProto.UnregistrationParams
            {
                Unregisterations =
                [
                    new LsProto.Unregistration
                    {
                        Id = id.Value,
                        Method = LsProto.Methods.WorkspaceDidChangeWatchedFiles.Value,
                    },
                ],
            });
            if (err is not null)
            {
                return Wrap("failed to unregister file watcher", err);
            }

            watchers.Delete(id);
            return null;
        }

        return new InvalidOperationException($"no file watcher exists with ID {id.Value}");
    }

    // RefreshDiagnostics implements project.Client.
    public Exception? RefreshDiagnostics(CancellationToken ctx)
    {
        if (!clientCapabilities.Workspace.Diagnostics.RefreshSupport)
        {
            return null;
        }

        if (ctx.IsCancellationRequested)
        {
            return new OperationCanceledException(ctx);
        }

        // Fire-and-forget: the client always returns null, and waiting for the response
        // can cause the server to hang if the client is slow or unresponsive.
        // Any response from the client will be silently ignored by the read loop.
        Exception? err = SendClientRequestFireAndForget(LsProto.Infos.WorkspaceDiagnosticRefreshInfo, new LsProto.NoParams());
        if (err is not null)
        {
            return Wrap("failed to refresh diagnostics", err);
        }

        return null;
    }

    // PublishDiagnostics implements project.Client.
    public Exception? PublishDiagnostics(CancellationToken ctx, LsProto.PublishDiagnosticsParams @params)
    {
        return SendNotification(LsProto.Infos.TextDocumentPublishDiagnosticsInfo, @params);
    }

    // SendTelemetry implements project.Client.
    public Exception? SendTelemetry(CancellationToken ctx, TelemetryEvent telemetry)
    {
        if (!telemetryEnabled)
        {
            throw new InvalidOperationException("SendTelemetry called with telemetry disabled");
        }
        return SendNotification(LsProto.Infos.TelemetryEventInfo, telemetry);
    }

    // IsActive implements project.Client.
    public bool IsActive()
    {
        long last = Interlocked.Read(ref lastRequestTimeMs);
        return last == 0 || NowUnixMilli() - last <= 60_000;
    }

    public Exception? RefreshInlayHints(CancellationToken ctx)
    {
        if (!clientCapabilities.Workspace.InlayHint.RefreshSupport)
        {
            return null;
        }

        Exception? err = SendClientRequestFireAndForget(LsProto.Infos.WorkspaceInlayHintRefreshInfo, new LsProto.NoParams());
        if (err is not null)
        {
            return Wrap("failed to refresh inlay hints", err);
        }
        return null;
    }

    public Exception? RefreshCodeLens(CancellationToken ctx)
    {
        if (!clientCapabilities.Workspace.CodeLens.RefreshSupport)
        {
            return null;
        }

        Exception? err = SendClientRequestFireAndForget(LsProto.Infos.WorkspaceCodeLensRefreshInfo, new LsProto.NoParams());
        if (err is not null)
        {
            return Wrap("failed to refresh code lens", err);
        }
        return null;
    }

    // ProgressStart implements project.Client.
    public void ProgressStart(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        projectProgress?.Start(message, args);
    }

    // ProgressFinish implements project.Client.
    public void ProgressFinish(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        projectProgress?.Finish(message, args);
    }

    // server.go:362-422 — RequestConfiguration.
    public (global::Ts.Ls.UserPreferences? Preferences, Exception? Error) RequestConfiguration(RequestContext ctx)
    {
        // Go: lsproto.GetClientCapabilities(ctx) — the request ctx carries s.clientCapabilities.
        LsProto.ResolvedClientCapabilities caps = clientCapabilities;
        if (!caps.Workspace.Configuration)
        {
            if (initializationOptions.UserPreferences is { } userPrefs)
            {
                // Go: "%T\n%+v" — the JSON kind stands in for the dynamic Go type.
                logger.Logf(
                    "received formatting options from initialization: {0}\n{1}",
                    userPrefs.ValueKind,
                    userPrefs.GetRawText());
                if (userPrefs.ValueKind == JsonValueKind.Object)
                {
                    return (global::Ts.Ls.UserPreferences.ParseUserPreferences(new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["js/ts"] = userPrefs }), null);
                }
            }
            return (global::Ts.Ls.UserPreferences.NewDefaultUserPreferences(), null);
        }
        var (configs, err) = SendClientRequest(ctx.Cancellation, LsProto.Infos.WorkspaceConfigurationInfo, new LsProto.ConfigurationParams
        {
            Items =
            [
                new LsProto.ConfigurationItem { Section = "js/ts" },
                new LsProto.ConfigurationItem { Section = "typescript" },
                new LsProto.ConfigurationItem { Section = "javascript" },
                new LsProto.ConfigurationItem { Section = "editor" },
            ],
        });
        if (err is not null)
        {
            return (null, Wrap("configure request failed", err));
        }
        var configMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        configs ??= [];
        for (int i = 0; i < configs.Length; i++)
        {
            if (configs[i] is not { } config)
            {
                continue; // Go stores a nil any, which ParseUserPreferences ignores.
            }
            switch (i)
            {
                case 0:
                    configMap["js/ts"] = config;
                    break;
                case 1:
                    configMap["typescript"] = config;
                    break;
                case 2:
                    configMap["javascript"] = config;
                    break;
                case 3:
                    configMap["editor"] = config;
                    break;
            }
        }
        logger.Logf(
            "received options from workspace/configuration request:\njs/ts: {0}\n\ntypescript: {1}\n\njavascript: {2}\n\neditor: {3}\n",
            RawOrNil(configMap, "js/ts"),
            RawOrNil(configMap, "typescript"),
            RawOrNil(configMap, "javascript"),
            RawOrNil(configMap, "editor"));
        return (global::Ts.Ls.UserPreferences.ParseUserPreferences(configMap), null);
    }

    private static string RawOrNil(Dictionary<string, JsonElement> configMap, string key) =>
        configMap.TryGetValue(key, out JsonElement value) ? value.GetRawText() : "<nil>";

    // server.go:424-446 — Run.
    public Exception? Run(CancellationToken parent)
    {
        // Go: g, ctx := errgroup.WithContext(ctx)
        using var group = CancellationTokenSource.CreateLinkedTokenSource(parent);
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
            try
            {
                group.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        backgroundCtx = new RequestContext { Cancellation = ctx };
        Task dispatch = Task.Factory.StartNew(() => GroupFail(DispatchLoop(ctx)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task write = Task.Factory.StartNew(() => GroupFail(WriteLoop(ctx)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Don't run readLoop in the group, as it blocks on stdin read and cannot be cancelled.
        var readLoopErr = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task waiter = Task.Run(async () =>
        {
            Task canceled = Task.Delay(Timeout.Infinite, ctx).ContinueWith(static _ => { }, TaskScheduler.Default);
            Task done = await Task.WhenAny(canceled, readLoopErr.Task).ConfigureAwait(false);
            if (done == readLoopErr.Task)
            {
                GroupFail(readLoopErr.Task.Result);
            }
            else
            {
                GroupFail(new OperationCanceledException(ctx));
            }
        });
        _ = Task.Factory.StartNew(() => readLoopErr.TrySetResult(ReadLoop(ctx)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Task.WaitAll(dispatch, write, waiter);
        Exception? err = firstErr;
        if (err is not null && !global::Ts.LsProto.LspErrors.IsEof(err) && ctx.IsCancellationRequested)
        {
            return err;
        }
        return null;
    }

    // server.go:448-516 — readLoop.
    private Exception? ReadLoop(CancellationToken ctx)
    {
        while (true)
        {
            if (ctx.IsCancellationRequested)
            {
                return new OperationCanceledException(ctx);
            }
            var (msg, err) = Read();
            if (err is not null)
            {
                ErrorCode? code = global::Ts.LsProto.LspErrors.CodeOf(err);
                if (code is ErrorCode.InvalidRequest or ErrorCode.InvalidParams)
                {
                    Id? id = null;
                    if (code == ErrorCode.InvalidParams)
                    {
                        if (msg is not null && msg.Kind == global::Ts.JsonRpc.MessageKind.Request)
                        {
                            id = msg.AsRequest().Id;
                        }
                    }
                    Exception? sendErr = SendError(id, err);
                    if (sendErr is not null)
                    {
                        return sendErr;
                    }
                    continue;
                }
                return err;
            }

            if (initializeParams is null && msg!.Kind == global::Ts.JsonRpc.MessageKind.Request)
            {
                RequestMessage req = msg.AsRequest();
                if (req.Method == LsProto.Methods.Initialize)
                {
                    LsProto.InitializeParams @params;
                    try
                    {
                        @params = global::Ts.LsProto.LsProtoUnmarshal.UnmarshalParams(req.Params, LsProto.Infos.InitializeInfo.ParamsTypeInfo)!;
                    }
                    catch (global::Ts.LsProto.LspErrorException unmarshalErr)
                    {
                        Exception? sendErr = SendError(req.Id, unmarshalErr);
                        if (sendErr is not null)
                        {
                            return sendErr;
                        }
                        continue;
                    }
                    var (resp, initErr) = HandleInitialize(new RequestContext { Cancellation = ctx }, @params, req);
                    if (initErr is not null)
                    {
                        return initErr;
                    }
                    Exception? resultErr = SendResult(req.Id, resp, LsProto.Infos.InitializeInfo.ResultTypeInfo);
                    if (resultErr is not null)
                    {
                        return resultErr;
                    }
                }
                else
                {
                    Exception? sendErr = SendError(req.Id, ErrorCodeError(ErrorCode.ServerNotInitialized));
                    if (sendErr is not null)
                    {
                        return sendErr;
                    }
                }
                continue;
            }

            if (msg!.Kind == global::Ts.JsonRpc.MessageKind.Response)
            {
                ResponseMessage resp = msg.AsResponse();
                lock (pendingServerRequestsMu)
                {
                    if (pendingServerRequests.Remove(resp.Id!, out TaskCompletionSource<ResponseMessage>? respChan))
                    {
                        respChan.TrySetResult(resp);
                    }
                }
            }
            else
            {
                RequestMessage req = msg.AsRequest();
                if (req.Method == LsProto.Methods.CancelRequest)
                {
                    try
                    {
                        LsProto.CancelParams? @params = global::Ts.LsProto.LsProtoUnmarshal.UnmarshalParams(req.Params, LsProto.Infos.CancelRequestInfo.ParamsTypeInfo);
                        if (@params is not null)
                        {
                            CancelRequest(@params.Id);
                        }
                    }
                    catch (global::Ts.LsProto.LspErrorException)
                    {
                        // Go: a cancel notification with bad params is dropped.
                    }
                }
                else
                {
                    Exception? putErr = requestQueue.Put(ctx, req);
                    if (putErr is not null)
                    {
                        return putErr;
                    }
                }
            }
        }
    }

    // server.go:518-527 — cancelRequest.
    private void CancelRequest(LsProto.IntegerOrString rawID)
    {
        Id id = global::Ts.LsProto.LsProtoNewId.NewId(rawID);
        lock (pendingClientRequestsMu)
        {
            if (pendingClientRequests.Remove(id, out PendingClientRequest pendingReq))
            {
                pendingReq.Cancel.Cancel();
            }
        }
    }

    private (LsMessage? Message, Exception? Error) Read()
    {
        return r.Read();
    }

    // server.go:533-597 — dispatchLoop.
    private Exception? DispatchLoop(CancellationToken parent)
    {
        // Go: ctx, lspExit := context.WithCancelCause(ctx); defer lspExit(nil). The CTS is
        // deliberately not disposed: async request work may still call lspExit after the
        // loop returns.
        var lspExitCts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        CancellationToken ctx = lspExitCts.Token;
        void LspExit(Exception? cause)
        {
            _ = cause; // Go records the cause, which is never read back (Get returns ctx.Err()).
            lspExitCts.Cancel();
        }
        try
        {
            while (true)
            {
                var (req, err) = requestQueue.Get(ctx);
                if (err is not null)
                {
                    return err;
                }

                Interlocked.Exchange(ref lastRequestTimeMs, NowUnixMilli());
                RequestContext requestCtx = new RequestContext { Cancellation = ctx }.WithLocale(locale);
                CancellationTokenSource? cancel = null;
                if (req.Id is not null)
                {
                    cancel = CancellationTokenSource.CreateLinkedTokenSource(ctx);
                    requestCtx = (requestCtx with { Cancellation = cancel.Token }).WithRequestId(req.Id.ToString());
                    lock (pendingClientRequestsMu)
                    {
                        pendingClientRequests[req.Id] = new PendingClientRequest(req, cancel);
                    }
                }

                void HandleError(Exception handlerErr)
                {
                    if (global::Ts.LsProto.LspErrors.IsCanceled(handlerErr))
                    {
                        Exception? sendErr = SendError(req.Id, ErrorCodeError(ErrorCode.RequestCancelled));
                        if (sendErr is not null)
                        {
                            LspExit(sendErr);
                        }
                    }
                    else if (global::Ts.LsProto.LspErrors.IsEof(handlerErr))
                    {
                        LspExit(null);
                    }
                    else
                    {
                        Exception? sendErr = SendError(req.Id, handlerErr);
                        if (sendErr is not null)
                        {
                            LspExit(sendErr);
                        }
                    }
                }

                void RemoveRequest()
                {
                    if (req.Id is not null)
                    {
                        lock (pendingClientRequestsMu)
                        {
                            pendingClientRequests.Remove(req.Id);
                        }
                        cancel!.Cancel();
                        cancel.Dispose();
                    }
                }

                var (doAsyncWork, handleErr) = HandleRequestOrNotification(requestCtx, req);
                if (handleErr is not null)
                {
                    HandleError(handleErr);
                    RemoveRequest();
                }
                else if (doAsyncWork is not null)
                {
                    _ = Task.Run(() =>
                    {
                        Exception? lsError = doAsyncWork();
                        if (lsError is not null)
                        {
                            HandleError(lsError);
                        }
                        RemoveRequest();
                    });
                }
                else
                {
                    RemoveRequest();
                }
            }
        }
        finally
        {
            LspExit(null);
        }
    }

    // server.go:599-610 — writeLoop.
    internal Exception? WriteLoop(CancellationToken ctx)
    {
        while (true)
        {
            var (msg, err) = outgoingQueue.Get(ctx);
            if (err is not null)
            {
                return err;
            }
            Exception? writeErr = w.Write(msg);
            if (writeErr is not null)
            {
                return Wrap("failed to write message", writeErr);
            }
        }
    }

    // server.go:612-638 — sendClientRequest.
    // WARNING: this should only be called in the async portion of a request handler,
    // otherwise a deadlock can occur.
    internal (TResult Result, Exception? Error) SendClientRequest<TParams, TResult>(CancellationToken ctx, LsProto.RequestInfo<TParams, TResult> info, TParams @params)
    {
        Id id = Id.NewIdString("ts" + Interlocked.Increment(ref clientSeq).ToString(CultureInfo.InvariantCulture));
        RequestMessage req = RequestMessage.New(info, id, @params);

        var responseChan = new TaskCompletionSource<ResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingServerRequestsMu)
        {
            pendingServerRequests[id] = responseChan;
        }

        try
        {
            Exception? sendErr = Send(req.Message());
            if (sendErr is not null)
            {
                return (default!, sendErr);
            }

            try
            {
                responseChan.Task.Wait(ctx);
            }
            catch (OperationCanceledException e)
            {
                return (default!, e);
            }
            ResponseMessage resp = responseChan.Task.Result;
            if (resp.Error is not null)
            {
                return (default!, new InvalidOperationException($"request failed: {resp.Error}"));
            }
            try
            {
                return (global::Ts.LsProto.LsProtoUnmarshal.UnmarshalResult(resp.Result, info.ResultTypeInfo), null);
            }
            catch (global::Ts.LsProto.LspErrorException e)
            {
                return (default!, e);
            }
        }
        finally
        {
            lock (pendingServerRequestsMu)
            {
                pendingServerRequests.Remove(id);
            }
        }
    }

    // server.go:640-648 — sendClientRequestFireAndForget sends a request to the client
    // without waiting for a response. The response, if any, will be silently ignored by
    // the read loop since no pending channel is registered. This means any error returned
    // by the client will not be observed. Use only for requests where the response value
    // is not needed (e.g., the client always returns null).
    internal Exception? SendClientRequestFireAndForget<TParams, TResult>(LsProto.RequestInfo<TParams, TResult> info, TParams @params)
    {
        Id id = Id.NewIdString("ts" + Interlocked.Increment(ref clientSeq).ToString(CultureInfo.InvariantCulture));
        RequestMessage req = RequestMessage.New(info, id, @params);
        return Send(req.Message());
    }

    // server.go:650-655 — sendResult. Go marshals `result any`; C# serializes through the
    // request's source-generated result type info.
    private Exception? SendResult<TResult>(Id? id, TResult result, JsonTypeInfo<TResult> typeInfo)
    {
        return SendResponse(new ResponseMessage
        {
            Id = id,
            Result = JsonSerializer.SerializeToElement(result, typeInfo),
        });
    }

    // server.go:662-681 — sendError.
    internal Exception? SendError(Id? id, Exception err)
    {
        ErrorCode? errCode = global::Ts.LsProto.LspErrors.CodeOf(err);
        // Do not send error response for notifications,
        // except for parse errors which may occur before determining if the message is a request or notification.
        if (id is null && errCode != ErrorCode.InvalidRequest)
        {
            logger.Errorf("error handling notification: {0}", err.Message);
            return null;
        }
        ErrorCode code = errCode ?? ErrorCode.InternalError;
        // TODO(jakebailey): error data
        return SendResponse(new ResponseMessage
        {
            Id = id,
            Error = new global::Ts.JsonRpc.ResponseError
            {
                Code = (int)code,
                Message = err.Message,
            },
        });
    }

    // server.go:683-685 — sendNotification.
    internal Exception? SendNotification<TParams>(LsProto.NotificationInfo<TParams> info, TParams @params)
    {
        return Send(RequestMessage.New(info, @params).Message());
    }

    private Exception? SendResponse(ResponseMessage resp)
    {
        return Send(resp.Message());
    }

    // send writes a message to the outgoing queue, respecting context cancellation.
    internal Exception? Send(LsMessage msg)
    {
        return outgoingQueue.Put(backgroundCtx.Cancellation, msg);
    }

    // server.go:696-740 — handleRequestOrNotification looks up the handler for the given
    // request or notification, executes its synchronous work and returns any asynchronous
    // work as a function to be executed by the caller.
    private (Func<Exception?>? AsyncWork, Exception? Error) HandleRequestOrNotification(RequestContext ctx, RequestMessage req)
    {
        // Go: ctx = lsproto.WithClientCapabilities(ctx, &s.clientCapabilities) — see the
        // file header (capabilities travel on SessionOptions).
        if (Handlers.Value.TryGetValue(req.Method, out HandlerFunc? handler))
        {
            var start = Stopwatch.StartNew();
            var (doAsyncWork, err) = handler(this, ctx, req);
            string idStr = "";
            if (req.Id is not null)
            {
                idStr = " (" + req.Id + ")";
            }
            if (err is not null)
            {
                if (!IsUserFacing(err))
                {
                    logger.Error("error handling method '", req.Method, "'", idStr, ": ", err.Message);
                }
                else if (!logger.IsTracing())
                {
                    logger.Info("handled method '", req.Method, "'", idStr, " in ", start.Elapsed);
                }
                return (null, err);
            }
            if (doAsyncWork is not null)
            {
                return (() =>
                {
                    // note: ctx.Err() has to be checked in the async work to allow async handlers to cleanup resources correctly
                    Exception? asyncWorkErr = doAsyncWork();
                    bool isUserFacing = IsUserFacing(asyncWorkErr);
                    bool isRealError = asyncWorkErr is not null && !isUserFacing;
                    if (isRealError)
                    {
                        logger.Info("error handling method '", req.Method, "'", idStr, " in ", start.Elapsed);
                    }
                    else if (!logger.IsTracing())
                    {
                        logger.Info("handled method '", req.Method, "'", idStr, " in ", start.Elapsed);
                    }
                    return asyncWorkErr;
                }, null);
            }
            if (!logger.IsTracing())
            {
                logger.Info("handled method '", req.Method, "'", idStr, " in ", start.Elapsed);
            }
            return (null, null);
        }
        logger.Warn("unknown method '", req.Method, "'");
        if (req.Id is not null)
        {
            return (null, SendError(req.Id, ErrorCodeError(ErrorCode.InvalidRequest)));
        }
        return (null, null);
    }

    // server.go:742-745 — handlerMap maps LSP method to a handler function. The handler
    // function executes any work that must be done synchronously before other
    // requests/notifications can be processed, and returns any additional work as a
    // function to be executed asynchronously after the synchronous work is complete.
    internal delegate (Func<Exception?>? AsyncWork, Exception? Error) HandlerFunc(Server s, RequestContext ctx, RequestMessage req);

    // server.go:747-815 — handlers (sync.OnceValue).
    private static readonly Lazy<Dictionary<LsProto.Method, HandlerFunc>> Handlers = new(static () =>
    {
        var handlers = new Dictionary<LsProto.Method, HandlerFunc>();

        RegisterRequestHandler(handlers, LsProto.Infos.InitializeInfo, static (s, ctx, p, req) => s.HandleInitialize(ctx, p, req));
        RegisterNotificationHandler(handlers, LsProto.Infos.InitializedInfo, static (s, ctx, p) => s.HandleInitialized(ctx, p));
        RegisterRequestHandler(handlers, LsProto.Infos.ShutdownInfo, static (s, ctx, p, req) => s.HandleShutdown(ctx, p, req));
        RegisterNotificationHandler(handlers, LsProto.Infos.ExitInfo, static (s, ctx, p) => s.HandleExit(ctx, p));

        RegisterNotificationHandler(handlers, LsProto.Infos.WorkspaceDidChangeConfigurationInfo, static (s, ctx, p) => s.HandleDidChangeWorkspaceConfiguration(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.TextDocumentDidOpenInfo, static (s, ctx, p) => s.HandleDidOpen(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.TextDocumentDidChangeInfo, static (s, ctx, p) => s.HandleDidChange(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.TextDocumentDidSaveInfo, static (s, ctx, p) => s.HandleDidSave(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.TextDocumentDidCloseInfo, static (s, ctx, p) => s.HandleDidClose(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.WorkspaceDidChangeWatchedFilesInfo, static (s, ctx, p) => s.HandleDidChangeWatchedFiles(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.SetTraceInfo, static (s, ctx, p) => s.HandleSetTrace(ctx, p));
        RegisterNotificationHandler(handlers, LsProto.Infos.CustomSetLogVerbosityInfo, static (s, ctx, p) => s.HandleSetLogVerbosity(ctx, p));
        RegisterRequestHandler(handlers, LsProto.Infos.WorkspaceWillRenameFilesInfo, static (s, ctx, p, req) => s.HandleWillRenameFiles(ctx, p, req));

        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentDiagnosticInfo, static (s, ctx, ls, p) => s.HandleDocumentDiagnostic(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentHoverInfo, static (s, ctx, ls, p) => s.HandleHover(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentDefinitionInfo, static (s, ctx, ls, p) => s.HandleDefinition(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.CustomTextDocumentSourceDefinitionInfo, static (s, ctx, ls, p) => s.HandleSourceDefinition(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentTypeDefinitionInfo, static (s, ctx, ls, p) => s.HandleTypeDefinition(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentSignatureHelpInfo, static (s, ctx, ls, p) => s.HandleSignatureHelp(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentFormattingInfo, static (s, ctx, ls, p) => s.HandleDocumentFormat(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentRangeFormattingInfo, static (s, ctx, ls, p) => s.HandleDocumentRangeFormat(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentOnTypeFormattingInfo, static (s, ctx, ls, p) => s.HandleDocumentOnTypeFormat(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentDocumentSymbolInfo, static (s, ctx, ls, p) => s.HandleDocumentSymbol(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentDocumentHighlightInfo, static (s, ctx, ls, p) => s.HandleDocumentHighlight(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.CustomTextDocumentMultiDocumentHighlightInfo, static (s, ctx, ls, p) => s.HandleMultiDocumentHighlight(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentSelectionRangeInfo, static (s, ctx, ls, p) => s.HandleSelectionRange(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentInlayHintInfo, static (s, ctx, ls, p) => s.HandleInlayHint(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentCodeLensInfo, static (s, ctx, ls, p) => s.HandleCodeLens(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentCodeActionInfo, static (s, ctx, ls, p) => s.HandleCodeAction(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentPrepareCallHierarchyInfo, static (s, ctx, ls, p) => s.HandlePrepareCallHierarchy(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentFoldingRangeInfo, static (s, ctx, ls, p) => s.HandleFoldingRange(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentPrepareRenameInfo, static (s, ctx, ls, p) => s.HandlePrepareRename(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentLinkedEditingRangeInfo, static (s, ctx, ls, p) => s.HandleLinkedEditingRange(ctx, ls, p));

        RegisterLanguageServiceWithAutoImportsRequestHandler(handlers, LsProto.Infos.TextDocumentCompletionInfo, static (s, ctx, ls, p) => s.HandleCompletion(ctx, ls, p));
        RegisterLanguageServiceWithAutoImportsRequestHandler(handlers, LsProto.Infos.TextDocumentCodeActionInfo, static (s, ctx, ls, p) => s.HandleCodeAction(ctx, ls, p));

        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentVSOnAutoInsertInfo, static (s, ctx, ls, p) => s.HandleVSOnAutoInsert(ctx, ls, p));

        RegisterMultiProjectReferenceRequestHandler(handlers, LsProto.Infos.TextDocumentReferencesInfo, static (ls, ctx, p, o) => ls.ProvideReferences(ctx.Cancellation, p, o));
        RegisterMultiProjectReferenceRequestHandler(handlers, LsProto.Infos.TextDocumentVSReferencesInfo, static (ls, ctx, p, o) => ls.ProvideVSReferences(ctx.Cancellation, p, o));
        RegisterRequestHandler(handlers, LsProto.Infos.TextDocumentRenameInfo, static (s, ctx, p, req) => s.HandleRename(ctx, p, req));
        RegisterMultiProjectReferenceRequestHandler(handlers, LsProto.Infos.TextDocumentImplementationInfo, static (ls, ctx, p, o) => ls.ProvideImplementations(ctx.Cancellation, p, o));

        RegisterRequestHandler(handlers, LsProto.Infos.CallHierarchyIncomingCallsInfo, static (s, ctx, p, req) => s.HandleCallHierarchyIncomingCalls(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CallHierarchyOutgoingCallsInfo, static (s, ctx, p, req) => s.HandleCallHierarchyOutgoingCalls(ctx, p, req));

        RegisterRequestHandler(handlers, LsProto.Infos.WorkspaceSymbolInfo, static (s, ctx, p, req) => s.HandleWorkspaceSymbol(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CompletionItemResolveInfo, static (s, ctx, p, req) => s.HandleCompletionItemResolve(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CodeLensResolveInfo, static (s, ctx, p, req) => s.HandleCodeLensResolve(ctx, p, req));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentSemanticTokensFullInfo, static (s, ctx, ls, p) => s.HandleSemanticTokensFull(ctx, ls, p));
        RegisterLanguageServiceDocumentRequestHandler(handlers, LsProto.Infos.TextDocumentSemanticTokensRangeInfo, static (s, ctx, ls, p) => s.HandleSemanticTokensRange(ctx, ls, p));

        // Developer/debugging commands
        RegisterRequestHandler(handlers, LsProto.Infos.CustomRunGCInfo, static (s, ctx, p, req) => s.HandleRunGC(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CustomSaveHeapProfileInfo, static (s, ctx, p, req) => s.HandleSaveHeapProfile(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CustomSaveAllocProfileInfo, static (s, ctx, p, req) => s.HandleSaveAllocProfile(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CustomStartCPUProfileInfo, static (s, ctx, p, req) => s.HandleStartCPUProfile(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CustomStopCPUProfileInfo, static (s, ctx, p, req) => s.HandleStopCPUProfile(ctx, p, req));

        RegisterRequestHandler(handlers, LsProto.Infos.CustomInitializeAPISessionInfo, static (s, ctx, p, req) => s.HandleInitializeAPISession(ctx, p, req));
        RegisterRequestHandler(handlers, LsProto.Infos.CustomProjectInfoInfo, static (s, ctx, p, req) => s.HandleProjectInfo(ctx, p, req));
        return handlers;
    });

    // server.go:817-832 — registerNotificationHandler.
    private static void RegisterNotificationHandler<TReq>(
        Dictionary<LsProto.Method, HandlerFunc> handlers,
        LsProto.NotificationInfo<TReq> info,
        Func<Server, RequestContext, TReq, Exception?> fn)
    {
        handlers[info.Method] = (s, ctx, req) =>
        {
            if (s.session is null && req.Method != LsProto.Methods.Initialized)
            {
                return (null, ErrorCodeError(ErrorCode.ServerNotInitialized));
            }

            var (@params, err) = UnmarshalParams(req, info.ParamsTypeInfo);
            if (err is not null)
            {
                return (null, err);
            }
            Exception? fnErr = fn(s, ctx, @params);
            if (fnErr is not null)
            {
                return (null, fnErr);
            }
            return (null, CtxErr(ctx));
        };
    }

    // server.go:834-857 — registerRequestHandler.
    private static void RegisterRequestHandler<TReq, TResp>(
        Dictionary<LsProto.Method, HandlerFunc> handlers,
        LsProto.RequestInfo<TReq, TResp> info,
        Func<Server, RequestContext, TReq, RequestMessage, (TResp Result, Exception? Error)> fn)
    {
        handlers[info.Method] = (s, ctx, req) =>
        {
            if (s.session is null && req.Method != LsProto.Methods.Initialize)
            {
                return (null, ErrorCodeError(ErrorCode.ServerNotInitialized));
            }

            var (@params, err) = UnmarshalParams(req, info.ParamsTypeInfo);
            if (err is not null)
            {
                return (null, err);
            }
            var (resp, fnErr) = Invoke(() => fn(s, ctx, @params, req));
            if (fnErr is not null)
            {
                return (null, fnErr);
            }
            if (CtxErr(ctx) is { } ctxErr)
            {
                return (null, ctxErr);
            }
            return (null, s.SendResult(req.Id, resp, info.ResultTypeInfo));
        };
    }

    // server.go:859-884 — registerLanguageServiceDocumentRequestHandler.
    private static void RegisterLanguageServiceDocumentRequestHandler<TReq, TResp>(
        Dictionary<LsProto.Method, HandlerFunc> handlers,
        LsProto.RequestInfo<TReq, TResp> info,
        Func<Server, RequestContext, global::Ts.Ls.LanguageService, TReq, (TResp Result, Exception? Error)> fn)
        where TReq : LsProto.IHasTextDocumentUri
    {
        handlers[info.Method] = (s, ctx, req) =>
        {
            var (@params, err) = UnmarshalParams(req, info.ParamsTypeInfo);
            if (err is not null)
            {
                return (null, err);
            }
            var (ls, lsErr0) = s.session!.GetLanguageService(ctx, @params.TextDocumentURI());
            if (lsErr0 is not null)
            {
                return (null, lsErr0);
            }
            return (() =>
            {
                try
                {
                    var (resp, lsErr) = Invoke(() => fn(s, ctx, ls!, @params));
                    // After any language service request, check if new global diagnostics were
                    // discovered during checking and push updated tsconfig diagnostics if so.
                    s.session.EnqueuePublishGlobalDiagnostics();
                    if (lsErr is not null)
                    {
                        return lsErr;
                    }
                    if (CtxErr(ctx) is { } ctxErr)
                    {
                        return ctxErr;
                    }
                    return s.SendResult(req.Id, resp, info.ResultTypeInfo);
                }
                catch (Exception panic)
                {
                    s.Recover(req, panic);
                    return null;
                }
            }, null);
        };
    }

    // server.go:886-919 — registerLanguageServiceWithAutoImportsRequestHandler.
    private static void RegisterLanguageServiceWithAutoImportsRequestHandler<TReq, TResp>(
        Dictionary<LsProto.Method, HandlerFunc> handlers,
        LsProto.RequestInfo<TReq, TResp> info,
        Func<Server, RequestContext, global::Ts.Ls.LanguageService, TReq, (TResp Result, Exception? Error)> fn)
        where TReq : LsProto.IHasTextDocumentUri
    {
        handlers[info.Method] = (s, ctx, req) =>
        {
            var (@params, err) = UnmarshalParams(req, info.ParamsTypeInfo);
            if (err is not null)
            {
                return (null, err);
            }
            return s.session!.WithLanguageServiceAndSnapshot(ctx, @params.TextDocumentURI(), (languageService, snapshot) => ((Func<Exception?>)(() =>
            {
                try
                {
                    var (resp, lsErr) = Invoke(() => fn(s, ctx, languageService, @params));
                    if (lsErr is global::Ts.Ls.NeedsAutoImportsException)
                    {
                        (global::Ts.Ls.LanguageService? withAutoImports, lsErr) = s.session.GetLanguageServiceWithAutoImports(ctx, snapshot, @params.TextDocumentURI());
                        if (lsErr is not null)
                        {
                            return lsErr;
                        }
                        if (CtxErr(ctx) is { } autoImportsCtxErr)
                        {
                            return autoImportsCtxErr;
                        }
                        (resp, lsErr) = Invoke(() => fn(s, ctx, withAutoImports!, @params));
                        if (lsErr is global::Ts.Ls.NeedsAutoImportsException)
                        {
                            throw new InvalidOperationException(info.Method.Value + " returned ErrNeedsAutoImports even after enabling auto imports");
                        }
                    }
                    if (lsErr is not null)
                    {
                        return lsErr;
                    }
                    if (CtxErr(ctx) is { } ctxErr)
                    {
                        return ctxErr;
                    }
                    return s.SendResult(req.Id, resp, info.ResultTypeInfo);
                }
                catch (Exception panic)
                {
                    s.Recover(req, panic);
                    return null;
                }
            }), (Exception?)null));
        };
    }

    // server.go:921-948 — registerMultiProjectReferenceRequestHandler.
    private static void RegisterMultiProjectReferenceRequestHandler<TReq, TResp>(
        Dictionary<LsProto.Method, HandlerFunc> handlers,
        LsProto.RequestInfo<TReq, TResp> info,
        Func<global::Ts.Ls.LanguageService, RequestContext, TReq, global::Ts.Ls.CrossProjectOrchestrator, TResp> fn)
        where TReq : LsProto.IHasTextDocumentPosition
    {
        handlers[info.Method] = (s, ctx, req) =>
        {
            var (@params, err) = UnmarshalParams(req, info.ParamsTypeInfo);
            if (err is not null)
            {
                return (null, err);
            }
            // !!! sheetal: multiple projects that contain the file through symlinks
            var (defaultLs, orchestrator, orchestratorErr) = s.GetLanguageServiceAndCrossProjectOrchestrator(ctx, @params.TextDocumentURI(), req);
            if (orchestratorErr is not null)
            {
                return (null, orchestratorErr);
            }
            return (() =>
            {
                try
                {
                    var (resp, lsErr) = Invoke(() => (fn(defaultLs!, ctx, @params, orchestrator!), (Exception?)null));
                    if (lsErr is not null)
                    {
                        return lsErr;
                    }
                    if (CtxErr(ctx) is { } ctxErr)
                    {
                        return ctxErr;
                    }
                    return s.SendResult(req.Id, resp, info.ResultTypeInfo);
                }
                catch (Exception panic)
                {
                    s.Recover(req, panic);
                    return null;
                }
            }, null);
        };
    }

    // server.go:950-985 — crossProjectOrchestrator.
    private sealed class CrossProjectOrchestrator(
        Server server,
        RequestMessage req,
        RequestContext requestCtx,
        global::Ts.Project.Project defaultProject,
        global::Ts.Ls.Project[] allProjects) : global::Ts.Ls.CrossProjectOrchestrator
    {
        private readonly RequestMessage req = req;

        public global::Ts.Ls.Project GetDefaultProject() => defaultProject;

        public global::Ts.Ls.Project[] GetAllProjectsForInitialRequest() => allProjects;

        public global::Ts.Ls.LanguageService? GetLanguageServiceForProjectWithFile(CancellationToken ctx, global::Ts.Ls.Project p, LsProto.DocumentUri uri)
        {
            return server.session!.GetLanguageServiceForProjectWithFile(requestCtx with { Cancellation = ctx }, (global::Ts.Project.Project)p, uri);
        }

        public global::Ts.Ls.Project[]? GetProjectsForFile(CancellationToken ctx, LsProto.DocumentUri uri)
        {
            return server.session!.GetProjectsForFile(requestCtx with { Cancellation = ctx }, uri);
        }

        // Go yields from inside WithSnapshotLoadingProjectTree's callback (iter.Seq). C#
        // iterators cannot yield across a delegate boundary, so the projects of the
        // loaded snapshot are materialized while the snapshot is held and then streamed.
        public IEnumerable<global::Ts.Ls.Project> GetProjectsLoadingProjectTree(CancellationToken ctx, global::Ts.Collections.Set<global::Ts.TsPath.Path> requestedProjectTrees)
        {
            var projects = new List<global::Ts.Ls.Project>();
            server.session!.WithSnapshotLoadingProjectTree(requestCtx with { Cancellation = ctx }, requestedProjectTrees, snapshot =>
            {
                foreach (global::Ts.Project.Project p in snapshot.ProjectCollection.Projects())
                {
                    projects.Add(p);
                }
            });
            foreach (global::Ts.Ls.Project p in projects)
            {
                yield return p;
            }
        }
    }

    // server.go:987-994 — getLanguageServiceAndCrossProjectOrchestrator.
    private (global::Ts.Ls.LanguageService? LanguageService, global::Ts.Ls.CrossProjectOrchestrator? Orchestrator, Exception? Error) GetLanguageServiceAndCrossProjectOrchestrator(RequestContext ctx, LsProto.DocumentUri uri, RequestMessage req)
    {
        var (defaultProject, defaultLs, allProjects, err) = session!.GetLanguageServiceAndProjectsForFile(ctx, uri);
        global::Ts.Ls.CrossProjectOrchestrator? orchestrator = null;
        if (err is null)
        {
            orchestrator = new CrossProjectOrchestrator(this, req, ctx, defaultProject!, allProjects);
        }
        return (defaultLs, orchestrator, err);
    }

    // server.go:996-1018 — recover. Called from a catch block with the panic value.
    private void Recover(RequestMessage req, Exception r)
    {
        string stack = r.ToString(); // Go: debug.Stack()
        logger.Errorf("panic handling request {0}: {1}\n{2}", req.Method, r.Message, stack);
        if (req.Id is not null)
        {
            _ = SendError(req.Id, new global::Ts.LsProto.LspErrorException(
                ErrorCode.InternalError,
                $"{(global::Ts.LsProto.LspErrorException.CodeToString(ErrorCode.InternalError))}: panic handling request {req.Method}: {r.Message}",
                r));
        }
        else
        {
            logger.Error("unhandled panic in notification", req.Method, r.Message);
        }

        if (telemetryEnabled)
        {
            _ = SendNotification(LsProto.Infos.TelemetryEventInfo, TelemetryEvent.FromRequestFailureTelemetryEvent(new LsProto.RequestFailureTelemetryEvent
            {
                Properties = new LsProto.RequestFailureTelemetryProperties
                {
                    ErrorCode = global::Ts.LsProto.LspErrorException.CodeToString(ErrorCode.InternalError),
                    RequestMethod = req.Method.Value.Replace("/", ".", StringComparison.Ordinal),
                    Stack = StackSanitizer.SanitizeStackTrace(stack),
                },
            }));
        }
    }

    // server.go:1020-1196 — handleInitialize.
    private (LsProto.InitializeResult? Result, Exception? Error) HandleInitialize(RequestContext ctx, LsProto.InitializeParams @params, RequestMessage? req)
    {
        if (initializeParams is not null)
        {
            return (null, ErrorCodeError(ErrorCode.InvalidRequest));
        }

        Volatile.Write(ref initStarted, 1);

        initializeParams = @params;
        // The spec types initializationOptions as nullable; treat both null and an
        // absent value as empty options so the rest of the server can read fields
        // off s.initializationOptions without nil-checking the container.
        if (@params.InitializationOptions?.InitializationOptions is { } initOptions)
        {
            initializationOptions = initOptions;
        }
        else
        {
            initializationOptions = new LsProto.InitializationOptions();
        }
        if (initializationOptions.LogVerbosity is { } v)
        {
            if (Logger.IsValidLogVerbosity(v))
            {
                logger.SetVerbosity(v);
            }
        }
        clientCapabilities = LsProto.ResolvedClientCapabilitiesExtensions.Resolve(@params.Capabilities);
        if (clientCapabilities.Window.WorkDoneProgress)
        {
            projectProgress = ProjectLoadingProgress.New(this, progressDelay);
        }

        string capabilitiesJSON;
        try
        {
            capabilitiesJSON = MarshalIndent(clientCapabilities, LsProto.LspJsonContext.Default.ResolvedClientCapabilities);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return (null, e);
        }
        logger.Info("Resolved client capabilities: " + capabilitiesJSON);

        positionEncoding = LsProto.PositionEncodingKind.UTF16;
        if (Array.IndexOf(clientCapabilities.General.PositionEncodings ?? [], LsProto.PositionEncodingKind.UTF8) >= 0)
        {
            positionEncoding = LsProto.PositionEncodingKind.UTF8;
        }

        if (initializeParams.Locale is { } localeStr)
        {
            _ = global::Ts.Locale.Locale.TryParse(localeStr, out locale);
        }

        if (startWatchdog is not null && @params.ProcessId?.Integer is { } processId)
        {
            startWatchdog(processId);
        }

        var response = new LsProto.InitializeResult
        {
            ServerInfo = new LsProto.ServerInfo
            {
                Name = "typescript-go",
                Version = global::Ts.Core.TsVersion.Version(),
            },
            Capabilities = new LsProto.ServerCapabilities
            {
                PositionEncoding = positionEncoding,
                TextDocumentSync = LsProto.TextDocumentSyncOptionsOrKind.FromOptions(new LsProto.TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = LsProto.TextDocumentSyncKind.Incremental,
                    Save = LsProto.BooleanOrSaveOptions.FromBoolean(true),
                }),
                HoverProvider = LsProto.BooleanOrHoverOptions.FromBoolean(true),
                DefinitionProvider = LsProto.BooleanOrDefinitionOptions.FromBoolean(true),
                TypeDefinitionProvider = LsProto.BooleanOrTypeDefinitionOptionsOrTypeDefinitionRegistrationOptions.FromBoolean(true),
                ReferencesProvider = LsProto.BooleanOrReferenceOptions.FromBoolean(true),
                ImplementationProvider = LsProto.BooleanOrImplementationOptionsOrImplementationRegistrationOptions.FromBoolean(true),
                DiagnosticProvider = LsProto.DiagnosticOptionsOrRegistrationOptions.FromOptions(new LsProto.DiagnosticOptions
                {
                    Identifier = "typescript",
                    InterFileDependencies = true,
                }),
                CompletionProvider = new LsProto.CompletionOptions
                {
                    TriggerCharacters = global::Ts.Ls.LanguageService.TriggerCharacters,
                    ResolveProvider = true,
                    CompletionItem = new LsProto.ServerCompletionItemOptions
                    {
                        LabelDetailsSupport = true,
                    },
                },
                SignatureHelpProvider = new LsProto.SignatureHelpOptions
                {
                    TriggerCharacters = ["(", ",", "<"],
                    RetriggerCharacters = [")"],
                },
                DocumentFormattingProvider = LsProto.BooleanOrDocumentFormattingOptions.FromBoolean(true),
                DocumentRangeFormattingProvider = LsProto.BooleanOrDocumentRangeFormattingOptions.FromBoolean(true),
                DocumentOnTypeFormattingProvider = new LsProto.DocumentOnTypeFormattingOptions
                {
                    FirstTriggerCharacter = "{",
                    MoreTriggerCharacter = ["}", ";", "\n"],
                },
                WorkspaceSymbolProvider = LsProto.BooleanOrWorkspaceSymbolOptions.FromBoolean(true),
                DocumentSymbolProvider = LsProto.BooleanOrDocumentSymbolOptions.FromBoolean(true),
                FoldingRangeProvider = LsProto.BooleanOrFoldingRangeOptionsOrFoldingRangeRegistrationOptions.FromBoolean(true),
                RenameProvider = LsProto.BooleanOrRenameOptions.FromRenameOptions(new LsProto.RenameOptions
                {
                    PrepareProvider = true,
                }),
                DocumentHighlightProvider = LsProto.BooleanOrDocumentHighlightOptions.FromBoolean(true),
                SelectionRangeProvider = LsProto.BooleanOrSelectionRangeOptionsOrSelectionRangeRegistrationOptions.FromBoolean(true),
                LinkedEditingRangeProvider = LsProto.BooleanOrLinkedEditingRangeOptionsOrLinkedEditingRangeRegistrationOptions.FromBoolean(true),
                InlayHintProvider = LsProto.BooleanOrInlayHintOptionsOrInlayHintRegistrationOptions.FromBoolean(true),
                CodeLensProvider = new LsProto.CodeLensOptions
                {
                    ResolveProvider = true,
                },
                CodeActionProvider = LsProto.BooleanOrCodeActionOptions.FromCodeActionOptions(new LsProto.CodeActionOptions
                {
                    CodeActionKinds =
                    [
                        LsProto.CodeActionKind.QuickFix,
                        LsProto.CodeActionKind.SourceOrganizeImports,
                        LsProto.CodeActionKind.SourceRemoveUnusedImports,
                        LsProto.CodeActionKind.SourceSortImports,
                        LsProto.CodeActionKind.SourceFixAll,
                    ],
                }),
                CallHierarchyProvider = LsProto.BooleanOrCallHierarchyOptionsOrCallHierarchyRegistrationOptions.FromBoolean(true),
                Experimental = new LsProto.ExperimentalServerCapabilities
                {
                    CustomSourceDefinitionProvider = true,
                    CustomMultiDocumentHighlightProvider = true,
                },
                VSReferencesProvider = true,
                VSOnAutoInsertProvider = new LsProto.VSOnAutoInsertOptions
                {
                    VSTriggerCharacters = [">"],
                },
                Workspace = new LsProto.WorkspaceOptions
                {
                    FileOperations = new LsProto.FileOperationOptions
                    {
                        WillRename = new LsProto.FileOperationRegistrationOptions
                        {
                            Filters = fileRenameFilters,
                        },
                    },
                },
                SemanticTokensProvider = LsProto.SemanticTokensOptionsOrRegistrationOptions.FromOptions(new LsProto.SemanticTokensOptions
                {
                    Legend = global::Ts.Ls.LanguageService.SemanticTokensLegend(clientCapabilities.TextDocument.SemanticTokens),
                    Full = LsProto.BooleanOrSemanticTokensFullDelta.FromBoolean(true),
                    Range = LsProto.BooleanOrEmptyObject.FromBoolean(true),
                }),
            },
        };

        return (response, null);
    }

    // server.go:1198-1301 — handleInitialized.
    private Exception? HandleInitialized(RequestContext ctx, LsProto.InitializedParams @params)
    {
        bool disablePushDiagnostics = false;
        bool enableTelemetry = false;
        if (initializationOptions.DisablePushDiagnostics is { } disable)
        {
            disablePushDiagnostics = disable;
        }
        if (initializationOptions.EnableTelemetry is { } enable)
        {
            enableTelemetry = enable;
        }
        bool hasDynamicWatchRegistration = clientCapabilities.Workspace.DidChangeWatchedFiles.DynamicRegistration;
        if (hasDynamicWatchRegistration)
        {
            logger.Logf("file watching: using LSP client-side watching (client supports dynamic registration)");
            watchEnabled = true;
        }
        else if (global::Ts.FsWatch.Watcher.Default().HasFastRecursiveBackend())
        {
            // The client cannot watch files itself, but the builtin watcher has a
            // backend with efficient recursive watching (Windows or FSEvents), so
            // fall back to watching files in-process.
            logger.Logf("file watching: using builtin in-process watcher (client lacks dynamic watch registration)");
            watchEnabled = true;
            builtinWatcher = LspWatcher.Watcher.New(fs, changes =>
            {
                session?.DidChangeWatchedFiles(backgroundCtx, changes);
            }, logger);
        }
        else
        {
            // The client cannot watch files and the builtin watcher backend lacks
            // efficient recursive watching, so file watching is disabled.
            logger.Logf("file watching: disabled (client lacks dynamic watch registration and builtin watcher backend is not fast-recursive)");
        }

        string sessionCwd = cwd;
        if (clientCapabilities.Workspace.WorkspaceFolders &&
            initializeParams!.WorkspaceFolders?.WorkspaceFolders is { Length: 1 } folders)
        {
            sessionCwd = LsProto.DocumentUriExtensions.FileName(new LsProto.DocumentUri(folders[0].Uri.Value));
        }
        else if (initializeParams!.RootUri?.DocumentUri is { } rootUri)
        {
            sessionCwd = LsProto.DocumentUriExtensions.FileName(rootUri);
        }
        else if (initializeParams.RootPath?.String is { } rootPath)
        {
            sessionCwd = rootPath;
        }
        if (!global::Ts.TsPath.TsPath.PathIsAbsolute(sessionCwd))
        {
            sessionCwd = cwd;
        }

        telemetryEnabled = enableTelemetry;

        session = global::Ts.Project.Session.NewSession(new global::Ts.Project.SessionInit
        {
            // Go: lsproto.WithClientCapabilities(s.backgroundCtx, &s.clientCapabilities) —
            // the capabilities ride SessionOptions.ClientCapabilities (file header).
            BackgroundCtx = backgroundCtx,
            Options = new global::Ts.Project.SessionOptions
            {
                CurrentDirectory = sessionCwd,
                DefaultLibraryPath = defaultLibraryPath,
                TypingsLocation = typingsLocation,
                PositionEncoding = positionEncoding,
                WatchEnabled = watchEnabled,
                LoggingEnabled = true,
                TelemetryEnabled = enableTelemetry,
                DebounceDelay = TimeSpan.FromMilliseconds(500),
                PushDiagnosticsEnabled = !disablePushDiagnostics,
                Locale = locale,
                ClientCapabilities = clientCapabilities,
            },
            FS = fs,
            Logger = logger,
            Client = this,
            NpmExecutor = this,
            ParseCache = parseCache,
        });

        var (userPreferences, err) = RequestConfiguration(ctx);
        if (err is not null)
        {
            return err;
        }
        session.InitializeWithUserConfig(userPreferences!);

        (_, err) = SendClientRequest(ctx.Cancellation, LsProto.Infos.ClientRegisterCapabilityInfo, new LsProto.RegistrationParams
        {
            Registrations =
            [
                new LsProto.Registration
                {
                    Id = "typescript-config-watch-id",
                    RegisterOptions = new LsProto.RegisterOptions
                    {
                        WorkspaceDidChangeConfiguration = new LsProto.DidChangeConfigurationRegistrationOptions
                        {
                            Section = LsProto.StringOrStrings.FromStrings(["js/ts", "typescript", "javascript", "editor"]),
                        },
                    },
                },
            ],
        });
        if (err is not null)
        {
            return Wrap("failed to register configuration change watcher", err);
        }

        // !!! temporary.
        // Remove when we have `handleDidChangeConfiguration`/implicit project config support
        // derived from 'js/ts.implicitProjectConfig.*'.
        if (compilerOptionsForInferredProjects is not null)
        {
            session.DidChangeCompilerOptionsForInferredProjects(ctx, compilerOptionsForInferredProjects);
        }

        session.StartPerformanceTelemetry();

        initComplete.TrySetResult();
        return null;
    }

    // server.go:1303-1309 — handleShutdown.
    private (LsProto.Null Result, Exception? Error) HandleShutdown(RequestContext ctx, LsProto.NoParams _, RequestMessage __)
    {
        builtinWatcher?.Close();
        session!.Close();
        return (default, null);
    }

    // server.go:1311-1313 — handleExit.
    private Exception? HandleExit(RequestContext ctx, LsProto.NoParams _)
    {
        return global::Ts.LsProto.EofException.Instance;
    }

    // server.go:1315-1322 — handleDidChangeWorkspaceConfiguration.
    private Exception? HandleDidChangeWorkspaceConfiguration(RequestContext ctx, LsProto.DidChangeConfigurationParams @params)
    {
        if (@params.Settings is not { } settings || settings.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        else if (settings.ValueKind == JsonValueKind.Object)
        {
            // Go: params.Settings.(map[string]any) → lsutil.ParseUserPreferences(settings).
            var settingsMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in settings.EnumerateObject())
            {
                settingsMap[property.Name] = property.Value;
            }
            session!.Configure(global::Ts.Ls.UserPreferences.ParseUserPreferences(settingsMap));
        }
        return null;
    }

    private Exception? HandleDidOpen(RequestContext ctx, LsProto.DidOpenTextDocumentParams @params)
    {
        session!.DidOpenFile(ctx, @params.TextDocument.Uri, @params.TextDocument.Version, @params.TextDocument.Text, @params.TextDocument.LanguageId.Value ?? "");
        return null;
    }

    private Exception? HandleDidChange(RequestContext ctx, LsProto.DidChangeTextDocumentParams @params)
    {
        session!.DidChangeFile(ctx, @params.TextDocument.Uri, @params.TextDocument.Version, @params.ContentChanges);
        return null;
    }

    private Exception? HandleDidSave(RequestContext ctx, LsProto.DidSaveTextDocumentParams @params)
    {
        session!.DidSaveFile(ctx, @params.TextDocument.Uri);
        return null;
    }

    private Exception? HandleDidClose(RequestContext ctx, LsProto.DidCloseTextDocumentParams @params)
    {
        session!.DidCloseFile(ctx, @params.TextDocument.Uri);
        return null;
    }

    private Exception? HandleDidChangeWatchedFiles(RequestContext ctx, LsProto.DidChangeWatchedFilesParams @params)
    {
        session!.DidChangeWatchedFiles(ctx, @params.Changes);
        return null;
    }

    private Exception? HandleSetTrace(RequestContext _, LsProto.SetTraceParams __)
    {
        // $/setTrace is sent by vscode-languageclient when trace settings change.
        // Server log verbosity is controlled separately by custom/setLogVerbosity,
        // so this handler is intentionally a no-op.
        return null;
    }

    private Exception? HandleSetLogVerbosity(RequestContext _, LsProto.SetLogVerbosityParams @params)
    {
        if (!Logger.IsValidLogVerbosity(@params.Verbosity))
        {
            return new global::Ts.LsProto.LspErrorException(
                ErrorCode.InvalidParams,
                $"{(global::Ts.LsProto.LspErrorException.CodeToString(ErrorCode.InvalidParams))}: invalid log verbosity {((int)@params.Verbosity).ToString(CultureInfo.InvariantCulture)}");
        }
        logger.SetVerbosity(@params.Verbosity);
        return null;
    }

    private (LsProto.RelatedFullDocumentDiagnosticReportOrUnchangedDocumentDiagnosticReport? Result, Exception? Error) HandleDocumentDiagnostic(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DocumentDiagnosticParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        return (ls.ProvideDiagnostics(ctx, @params.TextDocument.Uri), null);
    }

    private (LsProto.HoverOrNull? Result, Exception? Error) HandleHover(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.HoverParams @params)
    {
        return (ls.ProvideHover(ctx, @params), null);
    }

    private (LsProto.RangeOrPrepareRenamePlaceholderOrPrepareRenameDefaultBehaviorOrNull? Result, Exception? Error) HandlePrepareRename(RequestContext ctx, global::Ts.Ls.LanguageService languageService, LsProto.PrepareRenameParams @params)
    {
        global::Ts.Ls.RenameInfo info = languageService.GetRenameInfo(ctx, "" /*newName*/, @params.TextDocument.Uri, @params.Position);
        if (!info.CanRename)
        {
            return (new LsProto.RangeOrPrepareRenamePlaceholderOrPrepareRenameDefaultBehaviorOrNull(), new global::Ts.LsProto.UserFacingRequestFailedException(info.LocalizedErrorMessage));
        }
        return (LsProto.RangeOrPrepareRenamePlaceholderOrPrepareRenameDefaultBehaviorOrNull.FromPrepareRenamePlaceholder(new LsProto.PrepareRenamePlaceholder
        {
            Range = info.TriggerSpan!,
            Placeholder = info.DisplayName,
        }), null);
    }

    // server.go:1386-1421 — handleRename.
    private (LsProto.WorkspaceEditOrNull? Result, Exception? Error) HandleRename(RequestContext ctx, LsProto.RenameParams @params, RequestMessage req)
    {
        var (defaultLs, orchestrator, err) = GetLanguageServiceAndCrossProjectOrchestrator(ctx, @params.TextDocument.Uri, req);
        if (err is not null)
        {
            return (new LsProto.WorkspaceEditOrNull(), err);
        }
        global::Ts.Ls.RenameInfo info = defaultLs!.GetRenameInfo(ctx, @params.NewName, @params.TextDocument.Uri, @params.Position);
        if (info.CanRename && info.FileToRename != "")
        {
            // We send a `willRenameFiles` request if the client allows;
            // otherwise we directly compute the edits for renaming the file.
            if (global::Ts.Ls.LanguageService.ClientSupportsWillRenameFiles(clientCapabilities))
            {
                LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile[] documentChanges =
                [
                    LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile.FromRenameFile(new LsProto.RenameFile
                    {
                        Kind = new LsProto.StringLiteralRename(),
                        OldUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(info.FileToRename),
                        NewUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(info.NewFileName),
                    }),
                ];
                return (LsProto.WorkspaceEditOrNull.FromWorkspaceEdit(new LsProto.WorkspaceEdit
                {
                    DocumentChanges = documentChanges,
                }), null);
            }
            var renameFilesParams = new LsProto.RenameFilesParams
            {
                Files =
                [
                    new LsProto.FileRename
                    {
                        OldUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(info.FileToRename).Value,
                        NewUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(info.NewFileName).Value,
                    },
                ],
            };
            return HandleWillRenameFilesWorker(ctx, renameFilesParams, req, true /*sendRenameFile*/);
        }

        return (defaultLs.ProvideRename(ctx, @params, orchestrator!), null);
    }

    private (LsProto.WorkspaceEditOrNull? Result, Exception? Error) HandleWillRenameFiles(RequestContext ctx, LsProto.RenameFilesParams @params, RequestMessage msg)
    {
        return HandleWillRenameFilesWorker(ctx, @params, msg, false /*sendRenameFile*/);
    }

    // Go: editKey{uri, range_} — lsproto.Range is a class here, so the key spells out
    // its comparable coordinates.
    private readonly record struct EditKey(string Uri, uint StartLine, uint StartCharacter, uint EndLine, uint EndCharacter);

    // server.go:1427-1530 — If `sendRenameFile` is true, the original `willRenameFiles`
    // request is being handled as part of a rename operation where the client doesn't
    // support `willRenameFiles`, so we should include the file rename in the edits we
    // return.
    private (LsProto.WorkspaceEditOrNull? Result, Exception? Error) HandleWillRenameFilesWorker(RequestContext ctx, LsProto.RenameFilesParams @params, RequestMessage? _, bool sendRenameFile)
    {
        if (@params.Files.Length == 0)
        {
            return (new LsProto.WorkspaceEditOrNull(), null);
        }

        var uris = new List<LsProto.DocumentUri>(@params.Files.Length);
        foreach (LsProto.FileRename file in @params.Files)
        {
            uris.Add(new LsProto.DocumentUri(file.OldUri));
        }

        if (uris.Count == 0)
        {
            return (new LsProto.WorkspaceEditOrNull(), null);
        }

        global::Ts.Ls.LanguageService[] services = session!.GetLanguageServicesForDocuments(ctx, uris.ToArray());

        var seenEdits = new Dictionary<EditKey, string>();
        var seenRenames = new HashSet<LsProto.DocumentUri>();
        var documentChanges = new List<LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile>();

        foreach (global::Ts.Ls.LanguageService languageService in services)
        {
            foreach (LsProto.FileRename file in @params.Files)
            {
                List<LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile> changes = languageService.GetEditsForFileRename(ctx.Cancellation, new LsProto.DocumentUri(file.OldUri), new LsProto.DocumentUri(file.NewUri));
                foreach (LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile change in changes)
                {
                    if (change.RenameFile is not null)
                    {
                        if (seenRenames.Add(change.RenameFile.OldUri))
                        {
                            documentChanges.Add(change);
                        }
                    }
                    else if (change.TextDocumentEdit is not null)
                    {
                        LsProto.DocumentUri uri = change.TextDocumentEdit.TextDocument.Uri;
                        var deduped = new List<LsProto.TextEditOrAnnotatedTextEditOrSnippetTextEdit>();
                        foreach (LsProto.TextEditOrAnnotatedTextEditOrSnippetTextEdit edit in change.TextDocumentEdit.Edits)
                        {
                            if (edit.TextEdit is not null)
                            {
                                LsProto.Range range = edit.TextEdit.Range;
                                var key = new EditKey(uri.Value, range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
                                if (seenEdits.TryGetValue(key, out string? prev) && prev == edit.TextEdit.NewText)
                                {
                                    continue;
                                }
                                seenEdits[key] = edit.TextEdit.NewText;
                            }
                            deduped.Add(edit);
                        }
                        if (deduped.Count > 0)
                        {
                            documentChanges.Add(LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile.FromTextDocumentEdit(new LsProto.TextDocumentEdit
                            {
                                TextDocument = change.TextDocumentEdit.TextDocument,
                                Edits = deduped.ToArray(),
                            }));
                        }
                    }
                }
            }
        }

        if (sendRenameFile)
        {
            foreach (LsProto.FileRename file in @params.Files)
            {
                documentChanges.Add(LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile.FromRenameFile(new LsProto.RenameFile
                {
                    Kind = new LsProto.StringLiteralRename(),
                    OldUri = new LsProto.DocumentUri(file.OldUri),
                    NewUri = new LsProto.DocumentUri(file.NewUri),
                }));
            }
        }

        if (documentChanges.Count == 0)
        {
            return (new LsProto.WorkspaceEditOrNull(), null);
        }

        if (global::Ts.Ls.LanguageService.ClientSupportsDocumentChanges(clientCapabilities))
        {
            return (LsProto.WorkspaceEditOrNull.FromWorkspaceEdit(new LsProto.WorkspaceEdit
            {
                DocumentChanges = documentChanges.ToArray(),
            }), null);
        }

        // map-iter: 序无关 — Go builds a map whose JSON key order is unspecified; the C#
        // dictionary keeps first-insertion order of the document changes.
        var changesByUri = new Dictionary<LsProto.DocumentUri, List<LsProto.TextEdit>>();
        foreach (LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile change in documentChanges)
        {
            if (change.TextDocumentEdit is not null)
            {
                LsProto.DocumentUri uri = change.TextDocumentEdit.TextDocument.Uri;
                foreach (LsProto.TextEditOrAnnotatedTextEditOrSnippetTextEdit edit in change.TextDocumentEdit.Edits)
                {
                    if (edit.TextEdit is not null)
                    {
                        if (!changesByUri.TryGetValue(uri, out List<LsProto.TextEdit>? edits))
                        {
                            edits = [];
                            changesByUri[uri] = edits;
                        }
                        edits.Add(edit.TextEdit);
                    }
                }
            }
        }

        var changesMap = new Dictionary<LsProto.DocumentUri, LsProto.TextEdit[]>(changesByUri.Count);
        // map-iter: 序无关 — copies into the wire map preserving insertion order.
        foreach (var (uri, edits) in changesByUri)
        {
            changesMap[uri] = edits.ToArray();
        }
        return (LsProto.WorkspaceEditOrNull.FromWorkspaceEdit(new LsProto.WorkspaceEdit
        {
            Changes = changesMap,
        }), null);
    }

    private (LsProto.SignatureHelpOrNull? Result, Exception? Error) HandleSignatureHelp(RequestContext ctx, global::Ts.Ls.LanguageService languageService, LsProto.SignatureHelpParams @params)
    {
        return (languageService.ProvideSignatureHelp(
            ctx,
            @params.TextDocument.Uri,
            @params.Position,
            @params.Context), null);
    }

    private (LsProto.FoldingRangesOrNull? Result, Exception? Error) HandleFoldingRange(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.FoldingRangeParams @params)
    {
        return (ls.ProvideFoldingRange(ctx, @params.TextDocument.Uri), null);
    }

    private (LsProto.VSOnAutoInsertResponseItemOrNull? Result, Exception? Error) HandleVSOnAutoInsert(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.VSOnAutoInsertParams @params)
    {
        return (ls.ProvideOnAutoInsert(ctx, @params), null);
    }

    private (LsProto.LinkedEditingRangesOrNull? Result, Exception? Error) HandleLinkedEditingRange(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.LinkedEditingRangeParams @params)
    {
        return (ls.ProvideLinkedEditingRange(ctx, @params), null);
    }

    private (LsProto.LocationOrLocationsOrDefinitionLinksOrNull? Result, Exception? Error) HandleDefinition(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DefinitionParams @params)
    {
        return (ls.ProvideDefinition(ctx, @params.TextDocument.Uri, @params.Position), null);
    }

    private (LsProto.LocationOrLocationsOrDefinitionLinksOrNull? Result, Exception? Error) HandleSourceDefinition(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.TextDocumentPositionParams @params)
    {
        return (ls.ProvideSourceDefinition(ctx, @params.TextDocument.Uri, @params.Position), null);
    }

    private (LsProto.LocationOrLocationsOrDefinitionLinksOrNull? Result, Exception? Error) HandleTypeDefinition(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.TypeDefinitionParams @params)
    {
        return (ls.ProvideTypeDefinition(ctx, @params.TextDocument.Uri, @params.Position), null);
    }

    private (LsProto.CompletionItemsOrListOrNull? Result, Exception? Error) HandleCompletion(RequestContext ctx, global::Ts.Ls.LanguageService languageService, LsProto.CompletionParams @params)
    {
        return (languageService.ProvideCompletion(
            ctx,
            @params.TextDocument.Uri,
            @params.Position,
            @params.Context), null);
    }

    // server.go:1578-1590 — handleCompletionItemResolve.
    private (LsProto.CompletionItem? Result, Exception? Error) HandleCompletionItemResolve(RequestContext ctx, LsProto.CompletionItem @params, RequestMessage reqMsg)
    {
        LsProto.CompletionItemData? data = @params.Data;
        var (languageService, err) = session!.GetLanguageService(ctx, global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(data!.FileName));
        if (err is not null)
        {
            return (null, err);
        }
        try
        {
            return (languageService!.ResolveCompletionItem(
                ctx,
                @params,
                data), null);
        }
        catch (Exception panic) when (!IsErrorValue(panic))
        {
            Recover(reqMsg, panic);
            return (null, null);
        }
    }

    private (LsProto.TextEditsOrNull? Result, Exception? Error) HandleDocumentFormat(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DocumentFormattingParams @params)
    {
        return (ls.ProvideFormatDocument(
            ctx,
            @params.TextDocument.Uri,
            @params.Options), null);
    }

    private (LsProto.TextEditsOrNull? Result, Exception? Error) HandleDocumentRangeFormat(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DocumentRangeFormattingParams @params)
    {
        return (ls.ProvideFormatDocumentRange(
            ctx,
            @params.TextDocument.Uri,
            @params.Options,
            @params.Range), null);
    }

    private (LsProto.TextEditsOrNull? Result, Exception? Error) HandleDocumentOnTypeFormat(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DocumentOnTypeFormattingParams @params)
    {
        return (ls.ProvideFormatDocumentOnType(
            ctx,
            @params.TextDocument.Uri,
            @params.Options,
            @params.Position,
            @params.Ch), null);
    }

    // server.go:1619-1634 — handleWorkspaceSymbol.
    private (LsProto.SymbolInformationsOrWorkspaceSymbolsOrNull? Result, Exception? Error) HandleWorkspaceSymbol(RequestContext ctx, LsProto.WorkspaceSymbolParams @params, RequestMessage reqMsg)
    {
        LsProto.SymbolInformationsOrWorkspaceSymbolsOrNull? resp = null;
        Exception? lsErr = null;
        session!.WithSnapshotLoadingProjectTree(ctx, null, snapshot =>
        {
            try
            {
                List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
                var programs = new global::Ts.Compiler.Program[projects.Count];
                for (int i = 0; i < projects.Count; i++)
                {
                    programs[i] = projects[i].GetProgram()!; // Go: core.Map(..., (*Project).GetProgram)
                }
                (resp, lsErr) = Invoke(() => ((LsProto.SymbolInformationsOrWorkspaceSymbolsOrNull?)global::Ts.Ls.LanguageService.ProvideWorkspaceSymbols(
                    ctx,
                    programs,
                    snapshot.Converters(),
                    snapshot.UserPreferences(),
                    @params.Query), (Exception?)null));
            }
            catch (Exception panic)
            {
                Recover(reqMsg, panic);
            }
        });
        return (resp, lsErr);
    }

    private (LsProto.SymbolInformationsOrDocumentSymbolsOrNull? Result, Exception? Error) HandleDocumentSymbol(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DocumentSymbolParams @params)
    {
        return (ls.ProvideDocumentSymbols(ctx, @params.TextDocument.Uri), null);
    }

    private (LsProto.DocumentHighlightsOrNull? Result, Exception? Error) HandleDocumentHighlight(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.DocumentHighlightParams @params)
    {
        return (ls.ProvideDocumentHighlights(ctx, @params.TextDocument.Uri, @params.Position), null);
    }

    private (LsProto.MultiDocumentHighlightsOrNull? Result, Exception? Error) HandleMultiDocumentHighlight(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.MultiDocumentHighlightParams @params)
    {
        return (ls.ProvideMultiDocumentHighlights(ctx, @params.TextDocument.Uri, @params.Position, @params.FilesToSearch), null);
    }

    private (LsProto.SelectionRangesOrNull? Result, Exception? Error) HandleSelectionRange(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.SelectionRangeParams @params)
    {
        return (ls.ProvideSelectionRanges(ctx, @params), null);
    }

    private (LsProto.CommandOrCodeActionArrayOrNull? Result, Exception? Error) HandleCodeAction(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.CodeActionParams @params)
    {
        return (ls.ProvideCodeActions(ctx, @params), null);
    }

    private (LsProto.InlayHintsOrNull? Result, Exception? Error) HandleInlayHint(
        RequestContext ctx,
        global::Ts.Ls.LanguageService languageService,
        LsProto.InlayHintParams @params)
    {
        return (languageService.ProvideInlayHint(ctx, @params), null);
    }

    private (LsProto.CodeLensesOrNull? Result, Exception? Error) HandleCodeLens(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.CodeLensParams @params)
    {
        return (ls.ProvideCodeLenses(ctx.Cancellation, @params.TextDocument.Uri), null);
    }

    // server.go:1668-1690 — handleCodeLensResolve.
    private (LsProto.CodeLens? Result, Exception? Error) HandleCodeLensResolve(RequestContext ctx, LsProto.CodeLens codeLens, RequestMessage reqMsg)
    {
        var (defaultLs, orchestrator, err) = GetLanguageServiceAndCrossProjectOrchestrator(ctx, codeLens.Data!.Uri, reqMsg);
        if (CtxErr(ctx) is { } ctxErr)
        {
            return (null, ctxErr);
        }
        if (err is not null)
        {
            // This can happen if a codeLens/resolve request comes in after a program change.
            // While it's true that handlers should latch onto a specific snapshot
            // while processing requests, we just set `Data.Uri` based on
            // some older snapshot's contents. The content could have been modified,
            // or the file itself could have been removed from the session entirely.
            // Note this won't bail out on every change, but will prevent crashing
            // based on non-existent files and line maps from shortened files.
            return (codeLens, ErrorCodeError(ErrorCode.ContentModified));
        }
        try
        {
            return (defaultLs!.ResolveCodeLens(
                ctx.Cancellation,
                codeLens,
                initializationOptions.CodeLensShowLocationsCommandName,
                orchestrator!), null);
        }
        catch (Exception panic) when (!IsErrorValue(panic))
        {
            Recover(reqMsg, panic);
            return (null, null);
        }
    }

    private (LsProto.CallHierarchyItemsOrNull? Result, Exception? Error) HandlePrepareCallHierarchy(
        RequestContext ctx,
        global::Ts.Ls.LanguageService languageService,
        LsProto.CallHierarchyPrepareParams @params)
    {
        return (languageService.ProvidePrepareCallHierarchy(ctx, @params.TextDocument.Uri, @params.Position), null);
    }

    private (LsProto.CallHierarchyIncomingCallsOrNull? Result, Exception? Error) HandleCallHierarchyIncomingCalls(
        RequestContext ctx,
        LsProto.CallHierarchyIncomingCallsParams @params,
        RequestMessage reqMsg)
    {
        var (defaultLs, orchestrator, err) = GetLanguageServiceAndCrossProjectOrchestrator(ctx, @params.Item.Uri, reqMsg);
        if (err is not null)
        {
            return (new LsProto.CallHierarchyIncomingCallsOrNull(), err);
        }
        return (defaultLs!.ProvideCallHierarchyIncomingCalls(ctx, @params.Item, orchestrator!), null);
    }

    private (LsProto.CallHierarchyOutgoingCallsOrNull? Result, Exception? Error) HandleCallHierarchyOutgoingCalls(
        RequestContext ctx,
        LsProto.CallHierarchyOutgoingCallsParams @params,
        RequestMessage _)
    {
        var (languageService, err) = session!.GetLanguageService(ctx, @params.Item.Uri);
        if (err is not null)
        {
            return (new LsProto.CallHierarchyOutgoingCallsOrNull(), err);
        }
        return (languageService!.ProvideCallHierarchyOutgoingCalls(ctx, @params.Item), null);
    }

    private (LsProto.SemanticTokensOrNull? Result, Exception? Error) HandleSemanticTokensFull(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.SemanticTokensParams @params)
    {
        return (ls.ProvideSemanticTokens(ctx, @params.TextDocument.Uri), null);
    }

    private (LsProto.SemanticTokensOrNull? Result, Exception? Error) HandleSemanticTokensRange(RequestContext ctx, global::Ts.Ls.LanguageService ls, LsProto.SemanticTokensRangeParams @params)
    {
        return (ls.ProvideSemanticTokensRange(ctx, @params.TextDocument.Uri, @params.Range), null);
    }

    // server.go:1732-1798 — handleInitializeAPISession.
    private (LsProto.InitializeAPISessionResult? Result, Exception? Error) HandleInitializeAPISession(RequestContext ctx, LsProto.InitializeAPISessionParams @params, RequestMessage req)
    {
        lock (apiSessionsMu)
        {
            apiSessions ??= new Dictionary<string, global::Ts.Api.Session>(StringComparer.Ordinal);

            global::Ts.Api.Session apiSession = global::Ts.Api.Session.NewSession(session!, null);

            // Use provided pipe path or generate a unique one
            string pipePath;
            if (@params.Pipe is { Length: > 0 } pipe)
            {
                pipePath = pipe;
            }
            else
            {
                pipePath = GenerateAPIPipePath();
            }

            global::Ts.Api.PipeTransport transport;
            try
            {
                transport = global::Ts.Api.PipeTransport.NewPipeTransport(pipePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                return (null, Wrap("failed to create API transport", e));
            }

            // Start accepting connections in the background
            _ = Task.Run(() =>
            {
                try
                {
                    Stream rwc;
                    try
                    {
                        rwc = transport.Accept();
                    }
                    catch (Exception acceptErr)
                    {
                        transport.Close();
                        logger.Errorf("API session {0}: failed to accept connection: {1}", apiSession.Id(), acceptErr.Message);
                        return;
                    }
                    transport.Close();

                    // Create a cancellable context for the API connection
                    using var apiCancel = CancellationTokenSource.CreateLinkedTokenSource(backgroundCtx.Cancellation);
                    try
                    {
                        var conn = new global::Ts.Api.AsyncConn(rwc, apiSession);
                        Exception? apiErr = conn.Run(backgroundCtx with { Cancellation = apiCancel.Token });
                        if (apiErr is not null)
                        {
                            logger.Errorf("API session {0}: {1}", apiSession.Id(), apiErr.Message);
                        }
                    }
                    catch (Exception r)
                    {
                        // Run the connection with panic recovery
                        logger.Errorf("API session {0}: panic: {1}\n{2}", apiSession.Id(), r.Message, r.ToString());
                        // Cancel the context to shut down the connection
                        apiCancel.Cancel();
                        // Close the underlying connection
                        rwc.Dispose();
                    }
                }
                finally
                {
                    apiSession.Close();
                    RemoveAPISession(apiSession.Id());
                }
            });

            apiSessions[apiSession.Id()] = apiSession;

            return (new LsProto.InitializeAPISessionResult
            {
                SessionId = apiSession.Id(),
                Pipe = pipePath,
            }, null);
        }
    }

    private static string GenerateAPIPipePath()
    {
        // Generate a high-entropy path using time and random source
        long now = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        ulong rnd = (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.Next() << 63);
        return global::Ts.Api.TransportFunctions.GeneratePipePath(
            "tsgo-api-" + now.ToString("x", CultureInfo.InvariantCulture) + "-" + rnd.ToString("x", CultureInfo.InvariantCulture));
    }

    private void RemoveAPISession(string id)
    {
        lock (apiSessionsMu)
        {
            apiSessions?.Remove(id);
        }
    }

    // !!! temporary; remove when we have `handleDidChangeConfiguration`/implicit project config support
    public void SetCompilerOptionsForInferredProjects(RequestContext ctx, global::Ts.Core.CompilerOptions? options)
    {
        compilerOptionsForInferredProjects = options;
        session?.DidChangeCompilerOptionsForInferredProjects(ctx, options);
    }

    // NpmInstall implements ata.NpmExecutor
    public (byte[] Output, Exception? Error) NpmInstall(string cwd, string[] args)
    {
        return npmInstall!(cwd, args);
    }

    // Developer/debugging command handlers

    private (LsProto.Null Result, Exception? Error) HandleRunGC(RequestContext _, LsProto.NoParams __, RequestMessage ___)
    {
        global::Ts.Profiling.Pprof.RunGC();
        logger.Info("GC triggered");
        return (default, null);
    }

    // PORT-NOTE: Pprof.SaveHeapProfile/SaveAllocProfile throw where Go returns an error
    // (Profiling/Pprof.cs); the throw is folded back into the error channel here.
    private (LsProto.ProfileResult? Result, Exception? Error) HandleSaveHeapProfile(RequestContext _, LsProto.ProfileParams @params, RequestMessage __)
    {
        string filePath;
        try
        {
            filePath = global::Ts.Profiling.Pprof.SaveHeapProfile(@params.Dir);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return (null, err);
        }
        logger.Info("Heap profile saved to: ", filePath);
        return (new LsProto.ProfileResult { File = filePath }, null);
    }

    private (LsProto.ProfileResult? Result, Exception? Error) HandleSaveAllocProfile(RequestContext _, LsProto.ProfileParams @params, RequestMessage __)
    {
        string filePath;
        try
        {
            filePath = global::Ts.Profiling.Pprof.SaveAllocProfile(@params.Dir);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return (null, err);
        }
        logger.Info("Allocation profile saved to: ", filePath);
        return (new LsProto.ProfileResult { File = filePath }, null);
    }

    private (LsProto.Null Result, Exception? Error) HandleStartCPUProfile(RequestContext _, LsProto.ProfileParams @params, RequestMessage __)
    {
        string? err = cpuProfiler.StartCPUProfile(@params.Dir);
        if (err is not null)
        {
            return (default, new InvalidOperationException(err));
        }
        logger.Info("CPU profiling started, will save to: ", @params.Dir);
        return (default, null);
    }

    private (LsProto.ProfileResult? Result, Exception? Error) HandleStopCPUProfile(RequestContext _, LsProto.NoParams __, RequestMessage ___)
    {
        if (!cpuProfiler.TryStopCPUProfile(out string filePath, out string? err))
        {
            return (null, new InvalidOperationException(err));
        }
        logger.Info("CPU profile saved to: ", filePath);
        return (new LsProto.ProfileResult { File = filePath }, null);
    }

    // server.go:1870-1883 — handleProjectInfo.
    private (LsProto.ProjectInfoResult? Result, Exception? Error) HandleProjectInfo(RequestContext ctx, LsProto.ProjectInfoParams @params, RequestMessage _)
    {
        LsProto.DocumentUri uri = @params.TextDocument.Uri;
        var (defaultProject, _, _, err) = session!.GetLanguageServiceAndProjectsForFile(ctx, uri);
        if (err is not null)
        {
            return (null, err);
        }
        string configFilePath = "";
        if (defaultProject is not null && defaultProject.Kind == global::Ts.Project.Kind.Configured)
        {
            configFilePath = defaultProject.Name();
        }
        return (new LsProto.ProjectInfoResult
        {
            ConfigFilePath = configFilePath,
        }, null);
    }

    // ---- helpers (Go language-level constructs) ----

    // Go: an lsproto.ErrorCode used directly as an error value (its Error() text).
    private static global::Ts.LsProto.LspErrorException ErrorCodeError(ErrorCode code) =>
        new(code, global::Ts.LsProto.LspErrorException.CodeToString(code));

    // Go: ctx.Err().
    private static Exception? CtxErr(RequestContext ctx) =>
        ctx.Cancellation.IsCancellationRequested ? new OperationCanceledException(ctx.Cancellation) : null;

    // Go: fmt.Errorf("<prefix>: %w", err).
    private static Exception Wrap(string prefix, Exception err) =>
        new InvalidOperationException(prefix + ": " + err.Message, err);

    // Go: errors.AsType[userFacingRequestFailedError](err).
    private static bool IsUserFacing(Exception? err)
    {
        while (err is not null)
        {
            if (err is global::Ts.LsProto.UserFacingRequestFailedException)
            {
                return true;
            }
            err = err.InnerException;
        }
        return false;
    }

    // The exceptions that stand for Go error *values* returned by the language service
    // (as opposed to panics); see the file header.
    private static bool IsErrorValue(Exception e) =>
        e is global::Ts.LsProto.LspErrorException
            or global::Ts.LsProto.UserFacingRequestFailedException
            or OperationCanceledException
            or global::Ts.Ls.NeedsAutoImportsException;

    private static (T Result, Exception? Error) Invoke<T>(Func<(T Result, Exception? Error)> fn)
    {
        try
        {
            return fn();
        }
        catch (Exception e) when (IsErrorValue(e))
        {
            return (default!, e);
        }
    }

    // Go: lsproto.UnmarshalParams[Req](req).
    private static (TReq Params, Exception? Error) UnmarshalParams<TReq>(RequestMessage req, JsonTypeInfo<TReq> typeInfo)
    {
        try
        {
            return (global::Ts.LsProto.LsProtoUnmarshal.UnmarshalParams(req.Params, typeInfo), null);
        }
        catch (global::Ts.LsProto.LspErrorException e)
        {
            return (default!, e);
        }
    }

    // Go: json.MarshalIndent(v, "", "\t").
    private static string MarshalIndent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentCharacter = '\t', IndentSize = 1 }))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static long NowUnixMilli() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
