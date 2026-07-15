// Single dedup surface for methods the grammar-checks wave (Checker.Grammar.cs) CALLS but does not
// own. They fall into two buckets:
//
//  (1) AST-LAYER GAPS — utilities that live in Go's internal/ast/utilities.go but are not yet ported
//      into Ts.Ast (NodeCanBeDecorated / GetAllAccessorDeclarationsForDeclaration + its
//      AllAccessorDeclarations shape / HasDecorators / CanHaveModifiers / CanHaveIllegalModifiers).
//      These belong to the AST layer (out of scope for the grammar wave, which only writes
//      Checker.Grammar.*). They are reached only on deep decorator/legacy-accessor paths and are
//      stubbed here as Checker-local wrappers so the grammar call sites compile. When the AST layer
//      lands them, replace these wrappers with direct AstUtilities.X calls in Checker.Grammar.cs.
//
//  (2) CHECKER-SIDE, NOT-YET-LANDED — checker methods/free funcs the grammar checks call that belong
//      to other (unported) clusters: IsInParameterInitializerBeforeContainingFunction (checker.go:12204),
//      GetVerbatimModuleSyntaxErrorMessage (checker.go:5660), IsEffectiveExternalModule (ast:1650),
//      IsDeclarationReadonly / HasAsyncModifier (checker utilities.go). Each throws
//      NotImplementedException("goName"); the owning wave deletes its stub when it lands the real body.
//
// **Do NOT implement whole missing subsystems here.** Every method is a single stub.
//
// §1.5: diagnostics via `global::Ts.Diagnostics.*`; cross-package classes shadowed by sibling
// namespaces are `global::`-qualified where referenced.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

// AST-layer shape not yet ported (ast.AllAccessorDeclarations). Minimal placeholder so
// checkGrammarModifiers' legacy-decorator branch type-checks; fields mirror the Go struct.
// PORT-NOTE: delete once Ts.Ast lands the real AllAccessorDeclarations (utilities.go:4344).
internal struct AllAccessorDeclarations
{
    internal Node? FirstAccessor;
    internal Node? SecondAccessor;
    internal Node? GetAccessor;
    internal Node? SetAccessor;
}

public partial class Checker
{
    // ---- AST-layer gaps (ast.* utilities, not yet in Ts.Ast) ----
    // NodeCanBeDecorated / HasDecorators are now REAL in Ts.Ast (AstUtilities, Utilities.Decorators.cs);
    // call sites use AstUtilities.NodeCanBeDecorated / AstUtilities.HasDecorators directly.
    // ast/utilities.go:4344 — GetAllAccessorDeclarationsForDeclaration.
    internal static AllAccessorDeclarations GetAllAccessorDeclarationsForDeclaration(Node accessor, Node[] declarationsOfSymbol)
    {
        SyntaxKind otherKind;
        if (accessor.Kind == SyntaxKind.SetAccessor)
        {
            otherKind = SyntaxKind.GetAccessor;
        }
        else if (accessor.Kind == SyntaxKind.GetAccessor)
        {
            otherKind = SyntaxKind.SetAccessor;
        }
        else
        {
            throw new InvalidOperationException($"Unexpected node kind {accessor.Kind}");
        }
        // otherAccessor := GetDeclarationOfKind(c.getSymbolOfDeclaration(accessor), otherKind)
        Node? otherAccessor = null;
        foreach (var d in declarationsOfSymbol)
        {
            if (d.Kind == otherKind)
            {
                otherAccessor = d;
                break;
            }
        }

        Node? firstAccessor;
        Node? secondAccessor;
        if (otherAccessor != null && otherAccessor.Pos < accessor.Pos)
        {
            firstAccessor = otherAccessor;
            secondAccessor = accessor;
        }
        else
        {
            firstAccessor = accessor;
            secondAccessor = otherAccessor;
        }

        Node? setAccessor = null;
        Node? getAccessor = null;
        if (accessor.Kind == SyntaxKind.SetAccessor)
        {
            setAccessor = accessor;
            if (otherAccessor != null)
            {
                getAccessor = otherAccessor;
            }
        }
        else
        {
            getAccessor = accessor;
            if (otherAccessor != null)
            {
                setAccessor = otherAccessor;
            }
        }

        return new AllAccessorDeclarations
        {
            FirstAccessor = firstAccessor,
            SecondAccessor = secondAccessor,
            SetAccessor = setAccessor,
            GetAccessor = getAccessor,
        };
    }

