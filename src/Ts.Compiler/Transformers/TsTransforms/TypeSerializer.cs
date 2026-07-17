// Ported from tsgo internal/transformers/tstransforms/typeserializer.go — the decorator-metadata
// type serializer (design:type / design:paramtypes / design:returntype value synthesis).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

// typeserializer.go:11-18
internal sealed class MetadataSerializer
{
    private readonly global::Ts.Printer.EmitResolver resolver;
    private readonly ScriptTarget languageVersion;
    private readonly bool strictNullChecks;
    private readonly global::Ts.Printer.NodeFactory f;
    private readonly global::Ts.Printer.EmitContext ec;
    private MetadataSerializerContext c;

    // typeserializer.go:26-28
    internal MetadataSerializer(
        global::Ts.Printer.EmitResolver resolver,
        global::Ts.Printer.NodeFactory f,
        global::Ts.Printer.EmitContext ec,
        ScriptTarget languageVersion,
        bool strictNullChecks)
    {
        this.resolver = resolver;
        this.f = f;
        this.ec = ec;
        this.languageVersion = languageVersion;
        this.strictNullChecks = strictNullChecks;
    }

    // typeserializer.go:34-39
    internal Node SerializeTypeOfNode(MetadataSerializerContext ctx, Node node, Node container)
    {
        var oldCtx = c;
        c = ctx;
        try
        {
            return SerializeTypeOfNodeCore(node, container);
        }
        finally
        {
            c = oldCtx;
        }
    }

    // typeserializer.go:41-46
    internal Node SerializeParameterTypesOfNode(MetadataSerializerContext ctx, Node node, Node container)
    {
        var oldCtx = c;
        c = ctx;
        try
        {
            return SerializeParameterTypesOfNodeCore(node, container);
        }
        finally
        {
            c = oldCtx;
        }
    }

    // typeserializer.go:48-53
    internal Node SerializeReturnTypeOfNode(MetadataSerializerContext ctx, Node node)
    {
        var oldCtx = c;
        c = ctx;
        try
        {
            return SerializeReturnTypeOfNodeCore(node);
        }
        finally
        {
            c = oldCtx;
        }
    }

    // typeserializer.go:55-63
    internal static Node? GetSetAccessorValueParameter(SetAccessorDeclaration? node)
    {
        if (node != null && node.Parameters!.Nodes.Length > 0)
        {
            if (node.Parameters.Nodes.Length >= 2 && AstUtilities.IsThisParameter(node.Parameters.Nodes[0]))
            {
                return node.Parameters.Nodes[1];
            }
            return node.Parameters.Nodes[0];
        }
        return null;
    }

    /**
     * Get the type annotation for the value parameter.
     *
     * @internal
     */
    // typeserializer.go:70-76
    private static Node? GetSetAccessorTypeAnnotationNode(SetAccessorDeclaration node)
    {
        var p = GetSetAccessorValueParameter(node);
        if (p != null && p.Type() != null)
        {
            return p.Type();
        }
        return null;
    }

    // typeserializer.go:78-87
    private static Node? GetAccessorTypeNode(Node node, Node container)
    {
        var accessors = AstUtilities.GetAllAccessorDeclarations(container.Members() ?? [], node);
        if (accessors.SetAccessor != null)
        {
            return GetSetAccessorTypeAnnotationNode(accessors.SetAccessor);
        }
        if (accessors.GetAccessor != null)
        {
            return accessors.GetAccessor.Type;
        }
        return null;
    }

