// Per-checker type tracer + type-descriptor construction (tracing.go:484-763).
//
// PORT-NOTE: 非 1:1，EventSource 重写 (plan/06 §9.2). The types_<n>.json dump is the
// one part of --generateTrace that carries structured type data; plan/06 §13.4 leaves
// its byte-fidelity 待澄清, so it is retained faithfully via a hand-written Utf8JsonWriter
// (TraceJson.SerializeTypeDescriptor) rather than degraded to EventSource counters.
//
// §1.5 全限定：references to sibling namespaces whose class name equals the namespace
// tail (Ts.Scanner.Scanner, Ts.TsPath.TsPath) use `global::…` — see plan/05 §1.5.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Ts.Ast;

namespace Ts.Tracing;

// TypeDescriptor represents a type in the output JSON (tracing.go:533-566).
// PORT-NOTE: pointer fields are C# nullable value types; a set value (even 0) is
// emitted, a null is omitted — matching Go's `omitzero` on a pointer (nil ⇒ omit).
public sealed class TypeDescriptor
{
    public uint ID;
    public string IntrinsicName = "";
    public string SymbolName = "";
    public int? RecursionID;
    public bool IsTuple;
    public uint[]? UnionTypes;
    public uint[]? IntersectionTypes;
    public uint[]? AliasTypeArguments;
    public uint? KeyofType;
    public uint? IndexedAccessObjectType;
    public uint? IndexedAccessIndexType;
    public uint? ConditionalCheckType;
    public uint? ConditionalExtendsType;
    // ConditionalTrueType / ConditionalFalseType are int (not uint) because unresolved
    // branches serialize as -1 (tracing.go:548-551).
    public int? ConditionalTrueType;
    public int? ConditionalFalseType;
    public uint? SubstitutionBaseType;
    public uint? ConstraintType;
    public uint? InstantiatedType;
    public uint[]? TypeArguments;
    public Location? ReferenceLocation;
    public uint? ReverseMappedSourceType;
    public uint? ReverseMappedMappedType;
    public uint? ReverseMappedConstraintType;
    public uint? EvolvingArrayElementType;
    public uint? EvolvingArrayFinalType;
    public Location? DestructuringPattern;
    public Location? FirstDeclaration;
    public string[]? Flags;
    public string Display = "";
}

// Location represents a source code location (tracing.go:569-573).
public sealed class Location
{
    public string Path = "";
    public LineAndChar? Start;
    public LineAndChar? End;
}

// LineAndChar represents a line and character position (1-indexed) (tracing.go:576-579).
public sealed class LineAndChar
{
    public int Line;
    public int Character;
}

// typeTracer is the per-checker tracer implementation (tracing.go:484-531).
internal sealed class TypeTracer : ITracer
{
    private readonly IFileSystemShim fs;
    private readonly int checkerIndex;
    private readonly string typesPath;
    private readonly List<ITracedType> types = [];
    private readonly object mu = new();

    internal TypeTracer(Ts.Vfs.IFileSystem fs, int checkerIndex, string typesPath)
    {
        this.fs = new IFileSystemShim(fs);
        this.checkerIndex = checkerIndex;
        this.typesPath = typesPath;
    }

    internal int CheckerIndex => checkerIndex;

    public void RecordType(ITracedType type)
    {
        lock (mu)
        {
            types.Add(type);
        }
    }

    public void DumpTypes()
    {
        // Copy the types list under lock, then release so Display() calls during
        // buildTypeDescriptor don't deadlock when they create new types.
        List<ITracedType> snapshot;
        lock (mu)
        {
            snapshot = new List<ITracedType>(types);
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        // Write opening bracket (no newline so type ID matches line number).
        sb.Append('[');

        // map-iter: 序无关 —— identity → token, order fixed by first-seen assignment.
        var recursionIdentityMap = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < snapshot.Count; i++)
        {
            ITracedType typ = snapshot[i];
            TypeDescriptor descriptor = BuildTypeDescriptor(typ, recursionIdentityMap);
            sb.Append(TraceJson.SerializeTypeDescriptor(descriptor));
            if (i < snapshot.Count - 1)
            {
                sb.Append(",\n");
            }
        }

        sb.Append("]\n");

        fs.WriteFile(typesPath, sb.ToString());
    }

