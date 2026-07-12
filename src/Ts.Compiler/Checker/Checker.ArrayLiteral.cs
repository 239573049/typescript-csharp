// Ported from tsgo internal/checker/checker.go — array-literal checking (checkArrayLiteral at
// checker.go:7998, createArrayLiteralType at 8080, isSpreadIntoCallOrNew at 8094).
//
// §1.5: cross-package via global::; host classes (AstUtilities/NodeTests) direct. Nullability follows Go.
// Deep leaves still stubbed and reached only from the tuple/inferential path: getInferenceContext.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:7998-8078
    internal Type CheckArrayLiteral(Node node, CheckMode checkMode)
    {
        var elements = node.Elements()!;
        var elementTypes = new Type[elements.Length];
        var elementInfos = new TupleElementInfo[elements.Length];
        PushCachedContextualType(node);
        bool inDestructuringPattern = AstUtilities.IsAssignmentTarget(node);
        bool inConstContext = IsConstContext(node);
        var contextualType = GetApparentTypeOfContextualType(node, ContextFlags.None);
        bool inTupleContext = IsSpreadIntoCallOrNew(node) || contextualType != null && SomeType(contextualType, t =>
            IsTupleLikeType(t) || IsGenericMappedType(t) && t.AsMappedType()!.nameType == null && GetHomomorphicTypeVariable(CoreUtil.OrElse(t.AsMappedType()!.target, t)!) != null);
        bool hasOmittedExpression = false;
        for (int i = 0; i < elements.Length; i++)
        {
            var e = elements[i];
            if (NodeTests.IsSpreadElement(e))
            {
                var spreadType = CheckExpressionEx(e.Expression()!, checkMode);
                if (IsArrayLikeType(spreadType))
                {
                    elementTypes[i] = spreadType;
                    elementInfos[i] = new TupleElementInfo(ElementFlags.Variadic, null);
                }
                else if (inDestructuringPattern)
                {
                    // Given the following situation:
                    //    var c: {};
                    //    [...c] = ["", 0];
                    //
                    // c is represented in the tree as a spread element in an array literal.
                    // But c really functions as a rest element, and its purpose is to provide
                    // a contextual type for the right hand side of the assignment. Therefore,
                    // instead of calling checkExpression on "...c", which will give an error
                    // if c is not iterable/array-like, we need to act as if we are trying to
                    // get the contextual element type from it. So we do something similar to
                    // getContextualTypeForElementExpression, which will crucially not error
                    // if there is no index type / iterated type.
                    var restElementType = GetIndexTypeOfType(spreadType, numberType!);
                    if (restElementType == null)
                    {
                        restElementType = GetIteratedTypeOrElementType(IterationUse.Destructuring, spreadType, undefinedType!, null /*errorNode*/, false /*checkAssignability*/);
                        if (restElementType == null)
                        {
                            restElementType = unknownType!;
                        }
                    }
                    elementTypes[i] = restElementType;
                    elementInfos[i] = new TupleElementInfo(ElementFlags.Rest, null);
                }
                else
                {
                    elementTypes[i] = CheckIteratedTypeOrElementType(IterationUse.Spread, spreadType, undefinedType!, e.Expression());
                    elementInfos[i] = new TupleElementInfo(ElementFlags.Rest, null);
                }
            }
            else if (exactOptionalPropertyTypes && NodeTests.IsOmittedExpression(e))
            {
                hasOmittedExpression = true;
                elementTypes[i] = undefinedOrMissingType!;
                elementInfos[i] = new TupleElementInfo(ElementFlags.Optional, null);
            }
            else
            {
                var t = CheckExpressionForMutableLocation(e, checkMode);
                elementTypes[i] = AddOptionalityEx(t, true /*isProperty*/, hasOmittedExpression);
                elementInfos[i] = new TupleElementInfo(hasOmittedExpression ? ElementFlags.Optional : ElementFlags.Required, null);
                if (inTupleContext && (checkMode & CheckMode.Inferential) != 0 && (checkMode & CheckMode.SkipContextSensitive) == 0 && IsContextSensitive(e))
                {
                    var inferenceContext = GetInferenceContext(node);
                    // In CheckMode.Inferential we should always have an inference context
                    AddIntraExpressionInferenceSite(inferenceContext!, e, t);
                }
            }
        }
        PopContextualType();
        if (inDestructuringPattern)
        {
            return CreateTupleTypeEx(elementTypes, elementInfos, false);
        }
        if ((checkMode & CheckMode.ForceTuple) != 0 || inConstContext || inTupleContext)
        {
            return CreateArrayLiteralType(CreateTupleTypeEx(elementTypes, elementInfos, inConstContext && !(contextualType != null && SomeType(contextualType, IsMutableArrayLikeType)) /*readonly*/));
        }
        Type elementType;
        if (elementTypes.Length != 0)
        {
            for (int i = 0; i < elementTypes.Length; i++)
            {
                if ((elementInfos[i].flags & ElementFlags.Variadic) != 0)
                {
                    elementTypes[i] = GetIndexedAccessTypeOrUndefined(elementTypes[i], numberType!, AccessFlags.None, null, null) ?? anyType!;
                }
            }
            elementType = GetUnionTypeEx(elementTypes, UnionReduction.Subtype, null, null);
        }
        else
        {
            elementType = strictNullChecks ? implicitNeverType! : undefinedWideningType!;
        }
        return CreateArrayLiteralType(CreateArrayTypeEx(elementType, inConstContext));
    }

    // checker.go:8080-8092
    internal Type CreateArrayLiteralType(Type t)
    {
        if ((t.objectFlags & ObjectFlags.Reference) == 0)
        {
            return t;
        }
        var key = new CachedTypeKey(CachedTypeKind.ArrayLiteralType, t.id);
        if (cachedTypes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var literalType = CloneTypeReference(t);
        literalType.objectFlags |= ObjectFlags.ArrayLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
        cachedTypes[key] = literalType;
        return literalType;
    }

    // checker.go:8094-8097
    internal static bool IsSpreadIntoCallOrNew(Node node)
    {
        var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent)!;
        return NodeTests.IsSpreadElement(parent) && AstUtilities.IsCallOrNewExpression(parent.Parent!);
    }
}