    /**
    * Serializes the type of a node for use with decorator type metadata.
    * @param node The node that should have its type serialized.
     */
    // typeserializer.go:93-104
    private Node SerializeTypeOfNodeCore(Node node, Node container)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.Parameter:
                return SerializeTypeNode(node.Type());
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return SerializeTypeNode(GetAccessorTypeNode(node, container));
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.MethodDeclaration:
                return f.NewIdentifier("Function");
            default:
                return f.NewVoidZeroExpression();
        }
    }

    /**
    * Serializes the type of a node for use with decorator type metadata.
    * @param node The node that should have its type serialized.
     */
    // typeserializer.go:110-135
    private Node SerializeParameterTypesOfNodeCore(Node node, Node container)
    {
        Node? valueDeclaration = null;
        if (AstUtilities.IsClassLike(node))
        {
            valueDeclaration = AstUtilities.GetFirstConstructorWithBody(node);
        }
        else if (AstUtilities.IsFunctionLike(node) && AstUtilities.NodeIsPresent(node.Body()))
        {
            valueDeclaration = node;
        }

        if (valueDeclaration == null)
        {
            return f.NewArrayLiteralExpression(f.NewNodeList([]), false);
        }

        var expressions = new List<Node>();
        var parameters = GetParametersOfDecoratedDeclaration(valueDeclaration, container);
        for (var i = 0; i < parameters!.Nodes.Length; i++)
        {
            var parameter = parameters.Nodes[i];
            if (i == 0 && NodeTests.IsIdentifier(parameter.Name()!) && parameter.Name()!.Text() == "this")
            {
                continue;
            }
            if (parameter.AsParameterDeclaration().DotDotDotToken != null)
            {
                expressions.Add(SerializeTypeNode(AstUtilities.GetRestParameterElementType(parameter.Type())));
            }
            else
            {
                expressions.Add(SerializeTypeOfNodeCore(parameter, container));
            }
        }
        return f.NewArrayLiteralExpression(f.NewNodeList(expressions.ToArray()), false);
    }

    // typeserializer.go:137-145
    private static NodeList? GetParametersOfDecoratedDeclaration(Node node, Node? container)
    {
        if (container != null && node.Kind == SyntaxKind.GetAccessor)
        {
            var acc = AstUtilities.GetAllAccessorDeclarations(container.Members() ?? [], node);
            if (acc.SetAccessor != null)
            {
                return acc.SetAccessor.Parameters;
            }
        }
        return node.ParameterList();
    }

    /**
    * Serializes the return type of a node for use with decorator type metadata.
    * @param node The node that should have its return type serialized.
     */
    // typeserializer.go:151-158
    private Node SerializeReturnTypeOfNodeCore(Node node)
    {
        if (AstUtilities.IsFunctionLike(node) && node.Type() != null)
        {
            return SerializeTypeNode(node.Type());
        }
        else if (AstUtilities.IsAsyncFunction(node))
        {
            return f.NewIdentifier("Promise");
        }
        return f.NewVoidZeroExpression();
    }

    /**
    * Serializes a type node for use with decorator type metadata.
    *
    * Types are serialized in the following fashion:
    * - Void types point to "undefined" (e.g. "void 0")
    * - Function and Constructor types point to the global "Function" constructor.
    * - Interface types with a call or construct signature types point to the global
    *   "Function" constructor.
    * - Array and Tuple types point to the global "Array" constructor.
    * - Type predicates and booleans point to the global "Boolean" constructor.
    * - String literal types and strings point to the global "String" constructor.
    * - Enum and number types point to the global "Number" constructor.
    * - Symbol types point to the global "Symbol" constructor.
    * - Type references to classes (or class-like variables) point to the constructor for the class.
    * - Anything else points to the global "Object" constructor.
    *
    * @param node The type node to serialize.
     */
    // typeserializer.go:178-240
    private Node SerializeTypeNode(Node? node)
    {
        if (node == null)
        {
            return f.NewIdentifier("Object");
        }

        node = AstUtilities.SkipTypeParentheses(node);

        switch (node.Kind)
        {
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NeverKeyword:
                return f.NewVoidZeroExpression();
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
                return f.NewIdentifier("Function");
            case SyntaxKind.ArrayType:
            case SyntaxKind.TupleType:
                return f.NewIdentifier("Array");
            case SyntaxKind.TypePredicate:
                if (node.AsTypePredicateNode().AssertsModifier != null)
                {
                    return f.NewVoidZeroExpression();
                }
                return f.NewIdentifier("Boolean");
            case SyntaxKind.BooleanKeyword:
                return f.NewIdentifier("Boolean");
            case SyntaxKind.TemplateLiteralType:
            case SyntaxKind.StringKeyword:
                return f.NewIdentifier("String");
            case SyntaxKind.ObjectKeyword:
                return f.NewIdentifier("Object");
            case SyntaxKind.LiteralType:
                return SerializeLiteralOfLiteralTypeNode(node.AsLiteralTypeNode().Literal!);
            case SyntaxKind.NumberKeyword:
                return f.NewIdentifier("Number");
            case SyntaxKind.BigIntKeyword:
                return SerializeBigIntConstructor();
            case SyntaxKind.SymbolKeyword:
                return f.NewIdentifier("Symbol");
            case SyntaxKind.TypeReference:
                return SerializeTypeReferenceNode(node.AsTypeReferenceNode());
            case SyntaxKind.IntersectionType:
                return SerializeUnionOrIntersectionConstituents(node.AsIntersectionTypeNode().Types!.Nodes, true);
            case SyntaxKind.UnionType:
                return SerializeUnionOrIntersectionConstituents(node.AsUnionTypeNode().Types!.Nodes, false);
            case SyntaxKind.ConditionalType:
            {
                var oldState = c.SerializingConditionalTypeBranch;
                c.SerializingConditionalTypeBranch = true;
                try
                {
                    return SerializeUnionOrIntersectionConstituents(
                        [node.AsConditionalTypeNode().TrueType!, node.AsConditionalTypeNode().FalseType!], false);
                }
                finally
                {
                    c.SerializingConditionalTypeBranch = oldState;
                }
            }
            case SyntaxKind.TypeOperator:
                if (node.AsTypeOperatorNode().Operator == SyntaxKind.ReadonlyKeyword)
                {
                    return SerializeTypeNode(node.Type());
                }
                // TODO: why is `unique symbol` not handled as `Symbol`? This falls back to `Object`
                break;
            case SyntaxKind.TypeQuery:
            case SyntaxKind.IndexedAccessType:
            case SyntaxKind.MappedType:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.ThisType:
            case SyntaxKind.ImportType:
                break;

            // handle JSDoc types from an invalid parse
            case SyntaxKind.JSDocAllType:
            case SyntaxKind.JSDocVariadicType:
                break;
            case SyntaxKind.JSDocNullableType:
            case SyntaxKind.JSDocNonNullableType:
            case SyntaxKind.JSDocOptionalType:
                return SerializeTypeNode(node.Type());
            default:
                global::Ts.Core.Debug.FailBadSyntaxKind(node);
                return null!;
        }
        return f.NewIdentifier("Object");
    }

    // typeserializer.go:242-293
    private Node SerializeUnionOrIntersectionConstituents(Node[] types, bool isIntersection)
    {
        // Note when updating logic here also update `getEntityNameForDecoratorMetadata` in checker.ts so that aliases can be marked as referenced
        Node? serializedType = null;
        foreach (var typeNodeIter in types)
        {
            var typeNode = AstUtilities.SkipTypeParentheses(typeNodeIter);
            if (typeNode.Kind == SyntaxKind.NeverKeyword)
            {
                if (isIntersection)
                {
                    return f.NewVoidZeroExpression(); // Reduce to `never` in an intersection
                }
                continue; // Elide `never` in a union
            }

            if (typeNode.Kind == SyntaxKind.UnknownKeyword)
            {
                if (!isIntersection)
                {
                    return f.NewIdentifier("Object"); // Reduce to `unknown` in a union
                }
                continue; // Elide `unknown` in an intersection
            }

            if (typeNode.Kind == SyntaxKind.AnyKeyword)
            {
                return f.NewIdentifier("Object"); // Reduce to `any` in a union or intersection
            }

            if (!strictNullChecks && ((NodeTests.IsLiteralTypeNode(typeNode) && typeNode.AsLiteralTypeNode().Literal!.Kind == SyntaxKind.NullKeyword) || typeNode.Kind == SyntaxKind.UndefinedKeyword))
            {
                continue; // Elide null and undefined from unions for metadata, just like what we did prior to the implementation of strict null checks
            }

            var serializedConstituent = SerializeTypeNode(typeNode);
            if (NodeTests.IsIdentifier(serializedConstituent) && serializedConstituent.AsIdentifier().Text == "Object")
            {
                // One of the individual is global object, return immediately
                return serializedConstituent;
            }

            // If there exists union that is not `void 0` expression, check if the the common type is identifier.
            // anything more complex and we will just default to Object
            if (serializedType != null)
            {
                // Different types
                if (!EquateSerializedTypeNodes(serializedType, serializedConstituent))
                {
                    return f.NewIdentifier("Object");
                }
            }
            else
            {
                // Initialize the union type
                serializedType = serializedConstituent;
            }
        }

        // If we were able to find common type, use it
        if (serializedType != null)
        {
            return serializedType;
        }
        return f.NewVoidZeroExpression(); // Fallback is only hit if all union constituents are null/undefined/never
    }

    // typeserializer.go:295-320
    private Node SerializeLiteralOfLiteralTypeNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return f.NewIdentifier("String");
            case SyntaxKind.PrefixUnaryExpression:
            {
                var operand = node.AsPrefixUnaryExpression().Operand!;
                switch (operand.Kind)
                {
                    case SyntaxKind.NumericLiteral:
                    case SyntaxKind.BigIntLiteral:
                        return SerializeLiteralOfLiteralTypeNode(operand);
                    default:
                        global::Ts.Core.Debug.FailBadSyntaxKind(operand);
                        return null!;
                }
            }
            case SyntaxKind.NumericLiteral:
                return f.NewIdentifier("Number");
            case SyntaxKind.BigIntLiteral:
                return SerializeBigIntConstructor();
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
                return f.NewIdentifier("Boolean");
            case SyntaxKind.NullKeyword:
                return f.NewVoidZeroExpression();
            default:
                global::Ts.Core.Debug.FailBadSyntaxKind(node);
                return null!;
        }
    }

    /**
    * Serializes a TypeReferenceNode to an appropriate JS constructor value for use with decorator type metadata.
    * @param node The type reference node.
     */
    // typeserializer.go:326-386
    private Node SerializeTypeReferenceNode(TypeReferenceNode node)
    {
        var serialScope = c.CurrentNameScope;
        serialScope ??= c.CurrentLexicalScope;
        var kind = resolver.GetTypeReferenceSerializationKind(ec.ParseNode(node.TypeName)!, ec.ParseNode(serialScope)!);
        switch (kind)
        {
            case global::Ts.Printer.TypeReferenceSerializationKind.Unknown:
            {
                // From conditional type type reference that cannot be resolved is Similar to any or unknown
                if (c.SerializingConditionalTypeBranch)
                {
                    return f.NewIdentifier("Object");
                }

                var serialized = SerializeEntityNameAsExpressionFallback(node.TypeName!);
                var temp = f.NewTempVariable();
                ec.AddVariableDeclaration(temp);
                return f.NewConditionalExpression(
                    f.NewTypeCheck(f.NewAssignmentExpression(temp, serialized), "function"),
                    f.NewToken(SyntaxKind.QuestionToken),
                    temp,
                    f.NewToken(SyntaxKind.ColonToken),
                    f.NewIdentifier("Object"));
            }

            case global::Ts.Printer.TypeReferenceSerializationKind.TypeWithConstructSignatureAndValue:
                return SerializeEntityNameAsExpression(node.TypeName!)!;

            case global::Ts.Printer.TypeReferenceSerializationKind.VoidNullableOrNeverType:
                return f.NewVoidZeroExpression();

            case global::Ts.Printer.TypeReferenceSerializationKind.BigIntLikeType:
                return SerializeBigIntConstructor();

            case global::Ts.Printer.TypeReferenceSerializationKind.BooleanType:
                return f.NewIdentifier("Boolean");

            case global::Ts.Printer.TypeReferenceSerializationKind.NumberLikeType:
                return f.NewIdentifier("Number");

            case global::Ts.Printer.TypeReferenceSerializationKind.StringLikeType:
                return f.NewIdentifier("String");

            case global::Ts.Printer.TypeReferenceSerializationKind.ArrayLikeType:
                return f.NewIdentifier("Array");

            case global::Ts.Printer.TypeReferenceSerializationKind.ESSymbolType:
                return f.NewIdentifier("Symbol");

            case global::Ts.Printer.TypeReferenceSerializationKind.TypeWithCallSignature:
                return f.NewIdentifier("Function");

            case global::Ts.Printer.TypeReferenceSerializationKind.Promise:
                return f.NewIdentifier("Promise");

            case global::Ts.Printer.TypeReferenceSerializationKind.ObjectType:
                return f.NewIdentifier("Object");
            default:
                global::Ts.Core.Debug.Fail("unknown type reference serialization kind");
                return null!;
        }
    }

    // typeserializer.go:388-399
    private Node SerializeBigIntConstructor()
    {
        if (languageVersion >= ScriptTarget.ES2020)
        {
            return f.NewIdentifier("BigInt");
        }
        return f.NewConditionalExpression(
            f.NewTypeCheck(f.NewIdentifier("BigInt"), "function"),
            f.NewToken(SyntaxKind.QuestionToken),
            f.NewIdentifier("BigInt"),
            f.NewToken(SyntaxKind.ColonToken),
            f.NewIdentifier("Object"));
    }

    /**
    * Serializes an entity name as an expression for decorator type metadata.
    * @param node The entity name to serialize.
     */
    // typeserializer.go:405-419
    private Node? SerializeEntityNameAsExpression(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            {
                // Create a clone of the name with a new parent, and treat it as if it were
                // a source tree node for the purposes of the checker.
                var name = node.Clone(f)!;
                name.Loc = node.Loc;
                ec.UnsetOriginal(name); // make this identifier emulate a parse node, making it behave correctly when inspected by the module transforms
                name.Parent = ec.ParseNode(c.CurrentLexicalScope); // ensure the parent is set to a parse tree node.
                return name;
            }
            case SyntaxKind.QualifiedName:
                return SerializeQualifiedNameAsExpression(node.AsQualifiedName());
        }
        return null;
    }

    /**
    * Serializes an qualified name as an expression for decorator type metadata.
    * @param node The qualified name to serialize.
     */
    // typeserializer.go:425-427
    private Node SerializeQualifiedNameAsExpression(QualifiedName node)
    {
        return f.NewPropertyAccessExpression(SerializeEntityNameAsExpression(node.Left!)!, null, node.Right!, NodeFlags.None);
    }

    /**
    * Serializes an entity name which may not exist at runtime, but whose access shouldn't throw
    * @param node The entity name to serialize.
     */
    // typeserializer.go:433-454
    private Node SerializeEntityNameAsExpressionFallback(Node node)
    {
        if (node.Kind == SyntaxKind.Identifier)
        {
            // A -> typeof A !== "undefined" && A
            var copied = SerializeEntityNameAsExpression(node)!;
            return CreateCheckedValue(copied, copied);
        }
        if (node.AsQualifiedName().Left!.Kind == SyntaxKind.Identifier)
        {
            // A.B -> typeof A !== "undefined" && A.B
            return CreateCheckedValue(SerializeEntityNameAsExpression(node.AsQualifiedName().Left!)!, SerializeEntityNameAsExpression(node)!);
        }
        // A.B.C -> typeof A !== "undefined" && (_a = A.B) !== void 0 && _a.C
        var left = SerializeEntityNameAsExpressionFallback(node.AsQualifiedName().Left!);
        var temp = f.NewTempVariable();
        ec.AddVariableDeclaration(temp);
        return f.NewLogicalANDExpression(
            f.NewLogicalANDExpression(
                left.AsBinaryExpression().Left!,
                f.NewStrictInequalityExpression(f.NewAssignmentExpression(temp, left.AsBinaryExpression().Right!), f.NewVoidZeroExpression())),
            f.NewPropertyAccessExpression(temp, null, node.AsQualifiedName().Right!, NodeFlags.None));
    }

    /**
    * Produces an expression that results in `right` if `left` is not undefined at runtime:
    *
    * ```
    * typeof left !== "undefined" && right
    * ```
    *
    * We use `typeof L !== "undefined"` (rather than `L !== undefined`) since `L` may not be declared.
    * It's acceptable for this expression to result in `false` at runtime, as the result is intended to be
    * further checked by any containing expression.
     */
    // typeserializer.go:467-472
    private Node CreateCheckedValue(Node left, Node right)
    {
        return f.NewLogicalANDExpression(
            f.NewStrictInequalityExpression(f.NewTypeOfExpression(left), f.NewStringLiteral("undefined", TokenFlags.None)),
            right);
    }

    // typeserializer.go:474-511
    private bool EquateSerializedTypeNodes(Node left, Node right)
    {
        // temp vars used in fallback
        if (global::Ts.Transformers.Transformers.IsGeneratedIdentifier(ec, left))
        {
            return global::Ts.Transformers.Transformers.IsGeneratedIdentifier(ec, right);
        }
        // entity names
        if (NodeTests.IsIdentifier(left))
        {
            return NodeTests.IsIdentifier(right) && left.Text() == right.Text();
        }
        if (NodeTests.IsPropertyAccessExpression(left))
        {
            return NodeTests.IsPropertyAccessExpression(right) && EquateSerializedTypeNodes(left.Expression()!, right.Expression()!) && EquateSerializedTypeNodes(left.Name()!, right.Name()!);
        }
        // `void 0`
        if (NodeTests.IsVoidExpression(left))
        {
            return NodeTests.IsVoidExpression(right) && NodeTests.IsNumericLiteral(left.Expression()!) && NodeTests.IsNumericLiteral(right.Expression()!) && left.Expression()!.Text() == "0" && right.Expression()!.Text() == "0";
        }
        // `"undefined"` or `"function"` in `typeof` checks
        if (NodeTests.IsStringLiteral(left))
        {
            return NodeTests.IsStringLiteral(right) && left.Text() == right.Text();
        }
        // used in `typeof` checks for fallback
        if (NodeTests.IsTypeOfExpression(left))
        {
            return NodeTests.IsTypeOfExpression(right) && EquateSerializedTypeNodes(left.Expression()!, right.Expression()!);
        }
        // parens in `typeof` checks with temps
        if (NodeTests.IsParenthesizedExpression(left))
        {
            return NodeTests.IsParenthesizedExpression(right) && EquateSerializedTypeNodes(left.Expression()!, right.Expression()!);
        }
        // conditionals used in fallback
        if (NodeTests.IsConditionalExpression(left))
        {
            return NodeTests.IsConditionalExpression(right) && EquateSerializedTypeNodes(left.AsConditionalExpression().Condition!, right.AsConditionalExpression().Condition!) && EquateSerializedTypeNodes(left.AsConditionalExpression().WhenTrue!, right.AsConditionalExpression().WhenTrue!) && EquateSerializedTypeNodes(left.AsConditionalExpression().WhenFalse!, right.AsConditionalExpression().WhenFalse!);
        }
        // logical binary and assignments used in fallback
        if (NodeTests.IsBinaryExpression(left))
        {
            return NodeTests.IsBinaryExpression(right) && left.AsBinaryExpression().OperatorToken!.Kind == right.AsBinaryExpression().OperatorToken!.Kind && EquateSerializedTypeNodes(left.AsBinaryExpression().Left!, right.AsBinaryExpression().Left!) && EquateSerializedTypeNodes(left.AsBinaryExpression().Right!, right.AsBinaryExpression().Right!);
        }
        return false;
    }
}

// typeserializer.go:20-24
internal struct MetadataSerializerContext
{
    internal Node? CurrentLexicalScope;
    internal Node? CurrentNameScope;
    internal bool SerializingConditionalTypeBranch;
}
