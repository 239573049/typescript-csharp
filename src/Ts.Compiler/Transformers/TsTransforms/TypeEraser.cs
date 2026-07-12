// Ported from tsgo internal/transformers/tstransforms/typeeraser.go.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

/// <summary>Erases TypeScript-only syntax while retaining JavaScript runtime syntax.</summary>
public sealed class TypeEraserTransformer : global::Ts.Transformers.Transformer
{
    private readonly CompilerOptions compilerOptions;
    private Node? parentNode;
    private Node? currentNode;

    private TypeEraserTransformer(global::Ts.Transformers.TransformOptions options)
    {
        compilerOptions = options.CompilerOptions ?? new CompilerOptions();
        NewTransformer(Visit, options.Context);
    }

    public static global::Ts.Transformers.Transformer NewTypeEraserTransformer(
        global::Ts.Transformers.TransformOptions options) => new TypeEraserTransformer(options);

    // Pushes a new child node onto the ancestor tracking stack, returning the grandparent node to be restored later via PopNode.
    private Node? PushNode(Node node)
    {
        var grandparentNode = parentNode;
        parentNode = currentNode;
        currentNode = node;
        return grandparentNode;
    }

    // Pops the last child node off the ancestor tracking stack, restoring the grandparent node.
    private void PopNode(Node? grandparentNode)
    {
        currentNode = parentNode;
        parentNode = grandparentNode;
    }

    private Statement Elide(Node node) => EmitContext().NewNotEmittedStatement(node);

    private Node? Visit(Node node)
    {
        // PORT-NOTE: Several parser-produced container nodes do not yet aggregate SubtreeFacts in
        // the C# port. Traversing them is semantically equivalent and prevents TS syntax leaking.
        if (global::Ts.Ast.AstUtilities.IsStatement(node) &&
            global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Ambient))
        {
            return Elide(node);
        }

