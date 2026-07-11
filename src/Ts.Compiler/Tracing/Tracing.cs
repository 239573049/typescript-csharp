// Ported from tsgo internal/tracing/tracing.go (763 LOC).
//
// PORT-NOTE: 非 1:1，EventSource 重写 (plan/06-module-inventory.md §9.2).
//   The tsgo package writes Chrome `trace_event` JSON via a goroutine-shared
//   strings.Builder and a per-checker types.json dump. The plan's C# 归宿 is a
//   .NET diagnostics surface: the *event emission side* is mirrored onto a
//   System.Diagnostics.Tracing.EventSource (EventPipe / dotnet-trace consumable),
//   while the byte-faithful Chrome-trace JSON writer is retained (hand-written
//   Utf8JsonWriter, per plan/07 §4.1) because
//     (a) tracing_test.go asserts on the trace.json bytes, and
//     (b) plan/06 §13.4 leaves --generateTrace / types.json fidelity 待澄清 —
//         keeping a faithful writer preserves that door without a rewrite later.
//   Both sides run through an injectable ITraceSink so tests can assert the
//   Push/Pop event sequence deterministically without touching disk (plan/08).
//
// The public shape below (ITracer / ITracedType / Tracing / TracingExtensions /
// Phase) is the stage-4/5 interface skeleton that checker/compiler already
// reference; its signatures MUST stay stable (plan/06 §9.2 前置). Only the
// implementation is filled in here.
//
// §1.5 全限定：本文件 namespace Ts.Tracing 引用兄弟命名空间中与末段同名的类
// (Ts.TsPath.TsPath) 必须 `global::Ts.TsPath.TsPath.*`，否则外层 Ts 命名空间
// 遮蔽 using-alias → CS0234。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Ts.Ast;
using Ts.Vfs;

namespace Ts.Tracing;

// Tracer is an interface for recording types during type checking.
// Each checker should have its own Tracer instance to avoid sharing types between checkers.
public interface ITracer
{
    // RecordType records a type for later dumping.
    public void RecordType(ITracedType type);

    // DumpTypes writes all recorded types to disk.
    public void DumpTypes();
}

// TracedType is an interface that represents a type that can be traced.
// This allows the tracing package to work with types from the checker package
// without creating a circular dependency.
public interface ITracedType
{
    public uint Id { get; }

    public string[]? FormatFlags();

    public bool IsConditional { get; }

    public Symbol? Symbol { get; }

    public Symbol? AliasSymbol { get; }

    public ITracedType[]? AliasTypeArguments();

    // Type-specific data accessors
    public string IntrinsicName { get; }

    public ITracedType[]? UnionTypes();

    public ITracedType[]? IntersectionTypes();

    public ITracedType? IndexType();

    public ITracedType? IndexedAccessObjectType();

    public ITracedType? IndexedAccessIndexType();

    public ITracedType? ConditionalCheckType();

    public ITracedType? ConditionalExtendsType();

    public ITracedType? ConditionalTrueType();

    public ITracedType? ConditionalFalseType();

    public ITracedType? SubstitutionBaseType();

    public ITracedType? SubstitutionConstraintType();

    public ITracedType? ReferenceTarget();

    public ITracedType[]? ReferenceTypeArguments();

    public Node? ReferenceNode { get; }

    public ITracedType? ReverseMappedSourceType();

    public ITracedType? ReverseMappedMappedType();

    public ITracedType? ReverseMappedConstraintType();

    public ITracedType? EvolvingArrayElementType();

    public ITracedType? EvolvingArrayFinalType();

    public bool IsTuple { get; }

    public Node? Pattern { get; }

    public object? RecursionIdentity();

    // Display is an optional string representation of the type
    public string Display();
}

// TraceRecord represents metadata about a single trace file.
// Go: tracing.go:72-77.
public sealed class TraceRecord
{
    public string ConfigFilePath = ""; // json: "configFilePath,omitzero"
    public string TracePath = "";      // json: "tracePath,omitzero"
    public string TypesPath = "";      // json: "typesPath,omitzero"
    public int CheckerID;              // json: "checkerId"
}

