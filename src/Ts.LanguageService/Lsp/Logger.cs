// Ported from tsgo internal/lsp/logger.go (whole file).
//
// PORT-NOTE: Go's nil-receiver guards (`if l == nil`) have no C# counterpart
// (plan/05 §10); a null Logger? is simply not dereferenced. IsTracing/SetVerbosity are
// lsp-logger-specific extensions that live on the concrete type, exactly like Go (they
// are not part of the project.Logging.Logger interface).

using System;
using System.IO;
using System.Threading;

namespace Ts.Lsp;

// logger.go:11 — var _ logging.Logger = (*logger)(nil).
public sealed class Logger : global::Ts.Project.Logging.Logger
{
    private readonly Server server;

    // logger.go:14 — mu sync.Mutex.
    private readonly Lock mu = new();

    // logger.go:15 — verbosity lsproto.LogVerbosity.
    private global::Ts.LsProto.LogVerbosity verbosity;

    // logger.go:19-24 — newLogger(server *Server).
    internal Logger(Server server)
    {
        this.server = server;
        verbosity = global::Ts.LsProto.LogVerbosity.Info;
    }

    // logger.go:26-41 — maxVerbosityForMessageType returns the least-verbose log level
    // at which messages of the given LSP MessageType should still be sent.
    internal static global::Ts.LsProto.LogVerbosity MaxVerbosityForMessageType(global::Ts.LsProto.MessageType msgType)
    {
        return msgType switch
        {
            global::Ts.LsProto.MessageType.Error => global::Ts.LsProto.LogVerbosity.Error,
            global::Ts.LsProto.MessageType.Warning => global::Ts.LsProto.LogVerbosity.Warning,
            global::Ts.LsProto.MessageType.Info => global::Ts.LsProto.LogVerbosity.Info,
            global::Ts.LsProto.MessageType.Debug => global::Ts.LsProto.LogVerbosity.Debug,
            _ => global::Ts.LsProto.LogVerbosity.Info,
        };
    }

    // logger.go:43-46 — isValidLogVerbosity reports whether v is one of the defined
    // LogVerbosity values.
    internal static bool IsValidLogVerbosity(global::Ts.LsProto.LogVerbosity v)
    {
        return v >= global::Ts.LsProto.LogVerbosity.Off && v <= global::Ts.LsProto.LogVerbosity.Error;
    }

    // logger.go:48-76 — sendLogMessage.
    private void SendLogMessage(global::Ts.LsProto.MessageType msgType, string message)
    {
        if (!server.InitStarted)
        {
            server.Stderr.WriteLine(message);
            return;
        }

        // Don't send messages that the client will filter out anyway.
        global::Ts.LsProto.LogVerbosity currentVerbosity;
        lock (mu)
        {
            currentVerbosity = verbosity;
        }
        if (currentVerbosity == global::Ts.LsProto.LogVerbosity.Off ||
            currentVerbosity > MaxVerbosityForMessageType(msgType))
        {
            return;
        }

        var notification = global::Ts.LsProto.RequestMessage.New(
            global::Ts.LsProto.Infos.WindowLogMessageInfo,
            new global::Ts.LsProto.LogMessageParams
            {
                Type = msgType,
                Message = message,
            });

        Exception? err = server.Send(notification.Message());
        if (err is not null && server.BackgroundCancellation.IsCancellationRequested)
        {
            server.Stderr.WriteLine(message);
        }
    }

    // logger.go:78-90 — Log / Logf.
    public void Log(params object?[] msg) => SendLogMessage(global::Ts.LsProto.MessageType.Info, global::Ts.Project.Logging.LoggingUtil.Sprint(msg));

    public void Logf(string format, params object?[] args) => SendLogMessage(global::Ts.LsProto.MessageType.Info, global::Ts.Project.Logging.LoggingUtil.Sprintf(format, args));

    // logger.go:92-102 — Verbose.
    public global::Ts.Project.Logging.Logger? Verbose()
    {
        lock (mu)
        {
            if (verbosity == global::Ts.LsProto.LogVerbosity.Off || verbosity > global::Ts.LsProto.LogVerbosity.Debug)
            {
                return null;
            }
        }
        return this;
    }

    // logger.go:104-111 — IsVerbose.
    public bool IsVerbose()
    {
        lock (mu)
        {
            return verbosity >= global::Ts.LsProto.LogVerbosity.Trace && verbosity <= global::Ts.LsProto.LogVerbosity.Debug;
        }
    }

    // logger.go:113-124 — SetVerbose.
    public void SetVerbose(bool verbose)
    {
        lock (mu)
        {
            verbosity = verbose
                ? global::Ts.LsProto.LogVerbosity.Debug
                : global::Ts.LsProto.LogVerbosity.Info;
        }
    }

    // logger.go:126-133 — IsTracing.
    public bool IsTracing()
    {
        lock (mu)
        {
            return verbosity == global::Ts.LsProto.LogVerbosity.Trace;
        }
    }

    // logger.go:135-142 — SetVerbosity.
    public void SetVerbosity(global::Ts.LsProto.LogVerbosity verbosityValue)
    {
        lock (mu)
        {
            verbosity = verbosityValue;
        }
    }

    // logger.go:144-184 — Error / Errorf / Warn / Warnf / Info / Infof.
    public void Error(params object?[] msg) => SendLogMessage(global::Ts.LsProto.MessageType.Error, global::Ts.Project.Logging.LoggingUtil.Sprint(msg));

    public void Errorf(string format, params object?[] args) => SendLogMessage(global::Ts.LsProto.MessageType.Error, global::Ts.Project.Logging.LoggingUtil.Sprintf(format, args));

    public void Warn(params object?[] msg) => SendLogMessage(global::Ts.LsProto.MessageType.Warning, global::Ts.Project.Logging.LoggingUtil.Sprint(msg));

    public void Warnf(string format, params object?[] args) => SendLogMessage(global::Ts.LsProto.MessageType.Warning, global::Ts.Project.Logging.LoggingUtil.Sprintf(format, args));

    public void Info(params object?[] msg) => SendLogMessage(global::Ts.LsProto.MessageType.Info, global::Ts.Project.Logging.LoggingUtil.Sprint(msg));

    public void Infof(string format, params object?[] args) => SendLogMessage(global::Ts.LsProto.MessageType.Info, global::Ts.Project.Logging.LoggingUtil.Sprintf(format, args));
}
