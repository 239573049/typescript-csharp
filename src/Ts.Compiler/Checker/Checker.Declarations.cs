// Ported from tsgo internal/checker/checker.go (complex declaration / member / signature / type-node
// validators, all within the checker.go:2182-7016 range, plan/03 §4.2 #5). This is the
// COMPLEX-DECLARATION long-tail wave: the check* validators for class/interface/enum/module/
// import-export declarations plus signature/member/parameter/property/accessor/type-parameter and the
// type-node (checkTypeReferenceNode..checkMappedType) checks that were deferred as
// NotImplementedException stubs in Checker.Statements.Deps.cs.
//
// BREADTH-FIRST (task mandate): the reachable trunk of each validator is translated faithfully,
// same function order / same branch structure as Go, Go comments and `// !!!` markers preserved. A
// handful of validators pull in large not-yet-landed subsystems (full heritage/override-conflict
// analysis, index-constraint checks, enum-member value computation display, deferred JSDoc-parameter
// matching, iteration-types family). For those the trunk is kept and the deep helper is single-dedup
// stubbed in Checker.Declarations.Deps.cs (or already stubbed elsewhere in the assembly) — never
// re-implemented here.
//
// §1.5: diagnostics via `using Diag = ...`; cross-package classes shadowed by sibling namespaces are
// `global::`-qualified where referenced; the no-collision hosts (NodeTests/AstUtilities/CoreUtil) and
// the generated NodeTests guards are used directly, as in relater/inference/flow/statements.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // ======================================================================================
    // Type-parameter checks
    // ======================================================================================

    // checker.go:2591
    internal void CheckTypeParameter(Node node)
    {
        // Grammar Checking
        CheckGrammarModifiers(node);
        var tpNode = node.AsTypeParameterDeclaration();
        if (tpNode.Expression is { } expr)
        {
            GrammarErrorOnFirstToken(expr, Diag.Type_expected);
        }
        CheckSourceElement(tpNode.Constraint);
        CheckSourceElement(tpNode.DefaultType);
        var typeParameter = GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(node)!);
        // Resolve base constraint to reveal circularity errors
        GetBaseConstraintOfType(typeParameter);
        if (GetResolvedTypeParameterDefault(typeParameter) == circularConstraintType)
        {
            Error(tpNode.DefaultType, Diag.Type_parameter_0_has_a_circular_default, TypeToString(typeParameter));
        }
        var constraintType = GetConstraintOfTypeParameter(typeParameter);
        var defaultType = GetDefaultFromTypeParameter(typeParameter);
        if (constraintType != null && defaultType != null)
        {
            CheckTypeAssignableTo(defaultType, GetTypeWithThisArgument(InstantiateType(constraintType, TypeMapper.NewSimpleTypeMapper(typeParameter, defaultType)), defaultType, false), tpNode.DefaultType, Diag.Type_0_does_not_satisfy_the_constraint_1);
        }
        CheckTypeNameIsReserved(node.Name()!, Diag.Type_parameter_name_cannot_be_0);
        CheckNodeDeferred(node);
    }

    // checker.go:6979
    internal void CheckTypeParameters(Node[]? typeParameterDeclarations)
    {
        var seenDefault = false;
        var decls = typeParameterDeclarations.OrEmpty();
        for (var i = 0; i < decls.Length; i++)
        {
            var node = decls[i];
            CheckTypeParameter(node);
            var defaultTypeNode = node.AsTypeParameterDeclaration().DefaultType;
            if (defaultTypeNode != null)
            {
                seenDefault = true;
                CheckTypeParametersNotReferenced(defaultTypeNode, decls, i);
            }
            else if (seenDefault)
            {
                Error(node, Diag.Required_type_parameters_may_not_follow_optional_type_parameters);
            }
            for (var j = 0; j < i; j++)
            {
                if (decls[j].Symbol() == node.Symbol())
                {
                    Error(node.Name(), Diag.Duplicate_identifier_0, global::Ts.Scanner.Scanner.DeclarationNameToString(node.Name()));
                }
            }
        }
    }

    // ======================================================================================
    // Parameter / property checks
    // ======================================================================================

    // checker.go:2642
    internal void CheckParameter(Node node)
    {
        // Grammar checking
        // It is a SyntaxError if the Identifier "eval" or the Identifier "arguments" occurs as the
        // Identifier in a PropertySetParameterList of a PropertyAssignment that is contained in strict code
        // or if its FunctionBody is strict code(11.1.5).
        CheckGrammarModifiers(node);
        CheckVariableLikeDeclaration(node);
        var fn = GetContainingFunction(node)!;
        string? paramName = null;
        if (node.Name() != null && NodeTests.IsIdentifier(node.Name()!))
        {
            paramName = node.Name()!.Text();
        }
        if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.ParameterPropertyModifier))
        {
            if (ShouldCheckErasableSyntax(node))
            {
                Error(node, Diag.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled);
            }
            if (!(NodeTests.IsConstructorDeclaration(fn) && AstUtilities.NodeIsPresent(fn.Body())))
            {
                Error(node, Diag.A_parameter_property_is_only_allowed_in_a_constructor_implementation);
            }
            if (NodeTests.IsConstructorDeclaration(fn) && paramName == "constructor")
            {
                Error(node.Name(), Diag.X_constructor_cannot_be_used_as_a_parameter_property_name);
            }
        }
        if (node.Initializer() == null && IsOptionalDeclaration(node) && AstUtilities.IsBindingPattern(node.Name()) && fn.Body() != null)
        {
            Error(node, Diag.A_binding_pattern_parameter_cannot_be_optional_in_an_implementation_signature);
        }
        if (paramName is "this" or "new")
        {
            if (Array.IndexOf(fn.Parameters(), node) != 0)
            {
                Error(node, Diag.A_0_parameter_must_be_the_first_parameter, paramName);
            }
            if (NodeTests.IsConstructorDeclaration(fn) || NodeTests.IsConstructSignatureDeclaration(fn) || NodeTests.IsConstructorTypeNode(fn))
            {
                Error(node, Diag.A_constructor_cannot_have_a_this_parameter);
            }
            if (NodeTests.IsArrowFunction(fn))
            {
                Error(node, Diag.An_arrow_function_cannot_have_a_this_parameter);
            }
            if (AstUtilities.IsAccessor(fn))
            {
                Error(node, Diag.X_get_and_set_accessors_cannot_declare_this_parameters);
            }
        }
        // Only check rest parameter type if it's not a binding pattern. Since binding patterns are
        // not allowed in a rest parameter, we already have an error from checkGrammarParameterList.
        if (HasDotDotDotToken(node) && !AstUtilities.IsBindingPattern(node.Name()) && !IsTypeAssignableTo(GetReducedType(GetTypeOfSymbol(node.Symbol()!)), anyReadonlyArrayType!))
        {
            Error(node, Diag.A_rest_parameter_must_be_of_an_array_type);
        }
    }

    // checker.go:2689
    internal void CheckPropertyDeclaration(Node node)
    {
        // Grammar checking
        if (!CheckGrammarModifiers(node) && !CheckGrammarProperty(node))
        {
            CheckGrammarComputedPropertyName(node.Name()!);
        }
        CheckVariableLikeDeclaration(node);
        SetNodeLinksForPrivateIdentifierScope(node);
        // property signatures already report "initializer not allowed in ambient context" elsewhere
        if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Abstract) && NodeTests.IsPropertyDeclaration(node))
        {
            if (node.Initializer() != null)
            {
                Error(node, Diag.Property_0_cannot_have_an_initializer_because_it_is_marked_abstract, global::Ts.Scanner.Scanner.DeclarationNameToString(node.Name()));
            }
        }
    }

    // checker.go:2704
    internal void CheckPropertySignature(Node node)
    {
        if (NodeTests.IsPrivateIdentifier(node.AsPropertySignatureDeclaration().Name()!))
        {
            Error(node, Diag.Private_identifiers_are_not_allowed_outside_class_bodies);
        }
        CheckPropertyDeclaration(node);
    }

    // ======================================================================================
    // Signature / method / constructor / accessor / static-block checks
    // ======================================================================================

    // checker.go:2711
    internal void CheckSignatureDeclaration(Node node)
    {
        // Grammar checking
        switch (node.Kind)
        {
            case SyntaxKind.IndexSignature:
                CheckGrammarIndexSignature(node.AsIndexSignatureDeclaration());
                break;
            case SyntaxKind.FunctionType:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.CallSignature:
            case SyntaxKind.Constructor:
            case SyntaxKind.ConstructSignature:
                CheckGrammarFunctionLikeDeclaration(node);
                break;
        }
        var functionFlags = AstUtilities.GetFunctionFlags(node);
        if ((functionFlags & FunctionFlags.Invalid) == 0)
        {
            // Async generators prior to ES2018 require the __await and __asyncGenerator helpers
            if ((functionFlags & FunctionFlags.AsyncGenerator) == FunctionFlags.AsyncGenerator &&
                languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.AsyncGenerators)
            {
                CheckExternalEmitHelpers(node, ExternalEmitHelpers.AsyncGeneratorIncludes);
            }
            if ((functionFlags & FunctionFlags.AsyncGenerator) == FunctionFlags.Async && languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.AsyncFunctions)
            {
                CheckExternalEmitHelpers(node, ExternalEmitHelpers.Awaiter);
            }
        }
        CheckTypeParameters(node.TypeParameters());
        CheckUnmatchedJSDocParameters(node);
        CheckSourceElements(node.Parameters()!);
        var returnTypeNode = node.Type();
        if (returnTypeNode != null)
        {
            CheckSourceElement(returnTypeNode);
        }
        if (noImplicitAny && returnTypeNode == null)
        {
            switch (node.Kind)
            {
                case SyntaxKind.ConstructSignature:
                    Error(node, Diag.Construct_signature_which_lacks_return_type_annotation_implicitly_has_an_any_return_type);
                    break;
                case SyntaxKind.CallSignature:
                    Error(node, Diag.Call_signature_which_lacks_return_type_annotation_implicitly_has_an_any_return_type);
                    break;
            }
        }
        if (returnTypeNode != null)
        {
            if ((functionFlags & (FunctionFlags.Invalid | FunctionFlags.Generator)) == FunctionFlags.Generator)
            {
                var returnType = GetTypeFromTypeNode(returnTypeNode);
                if (returnType == voidType)
                {
                    Error(returnTypeNode, Diag.A_generator_cannot_have_a_void_type_annotation);
                }
                else
                {
                    CheckGeneratorInstantiationAssignabilityToReturnType(returnType, functionFlags, returnTypeNode);
                }
            }
            else if ((functionFlags & FunctionFlags.AsyncGenerator) == FunctionFlags.Async)
            {
                CheckAsyncFunctionReturnType(node, returnTypeNode);
            }
        }
        if (!NodeTests.IsIndexSignatureDeclaration(node))
        {
            RegisterForUnusedIdentifiersCheck(node);
        }
    }

    // checker.go:2785
    internal void CheckMethodDeclaration(Node node)
    {
        // Grammar checking
        if (!CheckGrammarMethod(node))
        {
            CheckGrammarComputedPropertyName(node.Name()!);
            if (NodeTests.IsMethodDeclaration(node) && node.AsMethodDeclaration().AsteriskToken != null && NodeTests.IsIdentifier(node.Name()!) && node.Name()!.Text() == "constructor")
            {
                Error(node.Name(), Diag.Class_constructor_may_not_be_a_generator);
            }
        }
        // Grammar checking for modifiers is done inside the function checkGrammarFunctionLikeDeclaration
        CheckFunctionOrMethodDeclaration(node);
        // method signatures already report "implementation not allowed in ambient context" elsewhere
        if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Abstract) && NodeTests.IsMethodDeclaration(node) && node.Body() != null)
        {
            Error(node, Diag.Method_0_cannot_have_an_implementation_because_it_is_marked_abstract, global::Ts.Scanner.Scanner.DeclarationNameToString(node.Name()));
        }
        // Private named methods are only allowed in class declarations
        if (NodeTests.IsPrivateIdentifier(node.Name()!) && AstUtilities.GetContainingClass(node) == null)
        {
            Error(node, Diag.Private_identifiers_are_not_allowed_outside_class_bodies);
        }
        SetNodeLinksForPrivateIdentifierScope(node);
    }

    // checker.go:2806
    internal void CheckClassStaticBlockDeclaration(Node node)
    {
        // Grammar checking
        CheckGrammarModifiers(node);
        node.ForEachChild(CheckSourceElement);
    }

    // checker.go:2812
    internal void CheckConstructorDeclaration(Node node)
    {
        // Grammar check on signature of constructor and modifier of the constructor is done in checkSignatureDeclaration function.
        CheckSignatureDeclaration(node);
        // Grammar check for checking only related to constructorDeclaration
        var ctor = node.AsConstructorDeclaration();
        if (!CheckGrammarConstructorTypeParameters(ctor))
        {
            CheckGrammarConstructorTypeAnnotation(ctor);
        }
        CheckSourceElement(node.Body());
        var symbol = GetSymbolOfDeclaration(node)!;
        CheckFunctionOrConstructorSymbol(symbol);
        // exit early in the case of signature - super checks are not relevant to them
        if (AstUtilities.NodeIsMissing(node.Body()))
        {
            return;
        }
        // TS 1.0 spec (April 2014): 8.3.2
        // Constructors of classes with no extends clause may not contain super calls, whereas
        // constructors of derived classes must contain at least one super call somewhere in their function body.
        var containingClassDecl = node.Parent!;
        if (AstUtilities.GetExtendsHeritageClauseElement(containingClassDecl) == null)
        {
            return;
        }
        var classExtendsNull = ClassDeclarationExtendsNull(containingClassDecl);
        var superCall = FindFirstSuperCall(node.Body()!);
        if (superCall != null)
        {
            if (classExtendsNull)
            {
                Error(superCall, Diag.A_constructor_cannot_contain_a_super_call_when_its_class_extends_null);
            }
            // A super call must be root-level in a constructor if both of the following are true:
            // - The containing class is a derived class.
            // - The constructor declares parameter properties
            //   or the containing class declares instance member variables with initializers.
            var superCallShouldBeRootLevel = !emitStandardClassFields &&
                (node.Parent!.Members().OrEmpty().Any(IsInstancePropertyWithInitializerOrPrivateIdentifierProperty) ||
                    node.Parameters().OrEmpty().Any(p => AstUtilities.HasSyntacticModifier(p, ModifierFlags.ParameterPropertyModifier)));
            if (superCallShouldBeRootLevel)
            {
                // Until we have better flow analysis, it is an error to place the super call within any kind of block or conditional
                // See GH #8277
                if (!SuperCallIsRootLevelInConstructor(superCall, node.Body()!))
                {
                    Error(superCall, Diag.A_super_call_must_be_a_root_level_statement_within_a_constructor_of_a_derived_class_that_contains_initialized_properties_parameter_properties_or_private_identifiers);
                }
                else
                {
                    Node? superCallStatement = null;
                    foreach (var statement in node.Body()!.Statements().OrEmpty())
                    {
                        if (NodeTests.IsExpressionStatement(statement) && IsSuperCall(AstUtilities.SkipOuterExpressions(statement.Expression()!, OuterExpressionKinds.All)))
                        {
                            superCallStatement = statement;
                            break;
                        }
                        if (NodeImmediatelyReferencesSuperOrThis(statement))
                        {
                            break;
                        }
                    }
                    // Until we have better flow analysis, it is an error to place the super call within any kind of block or conditional
                    // See GH #8277
                    if (superCallStatement == null)
                    {
                        Error(node, Diag.A_super_call_must_be_the_first_statement_in_the_constructor_to_refer_to_super_or_this_when_a_derived_class_contains_initialized_properties_parameter_properties_or_private_identifiers);
                    }
                }
            }
        }
        else if (!classExtendsNull)
        {
            Error(node, Diag.Constructors_for_derived_classes_must_contain_a_super_call);
        }
    }

    // checker.go:2918
    internal void CheckAccessorDeclaration(Node node)
    {
        // Grammar checking accessors
        if (!CheckGrammarFunctionLikeDeclaration(node) && !CheckGrammarAccessor(node))
        {
            CheckGrammarComputedPropertyName(node.Name()!);
        }
        var name = node.Name()!;
        if (NodeTests.IsIdentifier(name) && name.Text() == "constructor" && AstUtilities.IsClassLike(node.Parent!))
        {
            Error(node.Name(), Diag.Class_constructor_may_not_be_an_accessor);
        }
        CheckDecorators(node);
        CheckSignatureDeclaration(node);
        if (NodeTests.IsGetAccessorDeclaration(node))
        {
            if ((node.Flags & NodeFlags.Ambient) == 0 && AstUtilities.NodeIsPresent(node.Body()) && (node.Flags & NodeFlags.HasImplicitReturn) != 0)
            {
                if ((node.Flags & NodeFlags.HasExplicitReturn) == 0)
                {
                    Error(name, Diag.A_get_accessor_must_return_a_value);
                }
            }
        }
        // Do not use hasDynamicName here, because that returns false for well known symbols.
        // We want to perform checkComputedPropertyName for all computed properties, including
        // well known symbols.
        if (NodeTests.IsComputedPropertyName(name))
        {
            CheckComputedPropertyName(name);
        }
        if (HasBindableName(node))
        {
            // TypeScript 1.0 spec (April 2014): 8.4.3
            // Accessors for the same member name must specify the same accessibility.
            var symbol = GetSymbolOfDeclaration(node)!;
            var getter = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.GetAccessor);
            var setter = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.SetAccessor);
            if (getter != null && setter != null && (nodeLinks.Get(getter).flags & NodeCheckFlags.TypeChecked) == 0)
            {
                nodeLinks.Get(getter).flags |= NodeCheckFlags.TypeChecked;
                var getterFlags = getter.ModifierFlags();
                var setterFlags = setter.ModifierFlags();
                if ((getterFlags & ModifierFlags.Abstract) != (setterFlags & ModifierFlags.Abstract))
                {
                    Error(getter.Name(), Diag.Accessors_must_both_be_abstract_or_non_abstract);
                    Error(setter.Name(), Diag.Accessors_must_both_be_abstract_or_non_abstract);
                }
                if (((getterFlags & ModifierFlags.Protected) != 0 && (setterFlags & (ModifierFlags.Protected | ModifierFlags.Private)) == 0) || ((getterFlags & ModifierFlags.Private) != 0 && (setterFlags & ModifierFlags.Private) == 0))
                {
                    Error(getter.Name(), Diag.A_get_accessor_must_be_at_least_as_accessible_as_the_setter);
                    Error(setter.Name(), Diag.A_get_accessor_must_be_at_least_as_accessible_as_the_setter);
                }
            }
        }
        var returnType = GetTypeOfAccessors(GetSymbolOfDeclaration(node)!);
        if (node.Kind == SyntaxKind.GetAccessor)
        {
            CheckAllCodePathsInNonVoidFunctionReturnOrThrow(node, returnType);
        }
        CheckSourceElement(node.Body());
        SetNodeLinksForPrivateIdentifierScope(node);
    }

    // ======================================================================================
    // Type-node checks (checkTypeReferenceNode .. checkMappedType)
    // ======================================================================================

    // checker.go:2970
    internal void CheckTypeReferenceNode(Node node)
    {
        CheckGrammarTypeArguments(node, node.TypeArgumentList());
        if (NodeTests.IsTypeReferenceNode(node) && (node.Flags & NodeFlags.JSDoc) == 0)
        {
            var data = node.AsTypeReferenceNode();
            if (data.TypeArguments != null && data.TypeName!.End != data.TypeArguments.Pos)
            {
                // If there was a token between the type name and the type arguments, check if it was a DotToken
                var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
                if (ScanTokenAtPosition(sourceFile, data.TypeName.End) == SyntaxKind.DotToken)
                {
                    GrammarErrorAtPos(node, global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), data.TypeName.End), 1, Diag.JSDoc_types_can_only_be_used_inside_documentation_comments);
                }
            }
        }
        CheckSourceElements(node.TypeArguments()!);
        CheckTypeReferenceOrImport(node);
    }

    // checker.go:3043
    internal void CheckTypePredicate(Node node)
    {
        var parent = GetTypePredicateParent(node);
        if (parent == null)
        {
            // The parent must not be valid.
            Error(node, Diag.A_type_predicate_is_only_allowed_in_return_type_position_for_functions_and_methods);
            return;
        }
        var signature = GetSignatureFromDeclaration(parent);
        var typePredicate = GetTypePredicateOfSignature(signature);
        if (typePredicate == null)
        {
            return;
        }
        CheckSourceElement(node.Type());
        var parameterName = node.AsTypePredicateNode().ParameterName;
        if (typePredicate.kind != TypePredicateKind.This && typePredicate.kind != TypePredicateKind.AssertsThis)
        {
            if (typePredicate.parameterIndex >= 0)
            {
                if (SignatureHasRestParameter(signature) && typePredicate.parameterIndex == signature.parameters.Len() - 1)
                {
                    Error(parameterName, Diag.A_type_predicate_cannot_reference_a_rest_parameter);
                }
                else
                {
                    if (typePredicate.t != null)
                    {
                        var diags = new List<Diagnostic>();
                        if (!CheckTypeAssignableToEx(typePredicate.t, GetTypeOfSymbol(signature.parameters![typePredicate.parameterIndex]), node.Type(), null /*headMessage*/, diags))
                        {
                            AddDiagnostic(Diagnostic.NewDiagnosticChain(diags[0], Diag.A_type_predicate_s_type_must_be_assignable_to_its_parameter_s_type));
                        }
                    }
                }
            }
            else if (parameterName != null)
            {
                var hasReportedError = false;
                foreach (var param in parent.Parameters().OrEmpty())
                {
                    var pname = param.Name();
                    if (AstUtilities.IsBindingPattern(pname) && CheckIfTypePredicateVariableIsDeclaredInBindingPattern(pname!, parameterName, typePredicate.parameterName))
                    {
                        hasReportedError = true;
                        break;
                    }
                }
                if (!hasReportedError)
                {
                    Error(parameterName, Diag.Cannot_find_parameter_0, typePredicate.parameterName);
                }
            }
        }
    }

    // checker.go:3116
    internal void CheckTypeQuery(Node node)
    {
        GetTypeFromTypeQueryNode(node);
    }

    // checker.go:3120
    internal void CheckTypeLiteral(Node node)
    {
        CheckSourceElements(node.Members()!);
        var t = GetTypeFromTypeLiteralOrFunctionOrConstructorTypeNode(node);
        CheckIndexConstraints(t, t.symbol!, false /*isStaticIndex*/);
        CheckTypeForDuplicateIndexSignatures(node);
        CheckObjectTypeForDuplicateDeclarations(node, false /*checkPrivateNames*/);
    }

    // checker.go:3215
    internal void CheckArrayType(Node node)
    {
        CheckSourceElement(node.AsArrayTypeNode().ElementType);
    }

    // checker.go:3219
    internal void CheckTupleType(Node node)
    {
        var seenOptionalElement = false;
        var seenRestElement = false;
        var elements = node.Elements();
        foreach (var e in elements.OrEmpty())
        {
            var flags = GetTupleElementFlags(e);
            if ((flags & ElementFlags.Variadic) != 0)
            {
                var t = GetTypeFromTypeNode(e.Type()!);
                if (!IsArrayLikeType(t))
                {
                    Error(e, Diag.A_rest_element_type_must_be_an_array_type);
                    break;
                }
                if (IsArrayType(t) || (IsTupleType(t) && (t.TargetTupleType().combinedFlags & ElementFlags.Rest) != 0))
                {
                    flags |= ElementFlags.Rest;
                }
            }
            if ((flags & ElementFlags.Rest) != 0)
            {
                if (seenRestElement)
                {
                    GrammarErrorOnNode(e, Diag.A_rest_element_cannot_follow_another_rest_element);
                    break;
                }
                seenRestElement = true;
            }
            else if ((flags & ElementFlags.Optional) != 0)
            {
                if (seenRestElement)
                {
                    GrammarErrorOnNode(e, Diag.An_optional_element_cannot_follow_a_rest_element);
                    break;
                }
                seenOptionalElement = true;
            }
            else if ((flags & ElementFlags.Required) != 0 && seenOptionalElement)
            {
                GrammarErrorOnNode(e, Diag.A_required_element_cannot_follow_an_optional_element);
                break;
            }
        }
        CheckSourceElements(elements!);
        GetTypeFromTypeNode(node);
    }

    // checker.go:3256
    internal void CheckUnionOrIntersectionType(Node node)
    {
        node.ForEachChild(CheckSourceElement);
        GetTypeFromTypeNode(node);
    }

    // checker.go:3261
    internal void CheckThisType(Node node)
    {
        GetTypeFromThisTypeNode(node);
    }

    // checker.go:3265
    internal void CheckTypeOperator(Node node)
    {
        CheckGrammarTypeOperatorNode(node.AsTypeOperatorNode());
        CheckSourceElement(node.Type());
    }

    // checker.go:3270
    internal void CheckConditionalType(Node node)
    {
        node.ForEachChild(CheckSourceElement);
    }

    // checker.go:3274
    internal void CheckInferType(Node node)
    {
        if (AstUtilities.FindAncestor(node, n => n.Parent != null && n.Parent.Kind == SyntaxKind.ConditionalType && n.Parent.AsConditionalTypeNode().ExtendsType == n) == null)
        {
            GrammarErrorOnNode(node, Diag.X_infer_declarations_are_only_permitted_in_the_extends_clause_of_a_conditional_type);
        }
        var typeParameterDeclarationNode = node.AsInferTypeNode().TypeParameter;
        CheckSourceElement(typeParameterDeclarationNode);
        var symbol = GetSymbolOfDeclaration(typeParameterDeclarationNode!)!;
        if (symbol.Declarations.Len() > 1)
        {
            var links = declaredTypeLinks.Get(symbol);
            if (!links.typeParametersChecked)
            {
                links.typeParametersChecked = true;
                var typeParameter = GetDeclaredTypeOfTypeParameter(symbol);
                var declarations = GetDeclarationsOfKind(symbol, SyntaxKind.TypeParameter);
                if (!AreTypeParametersIdentical(declarations, [typeParameter], decl => [decl]))
                {
                    // Report an error on every conflicting declaration.
                    var name = SymbolToString(symbol);
                    foreach (var declaration in declarations)
                    {
                        Error(declaration.Name(), Diag.All_declarations_of_0_must_have_identical_constraints, name);
                    }
                }
            }
        }
        RegisterForUnusedIdentifiersCheck(node);
    }

    // checker.go:4399
    internal void CheckTypeParameterListsIdentical(Symbol symbol)
    {
        if (symbol.Declarations.Len() == 1)
        {
            return;
        }
        var links = declaredTypeLinks.Get(symbol);
        if (!links.typeParametersChecked)
        {
            links.typeParametersChecked = true;
            var declarations = GetClassOrInterfaceDeclarationsOfSymbol(symbol);
            if (declarations.Length <= 1)
            {
                return;
            }
            var t = GetDeclaredTypeOfSymbol(symbol);
            if (!AreTypeParametersIdentical(declarations, t.AsInterfaceType()!.LocalTypeParameters()!, static decl => decl.TypeParameters().OrEmpty()))
            {
                // Report an error on every conflicting declaration.
                var name = SymbolToString(symbol);
                foreach (var declaration in declarations)
                {
                    Error(declaration.Name(), Diag.All_declarations_of_0_must_have_identical_type_parameters, name);
                }
            }
        }
    }

    // checker.go:4421
    internal Node[] GetClassOrInterfaceDeclarationsOfSymbol(Symbol symbol)
    {
        return CoreUtil.Filter(symbol.Declarations, static d => NodeTests.IsClassDeclaration(d) || NodeTests.IsInterfaceDeclaration(d)).OrEmpty();
    }

    // checker.go:4427
    internal bool AreTypeParametersIdentical(Node[] declarations, Type[] targetParameters, Func<Node, Node[]> getTypeParameterDeclarations)
    {
        var maxTypeArgumentCount = targetParameters.Length;
        var minTypeArgumentCount = GetMinTypeArgumentCount(targetParameters);
        foreach (var declaration in declarations)
        {
            // If this declaration has too few or too many type parameters, we report an error
            var sourceParameters = getTypeParameterDeclarations(declaration);
            if (sourceParameters.Length < minTypeArgumentCount || sourceParameters.Length > maxTypeArgumentCount)
            {
                return false;
            }
            for (var i = 0; i < sourceParameters.Length; i++)
            {
                var source = sourceParameters[i];
                var target = targetParameters[i];
                // If the type parameter node does not have the same name as the resolved type
                // parameter at this position, we report an error.
                if (source.Name()!.Text() != target.symbol!.Name)
                {
                    return false;
                }
                // If the type parameter node does not have an identical constraintNode as the resolved
                // type parameter at this position, we report an error.
                var constraintNode = source.AsTypeParameterDeclaration().Constraint;
                var targetConstraint = GetConstraintOfTypeParameter(target);
                // relax check if later interface augmentation has no constraint, it's more broad and is OK to merge with
                // a more constrained interface (this could be generalized to a full hierarchy check, but that's maybe overkill)
                if (constraintNode != null && targetConstraint != null && !IsTypeIdenticalTo(GetTypeFromTypeNode(constraintNode), targetConstraint))
                {
                    return false;
                }
                // If the type parameter node has a default and it is not identical to the default
                // for the type parameter at this position, we report an error.
                var defaultNode = source.AsTypeParameterDeclaration().DefaultType;
                var targetDefault = GetDefaultFromTypeParameter(target);
                if (defaultNode != null && targetDefault != null && !IsTypeIdenticalTo(GetTypeFromTypeNode(defaultNode), targetDefault))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // checker.go:3301
    internal void CheckTemplateLiteralType(Node node)
    {
        foreach (var span in node.AsTemplateLiteralTypeNode().TemplateSpans!.Nodes)
        {
            CheckSourceElement(span.Type());
            var t = GetTypeFromTypeNode(span.Type()!);
            CheckTypeAssignableTo(t, templateConstraintType!, span.Type(), null);
        }
        GetTypeFromTypeNode(node);
    }

    // checker.go:3310
    internal void CheckImportType(Node node)
    {
        CheckSourceElement(node.AsImportTypeNode().Argument);
        if (node.AsImportTypeNode().Attributes is { } attributes)
        {
            GetResolutionModeOverride(attributes.AsImportAttributes(), true /*reportErrors*/);
        }
        CheckTypeReferenceOrImport(node);
    }

    // checker.go:3351
    internal void CheckNamedTupleMember(Node node)
    {
        var tupleMember = node.AsNamedTupleMember();
        if (tupleMember.DotDotDotToken != null && tupleMember.QuestionToken != null)
        {
            GrammarErrorOnNode(node, Diag.A_tuple_member_cannot_be_both_optional_and_rest);
        }
        if (tupleMember.Type!.Kind == SyntaxKind.OptionalType)
        {
            GrammarErrorOnNode(tupleMember.Type, Diag.A_labeled_tuple_element_is_declared_as_optional_with_a_question_mark_after_the_name_and_before_the_colon_rather_than_after_the_type);
        }
        if (tupleMember.Type.Kind == SyntaxKind.RestType)
        {
            GrammarErrorOnNode(tupleMember.Type, Diag.A_labeled_tuple_element_is_declared_as_rest_with_a_before_the_name_rather_than_before_the_type);
        }
        CheckSourceElement(node.Type());
        GetTypeFromTypeNode(node);
    }

    // checker.go:3366
    internal void CheckIndexedAccessType(Node node)
    {
        node.ForEachChild(CheckSourceElement);
        CheckIndexedAccessIndexType(GetTypeFromIndexedAccessTypeNode(node), node);
    }

    // checker.go:3371
    internal void CheckMappedType(Node node)
    {
        var mappedTypeNode = node.AsMappedTypeNode();
        CheckGrammarMappedType(mappedTypeNode);
        CheckSourceElement(mappedTypeNode.TypeParameter);
        CheckSourceElement(mappedTypeNode.NameType);
        CheckSourceElement(mappedTypeNode.Type);
        if (mappedTypeNode.Type == null)
        {
            ReportImplicitAny(node, anyType!, WideningKind.Normal);
        }
        var t = GetTypeFromMappedTypeNode(node);
        var nameType = GetNameTypeFromMappedType(t);
        if (nameType != null)
        {
            CheckTypeAssignableTo(nameType, stringNumberSymbolType!, mappedTypeNode.NameType, null);
        }
        else
        {
            var constraintType = GetConstraintTypeFromMappedType(t);
            CheckTypeAssignableTo(constraintType, stringNumberSymbolType!, mappedTypeNode.TypeParameter!.AsTypeParameterDeclaration().Constraint, null);
        }
    }

    // ======================================================================================
    // Class / interface / enum / module declaration validators
    // ======================================================================================

    // checker.go:4261
    internal void CheckClassDeclaration(Node node)
    {
        var firstDecorator = node.ModifierNodes().OrEmpty().FirstOrDefault(NodeTests.IsDecorator);
        if (legacyDecorators && firstDecorator != null && node.Members().OrEmpty().Any(p =>
            AstUtilities.HasStaticModifier(p) && AstUtilities.IsPrivateIdentifierClassElementDeclaration(p)))
        {
            GrammarErrorOnNode(firstDecorator, Diag.Class_decorators_can_t_be_used_with_static_private_identifier_Consider_removing_the_experimental_decorator);
        }
        if (node.Name() == null && !AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default))
        {
            GrammarErrorOnFirstToken(node, Diag.A_class_declaration_without_the_default_modifier_must_have_a_name);
        }
        CheckClassLikeDeclaration(node);
        CheckSourceElements(node.Members()!);
        RegisterForUnusedIdentifiersCheck(node);
    }

    // checker.go:4276
    internal void CheckClassLikeDeclaration(Node node)
    {
        CheckGrammarClassLikeDeclaration(node);
        CheckDecorators(node);
        CheckCollisionsForDeclarationName(node, node.Name());
        CheckTypeParameters(node.TypeParameters());
        CheckExportsOnMergedDeclarations(node);
        var symbol = GetSymbolOfDeclaration(node)!;
        var classType = GetDeclaredTypeOfSymbol(symbol);
        var classTypeData = classType.AsInterfaceType()!;
        var typeWithThis = GetTypeWithThisArgument(classType, null, false);
        var staticType = GetTypeOfSymbol(symbol);
        CheckTypeParameterListsIdentical(symbol);
        CheckFunctionOrConstructorSymbol(symbol);
        CheckObjectTypeForDuplicateDeclarations(node, true /*checkPrivateNames*/);

        // Only check for reserved static identifiers on non-ambient context.
        var nodeInAmbientContext = (node.Flags & NodeFlags.Ambient) != 0;
        if (!nodeInAmbientContext)
        {
            CheckClassForStaticPropertyNameConflicts(node);
        }

        var baseTypeNode = AstUtilities.GetExtendsHeritageClauseElement(node);
        if (baseTypeNode != null)
        {
            CheckSourceElements(baseTypeNode.TypeArguments()!);
            var baseTypes = GetBaseTypes(classType);
            if (baseTypes.Length != 0)
            {
                var baseType = baseTypes[0];
                var baseConstructorType = GetBaseConstructorTypeOfClass(classType);
                var staticBaseType = GetApparentType(baseConstructorType);
                CheckBaseTypeAccessibility(staticBaseType, baseTypeNode);
                CheckSourceElement(baseTypeNode.Expression());
                if (baseTypeNode.TypeArguments().Len() != 0)
                {
                    CheckSourceElements(baseTypeNode.TypeArguments()!);
                    foreach (var constructor in GetConstructorsForTypeArguments(staticBaseType, baseTypeNode.TypeArguments(), baseTypeNode))
                    {
                        if (!CheckTypeArgumentConstraints(baseTypeNode, constructor.typeParameters))
                        {
                            break;
                        }
                    }
                }
                var baseWithThis = GetTypeWithThisArgument(baseType, classTypeData.thisType, false);
                if (!CheckTypeAssignableTo(typeWithThis, baseWithThis, null, null))
                {
                    IssueMemberSpecificError(node, typeWithThis, baseWithThis, Diag.Class_0_incorrectly_extends_base_class_1);
                }
                else
                {
                    // Report static side error only when instance type is assignable
                    CheckTypeAssignableTo(staticType, GetTypeWithoutSignatures(staticBaseType), node.Name() ?? node, Diag.Class_static_side_0_incorrectly_extends_base_class_static_side_1);
                }
                if ((baseConstructorType.flags & TypeFlags.TypeVariable) != 0)
                {
                    if (!IsMixinConstructorType(staticType))
                    {
                        Error(node.Name() ?? node, Diag.A_mixin_class_must_have_a_constructor_with_a_single_rest_parameter_of_type_any);
                    }
                    else
                    {
                        var constructSignatures = GetSignaturesOfType(baseConstructorType, SignatureKind.Construct);
                        if (constructSignatures.Any(signature => (signature.flags & SignatureFlags.Abstract) != 0) && !AstUtilities.HasSyntacticModifier(node, ModifierFlags.Abstract))
                        {
                            Error(node.Name() ?? node, Diag.A_mixin_class_that_extends_from_a_type_variable_containing_an_abstract_construct_signature_must_also_be_declared_abstract);
                        }
                    }
                }
                if (!(staticBaseType.symbol != null && (staticBaseType.symbol.Flags & SymbolFlags.Class) != 0) && (baseConstructorType.flags & TypeFlags.TypeVariable) == 0)
                {
                    // When the static base type is a "class-like" constructor function (but not actually a class), we verify
                    // that all instantiated base constructor signatures return the same type.
                    var constructors = GetInstantiatedConstructorsForTypeArguments(staticBaseType, baseTypeNode.TypeArguments(), baseTypeNode);
                    if (!constructors.All(sig => IsTypeIdenticalTo(GetReturnTypeOfSignature(sig), baseType)))
                    {
                        Error(baseTypeNode.Expression(), Diag.Base_constructors_must_all_have_the_same_return_type);
                    }
                }
                CheckKindsOfPropertyMemberOverrides(classType, baseType);
            }
        }
        CheckMembersForOverrideModifier(node, classType, typeWithThis, staticType);
        var implementedTypeNodes = GetImplementsHeritageClauseElements(node);
        foreach (var typeRefNode in implementedTypeNodes)
        {
            var expr = typeRefNode.Expression()!;
            if (!AstUtilities.IsEntityNameExpression(expr) || AstUtilities.IsOptionalChain(expr))
            {
                Error(expr, Diag.A_class_can_only_implement_an_identifier_Slashqualified_name_with_optional_type_arguments);
            }
            CheckTypeReferenceNode(typeRefNode);
            var t = GetReducedType(GetTypeFromTypeNode(typeRefNode));
            if (!IsErrorType(t))
            {
                if (IsValidBaseType(t))
                {
                    var genericDiag = t.symbol != null && (t.symbol.Flags & SymbolFlags.Class) != 0
                        ? Diag.Class_0_incorrectly_implements_class_1_Did_you_mean_to_extend_1_and_inherit_its_members_as_a_subclass
                        : Diag.Class_0_incorrectly_implements_interface_1;
                    var baseWithThis = GetTypeWithThisArgument(t, classType.AsInterfaceType()!.thisType, false);
                    if (!CheckTypeAssignableTo(typeWithThis, baseWithThis, null, null))
                    {
                        IssueMemberSpecificError(node, typeWithThis, baseWithThis, genericDiag);
                    }
                }
                else
                {
                    Error(typeRefNode, Diag.A_class_can_only_implement_an_object_type_or_intersection_of_object_types_with_statically_known_members);
                }
            }
        }
        CheckIndexConstraints(classType, symbol, false /*isStaticIndex*/);
        CheckIndexConstraints(staticType, symbol, true /*isStaticIndex*/);
        CheckClassOrInterfaceForDuplicateIndexSignatures(node);
        CheckPropertyInitialization(node);
    }

    // checker.go:4974
    internal void CheckInterfaceDeclaration(Node node)
    {
        if (!CheckGrammarModifiers(node))
        {
            CheckGrammarInterfaceDeclaration(node.AsInterfaceDeclaration());
        }
        if (!ContainerAllowsBlockScopedVariable(node.Parent!))
        {
            GrammarErrorOnNode(node, Diag.X_0_declarations_can_only_be_declared_inside_a_block, "interface");
        }
        CheckTypeParameters(node.TypeParameters());
        CheckTypeNameIsReserved(node.Name()!, Diag.Interface_name_cannot_be_0);
        CheckExportsOnMergedDeclarations(node);
        var symbol = GetSymbolOfDeclaration(node)!;
        CheckTypeParameterListsIdentical(symbol);
        // Only check this symbol once
        var links = declaredTypeLinks.Get(symbol);
        if (!links.interfaceChecked)
        {
            links.interfaceChecked = true;
            var t = GetDeclaredTypeOfSymbol(symbol);
            var typeWithThis = GetTypeWithThisArgument(t, null, false);
            // run subsequent checks only if first set succeeded
            if (CheckInheritedPropertiesAreIdentical(t, node.Name()))
            {
                foreach (var baseType in GetBaseTypes(t))
                {
                    CheckTypeAssignableTo(typeWithThis, GetTypeWithThisArgument(baseType, t.AsInterfaceType()!.thisType, false), node.Name(), Diag.Interface_0_incorrectly_extends_interface_1);
                }
                CheckIndexConstraints(t, symbol, false /*isStaticIndex*/);
            }
        }
        CheckObjectTypeForDuplicateDeclarations(node, false /*checkPrivateNames*/);
        foreach (var heritageElement in GetExtendsHeritageClauseElements(node))
        {
            var expr = heritageElement.Expression()!;
            if (!AstUtilities.IsEntityNameExpression(expr) || AstUtilities.IsOptionalChain(expr))
            {
                Error(expr, Diag.An_interface_can_only_extend_an_identifier_Slashqualified_name_with_optional_type_arguments);
            }
            CheckTypeReferenceNode(heritageElement);
        }
        CheckSourceElements(node.Members()!);
        CheckClassOrInterfaceForDuplicateIndexSignatures(node);
        RegisterForUnusedIdentifiersCheck(node);
    }

    // checker.go:5053
    internal void CheckEnumDeclaration(Node node)
    {
        CheckGrammarModifiers(node);
        CheckCollisionsForDeclarationName(node, node.Name());
        CheckExportsOnMergedDeclarations(node);
        CheckSourceElements(node.Members()!);

        if (ShouldCheckErasableSyntax(node) && (node.Flags & NodeFlags.Ambient) == 0)
        {
            Error(node, Diag.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled);
        }

        ComputeEnumMemberValues(node);
        // Spec 2014 - Section 9.3:
        // It isn't possible for one enum declaration to continue the automatic numbering sequence of another,
        // and when an enum type has multiple declarations, only one declaration is permitted to omit a value
        // for the first member.
        //
        // Only perform this check once per symbol
        var enumSymbol = GetSymbolOfDeclaration(node)!;
        var links = declaredTypeLinks.Get(enumSymbol);
        if (!links.enumChecked)
        {
            links.enumChecked = true;
            if (enumSymbol.Declarations.Len() > 1)
            {
                var enumIsConst = AstUtilities.IsEnumConst(node);
                // check that const is placed\omitted on all enum declarations
                foreach (var decl in enumSymbol.Declarations!)
                {
                    if (NodeTests.IsEnumDeclaration(decl) && AstUtilities.IsEnumConst(decl) != enumIsConst)
                    {
                        Error(AstUtilities.GetNameOfDeclaration(decl), Diag.Enum_declarations_must_all_be_const_or_non_const);
                    }
                }
            }
            var seenEnumMissingInitialInitializer = false;
            foreach (var declaration in enumSymbol.Declarations!)
            {
                // return true if we hit a violation of the rule, false otherwise
                if (declaration.Kind != SyntaxKind.EnumDeclaration)
                {
                    continue;
                }
                var members = declaration.Members();
                if (members.Len() == 0)
                {
                    continue;
                }
                var firstEnumMember = members![0];
                if (firstEnumMember.Initializer() == null)
                {
                    if (seenEnumMissingInitialInitializer)
                    {
                        Error(firstEnumMember.Name(), Diag.In_an_enum_with_multiple_declarations_only_one_declaration_can_omit_an_initializer_for_its_first_enum_element);
                    }
                    else
                    {
                        seenEnumMissingInitialInitializer = true;
                    }
                }
            }
        }
    }

    // checker.go:5104
    internal void CheckEnumMember(Node node)
    {
        if (NodeTests.IsPrivateIdentifier(node.Name()!))
        {
            Error(node, Diag.An_enum_member_cannot_be_named_with_a_private_identifier);
        }
        if (node.Initializer() != null)
        {
            CheckExpression(node.Initializer()!);
        }
    }

    // checker.go:5113
    internal void CheckModuleDeclaration(Node node)
    {
        if (node.Body() is { } body)
        {
            CheckSourceElement(body);
            if (!AstUtilities.IsGlobalScopeAugmentation(node))
            {
                RegisterForUnusedIdentifiersCheck(node);
            }
        }
        var isGlobalAugmentation = AstUtilities.IsGlobalScopeAugmentation(node);
        var inAmbientContext = (node.Flags & NodeFlags.Ambient) != 0;
        if (isGlobalAugmentation && !inAmbientContext)
        {
            Error(node.Name(), Diag.Augmentations_for_the_global_scope_should_have_declare_modifier_unless_they_appear_in_already_ambient_context);
        }
        var isAmbientExternalModule = AstUtilities.IsAmbientModule(node);
        var contextErrorMessage = isAmbientExternalModule
            ? Diag.An_ambient_module_declaration_is_only_allowed_at_the_top_level_in_a_file
            : Diag.A_namespace_declaration_is_only_allowed_at_the_top_level_of_a_namespace_or_module;
        if (CheckGrammarModuleElementContext(node, contextErrorMessage))
        {
            // If we hit a module declaration in an illegal context, just bail out to avoid cascading errors.
            return;
        }
        if (!CheckGrammarModifiers(node))
        {
            if (!inAmbientContext && NodeTests.IsStringLiteral(node.Name()!))
            {
                GrammarErrorOnNode(node.Name()!, Diag.Only_ambient_modules_can_use_quoted_names);
            }
        }
        if (NodeTests.IsIdentifier(node.Name()!))
        {
            CheckCollisionsForDeclarationName(node, node.Name());
            if (node.AsModuleDeclaration().Keyword == SyntaxKind.ModuleKeyword)
            {
                Error(node.Name(), Diag.A_namespace_declaration_should_not_be_declared_using_the_module_keyword_Please_use_the_namespace_keyword_instead);
            }
        }
        CheckExportsOnMergedDeclarations(node);
        var symbol = GetSymbolOfDeclaration(node)!;
        // The following checks only apply on a non-ambient instantiated module declaration.
        if ((symbol.Flags & SymbolFlags.ValueModule) != 0 && !inAmbientContext && AstUtilities.IsInstantiatedModule(node, compilerOptions.ShouldPreserveConstEnums()))
        {
            if (ShouldCheckErasableSyntax(node))
            {
                Error(node, Diag.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled);
            }
            if (compilerOptions.GetIsolatedModules() && AstUtilities.GetSourceFileOfNode(node)!.ExternalModuleIndicator == null)
            {
                // This could be loosened a little if needed. The only problem we are trying to avoid is unqualified
                // references to namespace members declared in other files. But use of namespaces is discouraged anyway,
                // so for now we will just not allow them in scripts, which is the only place they can merge cross-file.
                Error(node.Name(), Diag.Namespaces_are_not_allowed_in_global_script_files_when_0_is_enabled_If_this_file_is_not_intended_to_be_a_global_script_set_moduleDetection_to_force_or_add_an_empty_export_statement, GetIsolatedModulesLikeFlagName());
            }
            if (symbol.Declarations.Len() > 1)
            {
                var firstNonAmbientClassOrFunc = GetFirstNonAmbientClassOrFunctionDeclaration(symbol);
                if (firstNonAmbientClassOrFunc != null)
                {
                    if (AstUtilities.GetSourceFileOfNode(node) != AstUtilities.GetSourceFileOfNode(firstNonAmbientClassOrFunc))
                    {
                        Error(node.Name(), Diag.A_namespace_declaration_cannot_be_in_a_different_file_from_a_class_or_function_with_which_it_is_merged);
                    }
                    else if (node.Pos < firstNonAmbientClassOrFunc.Pos)
                    {
                        Error(node.Name(), Diag.A_namespace_declaration_cannot_be_located_prior_to_a_class_or_function_with_which_it_is_merged);
                    }
                }
            }
            if (compilerOptions.VerbatimModuleSyntax.IsTrue() && NodeTests.IsSourceFile(node.Parent!) && (node.ModifierFlags() & ModifierFlags.Export) != 0 && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(node.Parent!.AsSourceFile())) == ModuleKind.CommonJS)
            {
                var exportModifier = node.ModifierNodes().OrEmpty().FirstOrDefault(m => m.Kind == SyntaxKind.ExportKeyword);
                Error(exportModifier, Diag.A_top_level_export_modifier_cannot_be_used_on_value_declarations_in_a_CommonJS_module_when_verbatimModuleSyntax_is_enabled);
            }
        }
        if (isAmbientExternalModule)
        {
            if (IsExternalModuleAugmentation(node))
            {
                // body of the augmentation should be checked for consistency only if augmentation was applied to its target (either global scope or module)
                // otherwise we'll be swamped in cascading errors.
                // We can detect if augmentation was applied using following rules:
                // - augmentation for a global scope is always applied
                // - augmentation for some external module is applied if symbol for augmentation is merged (it was combined with target module).
                var checkBody = isGlobalAugmentation || (GetSymbolOfDeclaration(node)!.Flags & SymbolFlags.Transient) != 0;
                if (checkBody && node.Body() != null)
                {
                    foreach (var statement in node.Body()!.Statements().OrEmpty())
                    {
                        CheckModuleAugmentationElement(statement);
                    }
                }
            }
            else if (AstUtilities.IsGlobalSourceFile(node.Parent!))
            {
                if (isGlobalAugmentation)
                {
                    Error(node.Name(), Diag.Augmentations_for_the_global_scope_can_only_be_directly_nested_in_external_modules_or_ambient_module_declarations);
                }
                else if (global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(node.Name()!.Text()))
                {
                    Error(node.Name(), Diag.Ambient_module_declaration_cannot_specify_relative_module_name);
                }
            }
            else
            {
                if (isGlobalAugmentation)
                {
                    Error(node.Name(), Diag.Augmentations_for_the_global_scope_can_only_be_directly_nested_in_external_modules_or_ambient_module_declarations);
                }
                else
                {
                    // Node is not an augmentation and is not located on the script level.
                    // This means that this is declaration of ambient module that is located in other module or namespace which is prohibited.
                    Error(node.Name(), Diag.Ambient_modules_cannot_be_nested_in_other_modules_or_namespaces);
                }
            }
        }
    }

    // ======================================================================================
    // Import / export declaration validators
    // ======================================================================================

    // checker.go:5249
    internal void CheckImportDeclaration(Node node)
    {
        // Grammar checking
        global::Ts.Diagnostics.Message diagnostic;
        if (AstUtilities.IsInJSFile(node))
        {
            diagnostic = Diag.An_import_declaration_can_only_be_used_at_the_top_level_of_a_module;
        }
        else
        {
            diagnostic = Diag.An_import_declaration_can_only_be_used_at_the_top_level_of_a_namespace_or_module;
        }
        if (CheckGrammarModuleElementContext(node, diagnostic))
        {
            // If we hit an import declaration in an illegal context, just bail out to avoid cascading errors.
            return;
        }
        if (!CheckGrammarModifiers(node) && node.Modifiers() != null)
        {
            GrammarErrorOnFirstToken(node, Diag.An_import_declaration_cannot_have_modifiers);
        }
        if (CheckExternalImportOrExportDeclaration(node))
        {
            Symbol? resolvedModule = null;
            var importClause = node.ImportClause();
            var moduleSpecifier = node.ModuleSpecifier();
            if (importClause != null && !CheckGrammarImportClause(importClause.AsImportClause()))
            {
                if (importClause.Name() != null)
                {
                    CheckImportBinding(importClause);
                }
                var needsImportStar = false;
                var namedBindings = importClause.AsImportClause().NamedBindings;
                if (namedBindings != null)
                {
                    if (NodeTests.IsNamespaceImport(namedBindings))
                    {
                        CheckImportBinding(namedBindings);
                        if (program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
                        {
                            // import * as ns from "foo";
                            needsImportStar = true;
                            CheckExternalEmitHelpers(node, ExternalEmitHelpers.ImportStar);
                        }
                    }
                    else
                    {
                        resolvedModule = ResolveExternalModuleName(node, node.ModuleSpecifier()!, false);
                        if (resolvedModule != null)
                        {
                            foreach (var binding in namedBindings.Elements().OrEmpty())
                            {
                                CheckImportBinding(binding);
                            }
                        }
                    }
                }
                if (importClause.Name() != null &&
                    !needsImportStar &&
                    program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
                {
                    // import d from "foo";
                    CheckExternalEmitHelpers(node, ExternalEmitHelpers.ImportDefault);
                }

                if (!importClause.IsTypeOnly() &&
                    ModuleKind.Node18 <= moduleKind && moduleKind <= ModuleKind.NodeNext &&
                    IsOnlyImportableAsDefault(moduleSpecifier, resolvedModule) &&
                    !HasTypeJsonImportAttribute(node))
                {
                    Error(moduleSpecifier, Diag.Importing_a_JSON_file_into_an_ECMAScript_module_requires_a_type_Colon_json_import_attribute_when_module_is_set_to_0, ModuleKindToString(moduleKind));
                }
            }
            else if (compilerOptions.NoUncheckedSideEffectImports.IsTrueOrUnknown() && importClause == null)
            {
                var ignoreErrors = compilerOptions.NoCheck.IsTrue();
                global::Ts.Diagnostics.Message? errorMessage = null;
                if (!ignoreErrors)
                {
                    errorMessage = Diag.Cannot_find_module_or_type_declarations_for_side_effect_import_of_0;
                }
                ResolveExternalModuleNameWorker(node, moduleSpecifier!, errorMessage, ignoreErrors, false /*isForAugmentation*/);
            }
        }
        CheckImportAttributes(node);
    }

    // checker.go:5444
    internal void CheckImportEqualsDeclaration(Node node)
    {
        var diagnostic = AstUtilities.IsInJSFile(node)
            ? Diag.An_import_declaration_can_only_be_used_at_the_top_level_of_a_module
            : Diag.An_import_declaration_can_only_be_used_at_the_top_level_of_a_namespace_or_module;
        if (CheckGrammarModuleElementContext(node, diagnostic))
        {
            return; // If we hit an import declaration in an illegal context, just bail out to avoid cascading errors.
        }
        CheckGrammarModifiers(node);
        if (ShouldCheckErasableSyntax(node) && (node.Flags & NodeFlags.Ambient) == 0)
        {
            Error(node, Diag.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled);
        }
        if (IsInternalModuleImportEqualsDeclaration(node) || CheckExternalImportOrExportDeclaration(node))
        {
            CheckImportBinding(node);
            MarkLinkedReferences(node, ReferenceHint.ExportImportEquals, null, null);
            var moduleReference = node.AsImportEqualsDeclaration().ModuleReference!;
            if (!NodeTests.IsExternalModuleReference(moduleReference))
            {
                var (target, _) = ResolveAlias(GetSymbolOfDeclaration(node)!);
                if (target != unknownSymbol)
                {
                    var targetFlags = GetSymbolFlags(target!);
                    if ((targetFlags & SymbolFlags.Value) != 0)
                    {
                        // Target is a value symbol, check that it is not hidden by a local declaration with the same name
                        var moduleName = GetFirstIdentifier(moduleReference);
                        if ((ResolveEntityName(moduleName, SymbolFlags.Value | SymbolFlags.Namespace, false, false, null)!.Flags & SymbolFlags.Namespace) == 0)
                        {
                            Error(moduleName, Diag.Module_0_is_hidden_by_a_local_declaration_with_the_same_name, global::Ts.Scanner.Scanner.DeclarationNameToString(moduleName));
                        }
                    }
                    if ((targetFlags & SymbolFlags.Type) != 0)
                    {
                        CheckTypeNameIsReserved(node.Name()!, Diag.Import_name_cannot_be_0);
                    }
                }
                if (node.IsTypeOnly())
                {
                    GrammarErrorOnNode(node, Diag.An_import_alias_cannot_use_import_type);
                }
            }
            else
            {
                if (ModuleKind.ES2015 <= moduleKind && moduleKind <= ModuleKind.ESNext && !node.IsTypeOnly() && (node.Flags & NodeFlags.Ambient) == 0)
                {
                    // Import equals declaration cannot be emitted as ESM
                    GrammarErrorOnNode(node, Diag.Import_assignment_cannot_be_used_when_targeting_ECMAScript_modules_Consider_using_import_Asterisk_as_ns_from_mod_import_a_from_mod_import_d_from_mod_or_another_module_format_instead);
                }
            }
        }
    }

    // checker.go:5486
    internal void CheckExportDeclaration(Node node)
    {
        var diagnostic = AstUtilities.IsInJSFile(node)
            ? Diag.An_export_declaration_can_only_be_used_at_the_top_level_of_a_module
            : Diag.An_export_declaration_can_only_be_used_at_the_top_level_of_a_namespace_or_module;
        if (CheckGrammarModuleElementContext(node, diagnostic))
        {
            return; // If we hit an export in an illegal context, just bail out to avoid cascading errors.
        }
        var exportDecl = node.AsExportDeclaration();
        if (!CheckGrammarModifiers(node) && exportDecl.Modifiers() != null)
        {
            GrammarErrorOnFirstToken(node, Diag.An_export_declaration_cannot_have_modifiers);
        }
        CheckGrammarExportDeclaration(exportDecl);
        if (exportDecl.ModuleSpecifier == null || CheckExternalImportOrExportDeclaration(node))
        {
            if (exportDecl.ExportClause != null && !NodeTests.IsNamespaceExport(exportDecl.ExportClause))
            {
                // export { x, y }
                // export { x, y } from "foo"
                foreach (var binding in exportDecl.ExportClause.Elements().OrEmpty())
                {
                    CheckExportSpecifier(binding);
                }
                var inAmbientExternalModule = NodeTests.IsModuleBlock(node.Parent!) && AstUtilities.IsAmbientModule(node.Parent!.Parent!);
                var inAmbientNamespaceDeclaration = !inAmbientExternalModule && NodeTests.IsModuleBlock(node.Parent!) && exportDecl.ModuleSpecifier == null && (node.Flags & NodeFlags.Ambient) != 0;
                if (!NodeTests.IsSourceFile(node.Parent!) && !inAmbientExternalModule && !inAmbientNamespaceDeclaration)
                {
                    Error(node, Diag.Export_declarations_are_not_permitted_in_a_namespace);
                }
            }
            else
            {
                // export * from "foo"
                // export * as ns from "foo";
                var moduleSymbol = ResolveExternalModuleName(node, exportDecl.ModuleSpecifier, false);
                if (moduleSymbol != null && HasExportAssignmentSymbol(moduleSymbol))
                {
                    Error(exportDecl.ModuleSpecifier, Diag.Module_0_uses_export_and_cannot_be_used_with_export_Asterisk, SymbolToString(moduleSymbol));
                }
                else if (exportDecl.ExportClause != null)
                {
                    CheckAliasSymbol(exportDecl.ExportClause);
                    CheckModuleExportName(exportDecl.ExportClause.Name(), true /*allowStringLiteral*/);
                }
                if (program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
                {
                    if (node.AsExportDeclaration().ExportClause != null)
                    {
                        // export * as ns from "foo";
                        CheckExternalEmitHelpers(node, ExternalEmitHelpers.ImportStar);
                    }
                    else
                    {
                        // export * from "foo"
                        CheckExternalEmitHelpers(node, ExternalEmitHelpers.ExportStar);
                    }
                }
            }
        }
        CheckImportAttributes(node);
    }

    // checker.go:5558
    internal void CheckExportAssignment(Node node)
    {
        var isExportEquals = node.AsExportAssignment().IsExportEquals;
        var illegalContextMessage = isExportEquals
            ? Diag.An_export_assignment_must_be_at_the_top_level_of_a_file_or_module_declaration
            : Diag.A_default_export_must_be_at_the_top_level_of_a_file_or_module_declaration;
        if (CheckGrammarModuleElementContext(node, illegalContextMessage))
        {
            return; // If we hit an export assignment in an illegal context, just bail out to avoid cascading errors.
        }
        if (ShouldCheckErasableSyntax(node) && node.AsExportAssignment().IsExportEquals && (node.Flags & NodeFlags.Ambient) == 0)
        {
            Error(node, Diag.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled);
        }
        var container = node.Parent!;
        if (!NodeTests.IsSourceFile(container))
        {
            container = container.Parent!;
        }
        if (NodeTests.IsModuleDeclaration(container) && !AstUtilities.IsAmbientModule(container))
        {
            // TODO(danielr): should these be grammar errors?
            if (isExportEquals)
            {
                Error(node, Diag.An_export_assignment_cannot_be_used_in_a_namespace);
            }
            else
            {
                Error(node, Diag.A_default_export_can_only_be_used_in_an_ECMAScript_style_module);
            }
            return;
        }
        if (!CheckGrammarModifiers(node) && NodeTests.IsExportAssignment(node) && node.AsExportAssignment().Modifiers() != null)
        {
            GrammarErrorOnFirstToken(node, Diag.An_export_assignment_cannot_have_modifiers);
        }
        var isIllegalExportDefaultInCJS = !isExportEquals && (node.Flags & NodeFlags.Ambient) == 0 && compilerOptions.VerbatimModuleSyntax.IsTrue() && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS;
        if (NodeTests.IsIdentifier(node.Expression()!))
        {
            var id = node.Expression()!;
            var sym = GetExportSymbolOfValueSymbolIfExported(ResolveEntityName(id, SymbolFlags.All, true /*ignoreErrors*/, true /*dontResolveAlias*/, node));
            if (sym != null)
            {
                MarkLinkedReferences(node, ReferenceHint.ExportAssignment, null, null);
                var typeOnlyDeclaration = GetTypeOnlyAliasDeclarationEx(sym, SymbolFlags.Value);
                // If not a value, we're interpreting the identifier as a type export, along the lines of (`export { Id as default }`)
                if ((GetSymbolFlags(sym) & SymbolFlags.Value) != 0)
                {
                    // However if it is a value, we need to check it's being used correctly
                    CheckExpressionCached(id);
                    if (!isIllegalExportDefaultInCJS && (node.Flags & NodeFlags.Ambient) == 0 && compilerOptions.VerbatimModuleSyntax.IsTrue() && typeOnlyDeclaration != null)
                    {
                        var message = isExportEquals
                            ? Diag.An_export_declaration_must_reference_a_real_value_when_verbatimModuleSyntax_is_enabled_but_0_resolves_to_a_type_only_declaration
                            : Diag.An_export_default_must_reference_a_real_value_when_verbatimModuleSyntax_is_enabled_but_0_resolves_to_a_type_only_declaration;
                        Error(id, message, id.Text());
                    }
                }
                else if (!isIllegalExportDefaultInCJS && (node.Flags & NodeFlags.Ambient) == 0 && compilerOptions.VerbatimModuleSyntax.IsTrue())
                {
                    var message = isExportEquals
                        ? Diag.An_export_declaration_must_reference_a_value_when_verbatimModuleSyntax_is_enabled_but_0_only_refers_to_a_type
                        : Diag.An_export_default_must_reference_a_value_when_verbatimModuleSyntax_is_enabled_but_0_only_refers_to_a_type;
                    Error(id, message, id.Text());
                }
                if (!isIllegalExportDefaultInCJS && (node.Flags & NodeFlags.Ambient) == 0 && compilerOptions.GetIsolatedModules() && (sym.Flags & SymbolFlags.Value) == 0)
                {
                    var nonLocalMeanings = GetSymbolFlagsEx(sym, false /*excludeTypeOnlyMeanings*/, true /*excludeLocalMeanings*/);
                    if ((sym.Flags & SymbolFlags.Alias) != 0 && (nonLocalMeanings & SymbolFlags.Type) != 0 && (nonLocalMeanings & SymbolFlags.Value) == 0 && (typeOnlyDeclaration == null || AstUtilities.GetSourceFileOfNode(typeOnlyDeclaration) != AstUtilities.GetSourceFileOfNode(node)))
                    {
                        // import { SomeType } from "./someModule";
                        // export default SomeType; OR
                        // export = SomeType;
                        var message = isExportEquals
                            ? Diag.X_0_resolves_to_a_type_and_must_be_marked_type_only_in_this_file_before_re_exporting_when_1_is_enabled_Consider_using_import_type_where_0_is_imported
                            : Diag.X_0_resolves_to_a_type_and_must_be_marked_type_only_in_this_file_before_re_exporting_when_1_is_enabled_Consider_using_export_type_0_as_default;
                        Error(id, message, id.Text(), GetIsolatedModulesLikeFlagName());
                    }
                    else if (typeOnlyDeclaration != null && AstUtilities.GetSourceFileOfNode(typeOnlyDeclaration) != AstUtilities.GetSourceFileOfNode(node))
                    {
                        // import { SomeTypeOnlyValue } from "./someModule";
                        // export default SomeTypeOnlyValue; OR
                        // export = SomeTypeOnlyValue;
                        var message = isExportEquals
                            ? Diag.X_0_resolves_to_a_type_only_declaration_and_must_be_marked_type_only_in_this_file_before_re_exporting_when_1_is_enabled_Consider_using_import_type_where_0_is_imported
                            : Diag.X_0_resolves_to_a_type_only_declaration_and_must_be_marked_type_only_in_this_file_before_re_exporting_when_1_is_enabled_Consider_using_export_type_0_as_default;
                        AddTypeOnlyDeclarationRelatedInfo(Error(id, message, id.Text(), GetIsolatedModulesLikeFlagName()), typeOnlyDeclaration, id.Text());
                    }
                }
            }
            else
            {
                CheckExpressionCached(id);
                // doesn't resolve, check as expression to mark as error
            }
        }
        else
        {
            CheckExpressionCached(node.Expression()!);
        }
        if (isIllegalExportDefaultInCJS)
        {
            Error(node, GetVerbatimModuleSyntaxErrorMessage(node));
        }
        CheckExternalModuleExports(container);
        if (node.Type() is { } typeNode && node.Kind == SyntaxKind.ExportAssignment)
        {
            var t = GetTypeFromTypeNode(typeNode);
            var initializerType = CheckExpressionCached(node.Expression()!);
            CheckTypeAssignableToAndOptionallyElaborate(initializerType, t, node.Expression(), node.Expression(), null /*headMessage*/, null);
        }
        if ((node.Flags & NodeFlags.Ambient) != 0 && !AstUtilities.IsEntityNameExpression(node.Expression()!))
        {
            GrammarErrorOnNode(node.Expression()!, Diag.The_expression_of_an_export_assignment_must_be_an_identifier_or_qualified_name_in_an_ambient_context);
        }
        if (isExportEquals)
        {
            // Forbid export= in esm implementation files, and esm mode declaration files
            if (moduleKind >= ModuleKind.ES2015 && moduleKind != ModuleKind.Preserve && (((node.Flags & NodeFlags.Ambient) != 0 && program.GetImpliedNodeFormatForEmit(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.ESNext) || ((node.Flags & NodeFlags.Ambient) == 0 && program.GetImpliedNodeFormatForEmit(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) != ModuleKind.CommonJS)))
            {
                // export assignment is not supported in es6 modules
                GrammarErrorOnNode(node, Diag.Export_assignment_cannot_be_used_when_targeting_ECMAScript_modules_Consider_using_export_default_or_another_module_format_instead);
            }
            else if (moduleKind == ModuleKind.System && (node.Flags & NodeFlags.Ambient) == 0)
            {
                // system modules does not support export assignment
                GrammarErrorOnNode(node, Diag.Export_assignment_is_not_supported_when_module_flag_is_system);
            }
        }
    }

    // checker.go:5672
    internal void CheckExternalModuleExports(Node node)
    {
        var moduleSymbol = GetSymbolOfDeclaration(node)!;
        var links = moduleSymbolLinks.Get(moduleSymbol);
        if (!links.exportsChecked)
        {
            var exportEqualsSymbol = moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals);
            // An export assignment is in error if (a) the module exports value members or (b) if the module exports type or
            // namespace members and the exported entity also exports type or namespace members.
            if (exportEqualsSymbol != null && (HasExportedMembersOfKind(moduleSymbol, SymbolFlags.Value) || HasShadowedNamespace(exportEqualsSymbol)))
            {
                var declaration = GetDeclarationOfAliasSymbol(exportEqualsSymbol) ?? exportEqualsSymbol.ValueDeclaration;
                if (declaration != null && !IsTopLevelInExternalModuleAugmentation(declaration))
                {
                    Error(declaration, Diag.An_export_assignment_cannot_be_used_in_a_module_with_other_exported_elements);
                }
            }
            // Checks for export * conflicts
            // map-iter: 序无关 —— each exported symbol is validated independently; ordering does not affect diagnostics
            foreach (var (id, symbol) in GetExportsOfModule(moduleSymbol))
            {
                if (id == InternalSymbolName.ExportStar)
                {
                    continue;
                }
                // ECMA262: 15.2.1.1 It is a Syntax Error if the ExportedNames of ModuleItemList contains any duplicate entries.
                // (TS Exceptions: namespaces, function overloads, enums, and interfaces)
                if ((symbol.Flags & (SymbolFlags.Namespace | SymbolFlags.Enum)) != 0)
                {
                    continue;
                }
                var exportedDeclarationsCount = symbol.Declarations.OrEmpty().Count(d => IsNotOverload(d) && !AstUtilities.IsAccessor(d) && !NodeTests.IsInterfaceDeclaration(d));
                if ((symbol.Flags & SymbolFlags.TypeAlias) != 0 && exportedDeclarationsCount <= 2)
                {
                    // it is legal to merge type alias with other values
                    // so count should be either 1 (just type alias) or 2 (type alias + merged value)
                    continue;
                }
                if (exportedDeclarationsCount > 1 && !symbol.Declarations.OrEmpty().All(n => AstUtilities.GetAssignmentDeclarationKind(n) == JSDeclarationKind.ExportsProperty))
                {
                    foreach (var declaration in symbol.Declarations!)
                    {
                        if (IsNotOverload(declaration))
                        {
                            Error(declaration, Diag.Cannot_redeclare_exported_variable_0, id);
                        }
                    }
                }
            }
            links.exportsChecked = true;
        }
    }

    // checker.go:6843
    internal bool AreDeclarationFlagsIdentical(Node left, Node right)
    {
        if ((NodeTests.IsParameterDeclaration(left) && NodeTests.IsVariableDeclaration(right)) || (NodeTests.IsVariableDeclaration(left) && NodeTests.IsParameterDeclaration(right)))
        {
            // Differences in optionality between parameters and variables are allowed.
            return true;
        }
        if (IsOptionalDeclaration(left) != IsOptionalDeclaration(right))
        {
            return false;
        }
        var interestingFlags = ModifierFlags.Private | ModifierFlags.Protected | ModifierFlags.Async | ModifierFlags.Abstract | ModifierFlags.Readonly | ModifierFlags.Static;
        return GetSelectedModifierFlags(left, interestingFlags) == GetSelectedModifierFlags(right, interestingFlags);
    }

    // checker.go:6887
    internal void CheckExportsOnMergedDeclarations(Node node)
    {
        // If localSymbol is defined on node then node itself is exported - check is required.
        var symbol = node.LocalSymbol();
        if (symbol == null)
        {
            // Local symbol is undefined => this declaration is non-exported.
            // However, symbol might contain other declarations that are exported.
            symbol = GetSymbolOfDeclaration(node)!;
            if (symbol.ExportSymbol == null)
            {
                // This is a pure local symbol (all declarations are non-exported) - no need to check anything.
                return;
            }
        }
        // Run the check only for the first declaration in the list.
        if (AstUtilities.GetDeclarationOfKind(symbol, node.Kind) != node)
        {
            return;
        }
        var exportedDeclarationSpaces = DeclarationSpaces.None;
        var nonExportedDeclarationSpaces = DeclarationSpaces.None;
        var defaultExportedDeclarationSpaces = DeclarationSpaces.None;
        foreach (var d in symbol.Declarations!)
        {
            var declarationSpaces = GetDeclarationSpaces(d);
            var effectiveDeclarationFlags = GetEffectiveDeclarationFlags(d, ModifierFlags.Export | ModifierFlags.Default);
            if ((effectiveDeclarationFlags & ModifierFlags.Export) != 0)
            {
                if ((effectiveDeclarationFlags & ModifierFlags.Default) != 0)
                {
                    defaultExportedDeclarationSpaces |= declarationSpaces;
                }
                else
                {
                    exportedDeclarationSpaces |= declarationSpaces;
                }
            }
            else
            {
                nonExportedDeclarationSpaces |= declarationSpaces;
            }
        }
        // Spaces for anything not declared a 'default export'.
        var nonDefaultExportedDeclarationSpaces = exportedDeclarationSpaces | nonExportedDeclarationSpaces;
        var commonDeclarationSpacesForExportsAndLocals = exportedDeclarationSpaces & nonExportedDeclarationSpaces;
        var commonDeclarationSpacesForDefaultAndNonDefault = defaultExportedDeclarationSpaces & nonDefaultExportedDeclarationSpaces;
        if (commonDeclarationSpacesForExportsAndLocals != 0 || commonDeclarationSpacesForDefaultAndNonDefault != 0)
        {
            // declaration spaces for exported and non-exported declarations intersect
            foreach (var d in symbol.Declarations!)
            {
                var declarationSpaces = GetDeclarationSpaces(d);
                var name = AstUtilities.GetNameOfDeclaration(d);
                // Only error on the declarations that contributed to the intersecting spaces.
                if ((declarationSpaces & commonDeclarationSpacesForDefaultAndNonDefault) != 0)
                {
                    Error(name, Diag.Merged_declaration_0_cannot_include_a_default_export_declaration_Consider_adding_a_separate_export_default_0_declaration_instead, global::Ts.Scanner.Scanner.DeclarationNameToString(name));
                }
                else if ((declarationSpaces & commonDeclarationSpacesForExportsAndLocals) != 0)
                {
                    Error(name, Diag.Individual_declarations_in_merged_declaration_0_must_be_all_exported_or_all_local, global::Ts.Scanner.Scanner.DeclarationNameToString(name));
                }
            }
        }
    }

    // checker.go:6715
    internal void CheckAliasSymbol(Node node)
    {
        var symbol = GetSymbolOfDeclaration(node)!;
        var (target, _) = ResolveAlias(symbol);
        if (target == unknownSymbol)
        {
            return;
        }
        // For external modules, `symbol` represents the local symbol for an alias.
        // This local symbol will merge any other local declarations (excluding other aliases)
        // and symbol.flags will contains combined representation for all merged declaration.
        // Based on symbol.flags we can compute a set of excluded meanings (meaning that resolved alias should not have,
        // otherwise it will conflict with some local declaration). Note that in addition to normal flags we include matching SymbolFlags.Export*
        // in order to prevent collisions with declarations that were exported from the current module (they still contribute to local names).
        symbol = GetMergedSymbol(symbol.ExportSymbol ?? symbol)!;
        var targetFlags = GetSymbolFlags(target!);
        // A type-only import/export will already have a grammar error in a JS file, so no need to issue more errors within
        if (AstUtilities.IsInJSFile(node) && (targetFlags & SymbolFlags.Value) == 0 && !IsTypeOnlyImportOrExportDeclaration(node))
        {
            var errorNode = node.PropertyNameOrName() ?? node;
            global::Ts.Core.Debug.Assert(node.Kind != SyntaxKind.NamespaceExport);
            if (NodeTests.IsExportSpecifier(node))
            {
                var diag = Error(errorNode, Diag.Types_cannot_appear_in_export_declarations_in_JavaScript_files);
                if (AstUtilities.GetSourceFileOfNode(node)!.AsNode().Symbol() is { } sourceSymbol)
                {
                    if (sourceSymbol.Exports?.GetValueOrDefault(node.PropertyNameOrName()!.Text()) is { } alreadyExportedSymbol && alreadyExportedSymbol == target)
                    {
                        if (alreadyExportedSymbol.Declarations.OrEmpty().FirstOrDefault(NodeTests.IsJSTypeAliasDeclaration) is { } exportingDeclaration && diag != null)
                        {
                            AddRelatedInfo(diag, CreateDiagnosticForNode(exportingDeclaration, Diag.X_0_is_automatically_exported_here, alreadyExportedSymbol.Name));
                        }
                    }
                }
            }
            else
            {
                var identifierText = symbol.Name;
                if (NodeTests.IsIdentifier(errorNode))
                {
                    identifierText = errorNode.Text();
                }
                var specifierText = "...";
                if (AstUtilities.FindAncestor(node, n => IsImportOrImportEqualsDeclaration(n) || NodeTests.IsVariableDeclaration(n)) is { } importDeclaration)
                {
                    if (TryGetModuleSpecifierFromDeclaration(importDeclaration) is { } moduleSpecifier)
                    {
                        specifierText = moduleSpecifier.Text();
                    }
                }
                var importText = "import(\"" + specifierText + "\")";
                if (NodeTests.IsImportSpecifier(node))
                {
                    importText = importText + "." + identifierText;
                }
                Error(errorNode, Diag.X_0_is_a_type_and_cannot_be_imported_in_JavaScript_files_Use_1_in_a_JSDoc_type_annotation, identifierText, importText);
            }
            return;
        }
        var excludedMeanings =
            ((symbol.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue)) != 0 ? SymbolFlags.Value : 0) |
            ((symbol.Flags & SymbolFlags.Type) != 0 ? SymbolFlags.Type : 0) |
            ((symbol.Flags & SymbolFlags.Namespace) != 0 ? SymbolFlags.Namespace : 0);
        if ((targetFlags & excludedMeanings) != 0)
        {
            var message = NodeTests.IsExportSpecifier(node)
                ? Diag.Export_declaration_conflicts_with_exported_declaration_of_0
                : Diag.Import_declaration_conflicts_with_local_declaration_of_0;
            Error(node, message, SymbolToString(symbol));
        }
        else if (!NodeTests.IsExportSpecifier(node))
        {
            // Look at 'compilerOptions.isolatedModules' and not 'getIsolatedModules(...)' (which considers 'verbatimModuleSyntax')
            // here because 'verbatimModuleSyntax' will already have an error for importing a type without 'import type'.
            var appearsValueyToTranspiler = compilerOptions.IsolatedModules.IsTrue() && AstUtilities.FindAncestor(node, IsTypeOnlyImportOrExportDeclaration) == null;
            if (appearsValueyToTranspiler && (symbol.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue)) != 0)
            {
                Error(node, Diag.Import_0_conflicts_with_local_value_so_must_be_declared_with_a_type_only_import_when_isolatedModules_is_enabled, SymbolToString(symbol), GetIsolatedModulesLikeFlagName());
            }
        }
        if (compilerOptions.GetIsolatedModules() && !IsTypeOnlyImportOrExportDeclaration(node) && (node.Flags & NodeFlags.Ambient) == 0)
        {
            var typeOnlyAlias = GetTypeOnlyAliasDeclaration(symbol);
            var isType = (targetFlags & SymbolFlags.Value) == 0;
            if (isType || typeOnlyAlias != null)
            {
                switch (node.Kind)
                {
                    case SyntaxKind.ImportClause:
                    case SyntaxKind.ImportSpecifier:
                    case SyntaxKind.ImportEqualsDeclaration:
                        if (compilerOptions.VerbatimModuleSyntax.IsTrue())
                        {
                            global::Ts.Core.Debug.Assert(node.Name() != null, "An ImportClause with a symbol should have a name");
                            global::Ts.Diagnostics.Message message;
                            if (compilerOptions.VerbatimModuleSyntax.IsTrue() && IsInternalModuleImportEqualsDeclaration(node))
                            {
                                message = Diag.An_import_alias_cannot_resolve_to_a_type_or_type_only_declaration_when_verbatimModuleSyntax_is_enabled;
                            }
                            else if (isType)
                            {
                                message = Diag.X_0_is_a_type_and_must_be_imported_using_a_type_only_import_when_verbatimModuleSyntax_is_enabled;
                            }
                            else
                            {
                                message = Diag.X_0_resolves_to_a_type_only_declaration_and_must_be_imported_using_a_type_only_import_when_verbatimModuleSyntax_is_enabled;
                            }
                            var name = node.PropertyNameOrName()!.Text();
                            AddTypeOnlyDeclarationRelatedInfo(Error(node, message, name), isType ? null : typeOnlyAlias, name);
                        }
                        if (isType && node.Kind == SyntaxKind.ImportEqualsDeclaration && HasModifier(node, ModifierFlags.Export))
                        {
                            Error(node, Diag.Cannot_use_export_import_on_a_type_or_type_only_namespace_when_0_is_enabled, GetIsolatedModulesLikeFlagName());
                        }
                        break;
                    case SyntaxKind.ExportSpecifier:
                        // Don't allow re-exporting an export that will be elided when `--isolatedModules` is set.
                        // The exception is that `import type { A } from './a'; export { A }` is allowed
                        // because single-file analysis can determine that the export should be dropped.
                        if (compilerOptions.VerbatimModuleSyntax.IsTrue() || AstUtilities.GetSourceFileOfNode(typeOnlyAlias) != AstUtilities.GetSourceFileOfNode(node))
                        {
                            var name = node.PropertyNameOrName()!.Text();
                            Diagnostic? diagnostic;
                            if (isType)
                            {
                                diagnostic = Error(node, Diag.Re_exporting_a_type_when_0_is_enabled_requires_using_export_type, GetIsolatedModulesLikeFlagName());
                            }
                            else
                            {
                                diagnostic = Error(node, Diag.X_0_resolves_to_a_type_only_declaration_and_must_be_re_exported_using_a_type_only_re_export_when_1_is_enabled, name, GetIsolatedModulesLikeFlagName());
                            }
                            AddTypeOnlyDeclarationRelatedInfo(diagnostic, isType ? null : typeOnlyAlias, name);
                        }
                        break;
                }
            }
            if (compilerOptions.VerbatimModuleSyntax.IsTrue() && !NodeTests.IsImportEqualsDeclaration(node) && !AstUtilities.IsInJSFile(node) && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
            {
                Error(node, GetVerbatimModuleSyntaxErrorMessage(node));
            }
            else if (moduleKind == ModuleKind.Preserve && !NodeTests.IsImportEqualsDeclaration(node) && !NodeTests.IsVariableDeclaration(node) && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
            {
                // In `--module preserve`, ESM input syntax emits ESM output syntax, but there will be times
                // when we look at the `impliedNodeFormat` of this file and decide it's CommonJS (i.e., currently,
                // only if the file extension is .cjs/.cts). To avoid that inconsistency, we disallow ESM syntax
                // in files that are unambiguously CommonJS in this mode.
                Error(node, Diag.ECMAScript_module_syntax_is_not_allowed_in_a_CommonJS_module_when_module_is_set_to_preserve);
            }
            if (compilerOptions.VerbatimModuleSyntax.IsTrue() && !IsTypeOnlyImportOrExportDeclaration(node) && (node.Flags & NodeFlags.Ambient) == 0 && (targetFlags & SymbolFlags.ConstEnum) != 0)
            {
                var constEnumDeclaration = target!.ValueDeclaration!;
                var redirect = program.GetProjectReferenceFromOutputDts(AstUtilities.GetSourceFileOfNode(constEnumDeclaration)!.Path());
                if ((constEnumDeclaration.Flags & NodeFlags.Ambient) != 0 && (redirect == null || !redirect.Resolved!.CompilerOptions()!.ShouldPreserveConstEnums()))
                {
                    Error(node, Diag.Cannot_access_ambient_const_enums_when_0_is_enabled, GetIsolatedModulesLikeFlagName());
                }
            }
        }
        if (NodeTests.IsImportSpecifier(node))
        {
            var targetSymbol = ResolveAliasWithDeprecationCheck(symbol, node);
            if (IsDeprecatedSymbol(targetSymbol) && targetSymbol.Declarations != null)
            {
                AddDeprecatedSuggestion(node, targetSymbol.Declarations, targetSymbol.Name);
            }
        }
    }
}
