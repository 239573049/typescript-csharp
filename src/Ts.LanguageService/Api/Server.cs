// Ported from tsgo internal/api/server.go (whole file).
//
// PORT-NOTE: the project session constructor call mirrors Go server.go:81-91
// (project.NewSession(&project.SessionInit{...})). Ts.Project.Session /
// SessionInit / SessionOptions land with the project-session wave (they are already
// referenced by Project/Api.cs and Snapshot.cs); the field names here are the Go
// SessionInit shape and are the contract that wave implements.

using System;

namespace Ts.Api;

// StdioServerOptions configures the STDIO-based API server. (server.go:15-36)
public sealed class StdioServerOptions
{
    public System.IO.Stream In = System.IO.Stream.Null;
    public System.IO.Stream Out = System.IO.Stream.Null;
    public System.IO.TextWriter Err = System.IO.TextWriter.Null;
    public string Cwd = "";

    public string DefaultLibraryPath = "";

    // PipePath, if set, listens on a named pipe (Windows) or Unix domain
    // socket instead of using In/Out for communication.
    public string PipePath = "";

    // Callbacks specifies which filesystem operations should be delegated
    // to the client (e.g., "readFile", "fileExists"). Empty means no callbacks.
    public string[] Callbacks = [];

    // Async enables JSON-RPC protocol with async connection handling.
    // When false (default), uses MessagePack protocol with sync connection.
    public bool Async;

    // CollectTiming enables per-request server processing-time measurement.
    // When enabled, the server accumulates each request's processing time into
    // running totals and a recent-request ring buffer. Response messages are
    // left unchanged; the client folds this data into its own timing snapshot
    // on demand via getServerTiming / resetServerTiming requests.
    public bool CollectTiming;
}

// StdioServer runs an API session over STDIO using MessagePack protocol.
// This is the entry point for the synchronous STDIO-based API used by
// native TypeScript tooling integration. (server.go:38-43)
public sealed class StdioServer
{
    private readonly StdioServerOptions options;

    // NewStdioServer creates a new STDIO-based API server. (server.go:46-54)
    public StdioServer(StdioServerOptions options)
    {
        if (options.Cwd == "")
        {
            // Go: panic("StdioServerOptions.Cwd is required").
            throw new InvalidOperationException("StdioServerOptions.Cwd is required");
        }

        this.options = options;
    }

    // Run starts the server and blocks until the connection closes. (server.go:57-124)
    // PORT-NOTE: Go's deferred t.Close() / session.Close() run LIFO at return; the
    // try/finally chain below reproduces that order (session first, transport second).
    public Exception? Run(global::Ts.Core.RequestContext ctx)
    {
        Transport transport;
        bool closeTransport = false;
        if (options.PipePath != "")
        {
            PipeTransport t;
            try
            {
                t = PipeTransport.NewPipeTransport(options.PipePath);
            }
            catch (Exception e)
            {
                return new InvalidOperationException($"failed to create pipe transport: {e.Message}", e);
            }
            transport = t;
            closeTransport = true;
        }
        else
        {
            transport = new StdioTransport(options.In, options.Out);
            closeTransport = true;
        }

        try
        {
            return RunWithTransport(ctx, transport);
        }
        finally
        {
            if (closeTransport)
            {
                transport.Close();
            }
        }
    }

    private Exception? RunWithTransport(global::Ts.Core.RequestContext ctx, Transport transport)
    {
        global::Ts.Vfs.IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(global::Ts.Vfs.OsVfs.OsVfsUtil.FS());

        // Wrap the base FS with callbackFS if callbacks are requested
        CallbackFs? callbackFS = null;
        if (options.Callbacks.Length > 0)
        {
            callbackFS = new CallbackFs(fs, options.Callbacks);
            fs = callbackFS;
        }

        // Go: project.NewSession(&project.SessionInit{...}) (server.go:81-91).
        var projectSession = global::Ts.Project.Session.NewSession(new global::Ts.Project.SessionInit
        {
            BackgroundCtx = ctx,
            Logger = null, // TODO: Add logging support
            FS = fs,
            Options = new global::Ts.Project.SessionOptions
            {
                CurrentDirectory = options.Cwd,
                DefaultLibraryPath = options.DefaultLibraryPath,
                PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
                LoggingEnabled = false,
            },
        });

        Session session = Session.NewSession(projectSession, new Session.SessionOptions
        {
            UseBinaryResponses = !options.Async, // Only msgpack uses binary responses
        });

        // Accept connection from transport (server.go:98-102).
        System.IO.Stream rwc;
        try
        {
            rwc = transport.Accept();
        }
        catch (Exception e)
        {
            session.Close();
            return new InvalidOperationException($"failed to accept connection: {e.Message}", e);
        }

        // Create protocol and connection based on async mode (server.go:104-116).
        Conn conn;
        if (options.Async)
        {
            var protocol = new JsonRpcProtocol(rwc);
            var asyncConn = new AsyncConn(rwc, protocol, session);
            asyncConn.SetCollectTiming(options.CollectTiming);
            conn = asyncConn;
        }
        else
        {
            var protocol = new MessagePackProtocol(rwc);
            var syncConn = new SyncConn(rwc, protocol, session);
            syncConn.SetCollectTiming(options.CollectTiming);
            conn = syncConn;
        }

        // If callbacks are enabled, set the connection on the FS (server.go:118-121).
        callbackFS?.SetConnection(ctx, conn);

        Exception? runErr;
        try
        {
            runErr = conn.Run(ctx);
        }
        finally
        {
            // Go: defer session.Close() (server.go:96) runs after conn.Run returns.
            session.Close();
        }

        return runErr;
    }
}
