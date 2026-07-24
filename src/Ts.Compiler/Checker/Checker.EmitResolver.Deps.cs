// Single dedup surface for the EmitResolver wave (Checker.EmitResolver.cs + .Synthesis.cs).
//
// Contents:
//   * GetEmitResolver() — checker.go:31916 (lazy EmitResolver construction).
//   * NewNodeBuilder(EmitContext) — checker/nodebuilder.go:279 (request-scoped builder factory;
//     builder body is a throwing shell until the NodeBuilder request-builder wave lands).
//   * A handful of `ast` package utilities that the EmitResolver references but that are not yet
//     ported into Ts.Ast (IsVarConst / IsExpandoPropertyDeclaration / IsLateVisibilityPaintedStatement /
//     GetAnyImportSyntax). Hosted here as `internal static` on Checker (no other cluster references
//     them yet, so no CS0111 collision). Domain note: these belong to Ts.Ast; when the ast wave
//     ports them, these should move and these copies be deleted.
//   * checker methods the EmitResolver reads that belong to not-yet-ported waves
//     (GetExternalModuleFileFromDeclaration / GetConstantValue / IsSymbolAccessible /
//     GetGlobalPromiseConstructorSymbol / containsUndefinedType / containsNonMissingUndefinedType).
//     Faithful signatures; bodies throw until their owning wave lands. Each appears exactly once in
//     the assembly (helpers already stubbed elsewhere — e.g. GetFirstIdentifier,
//     IsInternalModuleImportEqualsDeclaration — are implemented in place, not re-declared here).
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified.

