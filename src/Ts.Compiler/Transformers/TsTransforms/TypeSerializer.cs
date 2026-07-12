// Ported from tsgo internal/transformers/tstransforms/typeserializer.go.
// PORT-NOTE: This is the metadata serializer scaffold. Type-reference resolution and checked
// fallback expressions consume EmitResolver exactly as tsgo does; metadata injection is deferred
// in Metadata.cs until the decorator helper-emission surface is complete.

using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

internal sealed class MetadataSerializer
{
    private readonly global::Ts.Printer.EmitResolver resolver;
    private readonly ScriptTarget languageVersion;
    private readonly bool strictNullChecks;
    private readonly global::Ts.Printer.NodeFactory factory;
    private readonly global::Ts.Printer.EmitContext emitContext;
    private MetadataSerializerContext context;

    internal MetadataSerializer(
        global::Ts.Printer.EmitResolver resolver,
        global::Ts.Printer.NodeFactory factory,
        global::Ts.Printer.EmitContext emitContext,
        ScriptTarget languageVersion,
        bool strictNullChecks)
    {
        this.resolver = resolver;
        this.factory = factory;
        this.emitContext = emitContext;
        this.languageVersion = languageVersion;
        this.strictNullChecks = strictNullChecks;
    }

    internal Node SerializeTypeOfNode(MetadataSerializerContext context, Node node, Node container)
    {
        var oldContext = this.context;
        this.context = context;
        try
        {
            return SerializeTypeOfNodeCore(node, container);
        }
        finally
        {
            this.context = oldContext;
        }
    }

    internal Node SerializeParameterTypesOfNode(MetadataSerializerContext context, Node node, Node container)
    {
        var oldContext = this.context;
        this.context = context;
        try
        {
            var declaration = global::Ts.Ast.AstUtilities.IsClassLike(node)
                ? GetFirstConstructorWithBody(node)
                : node;
            if (declaration is null || !global::Ts.Ast.AstUtilities.NodeIsPresent(declaration.Body()))
            {
                return factory.NewArrayLiteralExpression(factory.NewNodeList([]), false);
            }
            var parameters = declaration.Parameters();
            var values = new System.Collections.Generic.List<Node>();
            foreach (var parameter in parameters)
            {
                if (global::Ts.Ast.AstUtilities.IsThisParameter(parameter))
                {
                    continue;
                }
                values.Add(SerializeTypeNode(parameter.Type()));
            }
            return factory.NewArrayLiteralExpression(factory.NewNodeList(values.ToArray()), false);
        }
        finally
        {
            this.context = oldContext;
        }
    }

    internal Node SerializeReturnTypeOfNode(MetadataSerializerContext context, Node node)
    {
        var oldContext = this.context;
        this.context = context;
        try
        {
            return node.Type() is { } type ? SerializeTypeNode(type) : factory.NewVoidZeroExpression();
        }
        finally
        {
            this.context = oldContext;
        }
    }

    internal static Node? GetSetAccessorValueParameter(SetAccessorDeclaration? node)
    {
        if (node?.Parameters is not { Nodes.Length: > 0 } parameters)
        {
            return null;
        }
        return parameters.Nodes.Length >= 2 && global::Ts.Ast.AstUtilities.IsThisParameter(parameters.Nodes[0])
            ? parameters.Nodes[1]
            : parameters.Nodes[0];
    }

    private static Node? GetFirstConstructorWithBody(Node node)
    {
        foreach (var member in node.Members() ?? [])
        {
            if (member.Kind == SyntaxKind.Constructor &&
                global::Ts.Ast.AstUtilities.NodeIsPresent(member.Body()))
            {
                return member;
            }
        }
        return null;
    }

    private Node SerializeTypeOfNodeCore(Node node, Node container)
    {
        _ = container;
        return node.Kind switch
        {
            SyntaxKind.PropertyDeclaration or SyntaxKind.Parameter => SerializeTypeNode(node.Type()),
            SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression or SyntaxKind.MethodDeclaration =>
                factory.NewIdentifier("Function"),
            _ => factory.NewVoidZeroExpression(),
        };
    }

