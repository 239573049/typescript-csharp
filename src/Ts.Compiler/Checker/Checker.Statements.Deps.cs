// Single dedup surface for cross-cluster methods that the #5 statements wave CALLS but does not own.
// They live in not-yet-landed waves (grammar, expressions/#7-#8, name lookup, unused-identifiers,
// collision checks, external-module/import-export resolution, JSDoc, iteration types, decorators,
// and the complex class/interface/enum declaration validators). Each throws NotImplementedException
// so the statement call sites compile; deep runtime paths run only once the owning wave lands its
// real implementation, at which point that wave DELETES the corresponding stub here.
// **Do NOT implement whole missing subsystems.**
//
// COMPLEX-DECLARATION CHECKLIST: the class/interface/enum/module/import/export validators
// (checkClassDeclaration/checkClassLikeDeclaration/checkInterfaceDeclaration/checkEnumDeclaration/
// checkEnumMember/checkModuleDeclaration/checkImport*/checkExport*), plus the signature/method/
// accessor/constructor/parameter/property/type-parameter checks and the type-node checks
// (checkTypeReferenceNode..checkMappedType) are now REAL in Checker.Declarations.cs (complex-
// declaration long-tail wave). checkDecorators is REAL in Checker.Statements.cs. Still deferred:
// the whole iteration-types family (checkIteratedTypeOrElementType and its ~40 helpers,
// checker.go:6074-6713) and the deep override/index/member-conflict analysis those validators call —
// single-dedup stubbed in Checker.Declarations.Deps.cs / Checker.MembersSignatures.Deps.cs.
//
// §1.5: diagnostics via `using Diag = ...`; cross-package classes shadowed by sibling namespaces
// are `global::`-qualified where referenced.

