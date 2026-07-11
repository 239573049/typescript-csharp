// Local AST helpers + free-function generics for the Instantiation cluster (plan/03 §4.2 #14).
// These correspond to Go package-level `func`s in the same line range (isThisless family,
// isConflictingPrivateProperty) and the generic `instantiateList[T]` (checker.go:22689-22703),
// plus small array helpers (Go `len(nil)==0` / `append` semantics, plan/05 §6).

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:22689-22703 — generic list instantiation preserving identity (Go `[T comparable]`,
    // element compare is pointer identity → ReferenceEquals; unchanged input returned by reference).
    internal static T[]? InstantiateList<T>(Checker c, T[]? values, TypeMapper? m, Func<Checker, T, TypeMapper?, T> instantiator) where T : class
    {
        if (values is null)
        {
            return null;
        }
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var mapped = instantiator(c, value, m);
            if (!ReferenceEquals(mapped, value))
            {
                var result = new T[values.Length];
                Array.Copy(values, result, i);
                result[i] = mapped;
                for (int j = i + 1; j < values.Length; j++)
                {
                    result[j] = instantiator(c, values[j], m);
                }
                return result;
            }
        }
        return values;
    }

    // Go `len(nil)==0` for slices (plan/05 §6.1).
    private static int LenOrZero<T>(T[]? a) => a?.Length ?? 0;

    // Go `append(a, b...)` used read-only in getTypeArguments — always a fresh array (no aliasing).
    private static Type[] ConcatTypes(Type[]? a, Type[]? b)
    {
        var la = LenOrZero(a);
        var lb = LenOrZero(b);
        if (la == 0 && lb == 0)
        {
            return [];
        }
        if (la == 0)
        {
            return b!;
        }
        if (lb == 0)
        {
            return a!;
        }
        var r = new Type[la + lb];
        Array.Copy(a!, 0, r, 0, la);
        Array.Copy(b!, 0, r, la, lb);
        return r;
    }

    // checker.go:20703-20718 — true if the parameter or class/interface member is free of "this" refs.
    internal static bool IsThisless(Symbol symbol)
    {
        if ((symbol.Declarations?.Length ?? 0) == 1)
        {
            var declaration = symbol.Declarations![0];
            if (declaration is not null)
            {
                switch (declaration.Kind)
                {
                    case SyntaxKind.Parameter:
                        return IsThislessVariableLikeDeclaration(declaration);
                    case SyntaxKind.PropertyDeclaration:
                    case SyntaxKind.PropertySignature:
                        return IsThislessVariableLikeDeclaration(declaration);
                    case SyntaxKind.MethodDeclaration:
                    case SyntaxKind.MethodSignature:
                    case SyntaxKind.Constructor:
                    case SyntaxKind.GetAccessor:
                    case SyntaxKind.SetAccessor:
                        return IsThislessFunctionLikeDeclaration(declaration);
                }
            }
        }
        return false;
    }

    // checker.go:20722-20728 — free of this refs if it has a thisless type annotation, or no
    // annotation and no initializer (thus of type any).
    internal static bool IsThislessVariableLikeDeclaration(Node node)
    {
        var typeNode = node.Type();
        if (typeNode is not null)
        {
            return IsThislessType(typeNode);
        }
        return node.Initializer() is null;
    }

    // checker.go:20733-20744
    internal static bool IsThislessType(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.LiteralType:
                return true;
            case SyntaxKind.ArrayType:
                return IsThislessType(node.AsArrayTypeNode().ElementType!);
            case SyntaxKind.TypeReference:
                return CoreUtil.Every(node.TypeArguments(), IsThislessType);
        }
        return false;
    }

    // checker.go:20749-20754
    internal static bool IsThislessFunctionLikeDeclaration(Node node)
    {
        var returnType = node.Type();
        return (NodeTests.IsConstructorDeclaration(node) || returnType is not null && IsThislessType(returnType)) &&
            CoreUtil.Every(node.Parameters(), IsThislessVariableLikeDeclaration) &&
            CoreUtil.Every(node.TypeParameters(), IsThislessTypeParameter);
    }

    // checker.go:20757-20760
    internal static bool IsThislessTypeParameter(Node node)
    {
        var constraint = node.AsTypeParameterDeclaration().Constraint;
        return constraint is null || IsThislessType(constraint);
    }

    // checker.go:21793-21796 — synthetic property with multiple declarations, ≥1 private.
    internal static bool IsConflictingPrivateProperty(Symbol prop) =>
        prop.ValueDeclaration is null && (prop.CheckFlags & CheckFlags.ContainsPrivate) != 0;
}
