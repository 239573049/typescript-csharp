// Ported from tsgo internal/project/logging/logcollector.go (whole file).
// PORT-NOTE: fmt.Stringer collapses onto ToString().

using System;
using System.Globalization;
using System.IO;

namespace Ts.Project.Logging;

// logcollector.go:9-12 — LogCollector.
public interface LogCollector : Logger
{
}

// logcollector.go:14-17 — logCollector (Go embeds logger; C# wraps it).
internal sealed class LogCollectorImpl : LogCollector
{
    internal LoggerImpl logger = null!;
    internal StringWriter builder = null!;

    // logcollector.go:19-21 — String.
    public override string ToString()
    {
        return builder.ToString();
    }

    public void Log(params object?[] msg) => logger.Log(msg);

    public void Logf(string format, params object?[] args) => logger.Logf(format, args);

    public Logger? Verbose() => logger.Verbose() == null ? null : this;

    public bool IsVerbose() => logger.IsVerbose();

    public void SetVerbose(bool verbose) => logger.SetVerbose(verbose);

    public void Error(params object?[] msg) => logger.Error(msg);

    public void Errorf(string format, params object?[] args) => logger.Errorf(format, args);

    public void Warn(params object?[] msg) => logger.Warn(msg);

    public void Warnf(string format, params object?[] args) => logger.Warnf(format, args);

    public void Info(params object?[] msg) => logger.Info(msg);

    public void Infof(string format, params object?[] args) => logger.Infof(format, args);
}

public static partial class LoggingApi
{
    // logcollector.go:23-34 — NewTestLogger.
    public static LogCollector NewTestLogger()
    {
        var builder = new StringWriter();
        return new LogCollectorImpl
        {
            logger = new LoggerImpl
            {
                writer = builder,
                // time.Unix(1349085672, 0) formatted in local time, matching Go.
                prefix = () => LoggingUtil.FormatTime(DateTimeOffset.FromUnixTimeSeconds(1349085672).LocalDateTime),
            },
            builder = builder,
        };
    }
}
