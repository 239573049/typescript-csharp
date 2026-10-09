// Ported from tsgo internal/checker/relater.go — the (c *Checker) half of the relation engine
// (plan/03 §4.2, §7): the five relation entry points, isSimpleTypeRelatedTo/isEnumTypeRelatedTo,
// checkTypeRelatedTo(Ex) with the relationCount/overflow budget, error-chain assembly, elaboration,
// deeply-nested/recursion identity, discriminant machinery, variance, the signature-helper block,
// template-literal / string-mapping matching, and isTypeDerivedFrom.
//
// The Relater structural engine lives in Checker.Relater.Engine.cs; supporting types and small free
// functions in Checker.Relater.Support.cs. §1.5: diagnostics via the `Diag` alias.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;
using Message = global::Ts.Diagnostics.Message;

namespace Ts.Checker;

public partial class Checker
{
    // relater.go:118-120
    internal bool IsTypeIdenticalTo(Type source, Type target) => IsTypeRelatedTo(source, target, identityRelation!);

    // relater.go:122-127
    internal Ternary CompareTypesIdentical(Type source, Type target) =>
        IsTypeRelatedTo(source, target, identityRelation!) ? Ternary.True : Ternary.False;

    // relater.go:129-134
    internal Ternary CompareTypesAssignableSimple(Type source, Type target) =>
        IsTypeRelatedTo(source, target, assignableRelation!) ? Ternary.True : Ternary.False;

    // relater.go:136-141
    internal Ternary CompareTypesAssignableWorker(Type source, Type target, bool reportErrors) =>
        IsTypeRelatedTo(source, target, assignableRelation!) ? Ternary.True : Ternary.False;

    // relater.go:143-148
    internal Ternary CompareTypesSubtypeOf(Type source, Type target) =>
        IsTypeRelatedTo(source, target, subtypeRelation!) ? Ternary.True : Ternary.False;

    // relater.go:150-152
    internal bool IsTypeAssignableTo(Type source, Type target) => IsTypeRelatedTo(source, target, assignableRelation!);

    // relater.go:154-156
    internal bool IsTypeSubtypeOf(Type source, Type target) => IsTypeRelatedTo(source, target, subtypeRelation!);

    // relater.go:158-160
    internal bool IsTypeStrictSubtypeOf(Type source, Type target) => IsTypeRelatedTo(source, target, strictSubtypeRelation!);

    // relater.go:162-164
    internal bool IsTypeComparableTo(Type source, Type target) => IsTypeRelatedTo(source, target, comparableRelation!);

    // relater.go:166-168
    internal bool AreTypesComparable(Type type1, Type type2) => IsTypeComparableTo(type1, type2) || IsTypeComparableTo(type2, type1);

    // relater.go:170-204
    internal bool IsTypeRelatedTo(Type source, Type target, Relation relation)
    {
        if (IsFreshLiteralType(source))
        {
            source = source.AsLiteralType().regularType!;
        }
        if (IsFreshLiteralType(target))
        {
            target = target.AsLiteralType().regularType!;
        }
        if (source == target)
        {
            return true;
        }
        if (relation != identityRelation)
        {
            if (relation == comparableRelation && (target.flags & TypeFlags.Never) == 0 && IsSimpleTypeRelatedTo(target, source, relation, null) || IsSimpleTypeRelatedTo(source, target, relation, null))
            {
                return true;
            }
        }
        else if (((source.flags | target.flags) & (TypeFlags.UnionOrIntersection | TypeFlags.IndexedAccess | TypeFlags.Conditional | TypeFlags.Substitution)) == 0)
        {
            // We have excluded types that may simplify to other forms, so types must have identical flags
            if (source.flags != target.flags)
            {
                return false;
            }
            if ((source.flags & TypeFlags.Singleton) != 0)
            {
                return true;
            }
        }
        if ((source.flags & TypeFlags.Object) != 0 && (target.flags & TypeFlags.Object) != 0)
        {
            var (id, _) = GetRelationKey(source, target, IntersectionState.None, relation == identityRelation, false);
            var related = relation.Get(id);
            if (related != RelationComparisonResult.None)
            {
                return (related & RelationComparisonResult.Succeeded) != 0;
            }
        }
        if ((source.flags & TypeFlags.StructuredOrInstantiable) != 0 || (target.flags & TypeFlags.StructuredOrInstantiable) != 0)
        {
            return CheckTypeRelatedTo(source, target, relation, null);
        }
        return false;
    }

    // relater.go:206-280
    internal bool IsSimpleTypeRelatedTo(Type source, Type target, Relation relation, ErrorReporter? errorReporter)
    {
        var s = source.flags;
        var t = target.flags;
        if ((t & TypeFlags.Any) != 0 || (s & TypeFlags.Never) != 0 || source == wildcardType)
        {
            return true;
        }
        if ((t & TypeFlags.Unknown) != 0 && !(relation == strictSubtypeRelation && (s & TypeFlags.Any) != 0))
        {
            return true;
        }
        if ((t & TypeFlags.Never) != 0)
        {
            return false;
        }
        if ((s & TypeFlags.StringLike) != 0 && (t & TypeFlags.String) != 0)
        {
            return true;
        }
        if ((s & TypeFlags.StringLiteral) != 0 && (s & TypeFlags.EnumLiteral) != 0 && (t & TypeFlags.StringLiteral) != 0 && (t & TypeFlags.EnumLiteral) == 0 && Equals(source.AsLiteralType().value, target.AsLiteralType().value))
        {
            return true;
        }
        if ((s & TypeFlags.NumberLike) != 0 && (t & TypeFlags.Number) != 0)
        {
            return true;
        }
        if ((s & TypeFlags.NumberLiteral) != 0 && (s & TypeFlags.EnumLiteral) != 0 && (t & TypeFlags.NumberLiteral) != 0 && (t & TypeFlags.EnumLiteral) == 0 && Equals(source.AsLiteralType().value, target.AsLiteralType().value))
        {
            return true;
        }
        if ((s & TypeFlags.BigIntLike) != 0 && (t & TypeFlags.BigInt) != 0)
        {
            return true;
        }
        if ((s & TypeFlags.BooleanLike) != 0 && (t & TypeFlags.Boolean) != 0)
        {
            return true;
        }
        if ((s & TypeFlags.ESSymbolLike) != 0 && (t & TypeFlags.ESSymbol) != 0)
        {
            return true;
        }
        if ((s & TypeFlags.Enum) != 0 && (t & TypeFlags.Enum) != 0 && source.symbol!.Name == target.symbol!.Name && IsEnumTypeRelatedTo(source.symbol, target.symbol, errorReporter))
        {
            return true;
        }
        if ((s & TypeFlags.EnumLiteral) != 0 && (t & TypeFlags.EnumLiteral) != 0)
        {
            if ((s & TypeFlags.Union) != 0 && (t & TypeFlags.Union) != 0 && IsEnumTypeRelatedTo(source.symbol!, target.symbol!, errorReporter))
            {
                return true;
            }
            if ((s & TypeFlags.Literal) != 0 && (t & TypeFlags.Literal) != 0 && Equals(source.AsLiteralType().value, target.AsLiteralType().value) && IsEnumTypeRelatedTo(source.symbol!, target.symbol!, errorReporter))
            {
                return true;
            }
        }
        if ((s & TypeFlags.Undefined) != 0 && (!strictNullChecks && (t & TypeFlags.UnionOrIntersection) == 0 || (t & (TypeFlags.Undefined | TypeFlags.Void)) != 0))
        {
            return true;
        }
        if ((s & TypeFlags.Null) != 0 && (!strictNullChecks && (t & TypeFlags.UnionOrIntersection) == 0 || (t & TypeFlags.Null) != 0))
        {
            return true;
        }
        if ((s & TypeFlags.Object) != 0 && (t & TypeFlags.NonPrimitive) != 0 && !(relation == strictSubtypeRelation && IsEmptyAnonymousObjectType(source) && (source.objectFlags & ObjectFlags.FreshLiteral) == 0))
        {
            return true;
        }
        if (relation == assignableRelation || relation == comparableRelation)
        {
            if ((s & TypeFlags.Any) != 0)
            {
                return true;
            }
            if ((s & TypeFlags.Number) != 0 && ((t & TypeFlags.Enum) != 0 || (t & TypeFlags.NumberLiteral) != 0 && (t & TypeFlags.EnumLiteral) != 0))
            {
                return true;
            }
            if ((s & TypeFlags.NumberLiteral) != 0 && (s & TypeFlags.EnumLiteral) == 0 && ((t & TypeFlags.Enum) != 0 || (t & TypeFlags.NumberLiteral) != 0 && (t & TypeFlags.EnumLiteral) != 0 && Equals(source.AsLiteralType().value, target.AsLiteralType().value)))
            {
                return true;
            }
            if (IsUnknownLikeUnionType(target))
            {
                return true;
            }
        }
        return false;
    }