// Phase represents a tracing phase.
// PORT-NOTE: kept as a `readonly record struct` wrapper (stage-0 shell shape) rather
// than a bare `enum`/`string` alias so the interface skeleton stays byte-stable for
// checker/compiler; the underlying Value is the Go `Phase string` value verbatim.
public readonly record struct Phase(string Value)
{
    public static readonly Phase Parse = new("parse");
    public static readonly Phase Program = new("program");
    public static readonly Phase Bind = new("bind");
    public static readonly Phase Check = new("check");
    public static readonly Phase CheckTypes = new("checkTypes");
    public static readonly Phase Emit = new("emit");
    public static readonly Phase Session = new("session");
}

// A single Chrome trace_event record (tracing.go:79-89).
// PORT-NOTE: retained as a data object (not just an EventSource payload) so the
// faithful Utf8JsonWriter path can serialize it and tests can round-trip it.
public sealed class TraceEvent
{
    public int PID;                             // json: "pid"
    public int TID;                             // json: "tid"
    public string PH = "";                      // json: "ph"
    public string Cat = "";                     // json: "cat"
    public double TS;                           // json: "ts"
    public string Name = "";                    // json: "name,omitzero"
    public string S = "";                       // json: "s,omitzero" (scope; "g"=global)
    public double? Dur;                         // json: "dur,omitzero"
    public IReadOnlyDictionary<string, object?>? Args; // json: "args,omitzero"
}

// ITraceSink receives fully-formed trace events. The default sink writes both the
// EventSource event and buffers the byte-faithful Chrome-trace JSON; tests inject a
// recording sink for deterministic assertions on the Push/Pop sequence.
// PORT-NOTE: 非 1:1 —— this seam has no Go equivalent; Go writes straight into a
// strings.Builder. It exists to satisfy plan/08 "可注入 sink 做确定性断言".
public interface ITraceSink
{
    void OnEvent(TraceEvent evt);
}

// Tracing manages the overall tracing session including all checkers (tracing.go:112-134).
//
// PORT-NOTE: 非 1:1，EventSource 重写. A parameterless instance is an inert (no-op)
// tracer — this preserves the stage-0 shell semantics (`new Tracing()` was a no-op)
// and mirrors Go's `if tr == nil` guard: an unstarted Tracing swallows every call.
// Use StartTracing(...) for an active session.
public sealed class Tracing
{
    // sampleInterval matches TypeScript's 10ms sampling interval. Events with
    // separateBeginAndEnd=false are only recorded if their duration crosses a
    // 10ms sampling boundary. (tracing.go:91-94)
    private static readonly long SampleIntervalMicros = 10_000; // 10ms in microseconds

    private const string TraceFileName = "trace.json";

    private const int MainThreadID = 1;
    private const int FirstSyntheticThreadID = 2;
    private const int FirstFileThreadID = 1_000_000;
    private const long FileThreadIDHashRange = 1_000_000_000;

    // tracing.go:105
    private static readonly string[] TraceThreadArgKeys =
        ["path", "fileName", "containingFileName", "jsFilePath", "declarationFilePath"];

    // flushThreshold: buffered trace content is flushed to disk past this size. (tracing.go:110)
    private const int FlushThreshold = 256 * 1024;

    private readonly IFileSystem? fs;
    private readonly string traceDir = "";
    private readonly string tracePath = "";
    private readonly string configFilePath = "";
    private readonly List<TraceRecord> legend = [];
    private readonly List<TypeTracer> tracers = [];
    private readonly StringBuilder traceContent = new();
    private int traceStarted; // atomic.Bool → Interlocked (0=false, 1=true)
    // map-iter: 序无关 —— thread-id assignment maps, consumed only by keyed lookup.
    private readonly Dictionary<TraceThreadKey, int> threadIDs = new();
    private readonly Dictionary<int, TraceThreadKey> threadKeys = new();
    private double metadataTS;
    private readonly bool deterministic; // when true, use monotonic counter instead of real time
    private ulong timestampCounter;      // only used in deterministic mode
    private readonly long startTimestamp; // Stopwatch-style ticks; real-time mode only
    private readonly object mu = new();
    // flushErr holds the first error encountered while appending the trace buffer to
    // disk. Once set, subsequent flushes become no-ops and the error is surfaced from
    // StopTracing (tracing.go:129-133). Go returns error; here we retain it and rethrow.
    private Exception? flushErr;