    // ast/utilities.go:4394 — GetAllAccessorDeclarations.
    internal static AllAccessorDeclarations GetAllAccessorDeclarations(Node[]? parentDeclarations, Node accessor)
    {
        if (AstUtilities.HasDynamicName(accessor))
        {
            // dynamic names can only be match up via checker symbol lookup, just return an object with just this accessor
            return GetAllAccessorDeclarationsForDeclaration(accessor, [accessor]);
        }

        var accessorName = GetPropertyNameForPropertyNameNode(accessor.Name()!);
        var accessorStatic = AstUtilities.IsStatic(accessor);
        var matches = new List<Node>();
        foreach (var member in parentDeclarations.OrEmpty())
        {
            if (!AstUtilities.IsAccessor(member) || AstUtilities.IsStatic(member) != accessorStatic)
            {
                continue;
            }
            var memberName = GetPropertyNameForPropertyNameNode(member.Name()!);
            if (memberName == accessorName)
            {
                matches.Add(member);
            }
        }
        return GetAllAccessorDeclarationsForDeclaration(accessor, matches.ToArray());
    }

    // ast/utilities.go:4277 — ClassElementOrClassElementParameterIsDecorated.
    internal static bool ClassElementOrClassElementParameterIsDecorated(bool useLegacyDecorators, Node node, Node parent)
    {
        Node[]? parameters = null;
        if (AstUtilities.IsAccessor(node))
        {
            var decls = GetAllAccessorDeclarations(parent.Members(), node);
            Node? firstAccessorWithDecorators = null;
            if (AstUtilities.HasDecorators(decls.FirstAccessor!))
            {
                firstAccessorWithDecorators = decls.FirstAccessor;
            }
            else if (decls.SecondAccessor != null && AstUtilities.HasDecorators(decls.SecondAccessor))
            {
                firstAccessorWithDecorators = decls.SecondAccessor;
            }
            if (firstAccessorWithDecorators == null || node != firstAccessorWithDecorators)
            {
                return false;
            }
            if (decls.SetAccessor != null)
            {
                parameters = decls.SetAccessor.Parameters();
            }
        }
        else if (NodeTests.IsMethodDeclaration(node))
        {
            parameters = node.Parameters();
        }
        if (AstUtilities.NodeIsDecorated(useLegacyDecorators, node, parent, null))
        {
            return true;
        }
        if (parameters != null && parameters.Length > 0)
        {
            foreach (var parameter in parameters)
            {
                if (AstUtilities.IsThisParameter(parameter))
                {
                    continue;
                }
                if (AstUtilities.NodeIsDecorated(useLegacyDecorators, parameter, node, parent))
                {
                    return true;
                }
            }
        }
        return false;
    }
    // ast/utilities.go:1081 — CanHaveModifiers.
    internal static bool CanHaveModifiers(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TypeParameter:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.ExportDeclaration:
                return true;
        }
        return false;
    }
    // ast.CanHaveIllegalModifiers: real body in AstUtilities (Utilities.Binder.cs, utilities.go:1069).
    internal static bool CanHaveIllegalModifiers(Node node) => AstUtilities.CanHaveIllegalModifiers(node);
    // IsEffectiveExternalModule is now REAL in Ast/Utilities.Binder.cs (utilities.go:1650); callers use AstUtilities.IsEffectiveExternalModule.

    // PORT-NOTE: Go's ast.SourceFile satisfies ast.HasFileName structurally; the C# Ast port declares
    // FileName()/Path() on SourceFile but not `: HasFileName`. Wrap here (mirrors FileLoader.AsHasFileName)
    // so program.GetEmitModuleFormatOfFile(SourceFile) call sites in Checker.Grammar.cs compile.
    private static HasFileName SourceFileAsHasFileName(SourceFile file) => new global::Ts.Compiler.CompilerHasFileName(file.FileName(), file.Path());

    // ---- checker-side, not-yet-landed (owned by other clusters) ----
    // IsInParameterInitializerBeforeContainingFunction is now REAL in Checker.Expressions.cs (checker.go:12204).
    internal static global::Ts.Diagnostics.Message GetVerbatimModuleSyntaxErrorMessage(Node node) => throw new NotImplementedException("getVerbatimModuleSyntaxErrorMessage");
    // IsDeclarationReadonly is now REAL in Checker.Widening.cs (utilities.go:805).
    // checker.go — hasAsyncModifier.
    internal static bool HasAsyncModifier(Node node) => AstUtilities.HasSyntacticModifier(node, ModifierFlags.Async);
}