        var grandparentNode = PushNode(node);
        try
        {
            switch (node.Kind)
            {
                // TypeScript accessibility and readonly modifiers are elided.
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.PrivateKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.AbstractKeyword:
                case SyntaxKind.OverrideKeyword:
                case SyntaxKind.ConstKeyword:
                case SyntaxKind.DeclareKeyword:
                case SyntaxKind.ReadonlyKeyword:
                // TypeScript type nodes are elided.
                case SyntaxKind.ArrayType:
                case SyntaxKind.TupleType:
                case SyntaxKind.OptionalType:
                case SyntaxKind.RestType:
                case SyntaxKind.TypeLiteral:
                case SyntaxKind.TypePredicate:
                case SyntaxKind.TypeParameter:
                case SyntaxKind.AnyKeyword:
                case SyntaxKind.UnknownKeyword:
                case SyntaxKind.BooleanKeyword:
                case SyntaxKind.StringKeyword:
                case SyntaxKind.NumberKeyword:
                case SyntaxKind.NeverKeyword:
                case SyntaxKind.VoidKeyword:
                case SyntaxKind.SymbolKeyword:
                case SyntaxKind.ConstructorType:
                case SyntaxKind.FunctionType:
                case SyntaxKind.TypeQuery:
                case SyntaxKind.TypeReference:
                case SyntaxKind.UnionType:
                case SyntaxKind.IntersectionType:
                case SyntaxKind.ConditionalType:
                case SyntaxKind.ParenthesizedType:
                case SyntaxKind.ThisType:
                case SyntaxKind.TypeOperator:
                case SyntaxKind.IndexedAccessType:
                case SyntaxKind.MappedType:
                case SyntaxKind.LiteralType:
                // TypeScript index signatures are elided.
                case SyntaxKind.IndexSignature:
                    return null;

                case SyntaxKind.InKeyword:
                case SyntaxKind.OutKeyword:
                    // The `in` binary operator shares this token kind, so only elide modifiers.
                    return parentNode is null || !global::Ts.Ast.NodeTests.IsBinaryExpression(parentNode)
                        ? null
                        : Visitor().VisitEachChild(node);

                case SyntaxKind.JSImportDeclaration:
                    // Reparsed CommonJS imports are elided.
                    return null;

                case SyntaxKind.TypeAliasDeclaration:
                case SyntaxKind.JSTypeAliasDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                    return Elide(node);

                case SyntaxKind.NamespaceExportDeclaration:
                    return null;

                case SyntaxKind.ModuleDeclaration:
                {
                    var declaration = node.AsModuleDeclaration();
                    var innermost = TsTransformUtilities.GetInnermostModuleDeclarationFromDottedModule(declaration);
                    if (!global::Ts.Ast.NodeTests.IsIdentifier(node.Name()!) ||
                        !global::Ts.Ast.AstUtilities.IsInstantiatedModule(node, compilerOptions.ShouldPreserveConstEnums()) ||
                        innermost.Body is null)
                    {
                        return Elide(node);
                    }
                    return Visitor().VisitEachChild(node);
                }

                case SyntaxKind.ExpressionWithTypeArguments:
                {
                    var declaration = node.AsExpressionWithTypeArguments();
                    return Factory().UpdateExpressionWithTypeArguments(
                        declaration,
                        Visitor().VisitNode(declaration.Expression),
                        null);
                }

                case SyntaxKind.PropertyDeclaration:
                {
                    var declaration = node.AsPropertyDeclaration();
                    var ambientOrAbstract = global::Ts.Ast.AstUtilities.HasSyntacticModifier(
                        node,
                        ModifierFlags.Ambient | ModifierFlags.Abstract);
                    if (compilerOptions.ExperimentalDecorators == Tristate.True &&
                        ambientOrAbstract && global::Ts.Ast.AstUtilities.HasDecorators(node))
                    {
                        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
                            declaration,
                            Visitor().VisitModifiers(declaration.Modifiers()),
                            Visitor().VisitNode(declaration.Name()),
                            null,
                            null,
                            Visitor().VisitNode(declaration.Initializer()));
                    }
                    if (ambientOrAbstract)
                    {
                        return null;
                    }
                    return Factory().AsNodeFactory().UpdatePropertyDeclaration(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        Visitor().VisitNode(declaration.Name()),
                        null,
                        null,
                        Visitor().VisitNode(declaration.Initializer()));
                }

                case SyntaxKind.Constructor:
                {
                    var declaration = node.AsConstructorDeclaration();
                    if (global::Ts.Ast.AstUtilities.NodeIsMissing(declaration.Body))
                    {
                        return null;
                    }
                    return Factory().AsNodeFactory().UpdateConstructorDeclaration(
                        declaration, null, null, Visitor().VisitNodes(declaration.Parameters),
                        null, null, Visitor().VisitNode(declaration.Body));
                }

                case SyntaxKind.MethodDeclaration:
                {
                    var declaration = node.AsMethodDeclaration();
                    if (global::Ts.Ast.AstUtilities.NodeIsMissing(declaration.Body))
                    {
                        return null;
                    }
                    return Factory().AsNodeFactory().UpdateMethodDeclaration(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        declaration.AsteriskToken,
                        Visitor().VisitNode(declaration.Name()),
                        null, null,
                        Visitor().VisitNodes(declaration.Parameters),
                        null, null,
                        Visitor().VisitNode(declaration.Body));
                }

                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                    return VisitAccessor(node);

                case SyntaxKind.VariableDeclaration:
                {
                    var declaration = node.AsVariableDeclaration();
                    var updated = Factory().UpdateVariableDeclaration(
                        declaration,
                        Visitor().VisitNode(declaration.Name()),
                        null,
                        null,
                        Visitor().VisitNode(declaration.Initializer()));
                    if (declaration.Type is not null && updated.Name() is { } updatedName)
                    {
                        EmitContext().SetTypeNode(updatedName, declaration.Type);
                    }
                    return updated;
                }

                case SyntaxKind.HeritageClause:
                {
                    var declaration = node.AsHeritageClause();
                    return declaration.Token == SyntaxKind.ImplementsKeyword
                        ? null
                        : Factory().AsNodeFactory().UpdateHeritageClause(
                            declaration, declaration.Token, Visitor().VisitNodes(declaration.Types));
                }

                case SyntaxKind.ClassDeclaration:
                {
                    var declaration = node.AsClassDeclaration();
                    return Factory().AsNodeFactory().UpdateClassDeclaration(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        Visitor().VisitNode(declaration.Name()),
                        null,
                        Visitor().VisitNodes(declaration.HeritageClauses),
                        Visitor().VisitNodes(declaration.Members));
                }

                case SyntaxKind.ClassExpression:
                {
                    var declaration = node.AsClassExpression();
                    return Factory().AsNodeFactory().UpdateClassExpression(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        Visitor().VisitNode(declaration.Name()),
                        null,
                        Visitor().VisitNodes(declaration.HeritageClauses),
                        Visitor().VisitNodes(declaration.Members));
                }

                case SyntaxKind.FunctionDeclaration:
                {
                    var declaration = node.AsFunctionDeclaration();
                    if (global::Ts.Ast.AstUtilities.NodeIsMissing(declaration.Body))
                    {
                        return Elide(node);
                    }
                    return Factory().AsNodeFactory().UpdateFunctionDeclaration(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        declaration.AsteriskToken,
                        Visitor().VisitNode(declaration.Name()),
                        null,
                        Visitor().VisitNodes(declaration.Parameters),
                        null, null,
                        Visitor().VisitNode(declaration.Body));
                }

                case SyntaxKind.FunctionExpression:
                {
                    var declaration = node.AsFunctionExpression();
                    return Factory().AsNodeFactory().UpdateFunctionExpression(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        declaration.AsteriskToken,
                        Visitor().VisitNode(declaration.Name()),
                        null,
                        Visitor().VisitNodes(declaration.Parameters),
                        null, null,
                        Visitor().VisitNode(declaration.Body));
                }

                case SyntaxKind.ArrowFunction:
                {
                    var declaration = node.AsArrowFunction();
                    return Factory().AsNodeFactory().UpdateArrowFunction(
                        declaration,
                        Visitor().VisitModifiers(declaration.Modifiers()),
                        null,
                        Visitor().VisitNodes(declaration.Parameters),
                        null, null,
                        declaration.EqualsGreaterThanToken,
                        Visitor().VisitNode(declaration.Body));
                }

                case SyntaxKind.Parameter:
                    return VisitParameter(node.AsParameterDeclaration());

                case SyntaxKind.CallExpression:
                {
                    var expression = node.AsCallExpression();
                    return Factory().AsNodeFactory().UpdateCallExpression(
                        expression,
                        Visitor().VisitNode(expression.Expression),
                        expression.QuestionDotToken,
                        null,
                        Visitor().VisitNodes(expression.Arguments),
                        expression.Flags);
                }

                case SyntaxKind.NewExpression:
                {
                    var expression = node.AsNewExpression();
                    return Factory().AsNodeFactory().UpdateNewExpression(
                        expression,
                        Visitor().VisitNode(expression.Expression),
                        null,
                        Visitor().VisitNodes(expression.Arguments));
                }

                case SyntaxKind.TaggedTemplateExpression:
                {
                    var expression = node.AsTaggedTemplateExpression();
                    return Factory().AsNodeFactory().UpdateTaggedTemplateExpression(
                        expression,
                        Visitor().VisitNode(expression.Tag),
                        expression.QuestionDotToken,
                        null,
                        Visitor().VisitNode(expression.Template),
                        expression.Flags);
                }

                case SyntaxKind.NonNullExpression:
                case SyntaxKind.TypeAssertionExpression:
                case SyntaxKind.AsExpression:
                case SyntaxKind.SatisfiesExpression:
                {
                    var partial = Factory().AsNodeFactory().NewPartiallyEmittedExpression(
                        Visitor().VisitNode(node.Expression()));
                    EmitContext().SetOriginal(partial, node);
                    partial.Loc = node.Loc;
                    return partial;
                }

                case SyntaxKind.ParenthesizedExpression:
                    if (!global::Ts.Ast.AstUtilities.IsJSDocTypeAssertion(node))
                    {
                        var expression = node.AsParenthesizedExpression();
                        var inner = global::Ts.Ast.AstUtilities.SkipOuterExpressions(
                            expression.Expression!,
                            OuterExpressionKinds.All & ~OuterExpressionKinds.Assertions);
                        if (global::Ts.Ast.AstUtilities.IsAssertionExpression(inner) ||
                            global::Ts.Ast.NodeTests.IsSatisfiesExpression(inner))
                        {
                            var partial = Factory().AsNodeFactory().NewPartiallyEmittedExpression(
                                Visitor().VisitNode(expression.Expression));
                            EmitContext().SetOriginal(partial, node);
                            partial.Loc = node.Loc;
                            return partial;
                        }
                    }
                    return Visitor().VisitEachChild(node);

                case SyntaxKind.JsxSelfClosingElement:
                {
                    var expression = node.AsJsxSelfClosingElement();
                    return Factory().AsNodeFactory().UpdateJsxSelfClosingElement(
                        expression,
                        Visitor().VisitNode(expression.TagName),
                        null,
                        Visitor().VisitNode(expression.Attributes));
                }

                case SyntaxKind.JsxOpeningElement:
                {
                    var expression = node.AsJsxOpeningElement();
                    return Factory().AsNodeFactory().UpdateJsxOpeningElement(
                        expression,
                        Visitor().VisitNode(expression.TagName),
                        null,
                        Visitor().VisitNode(expression.Attributes));
                }

                case SyntaxKind.ImportEqualsDeclaration:
                    return node.AsImportEqualsDeclaration().IsTypeOnly
                        ? null
                        : Visitor().VisitEachChild(node);

                case SyntaxKind.ImportDeclaration:
                    return VisitImportDeclaration(node.AsImportDeclaration());

                case SyntaxKind.ImportClause:
                    return VisitImportClause(node.AsImportClause());

                case SyntaxKind.NamedImports:
                    return VisitNamedImports(node.AsNamedImports());

                case SyntaxKind.ImportSpecifier:
                    return node.AsImportSpecifier().IsTypeOnly ? null : node;

                case SyntaxKind.ExportDeclaration:
                    return VisitExportDeclaration(node.AsExportDeclaration());

                case SyntaxKind.NamedExports:
                    return VisitNamedExports(node.AsNamedExports());

                case SyntaxKind.ExportSpecifier:
                    return node.AsExportSpecifier().IsTypeOnly ? null : node;

                case SyntaxKind.EnumDeclaration:
                    return global::Ts.Ast.AstUtilities.IsEnumConst(node)
                        ? node
                        : Visitor().VisitEachChild(node);

                default:
                    return Visitor().VisitEachChild(node);
            }
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node? VisitAccessor(Node node)
    {
        var isAbstract = global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Abstract);
        if (global::Ts.Ast.AstUtilities.NodeIsMissing(node.Body()) && isAbstract)
        {
            return null;
        }
        var body = Visitor().VisitNode(node.Body()) ?? Factory().NewBlock(Factory().NewNodeList([]), false);
        if (node.Kind == SyntaxKind.GetAccessor)
        {
            var declaration = node.AsGetAccessorDeclaration();
            return Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
                declaration,
                Visitor().VisitModifiers(declaration.Modifiers()),
                Visitor().VisitNode(declaration.Name()),
                null,
                Visitor().VisitNodes(declaration.Parameters),
                null, null,
                body);
        }
        else
        {
            var declaration = node.AsSetAccessorDeclaration();
            return Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
                declaration,
                Visitor().VisitModifiers(declaration.Modifiers()),
                Visitor().VisitNode(declaration.Name()),
                null,
                Visitor().VisitNodes(declaration.Parameters),
                null, null,
                body);
        }
    }

    private Node? VisitParameter(ParameterDeclaration declaration)
    {
        if (global::Ts.Ast.AstUtilities.IsThisParameter(declaration.AsNode()))
        {
            return null;
        }

        ModifierList? modifiers = null;
        if (parentNode is not null &&
            global::Ts.Ast.AstUtilities.IsParameterPropertyDeclaration(declaration.AsNode(), parentNode))
        {
            modifiers = global::Ts.Transformers.Transformers.ExtractModifiers(
                EmitContext(), declaration.Modifiers(), ModifierFlags.ParameterPropertyModifier);
        }
        if (global::Ts.Ast.AstUtilities.HasDecorators(declaration.AsNode()))
        {
            var decorators = declaration.Decorators();
            var (visited, _) = Visitor().VisitSlice(decorators);
            if (visited is { Length: > 0 })
            {
                if (modifiers is null)
                {
                    modifiers = Factory().AsNodeFactory().NewModifierList(visited);
                }
                else
                {
                    var combined = new Node[modifiers.Nodes.Length + visited.Length];
                    Array.Copy(modifiers.Nodes, combined, modifiers.Nodes.Length);
                    Array.Copy(visited, 0, combined, modifiers.Nodes.Length, visited.Length);
                    modifiers = Factory().AsNodeFactory().NewModifierList(combined);
                }
            }
        }
        return Factory().AsNodeFactory().UpdateParameterDeclaration(
            declaration,
            modifiers,
            declaration.DotDotDotToken,
            Visitor().VisitNode(declaration.Name()),
            null,
            null,
            Visitor().VisitNode(declaration.Initializer()));
    }

    private Node? VisitImportDeclaration(ImportDeclaration declaration)
    {
        if (declaration.ImportClause is null)
        {
            return declaration.AsNode();
        }
        var importClause = Visitor().VisitNode(declaration.ImportClause);
        return importClause is null
            ? null
            : Factory().AsNodeFactory().UpdateImportDeclaration(
                declaration, declaration.Modifiers(), importClause,
                declaration.ModuleSpecifier, declaration.Attributes);
    }

    private Node? VisitImportClause(ImportClause declaration)
    {
        if (declaration.IsTypeOnly())
        {
            return null;
        }
        var name = declaration.Name();
        var namedBindings = Visitor().VisitNode(declaration.NamedBindings);
        return name is null && namedBindings is null
            ? null
            : Factory().AsNodeFactory().UpdateImportClause(
                declaration, declaration.PhaseModifier, name, namedBindings);
    }

    private Node? VisitNamedImports(NamedImports declaration)
    {
        if (declaration.Elements?.Nodes.Length == 0)
        {
            return declaration.AsNode();
        }
        var elements = Visitor().VisitNodes(declaration.Elements);
        if (compilerOptions.VerbatimModuleSyntax != Tristate.True && (elements?.Nodes.Length ?? 0) == 0)
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateNamedImports(declaration, elements);
    }

    private Node? VisitExportDeclaration(ExportDeclaration declaration)
    {
        if (declaration.IsTypeOnly)
        {
            return null;
        }
        Node? exportClause = null;
        if (declaration.ExportClause is not null)
        {
            exportClause = Visitor().VisitNode(declaration.ExportClause);
            if (exportClause is null)
            {
                return null;
            }
        }
        return Factory().AsNodeFactory().UpdateExportDeclaration(
            declaration, null, false, exportClause,
            Visitor().VisitNode(declaration.ModuleSpecifier),
            Visitor().VisitNode(declaration.Attributes));
    }

    private Node? VisitNamedExports(NamedExports declaration)
    {
        if (declaration.Elements?.Nodes.Length == 0)
        {
            return declaration.AsNode();
        }
        var elements = Visitor().VisitNodes(declaration.Elements);
        if (compilerOptions.VerbatimModuleSyntax != Tristate.True && (elements?.Nodes.Length ?? 0) == 0)
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateNamedExports(declaration, elements);
    }
}