    private readonly ITraceSink sink;

    // Parameterless: inert no-op tracer (shell-compatible). Never "started", so every
    // guarded call short-circuits exactly like Go's nil receiver.
    public Tracing()
    {
        sink = NullTraceSink.Instance;
    }

    private Tracing(IFileSystem fs, string traceDir, string configFilePath, bool deterministic, ITraceSink? sink)
    {
        this.fs = fs;
        this.traceDir = traceDir;
        this.tracePath = global::Ts.TsPath.TsPath.CombinePaths(traceDir, TraceFileName);
        this.configFilePath = configFilePath;
        this.deterministic = deterministic;
        this.startTimestamp = NowMicros();
        this.sink = sink ?? new DefaultTraceSink(this);
    }

    internal bool IsStarted => Volatile.Read(ref traceStarted) != 0;

    // StartTracing creates a new tracing session (tracing.go:152-187).
    // When deterministic is true, timestamps use a monotonic counter instead of real
    // wall-clock time, producing stable output for test baselines.
    //
    // PORT-NOTE: Go returns (*Tracing, error); the only error is a WriteFile failure
    // writing the header. Per plan/05 §4 the error text reaches the compiler at a
    // cross-layer consumption point → thrown IOException (matching IFileSystem.WriteFile,
    // which already throws). No Try-shape needed.
    public static Tracing StartTracing(
        IFileSystem fs,
        string traceDir,
        string configFilePath,
        bool deterministic,
        ITraceSink? sink = null)
    {
        var tr = new Tracing(fs, traceDir, configFilePath, deterministic, sink);
        Volatile.Write(ref tr.traceStarted, 1);

        // Write the trace file header with metadata events.
        tr.traceContent.Append("[\n");

        // Write metadata events (matching TypeScript's format).
        double metaTs = tr.Timestamp();
        tr.metadataTS = metaTs;
        tr.WriteEvent(new TraceEvent
        {
            PID = 1, TID = MainThreadID, PH = "M", Cat = "__metadata", TS = metaTs,
            Name = "process_name", Args = new Dictionary<string, object?> { ["name"] = "tsgo" },
        });
        tr.traceContent.Append(",\n");
        tr.WriteEvent(new TraceEvent
        {
            PID = 1, TID = MainThreadID, PH = "M", Cat = "__metadata", TS = metaTs,
            Name = "thread_name", Args = new Dictionary<string, object?> { ["name"] = "Main" },
        });
        tr.traceContent.Append(",\n");
        tr.WriteEvent(new TraceEvent
        {
            PID = 1, TID = MainThreadID, PH = "M", Cat = "disabled-by-default-devtools.timeline",
            TS = metaTs, Name = "TracingStartedInBrowser",
        });

        // Truncate any existing trace file with the header so subsequent AppendFile calls
        // extend a clean file.
        fs.WriteFile(tr.tracePath, tr.traceContent.ToString());
        tr.traceContent.Clear();

        return tr;
    }

    // NowMicros returns a monotonic timestamp source in microseconds. Uses Stopwatch
    // ticks (high-resolution, monotonic) mapped to microseconds, mirroring Go's
    // time.Now()/time.Since precision for the sampling math.
    private static long NowMicros()
    {
        // Stopwatch.GetTimestamp() is monotonic; convert ticks → microseconds.
        return System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L
            / System.Diagnostics.Stopwatch.Frequency;
    }

    // timestamp returns the current timestamp in microseconds (tracing.go:193-199).
    // In deterministic mode it returns a monotonically increasing counter; otherwise
    // the real elapsed wall-clock time since tracing started, in microseconds.
    private double Timestamp()
    {
        if (deterministic)
        {
            timestampCounter++;
            return timestampCounter;
        }
        return NowMicros() - startTimestamp;
    }

    // writeEvent buffers one event into both the faithful JSON buffer and the sink.
    private void WriteEvent(TraceEvent evt)
    {
        TraceJson.WriteEventTo(traceContent, evt);
        sink.OnEvent(evt);
    }

