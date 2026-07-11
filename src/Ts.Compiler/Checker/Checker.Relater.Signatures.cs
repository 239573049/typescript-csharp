// Ported from tsgo internal/checker/relater.go — the signature-comparison and signature-shape helper
// block (relater.go:1454-2524) plus the template-literal / string-mapping matching helpers. These are
// (c *Checker) methods that the relation engine (and callers across the checker) rely on.
// §1.5: diagnostics via the `Diag` alias.

using System;
using System.Globalization;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // relater.go:1454-1456
    internal bool IsSignatureAssignableTo(Signature source, Signature target, bool ignoreReturnTypes) =>
        CompareSignaturesRelated(source, target, ignoreReturnTypes ? SignatureCheckMode.IgnoreReturnTypes : SignatureCheckMode.None, false, null, compareTypesAssignable, null) != Ternary.False;

    // relater.go:1458-1640
    internal Ternary CompareSignaturesRelated(Signature source, Signature target, SignatureCheckMode checkMode, bool reportErrors, ErrorReporter? errorReporter, TypeComparer compareTypes, TypeMapper? reportUnreliableMarkers)
    {
        if (source == target)
        {
            return Ternary.True;
        }
        if (!((checkMode & SignatureCheckMode.StrictTopSignature) != 0 && IsTopSignature(source)) && IsTopSignature(target))
        {
            return Ternary.True;
        }
        if ((checkMode & SignatureCheckMode.StrictTopSignature) != 0 && IsTopSignature(source) && !IsTopSignature(target))
        {
            return Ternary.False;
        }
        int targetCount = GetParameterCount(target);
        bool sourceHasMoreParameters;
        if (!HasEffectiveRestParameter(target))
        {
            if ((checkMode & SignatureCheckMode.StrictArity) != 0)
            {
                sourceHasMoreParameters = HasEffectiveRestParameter(source) || GetParameterCount(source) > targetCount;
            }
            else
            {
                sourceHasMoreParameters = GetMinArgumentCount(source) > targetCount;
            }
        }
        else
        {
            sourceHasMoreParameters = false;
        }
        if (sourceHasMoreParameters)
        {
            if (reportErrors && (checkMode & SignatureCheckMode.StrictArity) == 0)
            {
                errorReporter!(Diag.Target_signature_provides_too_few_arguments_Expected_0_or_more_but_got_1, GetMinArgumentCount(source), targetCount);
            }
            return Ternary.False;
        }
        if (source.typeParameters.Len() != 0 && !CoreUtil.Same(source.typeParameters, target.typeParameters))
        {
            target = GetCanonicalSignature(target);
            source = InstantiateSignatureInContextOf(source, target, null, compareTypes);
        }
        int sourceCount = GetParameterCount(source);
        var sourceRestType = GetNonArrayRestType(source);
        var targetRestType = GetNonArrayRestType(target);
        if (sourceRestType != null || targetRestType != null)
        {
            InstantiateType(sourceRestType != null ? sourceRestType : targetRestType, reportUnreliableMarkers);
        }
        var kind = SyntaxKind.Unknown;
        if (target.declaration != null)
        {
            kind = target.declaration.Kind;
        }
        bool strictVariance = (checkMode & SignatureCheckMode.Callback) == 0 && strictFunctionTypes && kind != SyntaxKind.MethodDeclaration && kind != SyntaxKind.MethodSignature && kind != SyntaxKind.Constructor;
        var result = Ternary.True;
        var sourceThisType = GetThisTypeOfSignature(source);
        if (sourceThisType != null && sourceThisType != voidType)
        {
            var targetThisType = GetThisTypeOfSignature(target);
            if (targetThisType != null)
            {
                Ternary related = Ternary.False;
                if (!strictVariance)
                {
                    related = compareTypes(sourceThisType, targetThisType, false);
                }
                if (related == Ternary.False)
                {
                    related = compareTypes(targetThisType, sourceThisType, reportErrors);
                }
                if (related == Ternary.False)
                {
                    if (reportErrors)
                    {
                        errorReporter!(Diag.The_this_types_of_each_signature_are_incompatible);
                    }
                    return Ternary.False;
                }
                result &= related;
            }
        }
        int paramCount;
        if (sourceRestType != null || targetRestType != null)
        {
            paramCount = Math.Min(sourceCount, targetCount);
        }
        else
        {
            paramCount = Math.Max(sourceCount, targetCount);
        }
        int restIndex;
        if (sourceRestType != null || targetRestType != null)
        {
            restIndex = paramCount - 1;
        }
        else
        {
            restIndex = -1;
        }
        for (int i = 0; i < paramCount; i++)
        {
            Type? sourceType;
            if (i == restIndex)
            {
                sourceType = GetRestOrAnyTypeAtPosition(source, i);
            }
            else
            {
                sourceType = TryGetTypeAtPosition(source, i);
            }
            Type? targetType;
            if (i == restIndex)
            {
                targetType = GetRestOrAnyTypeAtPosition(target, i);
            }
            else
            {
                targetType = TryGetTypeAtPosition(target, i);
            }
            if (sourceType != null && targetType != null && (sourceType != targetType || (checkMode & SignatureCheckMode.StrictArity) != 0))
            {
                Signature? sourceSig = null;
                Signature? targetSig = null;
                if ((checkMode & SignatureCheckMode.Callback) == 0 && !IsInstantiatedGenericParameter(source, i))
                {
                    sourceSig = GetSingleCallSignature(GetNonNullableType(sourceType));
                }
                if ((checkMode & SignatureCheckMode.Callback) == 0 && !IsInstantiatedGenericParameter(target, i))
                {
                    targetSig = GetSingleCallSignature(GetNonNullableType(targetType));
                }
                bool callbacks = sourceSig != null && targetSig != null && GetTypePredicateOfSignature(sourceSig) == null && GetTypePredicateOfSignature(targetSig) == null &&
                    GetTypeFacts(sourceType, TypeFacts.IsUndefinedOrNull) == GetTypeFacts(targetType, TypeFacts.IsUndefinedOrNull);
                Ternary related;
                if (callbacks)
                {
                    related = CompareSignaturesRelated(targetSig!, sourceSig!, (checkMode & SignatureCheckMode.StrictArity) | (strictVariance ? SignatureCheckMode.StrictCallback : SignatureCheckMode.BivariantCallback), reportErrors, errorReporter, compareTypes, reportUnreliableMarkers);
                }
                else
                {
                    related = Ternary.False;
                    if ((checkMode & SignatureCheckMode.Callback) == 0 && !strictVariance)
                    {
                        related = compareTypes(sourceType, targetType, false);
                    }
                    if (related == Ternary.False)
                    {
                        related = compareTypes(targetType, sourceType, reportErrors);
                    }
                }
                if (related != Ternary.False && (checkMode & SignatureCheckMode.StrictArity) != 0 && i >= GetMinArgumentCount(source) && i < GetMinArgumentCount(target) && compareTypes(sourceType, targetType, false) != Ternary.False)
                {
                    related = Ternary.False;
                }
                if (related == Ternary.False)
                {
                    if (reportErrors)
                    {
                        errorReporter!(Diag.Types_of_parameters_0_and_1_are_incompatible, GetParameterNameAtPosition(source, i), GetParameterNameAtPosition(target, i));
                    }
                    return Ternary.False;
                }
                result &= related;
            }
        }
        if ((checkMode & SignatureCheckMode.IgnoreReturnTypes) == 0)
        {
            var targetReturnType = GetNonCircularReturnTypeOfSignature(target);
            if (targetReturnType == voidType || targetReturnType == anyType)
            {
                return result;
            }
            var sourceReturnType = GetNonCircularReturnTypeOfSignature(source);
            var targetTypePredicate = GetTypePredicateOfSignature(target);
            if (targetTypePredicate != null)
            {
                var sourceTypePredicate = GetTypePredicateOfSignature(source);
                if (sourceTypePredicate != null)
                {
                    result &= CompareTypePredicateRelatedTo(sourceTypePredicate, targetTypePredicate, reportErrors, errorReporter, compareTypes);
                }
                else if (targetTypePredicate.kind == TypePredicateKind.Identifier || targetTypePredicate.kind == TypePredicateKind.This)
                {
                    if (reportErrors)
                    {
                        errorReporter!(Diag.Signature_0_must_be_a_type_predicate, SignatureToString(source));
                    }
                    return Ternary.False;
                }
            }
            else
            {
                Ternary related = Ternary.False;
                if ((checkMode & SignatureCheckMode.BivariantCallback) != 0)
                {
                    related = compareTypes(targetReturnType, sourceReturnType, false);
                }
                if (related == Ternary.False)
                {
                    related = compareTypes(sourceReturnType, targetReturnType, reportErrors);
                }
                result &= related;
                if (result == Ternary.False && reportErrors)
                {
                    global::Ts.Diagnostics.Message message;
                    if (source.parameters.Len() == 0 && target.parameters.Len() == 0)
                    {
                        message = (source.flags & SignatureFlags.Construct) != 0
                            ? Diag.Construct_signatures_with_no_arguments_have_incompatible_return_types_0_and_1
                            : Diag.Call_signatures_with_no_arguments_have_incompatible_return_types_0_and_1;
                    }
                    else
                    {
                        message = (source.flags & SignatureFlags.Construct) != 0
                            ? Diag.Construct_signature_return_types_0_and_1_are_incompatible
                            : Diag.Call_signature_return_types_0_and_1_are_incompatible;
                    }
                    errorReporter!(message, TypeToString(sourceReturnType), TypeToString(targetReturnType));
                }
            }
        }
        return result;
    }

    // relater.go:1642-1672
    internal Ternary CompareTypePredicateRelatedTo(TypePredicate source, TypePredicate target, bool reportErrors, ErrorReporter? errorReporter, TypeComparer compareTypes)
    {
        if (source.kind != target.kind)
        {
            if (reportErrors)
            {
                errorReporter!(Diag.A_this_based_type_guard_is_not_compatible_with_a_parameter_based_type_guard);
                errorReporter(Diag.Type_predicate_0_is_not_assignable_to_1, TypePredicateToString(source), TypePredicateToString(target));
            }
            return Ternary.False;
        }
        if (source.kind == TypePredicateKind.Identifier || source.kind == TypePredicateKind.AssertsIdentifier)
        {
            if (source.parameterIndex != target.parameterIndex)
            {
                if (reportErrors)
                {
                    errorReporter!(Diag.Parameter_0_is_not_in_the_same_position_as_parameter_1, source.parameterName, target.parameterName);
                    errorReporter(Diag.Type_predicate_0_is_not_assignable_to_1, TypePredicateToString(source), TypePredicateToString(target));
                }
                return Ternary.False;
            }
        }
        Ternary related;
        if (source.t == target.t)
        {
            related = Ternary.True;
        }
        else if (source.t != null && target.t != null)
        {
            related = compareTypes(source.t, target.t, reportErrors);
        }
        else
        {
            related = Ternary.False;
        }
        if (related == Ternary.False && reportErrors)
        {
            errorReporter!(Diag.Type_predicate_0_is_not_assignable_to_1, TypePredicateToString(source), TypePredicateToString(target));
        }
        return related;
    }

    // relater.go:1675-1687
    internal bool IsTopSignature(Signature s)
    {
        if (s.typeParameters.Len() == 0 && (s.thisParameter == null || IsTypeAny(GetTypeOfParameter(s.thisParameter))) && s.parameters.Len() == 1 && SignatureHasRestParameter(s))
        {
            var paramType = GetTypeOfParameter(s.parameters![0]);
            Type restType;
            if (IsArrayType(paramType))
            {
                restType = GetTypeArguments(paramType)[0];
            }
            else
            {
                restType = paramType;
            }
            return (restType.flags & (TypeFlags.Any | TypeFlags.Never)) != 0 && (GetReturnTypeOfSignature(s).flags & TypeFlags.AnyOrUnknown) != 0;
        }
        return false;
    }

    // relater.go:1693-1702
    internal int GetParameterCount(Signature signature)
    {
        int length = signature.parameters.Len();
        if (SignatureHasRestParameter(signature))
        {
            var restType = GetTypeOfSymbol(signature.parameters![length - 1]);
            if (Checker.IsTupleType(restType))
            {
                return length + restType.TargetTupleType().fixedLength - ((restType.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0 ? 0 : 1);
            }
        }
        return length;
    }

    // relater.go:1704-1706
    internal int GetMinArgumentCount(Signature signature) => GetMinArgumentCountEx(signature, MinArgumentCountFlags.None);

    // relater.go:1708-1747
    internal int GetMinArgumentCountEx(Signature signature, MinArgumentCountFlags flags)
    {
        var strongArityForUntypedJS = flags & MinArgumentCountFlags.StrongArityForUntypedJS;
        var voidIsNonOptional = flags & MinArgumentCountFlags.VoidIsNonOptional;
        if (voidIsNonOptional != 0 || signature.resolvedMinArgumentCount == -1)
        {
            int minArgumentCount = -1;
            if (SignatureHasRestParameter(signature))
            {
                var restType = GetTypeOfSymbol(signature.parameters![signature.parameters.Len() - 1]);
                if (Checker.IsTupleType(restType))
                {
                    var infos = restType.TargetTupleType().elementInfos!;
                    int firstOptionalIndex = -1;
                    for (int i = 0; i < infos.Length; i++)
                    {
                        if ((infos[i].flags & ElementFlags.Required) == 0)
                        {
                            firstOptionalIndex = i;
                            break;
                        }
                    }
                    int requiredCount = firstOptionalIndex;
                    if (firstOptionalIndex < 0)
                    {
                        requiredCount = restType.TargetTupleType().fixedLength;
                    }
                    if (requiredCount > 0)
                    {
                        minArgumentCount = signature.parameters.Len() - 1 + requiredCount;
                    }
                }
            }
            if (minArgumentCount == -1)
            {
                if (strongArityForUntypedJS == 0 && (signature.flags & SignatureFlags.IsUntypedSignatureInJSFile) != 0)
                {
                    return 0;
                }
                minArgumentCount = signature.minArgumentCount;
            }
            if (voidIsNonOptional != 0)
            {
                return minArgumentCount;
            }
            for (int i = minArgumentCount - 1; i >= 0; i--)
            {
                var t = GetTypeAtPosition(signature, i);
                if (!Checker.SomeType(t, tt => (tt.flags & TypeFlags.Void) != 0))
                {
                    break;
                }
                minArgumentCount = i;
            }
            signature.resolvedMinArgumentCount = minArgumentCount;
        }
        return signature.resolvedMinArgumentCount;
    }

    // relater.go:1749-1755
    internal bool HasEffectiveRestParameter(Signature signature)
    {
        if (SignatureHasRestParameter(signature))
        {
            var restType = GetTypeOfSymbol(signature.parameters![signature.parameters.Len() - 1]);
            return !Checker.IsTupleType(restType) || (restType.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0;
        }
        return false;
    }

    // relater.go:1757-1763
    internal Type GetTypeAtPosition(Signature signature, int pos) => TryGetTypeAtPosition(signature, pos) ?? anyType!;

    // relater.go:1765-1781
    internal Type? TryGetTypeAtPosition(Signature signature, int pos)
    {
        int paramCount = signature.parameters.Len() - (SignatureHasRestParameter(signature) ? 1 : 0);
        if (pos < paramCount)
        {
            return GetTypeOfParameter(signature.parameters![pos]);
        }
        if (SignatureHasRestParameter(signature))
        {
            var restType = GetTypeOfSymbol(signature.parameters![paramCount]);
            int index = pos - paramCount;
            if (!Checker.IsTupleType(restType) || (restType.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0 || index < restType.TargetTupleType().fixedLength)
            {
                return GetIndexedAccessType(restType, GetNumberLiteralType(new global::Ts.JsNum.Number(index)));
            }
        }
        return null;
    }

    // relater.go:1786-1794
    internal Type? GetRestOrAnyTypeAtPosition(Signature source, int pos)
    {
        var restType = GetRestTypeAtPosition(source, pos, false);
        if (restType != null)
        {
            var elementType = GetElementTypeOfArrayType(restType);
            if (elementType != null && IsTypeAny(elementType))
            {
                return anyType;
            }
        }
        return restType;
    }

    // relater.go:1796-1825
    internal Type GetRestTypeAtPosition(Signature source, int pos, bool readonlyElements)
    {
        int parameterCount = GetParameterCount(source);
        int minArgumentCount = GetMinArgumentCount(source);
        var restType = GetEffectiveRestType(source);
        if (restType != null && pos >= parameterCount - 1)
        {
            if (pos == parameterCount - 1)
            {
                return restType;
            }
            return CreateArrayType(GetIndexedAccessType(restType, numberType!));
        }
        int length = parameterCount - pos;
        if (length <= 0)
        {
            return CreateTupleTypeEx([], [], readonlyElements);
        }
        var types = new Type[length];
        var infos = new TupleElementInfo[length];
        for (int i = 0; i < types.Length; i++)
        {
            ElementFlags eflags;
            if (restType == null || i < types.Length - 1)
            {
                types[i] = GetTypeAtPosition(source, i + pos);
                eflags = i + pos < minArgumentCount ? ElementFlags.Required : ElementFlags.Optional;
            }
            else
            {
                types[i] = restType;
                eflags = ElementFlags.Variadic;
            }
            infos[i] = new TupleElementInfo(eflags, GetNameableDeclarationAtPosition(source, i + pos));
        }
        return CreateTupleTypeEx(types, infos, readonlyElements);
    }

    // relater.go:1827-1852
    internal Node? GetNameableDeclarationAtPosition(Signature signature, int pos)
    {
        int paramCount = signature.parameters.Len() - (SignatureHasRestParameter(signature) ? 1 : 0);
        if (pos < paramCount)
        {
            var decl = signature.parameters![pos].ValueDeclaration;
            if (decl != null && IsValidDeclarationForTupleLabel(decl))
            {
                return decl;
            }
            return null;
        }
        if (SignatureHasRestParameter(signature))
        {
            var restParameter = signature.parameters![paramCount];
            var restType = GetTypeOfSymbol(restParameter);
            if (Checker.IsTupleType(restType))
            {
                var elementInfos = restType.TargetTupleType().elementInfos!;
                int index = pos - paramCount;
                if (index < elementInfos.Length)
                {
                    return elementInfos[index].labeledDeclaration;
                }
                return null;
            }
            if (restParameter.ValueDeclaration != null && IsValidDeclarationForTupleLabel(restParameter.ValueDeclaration))
            {
                return restParameter.ValueDeclaration;
            }
        }
        return null;
    }

    // relater.go:1854-1856
    internal bool IsValidDeclarationForTupleLabel(Node d) =>
        NodeTests.IsNamedTupleMember(d) || NodeTests.IsParameterDeclaration(d) && d.Name() != null && NodeTests.IsIdentifier(d.Name()!);

    // relater.go:1858-1864
    internal Type? GetNonArrayRestType(Signature signature)
    {
        var restType = GetEffectiveRestType(signature);
        if (restType != null && !IsArrayType(restType) && !IsTypeAny(restType))
        {
            return restType;
        }
        return null;
    }

    // relater.go:1866-1880
    internal Type? GetEffectiveRestType(Signature signature)
    {
        if (SignatureHasRestParameter(signature))
        {
            var restType = GetTypeOfSymbol(signature.parameters![signature.parameters.Len() - 1]);
            if (!Checker.IsTupleType(restType))
            {
                if (IsTypeAny(restType))
                {
                    return anyArrayType;
                }
                return restType;
            }
            if ((restType.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0)
            {
                return SliceTupleType(restType, restType.TargetTupleType().fixedLength, 0);
            }
        }
        return null;
    }

    // relater.go:1882-1895
    internal Type SliceTupleType(Type t, int index, int endSkipCount)
    {
        var target = t.TargetTupleType();
        int endIndex = GetTypeReferenceArity(t) - Math.Max(endSkipCount, 0);
        if (index > target.fixedLength)
        {
            var restArrayType = GetRestArrayTypeOfTupleType(t);
            if (restArrayType != null)
            {
                return restArrayType;
            }
            return CreateTupleType([]);
        }
        if (index >= endIndex)
        {
            return CreateTupleType([]);
        }
        return CreateTupleTypeEx(GetTypeArguments(t)[index..endIndex], target.elementInfos![index..endIndex], false);
    }

    // relater.go:1897-1905
    internal Type GetKnownKeysOfTupleType(Type t)
    {
        int fixedLength = t.TargetTupleType().fixedLength;
        var keys = new Type[fixedLength + 1];
        for (int i = 0; i < fixedLength; i++)
        {
            keys[i] = GetStringLiteralType(i.ToString(CultureInfo.InvariantCulture));
        }
        keys[fixedLength] = GetIndexType(t.TargetTupleType().readonlyElements ? globalReadonlyArrayType! : globalArrayType!);
        return GetUnionType(keys);
    }

    // relater.go:1907-1912
    internal Type? GetRestArrayTypeOfTupleType(Type t)
    {
        var restType = GetRestTypeOfTupleType(t);
        if (restType != null)
        {
            return CreateArrayType(restType);
        }
        return null;
    }

    // relater.go:1914-1919
    internal Type? GetThisTypeOfSignature(Signature signature)
    {
        if (signature.thisParameter != null)
        {
            return GetTypeOfSymbol(signature.thisParameter);
        }
        return null;
    }

    // relater.go:1921-1927
    internal bool IsInstantiatedGenericParameter(Signature signature, int pos)
    {
        if (signature.target == null)
        {
            return false;
        }
        var t = TryGetTypeAtPosition(signature.target, pos);
        return t != null && IsGenericType(t);
    }

    // relater.go:1929-1941
    internal string GetParameterNameAtPosition(Signature signature, int pos)
    {
        int paramCount = signature.parameters.Len() - (SignatureHasRestParameter(signature) ? 1 : 0);
        if (pos < paramCount)
        {
            return signature.parameters![pos].Name;
        }
        var restParameter = signature.parameters![paramCount];
        var restType = GetTypeOfSymbol(restParameter);
        if (Checker.IsTupleType(restType))
        {
            int index = pos - paramCount;
            return GetTupleElementLabel(restType.TargetTupleType().elementInfos![index], restParameter, index);
        }
        return restParameter.Name;
    }

    // relater.go:1943-1957
    internal string GetTupleElementLabel(TupleElementInfo elementInfo, Symbol? restSymbol, int index)
    {
        if (elementInfo.labeledDeclaration != null)
        {
            return elementInfo.labeledDeclaration.Name()!.Text();
        }
        if (restSymbol != null && restSymbol.ValueDeclaration != null && NodeTests.IsParameterDeclaration(restSymbol.ValueDeclaration))
        {
            return GetTupleElementLabelFromBindingElement(restSymbol.ValueDeclaration!, index, elementInfo.flags);
        }
        string rootName;
        if (restSymbol != null)
        {
            rootName = restSymbol.Name;
        }
        else
        {
            rootName = "arg";
        }
        return rootName + "_" + index.ToString(CultureInfo.InvariantCulture);
    }

    // relater.go:1959-2014
    internal string GetTupleElementLabelFromBindingElement(Node node, int index, ElementFlags elementFlags)
    {
        if (node.Name() != null)
        {
            switch (node.Name()!.Kind)
            {
                case SyntaxKind.Identifier:
                    var name = node.Name()!.Text();
                    if (HasDotDotDotToken(node))
                    {
                        if ((elementFlags & ElementFlags.Variable) != 0)
                        {
                            return name;
                        }
                        return name + "_" + index.ToString(CultureInfo.InvariantCulture);
                    }
                    if ((elementFlags & ElementFlags.Fixed) != 0)
                    {
                        return name;
                    }
                    return name + "_n";
                case SyntaxKind.ArrayBindingPattern:
                    if (HasDotDotDotToken(node))
                    {
                        var elements = node.Name()!.Elements()!;
                        var lastElement = CoreUtil.LastOrNil(elements);
                        bool lastElementIsBindingElementRest = lastElement != null && NodeTests.IsBindingElement(lastElement) && HasDotDotDotToken(lastElement);
                        int elementCount = elements.Length - (lastElementIsBindingElementRest ? 1 : 0);
                        if (index < elementCount)
                        {
                            var element = elements[index];
                            if (NodeTests.IsBindingElement(element))
                            {
                                return GetTupleElementLabelFromBindingElement(element, index, elementFlags);
                            }
                        }
                        else if (lastElementIsBindingElementRest)
                        {
                            return GetTupleElementLabelFromBindingElement(lastElement!, index - elementCount, elementFlags);
                        }
                    }
                    break;
            }
        }
        return "arg_" + index.ToString(CultureInfo.InvariantCulture);
    }

    // relater.go:2016-2048
    internal TypePredicate? GetTypePredicateOfSignature(Signature sig)
    {
        if (sig.resolvedTypePredicate == null)
        {
            if (sig.target != null)
            {
                var targetTypePredicate = GetTypePredicateOfSignature(sig.target);
                if (targetTypePredicate != null)
                {
                    sig.resolvedTypePredicate = InstantiateTypePredicate(targetTypePredicate, sig.mapper);
                }
            }
            else if (sig.composite != null)
            {
                sig.resolvedTypePredicate = GetUnionOrIntersectionTypePredicate(sig.composite.signatures, sig.composite.isUnion);
            }
            else
            {
                if (sig.declaration != null)
                {
                    var typeNode = sig.declaration.Type();
                    if (typeNode != null)
                    {
                        if (NodeTests.IsTypePredicateNode(typeNode))
                        {
                            sig.resolvedTypePredicate = CreateTypePredicateFromTypePredicateNode(typeNode, sig);
                        }
                    }
                    else if (AstUtilities.IsFunctionLikeDeclaration(sig.declaration) && (sig.resolvedReturnType == null || (sig.resolvedReturnType.flags & TypeFlags.Boolean) != 0) && GetParameterCount(sig) > 0)
                    {
                        sig.resolvedTypePredicate = noTypePredicate;
                        sig.resolvedTypePredicate = GetTypePredicateFromBody(sig.declaration);
                    }
                }
            }
            if (sig.resolvedTypePredicate == null)
            {
                sig.resolvedTypePredicate = noTypePredicate;
            }
        }
        if (sig.resolvedTypePredicate == noTypePredicate)
        {
            return null;
        }
        return sig.resolvedTypePredicate;
    }

    // relater.go:2050-2078
    internal TypePredicate? GetUnionOrIntersectionTypePredicate(Signature[] signatures, bool isUnion)
    {
        TypePredicate? last = null;
        System.Collections.Generic.List<Type> types = [];
        foreach (var sig in signatures)
        {
            var pred = GetTypePredicateOfSignature(sig);
            if (pred != null)
            {
                if (pred.kind != TypePredicateKind.This && pred.kind != TypePredicateKind.Identifier || last != null && !TypePredicateKindsMatch(last, pred))
                {
                    return null;
                }
                last = pred;
                types.Add(pred.t!);
            }
            else
            {
                Type? returnType = null;
                if (isUnion)
                {
                    returnType = GetReturnTypeOfSignature(sig);
                }
                if (returnType != falseType && returnType != regularFalseType)
                {
                    return null;
                }
            }
        }
        if (last == null)
        {
            return null;
        }
        var compositeType = GetUnionOrIntersectionType(types.ToArray(), isUnion, UnionReduction.Literal);
        return NewTypePredicate(last.kind, last.parameterName, last.parameterIndex, compositeType);
    }

    // relater.go:2080-2082
    internal bool TypePredicateKindsMatch(TypePredicate a, TypePredicate b) => a.kind == b.kind && a.parameterIndex == b.parameterIndex;

    // relater.go:2084-2098
    internal TypePredicate CreateTypePredicateFromTypePredicateNode(Node node, Signature signature)
    {
        var predicateNode = node.AsTypePredicateNode();
        Type? t = null;
        if (predicateNode.Type != null)
        {
            t = GetTypeFromTypeNode(predicateNode.Type);
        }
        if (NodeTests.IsThisTypeNode(predicateNode.ParameterName!))
        {
            var kind0 = predicateNode.AssertsModifier != null ? TypePredicateKind.AssertsThis : TypePredicateKind.This;
            return NewTypePredicate(kind0, "", 0, t);
        }
        var kind = predicateNode.AssertsModifier != null ? TypePredicateKind.AssertsIdentifier : TypePredicateKind.Identifier;
        var name = predicateNode.ParameterName!.Text();
        int index = CoreUtil.FindIndex(signature.parameters, p => p.Name == name);
        return NewTypePredicate(kind, name, index, t);
    }

    // relater.go:2100-2106
    internal TypePredicate InstantiateTypePredicate(TypePredicate predicate, TypeMapper? mapper)
    {
        var t = InstantiateType(predicate.t, mapper);
        if (t == predicate.t)
        {
            return predicate;
        }
        return NewTypePredicate(predicate.kind, predicate.parameterName, predicate.parameterIndex, t);
    }

    // relater.go:2108-2110
    internal TypePredicate NewTypePredicate(TypePredicateKind kind, string parameterName, int parameterIndex, Type? t) =>
        new() { kind = kind, parameterIndex = parameterIndex, parameterName = parameterName, t = t };

    // relater.go:2112-2117
    internal bool IsResolvingReturnTypeOfSignature(Signature signature)
    {
        if (signature.composite != null && CoreUtil.Some(signature.composite.signatures, IsResolvingReturnTypeOfSignature))
        {
            return true;
        }
        return signature.resolvedReturnType == null && FindResolutionCycleStartIndex(signature, TypeSystemPropertyName.ResolvedReturnType) >= 0;
    }

    // relater.go:2119-2152
    internal Signature[]? FindMatchingSignatures(Signature[][] signatureLists, Signature signature, int listIndex)
    {
        if (signature.typeParameters.Len() != 0)
        {
            if (listIndex > 0)
            {
                return null;
            }
            for (int i = 1; i < signatureLists.Length; i++)
            {
                if (FindMatchingSignature(signatureLists[i], signature, false, false, false) == null)
                {
                    return null;
                }
            }
            return [signature];
        }
        Signature[]? result = null;
        for (int i = 0; i < signatureLists.Length; i++)
        {
            Signature? match;
            if (i == listIndex)
            {
                match = signature;
            }
            else
            {
                match = FindMatchingSignature(signatureLists[i], signature, false, false, true);
                if (match == null)
                {
                    match = FindMatchingSignature(signatureLists[i], signature, true, false, true);
                }
            }
            if (match == null)
            {
                return null;
            }
            result = CoreUtil.AppendIfUnique(result ?? [], match);
        }
        return result;
    }

    // relater.go:2154-2162
    internal Signature? FindMatchingSignature(Signature[] signatureList, Signature signature, bool partialMatch, bool ignoreThisTypes, bool ignoreReturnTypes)
    {
        Func<Type, Type, Ternary> compareTypes = partialMatch ? CompareTypesSubtypeOf : CompareTypesIdentical;
        foreach (var s in signatureList)
        {
            if (CompareSignaturesIdentical(s, signature, partialMatch, ignoreThisTypes, ignoreReturnTypes, compareTypes) != 0)
            {
                return s;
            }
        }
        return null;
    }

    // relater.go:2167-2225
    internal Ternary CompareSignaturesIdentical(Signature source, Signature target, bool partialMatch, bool ignoreThisTypes, bool ignoreReturnTypes, Func<Type, Type, Ternary> compareTypes)
    {
        if (source == target)
        {
            return Ternary.True;
        }
        if (!IsMatchingSignature(source, target, partialMatch))
        {
            return Ternary.False;
        }
        if (source.typeParameters.Len() != target.typeParameters.Len())
        {
            return Ternary.False;
        }
        if (target.typeParameters.Len() != 0)
        {
            var mapper = TypeMapper.NewTypeMapper(source.typeParameters!, target.typeParameters!);
            for (int i = 0; i < target.typeParameters!.Length; i++)
            {
                var s = source.typeParameters![i];
                var t = target.typeParameters[i];
                if (!(s == t || compareTypes(InstantiateType(GetConstraintOrUnknownFromTypeParameter(s), mapper), GetConstraintOrUnknownFromTypeParameter(t)) != Ternary.False &&
                    compareTypes(InstantiateType(GetDefaultOrUnknownFromTypeParameter(s), mapper), GetDefaultOrUnknownFromTypeParameter(t)) != Ternary.False))
                {
                    return Ternary.False;
                }
            }
            source = InstantiateSignatureEx(source, mapper, true);
        }
        var result = Ternary.True;
        if (!ignoreThisTypes)
        {
            var sourceThisType = GetThisTypeOfSignature(source);
            if (sourceThisType != null)
            {
                var targetThisType = GetThisTypeOfSignature(target);
                if (targetThisType != null)
                {
                    var related = compareTypes(sourceThisType, targetThisType);
                    if (related == Ternary.False)
                    {
                        return Ternary.False;
                    }
                    result &= related;
                }
            }
        }
        int tpc = GetParameterCount(target);
        for (int i = 0; i < tpc; i++)
        {
            var s = GetTypeAtPosition(source, i);
            var t = GetTypeAtPosition(target, i);
            var related = compareTypes(t, s);
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        if (!ignoreReturnTypes)
        {
            var sourceTypePredicate = GetTypePredicateOfSignature(source);
            var targetTypePredicate = GetTypePredicateOfSignature(target);
            if (sourceTypePredicate != null || targetTypePredicate != null)
            {
                result &= CompareTypePredicatesIdentical(sourceTypePredicate, targetTypePredicate, compareTypes);
            }
            else
            {
                result &= compareTypes(GetReturnTypeOfSignature(source), GetReturnTypeOfSignature(target));
            }
        }
        return result;
    }

    // relater.go:2227-2245
    internal bool IsMatchingSignature(Signature source, Signature target, bool partialMatch)
    {
        int sourceParameterCount = GetParameterCount(source);
        int targetParameterCount = GetParameterCount(target);
        int sourceMinArgumentCount = GetMinArgumentCount(source);
        int targetMinArgumentCount = GetMinArgumentCount(target);
        bool sourceHasRestParameter = HasEffectiveRestParameter(source);
        bool targetHasRestParameter = HasEffectiveRestParameter(target);
        if (sourceParameterCount == targetParameterCount && sourceMinArgumentCount == targetMinArgumentCount && sourceHasRestParameter == targetHasRestParameter)
        {
            return true;
        }
        if (partialMatch && sourceMinArgumentCount <= targetMinArgumentCount)
        {
            return true;
        }
        return false;
    }

    // relater.go:2247-2268
    internal bool CompareTypeParametersIdentical(Type[]? sourceParams, Type[]? targetParams)
    {
        if (sourceParams.Len() != targetParams.Len())
        {
            return false;
        }
        var mapper = TypeMapper.NewTypeMapper(targetParams!, sourceParams!);
        for (int i = 0; i < sourceParams.Len(); i++)
        {
            var source = sourceParams![i];
            var target = targetParams![i];
            if (source == target)
            {
                continue;
            }
            if (!IsTypeIdenticalTo(GetConstraintFromTypeParameter(source) ?? unknownType!, InstantiateType(GetConstraintFromTypeParameter(target) ?? unknownType!, mapper)))
            {
                return false;
            }
        }
        return true;
    }

    // relater.go:2270-2280
    internal Ternary CompareTypePredicatesIdentical(TypePredicate? source, TypePredicate? target, Func<Type, Type, Ternary> compareTypes)
    {
        if (source == null || target == null || !TypePredicateKindsMatch(source, target))
        {
            return Ternary.False;
        }
        if (source.t == target.t)
        {
            return Ternary.True;
        }
        if (source.t != null && target.t != null)
        {
            return compareTypes(source.t, target.t);
        }
        return Ternary.False;
    }

    // relater.go:2282-2319
    internal Type? GetEffectiveConstraintOfIntersection(Type[] types, bool targetIsUnion)
    {
        System.Collections.Generic.List<Type>? constraints = null;
        bool hasDisjointDomainType = false;
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Instantiable) != 0)
            {
                var constraint = GetConstraintOfType(t);
                while (constraint != null && (constraint.flags & (TypeFlags.TypeParameter | TypeFlags.Index | TypeFlags.Conditional)) != 0)
                {
                    constraint = GetConstraintOfType(constraint);
                }
                if (constraint != null)
                {
                    (constraints ??= []).Add(constraint);
                    if (targetIsUnion)
                    {
                        constraints.Add(t);
                    }
                }
            }
            else if ((t.flags & TypeFlags.DisjointDomains) != 0 || IsEmptyAnonymousObjectType(t))
            {
                hasDisjointDomainType = true;
            }
        }
        if (constraints != null && (targetIsUnion || hasDisjointDomainType))
        {
            if (hasDisjointDomainType)
            {
                foreach (var t in types)
                {
                    if ((t.flags & TypeFlags.DisjointDomains) != 0 || IsEmptyAnonymousObjectType(t))
                    {
                        constraints.Add(t);
                    }
                }
            }
            return GetNormalizedType(GetIntersectionTypeEx(constraints.ToArray(), IntersectionFlags.NoConstraintReduction, null), false);
        }
        return null;
    }

    // relater.go:2321-2330
    internal bool TemplateLiteralTypesDefinitelyUnrelated(TemplateLiteralType source, TemplateLiteralType target)
    {
        var sourceStart = source.texts[0];
        var targetStart = target.texts[0];
        var sourceEnd = source.texts[^1];
        var targetEnd = target.texts[^1];
        int startLen = Math.Min(sourceStart.Length, targetStart.Length);
        int endLen = Math.Min(sourceEnd.Length, targetEnd.Length);
        return !string.Equals(sourceStart[..startLen], targetStart[..startLen], StringComparison.Ordinal) || !string.Equals(sourceEnd[(sourceEnd.Length - endLen)..], targetEnd[(targetEnd.Length - endLen)..], StringComparison.Ordinal);
    }

    // relater.go:2332-2343
    internal bool IsTypeMatchedByTemplateLiteralType(Type source, TemplateLiteralType target, TypeComparer compareTypes)
    {
        var inferences = InferTypesFromTemplateLiteralType(source, target);
        if (inferences != null)
        {
            for (int i = 0; i < inferences.Length; i++)
            {
                if (!IsValidTypeForTemplateLiteralPlaceholder(inferences[i], target.types[i], compareTypes))
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    // relater.go:2345-2362
    internal Type[]? InferTypesFromTemplateLiteralType(Type source, TemplateLiteralType target)
    {
        if ((source.flags & TypeFlags.StringLiteral) != 0)
        {
            return InferFromLiteralPartsToTemplateLiteral([GetStringLiteralValue(source)], null, target);
        }
        if ((source.flags & TypeFlags.TemplateLiteral) != 0)
        {
            if (StringSlicesEqual(source.AsTemplateLiteralType().texts, target.texts))
            {
                return CoreUtil.MapIndex(source.AsTemplateLiteralType().types, (s, i) =>
                {
                    if (IsTypeAssignableTo(GetBaseConstraintOrType(s), GetBaseConstraintOrType(target.types[i])))
                    {
                        return s;
                    }
                    return GetStringLikeTypeForType(s);
                });
            }
            return InferFromLiteralPartsToTemplateLiteral(source.AsTemplateLiteralType().texts, source.AsTemplateLiteralType().types, target);
        }
        return null;
    }

    // relater.go:2385-2467
    internal Type[]? InferFromLiteralPartsToTemplateLiteral(string[] sourceTexts, Type[]? sourceTypes, TemplateLiteralType target)
    {
        int lastSourceIndex = sourceTexts.Length - 1;
        var sourceStartText = sourceTexts[0];
        var sourceEndText = sourceTexts[lastSourceIndex];
        var targetTexts = target.texts;
        int lastTargetIndex = targetTexts.Length - 1;
        var targetStartText = targetTexts[0];
        var targetEndText = targetTexts[lastTargetIndex];
        if (lastSourceIndex == 0 && sourceStartText.Length < targetStartText.Length + targetEndText.Length || !sourceStartText.StartsWith(targetStartText, StringComparison.Ordinal) || !sourceEndText.EndsWith(targetEndText, StringComparison.Ordinal))
        {
            return null;
        }
        var remainingEndText = sourceEndText[..(sourceEndText.Length - targetEndText.Length)];
        int seg = 0;
        int pos = targetStartText.Length;
        System.Collections.Generic.List<Type> matches = [];
        string GetSourceText(int index)
        {
            if (index < lastSourceIndex)
            {
                return sourceTexts[index];
            }
            return remainingEndText;
        }
        void AddMatch(int s, int p)
        {
            Type matchType;
            if (s == seg)
            {
                matchType = GetStringLiteralType(global::Ts.StringUtil.StringUtil.CombineSurrogatePairs(GetSourceText(s)[pos..p]));
            }
            else
            {
                var matchTexts = new string[s - seg + 1];
                matchTexts[0] = sourceTexts[seg][pos..];
                for (int k = seg + 1; k < s; k++)
                {
                    matchTexts[k - seg] = sourceTexts[k];
                }
                matchTexts[s - seg] = GetSourceText(s)[..p];
                matchType = GetTemplateLiteralType(matchTexts, sourceTypes![seg..s]);
            }
            matches.Add(matchType);
            seg = s;
            pos = p;
        }
        for (int i = 1; i < lastTargetIndex; i++)
        {
            var delim = targetTexts[i];
            if (delim.Length > 0)
            {
                int s = seg;
                int p = pos;
                while (true)
                {
                    int d = GetSourceText(s).AsSpan(p).IndexOf(delim, StringComparison.Ordinal);
                    if (d >= 0)
                    {
                        p += d;
                        break;
                    }
                    s++;
                    if (s == sourceTexts.Length)
                    {
                        return null;
                    }
                    p = 0;
                }
                AddMatch(s, p);
                pos += delim.Length;
            }
            else if (GetSourceText(seg) is var sourceText && pos < sourceText.Length)
            {
                var (_, size) = global::Ts.StringUtil.StringUtil.DecodeJSStringRune(sourceText, pos);
                AddMatch(seg, pos + size);
            }
            else if (seg < lastSourceIndex)
            {
                AddMatch(seg + 1, 0);
            }
            else
            {
                return null;
            }
        }
        AddMatch(lastSourceIndex, GetSourceText(lastSourceIndex).Length);
        return matches.ToArray();
    }

    // relater.go:2469-2474
    internal Type GetStringLikeTypeForType(Type t)
    {
        if ((t.flags & (TypeFlags.Any | TypeFlags.StringLike)) != 0)
        {
            return t;
        }
        return GetTemplateLiteralType(["", ""], [t]);
    }

    // relater.go:2476-2496
    internal bool IsValidTypeForTemplateLiteralPlaceholder(Type source, Type target, TypeComparer compareTypes)
    {
        if ((target.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Every(target.Types(), t => t == emptyTypeLiteralType || IsValidTypeForTemplateLiteralPlaceholder(source, t, compareTypes));
        }
        if ((target.flags & TypeFlags.String) != 0 || compareTypes(source, target, false) != Ternary.False)
        {
            return true;
        }
        if ((source.flags & TypeFlags.StringLiteral) != 0)
        {
            var value = GetStringLiteralValue(source);
            return (target.flags & TypeFlags.Number) != 0 && IsValidNumberString(value, false) ||
                (target.flags & TypeFlags.BigInt) != 0 && IsValidBigIntString(value, false) ||
                (target.flags & (TypeFlags.BooleanLiteral | TypeFlags.Nullable)) != 0 && value == target.AsIntrinsicType().intrinsicName ||
                (target.flags & TypeFlags.StringMapping) != 0 && IsMemberOfStringMapping(source, target) ||
                (target.flags & TypeFlags.TemplateLiteral) != 0 && IsTypeMatchedByTemplateLiteralType(source, target.AsTemplateLiteralType(), compareTypes);
        }
        if ((source.flags & TypeFlags.TemplateLiteral) != 0)
        {
            var texts = source.AsTemplateLiteralType().texts;
            return texts.Length == 2 && texts[0] == "" && texts[1] == "" && compareTypes(source.AsTemplateLiteralType().types[0], target, false) != Ternary.False;
        }
        return false;
    }

    // relater.go:2498-2516
    internal bool IsMemberOfStringMapping(Type source, Type target)
    {
        if ((target.flags & TypeFlags.Any) != 0)
        {
            return true;
        }
        if ((target.flags & (TypeFlags.String | TypeFlags.TemplateLiteral)) != 0)
        {
            return IsTypeAssignableTo(source, target);
        }
        if ((target.flags & TypeFlags.StringMapping) != 0)
        {
            var (mapped, inner) = ApplyTargetStringMappingToSource(source, target);
            return mapped == source && IsMemberOfStringMapping(source, inner);
        }
        return false;
    }

    // relater.go:2518-2524
    internal (Type, Type) ApplyTargetStringMappingToSource(Type source, Type target)
    {
        var inner = target.AsStringMappingType().target;
        if ((inner.flags & TypeFlags.StringMapping) != 0)
        {
            (source, inner) = ApplyTargetStringMappingToSource(source, inner);
        }
        return (GetStringMappingType(target.symbol!, source), inner);
    }

    private static bool StringSlicesEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
