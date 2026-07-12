// Ported from tsgo internal/checker/checker.go (checkIdentifier 11014 + its co-located control-flow /
// narrowing helpers: getControlFlowContainer 11407, isInPropertyInitializerOrClassStaticBlock 13638,
// getNarrowedTypeOfSymbol 13655, isSameScopedBindingElement 11174, removeOptionalityFromDeclaredType 11181),
// plan/03 §4.2 #7. Faithful per-function translation: same function order, same branch structure; Go
// comments and `// !!!` markers preserved.
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified via the Diag alias;
// the no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used directly. The parameter-only leaves
// (parameterInitializerContainsUndefined) and deprecated/deep-narrow branches route through cross-cluster
// stubs (isUncalledFunctionReference/isDeprecatedSymbol/isSomeSymbolAssigned/isConstraintPosition), reached
// only on those specific paths.

using System.Linq;
using Ts.Ast;
using Ts.Core;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:11014
    internal Type CheckIdentifier(Node node, CheckMode checkMode)
    {
        if (AstUtilities.IsThisInTypeQuery(node))
        {
            return CheckThisExpression(node);
        }
        var symbol = GetResolvedSymbol(node);
        if (symbol == unknownSymbol)
        {
            return errorType!;
        }
        if (symbol == argumentsSymbol)
        {
            if (IsInPropertyInitializerOrClassStaticBlock(node, true /*ignoreArrowFunctions*/))
            {
                Error(node, Diag.X_arguments_cannot_be_referenced_in_property_initializers_or_class_static_initialization_blocks);
                return errorType!;
            }
            return GetTypeOfSymbol(symbol!);
        }
        if (ShouldMarkIdentifierAliasReferenced(node))
        {
            MarkLinkedReferences(node, ReferenceHint.Identifier, null /*propSymbol*/, null /*parentType*/);
        }
        var localOrExportSymbol = GetExportSymbolOfValueSymbolIfExported(symbol)!;
        var targetSymbol = ResolveAliasWithDeprecationCheck(localOrExportSymbol, node);
        if (targetSymbol.Declarations.Len() != 0 && IsDeprecatedSymbol(targetSymbol) && IsUncalledFunctionReference(node, targetSymbol))
        {
            AddDeprecatedSuggestion(node, targetSymbol.Declarations!, node.Text());
        }
        var declaration = localOrExportSymbol.ValueDeclaration;
        var immediateDeclaration = declaration;
        // If the identifier is declared in a binding pattern for which we're currently computing the implied type and the
        // reference occurs with the same binding pattern, return the non-inferrable any type. This for example occurs in
        // 'const [a, b = a + 1] = [2]' when we're computing the contextual type for the array literal '[2]'.
        if (declaration != null && declaration.Kind == SyntaxKind.BindingElement && contextualBindingPatterns.OrEmpty().Contains(declaration.Parent) &&
            AstUtilities.FindAncestor(node, parent => parent == declaration.Parent) != null)
        {
            return nonInferrableAnyType!;
        }
        var t = GetNarrowedTypeOfSymbol(localOrExportSymbol, node);
        var assignmentKind = GetAssignmentTargetKind(node);
        if (assignmentKind != AssignmentKind.None)
        {
            if ((localOrExportSymbol.Flags & SymbolFlags.Variable) == 0 && !(AstUtilities.IsInJSFile(node) && (localOrExportSymbol.Flags & SymbolFlags.ValueModule) != 0))
            {
                global::Ts.Diagnostics.Message assignmentError;
                if ((localOrExportSymbol.Flags & SymbolFlags.Enum) != 0)
                {
                    assignmentError = Diag.Cannot_assign_to_0_because_it_is_an_enum;
                }
                else if ((localOrExportSymbol.Flags & SymbolFlags.Class) != 0)
                {
                    assignmentError = Diag.Cannot_assign_to_0_because_it_is_a_class;
                }
                else if ((localOrExportSymbol.Flags & SymbolFlags.Module) != 0)
                {
                    assignmentError = Diag.Cannot_assign_to_0_because_it_is_a_namespace;
                }
                else if ((localOrExportSymbol.Flags & SymbolFlags.Function) != 0)
                {
                    assignmentError = Diag.Cannot_assign_to_0_because_it_is_a_function;
                }
                else if ((localOrExportSymbol.Flags & SymbolFlags.Alias) != 0)
                {
                    assignmentError = Diag.Cannot_assign_to_0_because_it_is_an_import;
                }
                else
                {
                    assignmentError = Diag.Cannot_assign_to_0_because_it_is_not_a_variable;
                }
                Error(node, assignmentError, SymbolToString(symbol));
                return errorType!;
            }
            if (IsReadonlySymbol(localOrExportSymbol))
            {
                if ((localOrExportSymbol.Flags & SymbolFlags.Variable) != 0)
                {
                    Error(node, Diag.Cannot_assign_to_0_because_it_is_a_constant, SymbolToString(symbol));
                }
                else
                {
                    Error(node, Diag.Cannot_assign_to_0_because_it_is_a_read_only_property, SymbolToString(symbol));
                }
                return errorType!;
            }
        }
        bool isAlias = (localOrExportSymbol.Flags & SymbolFlags.Alias) != 0;
        // We only narrow variables and parameters occurring in a non-assignment position. For all other
        // entities we simply return the declared type.
        if ((localOrExportSymbol.Flags & SymbolFlags.Variable) != 0)
        {
            if (assignmentKind == AssignmentKind.Definite)
            {
                if (IsInCompoundLikeAssignment(node))
                {
                    return GetBaseTypeOfLiteralType(t);
                }
                return t;
            }
        }
        else if (isAlias)
        {
            declaration = GetDeclarationOfAliasSymbol(symbol!);
        }
        else
        {
            return t;
        }
        if (declaration == null)
        {
            return t;
        }
        t = GetNarrowableTypeForReference(t, node, checkMode);
        // The declaration container is the innermost function that encloses the declaration of the variable
        // or parameter. The flow container is the innermost function starting with which we analyze the control
        // flow graph to determine the control flow based type.
        bool isParameter = AstUtilities.GetRootDeclaration(declaration).Kind == SyntaxKind.Parameter;
        var declarationContainer = GetControlFlowContainer(declaration);
        var flowContainer = GetControlFlowContainer(node);
        bool isOuterVariable = flowContainer != declarationContainer;
        bool isSpreadDestructuringAssignmentTarget = node.Parent != null && node.Parent.Parent != null && NodeTests.IsSpreadAssignment(node.Parent) && IsDestructuringAssignmentTarget(node.Parent.Parent);
        bool isModuleExports = (symbol!.Flags & SymbolFlags.ModuleExports) != 0;
        bool typeIsAutomatic = t == autoType || t == autoArrayType;
        bool isAutomaticTypeInNonNull = typeIsAutomatic && node.Parent!.Kind == SyntaxKind.NonNullExpression;
        // When the control flow originates in a function expression, arrow function, method, or accessor, and
        // we are referencing a closed-over const variable or parameter or mutable local variable past its last
        // assignment, we extend the origin of the control flow analysis to include the immediately enclosing
        // control flow container.
        while (flowContainer != declarationContainer &&
            (AstUtilities.IsFunctionExpressionOrArrowFunction(flowContainer!) || AstUtilities.IsObjectLiteralOrClassExpressionMethodOrAccessor(flowContainer!)) &&
            (IsConstantVariable(localOrExportSymbol) && t != autoArrayType || IsParameterOrMutableLocalVariable(localOrExportSymbol) && IsPastLastAssignment(localOrExportSymbol, node)))
        {
            flowContainer = GetControlFlowContainer(flowContainer!);
        }
        // We only look for uninitialized variables in strict null checking mode, and only when we can analyze
        // the entire control flow graph from the variable's declaration (i.e. when the flow container and
        // declaration container are the same).
        bool isNeverInitialized = immediateDeclaration != null && NodeTests.IsVariableDeclaration(immediateDeclaration) && !AstUtilities.IsForInOrOfStatement(immediateDeclaration.Parent!.Parent) &&
            immediateDeclaration.Initializer() == null && immediateDeclaration.AsVariableDeclaration().ExclamationToken == null &&
            IsMutableLocalVariableDeclaration(immediateDeclaration) && !IsSymbolAssignedDefinitely(symbol);
        bool assumeInitialized = isParameter ||
            isAlias ||
            (isOuterVariable && !isNeverInitialized) ||
            isSpreadDestructuringAssignmentTarget ||
            isModuleExports ||
            IsSameScopedBindingElement(node, declaration) ||
            t != autoType && t != autoArrayType && (!strictNullChecks || (t.flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void)) != 0 || IsInTypeQuery(node) || IsInAmbientOrTypeNode(node) || node.Parent!.Kind == SyntaxKind.ExportSpecifier) ||
            NodeTests.IsNonNullExpression(node.Parent!) ||
            NodeTests.IsVariableDeclaration(declaration) && declaration.AsVariableDeclaration().ExclamationToken != null ||
            (declaration.Flags & NodeFlags.Ambient) != 0;
        Type initialType;
        if (isAutomaticTypeInNonNull)
        {
            initialType = undefinedType!;
        }
        else if (assumeInitialized && isParameter)
        {
            initialType = RemoveOptionalityFromDeclaredType(t, declaration);
        }
        else if (assumeInitialized)
        {
            initialType = t;
        }
        else if (typeIsAutomatic)
        {
            initialType = undefinedType!;
        }
        else
        {
            initialType = GetOptionalType(t, false /*isProperty*/);
        }
        Type flowType;
        if (isAutomaticTypeInNonNull)
        {
            flowType = GetNonNullableType(GetFlowTypeOfReferenceEx(node, t, initialType, flowContainer, null));
        }
        else
        {
            flowType = GetFlowTypeOfReferenceEx(node, t, initialType, flowContainer, null);
        }
        // A variable is considered uninitialized when it is possible to analyze the entire control flow graph
        // from declaration to use, and when the variable's declared type doesn't include undefined but the
        // control flow based type does include undefined.
        if (!IsEvolvingArrayOperationTarget(node) && (t == autoType || t == autoArrayType))
        {
            if (flowType == autoType || flowType == autoArrayType)
            {
                if (noImplicitAny)
                {
                    Error(AstUtilities.GetNameOfDeclaration(declaration), Diag.Variable_0_implicitly_has_type_1_in_some_locations_where_its_type_cannot_be_determined, SymbolToString(symbol), TypeToString(flowType));
                    Error(node, Diag.Variable_0_implicitly_has_an_1_type, SymbolToString(symbol), TypeToString(flowType));
                }
                return ConvertAutoToAny(flowType);
            }
        }
        else if (!assumeInitialized && !ContainsUndefinedType(this, t) && ContainsUndefinedType(this, flowType))
        {
            Error(node, Diag.Variable_0_is_used_before_being_assigned, SymbolToString(symbol));
            // Return the declared type to reduce follow-on errors
            return t;
        }
        if (assignmentKind != AssignmentKind.None)
        {
            // Identifier is target of a compound assignment
            return GetBaseTypeOfLiteralType(flowType);
        }
        return flowType;
    }

    // checker.go:11174
    internal bool IsSameScopedBindingElement(Node node, Node declaration)
    {
        if (NodeTests.IsBindingElement(declaration))
        {
            var bindingElement = AstUtilities.FindAncestor(node, NodeTests.IsBindingElement);
            return bindingElement != null && AstUtilities.GetRootDeclaration(bindingElement) == AstUtilities.GetRootDeclaration(declaration);
        }
        return false;
    }

    // checker.go:11181 — Remove undefined from the annotated type of a parameter when there is an
    // initializer (that doesn't include undefined)
    internal Type RemoveOptionalityFromDeclaredType(Type declaredType, Node declaration)
    {
        bool removeUndefined = strictNullChecks && NodeTests.IsParameterDeclaration(declaration) && declaration.Initializer() != null && HasTypeFacts(declaredType, TypeFacts.IsUndefined) && !ParameterInitializerContainsUndefined(declaration);
        if (removeUndefined)
        {
            return GetTypeWithFacts(declaredType, TypeFacts.NEUndefined);
        }
        return declaredType;
    }

    // checker.go:11196 — parameter-initializer undefined probe; deferred (owning wave:
    // pushTypeResolution/reportCircularityError/popTypeResolution + NodeCheckFlags.InitializerIsUndefined
    // bookkeeping). Reached only via removeOptionalityFromDeclaredType on a parameter with an initializer.
    internal bool ParameterInitializerContainsUndefined(Node declaration) => throw new NotImplementedException("parameterInitializerContainsUndefined");

    // checker.go:11662 — uncalled deprecated function-reference probe; deferred (owning wave:
    // hasMatchingArgument). Reached only for deprecated function/method symbols.
    internal bool IsUncalledFunctionReference(Node node, Symbol symbol) => throw new NotImplementedException("isUncalledFunctionReference");

    // checker.go:11407
    internal Node? GetControlFlowContainer(Node node)
    {
        return AstUtilities.FindAncestor(node.Parent, static n =>
            AstUtilities.IsFunctionLike(n) && AstUtilities.GetImmediatelyInvokedFunctionExpression(n) == null || NodeTests.IsModuleBlock(n) || NodeTests.IsSourceFile(n) || NodeTests.IsPropertyDeclaration(n));
    }

    // checker.go:13638
    internal bool IsInPropertyInitializerOrClassStaticBlock(Node node, bool ignoreArrowFunctions)
    {
        return AstUtilities.FindAncestorOrQuit(node, n =>
        {
            switch (n.Kind)
            {
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.ClassStaticBlockDeclaration:
                    return FindAncestorResult.True;
                case SyntaxKind.TypeQuery:
                case SyntaxKind.JsxClosingElement:
                    return FindAncestorResult.Quit;
                case SyntaxKind.ArrowFunction:
                    return ignoreArrowFunctions ? FindAncestorResult.False : FindAncestorResult.Quit;
                case SyntaxKind.Block:
                    return AstUtilities.IsFunctionLikeDeclaration(n.Parent!) && n.Parent!.Kind != SyntaxKind.ArrowFunction ? FindAncestorResult.Quit : FindAncestorResult.False;
                default:
                    return FindAncestorResult.False;
            }
        }) != null;
    }

    // checker.go:13655
    internal Type GetNarrowedTypeOfSymbol(Symbol symbol, Node location)
    {
        var t = GetTypeOfSymbol(symbol);
        var declaration = symbol.ValueDeclaration;
        if (declaration != null)
        {
            // If we have a non-rest binding element with no initializer declared as a const variable or a const-like
            // parameter (a parameter for which there are no assignments in the function body), and if the parent type
            // for the destructuring is a union type, one or more of the binding elements may represent discriminant
            // properties, and we want the effects of conditional checks on such discriminants to affect the types of
            // other binding elements from the same destructuring.
            if (NodeTests.IsBindingElement(declaration) && declaration.Initializer() == null && !HasDotDotDotToken(declaration) && declaration.Parent!.Elements().Len() >= 2)
            {
                var parent = declaration.Parent!.Parent!;
                var rootDeclaration = AstUtilities.GetRootDeclaration(parent);
                if (NodeTests.IsVariableDeclaration(rootDeclaration) && (GetCombinedNodeFlagsCached(rootDeclaration) & NodeFlags.Constant) != 0 || NodeTests.IsParameterDeclaration(rootDeclaration))
                {
                    var links = nodeLinks.Get(parent);
                    if ((links.flags & NodeCheckFlags.InCheckIdentifier) == 0)
                    {
                        links.flags |= NodeCheckFlags.InCheckIdentifier;
                        var parentType = GetTypeForBindingElementParent(parent, CheckMode.Normal);
                        Type? parentTypeConstraint = null;
                        if (parentType != null)
                        {
                            parentTypeConstraint = MapType(parentType, GetBaseConstraintOrType);
                        }
                        if (parentTypeConstraint != null && (parentTypeConstraint.flags & TypeFlags.Union) != 0 && !(NodeTests.IsParameterDeclaration(rootDeclaration) && IsSomeSymbolAssigned(rootDeclaration)))
                        {
                            var pattern = declaration.Parent;
                            var narrowedType = GetFlowTypeOfReferenceEx(pattern, parentTypeConstraint, parentTypeConstraint, null /*flowContainer*/, GetFlowNodeOfNode(location));
                            if ((narrowedType.flags & TypeFlags.Never) != 0)
                            {
                                t = neverType!;
                            }
                            else
                            {
                                // Destructurings are validated against the parent type elsewhere. Here we disable tuple bounds
                                // checks because the narrowed type may have lower arity than the full parent type.
                                t = GetBindingElementTypeFromParentType(declaration, narrowedType, true /*noTupleBoundsCheck*/);
                            }
                        }
                        links.flags &= ~NodeCheckFlags.InCheckIdentifier;
                    }
                }
            }
            // If we have a const-like parameter with no type annotation or initializer, and if the parameter is contextually
            // typed by a signature with a single rest parameter of a union of tuple types, one or more of the parameters may
            // represent discriminant tuple elements, and we want the effects of conditional checks on such discriminants to
            // affect the types of other parameters in the same parameter list.
            else if (NodeTests.IsParameterDeclaration(declaration) && declaration.Type() == null && declaration.Initializer() == null && !HasDotDotDotToken(declaration))
            {
                var fn = declaration.Parent!;
                if (fn.Parameters().Length >= 2 && IsContextSensitiveFunctionOrObjectLiteralMethod(fn))
                {
                    var contextualSignature = GetContextualSignature(fn);
                    if (contextualSignature != null && contextualSignature.parameters.Len() == 1 && SignatureHasRestParameter(contextualSignature))
                    {
                        var context = GetInferenceContext(fn);
                        TypeMapper? mapper = null;
                        if (context != null)
                        {
                            // We clone the inference context to avoid disturbing a resolution in progress.
                            var inferenceContext = CloneInferenceContext(context, InferenceFlags.None)!;
                            var inferenceTargetType = GetReturnTypeOfSignature(contextualSignature);
                            InferTypes(inferenceContext.inferences, GetReturnTypeOfSignature(GetSignatureFromDeclaration(fn)), inferenceTargetType, InferencePriority.ReturnType, false);
                            mapper = inferenceContext.nonFixingMapper;
                        }
                        var restType = GetReducedApparentType(InstantiateType(GetTypeOfSymbol(contextualSignature.parameters![0]), mapper));
                        if ((restType.flags & TypeFlags.Union) != 0 && EveryType(restType, IsTupleType) && !CoreUtil.Some(fn.Parameters(), IsSomeSymbolAssigned))
                        {
                            var narrowedType = GetFlowTypeOfReferenceEx(fn, restType, restType, null /*flowContainer*/, GetFlowNodeOfNode(location));
                            int index = System.Array.IndexOf(fn.Parameters(), declaration) - (AstUtilities.GetThisParameter(fn) != null ? 1 : 0);
                            t = GetIndexedAccessType(narrowedType, GetNumberLiteralType(new global::Ts.JsNum.Number(index)));
                        }
                    }
                }
            }
        }
        return t;
    }
}