    // maybeFlushLocked appends the buffered trace content to disk if it has grown past
    // the flush threshold. Caller must hold mu. (tracing.go:215-227)
    private void MaybeFlushLocked()
    {
        if (flushErr != null)
        {
            traceContent.Clear();
            return;
        }
        if (traceContent.Length < FlushThreshold)
        {
            return;
        }
        try
        {
            fs!.AppendFile(tracePath, traceContent.ToString());
        }
        catch (Exception ex)
        {
            flushErr = ex;
        }
        traceContent.Clear();
    }

    // NewTypeTracer creates a new tracer for a specific checker (tracing.go:415-434).
    // The checkerIndex is used to create unique filenames for each checker's output.
    public ITracer NewTypeTracer(int checkerIndex)
    {
        if (!IsStarted || fs == null)
        {
            // Inert tracer: shell no-op semantics preserved.
            return NoOpTracer.Instance;
        }

        lock (mu)
        {
            string typesPath = global::Ts.TsPath.TsPath.CombinePaths(
                traceDir,
                string.Create(CultureInfo.InvariantCulture, $"types_{checkerIndex}.json"));
            var tracer = new TypeTracer(fs, checkerIndex, typesPath);
            tracers.Add(tracer);
            legend.Add(new TraceRecord
            {
                ConfigFilePath = configFilePath,
                TracePath = tracePath,
                TypesPath = typesPath,
                CheckerID = checkerIndex,
            });
            return tracer;
        }
    }

    // StopTracing finalizes the tracing session and writes all output files (tracing.go:437-482).
    public void StopTracing()
    {
        if (!IsStarted || fs == null)
        {
            return;
        }

        // Dump types from all tracers BEFORE acquiring the lock, because
        // DumpTypes → buildTypeDescriptor → Display() → TypeToString can re-enter the
        // checker which calls Push/Pop (which need mu).
        // map-iter: 已排序 —— iterating the ordered tracers list, insertion order.
        foreach (TypeTracer tracer in tracers)
        {
            tracer.DumpTypes();
        }

        lock (mu)
        {
            // Close the trace file(s).
            if (IsStarted)
            {
                if (flushErr != null)
                {
                    traceContent.Clear();
                    Volatile.Write(ref traceStarted, 0);
                    throw new global::System.IO.IOException("failed to flush trace file", flushErr);
                }
                // Flush any remaining buffered content and close the JSON array.
                fs.AppendFile(tracePath, traceContent.ToString() + "\n]\n");
                traceContent.Clear();
                Volatile.Write(ref traceStarted, 0);
            }

            // Sort legend entries by typesPath for deterministic output.
            legend.Sort(static (a, b) => string.CompareOrdinal(a.TypesPath, b.TypesPath));

            // Write the legend file.
            string legendPath = global::Ts.TsPath.TsPath.CombinePaths(traceDir, "legend.json");
            fs.WriteFile(legendPath, TraceJson.MarshalLegend(legend));
        }
    }

    // --- internals used by extension methods (Instant/Push) ---

    internal void InstantCore(Phase phase, string name, IReadOnlyDictionary<string, object?>? args)
    {
        if (!IsStarted)
        {
            return;
        }

        lock (mu)
        {
            // Re-check under the lock: StopTracing may have run between the check above
            // and acquiring the lock.
            if (!IsStarted)
            {
                return;
            }

            double ts = Timestamp();
            int tid = ThreadIDLocked(args);
            traceContent.Append(",\n");
            WriteEvent(new TraceEvent
            {
                PID = 1, TID = tid, PH = "I", Cat = phase.Value, TS = ts, Name = name, S = "g", Args = args,
            });
            MaybeFlushLocked();
        }
    }

