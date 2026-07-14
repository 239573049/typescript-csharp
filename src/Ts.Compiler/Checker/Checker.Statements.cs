// Ported from tsgo internal/checker/checker.go (checker.go:2182-7016), plan/03 §4.2 #5.
// The top-level check* dispatch wave — checkSourceFile → checkSourceElement(Worker) big switch
// + all statement/declaration check* functions. Faithful per-function translation: same function
// order as Go, same branch structure; Go comments and `// !!!` markers preserved.
//
// BREADTH-FIRST (task mandate, 4834-line range): this file carries the full dispatch backbone and
// the common statement/variable/function/type-alias checks translated in full. The complex
// declaration validators that pull in large not-yet-landed subsystems (full class/interface/enum
// validation, override/index/member-conflict checks, module/import/export resolution, the whole
// iteration-types family) are stubbed in Checker.Statements.Deps.cs with a note and remain future
// work — see that file's checklist. All cross-cluster methods this file CALLS but does not own are
// single-dedup stubbed in Checker.Statements.Deps.cs (or already stubbed elsewhere).
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified; the
// no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used directly, as in relater/inference/flow.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:2182
    internal void CheckSourceFile(SourceFile sourceFile, bool checkUnused)
    {
        var links = sourceFileLinks.Get(sourceFile);
        if (!links.typeChecked)
        {
            saveDeferredDiagnostics = true;
            // !!! tracing: defer tr.Push(...) omitted at skeleton level (tracer surface not yet landed).
            // Grammar checking
            CheckGrammarSourceFile(sourceFile);
            renamedBindingElementsInTypes = null;
            CheckSourceElements(sourceFile.Statements!.Nodes);
            CheckDeferredNodes(sourceFile);
            if (AstUtilities.IsExternalOrCommonJSModule(sourceFile))
            {
                CheckExternalModuleExports(sourceFile.AsNode());
                RegisterForUnusedIdentifiersCheck(sourceFile.AsNode());
            }
            if (!sourceFile.IsDeclarationFile && !IsCanceled())
            {
                CheckUnusedRenamedBindingElements();
            }
            saveDeferredDiagnostics = false;
            ProduceDeferredDiagnostics();
            reportedUnreachableNodes.Clear();
            links.typeChecked = true;
        }
        if (checkUnused && !links.unusedChecked)
        {
            // The unused identifiers check relies on a full type check having first been performed
            if (!sourceFile.IsDeclarationFile && !IsCanceled())
            {
                CheckUnusedIdentifiers(links.identifierCheckNodes!);
            }
            links.unusedChecked = true;
        }
        if (IsCanceled())
        {
            wasCanceled = true;
        }
    }

    // checker.go:7017-7021
    internal void RegisterForUnusedIdentifiersCheck(Node node)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(node);
        var links = sourceFileLinks.Get(sourceFile!);
        links.identifierCheckNodes = SlicesAppend(links.identifierCheckNodes, node);
    }

    // checker.go:2220
    // PORT-NOTE: Go `nodes []*ast.Node` is nil-tolerant (`range nil` is zero iterations); callers pass
    // `node.TypeArguments()`/`node.Members()` etc. which can be nil. Signature made nullable and the loop
    // uses OrEmpty() so the `range nil` semantics survive (05-conventions.md §6.1).
    internal void CheckSourceElements(Node[]? nodes)
    {
        foreach (var node in nodes.OrEmpty())
        {
            if (IsCanceled())
            {
                break;
            }
            CheckSourceElement(node);
        }
    }

    // checker.go:2229
    internal bool CheckSourceElement(Node? node)
    {
        if (node != null)
        {
            var saveCurrentNode = currentNode;
            var saveWithinUnreachableCode = withinUnreachableCode;
            currentNode = node;
            instantiationCount = 0;
            CheckSourceElementWorker(node);
            currentNode = saveCurrentNode;
            withinUnreachableCode = saveWithinUnreachableCode;
        }
        return false;
    }

    // checker.go:2242
    internal void CheckSourceElementWorker(Node node)
    {
        foreach (var jsdoc in node.EagerJSDoc(null).OrEmpty())
        {
            CheckJSDocComments(jsdoc);
            var tags = jsdoc.AsJSDoc().Tags;
            if (tags != null)
            {
                foreach (var tag in tags.Nodes)
                {
                    CheckJSDocComments(tag);
                }
            }
        }

        if (!withinUnreachableCode && compilerOptions.AllowUnreachableCode != Tristate.True)
        {
            if (CheckSourceElementUnreachable(node))
            {
                withinUnreachableCode = true;
            }
        }

        switch (node.Kind)
        {
            case SyntaxKind.TypeParameter:
                CheckTypeParameter(node);
                break;
            case SyntaxKind.Parameter:
                CheckParameter(node);
                break;
            case SyntaxKind.PropertyDeclaration:
                CheckPropertyDeclaration(node);
                break;
            case SyntaxKind.PropertySignature:
                CheckPropertySignature(node);
                break;
            case SyntaxKind.ConstructorType:
            case SyntaxKind.FunctionType:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.IndexSignature:
                CheckSignatureDeclaration(node);
                break;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                CheckMethodDeclaration(node);
                break;
            case SyntaxKind.ClassStaticBlockDeclaration:
                CheckClassStaticBlockDeclaration(node);
                break;
            case SyntaxKind.Constructor:
                CheckConstructorDeclaration(node);
                break;
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                CheckAccessorDeclaration(node);
                break;
            case SyntaxKind.TypeReference:
                CheckTypeReferenceNode(node);
                break;
            case SyntaxKind.TypePredicate:
                CheckTypePredicate(node);
                break;
            case SyntaxKind.TypeQuery:
                CheckTypeQuery(node);
                break;
            case SyntaxKind.TypeLiteral:
                CheckTypeLiteral(node);
                break;
            case SyntaxKind.ArrayType:
                CheckArrayType(node);
                break;
            case SyntaxKind.TupleType:
                CheckTupleType(node);
                break;
            case SyntaxKind.UnionType:
            case SyntaxKind.IntersectionType:
                CheckUnionOrIntersectionType(node);
                break;
            case SyntaxKind.ParenthesizedType:
            case SyntaxKind.OptionalType:
            case SyntaxKind.RestType:
                node.ForEachChild(CheckSourceElement);
                break;
            case SyntaxKind.ThisType:
                CheckThisType(node);
                break;
            case SyntaxKind.TypeOperator:
                CheckTypeOperator(node);
                break;
            case SyntaxKind.ConditionalType:
                CheckConditionalType(node);
                break;
            case SyntaxKind.InferType:
                CheckInferType(node);
                break;
            case SyntaxKind.TemplateLiteralType:
                CheckTemplateLiteralType(node);
                break;
            case SyntaxKind.ImportType:
                CheckImportType(node);
                break;
            case SyntaxKind.NamedTupleMember:
                CheckNamedTupleMember(node);
                break;
            case SyntaxKind.IndexedAccessType:
                CheckIndexedAccessType(node);
                break;
            case SyntaxKind.MappedType:
                CheckMappedType(node);
                break;
            case SyntaxKind.FunctionDeclaration:
                CheckFunctionDeclaration(node);
                break;
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
                CheckBlock(node);
                break;
            case SyntaxKind.VariableStatement:
                CheckVariableStatement(node);
                break;
            case SyntaxKind.ExpressionStatement:
                CheckExpressionStatement(node);
                break;
            case SyntaxKind.IfStatement:
                CheckIfStatement(node);
                break;
            case SyntaxKind.DoStatement:
                CheckDoStatement(node);
                break;
            case SyntaxKind.WhileStatement:
                CheckWhileStatement(node);
                break;
            case SyntaxKind.ForStatement:
                CheckForStatement(node);
                break;
            case SyntaxKind.ForInStatement:
                CheckForInStatement(node);
                break;
            case SyntaxKind.ForOfStatement:
                CheckForOfStatement(node);
                break;
            case SyntaxKind.ContinueStatement:
            case SyntaxKind.BreakStatement:
                CheckBreakOrContinueStatement(node);
                break;
            case SyntaxKind.ReturnStatement:
                CheckReturnStatement(node);
                break;
            case SyntaxKind.WithStatement:
                CheckWithStatement(node);
                break;
            case SyntaxKind.SwitchStatement:
                CheckSwitchStatement(node);
                break;
            case SyntaxKind.LabeledStatement:
                CheckLabeledStatement(node);
                break;
            case SyntaxKind.ThrowStatement:
                CheckThrowStatement(node);
                break;
            case SyntaxKind.TryStatement:
                CheckTryStatement(node);
                break;
            case SyntaxKind.VariableDeclaration:
                CheckVariableDeclaration(node);
                break;
            case SyntaxKind.BindingElement:
                CheckBindingElement(node);
                break;
            case SyntaxKind.ClassDeclaration:
                CheckClassDeclaration(node);
                break;
            case SyntaxKind.InterfaceDeclaration:
                CheckInterfaceDeclaration(node);
                break;
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                CheckTypeAliasDeclaration(node);
                break;
            case SyntaxKind.EnumDeclaration:
                CheckEnumDeclaration(node);
                break;
            case SyntaxKind.EnumMember:
                CheckEnumMember(node);
                break;
            case SyntaxKind.ModuleDeclaration:
                CheckModuleDeclaration(node);
                break;
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                CheckImportDeclaration(node);
                break;
            case SyntaxKind.ImportEqualsDeclaration:
                CheckImportEqualsDeclaration(node);
                break;
            case SyntaxKind.ExportDeclaration:
                CheckExportDeclaration(node);
                break;
            case SyntaxKind.ExportAssignment:
                CheckExportAssignment(node);
                break;
            case SyntaxKind.EmptyStatement:
                CheckGrammarStatementInAmbientContext(node);
                break;
            case SyntaxKind.DebuggerStatement:
                CheckGrammarStatementInAmbientContext(node);
                break;
            case SyntaxKind.MissingDeclaration:
                CheckMissingDeclaration(node);
                break;
            case SyntaxKind.JSDocNonNullableType:
            case SyntaxKind.JSDocNullableType:
            case SyntaxKind.JSDocAllType:
            case SyntaxKind.JSDocTypeLiteral:
                CheckJSDocType(node);
                break;
        }
    }

    // checker.go:2380
    internal bool CheckSourceElementUnreachable(Node node)
    {
        if (!AstUtilities.IsPotentiallyExecutableNode(node))
        {
            return false;
        }

        if (reportedUnreachableNodes.Has(node))
        {
            return true;
        }

        if (!IsSourceElementUnreachable(node))
        {
            return false;
        }

        reportedUnreachableNodes.Add(node);

        var sourceFile = AstUtilities.GetSourceFileOfNode(node);

        var startNode = node;
        var endNode = node;

        var parent = node.Parent!;
        if (parent.CanHaveStatements())
        {
            var statements = parent.Statements()!;
            var offset = Array.IndexOf(statements, node);
            if (offset >= 0)
            {
                // Scan backwards to find the first unreachable unreported node;
                // this may happen when producing region diagnostics where not all nodes
                // will have been visited.
                // TODO: enable this code once we support region diagnostics again.
                var first = offset;
                // for i := offset - 1; i >= 0; i-- { ... }  (disabled upstream)

                var last = offset;
                for (int i = offset + 1; i < statements.Length; i++)
                {
                    var nextNode = statements[i];
                    if (!AstUtilities.IsPotentiallyExecutableNode(nextNode) || !IsSourceElementUnreachable(nextNode))
                    {
                        break;
                    }
                    last = i;
                    reportedUnreachableNodes.Add(nextNode);
                }

                startNode = statements[first];
                endNode = statements[last];
            }
        }

        var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(startNode, sourceFile!, false /*includeJSDoc*/);

        var diagnostic = Diagnostic.NewDiagnostic(sourceFile, new TextRange(start, endNode.End), Diag.Unreachable_code_detected);
        AddErrorOrSuggestion(compilerOptions.AllowUnreachableCode == Tristate.False, diagnostic);

        return true;
    }

    // checker.go:2441
    internal bool IsSourceElementUnreachable(Node node)
    {
        // Precondition: ast.IsPotentiallyExecutableNode is true
        if ((node.Flags & NodeFlags.Unreachable) != 0)
        {
            // The binder has determined that this code is unreachable.
            // Ignore const enums unless preserveConstEnums is set.
            switch (node.Kind)
            {
                case SyntaxKind.EnumDeclaration:
                    return !AstUtilities.IsEnumConst(node) || compilerOptions.ShouldPreserveConstEnums();
                case SyntaxKind.ModuleDeclaration:
                    return AstUtilities.IsInstantiatedModule(node, compilerOptions.ShouldPreserveConstEnums());
                default:
                    return true;
            }
        }
        else
        {
            var flowNode = node.FlowNodeData()?.FlowNode;
            if (flowNode != null)
            {
                // For code the binder doesn't know is unreachable, use control flow / types.
                return !IsReachableFlowNode(flowNode);
            }
        }
        return false;
    }

    // Function and class expression bodies are checked after all statements in the enclosing body. This is
    // to ensure constructs like the following are permitted:
    //
    //	const foo = function () {
    //	   const s = foo();
    //	   return "hello";
    //	}
    //
    // Here, performing a full type check of the body of the function expression whilst in the process of
    // determining the type of foo would cause foo to be given type any because of the recursive reference.
    // Delaying the type check of the body ensures foo has been assigned a type.
    // checker.go:2472
    internal void CheckNodeDeferred(Node node)
    {
        var enclosingFile = AstUtilities.GetSourceFileOfNode(node);
        var links = sourceFileLinks.Get(enclosingFile!);
        if (!links.typeChecked)
        {
            links.deferredNodes.Add(node);
        }
    }

    // checker.go:2480
    internal void CheckDeferredNodes(SourceFile context)
    {
        var links = sourceFileLinks.Get(context);
        foreach (var node in links.deferredNodes.Values())
        {
            if (IsCanceled())
            {
                break;
            }
            CheckDeferredNode(node);
        }
        links.deferredNodes.Clear();
    }

    // checker.go:2491
    internal void CheckDeferredNode(Node node)
    {
        // !!! tracing: defer tr.Push(...) omitted at skeleton level.
        var saveCurrentNode = currentNode;
        currentNode = node;
        instantiationCount = 0;
        switch (node.Kind)
        {
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.Decorator:
            case SyntaxKind.JsxOpeningElement:
                // These node kinds are deferred checked when overload resolution fails. To save on work,
                // we ensure the arguments are checked just once in a deferred way.
                // checker.go:2499-2502 — direct translation: resolveUntypedCall.
                ResolveUntypedCall(node);
                break;
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                // checker.go:2503-2504
                CheckFunctionExpressionOrObjectLiteralMethodDeferred(node);
                break;
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                // checker.go:2505-2506
                CheckAccessorDeclaration(node);
                break;
            case SyntaxKind.ClassExpression:
                // checker.go:2507-2508
                CheckClassExpressionDeferred(node);
                break;
            case SyntaxKind.TypeParameter:
                // checker.go:2510-2511
                CheckTypeParameterDeferred(node);
                break;
            case SyntaxKind.JsxSelfClosingElement:
                // checker.go:2512-2513
                CheckJsxSelfClosingElementDeferred(node);
                break;
            case SyntaxKind.JsxElement:
                // checker.go:2514-2515
                CheckJsxElementDeferred(node);
                break;
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.AsExpression:
                // checker.go:2516-2517
                CheckAssertionDeferred(node);
                break;
            case SyntaxKind.VoidExpression:
                // checker.go:2519-2520
                CheckExpression(node.Expression()!);
                break;
            case SyntaxKind.BinaryExpression:
                // checker.go:2521-2523
                if (AstUtilities.IsInstanceOfExpression(node))
                {
                    ResolveUntypedCall(node);
                }
                break;
                // No default: Go's switch (checker.go:2497-2524) falls through silently for other kinds.
        }
        currentNode = saveCurrentNode;
    }

    // checker.go:2527
    internal void CheckJSDocComments(Node node)
    {
        foreach (var comment in node.Comments().OrEmpty())
        {
            CheckJSDocComment(comment);
        }
    }

    // checker.go:2533
    internal void CheckJSDocComment(Node node)
    {
        // This performs minimal checking of JSDoc nodes to ensure that @link references to entities are recorded
        // for purposes of checking unused identifiers.
        switch (node.Kind)
        {
            case SyntaxKind.JSDocLink:
            case SyntaxKind.JSDocLinkCode:
            case SyntaxKind.JSDocLinkPlain:
                ResolveJSDocMemberName(node.Name());
                break;
        }
    }

    // checker.go:2543
    internal Symbol? ResolveJSDocMemberName(Node? name)
    {
        if (name != null && AstUtilities.IsEntityName(name))
        {
            var meaning = SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Value;
            var symbol = ResolveEntityName(name, meaning, /*ignoreErrors*/ true, /*dontResolveAlias*/ true, AstUtilities.GetHostSignatureFromJSDoc(name));
            if (symbol != null)
            {
                return symbol;
            }
            if (NodeTests.IsQualifiedName(name))
            {
                var leftSymbol = ResolveJSDocMemberName(name.AsQualifiedName().Left);
                if (leftSymbol != null)
                {
                    Type? t = null;
                    if ((leftSymbol.Flags & SymbolFlags.Value) != 0)
                    {
                        var proto = GetPropertyOfType(GetTypeOfSymbol(leftSymbol), "prototype");
                        if (proto != null)
                        {
                            t = GetTypeOfSymbol(proto);
                        }
                    }
                    if (t == null)
                    {
                        t = GetDeclaredTypeOfSymbol(leftSymbol);
                    }
                    return GetPropertyOfType(t, name.AsQualifiedName().Right!.Text());
                }
            }
        }
        return null;
    }

    // checker.go:2615 — variance-annotation (in/out) check for a deferred type parameter. A type
    // parameter without in/out modifiers is a no-op here.
    internal void CheckTypeParameterDeferred(Node node)
    {
        if (NodeTests.IsInterfaceDeclaration(node.Parent!) || AstUtilities.IsClassLike(node.Parent!) || IsTypeOrJSTypeAliasDeclaration(node.Parent!))
        {
            var typeParameter = GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(node)!);
            var modifiers = GetTypeParameterModifiers(typeParameter) & (ModifierFlags.In | ModifierFlags.Out);
            if (modifiers != 0)
            {
                var symbol = GetSymbolOfDeclaration(node.Parent!)!;
                if (IsTypeOrJSTypeAliasDeclaration(node.Parent!) && (GetDeclaredTypeOfSymbol(symbol).objectFlags & (ObjectFlags.Anonymous | ObjectFlags.Mapped)) == 0)
                {
                    Error(node, Diag.Variance_annotations_are_only_supported_in_type_aliases_for_object_function_constructor_and_mapped_types);
                }
                else if (modifiers == ModifierFlags.In || modifiers == ModifierFlags.Out)
                {
                    // !!! tracing: defer tr.Push(...) omitted at skeleton level.
                    var source = CreateMarkerType(symbol, typeParameter, CoreUtil.IfElse(modifiers == ModifierFlags.Out, markerSubTypeForCheck!, markerSuperTypeForCheck!));
                    var target = CreateMarkerType(symbol, typeParameter, CoreUtil.IfElse(modifiers == ModifierFlags.Out, markerSuperTypeForCheck!, markerSubTypeForCheck!));
                    var saveVarianceTypeParameter = typeParameter;
                    varianceTypeParameter = typeParameter;
                    CheckTypeAssignableTo(source, target, node, Diag.Type_0_is_not_assignable_to_type_1_as_implied_by_variance_annotation);
                    varianceTypeParameter = saveVarianceTypeParameter;
                }
            }
        }
    }

    // ===== Statements =====

    // checker.go:3771
    internal void CheckBlock(Node node)
    {
        // Grammar checking for SyntaxKind.Block
        if (node.Kind == SyntaxKind.Block)
        {
            CheckGrammarStatementInAmbientContext(node);
        }
        if (AstUtilities.IsFunctionOrModuleBlock(node))
        {
            var saveFlowAnalysisDisabled = flowAnalysisDisabled;
            CheckSourceElements(node.Statements()!);
            flowAnalysisDisabled = saveFlowAnalysisDisabled;
        }
        else
        {
            CheckSourceElements(node.Statements()!);
        }
        if (node.Locals().Len() != 0)
        {
            RegisterForUnusedIdentifiersCheck(node);
        }
    }

    // checker.go:7306
    internal void CheckExpressionStatement(Node node)
    {
        // Grammar checking
        CheckGrammarStatementInAmbientContext(node);
        CheckExpression(node.Expression()!);
    }

    // checker.go:13992
    internal bool HasParseDiagnostics(SourceFile sourceFile) => sourceFile.Diagnostics().Len() > 0;

    // checker.go:3788
    internal void CheckIfStatement(Node node)
    {
        CheckGrammarStatementInAmbientContext(node);
        var t = CheckTruthinessExpression(node.Expression()!, CheckMode.Normal);
        var data = node.AsIfStatement();
        CheckTestingKnownTruthyCallableOrAwaitableOrEnumMemberType(node.Expression()!, t, data.ThenStatement);
        CheckSourceElement(data.ThenStatement);
        if (NodeTests.IsEmptyStatement(data.ThenStatement!))
        {
            Error(data.ThenStatement, Diag.The_body_of_an_if_statement_cannot_be_the_empty_statement);
        }
        CheckSourceElement(data.ElseStatement);
    }

    // checker.go:3800
    internal void CheckTestingKnownTruthyCallableOrAwaitableOrEnumMemberType(Node condExpr, Type condType, Node? body)
    {
        if (!strictNullChecks)
        {
            return;
        }
        CheckTestingKnownTruthyTypes(condExpr, condType, body);
    }

    // checker.go:3807
    internal void CheckTestingKnownTruthyTypes(Node condExpr, Type condType, Node? body)
    {
        condExpr = AstUtilities.SkipParentheses(condExpr);
        CheckTestingKnownTruthyType(condExpr, condType, body);
        while (NodeTests.IsBinaryExpression(condExpr) && (condExpr.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.BarBarToken || condExpr.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.QuestionQuestionToken))
        {
            condExpr = AstUtilities.SkipParentheses(condExpr.AsBinaryExpression().Left!);
            CheckTestingKnownTruthyType(condExpr, condType, body);
        }
    }

    // checker.go:3816
    internal void CheckTestingKnownTruthyType(Node condExpr, Type condType, Node? body)
    {
        var location = condExpr;
        if (AstUtilities.IsLogicalOrCoalescingBinaryExpression(condExpr))
        {
            location = AstUtilities.SkipParentheses(condExpr.AsBinaryExpression().Right!);
        }
        if (AstUtilities.IsModuleExportsAccessExpression(location))
        {
            return;
        }
        if (AstUtilities.IsLogicalOrCoalescingBinaryExpression(location))
        {
            CheckTestingKnownTruthyTypes(location, condType, body);
            return;
        }
        var t = condType;
        if (location != condExpr)
        {
            t = CheckExpression(location);
        }
        if ((t.flags & TypeFlags.EnumLiteral) != 0 && NodeTests.IsPropertyAccessExpression(location) && (global::Ts.Core.CoreUtil.OrElse(GetResolvedSymbolOrNil(location.Expression()!), unknownSymbol)!.Flags & SymbolFlags.Enum) != 0)
        {
            // EnumLiteral type at condition with known value is always truthy or always falsy, likely an error
            Error(location, Diag.This_condition_will_always_return_0, global::Ts.Core.CoreUtil.IfElse(global::Ts.Evaluator.EvaluatorFactory.IsTruthy(t.AsLiteralType().value), "true", "false"));
            return;
        }
        var isPropertyExpressionCast = NodeTests.IsPropertyAccessExpression(location) && IsTypeAssertion(location.Expression()!);
        if (!HasTypeFacts(t, TypeFacts.Truthy) || isPropertyExpressionCast)
        {
            return;
        }
        // While it technically should be invalid for any known-truthy value
        // to be tested, we de-scope to functions and Promises unreferenced in
        // the block as a heuristic to identify the most common bugs. There
        // are too many false positives for values sourced from type
        // definitions without strictNullChecks otherwise.
        var callSignatures = GetSignaturesOfType(t, SignatureKind.Call);
        var isPromise = GetAwaitedTypeOfPromise(t) != null;
        if (callSignatures.Length == 0 && !isPromise)
        {
            return;
        }
        Node? testedNode = null;
        if (NodeTests.IsIdentifier(location))
        {
            testedNode = location;
        }
        else if (NodeTests.IsPropertyAccessExpression(location))
        {
            testedNode = location.Name();
        }
        Symbol? testedSymbol = null;
        if (testedNode != null)
        {
            testedSymbol = GetSymbolAtLocation(testedNode, false);
        }
        if (testedSymbol == null && !isPromise)
        {
            return;
        }
        var isUsed = testedSymbol != null && NodeTests.IsBinaryExpression(condExpr.Parent!) && IsSymbolUsedInBinaryExpressionChain(condExpr.Parent!, testedSymbol) ||
            testedSymbol != null && body != null && IsSymbolUsedInConditionBody(condExpr, body, testedNode, testedSymbol);
        if (!isUsed)
        {
            if (isPromise)
            {
                ErrorAndMaybeSuggestAwait(location, true, Diag.This_condition_will_always_return_true_since_this_0_is_always_defined, GetTypeNameForErrorDisplay(t));
            }
            else
            {
                Error(location, Diag.This_condition_will_always_return_true_since_this_function_is_always_defined_Did_you_mean_to_call_it_instead);
            }
        }
    }

    // checker.go:3876
    internal bool IsSymbolUsedInBinaryExpressionChain(Node node, Symbol testedSymbol)
    {
        Visitor visit = null!;
        visit = (child) =>
        {
            if (NodeTests.IsIdentifier(child))
            {
                var symbol = GetSymbolAtLocation(child, false);
                if (symbol != null && symbol == testedSymbol)
                {
                    return true;
                }
            }
            return child.ForEachChild(visit);
        };
        while (NodeTests.IsBinaryExpression(node) && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.AmpersandAmpersandToken)
        {
            var isUsed = node.AsBinaryExpression().Right!.ForEachChild(visit);
            if (isUsed)
            {
                return true;
            }
            node = node.Parent!;
        }
        return false;
    }

    // checker.go:3897
    internal bool IsSymbolUsedInConditionBody(Node expr, Node body, Node? testedNode, Symbol testedSymbol)
    {
        Visitor visit = null!;
        visit = (childNode) =>
        {
            if (NodeTests.IsIdentifier(childNode))
            {
                var childSymbol = GetSymbolAtLocation(childNode, false);
                if (childSymbol != null && childSymbol == testedSymbol)
                {
                    // If the test was a simple identifier, the above check is sufficient
                    if (NodeTests.IsIdentifier(expr) || NodeTests.IsIdentifier(testedNode!) && NodeTests.IsBinaryExpression(testedNode!.Parent!))
                    {
                        return true;
                    }
                    // Otherwise we need to ensure the symbol is called on the same target
                    var testedExpression = testedNode!.Parent;
                    var childExpression = childNode.Parent;
                    while (testedExpression != null && childExpression != null)
                    {
                        if (NodeTests.IsIdentifier(testedExpression) && NodeTests.IsIdentifier(childExpression) || testedExpression.Kind == SyntaxKind.ThisKeyword && childExpression.Kind == SyntaxKind.ThisKeyword)
                        {
                            return GetSymbolAtLocation(testedExpression, false) == GetSymbolAtLocation(childExpression, false);
                        }
                        else if (NodeTests.IsPropertyAccessExpression(testedExpression) && NodeTests.IsPropertyAccessExpression(childExpression))
                        {
                            if (GetSymbolAtLocation(testedExpression.Name()!, false) != GetSymbolAtLocation(childExpression.Name()!, false))
                            {
                                return false;
                            }
                            childExpression = childExpression.Expression();
                            testedExpression = testedExpression.Expression();
                        }
                        else if (NodeTests.IsCallExpression(testedExpression) && NodeTests.IsCallExpression(childExpression))
                        {
                            childExpression = childExpression.Expression();
                            testedExpression = testedExpression.Expression();
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
            }
            return childNode.ForEachChild(visit);
        };
        return body.ForEachChild(visit);
    }

    // checker.go:3933
    internal void CheckDoStatement(Node node)
    {
        CheckGrammarStatementInAmbientContext(node);
        CheckSourceElement(node.Statement());
        CheckTruthinessExpression(node.Expression()!, CheckMode.Normal);
    }

    // checker.go:3939
    internal void CheckWhileStatement(Node node)
    {
        CheckGrammarStatementInAmbientContext(node);
        CheckTruthinessExpression(node.Expression()!, CheckMode.Normal);
        CheckSourceElement(node.Statement());
    }

    // checker.go:3945
    internal void CheckForStatement(Node node)
    {
        if (!CheckGrammarStatementInAmbientContext(node))
        {
            var init = node.Initializer();
            if (init != null && init.Kind == SyntaxKind.VariableDeclarationList)
            {
                CheckGrammarVariableDeclarationList(init.AsVariableDeclarationList());
            }
        }
        var data = node.AsForStatement();
        if (data.Initializer != null)
        {
            if (NodeTests.IsVariableDeclarationList(data.Initializer))
            {
                CheckVariableDeclarationList(data.Initializer);
            }
            else
            {
                CheckExpression(data.Initializer);
            }
        }
        if (data.Condition != null)
        {
            CheckTruthinessExpression(data.Condition, CheckMode.Normal);
        }
        if (data.Incrementor != null)
        {
            CheckExpression(data.Incrementor);
        }
        CheckSourceElement(data.Statement);
        if (node.Locals() != null)
        {
            RegisterForUnusedIdentifiersCheck(node);
        }
    }

    // checker.go:3971
    internal void CheckForInStatement(Node node)
    {
        var data = node.AsForInOrOfStatement();
        CheckGrammarForInOrForOfStatement(data);
        var rightType = GetNonNullableTypeIfNeeded(CheckExpression(data.Expression!));
        // TypeScript 1.0 spec (April 2014): 5.4
        // In a 'for-in' statement of the form
        // for (let VarDecl in Expr) Statement
        //   VarDecl must be a variable declaration without a type annotation that declares a variable of type Any,
        //   and Expr must be an expression of type Any, an object type, or a type parameter type.
        if (NodeTests.IsVariableDeclarationList(data.Initializer!))
        {
            var declarations = data.Initializer!.AsVariableDeclarationList().Declarations!.Nodes;
            if (declarations.Length != 0 && AstUtilities.IsBindingPattern(declarations[0].Name()))
            {
                Error(declarations[0].Name(), Diag.The_left_hand_side_of_a_for_in_statement_cannot_be_a_destructuring_pattern);
            }
            CheckVariableDeclarationList(data.Initializer);
        }
        else
        {
            // In a 'for-in' statement of the form
            // for (Var in Expr) Statement
            //   Var must be an expression classified as a reference of type Any or the String primitive type,
            //   and Expr must be an expression of type Any, an object type, or a type parameter type.
            var varExpr = data.Initializer!;
            var leftType = CheckExpression(varExpr);
            if (NodeTests.IsArrayLiteralExpression(varExpr) || NodeTests.IsObjectLiteralExpression(varExpr))
            {
                Error(varExpr, Diag.The_left_hand_side_of_a_for_in_statement_cannot_be_a_destructuring_pattern);
            }
            else if (!IsTypeAssignableTo(GetIndexTypeOrString(rightType), leftType))
            {
                Error(varExpr, Diag.The_left_hand_side_of_a_for_in_statement_must_be_of_type_string_or_any);
            }
            else
            {
                // run check only former check succeeded to avoid cascading errors
                CheckReferenceExpression(varExpr, Diag.The_left_hand_side_of_a_for_in_statement_must_be_a_variable_or_a_property_access, Diag.The_left_hand_side_of_a_for_in_statement_may_not_be_an_optional_property_access);
            }
        }
        // unknownType is returned i.e. if node.expression is identifier whose name cannot be resolved
        // in this case error about missing name is already reported - do not report extra one
        if (rightType == neverType || !IsTypeAssignableToKind(rightType, TypeFlags.NonPrimitive | TypeFlags.InstantiableNonPrimitive))
        {
            Error(data.Expression, Diag.The_right_hand_side_of_a_for_in_statement_must_be_of_type_any_an_object_type_or_a_type_parameter_but_here_has_type_0, TypeToString(rightType));
        }
        CheckSourceElement(data.Statement);
        if (node.Locals() != null)
        {
            RegisterForUnusedIdentifiersCheck(node);
        }
    }

    // checker.go:4013
    internal Type GetIndexTypeOrString(Type t)
    {
        var indexType = GetExtractStringType(GetIndexType(t));
        return global::Ts.Core.CoreUtil.IfElse((indexType.flags & TypeFlags.Never) != 0, stringType!, indexType);
    }

    // checker.go:4018
    internal void CheckForOfStatement(Node node)
    {
        var data = node.AsForInOrOfStatement();
        CheckGrammarForInOrForOfStatement(data);
        var container = GetContainingFunctionOrClassStaticBlock(node);
        if (data.AwaitModifier != null)
        {
            if (container != null && NodeTests.IsClassStaticBlockDeclaration(container))
            {
                GrammarErrorOnNode(data.AwaitModifier, Diag.X_for_await_loops_cannot_be_used_inside_a_class_static_block);
            }
            else
            {
                var functionFlags = AstUtilities.GetFunctionFlags(container);
                if ((functionFlags & (FunctionFlags.Invalid | FunctionFlags.Async)) == FunctionFlags.Async && languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.ForAwaitOf)
                {
                    // for..await..of in an async function or async generator function prior to ESNext requires the __asyncValues helper
                    CheckExternalEmitHelpers(node, ExternalEmitHelpers.ForAwaitOfIncludes);
                }
            }
        }
        // Check the LHS and RHS
        // If the LHS is a declaration, just check it as a variable declaration, which will in turn check the RHS
        // via checkRightHandSideOfForOf.
        // If the LHS is an expression, check the LHS, as a destructuring assignment or as a reference.
        // Then check that the RHS is assignable to it.
        if (NodeTests.IsVariableDeclarationList(data.Initializer!))
        {
            CheckVariableDeclarationList(data.Initializer!);
        }
        else
        {
            var varExpr = data.Initializer!;
            var iteratedType = CheckRightHandSideOfForOf(node);
            // There may be a destructuring assignment on the left side
            if (NodeTests.IsArrayLiteralExpression(varExpr) || NodeTests.IsObjectLiteralExpression(varExpr))
            {
                // iteratedType may be undefined. In this case, we still want to check the structure of
                // varExpr, in particular making sure it's a valid LeftHandSideExpression. But we'd like
                // to short circuit the type relation checking as much as possible, so we pass the unknownType.
                CheckDestructuringAssignment(varExpr, global::Ts.Core.CoreUtil.OrElse(iteratedType, errorType!), CheckMode.Normal, false);
            }
            else
            {
                var leftType = CheckExpression(varExpr);
                CheckReferenceExpression(varExpr, Diag.The_left_hand_side_of_a_for_of_statement_must_be_a_variable_or_a_property_access, Diag.The_left_hand_side_of_a_for_of_statement_may_not_be_an_optional_property_access);
                // iteratedType will be undefined if the rightType was missing properties/signatures
                // required to get its iteratedType (like [Symbol.iterator] or next). This may be
                // because we accessed properties from anyType, or it may have led to an error inside
                // getElementTypeOfIterable.
                if (iteratedType != null)
                {
                    CheckTypeAssignableToAndOptionallyElaborate(iteratedType, leftType, varExpr, data.Expression, null, null);
                }
            }
        }
        CheckSourceElement(data.Statement);
        if (node.Locals() != null)
        {
            RegisterForUnusedIdentifiersCheck(node);
        }
    }

    // checker.go:4066
    internal void CheckBreakOrContinueStatement(Node node)
    {
        if (!CheckGrammarStatementInAmbientContext(node))
        {
            CheckGrammarBreakOrContinueStatement(node);
        }
    }

    // checker.go:4072
    internal void CheckReturnStatement(Node node)
    {
        if (CheckGrammarStatementInAmbientContext(node))
        {
            return;
        }
        var container = GetContainingFunctionOrClassStaticBlock(node);
        if (container != null && NodeTests.IsClassStaticBlockDeclaration(container))
        {
            GrammarErrorOnFirstToken(node, Diag.A_return_statement_cannot_be_used_inside_a_class_static_block);
            return;
        }
        if (container == null)
        {
            GrammarErrorOnFirstToken(node, Diag.A_return_statement_can_only_be_used_within_a_function_body);
            return;
        }
        var signature = GetSignatureFromDeclaration(container);
        var returnType = GetReturnTypeOfSignature(signature);
        var functionFlags = AstUtilities.GetFunctionFlags(container);
        var exprNode = node.Expression();
        if (strictNullChecks || exprNode != null || (returnType.flags & TypeFlags.Never) != 0)
        {
            var exprType = undefinedType;
            if (exprNode != null)
            {
                exprType = CheckExpressionCached(exprNode);
            }
            if (NodeTests.IsSetAccessorDeclaration(container))
            {
                if (exprNode != null)
                {
                    Error(node, Diag.Setters_cannot_return_a_value);
                }
            }
            else if (NodeTests.IsConstructorDeclaration(container))
            {
                if (exprNode != null && !CheckTypeAssignableToAndOptionallyElaborate(exprType!, returnType, node, exprNode, null, null))
                {
                    Error(node, Diag.Return_type_of_constructor_signature_must_be_assignable_to_the_instance_type_of_the_class);
                }
            }
            else if (GetReturnTypeFromAnnotation(container) != null)
            {
                var unwrappedReturnType = global::Ts.Core.CoreUtil.OrElse(UnwrapReturnType(returnType, functionFlags), returnType);
                CheckReturnExpression(container, unwrappedReturnType, node, node.Expression(), exprType!, false);
            }
        }
        else if (!NodeTests.IsConstructorDeclaration(container) && compilerOptions.NoImplicitReturns.IsTrue() && !IsUnwrappedReturnTypeUndefinedVoidOrAny(container, returnType))
        {
            // The function has a return type, but the return statement doesn't have an expression.
            Error(node, Diag.Not_all_code_paths_return_a_value);
        }
    }

    // When checking an arrow expression such as `(x) => exp`, then `node` is the expression `exp`.
    // Otherwise, `node` is a return statement.
    // checker.go:4114
    internal void CheckReturnExpression(Node container, Type unwrappedReturnType, Node node, Node? expr, Type exprType, bool inConditionalExpression)
    {
        var unwrappedExprType = exprType;
        var functionFlags = AstUtilities.GetFunctionFlags(container);
        if (expr != null)
        {
            var unwrappedExpr = AstUtilities.SkipParentheses(expr);
            if (NodeTests.IsConditionalExpression(unwrappedExpr))
            {
                var whenTrue = unwrappedExpr.AsConditionalExpression().WhenTrue;
                var whenFalse = unwrappedExpr.AsConditionalExpression().WhenFalse;
                CheckReturnExpression(container, unwrappedReturnType, node, whenTrue, CheckExpression(whenTrue!), true /*inConditionalExpression*/);
                CheckReturnExpression(container, unwrappedReturnType, node, whenFalse, CheckExpression(whenFalse!), true /*inConditionalExpression*/);
                return;
            }
        }
        var inReturnStatement = node.Kind == SyntaxKind.ReturnStatement;
        if ((functionFlags & FunctionFlags.Async) != 0)
        {
            unwrappedExprType = CheckAwaitedType(exprType, false /*withAlias*/, node, Diag.The_return_type_of_an_async_function_must_either_be_a_valid_promise_or_must_not_contain_a_callable_then_member);
        }
        var effectiveExpr = expr; // The effective expression for diagnostics purposes.
        if (expr != null)
        {
            effectiveExpr = GetEffectiveCheckNode(expr);
        }
        var errorNode = global::Ts.Core.CoreUtil.IfElse(inReturnStatement && !inConditionalExpression, node, effectiveExpr);
        CheckTypeAssignableToAndOptionallyElaborate(unwrappedExprType, unwrappedReturnType, errorNode, effectiveExpr, null, null);
    }

    // checker.go:4139
    internal void CheckWithStatement(Node node)
    {
        if (!CheckGrammarStatementInAmbientContext(node))
        {
            if ((node.Flags & NodeFlags.AwaitContext) != 0)
            {
                GrammarErrorOnFirstToken(node, Diag.X_with_statements_are_not_allowed_in_an_async_function_block);
            }
        }
        CheckExpression(node.Expression()!);
        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        if (!HasParseDiagnostics(sourceFile))
        {
            var start = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), node.Pos);
            var end = node.Statement()!.Pos;
            GrammarErrorAtPos(sourceFile.AsNode(), start, end - start, Diag.The_with_statement_is_not_supported_All_symbols_in_a_with_block_will_have_type_any);
        }
    }

    // checker.go:4154
    internal void CheckSwitchStatement(Node node)
    {
        // Grammar checking
        CheckGrammarStatementInAmbientContext(node);
        Node? firstDefaultClause = null;
        var hasDuplicateDefaultClause = false;
        var expressionType = CheckExpression(node.Expression()!);
        var caseBlock = node.AsSwitchStatement().CaseBlock;
        foreach (var clause in caseBlock!.AsCaseBlock().Clauses!.Nodes)
        {
            // Grammar check for duplicate default clauses, skip if we already report duplicate default clause
            if (NodeTests.IsDefaultClause(clause) && !hasDuplicateDefaultClause)
            {
                if (firstDefaultClause == null)
                {
                    firstDefaultClause = clause;
                }
                else
                {
                    GrammarErrorOnNode(clause, Diag.A_default_clause_cannot_appear_more_than_once_in_a_switch_statement);
                    hasDuplicateDefaultClause = true;
                }
            }
            if (NodeTests.IsCaseClause(clause))
            {
                var caseType = CheckExpression(clause.Expression()!);
                if (!IsTypeEqualityComparableTo(expressionType, caseType))
                {
                    // expressionType is not comparable to caseType, try the reversed check and report errors if it fails
                    CheckTypeComparableTo(caseType, expressionType, clause.Expression(), null /*headMessage*/);
                }
            }
            CheckSourceElements(clause.Statements()!);
            if (compilerOptions.NoFallthroughCasesInSwitch.IsTrue())
            {
                var flowNode = clause.AsCaseOrDefaultClause().FallthroughFlowNode;
                if (flowNode != null && IsReachableFlowNode(flowNode))
                {
                    Error(clause, Diag.Fallthrough_case_in_switch);
                }
            }
        }
        if (caseBlock.Locals() != null)
        {
            RegisterForUnusedIdentifiersCheck(caseBlock);
        }
    }

    // checker.go:4190
    internal void CheckLabeledStatement(Node node)
    {
        var labeledStatement = node.AsLabeledStatement();
        var labelNode = labeledStatement.Label!;
        var labelText = labelNode.Text();
        if (!CheckGrammarStatementInAmbientContext(node))
        {
            for (var current = node.Parent; current != null && !AstUtilities.IsFunctionLike(current); current = current.Parent)
            {
                if (NodeTests.IsLabeledStatement(current) && current.Label()!.Text() == labelText)
                {
                    GrammarErrorOnNode(labelNode, Diag.Duplicate_label_0, labelText);
                    break;
                }
            }
        }
        if ((labelNode.Flags & NodeFlags.Unreachable) != 0 && compilerOptions.AllowUnusedLabels != Tristate.True)
        {
            ErrorOrSuggestion(compilerOptions.AllowUnusedLabels == Tristate.False, labelNode, Diag.Unused_label);
        }
        CheckSourceElement(labeledStatement.Statement);
    }

    // checker.go:4208
    internal void CheckThrowStatement(Node node)
    {
        var throwExpr = node.Expression()!;
        if (!CheckGrammarStatementInAmbientContext(node))
        {
            if (NodeTests.IsIdentifier(throwExpr) && throwExpr.Text().Length == 0)
            {
                GrammarErrorAtPos(node, throwExpr.Pos, 0 /*length*/, Diag.Line_break_not_permitted_here);
            }
        }
        CheckExpression(throwExpr);
    }

    // checker.go:4218
    internal void CheckTryStatement(Node node)
    {
        CheckGrammarStatementInAmbientContext(node);
        var data = node.AsTryStatement();
        CheckBlock(data.TryBlock!);
        if (data.CatchClause != null)
        {
            CheckCatchClause(data.CatchClause);
        }
        if (data.FinallyBlock != null)
        {
            CheckBlock(data.FinallyBlock);
        }
    }

    // checker.go:4230
    internal void CheckCatchClause(Node node)
    {
        var declaration = node.AsCatchClause().VariableDeclaration;
        if (declaration != null)
        {
            CheckVariableLikeDeclaration(declaration);
            var typeNode = declaration.Type();
            if (typeNode != null)
            {
                var t = GetTypeFromTypeNode(typeNode);
                if (t != null && (t.flags & TypeFlags.AnyOrUnknown) == 0)
                {
                    GrammarErrorOnFirstToken(typeNode, Diag.Catch_clause_variable_type_annotation_must_be_any_or_unknown_if_specified);
                }
            }
            else if (declaration.Initializer() != null)
            {
                GrammarErrorOnFirstToken(declaration.Initializer()!, Diag.Catch_clause_variable_cannot_have_an_initializer);
            }
            else
            {
                var blockLocals = node.AsCatchClause().Block!.Locals();
                if (blockLocals != null)
                {
                    // map-iter: 序无关 —— reports errors independently per caught name; iteration order does not affect output set.
                    foreach (var caughtName in node.Locals()!.Keys)
                    {
                        var blockLocal = blockLocals.GetValueOrDefault(caughtName);
                        if (blockLocal != null && blockLocal.ValueDeclaration != null && (blockLocal.Flags & SymbolFlags.BlockScopedVariable) != 0)
                        {
                            GrammarErrorOnNode(blockLocal.ValueDeclaration, Diag.Cannot_redeclare_identifier_0_in_catch_clause, caughtName);
                        }
                    }
                }
            }
        }
        CheckBlock(node.AsCatchClause().Block!);
    }

    // checker.go:4256
    internal void CheckBindingElement(Node node)
    {
        CheckGrammarBindingElement(node.AsBindingElement());
        CheckVariableLikeDeclaration(node);
    }

    // ===== Function declaration =====

    // checker.go:3390
    internal void CheckFunctionDeclaration(Node node)
    {
        CheckFunctionOrMethodDeclaration(node);
        CheckGrammarForGenerator(node);
        CheckCollisionsForDeclarationName(node, node.Name());
    }

    // checker.go:3396
    internal void CheckFunctionOrMethodDeclaration(Node node)
    {
        CheckDecorators(node);
        CheckSignatureDeclaration(node);
        var functionFlags = AstUtilities.GetFunctionFlags(node);
        // Do not use hasDynamicName here, because that returns false for well known symbols.
        // We want to perform checkComputedPropertyName for all computed properties, including
        // well known symbols.
        if (node.Name() != null && NodeTests.IsComputedPropertyName(node.Name()!))
        {
            // This check will account for methods in class/interface declarations,
            // as well as accessors in classes/object literals
            CheckComputedPropertyName(node.Name()!);
        }
        if (HasBindableName(node))
        {
            // first we want to check the local symbol that contain this declaration
            // - if node.localSymbol !== undefined - this is current declaration is exported and localSymbol points to the local symbol
            // - if node.localSymbol === undefined - this node is non-exported so we can just pick the result of getSymbolOfNode
            var symbol = GetSymbolOfDeclaration(node)!;
            var localSymbol = global::Ts.Core.CoreUtil.OrElse(node.LocalSymbol(), symbol);
            // Since the javascript won't do semantic analysis like typescript, ignore javascript function
            // declarations so that redeclaring a function in a JS file is not reported as a duplicate.
            if ((node.Flags & NodeFlags.JavaScriptFile) == 0)
            {
                CheckFunctionOrConstructorSymbol(localSymbol!);
            }
            if (symbol.Parent != null)
            {
                // run check on export symbol to check that modifiers agree across all exported declarations
                CheckFunctionOrConstructorSymbol(symbol);
            }
        }
        var body = node.Body();
        CheckSourceElement(body);
        CheckAllCodePathsInNonVoidFunctionReturnOrThrow(node, GetReturnTypeFromAnnotation(node));
        if (node.FunctionLikeData()!.FullSignature != null)
        {
            if (GetContextualCallSignature(GetTypeFromTypeNode(node.FunctionLikeData()!.FullSignature!), node) == null)
            {
                Error(node.FunctionLikeData()!.FullSignature, Diag.A_JSDoc_type_tag_on_a_function_must_have_a_signature_with_the_correct_number_of_arguments);
            }
        }
        if (node.Type() == null)
        {
            // Report an implicit any error if there is no body, no explicit return type, and node is not a private method
            // in an ambient context
            if (AstUtilities.NodeIsMissing(body) && !IsPrivateWithinAmbient(node))
            {
                ReportImplicitAny(node, anyType!, WideningKind.Normal);
            }
            if ((functionFlags & FunctionFlags.Generator) != 0 && AstUtilities.NodeIsPresent(body))
            {
                // A generator with a body and no type annotation can still cause errors. It can error if the
                // yielded values have no common supertype, or it can give an implicit any error if it has no
                // yielded values. The only way to trigger these errors is to try checking its return type.
                GetReturnTypeOfSignature(GetSignatureFromDeclaration(node));
            }
        }
    }

    // ===== Variable statements / declarations =====

    // checker.go:5739
    internal void CheckMissingDeclaration(Node node)
    {
        CheckDecorators(node);
    }

    // checker.go:5743
    internal void CheckVariableStatement(Node node)
    {
        var varStatement = node.AsVariableStatement();
        var declarationList = varStatement.DeclarationList!;
        if (!CheckGrammarModifiers(node) && !CheckGrammarVariableDeclarationList(declarationList.AsVariableDeclarationList()))
        {
            CheckGrammarForDisallowedBlockScopedVariableStatement(varStatement);
        }
        CheckVariableDeclarationList(declarationList);
    }

    // checker.go:5752
    internal void CheckVariableDeclarationList(Node node)
    {
        var blockScopeKind = AstUtilities.GetCombinedNodeFlags(node) & NodeFlags.BlockScoped;
        if ((blockScopeKind == NodeFlags.Using || blockScopeKind == NodeFlags.AwaitUsing) && languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.UsingAndAwaitUsing)
        {
            CheckExternalEmitHelpers(node, ExternalEmitHelpers.AddDisposableResourceAndDisposeResources);
        }
        CheckSourceElements(node.AsVariableDeclarationList().Declarations!.Nodes);
    }

    // checker.go:5760
    internal void CheckVariableDeclaration(Node node)
    {
        // !!! tracing: defer tr.Push(...) omitted at skeleton level.
        CheckGrammarVariableDeclaration(node.AsVariableDeclaration());
        CheckVariableLikeDeclaration(node);
    }

    // checker.go:6001
    internal void CheckDecorators(Node node)
    {
        // skip this check for nodes that cannot have decorators. These should have already had an error reported by
        // checkGrammarModifiers.
        if (!AstUtilities.CanHaveDecorators(node) || !AstUtilities.HasDecorators(node) || !AstUtilities.NodeCanBeDecorated(legacyDecorators, node, node.Parent, node.Parent!.Parent))
        {
            return;
        }
        var firstDecorator = CoreUtil.Find(node.ModifierNodes(), NodeTests.IsDecorator);
        if (firstDecorator == null)
        {
            return;
        }
        if (legacyDecorators)
        {
            CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.Decorate);
            if (NodeTests.IsParameterDeclaration(node))
            {
                CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.Param);
            }
        }
        else if (languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.ClassAndClassElementDecorators)
        {
            CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.ESDecorateAndRunInitializers);
            if (NodeTests.IsClassDeclaration(node))
            {
                if (node.Name() == null || GetFirstTransformableStaticClassElement(node) != null)
                {
                    CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.SetFunctionName);
                }
            }
            else if (!NodeTests.IsClassExpression(node))
            {
                var name = node.Name();
                if (NodeTests.IsPrivateIdentifier(name!) && (NodeTests.IsMethodDeclaration(node) || AstUtilities.IsAccessor(node) || AstUtilities.IsAutoAccessorPropertyDeclaration(node)))
                {
                    CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.SetFunctionName);
                }
                if (NodeTests.IsComputedPropertyName(name!))
                {
                    CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.PropKey);
                }
            }
        }
        MarkLinkedReferences(node, ReferenceHint.Decorator, null, null);
        foreach (var modifier in node.ModifierNodes().OrEmpty())
        {
            if (NodeTests.IsDecorator(modifier))
            {
                CheckDecorator(modifier);
            }
        }
    }

    // Check variable, parameter, or property declaration
    // checker.go:5769
    internal void CheckVariableLikeDeclaration(Node node)
    {
        CheckDecorators(node);
        var name = node.Name();
        if (name == null)
        {
            return; // Missing array binding elements have no name
        }
        var typeNode = node.Type();
        var initializer = node.Initializer();
        if (!NodeTests.IsBindingElement(node))
        {
            CheckSourceElement(typeNode);
        }
        // For a computed property, just check the initializer and exit
        // Do not use hasDynamicName here, because that returns false for well known symbols.
        // We want to perform checkComputedPropertyName for all computed properties, including
        // well known symbols.
        if (NodeTests.IsComputedPropertyName(name))
        {
            CheckComputedPropertyName(name);
            if (initializer != null)
            {
                CheckExpressionCached(initializer);
            }
        }
        if (NodeTests.IsBindingElement(node))
        {
            var propName = node.PropertyName();
            if (propName != null && NodeTests.IsIdentifier(node.Name()!) && AstUtilities.IsPartOfParameterDeclaration(node) && AstUtilities.NodeIsMissing(GetContainingFunction(node)!.Body()))
            {
                // type F = ({a: string}) => void;
                //               ^^^^^^
                // variable renaming in function type notation is confusing,
                // so we forbid it even if noUnusedLocals is not enabled
                renamedBindingElementsInTypes = CoreUtil.Concatenate(renamedBindingElementsInTypes, [node]);
                return;
            }
            if (NodeTests.IsObjectBindingPattern(node.Parent!) && HasDotDotDotToken(node) && languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.ObjectSpreadRest)
            {
                CheckExternalEmitHelpers(node, ExternalEmitHelpers.Rest);
            }
            // check computed properties inside property names of binding elements
            if (propName != null && NodeTests.IsComputedPropertyName(propName))
            {
                CheckComputedPropertyName(propName);
            }
            // check private/protected variable access
            var parentBinding = node.Parent!.Parent!;
            var parentCheckMode = global::Ts.Core.CoreUtil.IfElse(HasDotDotDotToken(node), CheckMode.RestBindingElement, CheckMode.Normal);
            var parentType = GetTypeForBindingElementParent(parentBinding, parentCheckMode);
            var propNameName = node.PropertyNameOrName();
            if (parentType != null && !AstUtilities.IsBindingPattern(propNameName))
            {
                var exprType = GetLiteralTypeFromPropertyName(propNameName!);
                if (IsTypeUsableAsPropertyName(exprType))
                {
                    var nameText = GetPropertyNameFromType(exprType);
                    var property = GetPropertyOfType(parentType, nameText);
                    if (property != null)
                    {
                        MarkPropertyAsReferenced(property, null /*nodeForCheckWriteOnly*/, false /*isSelfTypeAccess*/);
                        // A destructuring is never a write-only reference.
                        CheckPropertyAccessibility(node, parentBinding.Initializer() != null && parentBinding.Initializer()!.Kind == SyntaxKind.SuperKeyword, false /*writing*/, parentType, property);
                    }
                }
            }
        }
        // For a binding pattern, check contained binding elements
        if (AstUtilities.IsBindingPattern(name))
        {
            CheckSourceElements(name.Elements()!);
        }
        // For a parameter declaration with an initializer, error and exit if the containing function doesn't have a body
        if (initializer != null && AstUtilities.IsPartOfParameterDeclaration(node) && AstUtilities.NodeIsMissing(GetContainingFunction(node)!.Body()))
        {
            Error(node, Diag.A_parameter_initializer_is_only_allowed_in_a_function_or_constructor_implementation);
            return;
        }
        // For a binding pattern, validate the initializer and exit
        if (AstUtilities.IsBindingPattern(name))
        {
            if (IsInAmbientOrTypeNode(node))
            {
                return;
            }
            var needCheckInitializer = initializer != null && node.Parent!.Parent!.Kind != SyntaxKind.ForInStatement;
            var needCheckWidenedType = !global::Ts.Core.CoreUtil.Some(name.Elements(), (n) => n.Name() != null);
            if (needCheckInitializer || needCheckWidenedType)
            {
                // Don't validate for-in initializer as it is already an error
                var widenedType = GetWidenedTypeForVariableLikeDeclaration(node, false /*reportErrors*/);
                if (needCheckInitializer)
                {
                    var initializerType = CheckExpressionCached(initializer!);
                    if (strictNullChecks && needCheckWidenedType)
                    {
                        CheckNonNullNonVoidType(initializerType, node);
                    }
                    else
                    {
                        CheckTypeAssignableToAndOptionallyElaborate(initializerType, GetWidenedTypeForVariableLikeDeclaration(node, false), node, initializer, null, null);
                    }
                }
                // check the binding pattern with empty elements
                if (needCheckWidenedType)
                {
                    if (NodeTests.IsArrayBindingPattern(name))
                    {
                        CheckIteratedTypeOrElementType(IterationUse.Destructuring, widenedType, undefinedType!, node);
                    }
                    else if (strictNullChecks)
                    {
                        CheckNonNullNonVoidType(widenedType, node);
                    }
                }
            }
            return;
        }
        // For a commonjs `const x = require`, validate the alias and exit
        var symbol = GetSymbolOfDeclaration(node)!;
        if ((symbol.Flags & SymbolFlags.Alias) != 0 && AstUtilities.IsVariableDeclarationInitializedToRequire(node))
        {
            CheckAliasSymbol(node);
            return;
        }
        if (NodeTests.IsBigIntLiteral(name))
        {
            Error(name, Diag.A_bigint_literal_cannot_be_used_as_a_property_name);
        }
        var t = ConvertAutoToAny(GetTypeOfSymbol(symbol));
        if (node == symbol.ValueDeclaration)
        {
            // Node is the primary declaration of the symbol, just validate the initializer
            // Don't validate for-in initializer as it is already an error
            if (initializer != null && !NodeTests.IsForInStatement(node.Parent!.Parent!))
            {
                var initializerType = CheckExpressionCached(initializer);
                CheckTypeAssignableToAndOptionallyElaborate(initializerType, t, node, initializer, null /*headMessage*/, null);
                var blockScopeKind = GetCombinedNodeFlagsCached(node) & NodeFlags.BlockScoped;
                if (blockScopeKind == NodeFlags.AwaitUsing)
                {
                    var globalAsyncDisposableType = GetGlobalAsyncDisposableType();
                    var globalDisposableType = GetGlobalDisposableType();
                    if (globalAsyncDisposableType != emptyObjectType && globalDisposableType != emptyObjectType)
                    {
                        var optionalDisposableType = GetUnionType([globalAsyncDisposableType, globalDisposableType, nullType!, undefinedType!]);
                        CheckTypeAssignableTo(WidenTypeForVariableLikeDeclaration(initializerType, node, false), optionalDisposableType, initializer,
                            Diag.The_initializer_of_an_await_using_declaration_must_be_either_an_object_with_a_Symbol_asyncDispose_or_Symbol_dispose_method_or_be_null_or_undefined);
                    }
                }
                else if (blockScopeKind == NodeFlags.Using)
                {
                    var globalDisposableType = GetGlobalDisposableType();
                    if (globalDisposableType != emptyObjectType)
                    {
                        var optionalDisposableType = GetUnionType([globalDisposableType, nullType!, undefinedType!]);
                        CheckTypeAssignableTo(WidenTypeForVariableLikeDeclaration(initializerType, node, false), optionalDisposableType, initializer,
                            Diag.The_initializer_of_a_using_declaration_must_be_either_an_object_with_a_Symbol_dispose_method_or_be_null_or_undefined);
                    }
                }
            }
            if (symbol.Declarations.Len() > 1)
            {
                if (global::Ts.Core.CoreUtil.Some(symbol.Declarations, (d) => d != node && AstUtilities.IsVariableLike(d) && !AreDeclarationFlagsIdentical(d, node)))
                {
                    Error(name, Diag.All_declarations_of_0_must_have_identical_modifiers, global::Ts.Scanner.Scanner.DeclarationNameToString(name));
                }
            }
        }
        else
        {
            // Node is a secondary declaration, check that type is identical to primary declaration and check that
            // initializer is consistent with type associated with the node
            var declarationType = ConvertAutoToAny(GetWidenedTypeForVariableLikeDeclaration(node, false));
            if (!IsErrorType(t) && !IsErrorType(declarationType) && !IsTypeIdenticalTo(t, declarationType) && (symbol.Flags & SymbolFlags.Assignment) == 0)
            {
                ErrorNextVariableOrPropertyDeclarationMustHaveSameType(symbol.ValueDeclaration, t, node, declarationType);
            }
            if (initializer != null)
            {
                CheckTypeAssignableToAndOptionallyElaborate(CheckExpressionCached(initializer), declarationType, node, initializer, null /*headMessage*/, null);
            }
            if (symbol.ValueDeclaration != null && !AreDeclarationFlagsIdentical(node, symbol.ValueDeclaration))
            {
                Error(name, Diag.All_declarations_of_0_must_have_identical_modifiers, global::Ts.Scanner.Scanner.DeclarationNameToString(name));
            }
        }
        if (!NodeTests.IsPropertyDeclaration(node) && !NodeTests.IsPropertySignatureDeclaration(node))
        {
            // We know we don't have a binding pattern or computed name here
            CheckExportsOnMergedDeclarations(node);
            if (NodeTests.IsVariableDeclaration(node) || NodeTests.IsBindingElement(node))
            {
                CheckVarDeclaredNamesNotShadowed(node);
            }
            CheckCollisionsForDeclarationName(node, node.Name());
        }
    }

    // checker.go:5928
    internal void ErrorNextVariableOrPropertyDeclarationMustHaveSameType(Node? firstDeclaration, Type firstType, Node nextDeclaration, Type nextType)
    {
        var nextDeclarationName = AstUtilities.GetNameOfDeclaration(nextDeclaration);
        var message = global::Ts.Core.CoreUtil.IfElse(NodeTests.IsPropertyDeclaration(nextDeclaration) || NodeTests.IsPropertySignatureDeclaration(nextDeclaration),
            Diag.Subsequent_property_declarations_must_have_the_same_type_Property_0_must_be_of_type_1_but_here_has_type_2,
            Diag.Subsequent_variable_declarations_must_have_the_same_type_Variable_0_must_be_of_type_1_but_here_has_type_2);
        var declName = nextDeclarationName != null ? global::Ts.Scanner.Scanner.DeclarationNameToString(nextDeclarationName) : "";
        var diagnostic = Error(nextDeclarationName, message, declName, TypeToString(firstType), TypeToString(nextType));
        if (firstDeclaration != null)
        {
            AddRelatedInfo(diagnostic!, CreateDiagnosticForNode(firstDeclaration, Diag.X_0_was_also_declared_here, declName));
        }
    }

    // ===== Type alias =====

    // checker.go:6855
    internal void CheckTypeAliasDeclaration(Node node)
    {
        // Grammar checking
        CheckGrammarModifiers(node);
        CheckTypeNameIsReserved(node.Name()!, Diag.Type_alias_name_cannot_be_0);
        if (!ContainerAllowsBlockScopedVariable(node.Parent!))
        {
            GrammarErrorOnNode(node, Diag.X_0_declarations_can_only_be_declared_inside_a_block, "type");
        }
        CheckExportsOnMergedDeclarations(node);

        var typeNode = node.Type();
        var typeParameters = node.TypeParameters();
        CheckTypeParameters(typeParameters!);
        if (typeNode != null && typeNode.Kind == SyntaxKind.IntrinsicKeyword)
        {
            if (!(typeParameters.Len() == 0 && node.Name()!.Text() == "BuiltinIteratorReturn" ||
                typeParameters.Len() == 1 && IntrinsicTypeKinds.GetValueOrDefault(node.Name()!.Text()) != IntrinsicTypeKind.Unknown))
            {
                Error(typeNode, Diag.The_intrinsic_keyword_can_only_be_used_to_declare_compiler_provided_intrinsic_types);
            }
            return;
        }
        CheckSourceElement(typeNode);
        RegisterForUnusedIdentifiersCheck(node);
    }

    // checker.go:6878
    internal void CheckTypeNameIsReserved(Node name, global::Ts.Diagnostics.Message message)
    {
        // TS 1.0 spec (April 2014): 3.6.1
        // The predefined type keywords are reserved and cannot be used as names of user defined types.
        switch (name.Text())
        {
            case "any":
            case "unknown":
            case "never":
            case "number":
            case "bigint":
            case "boolean":
            case "string":
            case "symbol":
            case "void":
            case "object":
            case "undefined":
                Error(name, message, name.Text());
                break;
        }
    }
}