    // relater.go:282-338
    internal bool IsEnumTypeRelatedTo(Symbol source, Symbol target, ErrorReporter? errorReporter)
    {
        var sourceSymbol = CoreUtil.IfElse((source.Flags & SymbolFlags.EnumMember) != 0, GetParentOfSymbol(source), source);
        var targetSymbol = CoreUtil.IfElse((target.Flags & SymbolFlags.EnumMember) != 0, GetParentOfSymbol(target), target);
        if (sourceSymbol == targetSymbol)
        {
            return true;
        }
        if (sourceSymbol!.Name != targetSymbol!.Name || (sourceSymbol.Flags & SymbolFlags.RegularEnum) == 0 || (targetSymbol.Flags & SymbolFlags.RegularEnum) == 0)
        {
            return false;
        }
        var key = new EnumRelationKey(AstUtilities.GetSymbolId(sourceSymbol), AstUtilities.GetSymbolId(targetSymbol));
        var entry = enumRelation.GetValueOrDefault(key);
        if (entry != RelationComparisonResult.None && !((entry & RelationComparisonResult.Failed) != 0 && errorReporter != null))
        {
            return (entry & RelationComparisonResult.Succeeded) != 0;
        }
        var targetEnumType = GetTypeOfSymbol(targetSymbol);
        foreach (var sourceProperty in GetPropertiesOfType(GetTypeOfSymbol(sourceSymbol)))
        {
            if ((sourceProperty.Flags & SymbolFlags.EnumMember) != 0)
            {
                var targetProperty = GetPropertyOfType(targetEnumType, sourceProperty.Name);
                if (targetProperty == null || (targetProperty.Flags & SymbolFlags.EnumMember) == 0)
                {
                    errorReporter?.Invoke(Diag.Property_0_is_missing_in_type_1, SymbolToString(sourceProperty), TypeToStringEx(GetDeclaredTypeOfSymbol(targetSymbol), null, TypeFormatFlags.UseFullyQualifiedType, null));
                    enumRelation[key] = RelationComparisonResult.Failed;
                    return false;
                }
                var sourceValue = GetEnumMemberValue(AstUtilities.GetDeclarationOfKind(sourceProperty, SyntaxKind.EnumMember)!).Value;
                var targetValue = GetEnumMemberValue(AstUtilities.GetDeclarationOfKind(targetProperty, SyntaxKind.EnumMember)!).Value;
                if (!Equals(sourceValue, targetValue))
                {
                    if (sourceValue != null && targetValue != null)
                    {
                        errorReporter?.Invoke(Diag.Each_declaration_of_0_1_differs_in_its_value_where_2_was_expected_but_3_was_given, SymbolToString(targetSymbol), SymbolToString(targetProperty), ValueToString(targetValue), ValueToString(sourceValue));
                        enumRelation[key] = RelationComparisonResult.Failed;
                        return false;
                    }
                    bool sourceIsString = sourceValue is string;
                    bool targetIsString = targetValue is string;
                    if (sourceIsString || targetIsString)
                    {
                        if (errorReporter != null)
                        {
                            var knownStringValue = CoreUtil.OrElse(sourceValue, targetValue);
                            errorReporter(Diag.One_value_of_0_1_is_the_string_2_and_the_other_is_assumed_to_be_an_unknown_numeric_value, SymbolToString(targetSymbol), SymbolToString(targetProperty), ValueToString(knownStringValue));
                        }
                        enumRelation[key] = RelationComparisonResult.Failed;
                        return false;
                    }
                }
            }
        }
        enumRelation[key] = RelationComparisonResult.Succeeded;
        return true;
    }

    // relater.go:340-342
    internal bool CheckTypeAssignableTo(Type source, Type target, Node? errorNode, Message? headMessage) =>
        CheckTypeRelatedToEx(source, target, assignableRelation!, errorNode, headMessage, null);

    // relater.go:344-346
    internal bool CheckTypeAssignableToEx(Type source, Type target, Node? errorNode, Message? headMessage, List<Diagnostic>? diagnosticOutput) =>
        CheckTypeRelatedToEx(source, target, assignableRelation!, errorNode, headMessage, diagnosticOutput);

    // relater.go:348-350
    internal bool CheckTypeComparableTo(Type source, Type target, Node? errorNode, Message? headMessage) =>
        CheckTypeRelatedToEx(source, target, comparableRelation!, errorNode, headMessage, null);

    // relater.go:352-354
    internal bool CheckTypeRelatedTo(Type source, Type target, Relation relation, Node? errorNode) =>
        CheckTypeRelatedToEx(source, target, relation, errorNode, null, null);

    // relater.go:359-400
    internal bool CheckTypeRelatedToEx(Type source, Type target, Relation relation, Node? errorNode, Message? headMessage, List<Diagnostic>? diagnosticOutput)
    {
        var r = GetRelater();
        r.relation = relation;
        r.errorNode = errorNode;
        r.relationCount = (16_000_000 - relation.Size) / 8;
        var result = r.IsRelatedToEx(source, target, RecursionFlags.Both, errorNode != null, headMessage, IntersectionState.None);
        if (r.overflow)
        {
            var (id, _) = GetRelationKey(source, target, IntersectionState.None, relation == identityRelation, false);
            relation.Set(id, RelationComparisonResult.Failed | (r.relationCount <= 0 ? RelationComparisonResult.ComplexityOverflow : RelationComparisonResult.StackDepthOverflow));
            if (tracer is { } tr)
            {
                tr.Instant(global::Ts.Tracing.Phase.CheckTypes, "checkTypeRelatedTo_DepthLimit", new Dictionary<string, object?> { ["sourceId"] = source.id.Value, ["targetId"] = target.id.Value, ["depth"] = r.sourceStack.Count, ["targetDepth"] = r.targetStack.Count });
            }
            var message = r.relationCount <= 0 ? Diag.Excessive_complexity_comparing_types_0_and_1 : Diag.Excessive_stack_depth_comparing_types_0_and_1;
            errorNode ??= currentNode;
            ReportDiagnostic(NewDiagnosticForNode(errorNode, message, TypeToString(source), TypeToString(target)), diagnosticOutput);
        }
        else if (r.errorChain != null)
        {
            if (headMessage != null && errorNode != null && result == Ternary.False && source.symbol != null && exportTypeLinks.Has(source.symbol))
            {
                var links = exportTypeLinks.Get(source.symbol);
                if (links.originatingImport != null && !IsImportCall(links.originatingImport))
                {
                    var helpfulRetry = CheckTypeRelatedTo(GetTypeOfSymbol(links.target!), target, relation, null);
                    if (helpfulRetry)
                    {
                        r.relatedInfo.Add(CreateDiagnosticForNode(links.originatingImport, Diag.Type_originates_at_this_import_A_namespace_style_import_cannot_be_called_or_constructed_and_will_cause_a_failure_at_runtime_Consider_using_a_default_import_or_import_require_here_instead));
                    }
                }
            }
            ReportDiagnostic(CreateDiagnosticChainFromErrorChain(r.errorChain, r.errorNode, r.relatedInfo.ToArray()), diagnosticOutput);
        }
        PutRelater(r);
        return result != Ternary.False;
    }

    // relater.go:402-414
    internal Diagnostic? CreateDiagnosticChainFromErrorChain(ErrorChain? chain, Node? errorNode, Diagnostic[] relatedInfo)
    {
        while (chain != null && chain.message.ElidedInCompatibilityPyramid)
        {
            chain = chain.next;
        }
        if (chain == null)
        {
            return null;
        }
        var next = CreateDiagnosticChainFromErrorChain(chain.next, errorNode, relatedInfo);
        if (next == null)
        {
            return NewDiagnosticForNode(errorNode, chain.message, chain.args).SetRelatedInfo(relatedInfo);
        }
        return Diagnostic.NewDiagnosticChain(next, chain.message, chain.args);
    }

