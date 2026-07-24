// Ported from tsgo internal/project/logging/logtree.go (whole file).
// PORT-NOTE: Go's nil-receiver methods (Log/Logf/SetVerbose/Verbose/Embed/Fork) become
// null-tolerant extension methods per plan/05; consequently Go's `var _ LogCollector =
// (*LogTree)(nil)` interface assertion is not expressed — registry call sites use the
// concrete LogTree? type.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project.Logging;

// logtree.go:13-18 — logEntry.
internal sealed class LogEntry
{
    internal ulong seq;
    internal DateTime time;
    internal string message = "";
    internal LogTree? child;
}

// logtree.go:31-41 — LogTree.
public sealed class LogTree
{
    private static long seqCounter; // logtree.go:11 — var seq atomic.Uint64

    internal string name = "";
    private readonly object mu = new();
    internal List<LogEntry> logs = new();
    internal LogTree root = null!;
    internal int level;
    internal bool verbose;

    // Only set on root
    internal int count;
    internal int stringLength;

    // logtree.go:20-27 — newLogEntry.
    internal static LogEntry NewLogEntry(LogTree? child, string message)
    {
        return new LogEntry
        {
            seq = (ulong)Interlocked.Increment(ref seqCounter),
            time = DateTime.Now,
            message = message,
            child = child,
        };
    }

    // logtree.go:43-49 — NewLogTree.
    public LogTree(string name)
    {
        this.name = name;
        root = this;
    }

    internal LogTree()
    {
    }

    // logtree.go:51-58 — add.
    internal void Add(LogEntry log)
    {
        // indent + header + message + newline
        Interlocked.Add(ref root.stringLength, level + 15 + log.message.Length + 1);
        Interlocked.Increment(ref root.count);
        lock (mu)
        {
            logs.Add(log);
        }
    }

    // logtree.go:76-78 — IsVerbose.
    public bool IsVerbose()
    {
        return verbose;
    }

    // logtree.go:94-120 — Error/Errorf/Warn/Warnf/Info/Infof (delegate to Log/Logf).
    public void Error(params object?[] msg) => this.Log(msg);

    public void Errorf(string format, params object?[] args) => this.Logf(format, args);

    public void Warn(params object?[] msg) => this.Log(msg);

    public void Warnf(string format, params object?[] args) => this.Logf(format, args);

    public void Info(params object?[] msg) => this.Log(msg);

    public void Infof(string format, params object?[] args) => this.Logf(format, args);

    // logtree.go:141-152 — String.
    public override string ToString()
    {
        if (root != this)
        {
            throw new InvalidOperationException("can only call String on root LogTree");
        }
        var builder = new System.Text.StringBuilder();
        var header = $"======== {name} ========\n";
        builder.EnsureCapacity(stringLength + header.Length);
        builder.Append(header);
        WriteLogsRecursive(builder, "");
        return builder.ToString();
    }

    // logtree.go:154-163 — writeLogsRecursive.
    private void WriteLogsRecursive(System.Text.StringBuilder builder, string indent)
    {
        foreach (var log in logs)
        {
            builder.Append(indent);
            builder.Append(LoggingUtil.FormatTime(log.time));
            builder.Append(' ');
            builder.Append(log.message);
            builder.Append('\n');
            log.child?.WriteLogsRecursive(builder, indent + "\t");
        }
    }
}

public static class LogTreeExtensions
{
    // logtree.go:60-67 — Log.
    public static void Log(this LogTree? c, params object?[] message)
    {
        if (c == null)
        {
            return;
        }
        c.Add(LogTree.NewLogEntry(null, LoggingUtil.Sprint(message)));
    }

    // logtree.go:69-74 — Logf.
    public static void Logf(this LogTree? c, string format, params object?[] args)
    {
        if (c == null)
        {
            return;
        }
        c.Add(LogTree.NewLogEntry(null, LoggingUtil.Sprintf(format, args)));
    }

    // logtree.go:80-85 — SetVerbose.
    public static void SetVerbose(this LogTree? c, bool verbose)
    {
        if (c == null)
        {
            return;
        }
        c.verbose = verbose;
    }

    // logtree.go:87-92 — Verbose.
    public static LogTree? Verbose(this LogTree? c)
    {
        if (c == null || !c.verbose)
        {
            return null;
        }
        return c;
    }

    // logtree.go:122-131 — Embed.
    public static void Embed(this LogTree? c, LogTree logs)
    {
        if (c == null)
        {
            return;
        }
        var count = logs.count;
        Interlocked.Add(ref c.root.stringLength, logs.stringLength + count * c.level);
        Interlocked.Add(ref c.root.count, count);
        c.Add(LogTree.NewLogEntry(logs, logs.name));
    }

    // logtree.go:133-139 — Fork.
    public static LogTree? Fork(this LogTree? c, string message)
    {
        if (c == null)
        {
            return null;
        }
        var child = new LogTree { level = c.level + 1, root = c.root, verbose = c.verbose };
        c.Add(LogTree.NewLogEntry(child, message));
        return child;
    }
}
