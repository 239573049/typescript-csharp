// Ported from tsgo internal/checker/checker.go — #6 collision / unused-identifier cluster.
//
// This file lands the collision-check trunk (checkCollisionsForDeclarationName + the five
// checkCollisionWith*/recordPotentialCollisionWith* leaf checks + needCollisionCheckForIdentifier +
// checkClassNameCollisionWithObject) and the unused-identifier entry points (checkUnusedIdentifiers +
// checkUnusedRenamedBindingElements). The collision checks early-return for any name that is not one of
// the reserved codegen names (require/exports/Object/Promise/WeakMap/WeakSet/Reflect) — the common path —
// via needCollisionCheckForIdentifier's name mismatch. This is exactly the `const x` e2e-probe path.
//
// Deferred (single-dedup stubbed in Checker.Collisions.Deps.cs): the deep rare-warning leaf bodies
// (checkWeakMapSetCollision / checkReflectCollision, reached only for those exact reserved names) and the
// unused-identifier dispatch helpers (checkUnusedClassMembers / checkUnusedLocalsAndParameters /
// checkUnusedTypeParameters / checkUnusedInferTypeParameter), which belong to the fuller unused-analysis
// wave. Plus the AST-layer errorSkippedOnNoEmit/addDeferredDiagnostic diagnostics helpers, landed here
// because the collision cluster is their first consumer.
//
// §1.5: diagnostics via `using Diag = ...`; cross-package classes shadowed by sibling namespaces
// (Scanner) are `global::`-qualified where referenced.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:7023-7048
    // PORT-NOTE: Go `range nil` is zero iterations — a script file never registers identifier
    // check nodes, so the slice may be nil here (05-conventions.md §6.1).
    internal void CheckUnusedIdentifiers(Node[]? potentiallyUnusedIdentifiers)
    {
        foreach (var node in potentiallyUnusedIdentifiers.OrEmpty()) // map-iter: N/A — []*ast.Node slice, order preserved
        {
            switch (node.Kind)
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                    CheckUnusedClassMembers(node);
                    CheckUnusedTypeParameters(node);
                    break;
                case SyntaxKind.SourceFile:
                case SyntaxKind.ModuleDeclaration:
                case SyntaxKind.Block:
                case SyntaxKind.CaseBlock:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForInStatement:
                case SyntaxKind.ForOfStatement:
                    CheckUnusedLocalsAndParameters(node);
                    break;
                case SyntaxKind.Constructor:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.FunctionDeclaration:
                case SyntaxKind.ArrowFunction:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                    // Only report unused parameters on the implementation, not overloads.
                    if (node.Body() != null)
                    {
                        CheckUnusedLocalsAndParameters(node);
                    }
                    CheckUnusedTypeParameters(node);
                    break;
                case SyntaxKind.MethodSignature:
                case SyntaxKind.CallSignature:
                case SyntaxKind.ConstructSignature:
                case SyntaxKind.FunctionType:
                case SyntaxKind.ConstructorType:
                case SyntaxKind.TypeAliasDeclaration:
                case SyntaxKind.JSTypeAliasDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                    CheckUnusedTypeParameters(node);
                    break;
                case SyntaxKind.InferType:
                    CheckUnusedInferTypeParameter(node);
                    break;
                default:
                    throw new global::System.InvalidOperationException("Unhandled case in checkUnusedIdentifiers");
            }
        }
    }

    // checker.go:7291-7304
    internal void CheckUnusedRenamedBindingElements()
    {
        foreach (var node in renamedBindingElementsInTypes.OrEmpty()) // map-iter: N/A — []*ast.Node slice, order preserved
        {
            if (symbolReferenceLinks.Get(GetSymbolOfDeclaration(node)!).referenceKinds == 0)
            {
                var wrappingDeclaration = WalkUpBindingElementsAndPatterns(node);
                Debug.Assert(AstUtilities.IsPartOfParameterDeclaration(wrappingDeclaration), "Only parameter declaration should be checked here");
                var diagnostic = NewDiagnosticForNode(node.Name(), Diag.X_0_is_an_unused_renaming_of_1_Did_you_intend_to_use_it_as_a_type_annotation, global::Ts.Scanner.Scanner.DeclarationNameToString(node.Name()), global::Ts.Scanner.Scanner.DeclarationNameToString(node.PropertyName()));
                if (wrappingDeclaration.Type() == null)
                {
                    // entire parameter does not have type annotation, suggest adding an annotation
                    diagnostic.AddRelatedInfo(Diagnostic.NewDiagnostic(AstUtilities.GetSourceFileOfNode(wrappingDeclaration), new TextRange(wrappingDeclaration.End, wrappingDeclaration.End), Diag.We_can_only_write_a_type_for_0_by_adding_a_type_for_the_entire_parameter_here, global::Ts.Scanner.Scanner.DeclarationNameToString(node.PropertyName())));
                }
                AddDiagnostic(diagnostic);
            }
        }
    }

    // ==== unused-locals/parameters/type-parameters subsystem (checker.go:7050-7304) ====

    // checker.go:7050
    internal bool IsReferenced(Symbol symbol) => symbolReferenceLinks.Get(symbol).referenceKinds != 0;

    // checker.go:7061
    private void ReportUnusedVariable(Node location, Diagnostic diagnostic)
    {
        while (NodeTests.IsBindingElement(location) || AstUtilities.IsBindingPattern(location))
        {
            location = location.Parent!;
        }
        ReportUnused(location, NodeTests.IsParameterDeclaration(location) ? UnusedKind.Parameter : UnusedKind.Local, diagnostic);
    }

    // checker.go:7068
    private void ReportUnused(Node location, UnusedKind kind, Diagnostic diagnostic)
    {
        if ((location.Flags & (NodeFlags.Ambient | NodeFlags.ThisNodeOrAnySubNodesHasError)) == 0)
        {
            if (UnusedIsError(kind))
            {
                AddDiagnostic(diagnostic);
            }
            else
            {
                // PORT-NOTE: Go copies the diagnostic value before lowering to a suggestion; the C#
                // diagnostic is freshly created for this report and not shared, so we lower it in place.
                diagnostic.SetCategory(global::Ts.Diagnostics.Category.Suggestion);
                AddSuggestionDiagnostic(diagnostic);
            }
        }
    }

    // checker.go:7081
    private bool UnusedIsError(UnusedKind kind)
    {
        switch (kind)
        {
            case UnusedKind.Local:
                return compilerOptions.NoUnusedLocals.IsTrue();
            case UnusedKind.Parameter:
                return compilerOptions.NoUnusedParameters.IsTrue();
            default:
                throw new InvalidOperationException("Unhandled case in unusedIsError");
        }
    }

    // checker.go:7092
    internal void CheckUnusedClassMembers(Node node)
    {
        foreach (var member in node.Members() ?? [])
        {
            switch (member.Kind)
            {
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                    if (NodeTests.IsSetAccessorDeclaration(member) && (member.Symbol()!.Flags & SymbolFlags.GetAccessor) != 0)
                    {
                        break; // Already would have reported an error on the getter.
                    }
                    var symbol = GetSymbolOfDeclaration(member)!;
                    if (!IsReferenced(symbol) && (HasModifier(member, ModifierFlags.Private) || member.Name() != null && NodeTests.IsPrivateIdentifier(member.Name()!)) && (member.Flags & NodeFlags.Ambient) == 0)
                    {
                        ReportUnused(member, UnusedKind.Local, NewDiagnosticForNode(member.Name(), Diag.X_0_is_declared_but_its_value_is_never_read, SymbolToString(symbol)));
                    }
                    break;
                case SyntaxKind.Constructor:
                    foreach (var parameter in member.AsConstructorDeclaration().Parameters?.Nodes ?? [])
                    {
                        if (!IsReferenced(parameter.Symbol()!) && AstUtilities.HasSyntacticModifier(parameter, ModifierFlags.Private))
                        {
                            ReportUnused(parameter, UnusedKind.Local, NewDiagnosticForNode(parameter.Name(), Diag.Property_0_is_declared_but_its_value_is_never_read, AstUtilities.SymbolName(parameter.Symbol()!)));
                        }
                    }
                    break;
                case SyntaxKind.IndexSignature:
                case SyntaxKind.SemicolonClassElement:
                case SyntaxKind.ClassStaticBlockDeclaration:
                case SyntaxKind.JSTypeAliasDeclaration:
                    // Can't be private
                    break;
                default:
                    throw new InvalidOperationException("Unhandled case in checkUnusedClassMembers");
            }
        }
    }

    // checker.go:7117
    internal void CheckUnusedLocalsAndParameters(Node nodeWithLocals)
    {
        var variableParents = new global::Ts.Collections.Set<Node>();
        Dictionary<Node, List<Node>>? importClauses = null;
        foreach (var local in (nodeWithLocals.Locals() ?? []).Values) // map-iter: 序无关 — reports are grouped/sorted downstream
        {
            var referenceKinds = symbolReferenceLinks.Get(local).referenceKinds;
            if ((local.Flags & SymbolFlags.TypeParameter) != 0 && ((local.Flags & SymbolFlags.Variable) == 0 || (referenceKinds & SymbolFlags.Variable) != 0) ||
                (local.Flags & SymbolFlags.TypeParameter) == 0 && (referenceKinds != 0 || local.ExportSymbol != null ||
                    (local.Flags & SymbolFlags.ModuleExports) != 0))
            {
                continue;
            }
            foreach (var declaration in local.Declarations ?? [])
            {
                if (NodeTests.IsVariableDeclaration(declaration) || NodeTests.IsParameterDeclaration(declaration) || NodeTests.IsBindingElement(declaration))
                {
                    variableParents.Add(AstUtilities.GetRootDeclaration(declaration).Parent!);
                }
                else if (NodeTests.IsImportClause(declaration) || NodeTests.IsImportSpecifier(declaration) || NodeTests.IsNamespaceImport(declaration))
                {
                    if (!IsIdentifierThatStartsWithUnderscore(declaration.Name()!))
                    {
                        importClauses ??= new();
                        var importClause = ImportClauseFromImported(declaration);
                        if (!importClauses.TryGetValue(importClause, out var list))
                        {
                            list = new();
                            importClauses[importClause] = list;
                        }
                        list.Add(declaration);
                    }
                }
                else if (!NodeTests.IsTypeParameterDeclaration(declaration) && !AstUtilities.IsAmbientModule(declaration))
                {
                    ReportUnusedLocal(declaration, AstUtilities.SymbolName(local));
                }
            }
        }
        foreach (var declaration in variableParents.Keys()) // map-iter: 序无关 — Set insertion, reports sorted downstream
        {
            if (NodeTests.IsVariableDeclarationList(declaration))
            {
                ReportUnusedVariables(declaration);
            }
            else
            {
                ReportUnusedParameters(declaration);
            }
        }
        if (importClauses != null)
        {
            foreach (var (declaration, unuseds) in importClauses) // map-iter: 序无关 — reports sorted downstream
            {
                ReportUnusedImports(declaration, unuseds);
            }
        }
    }

    // checker.go:7158
    private void ReportUnusedLocal(Node node, string name)
    {
        var message = AstUtilities.IsTypeDeclaration(node) ? Diag.X_0_is_declared_but_never_used : Diag.X_0_is_declared_but_its_value_is_never_read;
        ReportUnused(node, UnusedKind.Local, NewDiagnosticForNode(node.Name() ?? node, message, name));
    }

    // checker.go:7163
    private void ReportUnusedVariables(Node node)
    {
        var declarations = node.AsVariableDeclarationList().Declarations?.Nodes ?? [];
        if (declarations.Length > 1 && CoreUtil.Every(declarations, IsUnreferencedVariableDeclaration))
        {
            ReportUnusedVariable(node, NewDiagnosticForNode(node, Diag.All_variables_are_unused));
        }
        else
        {
            ReportUnusedVariableDeclarations(declarations);
        }
    }

    // checker.go:7172
    private void ReportUnusedParameters(Node node)
    {
        ReportUnusedVariableDeclarations(node.Parameters() ?? []);
    }

    // checker.go:7180
    private void ReportUnusedBindingElements(Node node)
    {
        var declarations = node.Elements() ?? [];
        if (declarations.Length > 1 && CoreUtil.Every(declarations, IsUnreferencedVariableDeclaration))
        {
            ReportUnusedVariable(node, NewDiagnosticForNode(node, Diag.All_destructured_elements_are_unused));
        }
        else
        {
            ReportUnusedVariableDeclarations(declarations);
        }
    }

    // checker.go:7189
    private void ReportUnusedVariableDeclarations(Node[] declarations)
    {
        foreach (var declaration in declarations)
        {
            var name = declaration.Name();
            if (name != null && !AstUtilities.IsParameterPropertyDeclaration(declaration, declaration.Parent!) && !AstUtilities.IsThisParameter(declaration))
            {
                if (AstUtilities.IsBindingPattern(name))
                {
                    ReportUnusedBindingElements(name);
                }
                else if (IsUnreferencedVariableDeclaration(declaration))
                {
                    ReportUnusedVariable(declaration, NewDiagnosticForNode(name, Diag.X_0_is_declared_but_its_value_is_never_read, name.Text()));
                }
            }
        }
    }

    // checker.go:7204
    private bool IsUnreferencedVariableDeclaration(Node node)
    {
        var name = node.Name();
        if (name == null)
        {
            return true;
        }
        if (AstUtilities.IsBindingPattern(name))
        {
            return CoreUtil.Every(node.Name()!.Elements() ?? [], IsUnreferencedVariableDeclaration);
        }
        if ((symbolReferenceLinks.Get(GetSymbolOfDeclaration(node)!).referenceKinds & SymbolFlags.Variable) != 0)
        {
            return false;
        }
        if (NodeTests.IsBindingElement(node) && NodeTests.IsObjectBindingPattern(node.Parent!))
        {
            // In `{ a, ...b }, `a` is considered used since it removes a property from `b`. `b` may still be unused though.
            var lastElement = CoreUtil.LastOrNil(node.Parent!.Elements() ?? []);
            if (node != lastElement && HasDotDotDotToken(lastElement!))
            {
                return false;
            }
        }
        if ((NodeTests.IsParameterDeclaration(node) ||
            NodeTests.IsVariableDeclaration(node) && (AstUtilities.IsForInOrOfStatement(node.Parent!.Parent!) || (GetCombinedNodeFlagsCached(node) & NodeFlags.Using) != 0) ||
            NodeTests.IsBindingElement(node) && !(NodeTests.IsObjectBindingPattern(node.Parent!) && node.PropertyName() == null)) &&
            IsIdentifierThatStartsWithUnderscore(name))
        {
            return false;
        }
        return true;
    }

    // checker.go:7225
    private void ReportUnusedImports(Node node, List<Node> unuseds)
    {
        var declarationCount = node.Name() != null ? 1 : 0;
        var namedBindings = node.AsImportClause().NamedBindings;
        if (namedBindings != null)
        {
            if (NodeTests.IsNamespaceImport(namedBindings))
            {
                declarationCount++;
            }
            else
            {
                declarationCount += (namedBindings.Elements() ?? []).Length;
            }
        }
        if (declarationCount > 1 && declarationCount == unuseds.Count)
        {
            ReportUnused(node, UnusedKind.Local, NewDiagnosticForNode(node.Parent, Diag.All_imports_in_import_declaration_are_unused));
        }
        else
        {
            foreach (var unused in unuseds)
            {
                ReportUnusedLocal(unused, unused.Name()!.Text());
            }
        }
    }

    // checker.go:7244
    private static bool IsIdentifierThatStartsWithUnderscore(Node node) =>
        NodeTests.IsIdentifier(node) && node.Text() != "" && node.Text()[0] == '_';

    // checker.go:7248
    private static Node ImportClauseFromImported(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportClause:
                return node;
            case SyntaxKind.NamespaceImport:
                return node.Parent!;
            default:
                return node.Parent!.Parent!;
        }
    }

    // checker.go:7259
    internal void CheckUnusedInferTypeParameter(Node node)
    {
        var typeParameter = node.AsInferTypeNode().TypeParameter!;
        if (IsUnreferencedTypeParameter(typeParameter))
        {
            ReportUnused(node, UnusedKind.Parameter, NewDiagnosticForNode(typeParameter.Name(), Diag.X_0_is_declared_but_never_used, typeParameter.Name()!.Text()));
        }
    }

    // checker.go:7266
    internal void CheckUnusedTypeParameters(Node node)
    {
        if (!AllDeclarationsInSameSourceFile(GetSymbolOfDeclaration(node)!))
        {
            return;
        }
        var typeParameterList = node.TypeParameterList();
        if (typeParameterList == null)
        {
            return;
        }
        if (typeParameterList.Nodes.Length > 1 && CoreUtil.Every(typeParameterList.Nodes, IsUnreferencedTypeParameter))
        {
            var file = AstUtilities.GetSourceFileOfNode(node);
            var loc = RangeOfTypeParameters(file!, typeParameterList);
            ReportUnused(node, UnusedKind.Parameter, Diagnostic.NewDiagnostic(file, loc, Diag.All_type_parameters_are_unused));
        }
        else
        {
            foreach (var typeParameter in typeParameterList.Nodes)
            {
                if (IsUnreferencedTypeParameter(typeParameter))
                {
                    ReportUnused(node, UnusedKind.Parameter, NewDiagnosticForNode(typeParameter, Diag.X_0_is_declared_but_never_used, typeParameter.Name()!.Text()));
                }
            }
        }
    }

    // checker.go:7287
    private bool IsUnreferencedTypeParameter(Node typeParameter) =>
        (symbolReferenceLinks.Get(GetMergedSymbol(typeParameter.Symbol()!)!).referenceKinds & SymbolFlags.TypeParameter) == 0 && !IsIdentifierThatStartsWithUnderscore(typeParameter.Name()!);

    // utilities.go:1531
    private static TextRange RangeOfTypeParameters(SourceFile sourceFile, NodeList typeParameters) =>
        new TextRange(typeParameters.Pos - 1, Math.Min(sourceFile.Text().Length, global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), typeParameters.End) + 1));

    // utilities.go:1555
    private static bool AllDeclarationsInSameSourceFile(Symbol symbol)
    {
        if ((symbol.Declarations?.Length ?? 0) > 1)
        {
            SourceFile? sourceFile = null;
            for (var i = 0; i < symbol.Declarations!.Length; i++)
            {
                var d = symbol.Declarations[i];
                if (i == 0)
                {
                    sourceFile = AstUtilities.GetSourceFileOfNode(d);
                }
                else if (AstUtilities.GetSourceFileOfNode(d) != sourceFile)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // checker.go:10420-10437
    internal void CheckCollisionsForDeclarationName(Node node, Node? name)
    {
        if (name == null)
        {
            return;
        }
        CheckCollisionWithRequireExportsInGeneratedCode(node, name);
        CheckCollisionWithGlobalObjectInGeneratedCode(node, name);
        CheckCollisionWithGlobalPromiseInGeneratedCode(node, name);
        RecordPotentialCollisionWithWeakMapSetInGeneratedCode(node, name);
        RecordPotentialCollisionWithReflectInGeneratedCode(node, name);
        if (AstUtilities.IsClassLike(node))
        {
            CheckTypeNameIsReserved(name, Diag.Class_name_cannot_be_0);
            if ((node.Flags & NodeFlags.Ambient) == 0)
            {
                CheckClassNameCollisionWithObject(name);
            }
        }
        else if (NodeTests.IsEnumDeclaration(node))
        {
            CheckTypeNameIsReserved(name, Diag.Enum_name_cannot_be_0);
        }
    }

    // checker.go:10439-10457
    internal void CheckCollisionWithRequireExportsInGeneratedCode(Node node, Node? name)
    {
        // No need to check for require or exports for ES6 modules and later
        if (program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) >= ModuleKind.ES2015)
        {
            return;
        }
        if (name == null || !NeedCollisionCheckForIdentifier(node, name, "require") && !NeedCollisionCheckForIdentifier(node, name, "exports"))
        {
            return;
        }
        // Uninstantiated modules shouldnt do this check
        if (NodeTests.IsModuleDeclaration(node) && AstUtilities.GetModuleInstanceState(node) != ModuleInstanceState.Instantiated)
        {
            return;
        }
        // In case of variable declaration, node.parent is variable statement so look at the variable statement's parent
        var parent = AstUtilities.GetDeclarationContainer(node);
        if (NodeTests.IsSourceFile(parent!) && AstUtilities.IsExternalOrCommonJSModule(parent!.AsSourceFile()))
        {
            // If the declaration happens to be in external module, report error that require and exports are reserved keywords
            ErrorSkippedOnNoEmit(name, Diag.Duplicate_identifier_0_Compiler_reserves_name_1_in_top_level_scope_of_a_module, global::Ts.Scanner.Scanner.DeclarationNameToString(name), global::Ts.Scanner.Scanner.DeclarationNameToString(name));
        }
    }

    // checker.go:10459-10473
    internal void CheckCollisionWithGlobalObjectInGeneratedCode(Node node, Node? name)
    {
        if (name == null || AstUtilities.IsClassLike(node) || !NeedCollisionCheckForIdentifier(node, name, "Object"))
        {
            return;
        }
        // Uninstantiated modules shouldn't do this check
        if (NodeTests.IsModuleDeclaration(node) && AstUtilities.GetModuleInstanceState(node) != ModuleInstanceState.Instantiated)
        {
            return;
        }
        // In case of variable declaration, node.parent is variable statement so look at the variable statement's parent
        var parent = AstUtilities.GetDeclarationContainer(node);
        if (parent != null && NodeTests.IsSourceFile(parent) && AstUtilities.IsExternalOrCommonJSModule(parent.AsSourceFile()) && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(parent.AsSourceFile())) == ModuleKind.CommonJS)
        {
            // If the declaration happens to be in external module, report error that Object is a reserved identifier.
            ErrorSkippedOnNoEmit(name, Diag.Duplicate_identifier_0_Compiler_reserves_name_1_in_top_level_scope_of_a_module, global::Ts.Scanner.Scanner.DeclarationNameToString(name), global::Ts.Scanner.Scanner.DeclarationNameToString(name));
        }
    }

    // checker.go:10475-10501
    internal bool NeedCollisionCheckForIdentifier(Node node, Node? identifier, string name)
    {
        if (identifier != null && identifier.Text() != name)
        {
            return false;
        }
        switch (node.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.PropertyAssignment:
                // it is ok to have member named '_super', '_this', `Promise`, etc. - member access is always qualified
                return false;
        }
        if ((node.Flags & NodeFlags.Ambient) != 0)
        {
            // ambient context - no codegen impact
            return false;
        }
        if (NodeTests.IsImportClause(node) || NodeTests.IsImportEqualsDeclaration(node) || NodeTests.IsImportSpecifier(node))
        {
            // type-only imports do not require collision checks against runtime values.
            if (IsTypeOnlyImportOrExportDeclaration(node))
            {
                return false;
            }
        }
        var root = AstUtilities.GetRootDeclaration(node);
        if (NodeTests.IsParameterDeclaration(root) && AstUtilities.NodeIsMissing(root.Parent!.Body()))
        {
            // just an overload - no codegen impact
            return false;
        }
        return true;
    }

    // checker.go:10515-10522
    internal void RecordPotentialCollisionWithWeakMapSetInGeneratedCode(Node node, Node name)
    {
        if (languageVersion <= ScriptTarget.ES2021 &&
            (NeedCollisionCheckForIdentifier(node, name, "WeakMap") || NeedCollisionCheckForIdentifier(node, name, "WeakSet")))
        {
            AddDeferredDiagnostic(() => CheckWeakMapSetCollision(node));
        }
    }

    // checker.go:10534-10548
    internal void CheckCollisionWithGlobalPromiseInGeneratedCode(Node node, Node? name)
    {
        if (name == null || languageVersion >= ScriptTarget.ES2017 || !NeedCollisionCheckForIdentifier(node, name, "Promise"))
        {
            return;
        }
        // Uninstantiated modules shouldn't do this check
        if (NodeTests.IsModuleDeclaration(node) && AstUtilities.GetModuleInstanceState(node) != ModuleInstanceState.Instantiated)
        {
            return;
        }
        // In case of variable declaration, node.parent is variable statement so look at the variable statement's parent
        var parent = AstUtilities.GetDeclarationContainer(node);
        if (parent != null && NodeTests.IsSourceFile(parent) && AstUtilities.IsExternalOrCommonJSModule(parent.AsSourceFile()) && (parent.Flags & NodeFlags.HasAsyncFunctions) != 0)
        {
            // If the declaration happens to be in external module, report error that Promise is a reserved identifier.
            ErrorSkippedOnNoEmit(name, Diag.Duplicate_identifier_0_Compiler_reserves_name_1_in_top_level_scope_of_a_module_containing_async_functions, global::Ts.Scanner.Scanner.DeclarationNameToString(name), global::Ts.Scanner.Scanner.DeclarationNameToString(name));
        }
    }

    // checker.go:10550-10556
    internal void RecordPotentialCollisionWithReflectInGeneratedCode(Node node, Node? name)
    {
        if (name != null && languageVersion <= ScriptTarget.ES2021 && NeedCollisionCheckForIdentifier(node, name, "Reflect"))
        {
            AddDeferredDiagnostic(() => CheckReflectCollision(node));
        }
    }

    // checker.go:10587-10591
    internal void CheckClassNameCollisionWithObject(Node name)
    {
        if (name.Text() == "Object" && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(name)!)) < ModuleKind.ES2015)
        {
            // PORT-NOTE: Go moduleKind.String() → C# enum ToString() (AOT-permitted, plan/07 §禁令 bullet 2).
            // The trimmed Go stringer names match the C# ModuleKind member names 1:1 (CommonJS/ES2015/…);
            // this branch only fires for module < ES2015 (None/CommonJS/AMD/UMD/System), all in the name range.
            Error(name, Diag.Class_name_cannot_be_Object_when_targeting_ES5_and_above_with_module_0, moduleKind.ToString());
        }
    }

    // checker.go:13898-13902 — enqueue a deferred diagnostic callback (only while saveDeferredDiagnostics).
    // Landed here as the collision cluster is its first consumer; produceDeferredDiagnostics is in
    // Checker.Statements.Deps.cs.
    internal void AddDeferredDiagnostic(Action callback)
    {
        if (saveDeferredDiagnostics)
        {
            deferredDiagnosticCallbacks.Add(callback);
        }
    }

    // checker.go:13931-13935 — an error diagnostic that is suppressed when emit is skipped.
    internal Diagnostic ErrorSkippedOnNoEmit(Node? location, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var diagnostic = Error(location, message, args);
        diagnostic.SetSkippedOnNoEmit();
        return diagnostic;
    }

    // checker.go:10524 — checkWeakMapSetCollision.
    internal void CheckWeakMapSetCollision(Node node)
    {
        Node? enclosingBlockScope = AstUtilities.GetEnclosingBlockScopeContainer(node);
        if ((nodeLinks.Get(enclosingBlockScope!).flags & NodeCheckFlags.ContainsClassWithPrivateIdentifiers) != 0)
        {
            Node? name = node.Name();
            if (name != null && NodeTests.IsIdentifier(name))
            {
                ErrorSkippedOnNoEmit(node, Diag.Compiler_reserves_name_0_when_emitting_private_identifier_downlevel, name.Text());
            }
        }
    }

    // checker.go:10558 — checkReflectCollision.
    internal void CheckReflectCollision(Node node)
    {
        bool hasCollision = false;
        if (NodeTests.IsClassExpression(node))
        {
            // ClassExpression names don't contribute to their containers, but do matter for any of their block-scoped members.
            foreach (Node member in node.Members() ?? [])
            {
                if ((nodeLinks.Get(member).flags & NodeCheckFlags.ContainsSuperPropertyInStaticInitializer) != 0)
                {
                    hasCollision = true;
                    break;
                }
            }
        }
        else if (NodeTests.IsFunctionExpression(node))
        {
            // FunctionExpression names don't contribute to their containers, but do matter for their contents.
            if ((nodeLinks.Get(node).flags & NodeCheckFlags.ContainsSuperPropertyInStaticInitializer) != 0)
            {
                hasCollision = true;
            }
        }
        else
        {
            Node? container = AstUtilities.GetEnclosingBlockScopeContainer(node);
            if (container != null && (nodeLinks.Get(container).flags & NodeCheckFlags.ContainsSuperPropertyInStaticInitializer) != 0)
            {
                hasCollision = true;
            }
        }
        if (hasCollision)
        {
            Node? name = node.Name();
            if (name != null && NodeTests.IsIdentifier(name))
            {
                ErrorSkippedOnNoEmit(node, Diag.Duplicate_identifier_0_Compiler_reserves_name_1_when_emitting_super_references_in_static_initializers, global::Ts.Scanner.Scanner.DeclarationNameToString(name), "Reflect");
            }
        }
    }
}