    // relater.go:416-424
    internal void ReportDiagnostic(Diagnostic? diagnostic, List<Diagnostic>? diagnosticOutput)
    {
        if (diagnostic != null)
        {
            if (diagnosticOutput != null)
            {
                diagnosticOutput.Add(diagnostic);
            }
            else
            {
                AddDiagnostic(diagnostic);
            }
        }
    }

    // Bridge so the Relater engine (separate class) can reach the private static createDiagnosticForNode.
    internal Diagnostic MakeDiagnosticForNode(Node node, Message message, params object?[] args) => CreateDiagnosticForNode(node, message, args);

    // relater.go:2566-2573
    internal Relater GetRelater()
    {
        var r = freeRelater;
        if (r == null)
        {
            r = new Relater(this);
        }
        freeRelater = r.next;
        return r;
    }

    // relater.go:2575-2586 — reset but keep grown collections.
    internal void PutRelater(Relater r)
    {
        r.maybeKeysSet.Clear();
        r.maybeKeys.Clear();
        r.sourceStack.Clear();
        r.targetStack.Clear();
        r.relatedInfo.Clear();
        r.relation = null!;
        r.errorNode = null;
        r.errorChain = null;
        r.maybeCount = 0;
        r.sourceDepth = 0;
        r.targetDepth = 0;
        r.expandingFlags = ExpandingFlags.None;
        r.overflow = false;
        r.relationCount = 0;
        r.next = freeRelater;
        freeRelater = r;
    }

    // relater.go:426-428
    internal bool CheckTypeAssignableToAndOptionallyElaborate(Type source, Type target, Node? errorNode, Node? expr, Message? headMessage, List<Diagnostic>? diagnosticOutput) =>
        CheckTypeRelatedToAndOptionallyElaborate(source, target, assignableRelation!, errorNode, expr, headMessage, diagnosticOutput);

    // relater.go:430-438
    internal bool CheckTypeRelatedToAndOptionallyElaborate(Type source, Type target, Relation relation, Node? errorNode, Node? expr, Message? headMessage, List<Diagnostic>? diagnosticOutput)
    {
        if (IsTypeRelatedTo(source, target, relation))
        {
            return true;
        }
        if (errorNode != null && !ElaborateError(expr, source, target, relation, headMessage, diagnosticOutput))
        {
            return CheckTypeRelatedToEx(source, target, relation, errorNode, headMessage, diagnosticOutput);
        }
        return false;
    }

    // relater.go:440-474
    internal bool ElaborateError(Node? node, Type source, Type target, Relation relation, Message? headMessage, List<Diagnostic>? diagnosticOutput)
    {
        if (node == null || IsOrHasGenericConditional(target))
        {
            return false;
        }
        if (compilerOptions.NoCheck.IsTrue())
        {
            return false;
        }
        if (ElaborateDidYouMeanToCallOrConstruct(node, source, target, relation, SignatureKind.Construct, headMessage, diagnosticOutput) ||
            ElaborateDidYouMeanToCallOrConstruct(node, source, target, relation, SignatureKind.Call, headMessage, diagnosticOutput))
        {
            return true;
        }
        switch (node.Kind)
        {
            case SyntaxKind.AsExpression:
                if (!AstUtilities.IsConstAssertion(node))
                {
                    break;
                }
                goto case SyntaxKind.ParenthesizedExpression;
            case SyntaxKind.JsxExpression:
            case SyntaxKind.ParenthesizedExpression:
                return ElaborateError(node.Expression(), source, target, relation, headMessage, diagnosticOutput);
            case SyntaxKind.BinaryExpression:
                switch (node!.AsBinaryExpression().OperatorToken!.Kind)
                {
                    case SyntaxKind.EqualsToken:
                    case SyntaxKind.CommaToken:
                        return ElaborateError(node!.AsBinaryExpression().Right, source, target, relation, headMessage, diagnosticOutput);
                }
                break;
            case SyntaxKind.ObjectLiteralExpression:
                return ElaborateObjectLiteral(node, source, target, relation, diagnosticOutput);
            case SyntaxKind.ArrayLiteralExpression:
                return ElaborateArrayLiteral(node, source, target, relation, diagnosticOutput);
            case SyntaxKind.ArrowFunction:
                return ElaborateArrowFunction(node, source, target, relation, diagnosticOutput);
            case SyntaxKind.JsxAttributes:
                return ElaborateJsxComponents(node, source, target, relation, diagnosticOutput);
        }
        return false;
    }

    // relater.go:476-478
    internal bool IsOrHasGenericConditional(Type t) =>
        (t.flags & TypeFlags.Conditional) != 0 || ((t.flags & TypeFlags.Intersection) != 0 && CoreUtil.Some(t.Types(), IsOrHasGenericConditional));

    // relater.go:480-496
    internal bool ElaborateDidYouMeanToCallOrConstruct(Node node, Type source, Type target, Relation relation, SignatureKind kind, Message? headMessage, List<Diagnostic>? diagnosticOutput)
    {
        if (CoreUtil.Some(GetSignaturesOfType(source, kind), s =>
        {
            var returnType = GetReturnTypeOfSignature(s);
            return (returnType.flags & (TypeFlags.Any | TypeFlags.Never)) == 0 && CheckTypeRelatedTo(returnType, target, relation, null);
        }))
        {
            List<Diagnostic> diags = [];
            if (!CheckTypeRelatedToEx(source, target, relation, node, headMessage, diags))
            {
                var diagnostic = diags[0];
                var message = kind == SignatureKind.Construct
                    ? Diag.Did_you_mean_to_use_new_with_this_expression
                    : Diag.Did_you_mean_to_call_this_expression;
                ReportDiagnostic(diagnostic.AddRelatedInfo(CreateDiagnosticForNode(node, message)), diagnosticOutput);
                return true;
            }
        }
        return false;
    }

    // relater.go:498-520
    internal bool ElaborateObjectLiteral(Node node, Type source, Type target, Relation relation, List<Diagnostic>? diagnosticOutput)
    {
        if ((target.flags & (TypeFlags.Primitive | TypeFlags.Never)) != 0)
        {
            return false;
        }
        bool reportedError = false;
        foreach (var prop in node.Properties() ?? [])
        {
            if (NodeTests.IsSpreadAssignment(prop))
            {
                continue;
            }
            var nameType = GetLiteralTypeFromProperty(GetSymbolOfDeclaration(prop)!, TypeFlags.StringOrNumberLiteralOrUnique, false);
            if (nameType == null || (nameType.flags & TypeFlags.Never) != 0)
            {
                continue;
            }
            switch (prop.Kind)
            {
                case SyntaxKind.SetAccessor:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.ShorthandPropertyAssignment:
                    reportedError = ElaborateElement(source, target, relation, prop.Name()!, null, nameType, null, null, diagnosticOutput) || reportedError;
                    break;
                case SyntaxKind.PropertyAssignment:
                    var message = IsComputedNonLiteralName(prop.Name()!) ? Diag.Type_of_computed_property_s_value_is_0_which_is_not_assignable_to_type_1 : null;
                    reportedError = ElaborateElement(source, target, relation, prop.Name()!, prop.Initializer(), nameType, message, null, diagnosticOutput) || reportedError;
                    break;
            }
        }
        return reportedError;
    }

    // relater.go:522-544
    internal bool ElaborateArrayLiteral(Node node, Type source, Type target, Relation relation, List<Diagnostic>? diagnosticOutput)
    {
        if ((target.flags & (TypeFlags.Primitive | TypeFlags.Never)) != 0)
        {
            return false;
        }
        if (!IsTupleLikeType(source))
        {
            PushContextualType(node, target, false);
            source = CheckArrayLiteral(node, CheckMode.Contextual | CheckMode.ForceTuple);
            PopContextualType();
            if (!IsTupleLikeType(source))
            {
                return false;
            }
        }
        bool reportedError = false;
        var elements = node.Elements()!;
        for (int i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            if (NodeTests.IsOmittedExpression(element) || IsTupleLikeType(target) && GetPropertyOfType(target, new global::Ts.JsNum.Number(i).ToString()) == null)
            {
                continue;
            }
            var nameType = GetNumberLiteralType(new global::Ts.JsNum.Number(i));
            var checkNode = GetEffectiveCheckNode(element);
            reportedError = ElaborateElement(source, target, relation, checkNode, checkNode, nameType, null, null, diagnosticOutput) || reportedError;
        }
        return reportedError;
    }

