// Ported from tsgo internal/checker/jsx.go (jsx.go:1-1482), plan/03 §4.2 jsx wave.
// The JSX type-checking family: checkJsxElement/Fragment/SelfClosingElement, the opening-like
// element resolution trunk, attributes-type construction, intrinsic tag/element resolution, the
// JSX namespace/factory machinery, and the elaboration helpers used by the relater. Faithful
// per-function translation: same function order as Go, same branch structure; Go comments and
// `// !!!` markers preserved.
//
// §1.5: diagnostics via `using Diag = global::Ts.Diagnostics.Diagnostics;`; scanner via
// `global::Ts.Scanner.Scanner.X` (Scanner is a sibling namespace, so fully qualified); parser via
// `global::Ts.Parser.Parser.X`; the no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used
// directly, as in relater/inference/flow/statements. `core.IfElse/OrElse/Filter/Find/Map` →
// `CoreUtil.X`. `ast.NewDiagnosticChain`/`ast.NewDiagnostic` → `Diagnostic.NewDiagnosticChain`/
// `Diagnostic.NewDiagnostic`.
//
// A number of surrounding subsystems this file calls are not yet landed (contextual-type fetchers,
// spread types, call resolution, iteration types, the implicit-JSX-runtime import specifier, a
// handful of AST helpers living in the transformer adapters). Those are single-dedup stubbed in
// Checker.Jsx.Deps.cs and reached only on the deeper JSX paths; the intrinsic-tag path and the
// element/fragment/attributes trunk resolve for real where their dependencies exist.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.JsNum;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // utilities.go:1093 — a JSX tag name is intrinsic when it is a lowercase-first identifier, or any
    // namespaced name. Lives in checker/utilities.go; carried in the jsx wave as its only consumers
    // are the JSX family.
    internal static bool IsJsxIntrinsicTagName(Node tagName)
    {
        return NodeTests.IsIdentifier(tagName) && global::Ts.Scanner.Scanner.IsIntrinsicJsxName(tagName.Text()) || NodeTests.IsJsxNamespacedName(tagName);
    }

    // ast.IsJsxOpeningLikeElement — carried here (the AST-layer host is not yet landed; the
    // transformer adapter has an identical private copy).
    private static bool IsJsxOpeningLikeElement(Node node) =>
        NodeTests.IsJsxOpeningElement(node) || NodeTests.IsJsxSelfClosingElement(node);

    // jsx.go:71
    internal Type CheckJsxElement(Node node, CheckMode checkMode)
    {
        CheckNodeDeferred(node);
        return GetJsxElementTypeAt(node)!;
    }

    // jsx.go:76
    internal void CheckJsxElementDeferred(Node node)
    {
        var jsxElement = node.AsJsxElement();
        CheckJsxOpeningLikeElementOrOpeningFragment(jsxElement.OpeningElement!);
        // Perform resolution on the closing tag so that rename/go to definition/etc work
        if (IsJsxIntrinsicTagName(jsxElement.ClosingElement!.TagName()!))
        {
            GetIntrinsicTagSymbol(jsxElement.ClosingElement!);
        }
        else
        {
            CheckExpression(jsxElement.ClosingElement!.TagName()!);
        }
        CheckJsxChildren(node, CheckMode.Normal);
    }

    // jsx.go:88
    internal Type CheckJsxExpression(Node node, CheckMode checkMode)
    {
        CheckGrammarJsxExpression(node.AsJsxExpression());
        if (node.Expression() == null)
        {
            return errorType!;
        }
        var t = CheckExpressionEx(node.Expression()!, checkMode);
        if (node.AsJsxExpression().DotDotDotToken != null && t != anyType && !IsArrayType(t))
        {
            Error(node, Diag.JSX_spread_child_must_be_an_array_type);
        }
        return t;
    }

    // jsx.go:100
    internal Type CheckJsxSelfClosingElement(Node node, CheckMode checkMode)
    {
        CheckNodeDeferred(node);
        return GetJsxElementTypeAt(node)!;
    }

    // jsx.go:105
    internal void CheckJsxSelfClosingElementDeferred(Node node)
    {
        CheckJsxOpeningLikeElementOrOpeningFragment(node);
    }

    // jsx.go:109
    internal Type CheckJsxFragment(Node node)
    {
        CheckJsxOpeningLikeElementOrOpeningFragment(node.AsJsxFragment().OpeningFragment!);
        // by default, jsx:'react' will use jsxFactory = React.createElement and jsxFragmentFactory = React.Fragment
        // if jsxFactory compiler option is provided, ensure jsxFragmentFactory compiler option or @jsxFrag pragma is provided too
        var nodeSourceFile = AstUtilities.GetSourceFileOfNode(node);
        if (compilerOptions.GetJSXTransformEnabled() && (compilerOptions.JsxFactory != "" || GetPragmaFromSourceFile(nodeSourceFile!, "jsx") != null) && compilerOptions.JsxFragmentFactory == "" && GetPragmaFromSourceFile(nodeSourceFile!, "jsxfrag") == null)
        {
            var message = CoreUtil.IfElse(compilerOptions.JsxFactory != "",
                Diag.The_jsxFragmentFactory_compiler_option_must_be_provided_to_use_JSX_fragments_with_the_jsxFactory_compiler_option,
                Diag.An_jsxFrag_pragma_is_required_when_using_an_jsx_pragma_with_JSX_fragments);
            Error(node, message);
        }
        CheckJsxChildren(node, CheckMode.Normal);
        var t = GetJsxElementTypeAt(node)!;
        return CoreUtil.IfElse(IsErrorType(t), anyType!, t);
    }

    // jsx.go:125
    internal Type CheckJsxAttributes(Node node, CheckMode checkMode)
    {
        return CreateJsxAttributesTypeFromAttributesProperty(node.Parent!, checkMode);
    }

    // jsx.go:129
    internal void CheckJsxOpeningLikeElementOrOpeningFragment(Node node)
    {
        var isNodeOpeningLikeElement = IsJsxOpeningLikeElement(node);
        if (isNodeOpeningLikeElement)
        {
            CheckGrammarJsxElement(node);
        }
        CheckJsxPreconditions(node);
        MarkJsxAliasReferenced(node);
        var sig = GetResolvedSignature(node, null, CheckMode.Normal);
        CheckDeprecatedSignature(sig, node);
        if (isNodeOpeningLikeElement)
        {
            var elementTypeConstraint = GetJsxElementTypeTypeAt(node);
            if (elementTypeConstraint != null)
            {
                var tagName = node.TagName()!;
                Type tagType;
                if (IsJsxIntrinsicTagName(tagName))
                {
                    tagType = GetStringLiteralType(tagName.Text());
                }
                else
                {
                    tagType = CheckExpression(tagName);
                }
                var diags = new List<Diagnostic>();
                if (!CheckTypeRelatedToEx(tagType, elementTypeConstraint, assignableRelation!, tagName, Diag.Its_type_0_is_not_a_valid_JSX_element_type, diags))
                {
                    AddDiagnostic(Diagnostic.NewDiagnosticChain(diags[0], Diag.X_0_cannot_be_used_as_a_JSX_component, global::Ts.Scanner.Scanner.GetTextOfNode(tagName)));
                }
            }
            else
            {
                CheckJsxReturnAssignableToAppropriateBound(GetJsxReferenceKind(node), GetReturnTypeOfSignature(sig), node);
            }
        }
    }

    // jsx.go:158
    internal void CheckJsxPreconditions(Node errorNode)
    {
        // Preconditions for using JSX
        if (compilerOptions.Jsx == JsxEmit.None)
        {
            Error(errorNode, Diag.Cannot_use_JSX_unless_the_jsx_flag_is_provided);
        }
        if (noImplicitAny && GetJsxElementTypeAt(errorNode) == null)
        {
            Error(errorNode, Diag.JSX_element_implicitly_has_type_any_because_the_global_type_JSX_Element_does_not_exist);
        }
    }

    // jsx.go:168
    internal void CheckJsxReturnAssignableToAppropriateBound(JsxReferenceKind refKind, Type elemInstanceType, Node openingLikeElement)
    {
        var diags = new List<Diagnostic>();
        switch (refKind)
        {
            case JsxReferenceKind.Function:
                {
                    var sfcReturnConstraint = GetJsxStatelessElementTypeAt(openingLikeElement);
                    if (sfcReturnConstraint != null)
                    {
                        CheckTypeRelatedToEx(elemInstanceType, sfcReturnConstraint, assignableRelation!, openingLikeElement.TagName(), Diag.Its_return_type_0_is_not_a_valid_JSX_element, diags);
                    }
                    break;
                }
            case JsxReferenceKind.Component:
                {
                    var classConstraint = GetJsxElementClassTypeAt(openingLikeElement);
                    if (classConstraint != null)
                    {
                        // Issue an error if this return type isn't assignable to JSX.ElementClass, failing that
                        CheckTypeRelatedToEx(elemInstanceType, classConstraint, assignableRelation!, openingLikeElement.TagName(), Diag.Its_instance_type_0_is_not_a_valid_JSX_element, diags);
                    }
                    break;
                }
            default:
                {
                    var sfcReturnConstraint = GetJsxStatelessElementTypeAt(openingLikeElement);
                    var classConstraint = GetJsxElementClassTypeAt(openingLikeElement);
                    if (sfcReturnConstraint == null || classConstraint == null)
                    {
                        return;
                    }
                    var combined = GetUnionType(new[] { sfcReturnConstraint, classConstraint });
                    CheckTypeRelatedToEx(elemInstanceType, combined, assignableRelation!, openingLikeElement.TagName(), Diag.Its_element_type_0_is_not_a_valid_JSX_element, diags);
                    break;
                }
        }
        if (diags.Count != 0)
        {
            AddDiagnostic(Diagnostic.NewDiagnosticChain(diags[0], Diag.X_0_cannot_be_used_as_a_JSX_component, global::Ts.Scanner.Scanner.GetTextOfNode(openingLikeElement.TagName()!)));
        }
    }

    // jsx.go:196
    internal Type[] InferJsxTypeArguments(Node node, Signature signature, CheckMode checkMode, InferenceContext context)
    {
        var paramType = GetEffectiveFirstArgumentForJsxSignature(signature, node);
        var checkAttrType = CheckExpressionWithContextualType(node.Attributes()!, paramType, context, checkMode);
        InferTypes(context.inferences, checkAttrType, paramType, InferencePriority.None, false);
        return GetInferredTypes(context);
    }

    // jsx.go:203
    internal Type? GetContextualTypeForJsxExpression(Node node, ContextFlags contextFlags)
    {
        if (AstUtilities.IsJsxAttributeLike(node.Parent!))
        {
            return GetContextualType(node, contextFlags);
        }
        if (NodeTests.IsJsxElement(node.Parent!))
        {
            return GetContextualTypeForChildJsxExpression(node.Parent!, node, contextFlags);
        }
        return null;
    }

    // jsx.go:213
    internal Type? GetContextualTypeForJsxAttribute(Node attribute, ContextFlags contextFlags)
    {
        // When we trying to resolve JsxOpeningLikeElement as a stateless function element, we will already give its attributes a contextual type
        // which is a type of the parameter of the signature we are trying out.
        // If there is no contextual type (e.g. we are trying to resolve stateful component), get attributes type from resolving element's tagName
        if (NodeTests.IsJsxAttribute(attribute))
        {
            var attributesType = GetApparentTypeOfContextualType(attribute.Parent!, contextFlags);
            if (attributesType == null || IsTypeAny(attributesType))
            {
                return null;
            }
            return GetTypeOfPropertyOfContextualType(attributesType, attribute.Name()!.Text());
        }
        return GetContextualType(attribute.Parent!, contextFlags);
    }

    // jsx.go:227
    internal Type? GetContextualJsxElementAttributesType(Node node, ContextFlags contextFlags)
    {
        if (NodeTests.IsJsxOpeningElement(node) && contextFlags != ContextFlags.IgnoreNodeInferences)
        {
            var index = FindContextualNode(node.Parent!, contextFlags == ContextFlags.None);
            if (index >= 0)
            {
                // Contextually applied type is moved from attributes up to the outer jsx attributes so when walking up from the children they get hit
                // _However_ to hit them from the _attributes_ we must look for them here; otherwise we'll used the declared type
                // (as below) instead!
                return contextualInfos[index].t;
            }
        }
        return GetContextualTypeForArgumentAtIndex(node, 0);
    }

    // jsx.go:240
    internal Type? GetContextualTypeForChildJsxExpression(Node node, Node child /*JsxChild*/, ContextFlags contextFlags)
    {
        var attributesType = GetApparentTypeOfContextualType(node.AsJsxElement().OpeningElement!.Attributes()!, contextFlags);
        // JSX expression is in children of JSX Element, we will look for an "children" attribute (we get the name from JSX.ElementAttributesProperty)
        var jsxChildrenPropertyName = GetJsxElementChildrenPropertyName(GetJsxNamespaceAt(node));
        if (!(attributesType != null && !IsTypeAny(attributesType) && jsxChildrenPropertyName != InternalSymbolName.Missing && jsxChildrenPropertyName != ""))
        {
            return null;
        }
        var realChildren = GetSemanticJsxChildren(node.Children()!.Nodes);
        var childIndex = Array.IndexOf(realChildren, child);
        var childFieldType = GetTypeOfPropertyOfContextualType(attributesType, jsxChildrenPropertyName);
        if (childFieldType == null)
        {
            return null;
        }
        if (realChildren.Length == 1)
        {
            return childFieldType;
        }
        return MapTypeEx(childFieldType, t =>
        {
            if (IsArrayLikeType(t))
            {
                return GetIndexedAccessType(t, GetNumberLiteralType(new Number(childIndex)));
            }
            return t;
        }, true /*noReductions*/);
    }

    // jsx.go:264
    internal Type DiscriminateContextualTypeByJSXAttributes(Node node, Type contextualType)
    {
        var key = new DiscriminatedContextualTypeKey(AstUtilities.GetNodeId(node), contextualType.id);
        if (discriminatedContextualTypes.TryGetValue(key, out var discriminatedCached) && discriminatedCached != null)
        {
            return discriminatedCached;
        }
        var jsxChildrenPropertyName = GetJsxElementChildrenPropertyName(GetJsxNamespaceAt(node));
        var discriminantProperties = CoreUtil.Filter(node.Properties(), p =>
        {
            var symbol = p.Symbol();
            if (symbol == null || !NodeTests.IsJsxAttribute(p))
            {
                return false;
            }
            var initializer = p.Initializer();
            return (initializer == null || IsPossiblyDiscriminantValue(initializer)) && IsDiscriminantProperty(contextualType, symbol.Name);
        });
        var discriminantMembers = CoreUtil.Filter(GetPropertiesOfType(contextualType), s =>
        {
            if ((s.Flags & SymbolFlags.Optional) == 0 || node.Symbol() == null)
            {
                return false;
            }
            var element = node.Parent!.Parent;
            if (s.Name == jsxChildrenPropertyName && element != null && NodeTests.IsJsxElement(element) && GetSemanticJsxChildren(element.Children()!.Nodes).Length != 0)
            {
                return false;
            }
            return node.Symbol()!.Members?.GetValueOrDefault(s.Name) == null && IsDiscriminantProperty(contextualType, s.Name);
        });
        var discriminator = new ObjectLiteralDiscriminator(this, discriminantProperties ?? [], discriminantMembers ?? []);
        var discriminated = DiscriminateTypeByDiscriminableItems(contextualType, discriminator);
        discriminatedContextualTypes[key] = discriminated;
        return discriminated;
    }

    // jsx.go:294
    internal bool ElaborateJsxComponents(Node node, Type source, Type target, Relation relation, List<Diagnostic>? diagnosticOutput)
    {
        var reportedError = false;
        foreach (var prop in node.Properties() ?? [])
        {
            if (!NodeTests.IsJsxSpreadAttribute(prop) && !IsHyphenatedJsxName(prop.Name()!.Text()))
            {
                var nameType = GetStringLiteralType(prop.Name()!.Text());
                if (nameType != null && (nameType.flags & TypeFlags.Never) == 0)
                {
                    reportedError = ElaborateElement(source, target, relation, prop.Name()!, prop.Initializer(), nameType, null, null, diagnosticOutput) || reportedError;
                }
            }
        }
        if (NodeTests.IsJsxOpeningElement(node.Parent!) && NodeTests.IsJsxElement(node.Parent!.Parent!))
        {
            var containingElement = node.Parent!.Parent!; // Containing JSXElement
            var childrenPropName = GetJsxElementChildrenPropertyName(GetJsxNamespaceAt(node));
            if (childrenPropName == InternalSymbolName.Missing)
            {
                childrenPropName = "children";
            }
            var childrenNameType = GetStringLiteralType(childrenPropName);
            var childrenTargetType = GetIndexedAccessType(target, childrenNameType);
            var validChildren = GetSemanticJsxChildren(containingElement.Children()!.Nodes);
            if (validChildren.Length == 0)
            {
                return reportedError;
            }
            var moreThanOneRealChildren = validChildren.Length > 1;
            Type arrayLikeTargetParts;
            Type nonArrayLikeTargetParts;
            var iterableType = GetGlobalIterableType();
            if (iterableType != emptyGenericType)
            {
                var anyIterable = CreateIterableType(anyType!);
                arrayLikeTargetParts = FilterType(childrenTargetType, t => IsTypeAssignableTo(t, anyIterable));
                nonArrayLikeTargetParts = FilterType(childrenTargetType, t => !IsTypeAssignableTo(t, anyIterable));
            }
            else
            {
                arrayLikeTargetParts = FilterType(childrenTargetType, IsArrayOrTupleLikeType);
                nonArrayLikeTargetParts = FilterType(childrenTargetType, t => !IsArrayOrTupleLikeType(t));
            }
            global::Ts.Diagnostics.Message? invalidTextDiagnostic = null;
            object?[]? invalidTextDiagnosticArgs = null;
            (global::Ts.Diagnostics.Message, object?[]) GetInvalidTextualChildDiagnostic()
            {
                if (invalidTextDiagnostic == null)
                {
                    var tagNameText = global::Ts.Scanner.Scanner.GetTextOfNode(node.Parent!.TagName()!);
                    invalidTextDiagnostic = Diag.X_0_components_don_t_accept_text_as_child_elements_Text_in_JSX_has_the_type_string_but_the_expected_type_of_1_is_2;
                    invalidTextDiagnosticArgs = new object?[] { tagNameText, childrenPropName, TypeToString(childrenTargetType) };
                }
                return (invalidTextDiagnostic, invalidTextDiagnosticArgs!);
            }
            if (moreThanOneRealChildren)
            {
                if (arrayLikeTargetParts != neverType)
                {
                    var realSource = CreateTupleType(CheckJsxChildren(containingElement, CheckMode.Normal));
                    var children = GenerateJsxChildren(containingElement, GetInvalidTextualChildDiagnostic);
                    reportedError = ElaborateIterableOrArrayLikeTargetElementwise(children, realSource, arrayLikeTargetParts, relation, diagnosticOutput) || reportedError;
                }
                else if (!IsTypeRelatedTo(GetIndexedAccessType(source, childrenNameType), childrenTargetType, relation))
                {
                    // arity mismatch
                    var diag = Error(containingElement.AsJsxElement().OpeningElement!.TagName(), Diag.This_JSX_tag_s_0_prop_expects_a_single_child_of_type_1_but_multiple_children_were_provided, childrenPropName, TypeToString(childrenTargetType));
                    ReportDiagnostic(diag, diagnosticOutput);
                    reportedError = true;
                }
            }
            else
            {
                if (nonArrayLikeTargetParts != neverType)
                {
                    var child = validChildren[0];
                    var e = GetElaborationElementForJsxChild(child, childrenNameType, GetInvalidTextualChildDiagnostic);
                    if (e.errorNode != null)
                    {
                        reportedError = ElaborateElement(source, target, relation, e.errorNode, e.innerExpression, e.nameType!, null, e.createDiagnostic, diagnosticOutput) || reportedError;
                    }
                }
                else if (!IsTypeRelatedTo(GetIndexedAccessType(source, childrenNameType), childrenTargetType, relation))
                {
                    // arity mismatch
                    var diag = Error(containingElement.AsJsxElement().OpeningElement!.TagName(), Diag.This_JSX_tag_s_0_prop_expects_type_1_which_requires_multiple_children_but_only_a_single_child_was_provided, childrenPropName, TypeToString(childrenTargetType));
                    ReportDiagnostic(diag, diagnosticOutput);
                    reportedError = true;
                }
            }
        }
        return reportedError;
    }

    // jsx.go:374
    internal IEnumerable<JsxElaborationElement> GenerateJsxChildren(Node node, Func<(global::Ts.Diagnostics.Message, object?[])> getInvalidTextDiagnostic)
    {
        var memberOffset = 0;
        var nodes = node.Children()!.Nodes;
        for (var i = 0; i < nodes.Length; i++)
        {
            var child = nodes[i];
            var nameType = GetNumberLiteralType(new Number(i - memberOffset));
            var e = GetElaborationElementForJsxChild(child, nameType, getInvalidTextDiagnostic);
            if (e.errorNode != null)
            {
                yield return e;
            }
            else
            {
                memberOffset++;
            }
        }
    }

    // jsx.go:391
    internal JsxElaborationElement GetElaborationElementForJsxChild(Node child, Type nameType, Func<(global::Ts.Diagnostics.Message, object?[])> getInvalidTextDiagnostic)
    {
        switch (child.Kind)
        {
            case SyntaxKind.JsxExpression:
                // child is of the type of the expression
                return new JsxElaborationElement { errorNode = child, innerExpression = child.Expression(), nameType = nameType };
            case SyntaxKind.JsxText:
                if (child.AsJsxText().ContainsOnlyTriviaWhiteSpaces)
                {
                    // Whitespace only jsx text isn't real jsx text
                    return default;
                }
                // child is a string
                return new JsxElaborationElement
                {
                    errorNode = child,
                    innerExpression = null,
                    nameType = nameType,
                    createDiagnostic = prop =>
                    {
                        var (errorMessage, errorArgs) = getInvalidTextDiagnostic();
                        return NewDiagnosticForNode(prop, errorMessage, errorArgs);
                    },
                };
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxFragment:
                // child is of type JSX.Element
                return new JsxElaborationElement { errorNode = child, innerExpression = child, nameType = nameType };
        }
        throw new InvalidOperationException("Unhandled case in getElaborationElementForJsxChild");
    }

    // jsx.go:418
    internal bool ElaborateIterableOrArrayLikeTargetElementwise(IEnumerable<JsxElaborationElement> iterator, Type source, Type target, Relation relation, List<Diagnostic>? diagnosticOutput)
    {
        var tupleOrArrayLikeTargetParts = FilterType(target, IsArrayOrTupleLikeType);
        var nonTupleOrArrayLikeTargetParts = FilterType(target, t => !IsArrayOrTupleLikeType(t));
        // If `nonTupleOrArrayLikeTargetParts` is not `never`, then that should mean `Iterable` is defined.
        Type? iterationType = null;
        if (nonTupleOrArrayLikeTargetParts != neverType)
        {
            iterationType = GetIterationTypeOfIterable(IterationUse.ForOf, IterationTypeKind.Yield, nonTupleOrArrayLikeTargetParts, null /*errorNode*/);
        }
        var reportedError = false;
        foreach (var e in iterator)
        {
            var prop = e.errorNode!;
            var next = e.innerExpression;
            var nameType = e.nameType!;
            var targetPropType = iterationType;
            Type? targetIndexedPropType = null;
            if (tupleOrArrayLikeTargetParts != neverType)
            {
                targetIndexedPropType = GetBestMatchIndexedAccessTypeOrUndefined(source, tupleOrArrayLikeTargetParts, nameType);
            }
            if (targetIndexedPropType != null && (targetIndexedPropType.flags & TypeFlags.IndexedAccess) == 0)
            {
                if (iterationType != null)
                {
                    targetPropType = GetUnionType(new[] { iterationType, targetIndexedPropType });
                }
                else
                {
                    targetPropType = targetIndexedPropType;
                }
            }
            if (targetPropType == null)
            {
                continue;
            }
            var sourcePropType = GetIndexedAccessTypeOrUndefined(source, nameType, AccessFlags.None, null, null);
            if (sourcePropType == null)
            {
                continue;
            }
            var propName = GetPropertyNameFromIndex(nameType, null /*accessNode*/);
            if (!CheckTypeRelatedTo(sourcePropType, targetPropType, relation, null /*errorNode*/))
            {
                var elaborated = next != null && ElaborateError(next, sourcePropType, targetPropType, relation, null /*headMessage*/, diagnosticOutput);
                reportedError = true;
                if (!elaborated)
                {
                    // Issue error on the prop itself, since the prop couldn't elaborate the error. Use the expression type, if available.
                    var specificSource = sourcePropType;
                    if (next != null)
                    {
                        specificSource = CheckExpressionForMutableLocationWithContextualType(next, sourcePropType);
                    }
                    if (e.createDiagnostic != null)
                    {
                        // Use the custom diagnostic factory if provided (e.g., for JSX text children with dynamic error messages)
                        ReportDiagnostic(e.createDiagnostic(prop), diagnosticOutput);
                    }
                    else if (exactOptionalPropertyTypes && IsExactOptionalPropertyMismatch(specificSource, targetPropType))
                    {
                        var diag = CreateDiagnosticForNode(prop, Diag.Type_0_is_not_assignable_to_type_1_with_exactOptionalPropertyTypes_Colon_true_Consider_adding_undefined_to_the_type_of_the_target, TypeToString(specificSource), TypeToString(targetPropType));
                        ReportDiagnostic(diag, diagnosticOutput);
                    }
                    else
                    {
                        var targetIsOptional = propName != InternalSymbolName.Missing && (CoreUtil.OrElse(GetPropertyOfType(tupleOrArrayLikeTargetParts, propName), unknownSymbol)!.Flags & SymbolFlags.Optional) != 0;
                        var sourceIsOptional = propName != InternalSymbolName.Missing && (CoreUtil.OrElse(GetPropertyOfType(source, propName), unknownSymbol)!.Flags & SymbolFlags.Optional) != 0;
                        targetPropType = RemoveMissingType(targetPropType, targetIsOptional);
                        sourcePropType = RemoveMissingType(sourcePropType, targetIsOptional && sourceIsOptional);
                        var result = CheckTypeRelatedToEx(specificSource, targetPropType, relation, prop, null, diagnosticOutput);
                        if (result && specificSource != sourcePropType)
                        {
                            // If for whatever reason the expression type doesn't yield an error, make sure we still issue an error on the sourcePropType
                            CheckTypeRelatedToEx(sourcePropType, targetPropType, relation, prop, null, diagnosticOutput);
                        }
                    }
                }
            }
        }
        return reportedError;
    }

    // jsx.go:483
    internal Symbol? GetSuggestedSymbolForNonexistentJSXAttribute(string name, Type containingType)
    {
        var properties = GetPropertiesOfType(containingType);
        Symbol? jsxSpecific = null;
        switch (name)
        {
            case "for":
                jsxSpecific = CoreUtil.Find(properties, x => AstUtilities.SymbolName(x) == "htmlFor");
                break;
            case "class":
                jsxSpecific = CoreUtil.Find(properties, x => AstUtilities.SymbolName(x) == "className");
                break;
        }
        if (jsxSpecific != null)
        {
            return jsxSpecific;
        }
        return GetSpellingSuggestionForName(name, properties, SymbolFlags.Value);
    }

    // jsx.go:498
    internal Type GetJSXFragmentType(Node node)
    {
        // An opening fragment is required in order for `getJsxNamespace` to give the fragment factory
        var links = sourceFileLinks.Get(AstUtilities.GetSourceFileOfNode(node)!);
        if (links.jsxFragmentType != null)
        {
            return links.jsxFragmentType;
        }
        var jsxFragmentFactoryName = GetJsxNamespace(node);
        // #38720/60122, allow null as jsxFragmentFactory
        var shouldResolveFactoryReference = (compilerOptions.Jsx == JsxEmit.React || compilerOptions.JsxFragmentFactory != "") && jsxFragmentFactoryName != "null";
        if (!shouldResolveFactoryReference)
        {
            links.jsxFragmentType = anyType;
            return links.jsxFragmentType!;
        }
        var jsxFactorySymbol = GetJsxNamespaceContainerForImplicitImport(node);
        if (jsxFactorySymbol == null)
        {
            var shouldModuleRefErr = compilerOptions.Jsx != JsxEmit.Preserve && compilerOptions.Jsx != JsxEmit.ReactNative;
            var flags = SymbolFlags.Value;
            if (!shouldModuleRefErr)
            {
                flags &= ~SymbolFlags.Enum;
            }
            jsxFactorySymbol = ResolveName(node, jsxFragmentFactoryName, flags, Diag.Using_JSX_fragments_requires_fragment_factory_0_to_be_in_scope_but_it_could_not_be_found, true /*isUse*/, false /*excludeGlobals*/);
        }
        if (jsxFactorySymbol == null)
        {
            links.jsxFragmentType = errorType;
            return links.jsxFragmentType!;
        }
        if (jsxFactorySymbol.Name == ReactNames.Fragment)
        {
            links.jsxFragmentType = GetTypeOfSymbol(jsxFactorySymbol);
            return links.jsxFragmentType;
        }
        var resolvedAlias = jsxFactorySymbol;
        if ((jsxFactorySymbol.Flags & SymbolFlags.Alias) != 0)
        {
            (resolvedAlias, _) = ResolveAlias(jsxFactorySymbol);
        }

        var reactExports = GetExportsOfSymbol(resolvedAlias!);
        var typeSymbol = GetSymbol(reactExports, ReactNames.Fragment, SymbolFlags.BlockScopedVariable);
        if (typeSymbol != null)
        {
            links.jsxFragmentType = GetTypeOfSymbol(typeSymbol);
        }
        else
        {
            links.jsxFragmentType = errorType;
        }
        return links.jsxFragmentType!;
    }

    // jsx.go:543
    internal Signature ResolveJsxOpeningLikeElement(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        var isJsxOpenFragment = NodeTests.IsJsxOpeningFragment(node);
        Type exprTypes;
        if (!isJsxOpenFragment)
        {
            if (IsJsxIntrinsicTagName(node.TagName()!))
            {
                var result = GetIntrinsicAttributesTypeFromJsxOpeningLikeElement(node);
                var fakeSignature = CreateSignatureForJSXIntrinsic(node, result);
                CheckTypeAssignableToAndOptionallyElaborate(CheckExpressionWithContextualType(node.Attributes()!, GetEffectiveFirstArgumentForJsxSignature(fakeSignature, node), null /*inferenceContext*/, CheckMode.Normal), result, node.TagName(), node.Attributes(), null, null);
                var typeArguments = node.TypeArguments();
                if (typeArguments != null && typeArguments.Length != 0)
                {
                    CheckSourceElements(typeArguments);
                    var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
                    var typeArgumentList = node.TypeArgumentList()!;
                    var loc = new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), typeArgumentList.Loc.Pos), typeArgumentList.Loc.End);
                    AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, loc, Diag.Expected_0_type_arguments_but_got_1, 0, typeArguments.Length));
                }
                return fakeSignature;
            }
            exprTypes = CheckExpression(node.TagName()!);
        }
        else
        {
            exprTypes = GetJSXFragmentType(node);
        }
        var apparentType = GetApparentType(exprTypes);
        if (IsErrorType(apparentType))
        {
            return ResolveErrorCall(node);
        }
        var signatures = GetUninstantiatedJsxSignaturesOfType(exprTypes, node);
        if (IsUntypedFunctionCall(exprTypes, apparentType, signatures.Length, 0 /*constructSignatures*/))
        {
            return ResolveUntypedCall(node);
        }
        if (signatures.Length == 0)
        {
            // We found no signatures at all, which is an error
            if (isJsxOpenFragment)
            {
                Error(node, Diag.JSX_element_type_0_does_not_have_any_construct_or_call_signatures, global::Ts.Scanner.Scanner.GetTextOfNode(node));
            }
            else
            {
                Error(node.TagName(), Diag.JSX_element_type_0_does_not_have_any_construct_or_call_signatures, global::Ts.Scanner.Scanner.GetTextOfNode(node.TagName()!));
            }
            return ResolveErrorCall(node);
        }
        return ResolveCall(node, signatures, candidatesOutArray, checkMode, SignatureFlags.None, null);
    }

    // Check if the given signature can possibly be a signature called by the JSX opening-like element.
    // @param node a JSX opening-like element we are trying to figure its call signature
    // @param signature a candidate signature we are trying whether it is a call signature
    // @param relation a relationship to check parameter and argument type
    // jsx.go:589
    internal bool CheckApplicableSignatureForJsxCallLikeElement(Node node, Signature signature, Relation relation, CheckMode checkMode, bool reportErrors, List<Diagnostic>? diagnosticOutput)
    {
        // Stateless function components can have maximum of three arguments: "props", "context", and "updater".
        // However "context" and "updater" are implicit and can't be specify by users. Only the first parameter, props,
        // can be specified by users through attributes property.
        var paramType = GetEffectiveFirstArgumentForJsxSignature(signature, node);
        Type attributesType;
        if (NodeTests.IsJsxOpeningFragment(node))
        {
            attributesType = CreateJsxAttributesTypeFromAttributesProperty(node, CheckMode.Normal);
        }
        else
        {
            attributesType = CheckExpressionWithContextualType(node.Attributes()!, paramType, null /*inferenceContext*/, checkMode);
        }
        Type checkAttributesType;
        bool CheckTagNameDoesNotExpectTooManyArguments()
        {
            if (GetJsxNamespaceContainerForImplicitImport(node) != null)
            {
                return true; // factory is implicitly jsx/jsxdev - assume it fits the bill, since we don't strongly look for the jsx/jsxs/jsxDEV factory APIs anywhere else (at least not yet)
            }
            // We assume fragments have the correct arity since the node does not have attributes
            Type? tagType = null;
            if ((NodeTests.IsJsxOpeningElement(node) || NodeTests.IsJsxSelfClosingElement(node)) && !(IsJsxIntrinsicTagName(node.TagName()!) || NodeTests.IsJsxNamespacedName(node.TagName()!)))
            {
                tagType = CheckExpression(node.TagName()!);
            }
            if (tagType == null)
            {
                return true;
            }
            var tagCallSignatures = GetSignaturesOfType(tagType, SignatureKind.Call);
            if (tagCallSignatures.Length == 0)
            {
                return true;
            }
            var factory = GetJsxFactoryEntity(node);
            if (factory == null)
            {
                return true;
            }
            var factorySymbol = ResolveEntityName(factory, SymbolFlags.Value, true /*ignoreErrors*/, false /*dontResolveAlias*/, node);
            if (factorySymbol == null)
            {
                return true;
            }

            var factoryType = GetTypeOfSymbol(factorySymbol);
            var callSignatures = GetSignaturesOfType(factoryType, SignatureKind.Call);
            if (callSignatures.Length == 0)
            {
                return true;
            }
            var hasFirstParamSignatures = false;
            var maxParamCount = 0;
            // Check that _some_ first parameter expects a FC-like thing, and that some overload of the SFC expects an acceptable number of arguments
            foreach (var sig in callSignatures)
            {
                var firstparam = GetTypeAtPosition(sig, 0);
                var signaturesOfParam = GetSignaturesOfType(firstparam, SignatureKind.Call);
                if (signaturesOfParam.Length == 0)
                {
                    continue;
                }
                foreach (var paramSig in signaturesOfParam)
                {
                    hasFirstParamSignatures = true;
                    if (HasEffectiveRestParameter(paramSig))
                    {
                        return true; // some signature has a rest param, so function components can have an arbitrary number of arguments
                    }
                    var paramCount = GetParameterCount(paramSig);
                    if (paramCount > maxParamCount)
                    {
                        maxParamCount = paramCount;
                    }
                }
            }
            if (!hasFirstParamSignatures)
            {
                // Not a single signature had a first parameter which expected a signature - for back compat, and
                // to guard against generic factories which won't have signatures directly, do not error
                return true;
            }
            var absoluteMinArgCount = int.MaxValue;
            foreach (var tagSig in tagCallSignatures)
            {
                var tagRequiredArgCount = GetMinArgumentCount(tagSig);
                if (tagRequiredArgCount < absoluteMinArgCount)
                {
                    absoluteMinArgCount = tagRequiredArgCount;
                }
            }
            if (absoluteMinArgCount <= maxParamCount)
            {
                return true; // some signature accepts the number of arguments the function component provides
            }
            if (reportErrors)
            {
                var tagName = node.TagName()!;
                // We will not report errors in this function for fragments, since we do not check them in this function
                var diag = NewDiagnosticForNode(tagName, Diag.Tag_0_expects_at_least_1_arguments_but_the_JSX_factory_2_provides_at_most_3, EntityNameToString(tagName), absoluteMinArgCount, EntityNameToString(factory), maxParamCount);
                var tagNameSymbol = GetSymbolAtLocation(tagName, false);
                if (tagNameSymbol != null && tagNameSymbol.ValueDeclaration != null)
                {
                    diag.AddRelatedInfo(NewDiagnosticForNode(tagNameSymbol.ValueDeclaration, Diag.X_0_is_declared_here, EntityNameToString(tagName)));
                }
                ReportDiagnostic(diag, diagnosticOutput);
            }
            return false;
        }
        if ((checkMode & CheckMode.SkipContextSensitive) != 0)
        {
            checkAttributesType = GetRegularTypeOfObjectLiteral(attributesType);
        }
        else
        {
            checkAttributesType = attributesType;
        }
        if (!CheckTagNameDoesNotExpectTooManyArguments())
        {
            return false;
        }
        Node? errorNode = null;
        if (reportErrors)
        {
            if (NodeTests.IsJsxOpeningFragment(node))
            {
                errorNode = node;
            }
            else
            {
                errorNode = node.TagName();
            }
        }
        Node? attributes = null;
        if (!NodeTests.IsJsxOpeningFragment(node))
        {
            attributes = node.Attributes();
        }
        return CheckTypeRelatedToAndOptionallyElaborate(checkAttributesType, paramType, relation, errorNode, attributes, null, diagnosticOutput);
    }

    // Get attributes type of the JSX opening-like element. The result is from resolving "attributes" property of the opening-like element.
    //
    // @param openingLikeElement a JSX opening-like element
    // @param filter a function to remove attributes that will not participate in checking whether attributes are assignable
    // @return an anonymous type (similar to the one returned by checkObjectLiteral) in which its properties are attributes property.
    // @remarks Because this function calls getSpreadType, it needs to use the same checks as checkObjectLiteral,
    // which also calls getSpreadType.
    // jsx.go:708
    internal Type CreateJsxAttributesTypeFromAttributesProperty(Node openingLikeElement, CheckMode checkMode)
    {
        SymbolTable? allAttributesTable = null;
        if (strictNullChecks)
        {
            allAttributesTable = new SymbolTable();
        }
        var attributesTable = new SymbolTable();
        Symbol? attributesSymbol = null;
        var attributeParent = openingLikeElement;
        var spread = emptyJsxObjectType!;
        var hasSpreadAnyType = false;
        Type? typeToIntersect = null;
        var explicitlySpecifyChildrenAttribute = false;
        var objectFlags = ObjectFlags.JsxAttributes;
        Type CreateJsxAttributesType()
        {
            objectFlags |= ObjectFlags.FreshLiteral;
            var result = NewAnonymousType(attributesSymbol, attributesTable, null, null, null);
            result.objectFlags |= objectFlags | ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
            return result;
        }
        var jsxChildrenPropertyName = GetJsxElementChildrenPropertyName(GetJsxNamespaceAt(openingLikeElement));
        var isJsxOpenFragment = NodeTests.IsJsxOpeningFragment(openingLikeElement);
        if (!isJsxOpenFragment)
        {
            var attributes = openingLikeElement.Attributes()!;
            attributesSymbol = attributes.Symbol();
            attributeParent = attributes;
            var contextualType = GetContextualType(attributes, ContextFlags.None);
            // Create anonymous type from given attributes symbol table.
            // @param symbol a symbol of JsxAttributes containing attributes corresponding to attributesTable
            // @param attributesTable a symbol table of attributes property
            foreach (var attributeDecl in attributes.Properties() ?? [])
            {
                var member = attributeDecl.Symbol()!;
                if (NodeTests.IsJsxAttribute(attributeDecl))
                {
                    var exprType = CheckJsxAttribute(attributeDecl, checkMode);
                    objectFlags |= exprType.objectFlags & ObjectFlags.PropagatingFlags;
                    var attributeSymbol = NewSymbol(SymbolFlags.Property | member.Flags, member.Name);
                    attributeSymbol.Declarations = member.Declarations;
                    attributeSymbol.Parent = member.Parent;
                    if (member.ValueDeclaration != null)
                    {
                        attributeSymbol.ValueDeclaration = member.ValueDeclaration;
                    }
                    var links = valueSymbolLinks.Get(attributeSymbol);
                    links.resolvedType = exprType;
                    links.target = member;
                    attributesTable[attributeSymbol.Name] = attributeSymbol;
                    if (allAttributesTable != null)
                    {
                        allAttributesTable[attributeSymbol.Name] = attributeSymbol;
                    }
                    if (attributeDecl.Name()!.Text() == jsxChildrenPropertyName)
                    {
                        explicitlySpecifyChildrenAttribute = true;
                    }
                    if (contextualType != null)
                    {
                        var prop = GetPropertyOfType(contextualType, member.Name);
                        if (prop != null && prop.Declarations != null && IsDeprecatedSymbol(prop) && NodeTests.IsIdentifier(attributeDecl.Name()!))
                        {
                            AddDeprecatedSuggestion(attributeDecl.Name()!, prop.Declarations, attributeDecl.Name()!.Text());
                        }
                    }
                    if (contextualType != null && (checkMode & CheckMode.Inferential) != 0 && (checkMode & CheckMode.SkipContextSensitive) == 0 && IsContextSensitive(attributeDecl))
                    {
                        var inferenceContext = GetInferenceContext(attributes);
                        Debug.Assert(inferenceContext != null);
                        // In CheckMode.Inferential we should always have an inference context
                        var inferenceNode = attributeDecl.Initializer()!.Expression();
                        AddIntraExpressionInferenceSite(inferenceContext!, inferenceNode!, exprType);
                    }
                }
                else
                {
                    Debug.Assert(attributeDecl.Kind == SyntaxKind.JsxSpreadAttribute);
                    if (attributesTable.Count != 0)
                    {
                        spread = GetSpreadType(spread, CreateJsxAttributesType(), attributesSymbol, objectFlags, false /*readonly*/);
                        attributesTable = new SymbolTable();
                    }
                    var exprType = GetReducedType(CheckExpressionEx(attributeDecl.Expression()!, checkMode & CheckMode.Inferential));
                    if (IsTypeAny(exprType))
                    {
                        hasSpreadAnyType = true;
                    }
                    if (IsValidSpreadType(exprType))
                    {
                        spread = GetSpreadType(spread, exprType, attributesSymbol, objectFlags, false /*readonly*/);
                        if (allAttributesTable != null)
                        {
                            CheckSpreadPropOverrides(exprType, allAttributesTable, attributeDecl);
                        }
                    }
                    else
                    {
                        Error(attributeDecl.Expression(), Diag.Spread_types_may_only_be_created_from_object_types);
                        if (typeToIntersect != null)
                        {
                            typeToIntersect = GetIntersectionType(new[] { typeToIntersect, exprType });
                        }
                        else
                        {
                            typeToIntersect = exprType;
                        }
                    }
                }
            }
            if (!hasSpreadAnyType)
            {
                if (attributesTable.Count != 0)
                {
                    spread = GetSpreadType(spread, CreateJsxAttributesType(), attributesSymbol, objectFlags, false /*readonly*/);
                }
            }
        }
        bool ParentHasSemanticJsxChildren(Node ole)
        {
            // Handle children attribute
            var parent = ole.Parent;
            if (parent == null)
            {
                return false;
            }
            Node[]? children = null;

            if (NodeTests.IsJsxElement(parent))
            {
                // We have to check that openingElement of the parent is the one we are visiting as this may not be true for selfClosingElement
                if (parent.AsJsxElement().OpeningElement == ole)
                {
                    children = parent.Children()!.Nodes;
                }
            }
            else if (NodeTests.IsJsxFragment(parent))
            {
                if (parent.AsJsxFragment().OpeningFragment == ole)
                {
                    children = parent.Children()!.Nodes;
                }
            }
            return GetSemanticJsxChildren(children).Length != 0;
        }
        if (ParentHasSemanticJsxChildren(openingLikeElement))
        {
            Type[] childTypes = CheckJsxChildren(openingLikeElement.Parent!, checkMode);
            if (!hasSpreadAnyType && jsxChildrenPropertyName != InternalSymbolName.Missing && jsxChildrenPropertyName != "")
            {
                // Error if there is a attribute named "children" explicitly specified and children element.
                // This is because children element will overwrite the value from attributes.
                // Note: we will not warn "children" attribute overwritten if "children" attribute is specified in object spread.
                if (explicitlySpecifyChildrenAttribute)
                {
                    Error(attributeParent, Diag.X_0_are_specified_twice_The_attribute_named_0_will_be_overwritten, jsxChildrenPropertyName);
                }
                Type? childrenContextualType = null;
                if (NodeTests.IsJsxOpeningElement(openingLikeElement))
                {
                    var contextualType = GetApparentTypeOfContextualType(openingLikeElement.Attributes()!, ContextFlags.None);
                    if (contextualType != null)
                    {
                        childrenContextualType = GetTypeOfPropertyOfContextualType(contextualType, jsxChildrenPropertyName);
                    }
                }
                // If there are children in the body of JSX element, create dummy attribute "children" with the union of children types so that it will pass the attribute checking process
                var childrenPropSymbol = NewSymbol(SymbolFlags.Property, jsxChildrenPropertyName);
                var links = valueSymbolLinks.Get(childrenPropSymbol);
                if (childTypes.Length == 1)
                {
                    links.resolvedType = childTypes[0];
                }
                else if (childrenContextualType != null && SomeType(childrenContextualType, IsTupleLikeType))
                {
                    links.resolvedType = CreateTupleType(childTypes);
                }
                else
                {
                    links.resolvedType = CreateArrayType(GetUnionType(childTypes));
                }
                // Fake up a property declaration for the children
                childrenPropSymbol.ValueDeclaration = factory.NewPropertySignatureDeclaration(null, factory.NewIdentifier(jsxChildrenPropertyName), null /*postfixToken*/, null /*type*/, null /*initializer*/);
                childrenPropSymbol.ValueDeclaration.Parent = attributeParent;
                childrenPropSymbol.ValueDeclaration.AsPropertySignatureDeclaration().Symbol = childrenPropSymbol;
                var childPropMap = new SymbolTable();
                childPropMap[jsxChildrenPropertyName] = childrenPropSymbol;
                spread = GetSpreadType(spread, NewAnonymousType(attributesSymbol, childPropMap, null, null, null), attributesSymbol, objectFlags | GetPropagatingFlagsOfTypes(childTypes, TypeFlags.None), false /*readonly*/);
            }
        }
        if (hasSpreadAnyType)
        {
            return anyType!;
        }
        if (typeToIntersect != null)
        {
            if (spread != emptyJsxObjectType)
            {
                return GetIntersectionType(new[] { typeToIntersect, spread });
            }
            return typeToIntersect;
        }
        if (spread == emptyJsxObjectType)
        {
            return CreateJsxAttributesType();
        }
        return spread;
    }

    // jsx.go:873
    internal Type CheckJsxAttribute(Node node, CheckMode checkMode)
    {
        if (node.Initializer() != null)
        {
            return CheckExpressionForMutableLocation(node.Initializer()!, checkMode);
        }
        // <Elem attr /> is sugar for <Elem attr={true} />
        return trueType!;
    }

    // jsx.go:881
    internal Type[] CheckJsxChildren(Node node, CheckMode checkMode)
    {
        List<Type>? childTypes = null;
        foreach (var child in node.Children()!.Nodes)
        {
            // In React, JSX text that contains only whitespaces will be ignored so we don't want to type-check that
            // because then type of children property will have constituent of string type.
            if (NodeTests.IsJsxText(child))
            {
                if (!child.AsJsxText().ContainsOnlyTriviaWhiteSpaces)
                {
                    (childTypes ??= []).Add(stringType!);
                }
            }
            else if (NodeTests.IsJsxExpression(child) && child.Expression() == null)
            {
                // empty jsx expressions don't *really* count as present children
                continue;
            }
            else
            {
                (childTypes ??= []).Add(CheckExpressionForMutableLocation(child, checkMode));
            }
        }
        return childTypes?.ToArray() ?? [];
    }

    // jsx.go:900
    internal Signature[] GetUninstantiatedJsxSignaturesOfType(Type elementType, Node caller)
    {
        if ((elementType.flags & TypeFlags.String) != 0)
        {
            return new[] { anySignature! };
        }
        if ((elementType.flags & TypeFlags.StringLiteral) != 0)
        {
            var intrinsicType = GetIntrinsicAttributesTypeFromStringLiteralType(elementType, caller);
            if (intrinsicType == null)
            {
                Error(caller, Diag.Property_0_does_not_exist_on_type_1, GetStringLiteralValue(elementType), "JSX." + JsxNames.IntrinsicElements);
                return [];
            }
            var fakeSignature = CreateSignatureForJSXIntrinsic(caller, intrinsicType);
            return new[] { fakeSignature };
        }
        var apparentElemType = GetApparentType(elementType);
        // Resolve the signatures, preferring constructor
        var signatures = GetSignaturesOfType(apparentElemType, SignatureKind.Construct);
        if (signatures.Length == 0)
        {
            // No construct signatures, try call signatures
            signatures = GetSignaturesOfType(apparentElemType, SignatureKind.Call);
        }
        if (signatures.Length == 0 && (apparentElemType.flags & TypeFlags.Union) != 0)
        {
            // If each member has some combination of new/call signatures; make a union signature list for those
            signatures = GetUnionSignatures(CoreUtil.Map(apparentElemType.Types(), t =>
                GetUninstantiatedJsxSignaturesOfType(t, caller))!);
        }
        return signatures;
    }

    // jsx.go:929
    internal Type GetEffectiveFirstArgumentForJsxSignature(Signature signature, Node node)
    {
        if (NodeTests.IsJsxOpeningFragment(node) || GetJsxReferenceKind(node) != JsxReferenceKind.Component)
        {
            return GetJsxPropsTypeFromCallSignature(signature, node);
        }
        return GetJsxPropsTypeFromClassType(signature, node);
    }

    // jsx.go:936
    internal Type GetJsxPropsTypeFromCallSignature(Signature sig, Node context)
    {
        var propsType = GetTypeOfFirstParameterOfSignatureWithFallback(sig, unknownType!);
        propsType = GetJsxManagedAttributesFromLocatedAttributes(context, GetJsxNamespaceAt(context), propsType);
        var intrinsicAttribs = GetJsxType(JsxNames.IntrinsicAttributes, context);
        if (!IsErrorType(intrinsicAttribs))
        {
            propsType = IntersectTypes(intrinsicAttribs, propsType)!;
        }
        return propsType;
    }

    // jsx.go:946
    internal Type GetJsxPropsTypeFromClassType(Signature sig, Node context)
    {
        var ns = GetJsxNamespaceAt(context);
        var forcedLookupLocation = GetJsxElementPropertiesName(ns);
        Type? attributesType;
        switch (forcedLookupLocation)
        {
            case InternalSymbolName.Missing:
                attributesType = GetTypeOfFirstParameterOfSignatureWithFallback(sig, unknownType!);
                break;
            case "":
                attributesType = GetReturnTypeOfSignature(sig);
                break;
            default:
                attributesType = GetJsxPropsTypeForSignatureFromMember(sig, forcedLookupLocation);
                if (attributesType == null && context.Attributes()!.Properties().Len() != 0)
                {
                    // There is no property named 'props' on this instance type
                    Error(context, Diag.JSX_element_class_does_not_support_attributes_because_it_does_not_have_a_0_property, forcedLookupLocation);
                }
                break;
        }
        if (attributesType == null)
        {
            return unknownType!;
        }
        attributesType = GetJsxManagedAttributesFromLocatedAttributes(context, ns, attributesType);
        if (IsTypeAny(attributesType))
        {
            // Props is of type 'any' or unknown
            return attributesType;
        }
        // Normal case -- add in IntrinsicClassAttributes<T> and IntrinsicAttributes
        var apparentAttributesType = attributesType;
        var intrinsicClassAttribs = GetJsxType(JsxNames.IntrinsicClassAttributes, context);
        if (!IsErrorType(intrinsicClassAttribs))
        {
            var typeParams = GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(intrinsicClassAttribs.symbol!);
            var hostClassType = GetReturnTypeOfSignature(sig);
            Type libraryManagedAttributeType;
            if (typeParams != null)
            {
                // apply JSX.IntrinsicClassAttributes<hostClassType, ...>
                var inferredArgs = FillMissingTypeArguments(new[] { hostClassType }, typeParams, GetMinTypeArgumentCount(typeParams), AstUtilities.IsInJSFile(context));
                libraryManagedAttributeType = InstantiateType(intrinsicClassAttribs, TypeMapper.NewTypeMapper(typeParams, inferredArgs!));
            }
            else
            {
                libraryManagedAttributeType = intrinsicClassAttribs;
            }
            apparentAttributesType = IntersectTypes(libraryManagedAttributeType, apparentAttributesType)!;
        }
        var intrinsicAttribs = GetJsxType(JsxNames.IntrinsicAttributes, context);
        if (!IsErrorType(intrinsicAttribs))
        {
            apparentAttributesType = IntersectTypes(intrinsicAttribs, apparentAttributesType)!;
        }
        return apparentAttributesType;
    }

    // jsx.go:993
    internal Type? GetJsxPropsTypeForSignatureFromMember(Signature sig, string forcedLookupLocation)
    {
        if (sig.composite != null)
        {
            // JSX Elements using the legacy `props`-field based lookup (eg, react class components) need to treat the `props` member as an input
            // instead of an output position when resolving the signature. We need to go back to the input signatures of the composite signature,
            // get the type of `props` on each return type individually, and then _intersect them_, rather than union them (as would normally occur
            // for a union signature). It's an unfortunate quirk of looking in the output of the signature for the type we want to use for the input.
            // The default behavior of `getTypeOfFirstParameterOfSignatureWithFallback` when no `props` member name is defined is much more sane.
            List<Type>? results = null;
            foreach (var signature in sig.composite.signatures)
            {
                var instance = GetReturnTypeOfSignature(signature);
                if (IsTypeAny(instance))
                {
                    return instance;
                }
                var propType = GetTypeOfPropertyOfType(instance, forcedLookupLocation);
                if (propType == null)
                {
                    return null;
                }
                (results ??= []).Add(propType);
            }
            return GetIntersectionType(results?.ToArray());
            // Same result for both union and intersection signatures
        }
        var instanceType = GetReturnTypeOfSignature(sig);
        if (IsTypeAny(instanceType))
        {
            return instanceType;
        }
        return GetTypeOfPropertyOfType(instanceType, forcedLookupLocation);
    }

    // jsx.go:1022
    internal Type GetJsxManagedAttributesFromLocatedAttributes(Node context, Symbol? ns, Type attributesType)
    {
        var managedSym = GetJsxLibraryManagedAttributes(ns);
        if (managedSym != null)
        {
            var ctorType = GetStaticTypeOfReferencedJsxConstructor(context);
            var result = InstantiateAliasOrInterfaceWithDefaults(managedSym, new[] { ctorType, attributesType }, AstUtilities.IsInJSFile(context));
            if (result != null)
            {
                return result;
            }
        }
        return attributesType;
    }

    // jsx.go:1034
    internal Type? InstantiateAliasOrInterfaceWithDefaults(Symbol managedSym, Type[]? typeArguments, bool inJavaScript)
    {
        var declaredManagedType = GetDeclaredTypeOfSymbol(managedSym);
        // fetches interface type, or initializes symbol links type parameters
        if ((managedSym.Flags & SymbolFlags.TypeAlias) != 0)
        {
            var @params = typeAliasLinks.Get(managedSym).typeParameters;
            if (@params.Len() >= typeArguments.Len())
            {
                var args = FillMissingTypeArguments(typeArguments, @params, typeArguments.Len(), inJavaScript);
                if (args.Len() == 0)
                {
                    return declaredManagedType;
                }
                return GetTypeAliasInstantiation(managedSym, args, null);
            }
        }
        if ((declaredManagedType.objectFlags & ObjectFlags.ClassOrInterface) != 0 && (declaredManagedType.AsInterfaceType()!.TypeParameters()).Len() >= typeArguments.Len())
        {
            var args = FillMissingTypeArguments(typeArguments, declaredManagedType.AsInterfaceType()!.TypeParameters(), typeArguments.Len(), inJavaScript);
            return CreateTypeReference(declaredManagedType, args);
        }
        return null;
    }

    // jsx.go:1054
    internal Symbol? GetJsxLibraryManagedAttributes(Symbol? jsxNamespace)
    {
        if (jsxNamespace != null)
        {
            return GetSymbol(jsxNamespace.Exports, JsxNames.LibraryManagedAttributes, SymbolFlags.Type);
        }
        return null;
    }

    // jsx.go:1061
    internal Symbol? GetJsxElementTypeSymbol(Symbol? jsxNamespace)
    {
        // JSX.ElementType [symbol]
        if (jsxNamespace != null)
        {
            return GetSymbol(jsxNamespace.Exports, JsxNames.ElementType, SymbolFlags.Type);
        }
        return null;
    }

    // e.g. "props" for React.d.ts,
    // or InternalSymbolNameMissing if ElementAttributesProperty doesn't exist (which means all
    //
    //	non-intrinsic elements' attributes type is 'any'),
    //
    // or "" if it has 0 properties (which means every
    //
    //	non-intrinsic elements' attributes type is the element instance type)
    // jsx.go:1077
    internal string GetJsxElementPropertiesName(Symbol? jsxNamespace)
    {
        return GetNameFromJsxElementAttributesContainer(JsxNames.ElementAttributesPropertyNameContainer, jsxNamespace);
    }

    // jsx.go:1081
    internal string GetJsxElementChildrenPropertyName(Symbol? jsxNamespace)
    {
        if (compilerOptions.Jsx == JsxEmit.ReactJSX || compilerOptions.Jsx == JsxEmit.ReactJSXDev)
        {
            // In these JsxEmit modes the children property is fixed to 'children'
            return "children";
        }
        return GetNameFromJsxElementAttributesContainer(JsxNames.ElementChildrenAttributeNameContainer, jsxNamespace);
    }

    // Look into JSX namespace and then look for container with matching name as nameOfAttribPropContainer.
    // Get a single property from that container if existed. Report an error if there are more than one property.
    //
    // @param nameOfAttribPropContainer a string of value JsxNames.ElementAttributesPropertyNameContainer or JsxNames.ElementChildrenAttributeNameContainer
    //
    //	if other string is given or the container doesn't exist, return undefined.
    // jsx.go:1095
    internal string GetNameFromJsxElementAttributesContainer(string nameOfAttribPropContainer, Symbol? jsxNamespace)
    {
        // JSX.ElementAttributesProperty | JSX.ElementChildrenAttribute [symbol]
        if (jsxNamespace != null)
        {
            var jsxElementAttribPropInterfaceSym = GetSymbol(jsxNamespace.Exports, nameOfAttribPropContainer, SymbolFlags.Type);
            if (jsxElementAttribPropInterfaceSym != null)
            {
                var jsxElementAttribPropInterfaceType = GetDeclaredTypeOfSymbol(jsxElementAttribPropInterfaceSym);
                var propertiesOfJsxElementAttribPropInterface = GetPropertiesOfType(jsxElementAttribPropInterfaceType);
                // Element Attributes has zero properties, so the element attributes type will be the class instance type
                if (propertiesOfJsxElementAttribPropInterface.Length == 0)
                {
                    return "";
                }
                if (propertiesOfJsxElementAttribPropInterface.Length == 1)
                {
                    return propertiesOfJsxElementAttribPropInterface[0].Name;
                }
                if (propertiesOfJsxElementAttribPropInterface.Length > 1 && jsxElementAttribPropInterfaceSym.Declarations.Len() != 0)
                {
                    // More than one property on ElementAttributesProperty is an error
                    Error(jsxElementAttribPropInterfaceSym.Declarations![0], Diag.The_global_type_JSX_0_may_not_have_more_than_one_property, nameOfAttribPropContainer);
                }
            }
        }
        return InternalSymbolName.Missing;
    }

    // jsx.go:1118
    internal Type GetStaticTypeOfReferencedJsxConstructor(Node context)
    {
        if (NodeTests.IsJsxOpeningFragment(context))
        {
            return GetJSXFragmentType(context);
        }
        if (IsJsxIntrinsicTagName(context.TagName()!))
        {
            var result = GetIntrinsicAttributesTypeFromJsxOpeningLikeElement(context);
            var fakeSignature = CreateSignatureForJSXIntrinsic(context, result);
            return GetOrCreateTypeFromSignature(fakeSignature);
        }
        var tagType = CheckExpressionCached(context.TagName()!);
        if ((tagType.flags & TypeFlags.StringLiteral) != 0)
        {
            var result = GetIntrinsicAttributesTypeFromStringLiteralType(tagType, context);
            if (result == null)
            {
                return errorType!;
            }
            var fakeSignature = CreateSignatureForJSXIntrinsic(context, result);
            return GetOrCreateTypeFromSignature(fakeSignature);
        }
        return tagType;
    }

    // jsx.go:1139
    internal Type? GetIntrinsicAttributesTypeFromStringLiteralType(Type t, Node location)
    {
        // If the elemType is a stringLiteral type, we can then provide a check to make sure that the string literal type is one of the Jsx intrinsic element type
        // For example:
        //      var CustomTag: "h1" = "h1";
        //      <CustomTag> Hello World </CustomTag>
        var intrinsicElementsType = GetJsxType(JsxNames.IntrinsicElements, location);
        if (!IsErrorType(intrinsicElementsType))
        {
            var stringLiteralTypeName = GetStringLiteralValue(t);
            var intrinsicProp = GetPropertyOfType(intrinsicElementsType, stringLiteralTypeName);
            if (intrinsicProp != null)
            {
                return GetTypeOfSymbol(intrinsicProp);
            }
            var indexSignatureType = GetIndexTypeOfType(intrinsicElementsType, stringType!);
            if (indexSignatureType != null)
            {
                return indexSignatureType;
            }
            return null;
        }
        // If we need to report an error, we already done so here. So just return any to prevent any more error downstream
        return anyType;
    }

    // jsx.go:1161
    internal JsxReferenceKind GetJsxReferenceKind(Node node)
    {
        if (IsJsxIntrinsicTagName(node.TagName()!))
        {
            return JsxReferenceKind.Mixed;
        }
        var tagType = GetApparentType(CheckExpression(node.TagName()!));
        if (GetSignaturesOfType(tagType, SignatureKind.Construct).Length != 0)
        {
            return JsxReferenceKind.Component;
        }
        if (GetSignaturesOfType(tagType, SignatureKind.Call).Length != 0)
        {
            return JsxReferenceKind.Function;
        }
        return JsxReferenceKind.Mixed;
    }

    // jsx.go:1175
    internal Signature CreateSignatureForJSXIntrinsic(Node node, Type result)
    {
        var elementType = errorType!;
        var @namespace = GetJsxNamespaceAt(node);
        if (@namespace != null)
        {
            var typeSymbol = GetSymbol(GetExportsOfSymbol(@namespace), JsxNames.Element, SymbolFlags.Type);
            if (typeSymbol != null)
            {
                elementType = GetDeclaredTypeOfSymbol(typeSymbol);
            }
        }
        // returnNode := typeSymbol && c.nodeBuilder.symbolToEntityName(typeSymbol, ast.SymbolFlagsType, node)
        // declaration := factory.createFunctionTypeNode(nil, []ParameterDeclaration{factory.createParameterDeclaration(nil, nil /*dotDotDotToken*/, "props", nil /*questionToken*/, c.nodeBuilder.typeToTypeNode(result, node))}, ifElse(returnNode != nil, factory.createTypeReferenceNode(returnNode, nil /*typeArguments*/), factory.createKeywordTypeNode(ast.KindAnyKeyword)))
        var parameterSymbol = NewSymbol(SymbolFlags.FunctionScopedVariable, "props");
        valueSymbolLinks.Get(parameterSymbol).resolvedType = result;
        return NewSignature(SignatureFlags.None, null, null, null, new[] { parameterSymbol }, elementType, null, 1);
    }

    // Get attributes type of the given intrinsic opening-like Jsx element by resolving the tag name.
    // The function is intended to be called from a function which has checked that the opening element is an intrinsic element.
    // @param node an intrinsic JSX opening-like element
    // jsx.go:1192
    internal Type GetIntrinsicAttributesTypeFromJsxOpeningLikeElement(Node node)
    {
        Debug.Assert(IsJsxIntrinsicTagName(node.TagName()!));
        var links = jsxElementLinks.Get(node);
        if (links.resolvedJsxElementAttributesType != null)
        {
            return links.resolvedJsxElementAttributesType;
        }
        var symbol = GetIntrinsicTagSymbol(node);
        if ((links.jsxFlags & JsxFlags.IntrinsicNamedElement) != 0)
        {
            links.resolvedJsxElementAttributesType = CoreUtil.OrElse(GetTypeOfSymbol(symbol), errorType!);
            return links.resolvedJsxElementAttributesType;
        }
        if ((links.jsxFlags & JsxFlags.IntrinsicIndexedElement) != 0)
        {
            var indexInfo = GetApplicableIndexInfoForName(GetJsxType(JsxNames.IntrinsicElements, node), node.TagName()!.Text());
            if (indexInfo != null)
            {
                links.resolvedJsxElementAttributesType = indexInfo.valueType;
                return links.resolvedJsxElementAttributesType;
            }
        }
        links.resolvedJsxElementAttributesType = errorType;
        return links.resolvedJsxElementAttributesType!;
    }

    // Looks up an intrinsic tag name and returns a symbol that either points to an intrinsic
    // property (in which case nodeLinks.jsxFlags will be IntrinsicNamedElement) or an intrinsic
    // string index signature (in which case nodeLinks.jsxFlags will be IntrinsicIndexedElement).
    // May also return unknownSymbol if both of these lookups fail.
    // jsx.go:1218
    internal Symbol GetIntrinsicTagSymbol(Node node)
    {
        var links = symbolNodeLinks.Get(node);
        if (links.resolvedSymbol != null)
        {
            return links.resolvedSymbol;
        }
        var intrinsicElementsType = GetJsxType(JsxNames.IntrinsicElements, node);
        if (!IsErrorType(intrinsicElementsType))
        {
            // Property case
            var tagName = node.TagName()!;
            if (!NodeTests.IsIdentifier(tagName) && !NodeTests.IsJsxNamespacedName(tagName))
            {
                throw new InvalidOperationException("Invalid tag name");
            }
            var propName = tagName.Text();
            var intrinsicProp = GetPropertyOfType(intrinsicElementsType, propName);
            if (intrinsicProp != null)
            {
                jsxElementLinks.Get(node).jsxFlags |= JsxFlags.IntrinsicNamedElement;
                links.resolvedSymbol = intrinsicProp;
                return links.resolvedSymbol;
            }
            // Intrinsic string indexer case
            var indexSymbol = GetApplicableIndexSymbol(intrinsicElementsType, GetStringLiteralType(propName));
            if (indexSymbol != null)
            {
                jsxElementLinks.Get(node).jsxFlags |= JsxFlags.IntrinsicIndexedElement;
                links.resolvedSymbol = indexSymbol;
                return links.resolvedSymbol;
            }
            if (GetTypeOfPropertyOrIndexSignatureOfType(intrinsicElementsType, propName) != null)
            {
                jsxElementLinks.Get(node).jsxFlags |= JsxFlags.IntrinsicIndexedElement;
                links.resolvedSymbol = intrinsicElementsType.symbol;
                return links.resolvedSymbol!;
            }
            // Wasn't found
            Error(node, Diag.Property_0_does_not_exist_on_type_1, tagName.Text(), "JSX." + JsxNames.IntrinsicElements);
            links.resolvedSymbol = unknownSymbol;
            return links.resolvedSymbol;
        }
        if (noImplicitAny)
        {
            Error(node, Diag.JSX_element_implicitly_has_type_any_because_no_interface_JSX_0_exists, JsxNames.IntrinsicElements);
        }
        links.resolvedSymbol = unknownSymbol;
        return links.resolvedSymbol;
    }

    // jsx.go:1261
    internal Type? GetJsxStatelessElementTypeAt(Node location)
    {
        var jsxElementType = GetJsxElementTypeAt(location);
        if (jsxElementType == null)
        {
            return null;
        }
        return GetUnionType(new[] { jsxElementType, nullType! });
    }

    // jsx.go:1269
    internal Type? GetJsxElementClassTypeAt(Node location)
    {
        var t = GetJsxType(JsxNames.ElementClass, location);
        if (IsErrorType(t))
        {
            return null;
        }
        return t;
    }

    // jsx.go:1277
    internal Type? GetJsxElementTypeAt(Node location)
    {
        return GetJsxType(JsxNames.Element, location);
    }

    // jsx.go:1281
    internal Type? GetJsxElementTypeTypeAt(Node location)
    {
        var ns = GetJsxNamespaceAt(location);
        if (ns == null)
        {
            return null;
        }
        var sym = GetJsxElementTypeSymbol(ns);
        if (sym == null)
        {
            return null;
        }
        var t = InstantiateAliasOrInterfaceWithDefaults(sym, null, AstUtilities.IsInJSFile(location));
        if (t == null || IsErrorType(t))
        {
            return null;
        }
        return t;
    }

    // jsx.go:1297
    internal Type GetJsxType(string name, Node? location)
    {
        var @namespace = GetJsxNamespaceAt(location);
        if (@namespace != null)
        {
            var exports = GetExportsOfSymbol(@namespace);
            if (exports != null)
            {
                var typeSymbol = GetSymbol(exports, name, SymbolFlags.Type);
                if (typeSymbol != null)
                {
                    return GetDeclaredTypeOfSymbol(typeSymbol);
                }
            }
        }
        return errorType!;
    }

    // jsx.go:1308
    internal Symbol? GetJsxNamespaceAt(Node? location)
    {
        JsxElementLinks? links = null;
        if (location != null)
        {
            links = jsxElementLinks.Get(location);
        }
        if (links != null && links.jsxNamespace != null && links.jsxNamespace != unknownSymbol)
        {
            return links.jsxNamespace;
        }
        if (links == null || links.jsxNamespace != unknownSymbol)
        {
            var resolvedNamespace = GetJsxNamespaceContainerForImplicitImport(location);
            if (resolvedNamespace == null || resolvedNamespace == unknownSymbol)
            {
                var namespaceName = GetJsxNamespace(location);
                resolvedNamespace = ResolveName(location, namespaceName, SymbolFlags.Namespace, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
            }
            if (resolvedNamespace != null)
            {
                var candidate = ResolveSymbol(GetSymbol(GetExportsOfSymbol(ResolveSymbol(resolvedNamespace)!), JsxNames.JSX, SymbolFlags.Namespace));
                if (candidate != null && candidate != unknownSymbol)
                {
                    if (links != null)
                    {
                        links.jsxNamespace = candidate;
                    }
                    return candidate;
                }
            }
            if (links != null)
            {
                links.jsxNamespace = unknownSymbol;
            }
        }
        // JSX global fallback
        var s = ResolveSymbol(GetGlobalSymbol(JsxNames.JSX, SymbolFlags.Namespace, null /*diagnostic*/));
        if (s == unknownSymbol)
        {
            return null;
        }
        return s;
    }

    // jsx.go:1343
    internal string GetJsxNamespace(Node? location)
    {
        if (location != null)
        {
            var file = AstUtilities.GetSourceFileOfNode(location);
            if (file != null)
            {
                var links = sourceFileLinks.Get(file);
                if (NodeTests.IsJsxOpeningFragment(location))
                {
                    if (links.localJsxFragmentNamespace != "")
                    {
                        return links.localJsxFragmentNamespace;
                    }
                    var jsxFragmentPragma = GetPragmaFromSourceFile(file, "jsxfrag");
                    if (jsxFragmentPragma != null)
                    {
                        links.localJsxFragmentFactory = ParseIsolatedEntityName(jsxFragmentPragma.Value.Args["factory"].Value);
                        if (links.localJsxFragmentFactory != null)
                        {
                            links.localJsxFragmentNamespace = GetFirstIdentifier(links.localJsxFragmentFactory).Text();
                            return links.localJsxFragmentNamespace;
                        }
                    }
                    var entity = GetJsxFragmentFactoryEntity(location);
                    if (entity != null)
                    {
                        links.localJsxFragmentFactory = entity;
                        links.localJsxFragmentNamespace = GetFirstIdentifier(entity).Text();
                        return links.localJsxFragmentNamespace;
                    }
                }
                else
                {
                    var localJsxNamespace = GetLocalJsxNamespace(file);
                    if (localJsxNamespace != "")
                    {
                        links.localJsxNamespace = localJsxNamespace;
                        return links.localJsxNamespace;
                    }
                }
            }
        }
        if (jsxNamespace == "")
        {
            jsxNamespace = "React";
            if (compilerOptions.JsxFactory != "")
            {
                jsxFactoryEntity = ParseIsolatedEntityName(compilerOptions.JsxFactory);
                if (jsxFactoryEntity != null)
                {
                    jsxNamespace = GetFirstIdentifier(jsxFactoryEntity).Text();
                }
            }
            else if (compilerOptions.ReactNamespace != "")
            {
                jsxNamespace = compilerOptions.ReactNamespace;
            }
        }
        if (jsxFactoryEntity == null)
        {
            jsxFactoryEntity = factory.NewQualifiedName(factory.NewIdentifier(jsxNamespace), factory.NewIdentifier("createElement"));
        }
        return jsxNamespace;
    }

    // jsx.go:1392
    internal string GetLocalJsxNamespace(SourceFile file)
    {
        var links = sourceFileLinks.Get(file);
        if (links.localJsxNamespace != "")
        {
            return links.localJsxNamespace;
        }
        var jsxPragma = GetPragmaFromSourceFile(file, "jsx");
        if (jsxPragma != null)
        {
            links.localJsxFactory = ParseIsolatedEntityName(jsxPragma.Value.Args["factory"].Value);
            if (links.localJsxFactory != null)
            {
                links.localJsxNamespace = GetFirstIdentifier(links.localJsxFactory).Text();
                return links.localJsxNamespace;
            }
        }
        return "";
    }

    // jsx.go:1408
    internal Node? GetJsxFactoryEntity(Node? location)
    {
        if (location != null)
        {
            GetJsxNamespace(location);
            var localJsxFactory = sourceFileLinks.Get(AstUtilities.GetSourceFileOfNode(location)!).localJsxFactory;
            if (localJsxFactory != null)
            {
                return localJsxFactory;
            }
        }
        return jsxFactoryEntity;
    }

    // jsx.go:1418
    internal EntityName? GetJsxFragmentFactoryEntity(Node? location)
    {
        if (location != null)
        {
            var file = AstUtilities.GetSourceFileOfNode(location);
            if (file != null)
            {
                var links = sourceFileLinks.Get(file);
                if (links.localJsxFragmentFactory != null)
                {
                    return links.localJsxFragmentFactory;
                }
                var jsxFragPragma = GetPragmaFromSourceFile(file, "jsxfrag");
                if (jsxFragPragma != null)
                {
                    links.localJsxFragmentFactory = ParseIsolatedEntityName(jsxFragPragma.Value.Args["factory"].Value);
                    return links.localJsxFragmentFactory;
                }
            }
        }
        if (compilerOptions.JsxFragmentFactory != "")
        {
            return ParseIsolatedEntityName(compilerOptions.JsxFragmentFactory);
        }
        return null;
    }

    // jsx.go:1439
    internal Node? ParseIsolatedEntityName(string name)
    {
        var result = global::Ts.Parser.Parser.ParseIsolatedEntityName(name);
        if (result != null)
        {
            MarkAsSynthetic(result);
        }
        return result;
    }

    // jsx.go:1447
    private static bool MarkAsSynthetic(Node node)
    {
        node.Loc = new TextRange(-1, -1);
        node.ForEachChild(MarkAsSynthetic);
        return false;
    }

    // jsx.go:1453
    internal Symbol? GetJsxNamespaceContainerForImplicitImport(Node? location)
    {
        SourceFile? file = null;
        JsxElementLinks? links = null;
        if (location != null)
        {
            if ((file = AstUtilities.GetSourceFileOfNode(location)) != null)
            {
                links = jsxElementLinks.Get(file.AsNode());
            }
        }
        if (links != null && links.jsxImplicitImportContainer != null)
        {
            return CoreUtil.IfElse(links.jsxImplicitImportContainer == unknownSymbol, null, links.jsxImplicitImportContainer);
        }
        var (moduleReference, specifier) = GetJSXRuntimeImportSpecifier(file);
        if (moduleReference == "")
        {
            return null;
        }
        var errorMessage = Diag.This_JSX_tag_requires_the_module_path_0_to_exist_but_none_could_be_found_Make_sure_you_have_types_for_the_appropriate_package_installed;
        var mod = ResolveExternalModule(CoreUtil.OrElse(specifier, location), moduleReference, errorMessage, location, false);
        Symbol? result = null;
        if (mod != null && mod != unknownSymbol)
        {
            result = GetMergedSymbol(ResolveSymbol(mod));
        }
        if (links != null)
        {
            links.jsxImplicitImportContainer = CoreUtil.OrElse(result, unknownSymbol);
        }
        return result;
    }

    // jsx.go:1480
    internal (string moduleReference, Node? specifier) GetJSXRuntimeImportSpecifier(SourceFile? file)
    {
        return program.GetJSXRuntimeImportSpecifier(file!.Path());
    }
}

