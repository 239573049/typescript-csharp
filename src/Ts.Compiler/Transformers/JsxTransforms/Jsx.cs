// Ported from tsgo internal/transformers/jsxtransforms/jsx.go.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Transformers.JsxTransforms;

public sealed class JSXTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;
    private readonly global::Ts.Printer.EmitResolver emitResolver;

    private string importSpecifier = "";
    private Node? filenameDeclaration;
    private readonly OrderedMap<string, Dictionary<string, Node>> utilizedImplicitRuntimeImports = new();
    private bool inJsxChild;
    private SourceFile? currentSourceFile;

    private JSXTransformer(TransformOptions options)
    {
        compilerOptions = options.CompilerOptions!;
        emitResolver = options.EmitResolver!;
        NewTransformer(Visit, options.Context);
    }

    public static Transformer NewJSXTransformer(TransformOptions options) => new JSXTransformer(options);

    private Node GetCurrentFileNameExpression()
    {
        if (filenameDeclaration is not null)
        {
            return filenameDeclaration.AsVariableDeclaration().Name()!;
        }
        var declaration = Factory().NewVariableDeclaration(
            Factory().NewUniqueNameEx("_jsxFileName", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic |
                    global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            }),
            null,
            null,
            Factory().NewStringLiteral(currentSourceFile!.FileName(), TokenFlags.None));
        filenameDeclaration = declaration;
        return declaration.AsVariableDeclaration().Name()!;
    }

    private string GetJsxFactoryCalleePrimitive(bool isStaticChildren)
    {
        if (compilerOptions.Jsx == JsxEmit.ReactJSXDev)
        {
            return "jsxDEV";
        }
        return isStaticChildren ? "jsxs" : "jsx";
    }

    private Node GetJsxFactoryCallee(bool isStaticChildren) =>
        GetImplicitImportForName(GetJsxFactoryCalleePrimitive(isStaticChildren));

    private Node GetImplicitJsxFragmentReference() => GetImplicitImportForName("Fragment");

    private Node GetImplicitImportForName(string name)
    {
        var importSource = importSpecifier;
        if (!string.Equals(name, "createElement", StringComparison.Ordinal))
        {
            importSource = TransformerAstAdapters.GetJsxRuntimeImport(importSource, compilerOptions);
        }

        if (utilizedImplicitRuntimeImports.TryGet(importSource, out var existing))
        {
            if (existing.TryGetValue(name, out var element))
            {
                return element.AsImportSpecifier().Name()!;
            }
        }
        else
        {
            existing = new Dictionary<string, Node>(StringComparer.Ordinal);
            utilizedImplicitRuntimeImports.Set(importSource, existing);
        }

        var generatedName = Factory().NewUniqueNameEx("_" + name, new global::Ts.Printer.AutoGenerateOptions
        {
            Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic |
                global::Ts.Printer.GeneratedIdentifierFlags.FileLevel |
                global::Ts.Printer.GeneratedIdentifierFlags.AllowNameSubstitution,
        });
        var specifier = Factory().NewImportSpecifier(false, Factory().NewIdentifier(name), generatedName);
        emitResolver.SetReferencedImportDeclaration(generatedName, specifier);
        existing[name] = specifier;
        return specifier.Name()!;
    }

    private void SetInChild(bool value) => inJsxChild = value;

    private Node? Visit(Node node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsJsx) == 0)
        {
            return node;
        }
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                SetInChild(false);
                return VisitSourceFile(node.AsSourceFile());
            case SyntaxKind.JsxElement:
                return VisitJsxElement(node.AsJsxElement());
            case SyntaxKind.JsxSelfClosingElement:
                return VisitJsxSelfClosingElement(node.AsJsxSelfClosingElement());
            case SyntaxKind.JsxFragment:
                return VisitJsxFragment(node.AsJsxFragment());
            case SyntaxKind.JsxOpeningElement:
                throw new InvalidOperationException("JsxOpeningElement should not be visited, handled in visitJsxElement");
            case SyntaxKind.JsxOpeningFragment:
                throw new InvalidOperationException("JsxOpeningFragment should not be visited, handled in visitJsxFragment");
            case SyntaxKind.JsxText:
                SetInChild(false);
                return VisitJsxText(node.AsJsxText());
            case SyntaxKind.JsxExpression:
                SetInChild(false);
                return VisitJsxExpression(node.AsJsxExpression());
            default:
                SetInChild(false);
                return Visitor().VisitEachChild(node);
        }
    }

    /**
     * The react jsx/jsxs transform falls back to `createElement` when an explicit `key` argument comes after a spread.
     */
    private static bool HasKeyAfterPropsSpread(Node node)
    {
        var spread = false;
        var opener = node.Kind == SyntaxKind.JsxElement ? node.AsJsxElement().OpeningElement! : node;
        foreach (var element in opener.Attributes()!.Properties() ?? [])
        {
            if (global::Ts.Ast.NodeTests.IsJsxSpreadAttribute(element) &&
                (!global::Ts.Ast.NodeTests.IsObjectLiteralExpression(element.Expression()!) ||
                global::Ts.Core.CoreUtil.Some(element.Expression()!.Properties(), global::Ts.Ast.NodeTests.IsSpreadAssignment)))
            {
                spread = true;
            }
            else if (spread && global::Ts.Ast.NodeTests.IsJsxAttribute(element) &&
                global::Ts.Ast.NodeTests.IsIdentifier(element.Name()!) &&
                string.Equals(element.Name()!.Text(), "key", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private bool ShouldUseCreateElement(Node node) => importSpecifier.Length == 0 || HasKeyAfterPropsSpread(node);

    private static Node[] InsertStatementAfterPrologue(
        Node[] statements,
        Node? statement,
        Func<Node, bool> isPrologueDirective)
    {
        if (statement is null)
        {
            return statements;
        }
        var statementIndex = 0;
        for (; statementIndex < statements.Length; statementIndex++)
        {
            if (!isPrologueDirective(statements[statementIndex]))
            {
                break;
            }
        }
        var result = new Node[statements.Length + 1];
        Array.Copy(statements, 0, result, 0, statementIndex);
        result[statementIndex] = statement;
        Array.Copy(statements, statementIndex, result, statementIndex + 1, statements.Length - statementIndex);
        return result;
    }

    private bool IsAnyPrologueDirective(Node node) =>
        global::Ts.Ast.AstUtilities.IsPrologueDirective(node) ||
        (EmitContext().EmitFlags(node) & global::Ts.Printer.EmitFlags.CustomPrologue) != 0;

    private Node[] InsertStatementAfterCustomPrologue(Node[] statements, Node? statement) =>
        InsertStatementAfterPrologue(statements, statement, IsAnyPrologueDirective);

    private static int SortImportSpecifiers(Node left, Node right)
    {
        var result = global::Ts.StringUtil.StringUtil.CompareStringsCaseSensitive(left.PropertyName()!.Text(), right.PropertyName()!.Text());
        return result != 0
            ? result
            : global::Ts.StringUtil.StringUtil.CompareStringsCaseSensitive(left.AsImportSpecifier().Name()!.Text(), right.AsImportSpecifier().Name()!.Text());
    }

    private static Node[] GetSortedSpecifiers(Dictionary<string, Node> map)
    {
        var result = new Node[map.Count];
        // map-iter: 已排序 — Dictionary values are sorted immediately below using the Go comparator.
        map.Values.CopyTo(result, 0);
        Array.Sort(result, SortImportSpecifiers);
        return result;
    }

    private Node VisitSourceFile(SourceFile file)
    {
        if (file.IsDeclarationFile)
        {
            return file.AsNode();
        }

        currentSourceFile = file;
        importSpecifier = TransformerAstAdapters.GetJsxImplicitImportBase(compilerOptions, file);
        filenameDeclaration = null;
        utilizedImplicitRuntimeImports.Clear();

        var visited = Visitor().VisitEachChild(file.AsNode())!;
        EmitContext().AddEmitHelper(visited.AsNode(), EmitContext().ReadEmitHelpers());
        var statements = visited.Statements() ?? [];
        var statementsUpdated = false;
        if (filenameDeclaration is not null)
        {
            statements = InsertStatementAfterCustomPrologue(
                statements,
                Factory().NewVariableStatement(
                    null,
                    Factory().NewVariableDeclarationList(Factory().NewNodeList([filenameDeclaration]), NodeFlags.Const)));
            statementsUpdated = true;
        }

        if (utilizedImplicitRuntimeImports.Size() > 0)
        {
            if (global::Ts.Ast.AstUtilities.IsExternalModule(file))
            {
                statementsUpdated = true;
                List<Node> newStatements = new(utilizedImplicitRuntimeImports.Size());
                foreach (var entry in utilizedImplicitRuntimeImports.Entries())
                {
                    var statement = Factory().NewImportDeclaration(
                        null,
                        Factory().NewImportClause(
                            SyntaxKind.Unknown,
                            null,
                            Factory().NewNamedImports(Factory().NewNodeList(GetSortedSpecifiers(entry.Value)))),
                        Factory().NewStringLiteral(entry.Key, TokenFlags.None),
                        null);
                    global::Ts.Ast.AstUtilities.SetParentInChildren(statement);
                    newStatements.Add(statement);
                }
                foreach (var statement in newStatements)
                {
                    statements = InsertStatementAfterCustomPrologue(statements, statement);
                }
            }
            else if (global::Ts.Ast.AstUtilities.IsExternalOrCommonJSModule(file))
            {
                statementsUpdated = true;
                List<Node> newStatements = new(utilizedImplicitRuntimeImports.Size());
                foreach (var entry in utilizedImplicitRuntimeImports.Entries())
                {
                    var sorted = GetSortedSpecifiers(entry.Value);
                    var bindingElements = new Node[sorted.Length];
                    for (var i = 0; i < sorted.Length; i++)
                    {
                        bindingElements[i] = Factory().NewBindingElement(null, sorted[i].PropertyName(), sorted[i].Name(), null);
                    }
                    var statement = Factory().NewVariableStatement(
                        null,
                        Factory().NewVariableDeclarationList(
                            Factory().NewNodeList([
                                Factory().NewVariableDeclaration(
                                    Factory().NewBindingPattern(SyntaxKind.ObjectBindingPattern, Factory().NewNodeList(bindingElements)),
                                    null,
                                    null,
                                    Factory().NewCallExpression(
                                        Factory().NewIdentifier("require"),
                                        null,
                                        null,
                                        Factory().NewNodeList([Factory().NewStringLiteral(entry.Key, TokenFlags.None)]),
                                        NodeFlags.None)),
                            ]),
                            NodeFlags.Const));
                    global::Ts.Ast.AstUtilities.SetParentInChildren(statement);
                    newStatements.Add(statement);
                }
                foreach (var statement in newStatements)
                {
                    statements = InsertStatementAfterCustomPrologue(statements, statement);
                }
            }
            else
            {
                // Do nothing (script file) - consider an error in the checker?
            }
        }

        if (statementsUpdated)
        {
            visited = Factory().UpdateSourceFile(file, Factory().NewNodeList(statements), file.EndOfFileToken);
        }

        currentSourceFile = null;
        importSpecifier = "";
        filenameDeclaration = null;
        utilizedImplicitRuntimeImports.Clear();
        return visited;
    }

    private Node VisitJsxElement(JsxElement element)
    {
        var location = new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(currentSourceFile!.Text(), element.Pos), element.End);
        return ShouldUseCreateElement(element.AsNode())
            ? VisitJsxOpeningLikeElementCreateElement(element.OpeningElement!, element.Children, location)
            : VisitJsxOpeningLikeElementJSX(element.OpeningElement!, element.Children, location);
    }

    private Node VisitJsxSelfClosingElement(JsxSelfClosingElement element)
    {
        var location = new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(currentSourceFile!.Text(), element.Pos), element.End);
        return ShouldUseCreateElement(element.AsNode())
            ? VisitJsxOpeningLikeElementCreateElement(element.AsNode(), null, location)
            : VisitJsxOpeningLikeElementJSX(element.AsNode(), null, location);
    }

    private Node VisitJsxFragment(JsxFragment fragment)
    {
        var location = new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(currentSourceFile!.Text(), fragment.Pos), fragment.End);
        return importSpecifier.Length == 0
            ? VisitJsxOpeningFragmentCreateElement(fragment.OpeningFragment!.AsJsxOpeningFragment(), fragment.Children, location)
            : VisitJsxOpeningFragmentJSX(fragment.OpeningFragment!.AsJsxOpeningFragment(), fragment.Children, location);
    }

    private Node? ConvertJsxChildrenToChildrenPropObject(Node[] children)
    {
        var property = ConvertJsxChildrenToChildrenPropAssignment(children);
        return property is null ? null : Factory().NewObjectLiteralExpression(Factory().NewNodeList([property]), false);
    }

    private Node? TransformJsxChildToExpression(Node node)
    {
        var previous = inJsxChild;
        SetInChild(true);
        try
        {
            return Visitor().Visit!(node);
        }
        finally
        {
            SetInChild(previous);
        }
    }

    private Node? ConvertJsxChildrenToChildrenPropAssignment(Node[] children)
    {
        var nonWhitespaceChildren = TransformerAstAdapters.GetSemanticJsxChildren(children);
        if (nonWhitespaceChildren.Length == 1 &&
            (nonWhitespaceChildren[0].Kind != SyntaxKind.JsxExpression || nonWhitespaceChildren[0].AsJsxExpression().DotDotDotToken is null))
        {
            var result = TransformJsxChildToExpression(nonWhitespaceChildren[0]);
            return result is null
                ? null
                : Factory().NewPropertyAssignment(null, Factory().NewIdentifier("children"), null, null, result);
        }

        List<Node> results = new(nonWhitespaceChildren.Length);
        foreach (var child in nonWhitespaceChildren)
        {
            var result = TransformJsxChildToExpression(child);
            if (result is null)
            {
                continue;
            }
            EmitContext().SetEmitFlags(result, EmitContext().EmitFlags(result) & ~global::Ts.Printer.EmitFlags.StartOnNewLine);
            results.Add(result);
        }
        return results.Count == 0
            ? null
            : Factory().NewPropertyAssignment(
                null,
                Factory().NewIdentifier("children"),
                null,
                null,
                Factory().NewArrayLiteralExpression(Factory().NewNodeList(results.ToArray()), false));
    }

    private Node GetTagName(Node node)
    {
        if (node.Kind == SyntaxKind.JsxElement)
        {
            return GetTagName(node.AsJsxElement().OpeningElement!);
        }
        if (TransformerAstAdapters.IsJsxOpeningLikeElement(node))
        {
            var tagName = node.TagName()!;
            if (global::Ts.Ast.NodeTests.IsIdentifier(tagName) && global::Ts.Scanner.Scanner.IsIntrinsicJsxName(tagName.Text()))
            {
                return Factory().NewStringLiteral(tagName.Text(), TokenFlags.None);
            }
            if (global::Ts.Ast.NodeTests.IsJsxNamespacedName(tagName))
            {
                return Factory().NewStringLiteral(
                    tagName.AsJsxNamespacedName().Namespace!.Text() + ":" + tagName.AsJsxNamespacedName().Name()!.Text(),
                    TokenFlags.None);
            }
            return Factory().CreateExpressionFromEntityName(tagName);
        }
        throw new InvalidOperationException("unhandled node kind passed to getTagName: " + node.Kind.ToDisplayString());
    }

    private Node VisitJsxOpeningLikeElementJSX(Node element, NodeList? children, TextRange location)
    {
        var tagName = GetTagName(element);
        var childrenProperty = children is { Nodes.Length: > 0 }
            ? ConvertJsxChildrenToChildrenPropAssignment(children.Nodes)
            : null;
        Node? keyAttribute = null;
        var attributes = element.Attributes()!.Properties() ?? [];
        for (var i = 0; i < attributes.Length; i++)
        {
            var property = attributes[i];
            if (property.Kind == SyntaxKind.JsxAttribute && property.Name() is { } name &&
                global::Ts.Ast.NodeTests.IsIdentifier(name) && string.Equals(name.Text(), "key", StringComparison.Ordinal))
            {
                keyAttribute = property;
                var withoutKey = new Node[attributes.Length - 1];
                Array.Copy(attributes, 0, withoutKey, 0, i);
                Array.Copy(attributes, i + 1, withoutKey, i, attributes.Length - i - 1);
                attributes = withoutKey;
                break;
            }
        }

        Node @object;
        if (attributes.Length > 0)
        {
            @object = TransformJsxAttributesToObjectProps(attributes, childrenProperty);
        }
        else
        {
            @object = Factory().NewObjectLiteralExpression(
                Factory().NewNodeList(childrenProperty is null ? [] : [childrenProperty]),
                false);
        }
        return VisitJsxOpeningLikeElementOrFragmentJSX(tagName, @object, keyAttribute, children, location);
    }

    private Node TransformJsxAttributesToObjectProps(Node[] attributes, Node? childrenProperty)
    {
        return compilerOptions.GetEmitScriptTarget() >= ScriptTarget.ES2018
            ? Factory().NewObjectLiteralExpression(Factory().NewNodeList(TransformJsxAttributesToProps(attributes, childrenProperty)), false)
            : TransformJsxAttributesToExpression(attributes, childrenProperty);
    }

    private Node TransformJsxAttributesToExpression(Node[] attributes, Node? childrenProperty)
    {
        List<Node> expressions = new(2);
        List<Node> properties = new(attributes.Length);
        foreach (var attribute in attributes)
        {
            if (global::Ts.Ast.NodeTests.IsJsxSpreadAttribute(attribute))
            {
                if (global::Ts.Ast.NodeTests.IsObjectLiteralExpression(attribute.Expression()!) && !HasProto(attribute.Expression()!))
                {
                    foreach (var property in attribute.Expression()!.Properties() ?? [])
                    {
                        if (global::Ts.Ast.NodeTests.IsSpreadAssignment(property))
                        {
                            CombinePropertiesIntoNewExpression(expressions, properties);
                            expressions.Add(Visitor().Visit!(property.Expression()!)!);
                            continue;
                        }
                        properties.Add(Visitor().Visit!(property)!);
                    }
                    continue;
                }
                CombinePropertiesIntoNewExpression(expressions, properties);
                expressions.Add(Visitor().Visit!(attribute.Expression()!)!);
                continue;
            }
            properties.Add(TransformJsxAttributeToObjectLiteralElement(attribute.AsJsxAttribute()));
        }
        if (childrenProperty is not null)
        {
            properties.Add(childrenProperty);
        }
        CombinePropertiesIntoNewExpression(expressions, properties);
        if (expressions.Count > 0 && !global::Ts.Ast.NodeTests.IsObjectLiteralExpression(expressions[0]))
        {
            expressions.Insert(0, Factory().NewObjectLiteralExpression(Factory().NewNodeList([]), false));
        }
        return expressions.Count == 1
            ? expressions[0]
            : Factory().NewAssignHelper(expressions.ToArray(), compilerOptions.GetEmitScriptTarget());
    }

    private void CombinePropertiesIntoNewExpression(List<Node> expressions, List<Node> properties)
    {
        if (properties.Count == 0)
        {
            return;
        }
        expressions.Add(Factory().NewObjectLiteralExpression(Factory().NewNodeList(properties.ToArray()), false));
        properties.Clear();
    }

    private Node[] TransformJsxAttributesToProps(Node[] attributes, Node? childrenProperty)
    {
        List<Node> properties = new(attributes.Length + (childrenProperty is null ? 0 : 1));
        foreach (var attribute in attributes)
        {
            if (attribute.Kind == SyntaxKind.JsxSpreadAttribute)
            {
                properties.AddRange(TransformJsxSpreadAttributesToProps(attribute.AsJsxSpreadAttribute()));
            }
            else
            {
                properties.Add(TransformJsxAttributeToObjectLiteralElement(attribute.AsJsxAttribute()));
            }
        }
        if (childrenProperty is not null)
        {
            properties.Add(childrenProperty);
        }
        return properties.ToArray();
    }

    private static bool HasProto(Node objectLiteral)
    {
        foreach (var property in objectLiteral.Properties() ?? [])
        {
            if (global::Ts.Ast.NodeTests.IsPropertyAssignment(property) &&
                (global::Ts.Ast.NodeTests.IsStringLiteral(property.Name()!) || global::Ts.Ast.NodeTests.IsIdentifier(property.Name()!)) &&
                string.Equals(property.Name()!.Text(), "__proto__", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private Node[] TransformJsxSpreadAttributesToProps(JsxSpreadAttribute node)
    {
        if (global::Ts.Ast.NodeTests.IsObjectLiteralExpression(node.Expression!) && !HasProto(node.Expression!))
        {
            var (result, _) = Visitor().VisitSlice(node.Expression!.Properties());
            return result ?? [];
        }
        return [Factory().NewSpreadAssignment(Visitor().Visit!(node.Expression!)!)];
    }

    private Node TransformJsxAttributeToObjectLiteralElement(JsxAttribute node) =>
        Factory().NewPropertyAssignment(null, GetAttributeName(node), null, null, TransformJsxAttributeInitializer(node.Initializer));

    /**
     * Emit an attribute name, which is quoted if it needs to be quoted. Because
     * these emit into an object literal property name, we don't need to be worried
     * about keywords, just non-identifier characters.
     */
    private Node GetAttributeName(JsxAttribute node)
    {
        var name = node.Name()!;
        if (global::Ts.Ast.NodeTests.IsIdentifier(name))
        {
            var text = name.Text();
            return global::Ts.Scanner.Scanner.IsIdentifierText(text, LanguageVariant.Standard)
                ? name
                : Factory().NewStringLiteral(text, TokenFlags.None);
        }
        return Factory().NewStringLiteral(
            name.AsJsxNamespacedName().Namespace!.Text() + ":" + name.AsJsxNamespacedName().Name()!.Text(),
            TokenFlags.None);
    }

    private Node TransformJsxAttributeInitializer(Node? node)
    {
        if (node is null)
        {
            return Factory().NewTrueExpression();
        }
        if (node.Kind == SyntaxKind.StringLiteral)
        {
            var result = Factory().NewStringLiteral(DecodeEntities(node.Text()), node.AsStringLiteral().TokenFlags);
            result.Loc = node.Loc;
            result.AsStringLiteral().TokenFlags = node.AsStringLiteral().TokenFlags;
            return result;
        }
        if (node.Kind == SyntaxKind.JsxExpression)
        {
            return node.Expression() is null ? Factory().NewTrueExpression() : Visitor().Visit!(node.Expression()!)!;
        }
        if (global::Ts.Ast.NodeTests.IsJsxElement(node) || global::Ts.Ast.NodeTests.IsJsxSelfClosingElement(node) ||
            global::Ts.Ast.NodeTests.IsJsxFragment(node))
        {
            SetInChild(false);
            return Visitor().Visit!(node)!;
        }
        throw new InvalidOperationException("Unhandled node kind found in jsx initializer: " + node.Kind.ToDisplayString());
    }

    private Node VisitJsxOpeningLikeElementOrFragmentJSX(
        Expression tagName,
        Expression @object,
        Node? keyAttribute,
        NodeList? children,
        TextRange location)
    {
        var nonWhitespaceChildren = TransformerAstAdapters.GetSemanticJsxChildren(children?.Nodes);
        var isStaticChildren = nonWhitespaceChildren.Length > 1 ||
            (nonWhitespaceChildren.Length == 1 && global::Ts.Ast.NodeTests.IsJsxExpression(nonWhitespaceChildren[0]) &&
            nonWhitespaceChildren[0].AsJsxExpression().DotDotDotToken is not null);
        List<Node> arguments = new(7) { tagName, @object };
        if (keyAttribute is not null)
        {
            arguments.Add(TransformJsxAttributeInitializer(keyAttribute.Initializer()));
        }

        if (compilerOptions.Jsx == JsxEmit.ReactJSXDev)
        {
            var originalFile = EmitContext().MostOriginal(currentSourceFile!.AsNode());
            if (originalFile is not null && global::Ts.Ast.NodeTests.IsSourceFile(originalFile))
            {
                if (keyAttribute is null)
                {
                    arguments.Add(Factory().NewVoidZeroExpression());
                }
                arguments.Add(isStaticChildren ? Factory().NewTrueExpression() : Factory().NewFalseExpression());
                var (line, column) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(originalFile.AsSourceFile(), location.Pos);
                arguments.Add(Factory().NewObjectLiteralExpression(Factory().NewNodeList([
                    Factory().NewPropertyAssignment(null, Factory().NewIdentifier("fileName"), null, null, GetCurrentFileNameExpression()),
                    Factory().NewPropertyAssignment(null, Factory().NewIdentifier("lineNumber"), null, null,
                        Factory().NewNumericLiteral((line + 1).ToString(CultureInfo.InvariantCulture), TokenFlags.None)),
                    Factory().NewPropertyAssignment(null, Factory().NewIdentifier("columnNumber"), null, null,
                        Factory().NewNumericLiteral((column + 1).ToString(CultureInfo.InvariantCulture), TokenFlags.None)),
                ]), false));
                arguments.Add(Factory().NewThisExpression());
            }
        }

        var element = Factory().NewCallExpression(
            GetJsxFactoryCallee(isStaticChildren),
            null,
            null,
            Factory().NewNodeList(arguments.ToArray()),
            NodeFlags.None);
        element.Loc = location;
        if (inJsxChild)
        {
            EmitContext().AddEmitFlags(element, global::Ts.Printer.EmitFlags.StartOnNewLine);
        }
        return element;
    }

    private Node VisitJsxOpeningFragmentJSX(JsxOpeningFragment fragment, NodeList? children, TextRange location)
    {
        Expression? childrenProperties = children is { Nodes.Length: > 0 }
            ? ConvertJsxChildrenToChildrenPropObject(children.Nodes)
            : null;
        childrenProperties ??= Factory().NewObjectLiteralExpression(Factory().NewNodeList([]), false);
        return VisitJsxOpeningLikeElementOrFragmentJSX(
            GetImplicitJsxFragmentReference(), childrenProperties, null, children, location);
    }

    private Node CreateReactNamespace(string reactNamespace, Node parent)
    {
        if (reactNamespace.Length == 0)
        {
            reactNamespace = "React";
        }
        var react = Factory().NewIdentifier(reactNamespace);
        react.Flags &= ~NodeFlags.Synthesized;
        react.Parent = EmitContext().ParseNode(parent);
        var container = emitResolver.GetReferencedExportContainer(react, false);
        if (container is not null && global::Ts.Ast.NodeTests.IsModuleDeclaration(container))
        {
            return Factory().NewPropertyAccessExpression(Factory().NewGeneratedNameForNode(container), null, react, NodeFlags.None);
        }
        return react;
    }

    private Node CreateJsxFactoryExpressionFromEntityName(Node entityName, Node parent)
    {
        if (global::Ts.Ast.NodeTests.IsQualifiedName(entityName))
        {
            var left = CreateJsxFactoryExpressionFromEntityName(entityName.AsQualifiedName().Left!, parent);
            var right = Factory().NewIdentifier(entityName.AsQualifiedName().Right!.Text());
            return Factory().NewPropertyAccessExpression(left, null, right, NodeFlags.None);
        }
        return CreateReactNamespace(entityName.Text(), parent);
    }

    private Node CreateJsxPseudoFactoryExpression(Node parent, Node? entityName, string target)
    {
        return entityName is not null
            ? CreateJsxFactoryExpressionFromEntityName(entityName, parent)
            : Factory().NewPropertyAccessExpression(
                CreateReactNamespace(compilerOptions.ReactNamespace, parent),
                null,
                Factory().NewIdentifier(target),
                NodeFlags.None);
    }

    private Node CreateJsxFactoryExpression(Node parent) =>
        CreateJsxPseudoFactoryExpression(parent, emitResolver.GetJsxFactoryEntity(currentSourceFile!.AsNode()), "createElement");

    private Node CreateJsxFragmentFactoryExpression(Node parent) =>
        CreateJsxPseudoFactoryExpression(parent, emitResolver.GetJsxFragmentFactoryEntity(currentSourceFile!.AsNode()), "Fragment");

    private Node VisitJsxOpeningLikeElementCreateElement(Node element, NodeList? children, TextRange location)
    {
        var tagName = GetTagName(element);
        var attributes = element.Attributes()!.Properties() ?? [];
        var objectProperties = attributes.Length > 0
            ? TransformJsxAttributesToObjectProps(attributes, null)
            : Factory().NewKeywordExpression(SyntaxKind.NullKeyword);
        var callee = importSpecifier.Length == 0 ? CreateJsxFactoryExpression(element) : GetImplicitImportForName("createElement");
        List<Node> newChildren = [];
        foreach (var child in children?.Nodes ?? [])
        {
            if (TransformJsxChildToExpression(child) is { } result)
            {
                newChildren.Add(result);
            }
        }
        if (newChildren.Count > 1)
        {
            foreach (var child in newChildren)
            {
                EmitContext().AddEmitFlags(child, global::Ts.Printer.EmitFlags.StartOnNewLine);
            }
        }
        List<Node> arguments = new(newChildren.Count + 2) { tagName, objectProperties };
        arguments.AddRange(newChildren);
        var resultNode = Factory().NewCallExpression(callee, null, null, Factory().NewNodeList(arguments.ToArray()), NodeFlags.None);
        resultNode.Loc = location;
        if (inJsxChild)
        {
            EmitContext().AddEmitFlags(resultNode, global::Ts.Printer.EmitFlags.StartOnNewLine);
        }
        return resultNode;
    }

    private Node VisitJsxOpeningFragmentCreateElement(JsxOpeningFragment fragment, NodeList? children, TextRange location)
    {
        var tagName = CreateJsxFragmentFactoryExpression(fragment.AsNode());
        var callee = CreateJsxFactoryExpression(fragment.AsNode());
        List<Node> newChildren = [];
        foreach (var child in children?.Nodes ?? [])
        {
            if (TransformJsxChildToExpression(child) is { } result)
            {
                newChildren.Add(result);
            }
        }
        if (newChildren.Count > 1)
        {
            foreach (var child in newChildren)
            {
                EmitContext().AddEmitFlags(child, global::Ts.Printer.EmitFlags.StartOnNewLine);
            }
        }
        List<Node> arguments = new(newChildren.Count + 2) { tagName, Factory().NewKeywordExpression(SyntaxKind.NullKeyword) };
        arguments.AddRange(newChildren);
        var resultNode = Factory().NewCallExpression(callee, null, null, Factory().NewNodeList(arguments.ToArray()), NodeFlags.None);
        resultNode.Loc = location;
        if (inJsxChild)
        {
            EmitContext().AddEmitFlags(resultNode, global::Ts.Printer.EmitFlags.StartOnNewLine);
        }
        return resultNode;
    }

    private Node? VisitJsxText(JsxText text)
    {
        var fixedText = FixupWhitespaceAndDecodeEntities(text.Text);
        return fixedText.Length == 0 ? null : Factory().NewStringLiteral(fixedText, TokenFlags.None);
    }

    private static void AddLineOfJsxText(StringBuilder builder, string trimmedLine, bool isInitial)
    {
        var decoded = DecodeEntities(trimmedLine);
        if (!isInitial)
        {
            builder.Append(' ');
        }
        builder.Append(decoded);
    }

    /**
     * JSX trims whitespace at the end and beginning of lines, except that the
     * start/end of a tag is considered a start/end of a line only if that line is
     * on the same line as the closing tag.
     */
    internal static string FixupWhitespaceAndDecodeEntities(string text)
    {
        StringBuilder accumulator = new();
        var initial = true;
        var firstNonWhitespace = 0;
        var lastNonWhitespaceEnd = -1;
        // C# strings are already UTF-16. All JSX whitespace and line-break code points are in the BMP,
        // so iterating code units preserves the required position/slicing semantics, including lone surrogates.
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (global::Ts.StringUtil.StringUtil.IsLineBreak(character))
            {
                if (firstNonWhitespace != -1 && lastNonWhitespaceEnd != -1)
                {
                    AddLineOfJsxText(accumulator, text[firstNonWhitespace..(lastNonWhitespaceEnd + 1)], initial);
                    initial = false;
                }
                firstNonWhitespace = -1;
            }
            else if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(character))
            {
                lastNonWhitespaceEnd = i;
                if (firstNonWhitespace == -1)
                {
                    firstNonWhitespace = i;
                }
            }
        }
        if (firstNonWhitespace != -1)
        {
            AddLineOfJsxText(accumulator, text[firstNonWhitespace..], initial);
        }
        return accumulator.ToString();
    }

    private Node? VisitJsxExpression(JsxExpression expression)
    {
        var result = Visitor().Visit!(expression.Expression!);
        return expression.DotDotDotToken is not null ? Factory().NewSpreadElement(result) : result;
    }

    /** Replace entities like "&nbsp;", "&#123;", and "&#xDEADBEEF;" with the characters they encode. */
    internal static string DecodeEntities(string text)
    {
        var index = text.IndexOf('&');
        if (index < 0)
        {
            return text;
        }
        StringBuilder result = new(text.Length);
        while (true)
        {
            result.Append(text.AsSpan(0, index));
            text = text[index..];
            var semicolon = text.IndexOf(';');
            if (semicolon < 0)
            {
                break;
            }
            while (true)
            {
                var nextAmpersand = text.AsSpan(1, semicolon - 1).IndexOf('&');
                if (nextAmpersand < 0)
                {
                    break;
                }
                result.Append(text.AsSpan(0, nextAmpersand + 1));
                text = text[(nextAmpersand + 1)..];
                semicolon -= nextAmpersand + 1;
            }
            var entity = text[1..semicolon];
            if (DecodeEntity(entity, out var decoded))
            {
                result.Append(global::Ts.StringUtil.StringUtil.EncodeJSStringRune(decoded));
            }
            else
            {
                result.Append(text.AsSpan(0, semicolon + 1));
            }
            text = text[(semicolon + 1)..];
            index = text.IndexOf('&');
            if (index < 0)
            {
                break;
            }
        }
        result.Append(text);
        return result.ToString();
    }

    private static bool DecodeEntity(string entity, out int result)
    {
        result = 0;
        if (entity.Length == 0)
        {
            return false;
        }
        if (entity[0] == '#')
        {
            entity = entity[1..];
            if (entity.Length == 0)
            {
                return false;
            }
            var numberBase = 10;
            if (entity[0] == 'x')
            {
                numberBase = 16;
                entity = entity[1..];
            }
            if (entity.Length == 0)
            {
                return false;
            }
            foreach (var character in entity)
            {
                if ((numberBase == 16 && !global::Ts.StringUtil.StringUtil.IsHexDigit(character)) ||
                    (numberBase == 10 && !global::Ts.StringUtil.StringUtil.IsDigit(character)))
                {
                    return false;
                }
            }
            return int.TryParse(
                entity,
                numberBase == 16 ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result);
        }
        return Entities.TryGetValue(entity, out result);
    }

    private static readonly Dictionary<string, int> Entities = new(StringComparer.Ordinal)
    {
        ["quot"] = 0x0022,
        ["amp"] = 0x0026,
        ["apos"] = 0x0027,
        ["lt"] = 0x003C,
        ["gt"] = 0x003E,
        ["nbsp"] = 0x00A0,
        ["iexcl"] = 0x00A1,
        ["cent"] = 0x00A2,
        ["pound"] = 0x00A3,
        ["curren"] = 0x00A4,
        ["yen"] = 0x00A5,
        ["brvbar"] = 0x00A6,
        ["sect"] = 0x00A7,
        ["uml"] = 0x00A8,
        ["copy"] = 0x00A9,
        ["ordf"] = 0x00AA,
        ["laquo"] = 0x00AB,
        ["not"] = 0x00AC,
        ["shy"] = 0x00AD,
        ["reg"] = 0x00AE,
        ["macr"] = 0x00AF,
        ["deg"] = 0x00B0,
        ["plusmn"] = 0x00B1,
        ["sup2"] = 0x00B2,
        ["sup3"] = 0x00B3,
        ["acute"] = 0x00B4,
        ["micro"] = 0x00B5,
        ["para"] = 0x00B6,
        ["middot"] = 0x00B7,
        ["cedil"] = 0x00B8,
        ["sup1"] = 0x00B9,
        ["ordm"] = 0x00BA,
        ["raquo"] = 0x00BB,
        ["frac14"] = 0x00BC,
        ["frac12"] = 0x00BD,
        ["frac34"] = 0x00BE,
        ["iquest"] = 0x00BF,
        ["Agrave"] = 0x00C0,
        ["Aacute"] = 0x00C1,
        ["Acirc"] = 0x00C2,
        ["Atilde"] = 0x00C3,
        ["Auml"] = 0x00C4,
        ["Aring"] = 0x00C5,
        ["AElig"] = 0x00C6,
        ["Ccedil"] = 0x00C7,
        ["Egrave"] = 0x00C8,
        ["Eacute"] = 0x00C9,
        ["Ecirc"] = 0x00CA,
        ["Euml"] = 0x00CB,
        ["Igrave"] = 0x00CC,
        ["Iacute"] = 0x00CD,
        ["Icirc"] = 0x00CE,
        ["Iuml"] = 0x00CF,
        ["ETH"] = 0x00D0,
        ["Ntilde"] = 0x00D1,
        ["Ograve"] = 0x00D2,
        ["Oacute"] = 0x00D3,
        ["Ocirc"] = 0x00D4,
        ["Otilde"] = 0x00D5,
        ["Ouml"] = 0x00D6,
        ["times"] = 0x00D7,
        ["Oslash"] = 0x00D8,
        ["Ugrave"] = 0x00D9,
        ["Uacute"] = 0x00DA,
        ["Ucirc"] = 0x00DB,
        ["Uuml"] = 0x00DC,
        ["Yacute"] = 0x00DD,
        ["THORN"] = 0x00DE,
        ["szlig"] = 0x00DF,
        ["agrave"] = 0x00E0,
        ["aacute"] = 0x00E1,
        ["acirc"] = 0x00E2,
        ["atilde"] = 0x00E3,
        ["auml"] = 0x00E4,
        ["aring"] = 0x00E5,
        ["aelig"] = 0x00E6,
        ["ccedil"] = 0x00E7,
        ["egrave"] = 0x00E8,
        ["eacute"] = 0x00E9,
        ["ecirc"] = 0x00EA,
        ["euml"] = 0x00EB,
        ["igrave"] = 0x00EC,
        ["iacute"] = 0x00ED,
        ["icirc"] = 0x00EE,
        ["iuml"] = 0x00EF,
        ["eth"] = 0x00F0,
        ["ntilde"] = 0x00F1,
        ["ograve"] = 0x00F2,
        ["oacute"] = 0x00F3,
        ["ocirc"] = 0x00F4,
        ["otilde"] = 0x00F5,
        ["ouml"] = 0x00F6,
        ["divide"] = 0x00F7,
        ["oslash"] = 0x00F8,
        ["ugrave"] = 0x00F9,
        ["uacute"] = 0x00FA,
        ["ucirc"] = 0x00FB,
        ["uuml"] = 0x00FC,
        ["yacute"] = 0x00FD,
        ["thorn"] = 0x00FE,
        ["yuml"] = 0x00FF,
        ["OElig"] = 0x0152,
        ["oelig"] = 0x0153,
        ["Scaron"] = 0x0160,
        ["scaron"] = 0x0161,
        ["Yuml"] = 0x0178,
        ["fnof"] = 0x0192,
        ["circ"] = 0x02C6,
        ["tilde"] = 0x02DC,
        ["Alpha"] = 0x0391,
        ["Beta"] = 0x0392,
        ["Gamma"] = 0x0393,
        ["Delta"] = 0x0394,
        ["Epsilon"] = 0x0395,
        ["Zeta"] = 0x0396,
        ["Eta"] = 0x0397,
        ["Theta"] = 0x0398,
        ["Iota"] = 0x0399,
        ["Kappa"] = 0x039A,
        ["Lambda"] = 0x039B,
        ["Mu"] = 0x039C,
        ["Nu"] = 0x039D,
        ["Xi"] = 0x039E,
        ["Omicron"] = 0x039F,
        ["Pi"] = 0x03A0,
        ["Rho"] = 0x03A1,
        ["Sigma"] = 0x03A3,
        ["Tau"] = 0x03A4,
        ["Upsilon"] = 0x03A5,
        ["Phi"] = 0x03A6,
        ["Chi"] = 0x03A7,
        ["Psi"] = 0x03A8,
        ["Omega"] = 0x03A9,
        ["alpha"] = 0x03B1,
        ["beta"] = 0x03B2,
        ["gamma"] = 0x03B3,
        ["delta"] = 0x03B4,
        ["epsilon"] = 0x03B5,
        ["zeta"] = 0x03B6,
        ["eta"] = 0x03B7,
        ["theta"] = 0x03B8,
        ["iota"] = 0x03B9,
        ["kappa"] = 0x03BA,
        ["lambda"] = 0x03BB,
        ["mu"] = 0x03BC,
        ["nu"] = 0x03BD,
        ["xi"] = 0x03BE,
        ["omicron"] = 0x03BF,
        ["pi"] = 0x03C0,
        ["rho"] = 0x03C1,
        ["sigmaf"] = 0x03C2,
        ["sigma"] = 0x03C3,
        ["tau"] = 0x03C4,
        ["upsilon"] = 0x03C5,
        ["phi"] = 0x03C6,
        ["chi"] = 0x03C7,
        ["psi"] = 0x03C8,
        ["omega"] = 0x03C9,
        ["thetasym"] = 0x03D1,
        ["upsih"] = 0x03D2,
        ["piv"] = 0x03D6,
        ["ensp"] = 0x2002,
        ["emsp"] = 0x2003,
        ["thinsp"] = 0x2009,
        ["zwnj"] = 0x200C,
        ["zwj"] = 0x200D,
        ["lrm"] = 0x200E,
        ["rlm"] = 0x200F,
        ["ndash"] = 0x2013,
        ["mdash"] = 0x2014,
        ["lsquo"] = 0x2018,
        ["rsquo"] = 0x2019,
        ["sbquo"] = 0x201A,
        ["ldquo"] = 0x201C,
        ["rdquo"] = 0x201D,
        ["bdquo"] = 0x201E,
        ["dagger"] = 0x2020,
        ["Dagger"] = 0x2021,
        ["bull"] = 0x2022,
        ["hellip"] = 0x2026,
        ["permil"] = 0x2030,
        ["prime"] = 0x2032,
        ["Prime"] = 0x2033,
        ["lsaquo"] = 0x2039,
        ["rsaquo"] = 0x203A,
        ["oline"] = 0x203E,
        ["frasl"] = 0x2044,
        ["euro"] = 0x20AC,
        ["image"] = 0x2111,
        ["weierp"] = 0x2118,
        ["real"] = 0x211C,
        ["trade"] = 0x2122,
        ["alefsym"] = 0x2135,
        ["larr"] = 0x2190,
        ["uarr"] = 0x2191,
        ["rarr"] = 0x2192,
        ["darr"] = 0x2193,
        ["harr"] = 0x2194,
        ["crarr"] = 0x21B5,
        ["lArr"] = 0x21D0,
        ["uArr"] = 0x21D1,
        ["rArr"] = 0x21D2,
        ["dArr"] = 0x21D3,
        ["hArr"] = 0x21D4,
        ["forall"] = 0x2200,
        ["part"] = 0x2202,
        ["exist"] = 0x2203,
        ["empty"] = 0x2205,
        ["nabla"] = 0x2207,
        ["isin"] = 0x2208,
        ["notin"] = 0x2209,
        ["ni"] = 0x220B,
        ["prod"] = 0x220F,
        ["sum"] = 0x2211,
        ["minus"] = 0x2212,
        ["lowast"] = 0x2217,
        ["radic"] = 0x221A,
        ["prop"] = 0x221D,
        ["infin"] = 0x221E,
        ["ang"] = 0x2220,
        ["and"] = 0x2227,
        ["or"] = 0x2228,
        ["cap"] = 0x2229,
        ["cup"] = 0x222A,
        ["int"] = 0x222B,
        ["there4"] = 0x2234,
        ["sim"] = 0x223C,
        ["cong"] = 0x2245,
        ["asymp"] = 0x2248,
        ["ne"] = 0x2260,
        ["equiv"] = 0x2261,
        ["le"] = 0x2264,
        ["ge"] = 0x2265,
        ["sub"] = 0x2282,
        ["sup"] = 0x2283,
        ["nsub"] = 0x2284,
        ["sube"] = 0x2286,
        ["supe"] = 0x2287,
        ["oplus"] = 0x2295,
        ["otimes"] = 0x2297,
        ["perp"] = 0x22A5,
        ["sdot"] = 0x22C5,
        ["lceil"] = 0x2308,
        ["rceil"] = 0x2309,
        ["lfloor"] = 0x230A,
        ["rfloor"] = 0x230B,
        ["lang"] = 0x2329,
        ["rang"] = 0x232A,
        ["loz"] = 0x25CA,
        ["spades"] = 0x2660,
        ["clubs"] = 0x2663,
        ["hearts"] = 0x2665,
        ["diams"] = 0x2666,
    };
}