    // relater.go:546-618
    internal bool ElaborateElement(Type source, Type target, Relation relation, Node prop, Node? next, Type nameType, Message? errorMessage, Func<Node, Diagnostic>? diagnosticFactory, List<Diagnostic>? diagnosticOutput)
    {
        var targetPropType = GetBestMatchIndexedAccessTypeOrUndefined(source, target, nameType);
        if (targetPropType == null || (targetPropType.flags & TypeFlags.IndexedAccess) != 0)
        {
            return false;
        }
        var sourcePropType = GetIndexedAccessTypeOrUndefined(source, nameType, AccessFlags.None, null, null);
        if (sourcePropType == null || CheckTypeRelatedTo(sourcePropType, targetPropType, relation, null))
        {
            return false;
        }
        if (next != null && ElaborateError(next, sourcePropType, targetPropType, relation, null, diagnosticOutput))
        {
            return true;
        }
        List<Diagnostic> diags = [];
        var specificSource = sourcePropType;
        if (next != null)
        {
            specificSource = CheckExpressionForMutableLocationWithContextualType(next, sourcePropType);
        }
        if (diagnosticFactory != null)
        {
            diags.Add(diagnosticFactory(prop));
        }
        else if (exactOptionalPropertyTypes && IsExactOptionalPropertyMismatch(specificSource, targetPropType))
        {
            diags.Add(CreateDiagnosticForNode(prop, Diag.Type_0_is_not_assignable_to_type_1_with_exactOptionalPropertyTypes_Colon_true_Consider_adding_undefined_to_the_type_of_the_target, TypeToString(specificSource), TypeToString(targetPropType)));
        }
        else
        {
            var propName = GetPropertyNameFromIndex(nameType, null);
            var targetIsOptional = ((GetPropertyOfType(target, propName) ?? unknownSymbol!).Flags & SymbolFlags.Optional) != 0;
            var sourceIsOptional = ((GetPropertyOfType(source, propName) ?? unknownSymbol!).Flags & SymbolFlags.Optional) != 0;
            targetPropType = RemoveMissingType(targetPropType, targetIsOptional);
            sourcePropType = RemoveMissingType(sourcePropType, targetIsOptional && sourceIsOptional);
            var result = CheckTypeRelatedToEx(specificSource, targetPropType, relation, prop, errorMessage, diags);
            if (result && specificSource != sourcePropType)
            {
                CheckTypeRelatedToEx(sourcePropType, targetPropType, relation, prop, errorMessage, diags);
            }
        }
        if (diags.Count == 0)
        {
            return false;
        }
        var diagnostic = diags[0];
        string propertyName = "";
        Symbol? targetProp = null;
        if (IsTypeUsableAsPropertyName(nameType))
        {
            propertyName = GetPropertyNameFromType(nameType);
            targetProp = GetPropertyOfType(target, propertyName);
        }
        bool issuedElaboration = false;
        if (targetProp == null)
        {
            var indexInfo = GetApplicableIndexInfo(target, nameType);
            if (indexInfo != null && indexInfo.declaration != null && !program.IsSourceFileDefaultLibrary(AstUtilities.GetSourceFileOfNode(indexInfo.declaration)!.Path()))
            {
                issuedElaboration = true;
                diagnostic.AddRelatedInfo(CreateDiagnosticForNode(indexInfo.declaration, Diag.The_expected_type_comes_from_this_index_signature));
            }
        }
        if (!issuedElaboration && (targetProp != null && (targetProp.Declarations?.Length ?? 0) != 0 || target.symbol != null && (target.symbol.Declarations?.Length ?? 0) != 0))
        {
            Node targetNode;
            if (targetProp != null && (targetProp.Declarations?.Length ?? 0) != 0)
            {
                targetNode = targetProp.Declarations![0];
            }
            else
            {
                targetNode = target.symbol!.Declarations![0];
            }
            if (propertyName == "" || (nameType.flags & TypeFlags.UniqueESSymbol) != 0)
            {
                propertyName = TypeToString(nameType);
            }
            if (!program.IsSourceFileDefaultLibrary(AstUtilities.GetSourceFileOfNode(targetNode)!.Path()))
            {
                diagnostic.AddRelatedInfo(CreateDiagnosticForNode(targetNode, Diag.The_expected_type_comes_from_property_0_which_is_declared_here_on_type_1, propertyName, TypeToString(target)));
            }
        }
        ReportDiagnostic(diagnostic, diagnosticOutput);
        return true;
    }

    // relater.go:620-632
    internal Type? GetBestMatchIndexedAccessTypeOrUndefined(Type source, Type target, Type nameType)
    {
        var idx = GetIndexedAccessTypeOrUndefined(target, nameType, AccessFlags.None, null, null);
        if (idx != null)
        {
            return idx;
        }
        if ((target.flags & TypeFlags.Union) != 0)
        {
            var best = GetBestMatchingType(source, target, CompareTypesAssignableSimple);
            if (best != null)
            {
                return GetIndexedAccessTypeOrUndefined(best, nameType, AccessFlags.None, null, null);
            }
        }
        return null;
    }

    // relater.go:634-639
    internal Type CheckExpressionForMutableLocationWithContextualType(Node next, Type sourcePropType)
    {
        PushContextualType(next, sourcePropType, false);
        var result = CheckExpressionForMutableLocation(next, CheckMode.Contextual);
        PopContextualType();
        return result;
    }

    // relater.go:641-677
    internal bool ElaborateArrowFunction(Node node, Type source, Type target, Relation relation, List<Diagnostic>? diagnosticOutput)
    {
        if (NodeTests.IsBlock(node.Body()!) || CoreUtil.Some(node.Parameters(), HasType))
        {
            return false;
        }
        var sourceSig = GetSingleCallSignature(source);
        if (sourceSig == null)
        {
            return false;
        }
        var targetSignatures = GetSignaturesOfType(target, SignatureKind.Call);
        if (targetSignatures.Length == 0)
        {
            return false;
        }
        var returnExpression = node.Body();
        var sourceReturn = GetReturnTypeOfSignature(sourceSig);
        var targetReturn = GetUnionType(CoreUtil.Map(targetSignatures, GetReturnTypeOfSignature)!);
        if (CheckTypeRelatedTo(sourceReturn, targetReturn, relation, null))
        {
            return false;
        }
        if (returnExpression != null && ElaborateError(returnExpression, sourceReturn, targetReturn, relation, null, diagnosticOutput))
        {
            return true;
        }
        List<Diagnostic> diags = [];
        CheckTypeRelatedToEx(sourceReturn, targetReturn, relation, returnExpression, null, diags);
        if (diags.Count != 0)
        {
            var diagnostic = diags[0];
            if (target.symbol != null && (target.symbol.Declarations?.Length ?? 0) != 0)
            {
                diagnostic.AddRelatedInfo(CreateDiagnosticForNode(target.symbol.Declarations![0], Diag.The_expected_type_comes_from_the_return_type_of_this_signature));
            }
            if ((AstUtilities.GetFunctionFlags(node) & FunctionFlags.Async) == 0 && GetTypeOfPropertyOfType(sourceReturn, "then") == null && CheckTypeRelatedTo(CreatePromiseType(sourceReturn), targetReturn, relation, null))
            {
                diagnostic.AddRelatedInfo(CreateDiagnosticForNode(node, Diag.Did_you_mean_to_mark_this_function_as_async));
            }
            ReportDiagnostic(diagnostic, diagnosticOutput);
            return true;
        }
        return false;
    }

    // relater.go:681-695
    internal bool IsWeakType(Type t)
    {
        if ((t.flags & TypeFlags.Object) != 0)
        {
            var resolved = ResolveStructuredTypeMembers(t);
            return resolved.signatures.Len() == 0 && resolved.indexInfos.Len() == 0 && resolved.properties.Len() > 0 && CoreUtil.Every(resolved.properties, p => (p.Flags & SymbolFlags.Optional) != 0);
        }
        if ((t.flags & TypeFlags.Substitution) != 0)
        {
            return IsWeakType(t.AsSubstitutionType().baseType);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Every(t.Types(), IsWeakType);
        }
        return false;
    }

