// Support helpers for the type-operations wave (plan/03 §4.2 #17/#18/#19: UnionIntersection /
// IndexedAccess / Constraints). These are small checker utilities that live OUTSIDE the three
// owned line ranges (mostly internal/checker/utilities.go and relater.go) but that #17/#18/#19
// depend on, and that had no C# home yet. Ported faithfully here so the cluster is self-contained;
// if a sibling cluster later lands a canonical copy, delete the duplicate here.
//
// §1.5 note: cross-package package-level helpers whose class name collides with a sibling namespace
// (Core) are called fully-qualified via `global::Ts.Core.Core.*`.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ===== CompareTypes and its helpers (internal/checker/utilities.go) =====

    // utilities.go:343-368 (was a stub in Checker.Stubs.cs; needed by CompareTypes)
    internal int CompareSymbolsWorker(Symbol? s1, Symbol? s2)
    {
        if (s1 == s2)
        {
            return 0;
        }
        if (s1 is null)
        {
            return 1;
        }
        if (s2 is null)
        {
            return -1;
        }
        if (s1.Declarations.Len() != 0 && s2.Declarations.Len() != 0)
        {
            var r = CompareNodes(s1.Declarations![0], s2.Declarations![0]);
            if (r != 0)
            {
                return r;
            }
        }
        else if (s1.Declarations.Len() != 0)
        {
            return -1;
        }
        else if (s2.Declarations.Len() != 0)
        {
            return 1;
        }
        var rn = string.CompareOrdinal(s1.Name, s2.Name);
        if (rn != 0)
        {
            return rn;
        }
        // Fall back to symbol IDs. This is a last resort that should happen only when symbols have
        // no declaration and duplicate names.
        return (int)AstUtilities.GetSymbolId(s1).Value - (int)AstUtilities.GetSymbolId(s2).Value;
    }

    // utilities.go:370-390
    internal int CompareNodes(Node? n1, Node? n2)
    {
        if (n1 == n2)
        {
            return 0;
        }
        if (n1 is null)
        {
            return 1;
        }
        if (n2 is null)
        {
            return -1;
        }
        var s1 = AstUtilities.GetSourceFileOfNode(n1);
        var s2 = AstUtilities.GetSourceFileOfNode(n2);
        if (s1 != s2)
        {
            var f1 = fileIndexMap.GetValueOrDefault(s1!);
            var f2 = fileIndexMap.GetValueOrDefault(s2!);
            // Order by index of file in the containing program
            return f1 - f2;
        }
        // In the same file, order by source position
        return n1.Pos - n2.Pos;
    }

    // utilities.go:392-556
    internal static int CompareTypes(Type? t1, Type? t2)
    {
        if (t1 == t2)
        {
            return 0;
        }
        if (t1 is null)
        {
            return -1;
        }
        if (t2 is null)
        {
            return 1;
        }
        if (t1.checker != t2.checker)
        {
            throw new InvalidOperationException("Cannot compare types from different checkers");
        }
        // First sort in order of increasing type flags values.
        {
            var c = GetSortOrderFlags(t1) - GetSortOrderFlags(t2);
            if (c != 0)
            {
                return c;
            }
        }
        // Order named types by name and, in the case of aliased types, by alias type arguments.
        {
            var c = CompareTypeNames(t1, t2);
            if (c != 0)
            {
                return c;
            }
        }
        // We have unnamed types or types with identical names. Now sort by data specific to the type.
        if ((t1.flags & (TypeFlags.Any | TypeFlags.Unknown | TypeFlags.String | TypeFlags.Number | TypeFlags.Boolean | TypeFlags.BigInt | TypeFlags.ESSymbol | TypeFlags.Void | TypeFlags.Undefined | TypeFlags.Null | TypeFlags.Never | TypeFlags.NonPrimitive)) != 0)
        {
            // Only distinguished by type IDs, handled below.
        }
        else if ((t1.flags & TypeFlags.Object) != 0)
        {
            // Order unnamed or identically named object types by symbol.
            {
                var c = t1.checker.compareSymbols(t1.symbol, t2.symbol);
                if (c != 0)
                {
                    return c;
                }
            }
            // When object types have the same or no symbol, order by kind. We order type references before other kinds.
            if ((t1.objectFlags & ObjectFlags.Reference) != 0 && (t2.objectFlags & ObjectFlags.Reference) != 0)
            {
                var r1 = t1.AsTypeReference()!;
                var r2 = t2.AsTypeReference()!;
                if ((r1.target!.objectFlags & ObjectFlags.Tuple) != 0 && (r2.target!.objectFlags & ObjectFlags.Tuple) != 0)
                {
                    // Tuple types have no associated symbol, instead we order by tuple element information.
                    var c = CompareTupleTypes(r1.target.AsTupleType(), r2.target!.AsTupleType());
                    if (c != 0)
                    {
                        return c;
                    }
                }
                // Here we know we have references to instantiations of the same type because we have matching targets.
                if (r1.node is null && r2.node is null)
                {
                    // Non-deferred type references with the same target are sorted by their type argument lists.
                    var c = CompareTypeLists(t1.AsTypeReference()!.resolvedTypeArguments, t2.AsTypeReference()!.resolvedTypeArguments);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                else
                {
                    // Deferred type references with the same target are ordered by the source location of the reference.
                    var c = t1.checker.CompareNodes(r1.node, r2.node);
                    if (c != 0)
                    {
                        return c;
                    }
                    // Instantiations of the same deferred type reference are ordered by their associated type mappers.
                    var cm = CompareTypeMappers(t1.AsObjectType()!.mapper, t2.AsObjectType()!.mapper);
                    if (cm != 0)
                    {
                        return cm;
                    }
                }
            }
            else if ((t1.objectFlags & ObjectFlags.Reference) != 0)
            {
                return -1;
            }
            else if ((t2.objectFlags & ObjectFlags.Reference) != 0)
            {
                return 1;
            }
            else
            {
                // Order unnamed non-reference object types by kind and associated type mappers.
                {
                    var c = (int)(t1.objectFlags & ObjectFlags.ObjectTypeKindMask) - (int)(t2.objectFlags & ObjectFlags.ObjectTypeKindMask);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                var cm = CompareTypeMappers(t1.AsObjectType()!.mapper, t2.AsObjectType()!.mapper);
                if (cm != 0)
                {
                    return cm;
                }
            }
        }
        else if ((t1.flags & TypeFlags.Union) != 0)
        {
            // Unions are ordered by origin and then constituent type lists.
            var o1 = t1.AsUnionType().origin;
            var o2 = t2.AsUnionType().origin;
            if (o1 is null && o2 is null)
            {
                var c = CompareTypeLists(t1.Types(), t2.Types());
                if (c != 0)
                {
                    return c;
                }
            }
            else if (o1 is null)
            {
                return 1;
            }
            else if (o2 is null)
            {
                return -1;
            }
            else
            {
                var c = CompareTypes(o1, o2);
                if (c != 0)
                {
                    return c;
                }
            }
        }
        else if ((t1.flags & TypeFlags.Intersection) != 0)
        {
            // Intersections are ordered by their constituent type lists.
            var c = CompareTypeLists(t1.Types(), t2.Types());
            if (c != 0)
            {
                return c;
            }
        }
        else if ((t1.flags & (TypeFlags.Enum | TypeFlags.EnumLiteral | TypeFlags.UniqueESSymbol)) != 0)
        {
            // Enum members are ordered by their symbol (and thus their declaration order).
            var c = t1.checker.compareSymbols(t1.symbol, t2.symbol);
            if (c != 0)
            {
                return c;
            }
        }
        else if ((t1.flags & TypeFlags.StringLiteral) != 0)
        {
            // String literal types are ordered by their values.
            var c = string.CompareOrdinal((string)t1.AsLiteralType().value!, (string)t2.AsLiteralType().value!);
            if (c != 0)
            {
                return c;
            }
        }
        else if ((t1.flags & TypeFlags.NumberLiteral) != 0)
        {
            // Numeric literal types are ordered by their values.
            var c = ((Ts.JsNum.Number)t1.AsLiteralType().value!).Value.CompareTo(((Ts.JsNum.Number)t2.AsLiteralType().value!).Value);
            if (c != 0)
            {
                return c;
            }
        }
        else if ((t1.flags & TypeFlags.BooleanLiteral) != 0)
        {
            var b1 = (bool)t1.AsLiteralType().value!;
            var b2 = (bool)t2.AsLiteralType().value!;
            if (b1 != b2)
            {
                return b1 ? 1 : -1;
            }
        }
        else if ((t1.flags & TypeFlags.TypeParameter) != 0)
        {
            var c = t1.checker.compareSymbols(t1.symbol, t2.symbol);
            if (c != 0)
            {
                return c;
            }
        }
        else if ((t1.flags & TypeFlags.Index) != 0)
        {
            {
                var c = CompareTypes(t1.AsIndexType().target, t2.AsIndexType().target);
                if (c != 0)
                {
                    return c;
                }
            }
            var cf = (int)t1.AsIndexType().indexFlags - (int)t2.AsIndexType().indexFlags;
            if (cf != 0)
            {
                return cf;
            }
        }
        else if ((t1.flags & TypeFlags.IndexedAccess) != 0)
        {
            {
                var c = CompareTypes(t1.AsIndexedAccessType().objectType, t2.AsIndexedAccessType().objectType);
                if (c != 0)
                {
                    return c;
                }
            }
            var ci = CompareTypes(t1.AsIndexedAccessType().indexType, t2.AsIndexedAccessType().indexType);
            if (ci != 0)
            {
                return ci;
            }
        }
        else if ((t1.flags & TypeFlags.Conditional) != 0)
        {
            {
                var c = t1.checker.CompareNodes(t1.AsConditionalType().root.node!.AsNode(), t2.AsConditionalType().root.node!.AsNode());
                if (c != 0)
                {
                    return c;
                }
            }
            var cm = CompareTypeMappers(t1.AsConditionalType().mapper, t2.AsConditionalType().mapper);
            if (cm != 0)
            {
                return cm;
            }
        }
        else if ((t1.flags & TypeFlags.Substitution) != 0)
        {
            {
                var c = CompareTypes(t1.AsSubstitutionType().baseType, t2.AsSubstitutionType().baseType);
                if (c != 0)
                {
                    return c;
                }
            }
            var cc = CompareTypes(t1.AsSubstitutionType().constraint, t2.AsSubstitutionType().constraint);
            if (cc != 0)
            {
                return cc;
            }
        }
        else if ((t1.flags & TypeFlags.TemplateLiteral) != 0)
        {
            {
                var c = CompareStringLists(t1.AsTemplateLiteralType().texts, t2.AsTemplateLiteralType().texts);
                if (c != 0)
                {
                    return c;
                }
            }
            var ct = CompareTypeLists(t1.AsTemplateLiteralType().types, t2.AsTemplateLiteralType().types);
            if (ct != 0)
            {
                return ct;
            }
        }
        else if ((t1.flags & TypeFlags.StringMapping) != 0)
        {
            var c = CompareTypes(t1.AsStringMappingType().target, t2.AsStringMappingType().target);
            if (c != 0)
            {
                return c;
            }
        }
        // Fall back to type IDs. This results in type creation order for built-in types.
        return (int)t1.id.Value - (int)t2.id.Value;
    }

    // utilities.go:558-564
    internal static int GetSortOrderFlags(Type t)
    {
        // Return TypeFlagsEnum for all enum-like unit types (they'll be sorted by their symbols)
        if ((t.flags & (TypeFlags.EnumLiteral | TypeFlags.Enum)) != 0 && (t.flags & TypeFlags.Union) == 0)
        {
            return (int)TypeFlags.Enum;
        }
        return (int)t.flags;
    }

    // utilities.go:566-582
    internal static int CompareTypeNames(Type t1, Type t2)
    {
        var s1 = GetTypeNameSymbol(t1);
        var s2 = GetTypeNameSymbol(t2);
        if (s1 == s2)
        {
            if (t1.alias is not null)
            {
                return CompareTypeLists(t1.alias.typeArguments, t2.alias!.typeArguments);
            }
            return 0;
        }
        if (s1 is null)
        {
            return 1;
        }
        if (s2 is null)
        {
            return -1;
        }
        return string.CompareOrdinal(s1.Name, s2.Name);
    }

    // utilities.go:584-592
    internal static Symbol? GetTypeNameSymbol(Type t)
    {
        if (t.alias is not null)
        {
            return t.alias.symbol;
        }
        if ((t.flags & (TypeFlags.TypeParameter | TypeFlags.StringMapping)) != 0 || (t.objectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0)
        {
            return t.symbol;
        }
        return null;
    }

    // utilities.go:601-622
    internal static int CompareTupleTypes(TupleType t1, TupleType t2)
    {
        if (t1 == t2)
        {
            return 0;
        }
        if (t1.readonlyElements != t2.readonlyElements)
        {
            return t1.readonlyElements ? 1 : -1;
        }
        var e1 = t1.elementInfos ?? [];
        var e2 = t2.elementInfos ?? [];
        if (e1.Length != e2.Length)
        {
            return e1.Length - e2.Length;
        }
        for (int i = 0; i < e1.Length; i++)
        {
            var c = (int)e1[i].flags - (int)e2[i].flags;
            if (c != 0)
            {
                return c;
            }
        }
        for (int i = 0; i < e1.Length; i++)
        {
            var c = CompareElementLabels(e1[i].labeledDeclaration, e2[i].labeledDeclaration);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    // utilities.go:624-635
    internal static int CompareElementLabels(Node? n1, Node? n2)
    {
        if (n1 == n2)
        {
            return 0;
        }
        if (n1 is null)
        {
            return -1;
        }
        if (n2 is null)
        {
            return 1;
        }
        return string.CompareOrdinal(n1.Name()!.Text(), n2.Name()!.Text());
    }

    // utilities.go:637-647
    internal static int CompareTypeLists(Type[]? s1, Type[]? s2)
    {
        var a = s1 ?? [];
        var b = s2 ?? [];
        if (a.Length != b.Length)
        {
            return a.Length - b.Length;
        }
        for (int i = 0; i < a.Length; i++)
        {
            var c = CompareTypes(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    // slices.Compare over string slices (ordinal, lexicographic then by length).
    private static int CompareStringLists(string[]? s1, string[]? s2)
    {
        var a = s1 ?? [];
        var b = s2 ?? [];
        int n = a.Length < b.Length ? a.Length : b.Length;
        for (int i = 0; i < n; i++)
        {
            var c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
            {
                return c < 0 ? -1 : 1;
            }
        }
        return a.Length - b.Length;
    }

    // utilities.go:649-688
    internal static int CompareTypeMappers(TypeMapper? m1, TypeMapper? m2)
    {
        if (m1 == m2)
        {
            return 0;
        }
        if (m1 is null)
        {
            return 1;
        }
        if (m2 is null)
        {
            return -1;
        }
        var kind1 = m1.Kind();
        var kind2 = m2.Kind();
        if (kind1 != kind2)
        {
            return (int)kind1 - (int)kind2;
        }
        switch (kind1)
        {
            case TypeMapperKind.Simple:
                {
                    var a = (SimpleTypeMapper)m1;
                    var b = (SimpleTypeMapper)m2;
                    var c = CompareTypes(a.source, b.source);
                    if (c != 0)
                    {
                        return c;
                    }
                    return CompareTypes(a.target, b.target);
                }
            case TypeMapperKind.Array:
                {
                    var a = (ArrayTypeMapper)m1;
                    var b = (ArrayTypeMapper)m2;
                    var c = CompareTypeLists(a.sources, b.sources);
                    if (c != 0)
                    {
                        return c;
                    }
                    return CompareTypeLists(a.targets, b.targets);
                }
            case TypeMapperKind.Merged:
                {
                    var a = (MergedTypeMapper)m1;
                    var b = (MergedTypeMapper)m2;
                    var c = CompareTypeMappers(a.m1, b.m1);
                    if (c != 0)
                    {
                        return c;
                    }
                    return CompareTypeMappers(a.m2, b.m2);
                }
        }
        return 0;
    }

    // ===== RecursionId (relater.go:90-97, 839-877) — needed by getResolvedBaseConstraint =====

    // relater.go:90-97 — value is *ast.Node | *ast.Symbol | *Type; record-struct equality over
    // an object? field gives reference equality for these (they never override Equals) — matches
    // Go interface equality on pointers.
    internal readonly record struct RecursionId(object? Value);

    internal static RecursionId AsRecursionId(Node? value) => new(value);

    internal static RecursionId AsRecursionId(Symbol? value) => new(value);

    internal static RecursionId AsRecursionId(Type? value) => new(value);

    // relater.go:839-877
    internal static RecursionId GetRecursionIdentity(Type t)
    {
        // Object and array literals are known not to contain recursive references and don't need a recursion identity.
        if ((t.flags & TypeFlags.Object) != 0 && !IsObjectOrArrayLiteralType(t))
        {
            if ((t.objectFlags & ObjectFlags.Reference) != 0 && t.AsTypeReference()!.node is not null)
            {
                // Deferred type references are tracked through their associated AST node.
                return AsRecursionId(t.AsTypeReference()!.node);
            }
            if (t.symbol is not null && !((t.objectFlags & ObjectFlags.Anonymous) != 0 && (t.symbol.Flags & SymbolFlags.Class) != 0) && (t.objectFlags & ObjectFlags.FromTypeNode) == 0)
            {
                // We track object types that have a symbol by that symbol (representing the origin of the type).
                return AsRecursionId(t.symbol);
            }
            if (IsTupleType(t) && (t.objectFlags & ObjectFlags.FromTypeNode) == 0)
            {
                return AsRecursionId(t.Target());
            }
        }
        if ((t.flags & TypeFlags.TypeParameter) != 0 && t.symbol is not null)
        {
            // We use the symbol of the type parameter such that all "fresh" instantiations have the same recursion identity.
            return AsRecursionId(t.symbol);
        }
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            // Identity is the leftmost object type in a chain of indexed accesses.
            t = t.AsIndexedAccessType().objectType;
            while ((t.flags & TypeFlags.IndexedAccess) != 0)
            {
                t = t.AsIndexedAccessType().objectType;
            }
            return AsRecursionId(t);
        }
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            // The root object represents the origin of the conditional type
            return AsRecursionId(t.AsConditionalType().root.node!.AsNode());
        }
        return AsRecursionId(t);
    }

    // ===== Local orderedSet (utilities.go:809-840) — insertion-ordered dedup set =====

    // orderedSetMapThreshold: size at which the set materializes its dedup map.
    private const int OrderedSetMapThreshold = 16;

    // utilities.go:813-840. Reference-typed T (Go used *Type); dedup by reference identity to
    // match Go pointer-key map semantics.
    internal sealed class OrderedTypeSet
    {
        internal HashSet<Type>? valuesByKey;
        internal List<Type> values = [];

        public bool Contains(Type value)
        {
            if (valuesByKey is null)
            {
                foreach (var v in values)
                {
                    if (ReferenceEquals(v, value))
                    {
                        return true;
                    }
                }
                return false;
            }
            return valuesByKey.Contains(value);
        }

        public void Add(Type value)
        {
            values.Add(value);
            // Small sets are served by a linear scan; only materialize the map once large enough.
            if (valuesByKey is null)
            {
                if (values.Count <= OrderedSetMapThreshold)
                {
                    return;
                }
                valuesByKey = new HashSet<Type>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < values.Count - 1; i++)
                {
                    valuesByKey.Add(values[i]);
                }
            }
            valuesByKey.Add(value);
        }
    }

    // ===== slices.* helpers over Type[] (reference-element semantics) =====

    // slices.Delete(s, i, j) — Go mutates+returns; callers reassign, so a fresh array is equivalent.
    internal static Type[] SlicesDelete(Type[] s, int i, int j)
    {
        if (i == j)
        {
            return s;
        }
        var result = new Type[s.Length - (j - i)];
        Array.Copy(s, 0, result, 0, i);
        Array.Copy(s, j, result, i, s.Length - j);
        return result;
    }

    // slices.Insert(s, i, v)
    internal static Type[] SlicesInsert(Type[]? s, int i, Type v)
    {
        if (s == null) return [v];
        var result = new Type[s.Length + 1];
        Array.Copy(s, 0, result, 0, i);
        result[i] = v;
        Array.Copy(s, i, result, i + 1, s.Length - i);
        return result;
    }

    // slices.Index by reference identity.
    internal static int SlicesIndex(Type[]? s, Type v)
    {
        if (s == null) return -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (ReferenceEquals(s[i], v))
            {
                return i;
            }
        }
        return -1;
    }

    // slices.Contains by reference identity.
    internal static bool SlicesContains(Type[]? s, Type v) => SlicesIndex(s, v) >= 0;

    // slices.IndexFunc
    internal static int SlicesIndexFunc(Type[]? s, Func<Type, bool> f)
    {
        if (s == null) return -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (f(s[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
