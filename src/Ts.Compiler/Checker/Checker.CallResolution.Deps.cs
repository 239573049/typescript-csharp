// Single dedup surface for the call-resolution + contextual-type wave (Checker.CallResolution*.cs /
// Checker.ContextualType.cs). Contents fall into two groups:
//
//   (a) Small helpers this wave OWNS and ports faithfully here (they are leaves the trunk needs and no
//       other wave owns): getContextualTypeForElementExpression, IndexOfNode (ast free func),
//       callLikeExpressionMayHaveTypeArguments.
//
//   (b) Deeper cross-cluster leaves the call-resolution/contextual trunk CALLS but does NOT own — the
//       owning wave will DELETE the corresponding stub when it lands. Each throws NotImplementedException
//       so the call sites compile. Stubs already declared by other dedup surfaces are NOT redeclared
//       here (checkExpressionWithContextualType, getInferenceContext, getRegularTypeOfObjectLiteral,
//       getAwaitedTypeOfPromise, isContextSensitive, mapType/mapTypeEx, getTypeOfExpression,
//       removeMissingType, getContextuallyTypedParameterType, isTypeUsableAsPropertyName,
//       getPropertyNameFromType, getWidenedLiteralType, isConstTypeVariable, getSelectedModifierFlags,
//       instantiateContextualType, checkDeprecatedSignature, getGlobalESSymbolConstructorSymbolOrNil,
//       getOptionalExpressionType, checkSuperExpression, checkNonNullExpression, resolveJsxOpeningLike-
//       Element, getEffectiveFirstArgumentForJsxSignature, inferJsxTypeArguments, ...). **Do NOT
//       implement whole missing subsystems here.**

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ===== (a) owned leaves =====

    // checker.go:29729
    private Type? GetContextualTypeForElementExpression(Type? t, int index, int length, int firstSpreadIndex, int lastSpreadIndex)
    {
        if (t == null)
        {
            return null;
        }
        return MapTypeEx(t, tt =>
        {
            if (IsTupleType(tt))
            {
                var target = tt.TargetTupleType()!;
                // If index is before any spread element and within the fixed part of the contextual tuple
                // type, return the type of the contextual tuple element.
                if ((firstSpreadIndex < 0 || index < firstSpreadIndex) && index < target.fixedLength)
                {
                    return RemoveMissingType(GetTypeArguments(tt)[index], (target.elementInfos![index].flags & ElementFlags.Optional) != 0);
                }
                // When the length is known and the index is after all spread elements we compute the offset
                // from the element to the end and the number of ending fixed elements.
                var offset = 0;
                if (length >= 0 && (lastSpreadIndex < 0 || index > lastSpreadIndex))
                {
                    offset = length - index;
                }
                var fixedEndLength = 0;
                if (offset > 0 && (target.combinedFlags & ElementFlags.Variable) != 0)
                {
                    fixedEndLength = GetEndElementCount(target, ElementFlags.Fixed);
                }
                // If the offset is within the ending fixed part, return the type of the contextual element.
                if (offset > 0 && offset <= fixedEndLength)
                {
                    return GetTypeArguments(tt)[GetTypeReferenceArity(tt) - offset];
                }
                // Return a union of the possible contextual element types with no subtype reduction.
                var tupleIndex = target.fixedLength;
                if (firstSpreadIndex >= 0)
                {
                    tupleIndex = global::System.Math.Min(tupleIndex, firstSpreadIndex);
                }
                var endSkipCount = fixedEndLength;
                if (length >= 0 && lastSpreadIndex >= 0)
                {
                    endSkipCount = global::System.Math.Min(fixedEndLength, length - lastSpreadIndex);
                }
                return GetElementTypeOfSliceOfTupleType(tt, tupleIndex, endSkipCount, false /*writing*/, true /*noReductions*/)!;
            }
            // If element index is known and a contextual property with that name exists, return it.
            if (firstSpreadIndex < 0 || index < firstSpreadIndex)
            {
                var propType = GetTypeOfPropertyOfContextualType(tt, index.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
                if (propType != null)
                {
                    return propType;
                }
            }
            return GetIteratedTypeOrElementType(IterationUse.Element, tt, undefinedType!, null /*errorNode*/, false /*checkAssignability*/)!;
        }, true /*noReductions*/);
    }

    // ast/utilities.go:3722 — index of a node within a node list by identity (positions are unique).
    // PORT-NOTE: Go uses BinarySearchFunc on CompareNodePositions; a linear identity scan is behaviorally
    // equivalent for the small element lists we consult and avoids adding the comparator surface here.
    private static int IndexOfNode(Node[] nodes, Node node) => global::System.Array.IndexOf(nodes, node);

    // utilities.go:1066
    private bool CallLikeExpressionMayHaveTypeArguments(Node node) =>
        NodeTests.IsCallExpression(node) || NodeTests.IsNewExpression(node) || NodeTests.IsTaggedTemplateExpression(node) || IsJsxOpeningLikeElement(node);

    // ast/utilities.go:2941
    private static bool IsJsxCallLike(Node node) => node.Kind switch
    {
        SyntaxKind.JsxOpeningElement or SyntaxKind.JsxSelfClosingElement or SyntaxKind.JsxOpeningFragment => true,
        _ => false,
    };

    // ast/utilities.go:4528
    private static bool IsSuperProperty(Node node) =>
        (NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsElementAccessExpression(node)) && node.Expression()!.Kind == SyntaxKind.SuperKeyword;

    // checker.go:28832 — removeOptionalTypeMarker.
    private Type RemoveOptionalTypeMarker(Type t)
    {
        if (strictNullChecks)
        {
            return RemoveType(t, optionalType!);
        }
        return t;
    }

    // checker/utilities.go:1245
    private static int GetNonRestParameterCount(Signature sig) => sig.parameters.Len() - (SignatureHasRestParameter(sig) ? 1 : 0);

    // checker/utilities.go:1249
    private static (int, int) MinAndMax<T>(T[] slice, Func<T, int> getValue)
    {
        var minValue = 0;
        var maxValue = 0;
        for (var i = 0; i < slice.Length; i++)
        {
            var value = getValue(slice[i]);
            if (i == 0)
            {
                minValue = value;
                maxValue = value;
            }
            else
            {
                minValue = global::System.Math.Min(minValue, value);
                maxValue = global::System.Math.Max(maxValue, value);
            }
        }
        return (minValue, maxValue);
    }

    // ===== (b) deferred leaves called by the trunk (owned elsewhere / not yet landed) =====

    // IsConstructorAccessible + TypeHasProtectedAccessibleBase are now REAL in Checker.CallResolution.cs
    // (checker.go:8624/8657); FindMixins is REAL in Checker.Instantiation.cs (checker.go:21270).

    // checker.go:8306 — deprecated-signature suggestion (deprecation-diagnostics cluster). (Real stub in
    // Checker.Jsx.Deps.cs — not redeclared.)

    // isCommonJSRequire (8320): stub already declared in Checker.SymbolResolution.cs — not redeclared.

    // checkNonNullTypeWithReporter 7390: real body landed in Checker.PropertyAccess.cs (non-null cluster).

    // checker.go:8438/8442 — remaining resolveSignature dispatch targets (decorator / instanceof waves).
    // resolveJsxOpeningLikeElement is REAL (jsx wave); resolveTaggedTemplateExpression is REAL (Checker.CallResolution.cs).
    private Signature ResolveDecorator(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode) => throw new NotImplementedException("resolveDecorator");
    // checker.go:8776
    private Signature ResolveInstanceofExpression(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        // if rightType is an object type with a custom `[Symbol.hasInstance]` method, then it is potentially
        // valid on the right-hand side of the `instanceof` operator (Step 2 of the ECMAScript algorithm).
        var right = node.AsBinaryExpression().Right!;
        var rightType = CheckExpression(right);
        if (!IsTypeAny(rightType))
        {
            var hasInstanceMethodType = GetSymbolHasInstanceMethodOfObjectType(rightType);
            if (hasInstanceMethodType != null)
            {
                var apparentType = GetApparentType(hasInstanceMethodType);
                if (IsErrorType(apparentType))
                {
                    return ResolveErrorCall(node);
                }
                var callSignatures = GetSignaturesOfType(apparentType, SignatureKind.Call);
                var constructSignatures = GetSignaturesOfType(apparentType, SignatureKind.Construct);
                if (IsUntypedFunctionCall(hasInstanceMethodType, apparentType, callSignatures.Length, constructSignatures.Length))
                {
                    return ResolveUntypedCall(node);
                }
                if (callSignatures.Length != 0)
                {
                    return ResolveCall(node, callSignatures, candidatesOutArray, checkMode, SignatureFlags.None, null);
                }
            }
            else if (!(TypeHasCallOrConstructSignatures(rightType) || IsTypeSubtypeOf(rightType, globalFunctionType!)))
            {
                Error(right, global::Ts.Diagnostics.Diagnostics.The_right_hand_side_of_an_instanceof_expression_must_be_either_of_type_any_a_class_function_or_other_type_assignable_to_the_Function_interface_type_or_an_object_type_with_a_Symbol_hasInstance_method);
                return ResolveErrorCall(node);
            }
        }
        // fall back to a default signature
        return anySignature!;
    }

    // checker.go:9108/29901 — decorator argument shape (legacy/ES decorator cluster).
    private int GetDecoratorArgumentCount(Node node, Signature signature) => throw new NotImplementedException("getDecoratorArgumentCount");
    private Node[] GetEffectiveDecoratorArguments(Node node) => throw new NotImplementedException("getEffectiveDecoratorArguments");

    // checker.go:10289 — getIntersectedSignatures: real body landed in Checker.ContextualType.cs
    // (combine helpers in Checker.Instantiation.cs, checker.go:21088-21207).

    // checker.go:31010 — getAwaitedType: real body landed in Checker.AwaitedTypes.cs (#22).

    // ---- global-type resolvers reached from call/contextual paths (global-type bootstrap cluster) ----
    // wired field resolver (Construction.cs); the method just unmasks it.
    private Type GetGlobalImportCallOptionsType() => getGlobalImportCallOptionsType();
    private Type GetGlobalImportAttributesType() => getGlobalImportAttributesType();
    // getGlobalTemplateStringsArrayType: resolved via the getGlobalTemplateStringsArrayType field (Checker.Construction.cs).
    private Symbol? GetGlobalPromiseConstructorSymbolOrNil() => getGlobalPromiseConstructorSymbolOrNil();

    // ---- deferred contextual branches (generator / decorator / JS-assignment-declaration surfaces) ----
    // checker.go:29476/29507 — yield/await contextual operands (generator-iteration cluster).
    private Type? GetContextualTypeForYieldOperand(Node node, ContextFlags contextFlags) => throw new NotImplementedException("getContextualTypeForYieldOperand");
    private Type? GetContextualTypeForAwaitOperand(Node node, ContextFlags contextFlags) => throw new NotImplementedException("getContextualTypeForAwaitOperand");
    // checker.go:29558 — decorator contextual type (decorator cluster).
    private Type? GetContextualTypeForDecorator(Node decorator) => throw new NotImplementedException("getContextualTypeForDecorator");
    // checker.go:29340/29369 — binding-element / static-property contextual type (binding/JS clusters).
    private Type? GetContextualTypeForBindingElement(Node declaration, ContextFlags contextFlags) => throw new NotImplementedException("getContextualTypeForBindingElement");
    private Type? GetContextualTypeForStaticPropertyDeclaration(Node declaration, ContextFlags contextFlags) => throw new NotImplementedException("getContextualTypeForStaticPropertyDeclaration");
    // checker.go:29600-29675 — assignment-expression contextual type (JS assignment-declaration cluster).
    private Type? GetContextualTypeForAssignmentExpression(BinaryExpression binary)
    {
        var left = binary.Left!;
        if (AstUtilities.IsAccessExpression(left))
        {
            var expr = left.Expression()!;
            switch (expr.Kind)
            {
                case SyntaxKind.Identifier:
                    {
                        var symbol = GetExportSymbolOfValueSymbolIfExported(GetResolvedSymbol(expr))!;
                        if ((symbol.Flags & SymbolFlags.ModuleExports) != 0)
                        {
                            // No contextual type for an expression of the form 'module.exports = expr'.
                            return null;
                        }
                        if (binary.Symbol != null)
                        {
                            // We have an assignment declaration (a binary expression with a symbol assigned by the binder) of the form
                            // 'F.id = expr' or 'F[xxx] = expr'. If 'F' is declared as a variable with a type annotation, we can obtain a
                            // contextual type from the annotated type without triggering a circularity. Otherwise, the assignment
                            // declaration has no contextual type.
                            if (symbol.ValueDeclaration != null && NodeTests.IsVariableDeclaration(symbol.ValueDeclaration))
                            {
                                var typeNode = symbol.ValueDeclaration.Type();
                                if (typeNode != null)
                                {
                                    if (NodeTests.IsPropertyAccessExpression(left))
                                    {
                                        return GetTypeOfPropertyOfContextualType(GetTypeFromTypeNode(typeNode), left.Name()!.Text());
                                    }
                                    var nameType = CheckExpressionCached(left.AsElementAccessExpression().ArgumentExpression!);
                                    if (IsTypeUsableAsPropertyName(nameType))
                                    {
                                        return GetTypeOfPropertyOfContextualTypeEx(GetTypeFromTypeNode(typeNode), GetPropertyNameFromType(nameType), nameType);
                                    }
                                    return GetTypeOfExpression(left);
                                }
                            }
                            return null;
                        }
                        break;
                    }
                case SyntaxKind.PropertyAccessExpression:
                case SyntaxKind.ElementAccessExpression:
                    if (binary.Symbol != null)
                    {
                        return null;
                    }
                    break;
                case SyntaxKind.ThisKeyword:
                    {
                        Symbol? symbol = null;
                        var thisType = GetTypeOfExpression(expr);
                        if (NodeTests.IsPropertyAccessExpression(left))
                        {
                            var name = left.Name()!;
                            if (NodeTests.IsPrivateIdentifier(name))
                            {
                                if (thisType.symbol != null)
                                {
                                    symbol = GetPropertyOfType(thisType, global::Ts.Binder.Binder.GetSymbolNameForPrivateIdentifier(thisType.symbol, name.Text()));
                                }
                            }
                            else
                            {
                                symbol = GetPropertyOfType(thisType, name.Text());
                            }
                        }
                        else
                        {
                            var propType = CheckExpressionCached(left.AsElementAccessExpression().ArgumentExpression!);
                            if (IsTypeUsableAsPropertyName(propType))
                            {
                                symbol = GetPropertyOfType(thisType, GetPropertyNameFromType(propType));
                            }
                        }
                        if (symbol != null)
                        {
                            var d = symbol.ValueDeclaration;
                            if (d != null && (NodeTests.IsPropertyDeclaration(d) || NodeTests.IsPropertySignatureDeclaration(d)) && d.Type() == null && d.Initializer() == null)
                            {
                                // No contextual type for 'this.xxx = expr', where xxx is declared as a property with no type annotation or initializer.
                                return null;
                            }
                        }
                        if (binary.Symbol != null && binary.Symbol.ValueDeclaration != null && binary.Symbol.ValueDeclaration.Type() == null)
                        {
                            // We have an assignment declaration 'this.xxx = expr' with no (synthetic) type annotation
                            if (!AstUtilities.IsObjectLiteralMethod(AstUtilities.GetThisContainer(expr, false, false)))
                            {
                                return null;
                            }
                            // and now for one single case of object literal methods
                            var name = AstUtilities.GetElementOrPropertyAccessName(left);
                            if (name == null)
                            {
                                return null;
                            }
                            else
                            {
                                // !!! contextual typing for `this` in object literals
                                return null;
                            }
                        }
                        break;
                    }
            }
        }
        return GetTypeOfExpression(left);
    }
    // GetTypeOfPropertyOfContextualTypeEx (+ its contextual-property helpers) are now REAL in Checker.ContextualType.cs (checker.go:30312).
    // checker.go:30469 — object-literal discrimination of union contextual types (discriminant cluster).
    private Type DiscriminateContextualTypeByObjectMembers(Node node, Type contextualType) => throw new NotImplementedException("discriminateContextualTypeByObjectMembers");
    // getAwaitedTypeNoAlias (31215): stub already declared in Checker.MembersSignatures.Deps.cs.
    // checkGeneratorInstantiationAssignabilityToReturnType: stub already in Checker.Declarations.Deps.cs.
}