    // relater.go:697-704
    internal bool HasCommonProperties(Type source, Type target, bool isComparingJsxAttributes)
    {
        foreach (var prop in GetPropertiesOfType(source))
        {
            if (IsKnownProperty(target, prop.Name, isComparingJsxAttributes))
            {
                return true;
            }
        }
        return false;
    }

    // relater.go:719-743
    internal bool IsKnownProperty(Type targetType, string name, bool isComparingJsxAttributes)
    {
        if ((targetType.flags & TypeFlags.Object) != 0)
        {
            if (GetPropertyOfObjectType(targetType, name) != null ||
                GetApplicableIndexInfoForName(targetType, name) != null ||
                IsLateBoundName(name) && GetIndexInfoOfType(targetType, stringType!) != null ||
                isComparingJsxAttributes && IsHyphenatedJsxName(name))
            {
                return true;
            }
        }
        if ((targetType.flags & TypeFlags.Substitution) != 0)
        {
            return IsKnownProperty(targetType.AsSubstitutionType().baseType, name, isComparingJsxAttributes);
        }
        if ((targetType.flags & TypeFlags.UnionOrIntersection) != 0 && IsExcessPropertyCheckTarget(targetType))
        {
            foreach (var t in targetType.Types())
            {
                if (IsKnownProperty(t, name, isComparingJsxAttributes))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // relater.go:745-747
    internal static bool IsHyphenatedJsxName(string name) => name.Contains('-', StringComparison.Ordinal);

    // relater.go:749-755
    internal static bool IsExcessPropertyCheckTarget(Type t) =>
        (t.flags & TypeFlags.Object) != 0 && (t.objectFlags & ObjectFlags.ObjectLiteralPatternWithComputedProperties) == 0 ||
        (t.flags & TypeFlags.NonPrimitive) != 0 ||
        (t.flags & TypeFlags.Substitution) != 0 && IsExcessPropertyCheckTarget(t.AsSubstitutionType().baseType) ||
        (t.flags & TypeFlags.Union) != 0 && CoreUtil.Some(t.Types(), IsExcessPropertyCheckTarget) ||
        (t.flags & TypeFlags.Intersection) != 0 && CoreUtil.Every(t.Types(), IsExcessPropertyCheckTarget);

    // relater.go:2822-2824
    internal static bool ShouldCheckAsExcessProperty(Symbol prop, Symbol container) =>
        prop.ValueDeclaration != null && container.ValueDeclaration != null && prop.ValueDeclaration.Parent == container.ValueDeclaration;

    // relater.go:2826-2828
    internal static bool IsIgnoredJsxProperty(Type source, Symbol sourceProp) =>
        (source.objectFlags & ObjectFlags.JsxAttributes) != 0 && IsHyphenatedJsxName(sourceProp.Name);

    // relater.go:768-798
    internal bool IsDeeplyNestedType(Type t, List<Type> stack, int maxDepth)
    {
        if (stack.Count >= maxDepth)
        {
            if ((t.objectFlags & ObjectFlags.InstantiatedMapped) == ObjectFlags.InstantiatedMapped)
            {
                t = GetMappedTargetWithSymbol(t);
            }
            if ((t.flags & TypeFlags.Intersection) != 0)
            {
                foreach (var tt in t.Types())
                {
                    if (IsDeeplyNestedType(tt, stack, maxDepth))
                    {
                        return true;
                    }
                }
            }
            var identity = GetRecursionIdentity(t);
            int count = 0;
            var lastTypeId = new TypeId(0);
            foreach (var st in stack)
            {
                if (HasMatchingRecursionIdentity(st, identity))
                {
                    if (st.id.Value >= lastTypeId.Value)
                    {
                        count++;
                        if (count >= maxDepth)
                        {
                            return true;
                        }
                    }
                    lastTypeId = st.id;
                }
            }
        }
        return false;
    }

    // relater.go:804-816
    internal Type GetMappedTargetWithSymbol(Type t)
    {
        while (true)
        {
            if ((t.objectFlags & ObjectFlags.InstantiatedMapped) == ObjectFlags.InstantiatedMapped)
            {
                var target = GetModifiersTypeFromMappedType(t);
                if (target != null && (target.symbol != null || (target.flags & TypeFlags.Intersection) != 0 &&
                    CoreUtil.Some(target.Types(), tt => tt.symbol != null)))
                {
                    t = target;
                    continue;
                }
            }
            return t;
        }
    }

    // relater.go:818-831
    internal bool HasMatchingRecursionIdentity(Type t, RecursionId identity)
    {
        if ((t.objectFlags & ObjectFlags.InstantiatedMapped) == ObjectFlags.InstantiatedMapped)
        {
            t = GetMappedTargetWithSymbol(t);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            foreach (var tt in t.Types())
            {
                if (HasMatchingRecursionIdentity(tt, identity))
                {
                    return true;
                }
            }
            return false;
        }
        return GetRecursionIdentity(t) == identity;
    }

    // relater.go:879-896
    internal Type? GetBestMatchingType(Type source, Type target, Func<Type, Type, Ternary> isRelatedTo)
    {
        var t1 = FindMatchingDiscriminantType(source, target, isRelatedTo);
        if (t1 != null)
        {
            return t1;
        }
        var t2 = FindMatchingTypeReferenceOrTypeAliasReference(source, target);
        if (t2 != null)
        {
            return t2;
        }
        var t3 = FindBestTypeForObjectLiteral(source, target);
        if (t3 != null)
        {
            return t3;
        }
        var t4 = FindBestTypeForInvokable(source, target, SignatureKind.Call);
        if (t4 != null)
        {
            return t4;
        }
        var t5 = FindBestTypeForInvokable(source, target, SignatureKind.Construct);
        if (t5 != null)
        {
            return t5;
        }
        return FindMostOverlappyType(source, target);
    }

    // relater.go:898-914
    internal Type? FindMatchingTypeReferenceOrTypeAliasReference(Type source, Type unionTarget)
    {
        var sourceObjectFlags = source.objectFlags;
        if ((sourceObjectFlags & (ObjectFlags.Reference | ObjectFlags.Anonymous)) != 0 && (unionTarget.flags & TypeFlags.Union) != 0)
        {
            foreach (var target in unionTarget.Types())
            {
                if ((target.flags & TypeFlags.Object) != 0)
                {
                    var overlapObjFlags = sourceObjectFlags & target.objectFlags;
                    if ((overlapObjFlags & ObjectFlags.Reference) != 0 && source.Target() == target.Target())
                    {
                        return target;
                    }
                    if ((overlapObjFlags & ObjectFlags.Anonymous) != 0 && source.alias != null && target.alias != null && source.alias.symbol == target.alias.symbol)
                    {
                        return target;
                    }
                }
            }
        }
        return null;
    }

    // relater.go:916-921
    internal Type? FindBestTypeForInvokable(Type source, Type unionTarget, SignatureKind kind)
    {
        if (GetSignaturesOfType(source, kind).Length != 0)
        {
            return CoreUtil.Find(unionTarget.Types(), t => GetSignaturesOfType(t, kind).Length != 0);
        }
        return null;
    }

    // relater.go:923-950
    internal Type? FindMostOverlappyType(Type source, Type unionTarget)
    {
        Type? bestMatch = null;
        if ((source.flags & (TypeFlags.Primitive | TypeFlags.InstantiablePrimitive)) == 0)
        {
            int matchingCount = 0;
            foreach (var target in unionTarget.Types())
            {
                if ((target.flags & (TypeFlags.Primitive | TypeFlags.InstantiablePrimitive)) == 0)
                {
                    var overlap = GetIntersectionType([GetIndexType(source), GetIndexType(target)]);
                    if ((overlap.flags & TypeFlags.Index) != 0)
                    {
                        return target;
                    }
                    else if (IsUnitType(overlap) || (overlap.flags & TypeFlags.Union) != 0)
                    {
                        int length = 1;
                        if ((overlap.flags & TypeFlags.Union) != 0)
                        {
                            length = CoreUtil.CountWhere(overlap.Types(), IsUnitType);
                        }
                        if (length >= matchingCount)
                        {
                            bestMatch = target;
                            matchingCount = length;
                        }
                    }
                }
            }
        }
        return bestMatch;
    }

    // relater.go:952-957
    internal Type? FindBestTypeForObjectLiteral(Type source, Type unionTarget)
    {
        if ((source.objectFlags & ObjectFlags.ObjectLiteral) != 0 && Checker.SomeType(unionTarget, IsArrayLikeType))
        {
            return CoreUtil.Find(unionTarget.Types(), t => !IsArrayLikeType(t));
        }
        return null;
    }

    // relater.go:959-972
    internal bool ShouldReportUnmatchedPropertyError(Type source, Type target)
    {
        var typeCallSignatures = GetSignaturesOfStructuredType(source, SignatureKind.Call);
        var typeConstructSignatures = GetSignaturesOfStructuredType(source, SignatureKind.Construct);
        var typeProperties = GetPropertiesOfObjectType(source);
        if ((typeCallSignatures.Length != 0 || typeConstructSignatures.Length != 0) && typeProperties.Length == 0)
        {
            if ((GetSignaturesOfType(target, SignatureKind.Call).Length != 0 && typeCallSignatures.Length != 0) ||
                GetSignaturesOfType(target, SignatureKind.Construct).Length != 0 && typeConstructSignatures.Length != 0)
            {
                return true;
            }
            return false;
        }
        return true;
    }

    // relater.go:974-976
    internal Symbol? GetUnmatchedProperty(Type source, Type target, bool requireOptionalProperties, bool matchDiscriminantProperties) =>
        GetUnmatchedPropertiesWorker(source, target, requireOptionalProperties, matchDiscriminantProperties, null);

    // relater.go:978-982
    internal Symbol[] GetUnmatchedProperties(Type source, Type target, bool requireOptionalProperties, bool matchDiscriminantProperties)
    {
        List<Symbol> props = [];
        GetUnmatchedPropertiesWorker(source, target, requireOptionalProperties, matchDiscriminantProperties, props);
        return props.ToArray();
    }

    // relater.go:984-1013
    internal Symbol? GetUnmatchedPropertiesWorker(Type source, Type target, bool requireOptionalProperties, bool matchDiscriminantProperties, List<Symbol>? propsOut)
    {
        var properties = GetPropertiesOfType(target);
        foreach (var targetProp in properties)
        {
            if (IsStaticPrivateIdentifierProperty(targetProp))
            {
                continue;
            }
            if (requireOptionalProperties || (targetProp.Flags & SymbolFlags.Optional) == 0 && (targetProp.CheckFlags & CheckFlags.Partial) == 0)
            {
                var sourceProp = GetPropertyOfType(source, targetProp.Name);
                if (sourceProp == null)
                {
                    if (propsOut == null)
                    {
                        return targetProp;
                    }
                    propsOut.Add(targetProp);
                }
                else if (matchDiscriminantProperties)
                {
                    var targetType = GetTypeOfSymbol(targetProp);
                    if ((targetType.flags & TypeFlags.Unit) != 0)
                    {
                        var sourceType = GetTypeOfSymbol(sourceProp);
                        if (!((sourceType.flags & TypeFlags.Any) != 0 || GetRegularTypeOfLiteralType(sourceType) == GetRegularTypeOfLiteralType(targetType)))
                        {
                            if (propsOut == null)
                            {
                                return targetProp;
                            }
                            propsOut.Add(targetProp);
                        }
                    }
                }
            }
        }
        return null;
    }

    // relater.go:1015-1035
    internal static Symbol[] ExcludeProperties(Symbol[] properties, Set<string> excludedProperties)
    {
        if (excludedProperties.Len() == 0 || properties.Length == 0)
        {
            return properties;
        }
        List<Symbol>? reduced = null;
        bool excluded = false;
        for (int i = 0; i < properties.Length; i++)
        {
            var prop = properties[i];
            if (!excludedProperties.Has(prop.Name))
            {
                if (excluded)
                {
                    reduced!.Add(prop);
                }
            }
            else if (!excluded)
            {
                reduced = new List<Symbol>(properties[..i]);
                excluded = true;
            }
        }
        if (excluded)
        {
            return reduced!.ToArray();
        }
        return properties;
    }

    // relater.go:1062-1075
    internal Type? FindMatchingDiscriminantType(Type source, Type target, Func<Type, Type, Ternary> isRelatedTo)
    {
        if ((target.flags & TypeFlags.Union) != 0 && (source.flags & (TypeFlags.Intersection | TypeFlags.Object)) != 0)
        {
            var match = GetMatchingUnionConstituentForType(target, source);
            if (match != null)
            {
                return match;
            }
            var discriminantProperties = FindDiscriminantProperties(GetPropertiesOfType(source), target);
            if (discriminantProperties.Length != 0)
            {
                var discriminator = new TypeDiscriminator(this, discriminantProperties, isRelatedTo);
                var discriminated = DiscriminateTypeByDiscriminableItems(target, discriminator);
                if (discriminated != target)
                {
                    return discriminated;
                }
            }
        }
        return null;
    }

    // relater.go:1077-1085
    internal Symbol[] FindDiscriminantProperties(Symbol[] sourceProperties, Type target)
    {
        List<Symbol> result = [];
        foreach (var sourceProperty in sourceProperties)
        {
            if (IsDiscriminantProperty(target, sourceProperty.Name))
            {
                result.Add(sourceProperty);
            }
        }
        return result.ToArray();
    }

    // relater.go:1087-1101
    internal bool IsDiscriminantProperty(Type? t, string name)
    {
        if (t != null && (t.flags & TypeFlags.Union) != 0)
        {
            var prop = GetUnionOrIntersectionProperty(t, name, false);
            if (prop != null && (prop.CheckFlags & CheckFlags.SyntheticProperty) != 0)
            {
                if ((prop.CheckFlags & CheckFlags.IsDiscriminantComputed) == 0)
                {
                    prop.CheckFlags |= CheckFlags.IsDiscriminantComputed;
                    if ((prop.CheckFlags & CheckFlags.NonUniformAndLiteral) == CheckFlags.NonUniformAndLiteral && !IsGenericType(GetTypeOfSymbol(prop)))
                    {
                        prop.CheckFlags |= CheckFlags.IsDiscriminant;
                    }
                }
                return (prop.CheckFlags & CheckFlags.IsDiscriminant) != 0;
            }
        }
        return false;
    }

    // relater.go:1103-1113
    internal Type? GetMatchingUnionConstituentForType(Type unionType, Type t)
    {
        var keyPropertyName = GetKeyPropertyName(unionType);
        if (keyPropertyName == "")
        {
            return null;
        }
        var propType = GetTypeOfPropertyOfType(t, keyPropertyName);
        if (propType == null)
        {
            return null;
        }
        return GetConstituentTypeForKeyType(unionType, propType);
    }

    // relater.go:1118-1127
    internal string GetKeyPropertyName(Type t)
    {
        var u = t.AsUnionType();
        if (u.keyPropertyName == "")
        {
            var (name, map) = ComputeKeyPropertyNameAndMap(t);
            u.keyPropertyName = name;
            u.constituentMap = map;
        }
        if (u.keyPropertyName == InternalSymbolName.Missing)
        {
            return "";
        }
        return u.keyPropertyName;
    }

    // relater.go:1131-1137
    internal Type? GetConstituentTypeForKeyType(Type t, Type keyType)
    {
        var result = t.AsUnionType().constituentMap?.GetValueOrDefault(GetRegularTypeOfLiteralType(keyType));
        if (result != null && result != unknownType)
        {
            return result;
        }
        return null;
    }

    // relater.go:1139-1153
    internal (string, Dictionary<Type, Type>?) ComputeKeyPropertyNameAndMap(Type t)
    {
        var types = t.Types();
        if (types.Length < 10 || (t.objectFlags & ObjectFlags.PrimitiveUnion) != 0 || CoreUtil.CountWhere(types, IsObjectOrInstantiableNonPrimitive) < 10)
        {
            return (InternalSymbolName.Missing, null);
        }
        var keyPropertyName = GetKeyPropertyCandidateName(types);
        if (keyPropertyName == "")
        {
            return (InternalSymbolName.Missing, null);
        }
        var mapByKeyProperty = MapTypesByKeyProperty(types, keyPropertyName);
        if (mapByKeyProperty == null)
        {
            return (InternalSymbolName.Missing, null);
        }
        return (keyPropertyName, mapByKeyProperty);
    }

    // relater.go:1155-1157
    internal static bool IsObjectOrInstantiableNonPrimitive(Type t) => (t.flags & (TypeFlags.Object | TypeFlags.InstantiableNonPrimitive)) != 0;

    // relater.go:1159-1170
    internal string GetKeyPropertyCandidateName(Type[] types)
    {
        foreach (var t in types)
        {
            if ((t.flags & (TypeFlags.Object | TypeFlags.InstantiableNonPrimitive)) != 0)
            {
                foreach (var p in GetPropertiesOfType(t))
                {
                    if (IsUnitType(GetTypeOfSymbol(p)))
                    {
                        return p.Name;
                    }
                }
            }
        }
        return "";
    }

    // relater.go:1176-1204
    internal Dictionary<Type, Type>? MapTypesByKeyProperty(Type[] types, string keyPropertyName)
    {
        var typesByKey = new Dictionary<Type, Type>(ReferenceEqualityComparer.Instance);
        int count = 0;
        foreach (var t in types)
        {
            if ((t.flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.InstantiableNonPrimitive)) != 0)
            {
                var discriminant = GetTypeOfPropertyOfType(t, keyPropertyName);
                if (discriminant == null || !IsLiteralType(discriminant))
                {
                    return null;
                }
                bool duplicate = false;
                foreach (var d in discriminant.Distributed())
                {
                    var key = GetRegularTypeOfLiteralType(d);
                    var existing = typesByKey.GetValueOrDefault(key);
                    if (existing == null)
                    {
                        typesByKey[key] = t;
                    }
                    else if (existing != unknownType)
                    {
                        typesByKey[key] = unknownType!;
                        duplicate = true;
                    }
                }
                if (!duplicate)
                {
                    count++;
                }
            }
        }
        if (count >= 10 && count * 2 >= types.Length)
        {
            return typesByKey;
        }
        return null;
    }

    // relater.go:1212-1261
    internal Type DiscriminateTypeByDiscriminableItems(Type target, IDiscriminator discriminator)
    {
        var types = target.Types();
        var include = new Ternary[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if ((t.flags & TypeFlags.Primitive) == 0 && (GetReducedType(t).flags & TypeFlags.Never) == 0)
            {
                include[i] = Ternary.True;
            }
        }
        for (int n = 0; n < discriminator.Len(); n++)
        {
            bool matched = false;
            for (int i = 0; i < types.Length; i++)
            {
                if (include[i] != Ternary.False)
                {
                    var targetType = GetTypeOfPropertyOrIndexSignatureOfType(types[i], discriminator.Name(n));
                    if (targetType != null)
                    {
                        if (discriminator.Matches(n, targetType))
                        {
                            matched = true;
                        }
                        else
                        {
                            include[i] = Ternary.Maybe;
                        }
                    }
                }
            }
            for (int i = 0; i < types.Length; i++)
            {
                if (include[i] == Ternary.Maybe)
                {
                    include[i] = matched ? Ternary.False : Ternary.True;
                }
            }
        }
        if (Array.IndexOf(include, Ternary.False) >= 0)
        {
            List<Type> filteredTypes = [];
            for (int i = 0; i < types.Length; i++)
            {
                if (include[i] == Ternary.True)
                {
                    filteredTypes.Add(types[i]);
                }
            }
            var filtered = GetUnionTypeEx(filteredTypes.ToArray(), UnionReduction.None, null, null);
            if ((filtered.flags & TypeFlags.Never) == 0)
            {
                return filtered;
            }
        }
        return target;
    }

    // relater.go:1263-1271
    internal Type FilterPrimitivesIfContainsNonPrimitive(Type unionType)
    {
        if (MaybeTypeOfKind(unionType, TypeFlags.NonPrimitive))
        {
            var result = FilterType(unionType, IsNonPrimitiveType);
            if ((result.flags & TypeFlags.Never) == 0)
            {
                return result;
            }
        }
        return unionType;
    }

    // relater.go:1273-1275
    internal static bool IsNonPrimitiveType(Type t) => (t.flags & TypeFlags.Primitive) == 0;

    // relater.go:1277-1295
    internal (string, string) GetTypeNamesForErrorDisplay(Type left, Type right)
    {
        string leftStr;
        if (SymbolValueDeclarationIsContextSensitive(left.symbol))
        {
            leftStr = TypeToStringEx(left, left.symbol!.ValueDeclaration, TypeFormatFlags.None, null);
        }
        else
        {
            leftStr = TypeToString(left);
        }
        string rightStr;
        if (SymbolValueDeclarationIsContextSensitive(right.symbol))
        {
            rightStr = TypeToStringEx(right, right.symbol!.ValueDeclaration, TypeFormatFlags.None, null);
        }
        else
        {
            rightStr = TypeToString(right);
        }
        if (leftStr == rightStr)
        {
            leftStr = GetTypeNameForErrorDisplay(left);
            rightStr = GetTypeNameForErrorDisplay(right);
        }
        return (leftStr, rightStr);
    }

    // relater.go:1297-1299
    internal string GetTypeNameForErrorDisplay(Type t) => TypeToStringEx(t, null, TypeFormatFlags.UseFullyQualifiedType, null);

    // relater.go:1301-1303
    internal bool SymbolValueDeclarationIsContextSensitive(Symbol? symbol) =>
        symbol != null && symbol.ValueDeclaration != null && AstUtilities.IsExpression(symbol.ValueDeclaration) && !IsContextSensitive(symbol.ValueDeclaration);

    // relater.go:1305-1322
    internal bool TypeCouldHaveTopLevelSingletonTypes(Type t)
    {
        if ((t.flags & TypeFlags.Boolean) != 0)
        {
            return false;
        }
        if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            return CoreUtil.Some(t.Types(), TypeCouldHaveTopLevelSingletonTypes);
        }
        if ((t.flags & TypeFlags.Instantiable) != 0)
        {
            var constraint = GetConstraintOfType(t);
            if (constraint != null && constraint != t)
            {
                return TypeCouldHaveTopLevelSingletonTypes(constraint);
            }
        }
        return IsUnitType(t) || (t.flags & TypeFlags.TemplateLiteral) != 0 || (t.flags & TypeFlags.StringMapping) != 0;
    }

    // relater.go:1324-1330
    internal VarianceFlags[] GetVariances(Type t)
    {
        if (t == globalArrayType || t == globalReadonlyArrayType || (t.objectFlags & ObjectFlags.Tuple) != 0)
        {
            return arrayVariances;
        }
        return GetVariancesWorker(t.symbol, t.AsInterfaceType()!.TypeParameters());
    }

    // relater.go:1332-1334
    internal VarianceFlags[] GetAliasVariances(Symbol symbol) => GetVariancesWorker(symbol, typeAliasLinks.Get(symbol).typeParameters);

    // relater.go:1341-1411
    internal VarianceFlags[] GetVariancesWorker(Symbol? symbol, Type[]? typeParameters)
    {
        var links = varianceLinks.Get(symbol!);
        if (links.variances == null)
        {
            // relater.go:1344-1356 — Go `defer func() { traceArgs["variances"] = formatted; popFn() }()`
            // → try/finally; the end event carries the formatted variances.
            Dictionary<string, object?>? traceArgs = null;
            Action? popFn = null;
            if (tracer is { } tr)
            {
                traceArgs = new Dictionary<string, object?> { ["arity"] = typeParameters.Len(), ["id"] = GetDeclaredTypeOfSymbol(symbol!).id.Value };
                popFn = tr.Push(global::Ts.Tracing.Phase.CheckTypes, "getVariancesWorker", traceArgs, true);
            }
            try
            {
                var oldVarianceComputation = inVarianceComputation;
                var saveResolutionStart = resolutionStart;
                if (!inVarianceComputation)
                {
                    inVarianceComputation = true;
                    resolutionStart = typeResolutions.Count;
                }
                links.variances = [];
                var variances = new VarianceFlags[typeParameters.Len()];
                for (int i = 0; i < typeParameters.Len(); i++)
                {
                    var tp = typeParameters![i];
                    var modifiers = GetTypeParameterModifiers(tp);
                    VarianceFlags variance;
                    if ((modifiers & ModifierFlags.Out) != 0)
                    {
                        if ((modifiers & ModifierFlags.In) != 0)
                        {
                            variance = VarianceFlags.Invariant;
                        }
                        else
                        {
                            variance = VarianceFlags.Covariant;
                        }
                    }
                    else if ((modifiers & ModifierFlags.In) != 0)
                    {
                        variance = VarianceFlags.Contravariant;
                    }
                    else
                    {
                        var saveReliabilityFlags = reliabilityFlags;
                        reliabilityFlags = 0;
                        var typeWithSuper = CreateMarkerType(symbol!, tp, markerSuperType!);
                        var typeWithSub = CreateMarkerType(symbol!, tp, markerSubType!);
                        variance = (IsTypeAssignableTo(typeWithSub, typeWithSuper) ? VarianceFlags.Covariant : 0) |
                            (IsTypeAssignableTo(typeWithSuper, typeWithSub) ? VarianceFlags.Contravariant : 0);
                        if (variance == VarianceFlags.Bivariant && IsTypeAssignableTo(CreateMarkerType(symbol!, tp, markerOtherType!), typeWithSuper))
                        {
                            variance = VarianceFlags.Independent;
                        }
                        if ((reliabilityFlags & RelationComparisonResult.ReportsUnmeasurable) != 0)
                        {
                            variance |= VarianceFlags.Unmeasurable;
                        }
                        if ((reliabilityFlags & RelationComparisonResult.ReportsUnreliable) != 0)
                        {
                            variance |= VarianceFlags.Unreliable;
                        }
                        reliabilityFlags = saveReliabilityFlags;
                    }
                    variances[i] = variance;
                }
                if (!oldVarianceComputation)
                {
                    inVarianceComputation = false;
                    resolutionStart = saveResolutionStart;
                }
                links.variances = variances;
            }
            finally
            {
                if (popFn != null)
                {
                    var linkVariances = links.variances ?? [];
                    var formatted = new string[linkVariances.Length];
                    for (int i = 0; i < linkVariances.Length; i++)
                    {
                        formatted[i] = linkVariances[i].ToVarianceString();
                    }
                    traceArgs!["variances"] = formatted;
                    popFn();
                }
            }
        }
        return links.variances;
    }

    // relater.go:1413-1427
    internal Type CreateMarkerType(Symbol symbol, Type source, Type target)
    {
        var mapper = TypeMapper.NewSimpleTypeMapper(source, target);
        var t = GetDeclaredTypeOfSymbol(symbol);
        if (IsErrorType(t))
        {
            return t;
        }
        Type result;
        if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            result = GetTypeAliasInstantiation(symbol, InstantiateTypes(typeAliasLinks.Get(symbol).typeParameters, mapper), null);
        }
        else
        {
            result = CreateTypeReference(t, InstantiateTypes(t.AsInterfaceType()!.TypeParameters(), mapper));
        }
        markerTypes.Add(result);
        return result;
    }