// jsx.go:367-372
internal struct JsxElaborationElement
{
    internal Node? errorNode;
    internal Node? innerExpression;
    internal Type? nameType;
    internal Func<Node, Diagnostic>? createDiagnostic; // Optional: creates a custom diagnostic for this element
}

// checker.go:30470-30510 — ObjectLiteralDiscriminator. Owned by the contextual-typing wave;
// landed here first because discriminateContextualTypeByJSXAttributes (jsx.go:264) is the first
// consumer to be ported. discriminateContextualTypeByObjectMembers (checker.go:30512) is the other.
internal sealed class ObjectLiteralDiscriminator : IDiscriminator
{
    private readonly Checker c;
    private readonly Node[] props;
    private readonly Symbol[] members;

    internal ObjectLiteralDiscriminator(Checker c, Node[] props, Symbol[] members)
    {
        this.c = c;
        this.props = props;
        this.members = members;
    }

    public int Len() => props.Length + members.Length;

    public string Name(int index)
    {
        if (index < props.Length)
        {
            return props[index].Symbol()!.Name;
        }
        return members[index - props.Length].Name;
    }

    public bool Matches(int index, Type t)
    {
        Type propType;
        if (index < props.Length)
        {
            var prop = props[index];
            if (NodeTests.IsPropertyAssignment(prop) || NodeTests.IsJsxAttribute(prop))
            {
                var initializer = prop.Initializer();
                if (initializer != null)
                {
                    propType = c.GetContextFreeTypeOfExpression(prop.Initializer()!);
                }
                else
                {
                    propType = c.trueType!; // JsxAttribute without initializer is always true
                }
            }
            else
            {
                propType = c.GetContextFreeTypeOfExpression(prop.Name()!);
            }
        }
        else
        {
            propType = c.undefinedType!;
        }
        foreach (var s in propType.Distributed())
        {
            if (c.IsTypeAssignableTo(s, t))
            {
                return true;
            }
        }
        return false;
    }
}
