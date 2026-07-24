// Ported from tsgo internal/project/logging/logger.go (whole file).
// PORT-NOTE: Go's nil *logger satisfies Logger with nil-receiver no-ops; C# expresses
// NewNopLogger as a no-op implementation instead.

using System;
using System.Globalization;
using System.IO;

namespace Ts.Project.Logging;

// logger.go:10-35 — Logger.
public interface Logger
{
    // Error logs an error message.
    void Error(params object?[] msg);

    // Errorf logs a formatted error message.
    void Errorf(string format, params object?[] args);

    // Warn logs a warning message.
    void Warn(params object?[] msg);

    // Warnf logs a formatted warning message.
    void Warnf(string format, params object?[] args);

    // Info logs an info message.
    void Info(params object?[] msg);

    // Infof logs a formatted info message.
    void Infof(string format, params object?[] args);

    // Log prints a line to the output writer with a header.
    void Log(params object?[] msg);

    // Logf prints a formatted line to the output writer with a header.
    void Logf(string format, params object?[] args);

    // Verbose returns the logger instance if verbose logging is enabled, and otherwise returns null.
    Logger? Verbose();

    // IsVerbose returns true if verbose logging is enabled, and false otherwise.
    bool IsVerbose();

    // SetVerbose sets the verbose logging flag.
    void SetVerbose(bool verbose);
}

// logger.go:39-44 — logger.
internal sealed class LoggerImpl : Logger
{
    private readonly object mu = new();
    private bool verbose;
    internal TextWriter writer = null!;
    internal Func<string> prefix = null!;

    // logger.go:46-54 — Log.
    public void Log(params object?[] msg)
    {
        lock (mu)
        {
            writer.WriteLine(prefix() + " " + LoggingUtil.Sprint(msg));
        }
    }

    // logger.go:56-63 — Logf.
    public void Logf(string format, params object?[] args)
    {
        lock (mu)
        {
            writer.WriteLine(prefix() + " " + LoggingUtil.Sprintf(format, args));
        }
    }

    // logger.go:65-75 — Verbose.
    public Logger? Verbose()
    {
        lock (mu)
        {
            if (!verbose)
            {
                return null;
            }
            return this;
        }
    }

    // logger.go:77-84 — IsVerbose.
    public bool IsVerbose()
    {
        lock (mu)
        {
            return verbose;
        }
    }

    // logger.go:86-93 — SetVerbose.
    public void SetVerbose(bool verbose)
    {
        lock (mu)
        {
            this.verbose = verbose;
        }
    }

    // logger.go:95-117 — Error/Errorf/Warn/Warnf/Info/Infof.
    public void Error(params object?[] msg) => Log(msg);

    public void Errorf(string format, params object?[] args) => Logf(format, args);

    public void Warn(params object?[] msg) => Log(msg);

    public void Warnf(string format, params object?[] args) => Logf(format, args);

    public void Info(params object?[] msg) => Log(msg);

    public void Infof(string format, params object?[] args) => Logf(format, args);
}

// logger.go:130-133 — NewNopLogger (a no-op Logger that discards all log messages).
internal sealed class NopLogger : Logger
{
    internal static readonly NopLogger Instance = new();

    public void Log(params object?[] msg)
    {
    }

    public void Logf(string format, params object?[] args)
    {
    }

    public Logger? Verbose() => null;

    public bool IsVerbose() => false;

    public void SetVerbose(bool verbose)
    {
    }

    public void Error(params object?[] msg)
    {
    }

    public void Errorf(string format, params object?[] args)
    {
    }

    public void Warn(params object?[] msg)
    {
    }

    public void Warnf(string format, params object?[] args)
    {
    }

    public void Info(params object?[] msg)
    {
    }

    public void Infof(string format, params object?[] args)
    {
    }
}

public static class LoggingUtil
{
    // logger.go:119-127 — NewLogger.
    public static Logger NewLogger(TextWriter output)
    {
        return new LoggerImpl
        {
            writer = output,
            prefix = () => FormatTime(DateTime.Now),
        };
    }

    // logger.go:130-133 — NewNopLogger.
    public static Logger NewNopLogger()
    {
        return NopLogger.Instance;
    }

    // logger.go:135 — formatTime ("[15:04:05.000]").
    internal static string FormatTime(DateTime t)
    {
        return "[" + t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "]";
    }

    // fmt.Sprint: concatenates operands (spaces between operands when neither is a string).
    // Logging call sites pass single strings or interpolations, so simple concat suffices.
    internal static string Sprint(object?[] msg)
    {
        return string.Concat(Array.ConvertAll(msg, m => m?.ToString() ?? ""));
    }

    // fmt.Sprintf over Go verbs — logging call sites are ported with {0}-style format
    // strings, so this is string.Format with the invariant culture.
    internal static string Sprintf(string format, object?[] args)
    {
        return string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
