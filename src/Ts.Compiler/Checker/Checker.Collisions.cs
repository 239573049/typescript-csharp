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
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:7023-7048
    internal void CheckUnusedIdentifiers(Node[] potentiallyUnusedIdentifiers)
    {
        foreach (var node in potentiallyUnusedIdentifiers) // map-iter: N/A — []*ast.Node slice, order preserved
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
}
