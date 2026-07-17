// Ported from tsgo internal/transformers/tstransforms/metadata.go — the emitDecoratorMetadata
// transform: injects synthetic `__metadata("design:type", ...)` decorators ahead of the legacy
// decorator lowering.

using System.Linq;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

public sealed class MetadataTransformer : global::Ts.Transformers.Transformer
{
    // metadata.go:10
    private const bool USE_NEW_TYPE_METADATA_FORMAT = false;

    private readonly bool legacyDecorators;
    private readonly global::Ts.Printer.EmitResolver? resolver;

    private MetadataSerializer? serializer;
    private readonly ScriptTarget languageVersion;
    private readonly bool strictNullChecks;
    private Node? parent;
    private Node? currentLexicalScope;

    // metadata.go:24-32
    private MetadataTransformer(global::Ts.Transformers.TransformOptions options)
    {
        var compilerOptions = options.CompilerOptions ?? new CompilerOptions();
        legacyDecorators = compilerOptions.ExperimentalDecorators.IsTrue();
        resolver = options.EmitResolver;
        languageVersion = compilerOptions.GetEmitScriptTarget();
        strictNullChecks = compilerOptions.GetStrictOptionValue(compilerOptions.StrictNullChecks);
        NewTransformer(Visit, options.Context);
    }

    public static global::Ts.Transformers.Transformer NewMetadataTransformer(
        global::Ts.Transformers.TransformOptions options) => new MetadataTransformer(options);