    // relater.go:1429-1431
    internal bool IsMarkerType(Type t) => markerTypes.Has(t);

    // relater.go:1433-1441
    internal ModifierFlags GetTypeParameterModifiers(Type tp)
    {
        ModifierFlags flags = 0;
        if (tp.symbol != null)
        {
            foreach (var d in tp.symbol.Declarations.OrEmpty())
            {
                flags |= d.ModifierFlags();
            }
        }
        return flags & (ModifierFlags.In | ModifierFlags.Out | ModifierFlags.Const);
    }

    // relater.go:1445-1452
    internal bool HasCovariantVoidArgument(Type[] typeArguments, VarianceFlags[] variances)
    {
        for (int i = 0; i < variances.Length; i++)
        {
            if ((variances[i] & VarianceFlags.VarianceMask) == VarianceFlags.Covariant && (typeArguments[i].flags & TypeFlags.Void) != 0)
            {
                return true;
            }
        }
        return false;
    }

    // relater.go:2796-2802
    internal Type GetTypeOfPropertyInTypes(Type[] types, string name)
    {
        List<Type> propTypes = [];
        foreach (var t in types)
        {
            propTypes.Add(GetTypeOfPropertyInType(t, name));
        }
        return GetUnionType(propTypes.ToArray());
    }

