// Single dedup surface for cross-cluster methods that flow.go CALLS but does not own — they live in
// not-yet-landed waves (expressions/#7-#8, symbol resolution/#10-#11, contextual/#21, type creation,
// facts, evaluator utilities, grammar). Each throws NotImplementedException so flow's call sites
// compile; deep runtime paths run only once the owning wave lands its real implementation, at which
// point that wave DELETES the corresponding stub here. **Do NOT implement whole missing subsystems.**

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ---- expressions (#7/#8) ----
    // getTypeOfExpression / checkExpression / checkExpressionCached / checkNonNullExpression /
    // getResolvedSymbol / getResolvedSymbolOrNil / checkRightHandSideOfForOf / checkIteratedTypeOrElementType
    // are stubbed by other Members/Signatures/Stubs dedup surfaces (already present).
    // getContextFreeTypeOfExpression (checker.go:7519) — real body landed in Checker.Expressions.cs (#7 wave).
    internal Type GetContextFreeTypeOfExpression(Node node) => GetContextFreeTypeOfExpressionCore(node);
    // checkNonNullType 7386: real body landed in Checker.PropertyAccess.cs (non-null cluster).
    // checker.go:28821 — getOptionalExpressionType.
    internal Type GetOptionalExpressionType(Type exprType, Node expression)
    {
        if (AstUtilities.IsExpressionOfOptionalChainRoot(expression))
        {
            return GetNonNullableType(exprType);
        }
        if (AstUtilities.IsOptionalChain(expression))
        {
            return RemoveOptionalTypeMarker(exprType);
        }
        return exprType;
    }
    // checkSuperExpression (7831): real body landed in Checker.Expressions.cs (with getSuperContainer
    // + isInConstructorArgumentInitializer).
    // getResolvedSignature (8383): real body landed in Checker.CallResolution.cs (candidatesOutArray
    // modeled as List<Signature>? to match the Go *[]*Signature out-parameter).
    // utilities.go:90
    internal static AssignmentKind GetAssignmentTargetKind(Node node)
    {
        var target = AstUtilities.GetAssignmentTarget(node);
        if (target == null)
        {
            return AssignmentKind.None;
        }
        switch (target.Kind)
        {
            case SyntaxKind.BinaryExpression:
                var binaryOperator = target.AsBinaryExpression().OperatorToken!.Kind;
                if (binaryOperator == SyntaxKind.EqualsToken || NodeTests.IsLogicalOrCoalescingAssignmentOperator(binaryOperator))
                {
                    return AssignmentKind.Definite;
                }
                return AssignmentKind.Compound;
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
                return AssignmentKind.Compound;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                return AssignmentKind.Definite;
        }
        throw new InvalidOperationException("Unhandled case in getAssignmentTargetKind");
    }
    // utilities.go:118
    internal bool IsInCompoundLikeAssignment(Node node)
    {
        var target = AstUtilities.GetAssignmentTarget(node);
        return target != null && AstUtilities.IsAssignmentExpression(target, true /*excludeCompoundAssignment*/) && IsCompoundLikeAssignment(target);
    }
    // utilities.go:123
    private static bool IsCompoundLikeAssignment(Node assignment)
    {
        var right = AstUtilities.SkipParentheses(assignment.AsBinaryExpression().Right!);
        return right.Kind == SyntaxKind.BinaryExpression && NodeTests.IsShiftOperatorOrHigher(right.AsBinaryExpression().OperatorToken!.Kind);
    }

    // ---- symbol resolution / symbol predicates (#10/#11) ----
    // resolveSymbol / resolveEntityName / getExportSymbolOfValueSymbolIfExported / getSymbolOfDeclaration /
    // getResolvedSymbol / isConstantVariable / isReadonlySymbol already stubbed elsewhere.
    // utilities.go:1021 — Return true if symbol is a parameter, a catch clause variable, or a mutable local variable
    internal bool IsParameterOrMutableLocalVariable(Symbol symbol)
    {
        if (symbol.ValueDeclaration != null)
        {
            var declaration = AstUtilities.GetRootDeclaration(symbol.ValueDeclaration);
            return declaration != null && (NodeTests.IsParameterDeclaration(declaration) || NodeTests.IsVariableDeclaration(declaration) && (NodeTests.IsCatchClause(declaration.Parent!) || IsMutableLocalVariableDeclaration(declaration)));
        }
        return false;
    }
    // utilities.go:1030 — Return true if symbol is a non-exported and non-global `let` variable
    internal bool IsMutableLocalVariableDeclaration(Node declaration)
    {
        return (declaration.Parent!.Flags & NodeFlags.Let) != 0 && !((AstUtilities.GetCombinedModifierFlags(declaration) & ModifierFlags.Export) != 0 || declaration.Parent.Parent!.Kind == SyntaxKind.VariableStatement && AstUtilities.IsGlobalSourceFile(declaration.Parent.Parent.Parent!));
    }
    internal bool IsSomeSymbolAssigned(Node rootDeclaration) => throw new NotImplementedException("isSomeSymbolAssigned");
    internal Symbol? GetSymbolForPrivateIdentifierExpression(Node privId) => throw new NotImplementedException("getSymbolForPrivateIdentifierExpression");
    internal Symbol? GetGlobalRecordSymbol() => throw new NotImplementedException("getGlobalRecordSymbol");
    // checker.go:1076 — memoized resolver assigned in the constructor (Checker.Construction.cs).
    internal Symbol? GetGlobalESSymbolConstructorSymbolOrNil() => getGlobalESSymbolConstructorSymbolOrNil();

    // ---- type creation / operations / facts ----
    // checker.go:30973-30981
    internal Type ConvertAutoToAny(Type t)
    {
        if (t == autoType)
        {
            return anyType!;
        }
        if (t == autoArrayType)
        {
            return anyArrayType!;
        }
        return t;
    }
    // recombineUnknownType 30958: real body landed in Checker.TypeFacts.cs.
    // checker.go:25332-25340
    internal Type ExtractUnitType(Type t)
    {
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            var u = CoreUtil.Find(t.AsIntersectionType().types, IsUnitType);
            if (u is not null)
            {
                return u;
            }
        }
        return t;
    }
    // getFreshTypeOfLiteralType is real in Checker.TypeCreation.cs.
    // checker.go:31248
    internal Type GetNarrowableTypeForReference(Type t, Node reference, CheckMode checkMode)
    {
        if (IsNoInferType(t))
        {
            t = t.AsSubstitutionType()!.baseType;
        }
        // When the type of a reference is or contains an instantiable type with a union type constraint, and
        // when the reference is in a constraint position (where it is known we'll obtain the apparent type) or
        // has a contextual type containing no top-level instantiables (meaning constraints will determine
        // assignability), we substitute constraints for all instantiables in the type of the reference to give
        // control flow analysis an opportunity to narrow it further. For example, for a reference of a type
        // parameter type 'T extends string | undefined' with a contextual type 'string', we substitute
        // 'string | undefined' to give control flow analysis the opportunity to narrow to type 'string'.
        bool substituteConstraints = (checkMode & CheckMode.Inferential) == 0 && SomeType(t, IsGenericTypeWithUnionConstraint) && (IsConstraintPosition(t, reference) || HasContextualTypeWithNoGenericTypes(reference, checkMode));
        if (substituteConstraints)
        {
            return MapType(t, GetBaseConstraintOrType);
        }
        return t;
    }
    // checker.go:31276
    internal bool IsGenericTypeWithUnionConstraint(Type t)
    {
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Some(t.AsIntersectionType()!.types, IsGenericTypeWithUnionConstraint);
        }
        return (t.flags & TypeFlags.Instantiable) != 0 && (GetBaseConstraintOrType(t).flags & (TypeFlags.Nullable | TypeFlags.Union)) != 0;
    }
    // checker.go:31266-31274
    internal bool IsConstraintPosition(Type t, Node node)
    {
        var parent = node.Parent!;
        // In an element access obj[x], we consider obj to be in a constraint position, except when obj is of
        // a generic type without a nullable constraint and x is a generic type. This is because when both obj
        // and x are of generic types T and K, we want the resulting type to be T[K].
        return NodeTests.IsPropertyAccessExpression(parent) || NodeTests.IsQualifiedName(parent) ||
            (NodeTests.IsCallExpression(parent) || NodeTests.IsNewExpression(parent)) && parent.Expression() == node ||
            NodeTests.IsElementAccessExpression(parent) && parent.Expression() == node && !(SomeType(t, IsGenericTypeWithoutNullableConstraint) && IsGenericIndexType(GetTypeOfExpression(parent.AsElementAccessExpression().ArgumentExpression!)));
    }

    // checker.go:31283-31288
    internal bool IsGenericTypeWithoutNullableConstraint(Type t)
    {
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Some(t.AsIntersectionType()!.types, IsGenericTypeWithoutNullableConstraint);
        }
        return (t.flags & TypeFlags.Instantiable) != 0 && !MaybeTypeOfKind(GetBaseConstraintOrType(t), TypeFlags.Nullable);
    }

    // checker.go:31290-31302
    internal bool HasContextualTypeWithNoGenericTypes(Node node, CheckMode checkMode)
    {
        // Computing the contextual type for a child of a JSX element involves resolving the type of the
        // element's tag name, so we exclude that here to avoid circularities.
        // If check mode has `CheckMode.RestBindingElement`, we skip binding pattern contextual types,
        // as we want the type of a rest element to be generic when possible.
        if ((NodeTests.IsIdentifier(node) || NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsElementAccessExpression(node)) &&
            !((NodeTests.IsJsxOpeningElement(node.Parent!) || NodeTests.IsJsxSelfClosingElement(node.Parent!)) && node.Parent!.TagName() == node))
        {
            var contextualType = GetContextualType(node, CoreUtil.IfElse((checkMode & CheckMode.RestBindingElement) != 0, ContextFlags.SkipBindingPatterns, ContextFlags.None));
            if (contextualType is not null)
            {
                return !IsGenericType(contextualType);
            }
        }
        return false;
    }
    // checker.go:25322-25330
    internal bool IsUnitLikeType(Type t)
    {
        // Intersections that reduce to 'never' (e.g. 'T & null' where 'T extends {}') are not unit types.
        t = GetBaseConstraintOrType(t);
        // Scan intersections such that tagged literal types are considered unit types.
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Some(t.AsIntersectionType().types, IsUnitType);
        }
        return IsUnitType(t);
    }
    // checker.go:11413-11421
    internal Type GetFlowTypeOfProperty(Node reference, Symbol? prop)
    {
        var initialType = undefinedType!;
        if (prop is not null && prop.ValueDeclaration is not null && (!IsAutoTypedProperty(prop) || (prop.ValueDeclaration.ModifierFlags() & ModifierFlags.Ambient) != 0))
        {
            var baseType = GetTypeOfPropertyInBaseClass(prop);
            if (baseType is not null)
            {
                initialType = baseType;
            }
        }
        return GetFlowTypeOfReferenceEx(reference, autoType!, initialType, null, null);
    }

    // checker.go:11423-11433 — Return the inherited type of the given property or undefined if property doesn't exist in a base class.
    internal Type? GetTypeOfPropertyInBaseClass(Symbol property)
    {
        var classType = GetDeclaringClass(property);
        if (classType is not null)
        {
            var baseClassTypes = GetBaseTypes(classType);
            if (baseClassTypes.Length > 0)
            {
                return GetTypeOfPropertyOfType(baseClassTypes[0], property.Name);
            }
        }
        return null;
    }

    // ---- type→property-name utilities (checker.go free funcs) ----
    // isTypeUsableAsPropertyName / getPropertyNameFromType already stubbed in Checker.SymbolResolution.cs.
    // checker.go:25314-25316
    internal static bool IsNeitherUnitTypeNorNever(Type t) =>
        (t.flags & (TypeFlags.Unit | TypeFlags.Never)) == 0;
    // utilities.go:1054-1056
    internal static bool IsNonNullAccess(Node node) =>
        AstUtilities.IsAccessExpression(node) && NodeTests.IsNonNullExpression(node.Expression()!);
    // IsTypeAny already stubbed in Checker.SymbolTypes.cs.
}

// checker.go — assignment-target kind (getAssignmentTargetKind). Owned by the operator-expressions
// cluster (#8, not yet landed); the enum lives here alongside the getAssignmentTargetKind stub so
// flow's call sites compile. When #8 lands it defines the enum and DELETES this + the stub above.
internal enum AssignmentKind
{
    None,
    Definite,
    Compound,
}