    // metadata.go:34-69
    private Node? Visit(Node node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsDecorators) == 0)
        {
            return node;
        }

        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                return VisitClassDeclaration(node.AsClassDeclaration());
            case SyntaxKind.ClassExpression:
                return VisitClassExpression(node.AsClassExpression());
            case SyntaxKind.PropertyDeclaration:
                return VisitPropertyDeclaration(node.AsPropertyDeclaration());
            case SyntaxKind.MethodDeclaration:
                return VisitMethodDeclaration(node.AsMethodDeclaration());
            case SyntaxKind.SetAccessor:
                return VisitSetAccessor(node.AsSetAccessorDeclaration());
            case SyntaxKind.GetAccessor:
                return VisitGetAccessor(node.AsGetAccessorDeclaration());
            case SyntaxKind.SourceFile:
            {
                parent = null;
                currentLexicalScope = node;
                try
                {
                    serializer = new MetadataSerializer(resolver!, Factory(), EmitContext(), languageVersion, strictNullChecks);
                    var updated = Visitor().VisitEachChild(node)!;
                    EmitContext().AddEmitHelper(updated, EmitContext().ReadEmitHelpers());
                    return updated;
                }
                finally
                {
                    parent = null;
                    currentLexicalScope = null;
                }
            }
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.Block:
            case SyntaxKind.CaseBlock:
            {
                var oldScope = currentLexicalScope;
                currentLexicalScope = node;
                try
                {
                    return Visitor().VisitEachChild(node);
                }
                finally
                {
                    currentLexicalScope = oldScope;
                }
            }
            default:
                return Visitor().VisitEachChild(node);
        }
    }

    // metadata.go:79-96
    private Node? VisitClassExpression(ClassExpression node)
    {
        var oldParent = parent;
        parent = node.AsNode();
        try
        {
            if (!AstUtilities.ClassOrConstructorParameterIsDecorated(legacyDecorators, node.AsNode()))
            {
                return Visitor().VisitEachChild(node.AsNode());
            }
            var modifiers = InjectClassTypeMetadata(Visitor().VisitModifiers(node.Modifiers()), node.AsNode());
            return Factory().AsNodeFactory().UpdateClassExpression(
                node,
                modifiers,
                Visitor().VisitNode(node.Name()),
                Visitor().VisitNodes(node.TypeParameters),
                Visitor().VisitNodes(node.HeritageClauses),
                Visitor().VisitNodes(node.Members));
        }
        finally
        {
            parent = oldParent;
        }
    }

    // metadata.go:98-115
    private Node? VisitClassDeclaration(ClassDeclaration node)
    {
        var oldParent = parent;
        parent = node.AsNode();
        try
        {
            if (!AstUtilities.ClassOrConstructorParameterIsDecorated(legacyDecorators, node.AsNode()))
            {
                return Visitor().VisitEachChild(node.AsNode());
            }
            var modifiers = InjectClassTypeMetadata(Visitor().VisitModifiers(node.Modifiers()), node.AsNode());
            return Factory().AsNodeFactory().UpdateClassDeclaration(
                node,
                modifiers,
                Visitor().VisitNode(node.Name()),
                Visitor().VisitNodes(node.TypeParameters),
                Visitor().VisitNodes(node.HeritageClauses),
                Visitor().VisitNodes(node.Members));
        }
        finally
        {
            parent = oldParent;
        }
    }

    // metadata.go:117-131
    private Node? VisitPropertyDeclaration(PropertyDeclaration node)
    {
        if (!AstUtilities.HasDecorators(node.AsNode()))
        {
            return Visitor().VisitEachChild(node.AsNode());
        }

        var modifiers = InjectClassElementTypeMetadata(Visitor().VisitModifiers(node.Modifiers()), node.AsNode(), parent);
        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
            node,
            modifiers,
            Visitor().VisitNode(node.Name()),
            Visitor().VisitNode(node.PostfixToken),
            Visitor().VisitNode(node.Type),
            Visitor().VisitNode(node.Initializer));
    }

    // metadata.go:133-151
    private Node? VisitMethodDeclaration(MethodDeclaration node)
    {
        if (!AstUtilities.HasDecorators(node.AsNode()) && !HasDecoratedParameters(node.AsNode()))
        {
            return Visitor().VisitEachChild(node.AsNode());
        }

        var modifiers = InjectClassElementTypeMetadata(Visitor().VisitModifiers(node.Modifiers()), node.AsNode(), parent);
        return Factory().AsNodeFactory().UpdateMethodDeclaration(
            node,
            modifiers,
            Visitor().VisitNode(node.AsteriskToken),
            Visitor().VisitNode(node.Name()),
            Visitor().VisitNode(node.PostfixToken),
            Visitor().VisitNodes(node.TypeParameters),
            Visitor().VisitNodes(node.Parameters),
            Visitor().VisitNode(node.Type),
            Visitor().VisitNode(node.FullSignature),
            Visitor().VisitNode(node.Body));
    }

    // metadata.go:153-169
    private Node? VisitSetAccessor(SetAccessorDeclaration node)
    {
        if (!AstUtilities.HasDecorators(node.AsNode()) && !HasDecoratedParameters(node.AsNode()))
        {
            return Visitor().VisitEachChild(node.AsNode());
        }

        var modifiers = InjectClassElementTypeMetadata(Visitor().VisitModifiers(node.Modifiers()), node.AsNode(), parent);
        return Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
            node,
            modifiers,
            Visitor().VisitNode(node.Name()),
            Visitor().VisitNodes(node.TypeParameters),
            Visitor().VisitNodes(node.Parameters),
            Visitor().VisitNode(node.Type),
            Visitor().VisitNode(node.FullSignature),
            Visitor().VisitNode(node.Body));
    }

    // metadata.go:171-187
    private Node? VisitGetAccessor(GetAccessorDeclaration node)
    {
        if (!AstUtilities.HasDecorators(node.AsNode()))
        {
            return Visitor().VisitEachChild(node.AsNode());
        }

        var modifiers = InjectClassElementTypeMetadata(Visitor().VisitModifiers(node.Modifiers()), node.AsNode(), parent);
        return Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
            node,
            modifiers,
            Visitor().VisitNode(node.Name()),
            Visitor().VisitNodes(node.TypeParameters),
            Visitor().VisitNodes(node.Parameters),
            Visitor().VisitNode(node.Type),
            Visitor().VisitNode(node.FullSignature),
            Visitor().VisitNode(node.Body));
    }

    // metadata.go:134,154 — len(getDecoratorsOfParameters(node)) > 0 probe (parameter decorator presence).
    private static bool HasDecoratedParameters(Node node)
    {
        foreach (var p in node.Parameters())
        {
            if (AstUtilities.HasDecorators(p))
            {
                return true;
            }
        }
        return false;
    }

    // metadata.go:189-221
    private ModifierList? InjectClassTypeMetadata(ModifierList? list, Node node)
    {
        var metadata = GetTypeMetadata(node, node);
        if (metadata.Count > 0)
        {
            Node[] originalNodes = list != null ? list.Nodes : [];
            if (originalNodes.Length == 0)
            {
                var newRes = Factory().AsNodeFactory().NewModifierList(metadata.ToArray());
                if (list != null)
                {
                    newRes.Loc = list.Loc;
                }
                return newRes;
            }
            var modifiersArray = new System.Collections.Generic.List<Node>();
            if (AstUtilities.IsModifier(originalNodes[0]) && (originalNodes[0].Kind == SyntaxKind.DefaultKeyword || originalNodes[0].Kind == SyntaxKind.ExportKeyword))
            {
                modifiersArray.Add(originalNodes[0]);
                if (originalNodes.Length > 1 && (originalNodes[1].Kind == SyntaxKind.DefaultKeyword || originalNodes[1].Kind == SyntaxKind.ExportKeyword))
                {
                    modifiersArray.Add(originalNodes[1]);
                }
            }
            var restStart = modifiersArray.Count;
            modifiersArray.AddRange(originalNodes.Where(NodeTests.IsDecorator));
            modifiersArray.AddRange(metadata);
            modifiersArray.AddRange(originalNodes.Skip(restStart).Where(AstUtilities.IsModifier));
            var res = Factory().AsNodeFactory().NewModifierList(modifiersArray.ToArray());
            res.Loc = list!.Loc;
            return res;
        }
        return list;
    }

    // metadata.go:223-254
    private ModifierList? InjectClassElementTypeMetadata(ModifierList? list, Node node, Node? container)
    {
        if (container == null || !AstUtilities.IsClassLike(container))
        {
            return list;
        }
        if (!AstUtilities.ClassElementOrClassElementParameterIsDecorated(legacyDecorators, node, container))
        {
            return list;
        }
        var metadata = GetTypeMetadata(node, container);
        if (metadata.Count > 0)
        {
            Node[] originalNodes = list != null ? list.Nodes : [];
            if (originalNodes.Length == 0)
            {
                var newRes = Factory().AsNodeFactory().NewModifierList(metadata.ToArray());
                if (list != null)
                {
                    newRes.Loc = list.Loc;
                }
                return newRes;
            }
            var modifiersArray = new System.Collections.Generic.List<Node>();
            modifiersArray.AddRange(originalNodes.Where(NodeTests.IsDecorator));
            modifiersArray.AddRange(metadata);
            modifiersArray.AddRange(originalNodes.Where(AstUtilities.IsModifier));
            var res = Factory().AsNodeFactory().NewModifierList(modifiersArray.ToArray());
            res.Loc = list!.Loc;
            return res;
        }
        return list;
    }

    /**
     * Gets optional type metadata for a declaration.
     *
     * @param node The declaration node.
     */
    // metadata.go:261-270
    private System.Collections.Generic.List<Node> GetTypeMetadata(Node node, Node container)
    {
        // Decorator metadata is not yet supported for ES decorators.
        if (!legacyDecorators)
        {
            return new System.Collections.Generic.List<Node>();
        }
#pragma warning disable CS0162 // USE_NEW_TYPE_METADATA_FORMAT mirrors Go's compile-time const switch.
        if (USE_NEW_TYPE_METADATA_FORMAT)
        {
            return GetNewTypeMetadata(node, container);
        }
        return GetOldTypeMetadata(node, container);
#pragma warning restore CS0162
    }

    // metadata.go:272-287
    private System.Collections.Generic.List<Node> GetOldTypeMetadata(Node node, Node container)
    {
        var decorators = new System.Collections.Generic.List<Node>();
        if (ShouldAddTypeMetadata(node))
        {
            var typeMetadata = Factory().NewMetadataHelper("design:type", serializer!.SerializeTypeOfNode(new MetadataSerializerContext { CurrentLexicalScope = currentLexicalScope, CurrentNameScope = container }, node, container));
            decorators.Add(Factory().AsNodeFactory().NewDecorator(typeMetadata));
        }
        if (ShouldAddParamTypesMetadata(node))
        {
            var paramTypesMetadata = Factory().NewMetadataHelper("design:paramtypes", serializer!.SerializeParameterTypesOfNode(new MetadataSerializerContext { CurrentLexicalScope = currentLexicalScope, CurrentNameScope = container }, node, container));
            decorators.Add(Factory().AsNodeFactory().NewDecorator(paramTypesMetadata));
        }
        if (ShouldAddReturnTypeMetadata(node))
        {
            var returnTypeMetadata = Factory().NewMetadataHelper("design:returntype", serializer!.SerializeReturnTypeOfNode(new MetadataSerializerContext { CurrentLexicalScope = currentLexicalScope, CurrentNameScope = container }, node));
            decorators.Add(Factory().AsNodeFactory().NewDecorator(returnTypeMetadata));
        }
        return decorators;
    }

    // metadata.go:289-347
    private System.Collections.Generic.List<Node> GetNewTypeMetadata(Node node, Node container)
    {
        var properties = new System.Collections.Generic.List<Node>();
        if (ShouldAddTypeMetadata(node))
        {
            properties.Add(Factory().NewPropertyAssignment(
                null,
                Factory().NewIdentifier("type"),
                null,
                null,
                Factory().NewArrowFunction(
                    null,
                    null,
                    Factory().NewNodeList([]),
                    null,
                    null,
                    Factory().NewToken(SyntaxKind.EqualsGreaterThanToken),
                    serializer!.SerializeTypeOfNode(new MetadataSerializerContext { CurrentLexicalScope = currentLexicalScope, CurrentNameScope = container }, node, container))));
        }
        if (ShouldAddParamTypesMetadata(node))
        {
            properties.Add(Factory().NewPropertyAssignment(
                null,
                Factory().NewIdentifier("paramTypes"),
                null,
                null,
                Factory().NewArrowFunction(
                    null,
                    null,
                    Factory().NewNodeList([]),
                    null,
                    null,
                    Factory().NewToken(SyntaxKind.EqualsGreaterThanToken),
                    serializer!.SerializeParameterTypesOfNode(new MetadataSerializerContext { CurrentLexicalScope = currentLexicalScope, CurrentNameScope = container }, node, container))));
        }
        if (ShouldAddReturnTypeMetadata(node))
        {
            properties.Add(Factory().NewPropertyAssignment(
                null,
                Factory().NewIdentifier("returnType"),
                null,
                null,
                Factory().NewArrowFunction(
                    null,
                    null,
                    Factory().NewNodeList([]),
                    null,
                    null,
                    Factory().NewToken(SyntaxKind.EqualsGreaterThanToken),
                    serializer!.SerializeReturnTypeOfNode(new MetadataSerializerContext { CurrentLexicalScope = currentLexicalScope, CurrentNameScope = container }, node))));
        }
        if (properties.Count > 0)
        {
            var typeInfoMetadata = Factory().NewMetadataHelper("design:typeinfo", Factory().NewObjectLiteralExpression(Factory().NewNodeList(properties.ToArray()), true));
            return new System.Collections.Generic.List<Node> { Factory().AsNodeFactory().NewDecorator(typeInfoMetadata) };
        }
        return new System.Collections.Generic.List<Node>();
    }

    /**
    * Determines whether to emit the "design:type" metadata based on the node's kind.
    * The caller should have already tested whether the node has decorators and whether the
    * emitDecoratorMetadata compiler option is set.
    *
    * @param node The node to test.
     */
    // metadata.go:356-362
    private static bool ShouldAddTypeMetadata(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.PropertyDeclaration:
                return true;
        }
        return false;
    }

    /**
    * Determines whether to emit the "design:returntype" metadata based on the node's kind.
    * The caller should have already tested whether the node has decorators and whether the
    * emitDecoratorMetadata compiler option is set.
    *
    * @param node The node to test.
     */
    // metadata.go:371-373
    private static bool ShouldAddReturnTypeMetadata(Node node)
    {
        return node.Kind == SyntaxKind.MethodDeclaration;
    }

    /**
    * Determines whether to emit the "design:paramtypes" metadata based on the node's kind.
    * The caller should have already tested whether the node has decorators and whether the
    * emitDecoratorMetadata compiler option is set.
    *
    * @param node The node to test.
     */
    // metadata.go:382-390
    private static bool ShouldAddParamTypesMetadata(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return AstUtilities.GetFirstConstructorWithBody(node) != null;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return true;
        }
        return false;
    }
}