    internal Action PushCore(Phase phase, string name, IReadOnlyDictionary<string, object?>? args, bool separateBeginAndEnd)
    {
        if (!IsStarted)
        {
            return NoOp;
        }

        if (separateBeginAndEnd)
        {
            int tid;
            lock (mu)
            {
                if (!IsStarted)
                {
                    return NoOp;
                }
                double ts = Timestamp();
                tid = ThreadIDLocked(args);
                traceContent.Append(",\n");
                WriteEvent(new TraceEvent
                {
                    PID = 1, TID = tid, PH = "B", Cat = phase.Value, TS = ts, Name = name, Args = args,
                });
                MaybeFlushLocked();
            }

            int capturedTid = tid;
            return () =>
            {
                lock (mu)
                {
                    if (!IsStarted)
                    {
                        return;
                    }
                    double endTs = Timestamp();
                    traceContent.Append(",\n");
                    WriteEvent(new TraceEvent
                    {
                        PID = 1, TID = capturedTid, PH = "E", Cat = phase.Value, TS = endTs, Name = name, Args = args,
                    });
                    MaybeFlushLocked();
                }
            };
        }

        // Sampled event: only record if duration crosses a sampling boundary. In
        // deterministic mode, sampled events are skipped entirely to avoid flaky
        // baselines.
        if (deterministic)
        {
            return NoOp;
        }

        long sampleStart = NowMicros();
        // maps.Clone(args): snapshot so a concurrent mutation of the caller's map does
        // not race the deferred end. (tracing.go:305)
        IReadOnlyDictionary<string, object?>? clonedArgs = CloneArgs(args);
        return () =>
        {
            double dur = NowMicros() - sampleStart;
            double startMicros = sampleStart - startTimestamp;
            double intervalMicros = SampleIntervalMicros;
            if (intervalMicros - Mod(startMicros, intervalMicros) > dur)
            {
                return;
            }
            lock (mu)
            {
                if (!IsStarted)
                {
                    return;
                }
                int tid = ThreadIDLocked(clonedArgs);
                traceContent.Append(",\n");
                WriteEvent(new TraceEvent
                {
                    PID = 1, TID = tid, PH = "X", Cat = phase.Value, TS = startMicros,
                    Name = name, Dur = dur, Args = clonedArgs,
                });
                MaybeFlushLocked();
            }
        };
    }

    // math.Mod semantics for the sampling boundary check (Go uses math.Mod on floats).
    private static double Mod(double x, double y) => x - (y * Math.Truncate(x / y));