using System;
using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:31916 — GetEmitResolver.
    public EmitResolver GetEmitResolver()
    {
        emitResolverOnce.Do(() => { emitResolver = new EmitResolver(this); });
        return emitResolver!;
    }

    // checker/nodebuilder.go:279 — NewNodeBuilder(ch, e). Request-scoped builder (per-emit-context).
    internal NodeBuilder NewNodeBuilder(global::Ts.Printer.EmitContext emitContext) => new(this, emitContext);

    // ── ast package utilities referenced by EmitResolver but not yet in Ts.Ast ──────────────────

    // ast/utilities.go:1238 — IsVarConst is already hosted (internal static) on
    // Checker.TypeNodes.Support.cs; not re-declared here (CS0111).

    // ast/utilities.go:4523 — IsExpandoPropertyDeclaration.
    internal static bool IsExpandoPropertyDeclaration(Node? node) =>
        node is not null && NodeTests.IsBinaryExpression(node);

    // ast/utilities.go:3529 — IsLateVisibilityPaintedStatement.
    internal static bool IsLateVisibilityPaintedStatement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
                return true;
            default:
                return false;
        }
    }

    // checker/utilities.go:1579 — getAnyImportSyntax.
    internal static Node? GetAnyImportSyntax(Node node)
    {
        Node? importNode;
        switch (node.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
                importNode = node;
                break;
            case SyntaxKind.ImportClause:
                importNode = node.Parent;
                break;
            case SyntaxKind.NamespaceImport:
                importNode = node.Parent!.Parent;
                break;
            case SyntaxKind.ImportSpecifier:
                importNode = node.Parent!.Parent!.Parent;
                break;
            default:
                return null;
        }
        return importNode;
    }

    // checker/utilities.go:1569 — containsNonMissingUndefinedType(c, t).
    internal static bool ContainsNonMissingUndefinedType(Checker c, Type t)
    {
        Type candidate;
        if ((t.flags & TypeFlags.Union) != 0)
        {
            candidate = t.AsUnionType().types[0];
        }
        else
        {
            candidate = t;
        }
        return (candidate.flags & TypeFlags.Undefined) != 0 && candidate != c.missingType;
    }

    // ── checker methods from not-yet-ported waves ───────────────────────────────────────────────

    // checker.go:15047 — getExternalModuleFileFromDeclaration.
    internal SourceFile? GetExternalModuleFileFromDeclaration(Node declaration)
    {
        Node? specifier = null;
        if (declaration.Kind == SyntaxKind.ModuleDeclaration)
        {
            if (NodeTests.IsStringLiteral(declaration.Name()!))
            {
                specifier = declaration.Name();
            }
        }
        else
        {
            specifier = global::Ts.Printer.PrinterUtilities.GetExternalModuleName(declaration);
        }
        Symbol? moduleSymbol = ResolveExternalModuleNameWorker(specifier!, specifier, null /*moduleNotFoundError*/, false /*ignoreErrors*/, false /*isForAugmentation*/);
        if (moduleSymbol == null)
        {
            return null;
        }
        Node? decl = AstUtilities.GetDeclarationOfKind(moduleSymbol, SyntaxKind.SourceFile);
        if (decl == null)
        {
            return null;
        }
        return decl.AsSourceFile();
    }

    // services.go:859 — GetConstantValue (const-enum inlining source of truth).
    public object? GetConstantValue(Node node)
    {
        if (node.Kind == SyntaxKind.EnumMember)
        {
            return GetEnumMemberValue(node).Value;
        }

        if (symbolNodeLinks.Get(node).resolvedSymbol == null)
        {
            CheckExpressionCached(node); // ensure cached resolved symbol is set
        }
        var symbol = symbolNodeLinks.Get(node).resolvedSymbol;
        if (symbol == null && AstUtilities.IsEntityNameExpression(node))
        {
            symbol = ResolveEntityName(
                node,
                SymbolFlags.Value,
                true,  /*ignoreErrors*/
                false, /*dontResolveAlias*/
                null   /*location*/);
        }
        if (symbol != null && (symbol.Flags & SymbolFlags.EnumMember) != 0)
        {
            // inline property\index accesses only for const enums
            var member = symbol.ValueDeclaration;
            if (AstUtilities.IsEnumConst(member!.Parent!))
            {
                return GetEnumMemberValue(member).Value;
            }
        }

        return null;
    }

    // symbolaccessibility.go:839 — IsSymbolAccessible.
    internal global::Ts.Printer.SymbolAccessibilityResult IsSymbolAccessible(
        Symbol? symbol, Node? enclosingDeclaration, SymbolFlags meaning, bool shouldComputeAliasesToMakeVisible) =>
        IsSymbolAccessibleWorkerCore(symbol, enclosingDeclaration, meaning, shouldComputeAliasesToMakeVisible, true /*allowModules*/);

    // symbolaccessibility.go:16 — IsValueSymbolAccessible.
    // IsValueSymbolAccessible: real body in Checker.SymbolAccessibility.cs (symbolaccessibility.go).
    internal bool IsValueSymbolAccessible(Symbol symbol, Node? enclosingDeclaration) =>
        IsValueSymbolAccessibleCore(symbol, enclosingDeclaration);

    // checker.go:843 — getGlobalPromiseConstructorSymbol (getGlobalValueSymbolResolver("Promise")).
    // Wired field resolver (Construction.cs:214); the method just unmasks it.
    internal Symbol? GetGlobalPromiseConstructorSymbol() => getGlobalPromiseConstructorSymbol();

    // checker.go:27683 — containsUndefinedType.
    internal static bool ContainsUndefinedType(Checker c, Type t)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            t = t.Types()[0];
        }
        return (t.flags & TypeFlags.Undefined) != 0;
    }

    // checker/utilities.go:280 — isOptionalParameter.
    internal bool IsOptionalParameter(Node node)
    {
        // !!! TODO: JSDoc support
        if (NodeTests.IsParameterDeclaration(node) && node.QuestionToken() != null)
        {
            return true;
        }
        if (!NodeTests.IsParameterDeclaration(node))
        {
            return false;
        }
        if (node.Initializer() != null)
        {
            var signature = GetSignatureFromDeclaration(node.Parent!);
            var parameterIndex = Array.IndexOf(node.Parent!.Parameters()!, node);
            global::Ts.Core.Debug.Assert(parameterIndex >= 0);
            // Only consider syntactic or instantiated parameters as optional, not `void` parameters as this function is used
            // in grammar checks and checking for `void` too early results in parameter types widening too early
            // and causes some noImplicitAny errors to be lost.
            return parameterIndex >= GetMinArgumentCountEx(signature, MinArgumentCountFlags.StrongArityForUntypedJS | MinArgumentCountFlags.VoidIsNonOptional);
        }
        var iife = AstUtilities.GetImmediatelyInvokedFunctionExpression(node.Parent!);
        if (iife != null)
        {
            var parameterIndex = Array.IndexOf(node.Parent!.Parameters()!, node);
            return node.Type() == null &&
                node.AsParameterDeclaration().DotDotDotToken == null &&
                parameterIndex >= GetEffectiveCallArguments(iife).Length;
        }
        return false;
    }
}
