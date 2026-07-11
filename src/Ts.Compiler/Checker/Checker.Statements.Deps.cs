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

namespace Ts.Checker;

public partial class Checker
{
    // ---- diagnostics / error reporting (owned by #9 diagnostics; some already stubbed elsewhere) ----
    // Error / ErrorOrSuggestion / NewDiagnosticForNode already stubbed in Stubs.cs / Deps surfaces.
    internal void AddErrorOrSuggestion(bool isError, Diagnostic diagnostic) => throw new NotImplementedException("addErrorOrSuggestion");
    internal void ErrorAndMaybeSuggestAwait(Node? location, bool maybeMissingAwait, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("errorAndMaybeSuggestAwait");
    internal Diagnostic? ErrorOrNil(Node? location, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("error");
    // CreateDiagnosticForNode is real in Checker.Relater.cs.
    internal void AddRelatedInfo(Diagnostic diagnostic, params Diagnostic[] relatedInformation) => throw new NotImplementedException("addRelatedInfo");

    // ---- grammar checks (grammar cluster) ----
    // All CheckGrammar*/GrammarErrorOn*/GrammarErrorAtPos are now REAL in Checker.Grammar.cs (grammar wave).

    // ---- unused-identifiers / collision / shadowing (owned by #6 unused cluster) ----
    // RegisterForUnusedIdentifiersCheck already stubbed in Stubs.cs.
    internal void CheckUnusedRenamedBindingElements() => throw new NotImplementedException("checkUnusedRenamedBindingElements");
    internal void CheckUnusedIdentifiers(Node[] identifierCheckNodes) => throw new NotImplementedException("checkUnusedIdentifiers");
    internal void CheckCollisionsForDeclarationName(Node node, Node? name) => throw new NotImplementedException("checkCollisionsForDeclarationName");

    // resolveName (checker.go) — core name resolution, owned by the NameLookup wave. Stubbed until then.
    internal Symbol? ResolveName(Node? location, string name, SymbolFlags meaning, global::Ts.Diagnostics.Message? nameNotFoundMessage, bool isUse, bool excludeGlobals) => throw new NotImplementedException("resolveName");

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
    internal Type CheckTruthinessExpression(Node node, CheckMode checkMode) => throw new NotImplementedException("checkTruthinessExpression");
    // CheckReferenceExpression owned by the operator-expressions cluster.
    internal Type CheckDestructuringAssignment(Node exprOrAssignment, Type rightType, CheckMode checkMode, bool rightIsThis) => throw new NotImplementedException("checkDestructuringAssignment");
    internal bool IsTypeAssertion(Node node) => throw new NotImplementedException("isTypeAssertion");

    // ---- symbol / node location helpers (#10/#11 symbol resolution) ----
    internal Symbol? GetSymbolAtLocation(Node node, bool ignoreErrors) => throw new NotImplementedException("getSymbolAtLocation");

    // ---- type comparison / awaited / iteration (relater/#22/iteration) ----
    // IsTypeEqualityComparableTo owned by the operator-expressions cluster.
    internal Type? GetAwaitedTypeOfPromise(Type t) => throw new NotImplementedException("getAwaitedTypeOfPromise");
    // CheckIteratedTypeOrElementType already stubbed in Checker.MembersSignatures.Deps.cs (iteration family, deferred).

    // ---- containers / global types (construction / global resolution) ----
    internal Node? GetContainingFunctionOrClassStaticBlock(Node node) => throw new NotImplementedException("getContainingFunctionOrClassStaticBlock");
    internal Type GetGlobalDisposableType() => throw new NotImplementedException("getGlobalDisposableType");
    internal Type GetGlobalAsyncDisposableType() => throw new NotImplementedException("getGlobalAsyncDisposableType");
    internal void CheckExternalEmitHelpers(Node location, ExternalEmitHelpers helpers) => throw new NotImplementedException("checkExternalEmitHelpers");

    // ---- non-null / property helpers (members/type ops) ----
    internal Type CheckNonNullNonVoidType(Type t, Node node) => throw new NotImplementedException("checkNonNullNonVoidType");
    internal void MarkPropertyAsReferenced(Symbol prop, Node? nodeForCheckWriteOnly, bool isSelfTypeAccess) => throw new NotImplementedException("markPropertyAsReferenced");
    internal bool CheckPropertyAccessibility(Node node, bool isSuper, bool writing, Type type, Symbol prop) => throw new NotImplementedException("checkPropertyAccessibility");

    // ---- contextual / implicit-any / signatures (#21 / signatures) ----
    internal Signature? GetContextualCallSignature(Type type, Node node) => throw new NotImplementedException("getContextualCallSignature");

    // ---- function/constructor symbol checks (owned by later statements pass) ----
    internal void CheckFunctionOrConstructorSymbol(Symbol symbol) => throw new NotImplementedException("checkFunctionOrConstructorSymbol");
    internal void CheckAllCodePathsInNonVoidFunctionReturnOrThrow(Node fn, Type? returnType) => throw new NotImplementedException("checkAllCodePathsInNonVoidFunctionReturnOrThrow");
    internal bool IsUnwrappedReturnTypeUndefinedVoidOrAny(Node fn, Type returnType) => throw new NotImplementedException("isUnwrappedReturnTypeUndefinedVoidOrAny");

    // ---- deferred node worker (deferred-check cluster) ----
    internal void CheckDeferredNodeWorker(Node node) => throw new NotImplementedException("checkDeferredNodeWorker");

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
    // branches (checkDecorator / getFirstTransformableStaticClassElement) remain stubbed below —
    // unreached for decorator-free inputs, which early-return at the guard. MarkLinkedReferences is
    // called by the import/export validators but its landing belongs to the reference-marking cluster.
    internal void CheckDecorator(Node node) => throw new NotImplementedException("checkDecorator");
    internal void MarkLinkedReferences(Node location, ReferenceHint hint, Symbol? propSymbol, Type? parentType) => throw new NotImplementedException("markLinkedReferences");
    internal Node? GetFirstTransformableStaticClassElement(Node node) => throw new NotImplementedException("getFirstTransformableStaticClassElement");

    // ---- JSDoc comment checks (JSDoc cluster) ----
    internal void CheckJSDocComments(Node node) => throw new NotImplementedException("checkJSDocComments");
    internal void CheckJSDocType(Node node) => throw new NotImplementedException("checkJSDocType");

    // ---- misc helpers (utilities) ----
    // ContainerAllowsBlockScopedVariable is now REAL in Checker.Grammar.cs (grammar wave).
    internal bool IsInAmbientOrTypeNode(Node node) => throw new NotImplementedException("isInAmbientOrTypeNode");
    // IsPrivateWithinAmbient is real in Checker.MembersSignatures.Support.cs.
}