using System;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // ---- diagnostics / error reporting (owned by #9 diagnostics; some already stubbed elsewhere) ----
    // Error / ErrorOrSuggestion / NewDiagnosticForNode already stubbed in Stubs.cs / Deps surfaces.
    // checker.go:13941
    internal void AddErrorOrSuggestion(bool isError, Diagnostic diagnostic)
    {
        if (isError)
        {
            AddDiagnostic(diagnostic);
        }
        else
        {
            var suggestion = diagnostic.Clone();
            suggestion.SetCategory(global::Ts.Diagnostics.Category.Suggestion);
            AddSuggestionDiagnostic(suggestion);
        }
    }
    // checker.go:13941
    internal Diagnostic ErrorAndMaybeSuggestAwait(Node? location, bool maybeMissingAwait, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var diagnostic = Error(location, message, args);
        if (maybeMissingAwait)
        {
            diagnostic.AddRelatedInfo(CreateDiagnosticForNode(location!, Diag.Did_you_forget_to_use_await));
        }
        return diagnostic;
    }
    internal Diagnostic? ErrorOrNil(Node? location, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("error");
    // CreateDiagnosticForNode is real in Checker.Relater.cs.
    // ast.Diagnostic.AddRelatedInfo (variadic helper form used by ported checker call sites).
    internal void AddRelatedInfo(Diagnostic diagnostic, params Diagnostic[] relatedInformation)
    {
        foreach (var related in relatedInformation)
        {
            diagnostic.AddRelatedInfo(related);
        }
    }

    // ---- grammar checks (grammar cluster) ----
    // All CheckGrammar*/GrammarErrorOn*/GrammarErrorAtPos are now REAL in Checker.Grammar.cs (grammar wave).

    // ---- unused-identifiers / collision / shadowing (#6 collision/unused cluster) ----
    // RegisterForUnusedIdentifiersCheck already stubbed in Stubs.cs. CheckUnusedRenamedBindingElements /
    // CheckUnusedIdentifiers are now REAL in Checker.Collisions.cs (their deep dispatch helpers remain
    // deferred there). CheckCollisionsForDeclarationName + the 5 collision leaf checks +
    // needCollisionCheckForIdentifier + checkClassNameCollisionWithObject are REAL in Checker.Collisions.cs.

    // resolveName / ResolveName — now REAL in Checker.NameLookup.cs (name-resolution wave): the ResolveName
    // method wrapper forwards to the `resolveName` field delegate (wired in Checker.Construction.cs).

    // checkVarDeclaredNamesNotShadowed (checker.go): a function-scoped (var) declaration with an
    // initializer must not step on a block-scoped (let/const) declaration hoisted from an outer scope.
    // Block-scoped declarations (const/let) and parameters early-return — the common case, incl. the
    // e2e probe's `const x`. The function-scoped-var shadowing branch routes through resolveName (NameLookup wave).
    internal void CheckVarDeclaredNamesNotShadowed(Node node)
    {
        // skip block-scoped variables and parameters
        if ((GetCombinedNodeFlagsCached(node) & NodeFlags.BlockScoped) != 0 || AstUtilities.IsPartOfParameterDeclaration(node))
        {
            return;
        }
        Symbol symbol = GetSymbolOfDeclaration(node)!;
        Node name = node.Name()!;
        if ((symbol.Flags & SymbolFlags.FunctionScopedVariable) != 0)
        {
            if (!NodeTests.IsIdentifier(name))
            {
                throw new global::System.InvalidOperationException("Identifier expected");
            }
            Symbol? localDeclarationSymbol = ResolveName(node, name.Text(), SymbolFlags.Variable, null /*nameNotFoundMessage*/, false /*isUse*/, false);
            if (localDeclarationSymbol != null && localDeclarationSymbol != symbol && (localDeclarationSymbol.Flags & SymbolFlags.BlockScopedVariable) != 0)
            {
                if ((GetDeclarationNodeFlagsFromSymbol(localDeclarationSymbol) & NodeFlags.BlockScoped) != 0)
                {
                    Node? varDeclList = AstUtilities.FindAncestorKind(localDeclarationSymbol.ValueDeclaration, SyntaxKind.VariableDeclarationList);
                    Node? container = null;
                    if (NodeTests.IsVariableStatement(varDeclList!.Parent!) && varDeclList.Parent!.Parent != null)
                    {
                        container = varDeclList.Parent.Parent;
                    }
                    // names of block-scoped and function scoped variables can collide only if the block-scoped
                    // variable is defined in the function/module/source-file scope (because of variable hoisting)
                    bool namesShareScope = container != null && (
                        (NodeTests.IsBlock(container) && AstUtilities.IsFunctionLike(container.Parent)) ||
                        NodeTests.IsModuleBlock(container) || NodeTests.IsModuleDeclaration(container) || NodeTests.IsSourceFile(container));
                    if (!namesShareScope)
                    {
                        string nameStr = SymbolToString(localDeclarationSymbol);
                        Error(node, global::Ts.Diagnostics.Diagnostics.Cannot_initialize_outer_scoped_variable_0_in_the_same_scope_as_block_scoped_declaration_1, nameStr, nameStr);
                    }
                }
            }
        }
    }

    // ---- deferred diagnostics / cancellation (checker.go / utilities.go) ----
    // produceDeferredDiagnostics (checker.go): run each queued callback, then clear the queue.
    // saveDeferredDiagnostics is false by the time this runs, so callbacks cannot re-enqueue.
    internal void ProduceDeferredDiagnostics()
    {
        foreach (var cb in deferredDiagnosticCallbacks)
        {
            cb();
        }
        deferredDiagnosticCallbacks = [];
    }

    // isCanceled (utilities.go:1635): Go `c.ctx != nil && c.ctx.Err() != nil`.
    internal bool IsCanceled() => ctx.IsCancellationRequested;

    // ---- expressions (#7/#8) ----
    // CheckExpression / CheckExpressionCached already stubbed in Stubs.cs; GetResolvedSymbolOrNil elsewhere.
    // checker.go:10917
    internal Type CheckTruthinessExpression(Node node, CheckMode checkMode) =>
        CheckTruthinessOfType(CheckExpressionEx(node, checkMode), node);
    // CheckReferenceExpression owned by the operator-expressions cluster.
    // checkDestructuringAssignment family (checker.go:12514-12680): real bodies in Checker.OperatorExpressions.cs.
    // utilities.go:324-326
    internal static bool IsTypeAssertion(Node node) => AstUtilities.IsAssertionExpression(AstUtilities.SkipParentheses(node));

    // ---- symbol / node location helpers (#10/#11 symbol resolution) ----
    internal Symbol? GetSymbolAtLocation(Node node, bool ignoreErrors) => throw new NotImplementedException("getSymbolAtLocation");

    // ---- type comparison / awaited / iteration (relater/#22/iteration) ----
    // IsTypeEqualityComparableTo owned by the operator-expressions cluster.
    // getAwaitedTypeOfPromise: real body landed in Checker.AwaitedTypes.cs (#22).
    // CheckIteratedTypeOrElementType already stubbed in Checker.MembersSignatures.Deps.cs (iteration family, deferred).

    // ---- containers / global types (construction / global resolution) ----
    // utilities.go:842
    internal static Node? GetContainingFunctionOrClassStaticBlock(Node node) => AstUtilities.FindAncestor(node.Parent, AstUtilities.IsFunctionLikeOrClassStaticBlockDeclaration);
    internal Type GetGlobalDisposableType() => getGlobalDisposableType();
    internal Type GetGlobalAsyncDisposableType() => getGlobalAsyncDisposableType();
    // CheckExternalEmitHelpers (+ HasSignatureWithArityGreaterThan / GetHelperNames / ResolveHelpersModule) are now REAL in Checker.AliasReferences.cs (checker.go:28333).

    // ---- non-null / property helpers (members/type ops) ----
    // checkNonNullNonVoidType 7414: real body landed in Checker.PropertyAccess.cs (non-null cluster).
    // markPropertyAsReferenced 27572 / checkPropertyAccessibility 11722: real bodies landed in Checker.PropertyAccess.cs.

    // ---- contextual / implicit-any / signatures (#21 / signatures) ----
    // getContextualCallSignature (10281): real body landed in Checker.ContextualType.cs.

    // ---- function/constructor symbol checks (owned by later statements pass) ----
    // checkFunctionOrConstructorSymbol 3447: real body landed in Checker.FunctionOrConstructorSymbol.cs.
    // checkAllCodePathsInNonVoidFunctionReturnOrThrow 3714 / isUnwrappedReturnTypeUndefinedVoidOrAny 3766:
    // real bodies landed in Checker.FunctionOrConstructorSymbol.cs.

    // ---- deferred node worker (deferred-check cluster) ----
    // CheckDeferredNode's switch in Checker.Statements.cs now covers every case of Go's
    // checkDeferredNode (checker.go:2491-2526); the worker stub is gone.

    // ---- signature/member/type-node/complex declaration validators ----
    // All CheckTypeParameter(s)/CheckParameter/CheckProperty*/CheckSignatureDeclaration/
    // CheckMethodDeclaration/CheckClassStaticBlockDeclaration/CheckConstructorDeclaration/
    // CheckAccessorDeclaration, the CheckTypeReferenceNode..CheckMappedType type-node checks, and the
    // CheckClassDeclaration/CheckInterfaceDeclaration/CheckEnumDeclaration/CheckEnumMember/
    // CheckModuleDeclaration/CheckImport*/CheckExport*/CheckExternalModuleExports/CheckAliasSymbol/
    // CheckExportsOnMergedDeclarations/AreDeclarationFlagsIdentical validators are now REAL in
    // Checker.Declarations.cs (complex-declaration long-tail wave). Their remaining un-landed callees
    // are single-dedup stubbed in Checker.Declarations.Deps.cs.
    //
    // CheckDecorators is REAL in Checker.Statements.cs (checker.go:6001). Its deep decorator-emit
    // branch (checkDecorator) remains stubbed below — unreached for decorator-free inputs, which
    // early-return at the guard. MarkLinkedReferences is now REAL in Checker.AliasReferences.cs
    // (alias-reference-marking wave). GetFirstTransformableStaticClassElement (checker.go:10031) is
    // now REAL in Checker.Expressions.cs (class-expression cluster).
    internal void CheckDecorator(Node node) => throw new NotImplementedException("checkDecorator");

    // ---- JSDoc comment checks (JSDoc cluster) ----
    // CheckJSDocComments/CheckJSDocComment/ResolveJSDocMemberName are now REAL in
    // Checker.Statements.cs (checker.go:2527-2565, checkJSDocComments wave). CheckJSDocType
    // (checker.go:2568) remains stubbed below — it is only dispatched for JSDoc type-node kinds
    // (JSDocNonNullableType/JSDocNullableType/...) and lands with the deep JSDoc type-resolution
    // chain (checkJSDocTypeIsInJsFile + getNullableType wiring).
    internal void CheckJSDocType(Node node) => throw new NotImplementedException("checkJSDocType");

    // ---- misc helpers (utilities) ----
    // ContainerAllowsBlockScopedVariable is now REAL in Checker.Grammar.cs (grammar wave).
    // utilities.go:106
    internal bool IsInAmbientOrTypeNode(Node node)
    {
        return (node.Flags & NodeFlags.Ambient) != 0 || AstUtilities.FindAncestor(node, static n =>
            NodeTests.IsInterfaceDeclaration(n) || NodeTests.IsTypeAliasDeclaration(n) || NodeTests.IsJSTypeAliasDeclaration(n) || NodeTests.IsTypeLiteralNode(n)) != null;
    }
    // IsPrivateWithinAmbient is real in Checker.MembersSignatures.Support.cs.
}