    private Node SerializeTypeNode(Node? node)
    {
        _ = strictNullChecks;
        if (node is null)
        {
            return factory.NewIdentifier("Object");
        }
        return node.Kind switch
        {
            SyntaxKind.VoidKeyword or SyntaxKind.UndefinedKeyword or SyntaxKind.NeverKeyword =>
                factory.NewVoidZeroExpression(),
            SyntaxKind.FunctionType or SyntaxKind.ConstructorType => factory.NewIdentifier("Function"),
            SyntaxKind.ArrayType or SyntaxKind.TupleType => factory.NewIdentifier("Array"),
            SyntaxKind.TypePredicate or SyntaxKind.BooleanKeyword => factory.NewIdentifier("Boolean"),
            SyntaxKind.TemplateLiteralType or SyntaxKind.StringKeyword => factory.NewIdentifier("String"),
            SyntaxKind.NumberKeyword => factory.NewIdentifier("Number"),
            SyntaxKind.BigIntKeyword => SerializeBigIntConstructor(),
            SyntaxKind.SymbolKeyword => factory.NewIdentifier("Symbol"),
            SyntaxKind.TypeReference => SerializeTypeReferenceNode(node.AsTypeReferenceNode()),
            _ => factory.NewIdentifier("Object"),
        };
    }

    private Node SerializeTypeReferenceNode(TypeReferenceNode node)
    {
        var serialScope = context.CurrentNameScope ?? context.CurrentLexicalScope;
        if (serialScope is null)
        {
            return factory.NewIdentifier("Object");
        }
        var typeName = emitContext.ParseNode(node.TypeName);
        var scope = emitContext.ParseNode(serialScope);
        if (typeName is null || scope is null)
        {
            return factory.NewIdentifier("Object");
        }
        return resolver.GetTypeReferenceSerializationKind(typeName, scope) switch
        {
            global::Ts.Printer.TypeReferenceSerializationKind.TypeWithConstructSignatureAndValue =>
                SerializeEntityNameAsExpression(node.TypeName!),
            global::Ts.Printer.TypeReferenceSerializationKind.VoidNullableOrNeverType =>
                factory.NewVoidZeroExpression(),
            global::Ts.Printer.TypeReferenceSerializationKind.NumberLikeType => factory.NewIdentifier("Number"),
            global::Ts.Printer.TypeReferenceSerializationKind.BigIntLikeType => SerializeBigIntConstructor(),
            global::Ts.Printer.TypeReferenceSerializationKind.StringLikeType => factory.NewIdentifier("String"),
            global::Ts.Printer.TypeReferenceSerializationKind.BooleanType => factory.NewIdentifier("Boolean"),
            global::Ts.Printer.TypeReferenceSerializationKind.ArrayLikeType => factory.NewIdentifier("Array"),
            global::Ts.Printer.TypeReferenceSerializationKind.ESSymbolType => factory.NewIdentifier("Symbol"),
            global::Ts.Printer.TypeReferenceSerializationKind.Promise => factory.NewIdentifier("Promise"),
            global::Ts.Printer.TypeReferenceSerializationKind.TypeWithCallSignature => factory.NewIdentifier("Function"),
            _ => factory.NewIdentifier("Object"),
        };
    }

    private Node SerializeBigIntConstructor()
    {
        if (languageVersion >= ScriptTarget.ES2020)
        {
            return factory.NewIdentifier("BigInt");
        }
        return factory.NewConditionalExpression(
            factory.NewTypeCheck(factory.NewIdentifier("BigInt"), "function"),
            factory.NewToken(SyntaxKind.QuestionToken),
            factory.NewIdentifier("BigInt"),
            factory.NewToken(SyntaxKind.ColonToken),
            factory.NewIdentifier("Object"));
    }

    private Node SerializeEntityNameAsExpression(Node node)
    {
        return node.Kind == SyntaxKind.Identifier
            ? node.Clone(factory)!
            : factory.NewPropertyAccessExpression(
                SerializeEntityNameAsExpression(node.AsQualifiedName().Left!),
                null,
                node.AsQualifiedName().Right!,
                NodeFlags.None);
    }
}

internal struct MetadataSerializerContext
{
    internal Node? CurrentLexicalScope;
    internal Node? CurrentNameScope;
    internal bool SerializingConditionalTypeBranch;
}
