// Ported from tsgo internal/checker/relater.go — supporting types and small free functions for the
// relation (assignable/subtype/strictSubtype/comparable/identity) engine (plan/03 §4.2, §7).
//
// This file carries: the two relater enums (SignatureCheckMode/MinArgumentCountFlags), the ErrorReporter
// delegate, the error-chain types (ErrorChain/ErrorState), the discriminator abstraction
// (IDiscriminator/TypeDiscriminator), the DiagnosticAndArguments/ErrorOutputContainer structs, and a
// handful of package-level free functions ported as static Checker members. The Relater engine class is
// in Checker.Relater.Engine.cs; the (c *Checker) relation methods are in Checker.Relater.cs.
//
// §1.5: cross-package classes are shadowed by sibling namespaces, so diagnostics use the `Diag` alias.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;
using Message = global::Ts.Diagnostics.Message;

namespace Ts.Checker;

// relater.go:18-28
[System.Flags]
public enum SignatureCheckMode : uint
{
    None = 0,
    BivariantCallback = 1 << 0,
    StrictCallback = 1 << 1,
    IgnoreReturnTypes = 1 << 2,
    StrictArity = 1 << 3,
    StrictTopSignature = 1 << 4,
    Callback = BivariantCallback | StrictCallback,
}

// relater.go:30-36
[System.Flags]
public enum MinArgumentCountFlags : uint
{
    None = 0,
    StrongArityForUntypedJS = 1 << 0,
    VoidIsNonOptional = 1 << 1,
}

// relater.go:88 — func(message *diagnostics.Message, args ...any)
public delegate void ErrorReporter(Message message, params object?[] args);

// relater.go:78-81
internal sealed class DiagnosticAndArguments
{
    internal Message? message;
    internal object?[] arguments = [];
}

// relater.go:83-86
internal sealed class ErrorOutputContainer
{
    internal List<Diagnostic> errors = [];
    internal bool skipLogging;
}

// relater.go:2541-2545 — singly-linked error chain built during a failed relation comparison.
internal sealed class ErrorChain
{
    internal ErrorChain? next;
    internal Message message = null!;
    internal object?[] args = [];
}

// relater.go:2536-2539 — errorState snapshot. relatedInfo is modeled as a length into the Relater's
// growing List<Diagnostic>; restore truncates back to that length (Go restores the old slice header).
internal readonly record struct ErrorState(ErrorChain? ErrorChain, int RelatedInfoLength);

// relater.go:1206-1210 — Discriminator interface.
internal interface IDiscriminator
{
    int Len(); // Number of discriminant properties
    string Name(int index); // Property name of index-th discriminator
    bool Matches(int index, Type t); // True if index-th discriminator matches the given type
}

// relater.go:1037-1059
internal sealed class TypeDiscriminator : IDiscriminator
{
    private readonly Checker c;
    private readonly Symbol[] props;
    private readonly Func<Type, Type, Ternary> isRelatedTo;

    internal TypeDiscriminator(Checker c, Symbol[] props, Func<Type, Type, Ternary> isRelatedTo)
    {
        this.c = c;
        this.props = props;
        this.isRelatedTo = isRelatedTo;
    }

    public int Len() => props.Length;

    public string Name(int index) => props[index].Name;

    public bool Matches(int index, Type t)
    {
        var propType = c.GetTypeOfSymbol(props[index]);
        foreach (var s in propType.Distributed())
        {
            if (isRelatedTo(s, t) != Ternary.False)
            {
                return true;
            }
        }
        return false;
    }
}

public partial class Checker
{
    // relater.go:2526-2534
    internal static string VisibilityToString(ModifierFlags flags)
    {
        if (flags == ModifierFlags.Private)
        {
            return "private";
        }
        if (flags == ModifierFlags.Protected)
        {
            return "protected";
        }
        return "public";
    }

    // relater.go:4872-4892
    internal static string AddToDottedName(string head, string tail)
    {
        if (head.StartsWith("new ", StringComparison.Ordinal))
        {
            head = "(" + head + ")";
        }
        int pos = 0;
        while (true)
        {
            if (tail.AsSpan(pos).StartsWith("("))
            {
                pos++;
            }
            else if (tail.AsSpan(pos).StartsWith("new "))
            {
                pos += 4;
            }
            else
            {
                break;
            }
        }
        string prefix = tail[..pos];
        string suffix = tail[pos..];
        if (suffix.StartsWith("[", StringComparison.Ordinal))
        {
            return prefix + head + suffix;
        }
        return prefix + head + "." + suffix;
    }

    // relater.go:4919-4925
    internal static string GetPropertyNameArg(object? arg)
    {
        string s = (string)arg!;
        if (s.Length != 0 && (s[0] == '"' || s[0] == '\'' || s[0] == '`'))
        {
            return "[" + s + "]";
        }
        return s;
    }

    // relater.go:4927-4934
    internal static bool IsConversionOrInterfaceImplementationMessage(Message message) =>
        message == Diag.Class_0_incorrectly_implements_interface_1 ||
        message == Diag.Class_0_incorrectly_implements_class_1_Did_you_mean_to_extend_1_and_inherit_its_members_as_a_subclass ||
        message == Diag.Conversion_of_type_0_to_type_1_may_be_a_mistake_because_neither_type_sufficiently_overlaps_with_the_other_If_this_was_intentional_convert_the_expression_to_unknown_first ||
        message == Diag.Its_instance_type_0_is_not_a_valid_JSX_element ||
        message == Diag.Its_return_type_0_is_not_a_valid_JSX_element ||
        message == Diag.Its_element_type_0_is_not_a_valid_JSX_element;

    // relater.go:4936-4943
    internal static int ChainDepth(ErrorChain? chain)
    {
        int depth = 0;
        while (chain != null)
        {
            depth++;
            chain = chain.next;
        }
        return depth;
    }

    // ===== Cross-cluster helpers with a temporary home here (owning wave will relocate) =====

    // checker.go:25301-25312 (owner: #16 TypeCreation / union reduction). Tiny and reached from
    // the relation engine; implemented faithfully here rather than as a throwing stub.
    internal static bool IsLiteralType(Type t)
    {
        if ((t.flags & TypeFlags.Boolean) != 0)
        {
            return true;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            if ((t.flags & TypeFlags.EnumLiteral) != 0)
            {
                return true;
            }
            return CoreUtil.Every(t.Types(), IsUnitType);
        }
        return IsUnitType(t);
    }

    // checker.go:28817-28819 (owner: #20 AliasReferences).
    internal bool IsPartialMappedType(Type t) =>
        (t.objectFlags & ObjectFlags.Mapped) != 0 && (GetMappedTypeModifiers(t) & MappedTypeModifiers.IncludeOptional) != 0;

    // utilities.go:1241-1243 (owner: utilities wave).
    internal static bool HasType(Node node) => node.Type() is not null;
}
