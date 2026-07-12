// Ported from tsgo internal/checker/checker.go (overload/implementation checks, plan/03 §4.2):
//   checkFunctionOrConstructorSymbol 3447 / checkFunctionOrConstructorSymbolWorker 3455 /
//   getEffectiveDeclarationFlags 3687 / isImplementationCompatibleWithOverload 3702 /
//   getEnclosingContainer (utilities.go:1231).
//
// Faithful per-function translation: same order, same branch structure. Go's inner closures
// (getCanonicalOverload / checkFlagAgreementBetweenOverloads / checkQuestionTokenAgreementBetweenOverloads /
// reportImplementationExpectedError) become local functions. Go comments preserved.
//
// §1.5: the cross-package Diagnostics class is shadowed by the sibling Ts.Diagnostics namespace, so it
// is reached through the `Diag` alias; the no-collision hosts (NodeTests/AstUtilities/CoreUtil/Scanner)
// and the in-package Binder class are used directly (Binder host does not collide with a sibling ns here).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:3447
    internal void CheckFunctionOrConstructorSymbol(Symbol symbol)
    {
        // Only check the symbol once
        var links = valueSymbolLinks.Get(symbol);
        if (!links.functionOrConstructorChecked)
        {
            links.functionOrConstructorChecked = true;
            CheckFunctionOrConstructorSymbolWorker(symbol);
        }
    }

    // checker.go:3455
    internal void CheckFunctionOrConstructorSymbolWorker(Symbol symbol)
    {
        var flagsToCheck = ModifierFlags.Export | ModifierFlags.Ambient | ModifierFlags.Private | ModifierFlags.Protected | ModifierFlags.Abstract;
        var someNodeFlags = ModifierFlags.None;
        var allNodeFlags = flagsToCheck;
        bool someHaveQuestionToken = false;
        bool allHaveQuestionToken = true;
        bool hasOverloads = false;
        Node? bodyDeclaration = null;
        Node? lastSeenNonAmbientDeclaration = null;
        Node? previousDeclaration = null;
        var declarations = symbol.Declarations;
        bool isConstructor = (symbol.Flags & SymbolFlags.Constructor) != 0;
        bool duplicateFunctionDeclaration = false;
        bool multipleConstructorImplementation = false;
        bool hasNonAmbientClass = false;
        var functionDeclarations = new List<Node>();

        Node GetCanonicalOverload(IReadOnlyList<Node> overloads, Node? implementation)
        {
            // Consider the canonical set of flags to be the flags of the bodyDeclaration or the first declaration
            // Error on all deviations from this canonical set of flags
            // The caveat is that if some overloads are defined in lib.d.ts, we don't want to
            // report the errors on those. To achieve this, we will say that the implementation is
            // the canonical signature only if it is in the same container as the first overload
            bool implementationSharesContainerWithFirstOverload = implementation != null && implementation.Parent == overloads[0].Parent;
            if (implementationSharesContainerWithFirstOverload)
            {
                return implementation!;
            }
            return overloads[0];
        }

        void CheckFlagAgreementBetweenOverloads(IReadOnlyList<Node> overloads, Node? implementation, ModifierFlags flagsToCheckLocal, ModifierFlags someOverloadFlags, ModifierFlags allOverloadFlags)
        {
            // Error if some overloads have a flag that is not shared by all overloads. To find the
            // deviations, we XOR someOverloadFlags with allOverloadFlags
            var someButNotAllOverloadFlags = someOverloadFlags ^ allOverloadFlags;
            if (someButNotAllOverloadFlags != 0)
            {
                var canonicalFlags = GetEffectiveDeclarationFlags(GetCanonicalOverload(overloads, implementation), flagsToCheckLocal);
                var groups = new Dictionary<SourceFile, List<Node>>(ReferenceEqualityComparer.Instance);
                var groupOrder = new List<SourceFile>();
                foreach (var overload in overloads)
                {
                    var sourceFile = AstUtilities.GetSourceFileOfNode(overload)!;
                    if (!groups.TryGetValue(sourceFile, out var list))
                    {
                        list = [];
                        groups[sourceFile] = list;
                        groupOrder.Add(sourceFile);
                    }
                    list.Add(overload);
                }
                // map-iter: 已排序 — iterate in first-insertion order to match Go's per-file grouping traversal.
                foreach (var sourceFile in groupOrder)
                {
                    var overloadsInFile = groups[sourceFile];
                    var canonicalFlagsForFile = GetEffectiveDeclarationFlags(GetCanonicalOverload(overloadsInFile, implementation), flagsToCheckLocal);
                    foreach (var overload in overloadsInFile)
                    {
                        var deviation = GetEffectiveDeclarationFlags(overload, flagsToCheckLocal) ^ canonicalFlags;
                        var deviationInFile = GetEffectiveDeclarationFlags(overload, flagsToCheckLocal) ^ canonicalFlagsForFile;
                        if ((deviationInFile & ModifierFlags.Export) != 0)
                        {
                            // Overloads in different files need not all have export modifiers. This is ok:
                            //   // lib.d.ts
                            //   declare function foo(s: number): string;
                            //   declare function foo(s: string): number;
                            //   export { foo };
                            //
                            //   // app.ts
                            //   declare module "lib" {
                            //     export function foo(s: boolean): boolean;
                            //   }
                            Error(AstUtilities.GetNameOfDeclaration(overload), Diag.Overload_signatures_must_all_be_exported_or_non_exported);
                        }
                        else if ((deviationInFile & ModifierFlags.Ambient) != 0)
                        {
                            // Though rare, a module augmentation (necessarily ambient) is allowed to add overloads
                            // to a non-ambient function in an implementation file.
                            Error(AstUtilities.GetNameOfDeclaration(overload), Diag.Overload_signatures_must_all_be_ambient_or_non_ambient);
                        }
                        else if ((deviation & (ModifierFlags.Private | ModifierFlags.Protected)) != 0)
                        {
                            Error(CoreUtil.OrElse(AstUtilities.GetNameOfDeclaration(overload), overload), Diag.Overload_signatures_must_all_be_public_private_or_protected);
                        }
                        else if ((deviation & ModifierFlags.Abstract) != 0)
                        {
                            Error(AstUtilities.GetNameOfDeclaration(overload), Diag.Overload_signatures_must_all_be_abstract_or_non_abstract);
                        }
                    }
                }
            }
        }

        void CheckQuestionTokenAgreementBetweenOverloads(IReadOnlyList<Node> overloads, Node? implementation, bool someHaveQuestionTokenLocal, bool allHaveQuestionTokenLocal)
        {
            if (someHaveQuestionTokenLocal != allHaveQuestionTokenLocal)
            {
                bool canonicalHasQuestionToken = IsOptionalDeclaration(GetCanonicalOverload(overloads, implementation));
                foreach (var o in overloads)
                {
                    if (IsOptionalDeclaration(o) != canonicalHasQuestionToken)
                    {
                        Error(AstUtilities.GetNameOfDeclaration(o), Diag.Overload_signatures_must_all_be_optional_or_required);
                    }
                }
            }
        }

        void ReportImplementationExpectedError(Node node)
        {
            var name = node.Name();
            if (name != null && AstUtilities.NodeIsMissing(name))
            {
                return;
            }
            bool seen = false;
            Node? subsequentNode = null;
            node.Parent!.ForEachChild(child =>
            {
                if (seen)
                {
                    subsequentNode = child;
                    return true;
                }
                seen = child == node;
                return false;
            });
            // We may be here because of some extra nodes between overloads that could not be parsed into a valid node.
            // In this case the subsequent node is not really consecutive (.pos !== node.end), and we must ignore it here.
            if (subsequentNode != null && subsequentNode.Pos == node.End)
            {
                if (subsequentNode.Kind == node.Kind)
                {
                    var subsequentName = subsequentNode.Name();
                    var errorNode2 = CoreUtil.OrElse(subsequentName, subsequentNode);
                    if (name != null && subsequentName != null &&
                        (NodeTests.IsPrivateIdentifier(name) && NodeTests.IsPrivateIdentifier(subsequentName) && name.Text() == subsequentName.Text() ||
                            NodeTests.IsComputedPropertyName(name) && NodeTests.IsComputedPropertyName(subsequentName) && IsTypeIdenticalTo(CheckComputedPropertyName(name), CheckComputedPropertyName(subsequentName)) ||
                            AstUtilities.IsPropertyNameLiteral(name) && AstUtilities.IsPropertyNameLiteral(subsequentName) && name.Text() == subsequentName.Text()))
                    {
                        bool reportError2 = (NodeTests.IsMethodDeclaration(node) || NodeTests.IsMethodSignatureDeclaration(node)) && AstUtilities.IsStatic(node) != AstUtilities.IsStatic(subsequentNode);
                        // we can get here in two cases
                        // 1. mixed static and instance class members
                        // 2. something with the same name was defined before the set of overloads that prevents them from merging
                        // here we'll report error only for the first case since for second we should already report error in binder
                        if (reportError2)
                        {
                            var diagnostic2 = CoreUtil.IfElse(AstUtilities.IsStatic(node), Diag.Function_overload_must_be_static, Diag.Function_overload_must_not_be_static);
                            Error(errorNode2, diagnostic2);
                        }
                        return;
                    }
                    if (AstUtilities.NodeIsPresent(subsequentNode.Body()))
                    {
                        Error(errorNode2, Diag.Function_implementation_name_must_be_0, global::Ts.Scanner.Scanner.DeclarationNameToString(name));
                        return;
                    }
                }
            }
            var errorNode = CoreUtil.OrElse(name, node);
            if (isConstructor)
            {
                Error(errorNode, Diag.Constructor_implementation_is_missing);
            }
            else
            {
                // Report different errors regarding non-consecutive blocks of declarations depending on whether
                // the node in question is abstract.
                if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Abstract))
                {
                    Error(errorNode, Diag.All_declarations_of_an_abstract_method_must_be_consecutive);
                }
                else
                {
                    Error(errorNode, Diag.Function_implementation_is_missing_or_not_immediately_following_the_declaration);
                }
            }
        }

        foreach (var node in declarations!)
        {
            bool inAmbientContext = (node.Flags & NodeFlags.Ambient) != 0;
            bool inAmbientContextOrInterface = inAmbientContext || node.Parent != null && (NodeTests.IsInterfaceDeclaration(node.Parent) || NodeTests.IsTypeLiteralNode(node.Parent));
            if (inAmbientContextOrInterface)
            {
                // check if declarations are consecutive only if they are non-ambient
                // 1. ambient declarations can be interleaved
                // i.e. this is legal
                //     declare function foo();
                //     declare function bar();
                //     declare function foo();
                // 2. mixing ambient and non-ambient declarations is a separate error that will be reported - do not want to report an extra one
                previousDeclaration = null;
            }
            if (AstUtilities.IsClassLike(node) && !inAmbientContext)
            {
                hasNonAmbientClass = true;
            }
            if (NodeTests.IsFunctionDeclaration(node) || NodeTests.IsMethodDeclaration(node) || NodeTests.IsMethodSignatureDeclaration(node) || NodeTests.IsConstructorDeclaration(node))
            {
                functionDeclarations.Add(node);
                var currentNodeFlags = GetEffectiveDeclarationFlags(node, flagsToCheck);
                someNodeFlags |= currentNodeFlags;
                allNodeFlags &= currentNodeFlags;
                someHaveQuestionToken = someHaveQuestionToken || IsOptionalDeclaration(node);
                allHaveQuestionToken = allHaveQuestionToken && IsOptionalDeclaration(node);
                bool bodyIsPresent = AstUtilities.NodeIsPresent(node.Body());
                if (bodyIsPresent && bodyDeclaration != null)
                {
                    if (isConstructor)
                    {
                        multipleConstructorImplementation = true;
                    }
                    else
                    {
                        duplicateFunctionDeclaration = true;
                    }
                }
                else if (previousDeclaration != null && previousDeclaration.Parent == node.Parent && previousDeclaration.End != node.Pos && (previousDeclaration.Flags & NodeFlags.Reparsed) == 0)
                {
                    ReportImplementationExpectedError(previousDeclaration);
                }
                if (bodyIsPresent)
                {
                    if (bodyDeclaration == null)
                    {
                        bodyDeclaration = node;
                    }
                }
                else
                {
                    hasOverloads = true;
                }
                previousDeclaration = node;
                if (!inAmbientContextOrInterface)
                {
                    lastSeenNonAmbientDeclaration = node;
                }
            }
        }
        if (multipleConstructorImplementation)
        {
            foreach (var declaration in functionDeclarations)
            {
                Error(declaration, Diag.Multiple_constructor_implementations_are_not_allowed);
            }
        }
        if (duplicateFunctionDeclaration)
        {
            foreach (var declaration in functionDeclarations)
            {
                Error(CoreUtil.OrElse(AstUtilities.GetNameOfDeclaration(declaration), declaration), Diag.Duplicate_function_implementation);
            }
        }
        if (hasNonAmbientClass && !isConstructor && (symbol.Flags & SymbolFlags.Function) != 0 && declarations.Length != 0)
        {
            var relatedDiagnostics = new List<Diagnostic>();
            foreach (var declaration in declarations)
            {
                if (NodeTests.IsClassDeclaration(declaration))
                {
                    relatedDiagnostics.Add(CreateDiagnosticForNode(declaration, Diag.Consider_adding_a_declare_modifier_to_this_class));
                }
            }
            foreach (var declaration in declarations)
            {
                global::Ts.Diagnostics.Message? diagnostic = null;
                switch (declaration.Kind)
                {
                    case SyntaxKind.ClassDeclaration:
                        diagnostic = Diag.Class_declaration_cannot_implement_overload_list_for_0;
                        break;
                    case SyntaxKind.FunctionDeclaration:
                        diagnostic = Diag.Function_with_bodies_can_only_merge_with_classes_that_are_ambient;
                        break;
                }
                if (diagnostic != null)
                {
                    Error(CoreUtil.OrElse(AstUtilities.GetNameOfDeclaration(declaration), declaration), diagnostic, symbol.Name).SetRelatedInfo(relatedDiagnostics.ToArray());
                }
            }
        }
        // Abstract methods can't have an implementation -- in particular, they don't need one.
        if (lastSeenNonAmbientDeclaration != null && lastSeenNonAmbientDeclaration.Body() == null && !AstUtilities.HasSyntacticModifier(lastSeenNonAmbientDeclaration, ModifierFlags.Abstract) && !IsOptionalDeclaration(lastSeenNonAmbientDeclaration))
        {
            ReportImplementationExpectedError(lastSeenNonAmbientDeclaration);
        }
        if (hasOverloads)
        {
            CheckFlagAgreementBetweenOverloads(declarations, bodyDeclaration, flagsToCheck, someNodeFlags, allNodeFlags);
            CheckQuestionTokenAgreementBetweenOverloads(declarations, bodyDeclaration, someHaveQuestionToken, allHaveQuestionToken);
            if (bodyDeclaration != null)
            {
                var signatures = GetSignaturesOfSymbol(symbol);
                var bodySignature = GetSignatureFromDeclaration(bodyDeclaration);
                foreach (var signature in signatures)
                {
                    if (!IsImplementationCompatibleWithOverload(bodySignature, signature))
                    {
                        var errorNode = signature.declaration;
                        Error(errorNode, Diag.This_overload_signature_is_not_compatible_with_its_implementation_signature).AddRelatedInfo(CreateDiagnosticForNode(bodyDeclaration, Diag.The_implementation_signature_is_declared_here));
                        break;
                    }
                }
            }
        }
    }

    // checker.go:3687
    internal ModifierFlags GetEffectiveDeclarationFlags(Node n, ModifierFlags flagsToCheck)
    {
        var flags = GetCombinedModifierFlagsCached(n);
        // children of classes (even ambient classes) should not be marked as ambient or export
        // because those flags have no useful semantics there.
        if (!NodeTests.IsInterfaceDeclaration(n.Parent!) && !NodeTests.IsClassDeclaration(n.Parent!) && !NodeTests.IsClassExpression(n.Parent!) && (n.Flags & NodeFlags.Ambient) != 0)
        {
            var container = GetEnclosingContainer(n);
            if (container != null && (container.Flags & NodeFlags.ExportContext) != 0 && (flags & ModifierFlags.Ambient) == 0 && !(NodeTests.IsModuleBlock(n.Parent!) && AstUtilities.IsGlobalScopeAugmentation(n.Parent!.Parent!)))
            {
                // It is nested in an ambient export context, which means it is automatically exported
                flags |= ModifierFlags.Export;
            }
            flags |= ModifierFlags.Ambient;
        }
        return flags & flagsToCheck;
    }

    // checker.go:3702
    internal bool IsImplementationCompatibleWithOverload(Signature implementation, Signature overload)
    {
        var erasedSource = GetErasedSignature(implementation);
        var erasedTarget = GetErasedSignature(overload);
        // First see if the return types are compatible in either direction.
        var sourceReturnType = GetReturnTypeOfSignature(erasedSource);
        var targetReturnType = GetReturnTypeOfSignature(erasedTarget);
        if (targetReturnType == voidType || IsTypeRelatedTo(targetReturnType, sourceReturnType, assignableRelation!) || IsTypeRelatedTo(sourceReturnType, targetReturnType, assignableRelation!))
        {
            return IsSignatureAssignableTo(erasedSource, erasedTarget, true /*ignoreReturnTypes*/);
        }
        return false;
    }

    // utilities.go:1231
    internal static Node? GetEnclosingContainer(Node node)
    {
        return AstUtilities.FindAncestor(node.Parent, n =>
            (global::Ts.Binder.Binder.GetContainerFlags(n) & global::Ts.Binder.ContainerFlags.IsContainer) != 0);
    }

    // checker.go:3714
    internal void CheckAllCodePathsInNonVoidFunctionReturnOrThrow(Node fn, Type? returnType)
    {
        var functionFlags = AstUtilities.GetFunctionFlags(fn);
        Type? t = null;
        if (returnType != null)
        {
            t = UnwrapReturnType(returnType, functionFlags);
        }
        // Functions with an explicitly specified return type that includes `void` or is exactly `any` or `undefined` don't
        // need any return statements.
        if (t != null && (MaybeTypeOfKind(t, TypeFlags.Void) || (t.flags & (TypeFlags.Any | TypeFlags.Undefined)) != 0))
        {
            return;
        }
        // If all we have is a function signature, or an arrow function with an expression body, then there is nothing to check.
        // also if HasImplicitReturn flag is not set this means that all codepaths in function body end with return or throw
        if (NodeTests.IsMethodSignatureDeclaration(fn) || AstUtilities.NodeIsMissing(fn.Body()) || !NodeTests.IsBlock(fn.Body()!) || !FunctionHasImplicitReturn(fn))
        {
            return;
        }
        bool hasExplicitReturn = (fn.Flags & NodeFlags.HasExplicitReturn) != 0;
        Node? errorNode = fn.Type();
        if (errorNode == null)
        {
            var data = fn.FunctionLikeData();
            if (data != null && data.FullSignature != null)
            {
                errorNode = data.FullSignature;
            }
        }
        if (errorNode == null)
        {
            errorNode = fn;
        }
        if (t != null && (t.flags & TypeFlags.Never) != 0)
        {
            Error(errorNode, Diag.A_function_returning_never_cannot_have_a_reachable_end_point);
        }
        else if (t != null && !hasExplicitReturn)
        {
            // minimal check: function has syntactic return type annotation and no explicit return statements in the body
            // this function does not conform to the specification.
            Error(errorNode, Diag.A_function_whose_declared_type_is_neither_undefined_void_nor_any_must_return_a_value);
        }
        else if (t != null && strictNullChecks && !IsTypeAssignableTo(undefinedType!, t))
        {
            Error(errorNode, Diag.Function_lacks_ending_return_statement_and_return_type_does_not_include_undefined);
        }
        else if (compilerOptions.NoImplicitReturns == Tristate.True)
        {
            if (t == null)
            {
                // If return type annotation is omitted check if function has any explicit return statements.
                // If it does not have any - its inferred return type is void - don't do any checks.
                // Otherwise get inferred return type from function body and report error only if it is not void / anytype
                if (!hasExplicitReturn)
                {
                    return;
                }
                var inferredReturnType = GetReturnTypeOfSignature(GetSignatureFromDeclaration(fn));
                if (IsUnwrappedReturnTypeUndefinedVoidOrAny(fn, inferredReturnType))
                {
                    return;
                }
            }
            Error(errorNode, Diag.Not_all_code_paths_return_a_value);
        }
    }

    // checker.go:3766
    internal bool IsUnwrappedReturnTypeUndefinedVoidOrAny(Node fn, Type returnType)
    {
        var t = UnwrapReturnType(returnType, AstUtilities.GetFunctionFlags(fn));
        return t != null && (MaybeTypeOfKind(t, TypeFlags.Void) || (t.flags & (TypeFlags.Any | TypeFlags.Undefined)) != 0);
    }
}
