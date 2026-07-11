using System;
using System.Collections.Generic;
using Ts.Ast;

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

// Tracing manages the overall tracing session including all checkers.
// PORT-NOTE: Stage 0 intentionally provides a no-op implementation. The EventSource
// implementation belongs to stage 9 (plan/06-module-inventory.md section 9.2).
public sealed class Tracing
{
    // NewTypeTracer creates a new tracer for a specific checker.
    // The checkerIndex is used to create unique filenames for each checker's output.
    public ITracer NewTypeTracer(int checkerIndex)
    {
        _ = checkerIndex;
        return NoOpTracer.Instance;
    }

    // StopTracing finalizes the tracing session and writes all output files.
    public void StopTracing()
    {
    }

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
}

public static class TracingExtensions
{
    private static readonly Action NoOp = static () => { };

    // Instant records an instant event in the trace.
    // Safe to call on nil receiver.
    public static void Instant(
        this Tracing? tracing,
        Phase phase,
        string name,
        Dictionary<string, object?>? args)
    {
        _ = tracing;
        _ = phase;
        _ = name;
        _ = args;
    }

    // Push starts a trace event block on the shared trace buffer.
    // Safe to call on nil receiver. Safe to call from multiple goroutines.
    //
    // Returns a function that should be called (typically deferred) to end the event.
    public static Action Push(
        this Tracing? tracing,
        Phase phase,
        string name,
        Dictionary<string, object?>? args,
        bool separateBeginAndEnd)
    {
        _ = tracing;
        _ = phase;
        _ = name;
        _ = args;
        _ = separateBeginAndEnd;
        return NoOp;
    }
}

// Phase represents a tracing phase.
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