    private static TypeDescriptor BuildTypeDescriptor(ITracedType typ, Dictionary<object, int> recursionIdentityMap)
    {
        Symbol? symbol = typ.Symbol;
        Symbol? aliasSymbol = typ.AliasSymbol;

        var desc = new TypeDescriptor
        {
            ID = typ.Id,
            Flags = typ.FormatFlags(),
        };

        // Assign a unique integer token per recursion identity (tracing.go:590-599).
        object? identity = typ.RecursionIdentity();
        if (identity != null)
        {
            if (!recursionIdentityMap.TryGetValue(identity, out int token))
            {
                token = recursionIdentityMap.Count;
                recursionIdentityMap[identity] = token;
            }
            desc.RecursionID = token;
        }

        // Intrinsic name.
        string intrinsicName = typ.IntrinsicName;
        if (intrinsicName.Length != 0)
        {
            desc.IntrinsicName = intrinsicName;
        }

        // Symbol name — escape the internal symbol name prefix for valid JSON.
        if (aliasSymbol != null)
        {
            desc.SymbolName = AstUtilities.EscapeAllInternalSymbolNames(aliasSymbol.Name);
        }
        else if (symbol != null)
        {
            desc.SymbolName = AstUtilities.EscapeAllInternalSymbolNames(symbol.Name);
        }

        // Tuple flag.
        if (typ.IsTuple)
        {
            desc.IsTuple = true;
        }

        // Union types.
        ITracedType[]? unionTypes = typ.UnionTypes();
        if (unionTypes is { Length: > 0 })
        {
            desc.UnionTypes = MapTypeIds(unionTypes);
        }

        // Intersection types.
        ITracedType[]? intersectionTypes = typ.IntersectionTypes();
        if (intersectionTypes is { Length: > 0 })
        {
            desc.IntersectionTypes = MapTypeIds(intersectionTypes);
        }

        // Alias type arguments.
        ITracedType[]? aliasArgs = typ.AliasTypeArguments();
        if (aliasArgs is { Length: > 0 })
        {
            desc.AliasTypeArguments = MapTypeIds(aliasArgs);
        }

        // Index type (keyof).
        ITracedType? indexType = typ.IndexType();
        if (indexType != null)
        {
            desc.KeyofType = indexType.Id;
        }

        // Indexed access type.
        ITracedType? objType = typ.IndexedAccessObjectType();
        if (objType != null)
        {
            desc.IndexedAccessObjectType = objType.Id;
        }
        ITracedType? idxType = typ.IndexedAccessIndexType();
        if (idxType != null)
        {
            desc.IndexedAccessIndexType = idxType.Id;
        }

        // Conditional type.
        if (typ.IsConditional)
        {
            ITracedType? checkType = typ.ConditionalCheckType();
            if (checkType != null)
            {
                desc.ConditionalCheckType = checkType.Id;
            }
            ITracedType? extendsType = typ.ConditionalExtendsType();
            if (extendsType != null)
            {
                desc.ConditionalExtendsType = extendsType.Id;
            }
            ITracedType? trueType = typ.ConditionalTrueType();
            desc.ConditionalTrueType = trueType != null ? (int)trueType.Id : -1;
            ITracedType? falseType = typ.ConditionalFalseType();
            desc.ConditionalFalseType = falseType != null ? (int)falseType.Id : -1;
        }

        // Substitution type.
        ITracedType? baseType = typ.SubstitutionBaseType();
        if (baseType != null)
        {
            desc.SubstitutionBaseType = baseType.Id;
        }
        ITracedType? constraint = typ.SubstitutionConstraintType();
        if (constraint != null)
        {
            desc.ConstraintType = constraint.Id;
        }

        // Reference type.
        ITracedType? target = typ.ReferenceTarget();
        if (target != null)
        {
            desc.InstantiatedType = target.Id;
        }
        ITracedType[]? refArgs = typ.ReferenceTypeArguments();
        if (refArgs is { Length: > 0 })
        {
            desc.TypeArguments = MapTypeIds(refArgs);
        }
        Node? refNode = typ.ReferenceNode;
        if (refNode != null)
        {
            desc.ReferenceLocation = GetLocation(refNode);
        }

        // Reverse mapped type.
        ITracedType? sourceType = typ.ReverseMappedSourceType();
        if (sourceType != null)
        {
            desc.ReverseMappedSourceType = sourceType.Id;
        }
        ITracedType? mappedType = typ.ReverseMappedMappedType();
        if (mappedType != null)
        {
            desc.ReverseMappedMappedType = mappedType.Id;
        }
        ITracedType? constraintType = typ.ReverseMappedConstraintType();
        if (constraintType != null)
        {
            desc.ReverseMappedConstraintType = constraintType.Id;
        }

        // Evolving array type.
        ITracedType? elemType = typ.EvolvingArrayElementType();
        if (elemType != null)
        {
            desc.EvolvingArrayElementType = elemType.Id;
        }
        ITracedType? finalType = typ.EvolvingArrayFinalType();
        if (finalType != null)
        {
            desc.EvolvingArrayFinalType = finalType.Id;
        }

        // Pattern (destructuring).
        Node? pattern = typ.Pattern;
        if (pattern != null)
        {
            desc.DestructuringPattern = GetLocation(pattern);
        }

        // First declaration — prefer aliasSymbol, matching TypeScript's `aliasSymbol ?? symbol`.
        Symbol? firstDeclSymbol = aliasSymbol ?? symbol;
        if (firstDeclSymbol?.Declarations is { Length: > 0 } declarations)
        {
            desc.FirstDeclaration = GetLocation(declarations[0]);
        }

        // Display text.
        string display = typ.Display();
        if (display.Length != 0)
        {
            desc.Display = display;
        }

        return desc;
    }

    private static uint[]? MapTypeIds(ITracedType[]? types)
    {
        if (types == null || types.Length == 0)
        {
            return null;
        }
        var ids = new uint[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] != null)
            {
                ids[i] = types[i].Id;
            }
        }
        return ids;
    }

    // getLocation (tracing.go:739-763).
    private static Location? GetLocation(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        SourceFile? file = AstUtilities.GetSourceFileOfNode(node);
        if (file == null)
        {
            return null;
        }

        int startPos = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, file, false);
        (int startLine, int startChar) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(file, startPos);
        (int endLine, int endChar) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(file, node.End);

        return new Location
        {
            Path = global::Ts.TsPath.TsPath.ToPath(file.FileName(), "", false).Value,
            Start = new LineAndChar { Line = startLine + 1, Character = startChar + 1 },
            End = new LineAndChar { Line = endLine + 1, Character = endChar + 1 },
        };
    }

    // Small shim so TypeTracer's stored FS field is not shadowed by the Ts.Vfs namespace
    // segment (§1.5 avoidance): we keep a private wrapper with the two methods we need.
    private readonly struct IFileSystemShim
    {
        private readonly Ts.Vfs.IFileSystem fs;

        internal IFileSystemShim(Ts.Vfs.IFileSystem fs)
        {
            this.fs = fs;
        }

        internal void WriteFile(string path, string data) => fs.WriteFile(path, data);
    }
}
