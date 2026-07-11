// Support helpers for the Members + Signatures cluster (plan/03 §4.2 #12/#13).
//
// These are small checker free functions (utilities.go / checker.go, OUTSIDE the #12/#13 line range)
// that #12/#13 depend on and that had no C# home yet. Ported faithfully here so the cluster is
// self-contained. If a sibling cluster later lands a canonical copy, delete the duplicate here.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:25318
    internal static bool IsUnitType(Type t) => (t.flags & TypeFlags.Unit) != 0;

    // utilities.go:801
    internal static bool IsObjectLiteralType(Type t) => (t.objectFlags & ObjectFlags.ObjectLiteral) != 0;

    // checker.go:26416
    internal static void ForEachType(Type t, Action<Type> f)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            foreach (var u in t.Types())
            {
                f(u);
            }
        }
        else
        {
            f(t);
        }
    }

    // checker.go:26426
    internal static bool SomeType(Type t, Func<Type, bool> f) =>
        (t.flags & TypeFlags.Union) != 0 ? CoreUtil.Some(t.Types(), f) : f(t);

    // checker.go:26433
    internal static bool EveryType(Type t, Func<Type, bool> f) =>
        (t.flags & TypeFlags.Union) != 0 ? CoreUtil.Every(t.Types(), f) : f(t);

    // utilities.go:242
    internal static bool HasOnlyExpressionInitializer(Node node) => node.Kind switch
    {
        SyntaxKind.VariableDeclaration or SyntaxKind.Parameter or SyntaxKind.BindingElement or
            SyntaxKind.PropertyDeclaration or SyntaxKind.PropertyAssignment or SyntaxKind.EnumMember => true,
        _ => false,
    };

    // utilities.go:250
    internal static bool HasDotDotDotToken(Node node) => node.Kind switch
    {
        SyntaxKind.Parameter => node.AsParameterDeclaration().DotDotDotToken is not null,
        SyntaxKind.BindingElement => node.AsBindingElement().DotDotDotToken is not null,
        SyntaxKind.NamedTupleMember => node.AsNamedTupleMember().DotDotDotToken is not null,
        SyntaxKind.JsxExpression => node.AsJsxExpression().DotDotDotToken is not null,
        _ => false,
    };

    // utilities.go:1058-1060
    internal static Node? GetBindingElementPropertyName(Node node) => node.PropertyNameOrName();

    // utilities.go:1062-1064
    internal static bool IsCallChain(Node node) => NodeTests.IsCallExpression(node) && (node.Flags & NodeFlags.OptionalChain) != 0;

    // checker.go:27658
    internal static bool IsRestParameter(Node param) => param.AsParameterDeclaration().DotDotDotToken is not null;

    // checker.go:27653
    internal static bool HasRestParameterFree(Node signature)
    {
        var last = CoreUtil.LastOrNil(signature.Parameters());
        return last is not null && IsRestParameter(last);
    }

    // utilities.go:320 — PORT-NOTE: ast.HasModifier → AstUtilities.HasSyntacticModifier.
    internal static bool IsPrivateWithinAmbient(Node node) =>
        (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Private) || AstUtilities.IsPrivateIdentifierClassElementDeclaration(node)) && (node.Flags & NodeFlags.Ambient) != 0;

    // utilities.go:68 — PORT-NOTE: ast.HasModifier → AstUtilities.HasSyntacticModifier.
    internal static bool HasReadonlyModifier(Node node) => AstUtilities.HasSyntacticModifier(node, ModifierFlags.Readonly);

    // utilities.go:72
    internal static bool IsStaticPrivateIdentifierProperty(Symbol s) =>
        s.ValueDeclaration is not null && AstUtilities.IsPrivateIdentifierClassElementDeclaration(s.ValueDeclaration) && AstUtilities.IsStatic(s.ValueDeclaration);

    // Literal value readers (types.go accessors on LiteralType.value).
    internal static string GetStringLiteralValue(Type t) => (string)t.AsLiteralType().value!;

    internal static global::Ts.JsNum.Number GetNumberLiteralValue(Type t) => (global::Ts.JsNum.Number)t.AsLiteralType().value!;

    // slices.Equal specialised for reference-element type slices (Go compares *Type by pointer).
    internal static bool SlicesEqual(Type[]? a, Type[]? b)
    {
        var la = a?.Length ?? 0;
        var lb = b?.Length ?? 0;
        if (la != lb)
        {
            return false;
        }
        for (int i = 0; i < la; i++)
        {
            if (!ReferenceEquals(a![i], b![i]))
            {
                return false;
            }
        }
        return true;
    }
}
