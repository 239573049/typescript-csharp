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
    internal static AllAccessorDeclarations GetAllAccessorDeclarationsForDeclaration(Node accessor, Node[] declarationsOfSymbol) => throw new NotImplementedException("ast.GetAllAccessorDeclarationsForDeclaration");
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
    internal static bool CanHaveIllegalModifiers(Node node) => throw new NotImplementedException("ast.CanHaveIllegalModifiers");
    // IsEffectiveExternalModule is now REAL in Ast/Utilities.Binder.cs (utilities.go:1650); callers use AstUtilities.IsEffectiveExternalModule.

    // PORT-NOTE: Go's ast.SourceFile satisfies ast.HasFileName structurally; the C# Ast port declares
    // FileName()/Path() on SourceFile but not `: HasFileName`. Wrap here (mirrors FileLoader.AsHasFileName)
    // so program.GetEmitModuleFormatOfFile(SourceFile) call sites in Checker.Grammar.cs compile.
    private static HasFileName SourceFileAsHasFileName(SourceFile file) => new global::Ts.Compiler.CompilerHasFileName(file.FileName(), file.Path());

    // ---- checker-side, not-yet-landed (owned by other clusters) ----
    // IsInParameterInitializerBeforeContainingFunction is now REAL in Checker.Expressions.cs (checker.go:12204).
    internal static global::Ts.Diagnostics.Message GetVerbatimModuleSyntaxErrorMessage(Node node) => throw new NotImplementedException("getVerbatimModuleSyntaxErrorMessage");
    // IsDeclarationReadonly is now REAL in Checker.Widening.cs (utilities.go:805).
    internal static bool HasAsyncModifier(Node node) => throw new NotImplementedException("hasAsyncModifier");
}
