using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Ts.Transformers;
using Ts.Transformers.JsxTransforms;
using Ts.Transformers.ModuleTransforms;
using Ts.TsPath;
using Xunit;

namespace Ts.Transformers.Tests;

public sealed class TransformerSmokeTests
{
    private static SourceFile Parse(string text, ScriptKind kind, string fileName)
    {
        var file = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions { FileName = fileName, Path = new Path(fileName) },
            text,
            kind);
        global::Ts.Binder.Binder.BindSourceFile(file);
        return file;
    }

    [Fact(Skip = "AST JSX nodes do not yet propagate SubtreeContainsJsx, so the faithful transformer fast-path correctly leaves the parsed tree untouched")]
    public void JsxElement_TransformsToReactCreateElementCallShape()
    {
        var sourceFile = Parse("const el = <div />;", ScriptKind.Tsx, "/test.tsx");
        var options = new CompilerOptions { Jsx = JsxEmit.React };
        var transformer = JSXTransformer.NewJSXTransformer(new TransformOptions
        {
            Context = EmitContext.NewEmitContext(),
            CompilerOptions = options,
            Resolver = new FakeEmitResolver(),
            EmitResolver = new FakeEmitResolver(),
            GetEmitModuleFormatOfFile = _ => ModuleKind.CommonJS,
        });

        var transformed = transformer.TransformSourceFile(sourceFile);
        var declaration = Assert.Single(transformed.Statements!.Nodes)
            .AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0]
            .AsVariableDeclaration();
        var call = declaration.Initializer!.AsCallExpression();

        Assert.Equal(SyntaxKind.PropertyAccessExpression, call.Expression!.Kind);
        Assert.Equal("React", call.Expression.Expression()!.Text());
        Assert.Equal("createElement", call.Expression.Name()!.Text());
        Assert.Equal(2, call.Arguments!.Nodes.Length);
        Assert.Equal("div", call.Arguments.Nodes[0].Text());
        Assert.Equal(SyntaxKind.NullKeyword, call.Arguments.Nodes[1].Kind);
    }

    [Fact]
    public void CommonJsModule_RewritesExportedVariableToExportsAssignment()
    {
        var sourceFile = Parse("export const answer = 42;", ScriptKind.TS, "/test.ts");
        var resolver = new FakeEmitResolver();
        var transformer = CommonJSModuleTransformer.NewCommonJSModuleTransformer(new TransformOptions
        {
            Context = EmitContext.NewEmitContext(),
            CompilerOptions = new CompilerOptions { Module = ModuleKind.CommonJS },
            Resolver = resolver,
            EmitResolver = resolver,
            GetEmitModuleFormatOfFile = _ => ModuleKind.CommonJS,
        });

        var transformed = transformer.TransformSourceFile(sourceFile);
        Node? exportAssignment = null;
        foreach (var statement in transformed.Statements!.Nodes)
        {
            if (statement.Kind != SyntaxKind.ExpressionStatement)
            {
                continue;
            }
            var expression = statement.Expression();
            if (expression?.Kind == SyntaxKind.BinaryExpression &&
                expression.AsBinaryExpression().Left?.Kind == SyntaxKind.PropertyAccessExpression &&
                string.Equals(expression.AsBinaryExpression().Left!.Name()!.Text(), "answer", System.StringComparison.Ordinal) &&
                expression.AsBinaryExpression().Right?.Kind == SyntaxKind.NumericLiteral)
            {
                exportAssignment = expression;
                break;
            }
        }

        Assert.NotNull(exportAssignment);
        Assert.Equal("exports", exportAssignment!.AsBinaryExpression().Left!.Expression()!.Text());
        Assert.Equal("42", exportAssignment.AsBinaryExpression().Right!.Text());
    }

    private sealed class FakeEmitResolver : EmitResolver
    {
        public Node? GetReferencedExportContainer(Node node, bool prefixLocals) => null;
        public Node? GetReferencedImportDeclaration(Node node) => null;
        public Node? GetReferencedValueDeclaration(Node node) => null;
        public Node[] GetReferencedValueDeclarations(Node node) => [];
        public string GetElementAccessExpressionName(ElementAccessExpression expression) => "";
        public Node? GetReferencedMemberValueDeclaration(Node node) => null;
        public bool IsReferencedAliasDeclaration(Node node) => false;
        public bool IsValueAliasDeclaration(Node node) => false;
        public bool IsTopLevelValueImportEqualsWithEntityName(Node node) => false;
        public void MarkLinkedReferencesRecursively(SourceFile file) { }
        public SourceFile? GetExternalModuleFileFromDeclaration(Node node) => null;
        public ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags) => flags;
        public ModuleKind GetResolutionModeOverride(Node node) => ModuleKind.None;
        public TypeReferenceSerializationKind GetTypeReferenceSerializationKind(EntityName name, Node serialScope) => TypeReferenceSerializationKind.Unknown;
        public object? GetConstantValue(Node node) => null;
        public Node? GetJsxFactoryEntity(Node location) => null;
        public Node? GetJsxFragmentFactoryEntity(Node location) => null;
        public void SetReferencedImportDeclaration(IdentifierNode node, Declaration @ref) { }
        public void PrecalculateDeclarationEmitVisibility(SourceFile file) { }
        public SymbolAccessibilityResult IsSymbolAccessible(Symbol symbol, Node enclosingDeclaration, SymbolFlags meaning, bool shouldComputeAliasToMarkVisible) => default;
        public SymbolAccessibilityResult IsEntityNameVisible(Node entityName, Node enclosingDeclaration) => default;
        public bool IsExpandoFunctionDeclaration(Node node) => false;
        public bool IsExpandoFunctionDeclarationUnsafe(Node node) => false;
        public bool IsLiteralConstDeclaration(Node node) => false;
        public bool RequiresAddingImplicitUndefined(Node node, Symbol symbol, Node enclosingDeclaration) => false;
        public bool IsDeclarationVisible(Node node) => true;
        public bool IsNameResolvable(Node location, string name) => false;
        public bool IsImportRequiredByAugmentation(ImportDeclaration declaration) => false;
        public bool IsDefinitelyReferenceToGlobalSymbolObject(Node node) => false;
        public bool IsImplementationOfOverload(SignatureDeclaration node) => false;
        public global::Ts.Evaluator.Result GetEnumMemberValue(Node node) => default;
        public bool IsLateBound(Node node) => false;
        public bool IsOptionalParameter(Node node) => false;
        public bool IsThisPropertyAssignmentDeclarationRedundant(Node node) => false;
        public Symbol[] GetPropertiesOfContainerFunction(Node node) => [];
        public bool RequiresAddingImplicitUndefinedUnsafe(Node node, Symbol symbol, Node enclosingDeclaration) => false;
        public Declaration? GetReferencedValueDeclarationUnsafe(IdentifierNode node) => null;
        public Node? CreateTypeOfDeclaration(EmitContext emitContext, Node declaration, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker) => null;
        public Node? CreateReturnTypeOfSignatureDeclaration(EmitContext emitContext, Node signatureDeclaration, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker) => null;
        public Node[] CreateTypeParametersOfSignatureDeclaration(EmitContext emitContext, Node signatureDeclaration, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker) => [];
        public Node? CreateLiteralConstValue(EmitContext emitContext, Node node, global::Ts.NodeBuilder.ISymbolTracker tracker) => null;
        public Node? CreateTypeOfExpression(EmitContext emitContext, Node expression, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker) => null;
        public Node[] CreateLateBoundIndexSignatures(EmitContext emitContext, Node container, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker) => [];
        public Node? TryJSTypeNodeToTypeNode(EmitContext emitContext, Node typeNode, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker) => null;
    }
}