    private static IReadOnlyDictionary<string, object?>? CloneArgs(IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null)
        {
            return null;
        }
        var copy = new Dictionary<string, object?>(args.Count);
        // map-iter: 序无关 —— cloning key/value pairs; order irrelevant.
        foreach (KeyValuePair<string, object?> kv in args)
        {
            copy[kv.Key] = kv.Value;
        }
        return copy;
    }

    private int ThreadIDLocked(IReadOnlyDictionary<string, object?>? args)
    {
        if (!TraceThreadKeyFromArgs(args, out TraceThreadKey key))
        {
            return MainThreadID;
        }

        if (threadIDs.TryGetValue(key, out int existingTid))
        {
            return existingTid;
        }

        int tid = key.DefaultThreadID();
        while (true)
        {
            if (!threadKeys.TryGetValue(tid, out TraceThreadKey existingKey) || existingKey.Equals(key))
            {
                break;
            }
            tid++;
        }
        threadIDs[key] = tid;
        threadKeys[tid] = key;
        WriteThreadNameEventLocked(tid, key.DisplayName());
        return tid;
    }

    private void WriteThreadNameEventLocked(int tid, string name)
    {
        traceContent.Append(",\n");
        WriteEvent(new TraceEvent
        {
            PID = 1, TID = tid, PH = "M", Cat = "__metadata", TS = metadataTS,
            Name = "thread_name", Args = new Dictionary<string, object?> { ["name"] = name },
        });
    }

    private static bool TraceThreadKeyFromArgs(IReadOnlyDictionary<string, object?>? args, out TraceThreadKey key)
    {
        key = default;
        if (args == null || args.Count == 0)
        {
            return false;
        }

        if (args.TryGetValue("checkerId", out object? checkerIdObj) && checkerIdObj is int checkerId)
        {
            key = new TraceThreadKey(TraceThreadKind.Checker, "", checkerId, HasIndex: true);
            return true;
        }

        // map-iter: 已排序 —— iterating a fixed ordered key list, not the map itself.
        foreach (string k in TraceThreadArgKeys)
        {
            if (args.TryGetValue(k, out object? value) && value is string path && path.Length != 0)
            {
                key = new TraceThreadKey(TraceThreadKind.File, path, 0, HasIndex: false);
                return true;
            }
        }

        return false;
    }

    internal static int StableTraceThreadID(TraceThreadKey key)
    {
        // Go: xxh3-64 over kind + ":" + (index|text). Byte-for-byte the same input,
        // via System.IO.Hashing.XxHash3 (64-bit XXH3), so thread ids match tsgo.
        var hash = new System.IO.Hashing.XxHash3();
        hash.Append(Encoding.UTF8.GetBytes(TraceThreadKindString(key.Kind)));
        hash.Append(":"u8);
        if (key.HasIndex)
        {
            hash.Append(Encoding.UTF8.GetBytes(key.Index.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            hash.Append(Encoding.UTF8.GetBytes(key.Text));
        }
        ulong sum = hash.GetCurrentHashAsUInt64();
        return FirstFileThreadID + (int)(sum % (ulong)FileThreadIDHashRange);
    }

    internal static string TraceThreadKindString(TraceThreadKind kind) => kind switch
    {
        TraceThreadKind.Checker => "checker",
        TraceThreadKind.File => "file",
        _ => "",
    };

    internal static int SyntheticThreadBase => FirstSyntheticThreadID;

    private static readonly Action NoOp = static () => { };

    // No-op tracer for inert sessions (matches the stage-0 shell).
    private sealed class NoOpTracer : ITracer
    {
        internal static readonly NoOpTracer Instance = new();

        private NoOpTracer()
        {
        }

        public void RecordType(ITracedType type)
        {
            _ = type;
        }

        public void DumpTypes()
        {
        }
    }

    // A sink that discards everything (inert session).
    private sealed class NullTraceSink : ITraceSink
    {
        internal static readonly NullTraceSink Instance = new();

        private NullTraceSink()
        {
        }

        public void OnEvent(TraceEvent evt)
        {
            _ = evt;
        }
    }

    // Default sink: forwards to the process EventSource for EventPipe consumption.
    // PORT-NOTE: 非 1:1，EventSource 重写 —— the EventSource is the .NET-native
    // observability surface; the byte-faithful JSON buffer is handled separately by
    // Tracing itself (WriteEvent already appended it before calling the sink).
    private sealed class DefaultTraceSink : ITraceSink
    {
        private readonly Tracing owner;

        internal DefaultTraceSink(Tracing owner)
        {
            this.owner = owner;
        }

        public void OnEvent(TraceEvent evt)
        {
            _ = owner;
            TscTraceEventSource.Log.TraceEvent(evt.PH, evt.Cat, evt.Name, evt.TID, evt.TS);
        }
    }
}

// TraceThreadKind (tracing.go:353-358).
public enum TraceThreadKind
{
    Checker,
    File,
}

// traceThreadKey (tracing.go:360-411). A value key over (kind, text, index, hasIndex).
public readonly record struct TraceThreadKey(TraceThreadKind Kind, string Text, int Index, bool HasIndex)
{
    public int DefaultThreadID()
    {
        if (Kind == TraceThreadKind.Checker && HasIndex && Index >= 0)
        {
            return Tracing.SyntheticThreadBase + Index;
        }
        return Tracing.StableTraceThreadID(this);
    }

    public string DisplayName()
    {
        string kind = Tracing.TraceThreadKindString(Kind);
        if (HasIndex)
        {
            return kind + ":" + Index.ToString(CultureInfo.InvariantCulture);
        }
        return kind + ":" + Text;
    }
}

public static class TracingExtensions
{
    private static readonly Action NoOp = static () => { };

    // Instant records an instant event in the trace (tracing.go:231-251).
    // Safe to call on nil receiver.
    public static void Instant(
        this Tracing? tracing,
        Phase phase,
        string name,
        Dictionary<string, object?>? args)
    {
        tracing?.InstantCore(phase, name, args);
    }

    // Push starts a trace event block on the shared trace buffer (tracing.go:267-323).
    // Safe to call on nil receiver. Safe to call from multiple threads.
    //
    // Returns a function that should be called (typically in a finally) to end the event.
    public static Action Push(
        this Tracing? tracing,
        Phase phase,
        string name,
        Dictionary<string, object?>? args,
        bool separateBeginAndEnd)
    {
        if (tracing == null)
        {
            return NoOp;
        }
        return tracing.PushCore(phase, name, args, separateBeginAndEnd);
    }
}