    // relater.go:2804-2820
    internal Type GetTypeOfPropertyInType(Type t, string name)
    {
        t = GetApparentType(t);
        Symbol? prop;
        if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            prop = GetPropertyOfUnionOrIntersectionType(t, name, false);
        }
        else
        {
            prop = GetPropertyOfObjectType(t, name);
        }
        if (prop != null)
        {
            return GetTypeOfSymbol(prop);
        }
        var indexInfo = GetApplicableIndexInfoForName(t, name);
        if (indexInfo != null)
        {
            return indexInfo.valueType;
        }
        return undefinedType!;
    }

    // relater.go:2830-2832
    internal bool IsTypeSubsetOf(Type source, Type target) =>
        source == target || (source.flags & TypeFlags.Never) != 0 || (target.flags & TypeFlags.Union) != 0 && IsTypeSubsetOfUnion(source, target);

    // relater.go:2834-2847
    internal bool IsTypeSubsetOfUnion(Type source, Type target)
    {
        if ((source.flags & TypeFlags.Union) != 0)
        {
            foreach (var t in source.Types())
            {
                if (!ContainsType(target.Types(), t))
                {
                    return false;
                }
            }
            return true;
        }
        if ((source.flags & TypeFlags.EnumLike) != 0 && GetBaseTypeOfEnumLikeType(source) == target)
        {
            return true;
        }
        return ContainsType(target.Types(), source);
    }

    // relater.go:4617-4625
    internal bool IsObjectTypeWithInferableIndex(Type t)
    {
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Every(t.Types(), IsObjectTypeWithInferableIndex);
        }
        return t.symbol != null &&
            (t.symbol.Flags & (SymbolFlags.ObjectLiteral | SymbolFlags.TypeLiteral | SymbolFlags.Enum | SymbolFlags.ValueModule)) != 0 && (t.symbol.Flags & SymbolFlags.Class) == 0 && !TypeHasCallOrConstructSignatures(t) ||
            (t.objectFlags & (ObjectFlags.JSLiteral | ObjectFlags.ObjectRestType)) != 0 ||
            (t.objectFlags & ObjectFlags.ReverseMapped) != 0 && IsObjectTypeWithInferableIndex(t.AsReverseMappedType().source!);
    }

    // relater.go:4955-4984
    internal bool IsTypeDerivedFrom(Type source, Type target)
    {
        if ((source.flags & TypeFlags.Union) != 0)
        {
            return CoreUtil.Every(source.AsUnionType().types, t => IsTypeDerivedFrom(t, target));
        }
        if ((target.flags & TypeFlags.Union) != 0)
        {
            return CoreUtil.Some(target.AsUnionType().types, t => IsTypeDerivedFrom(source, t));
        }
        if ((source.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Some(source.AsIntersectionType().types, t => IsTypeDerivedFrom(t, target));
        }
        if ((source.flags & TypeFlags.InstantiableNonPrimitive) != 0)
        {
            var constraint = GetBaseConstraintOfType(source);
            if (constraint == null)
            {
                constraint = unknownType;
            }
            return IsTypeDerivedFrom(constraint!, target);
        }
        if (IsEmptyAnonymousObjectType(target))
        {
            return (source.flags & (TypeFlags.Object | TypeFlags.NonPrimitive)) != 0;
        }
        if (target == globalObjectType)
        {
            return (source.flags & (TypeFlags.Object | TypeFlags.NonPrimitive)) != 0 && !IsEmptyAnonymousObjectType(source);
        }
        if (target == globalFunctionType)
        {
            return (source.flags & TypeFlags.Object) != 0 && IsFunctionObjectType(source);
        }
        return HasBaseType(source, GetTargetType(target)) || (IsArrayType(target) && !IsReadonlyArrayType(target) && IsTypeDerivedFrom(source, globalReadonlyArrayType!));
    }

    // relater.go:4986-4988
    internal bool IsDistributionDependent(ConditionalRoot root) =>
        root.isDistributive && (IsTypeParameterPossiblyReferenced(root.checkType!, root.node!.TrueType!) || IsTypeParameterPossiblyReferenced(root.checkType!, root.node.FalseType!));
}
