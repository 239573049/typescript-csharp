// Ported from tsgo internal/printer/printer.go (Types + Binding patterns + JSDoc type sections).

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using AstUtil = global::Ts.Ast.AstUtilities;

public sealed partial class Printer
{
    //
    // Types
    //

    private void EmitKeywordTypeNode(KeywordTypeNode node)
    {
        EmitKeywordNode(node.AsNode());
    }

    private void EmitTypePredicateParameterName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierReference(node.AsIdentifier());
                break;
            case SyntaxKind.ThisType:
                EmitThisType(node.AsThisTypeNode());
                break;
            default:
                throw new InvalidOperationException($"unexpected TypePredicateParameterName: {node.Kind}");
        }
    }

    private void EmitTypePredicate(TypePredicateNode node)
    {
        var state = EnterNode(node.AsNode());
        if (node.AssertsModifier is not null)
        {
            EmitTokenNode(node.AssertsModifier);
            WriteSpace();
        }
        EmitTypePredicateParameterName(node.ParameterName!);
        if (node.Type is not null)
        {
            WriteSpace();
            WriteKeyword("is");
            WriteSpace();
            EmitTypeNodeOutsideExtends(node.Type);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeArgument(Node node)
    {
        EmitTypeNodeOutsideExtends(node);
    }

    private void EmitTypeArguments(Node parentNode, NodeList? nodes)
    {
        if (nodes is null)
        {
            return;
        }
        EmitList(static (p, node) => p.EmitTypeParameterDeclarationNode(node), parentNode, nodes, ListFormat.TypeArguments);
    }

    private void EmitTypeReference(TypeReferenceNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitEntityName(node.TypeName!);
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        ExitNode(node.AsNode(), state);
    }

    // Emits the return type of a FunctionTypeNode or ConstructorTypeNode, including the arrow (`=>`)
    private void EmitReturnType(Node? node)
    {
        if (node is null)
        {
            return;
        }
        WritePunctuation("=>");
        WriteSpace();
        if (inExtends && node.Kind == SyntaxKind.InferType && node.AsInferTypeNode().TypeParameter!.AsTypeParameterDeclaration().Constraint is not null)
        {
            EmitTypeNodePreservingExtends(node, TypePrecedence.Highest);
        }
        else
        {
            EmitTypeNodePreservingExtends(node, TypePrecedence.Lowest);
        }
    }

    private void EmitFunctionType(FunctionTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitTypeParameters(node.AsNode(), node.TypeParameters);
        EmitParameters(node.AsNode(), node.Parameters!);
        WriteSpace();
        EmitReturnType(node.Type);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitConstructorType(ConstructorTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("new");
        WriteSpace();
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitTypeParameters(node.AsNode(), node.TypeParameters);
        EmitParameters(node.AsNode(), node.Parameters!);
        WriteSpace();
        EmitReturnType(node.Type);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeQuery(TypeQueryNode node)
    {
        var state = EnterNode(node.AsNode());
        WriteKeyword("typeof");
        WriteSpace();
        EmitEntityName(node.ExprName!);
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeLiteral(TypeLiteralNode node)
    {
        var state = EnterNode(node.AsNode());
        PushNameGenerationScope(node.AsNode());
        GenerateAllMemberNames(node.Members);
        WritePunctuation("{");
        var flags = ShouldEmitOnSingleLine(node.AsNode()) ? ListFormat.SingleLineTypeLiteralMembers : ListFormat.MultiLineTypeLiteralMembers;
        EmitList(static (p, n) => p.EmitTypeElement(n), node.AsNode(), node.Members, flags | ListFormat.NoSpaceIfEmpty);
        WritePunctuation("}");
        PopNameGenerationScope(node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitArrayType(ArrayTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitPostfixTypeOperand(node.ElementType!, node.AsNode());
        WritePunctuation("[");
        WritePunctuation("]");
        ExitNode(node.AsNode(), state);
    }

    private void EmitPostfixTypeOperand(Node operand, Node parent)
    {
        if (PrinterUtilities.IsParseTreeNode(parent) && operand.Kind == SyntaxKind.TypeQuery)
        {
            EmitTypeNode(operand, TypePrecedence.TypeOperator);
            return;
        }
        EmitTypeNode(operand, TypePrecedence.Postfix);
    }

    private void EmitTupleElementType(Node node)
    {
        EmitTypeNodeOutsideExtends(node);
    }

    private void EmitTupleType(TupleTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.OpenBracketToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        var flags = ShouldEmitOnSingleLine(node.AsNode()) ? ListFormat.SingleLineTupleTypeElements : ListFormat.MultiLineTupleTypeElements;
        EmitList(static (p, n) => p.EmitTupleElementType(n), node.AsNode(), node.Elements, flags | ListFormat.NoSpaceIfEmpty);
        EmitToken(SyntaxKind.CloseBracketToken, node.Elements!.End, WriteKind.Punctuation, node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitRestType(RestTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("...");
        EmitTypeNodeOutsideExtends(node.Type!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitOptionalType(OptionalTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitPostfixTypeOperand(node.Type!, node.AsNode());
        WritePunctuation("?");
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamedTupleMember(NamedTupleMember node)
    {
        var state = EnterNode(node.AsNode());
        EmitPunctuationNode(node.DotDotDotToken);
        EmitIdentifierName(node.Name()!.AsIdentifier());
        EmitPunctuationNode(node.QuestionToken);
        EmitToken(SyntaxKind.ColonToken, PrinterUtilities.GreatestEnd(node.Name()!.End, node.QuestionToken), WriteKind.Punctuation, node.AsNode());
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.Type!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitUnionTypeConstituent(Node node)
    {
        EmitTypeNode(node, TypePrecedence.TypeOperator);
    }

    private void EmitUnionType(UnionTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitList(static (p, n) => p.EmitUnionTypeConstituent(n), node.AsNode(), node.Types, ListFormat.UnionTypeConstituents);
        ExitNode(node.AsNode(), state);
    }

    private void EmitIntersectionTypeConstituent(Node node)
    {
        EmitTypeNode(node, TypePrecedence.TypeOperator);
    }

    private void EmitIntersectionType(IntersectionTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitList(static (p, n) => p.EmitIntersectionTypeConstituent(n), node.AsNode(), node.Types, ListFormat.IntersectionTypeConstituents);
        ExitNode(node.AsNode(), state);
    }

    private void EmitConditionalType(ConditionalTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitTypeNode(node.CheckType!, TypePrecedence.Union);
        WriteSpace();
        WriteKeyword("extends");
        WriteSpace();
        EmitTypeNodeInExtends(node.ExtendsType!);
        WriteSpace();
        WritePunctuation("?");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.TrueType!);
        WriteSpace();
        WritePunctuation(":");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.FalseType!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitInferTypeParameter(TypeParameterDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        if (node.Constraint is not null)
        {
            WriteSpace();
            WriteKeyword("extends");
            WriteSpace();
            EmitTypeNodeInExtends(node.Constraint);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitInferType(InferTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        WriteKeyword("infer");
        WriteSpace();
        EmitInferTypeParameter(node.TypeParameter!.AsTypeParameterDeclaration());
        ExitNode(node.AsNode(), state);
    }

    private void EmitParenthesizedType(ParenthesizedTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("(");
        EmitTypeNodeOutsideExtends(node.Type!);
        WritePunctuation(")");
        ExitNode(node.AsNode(), state);
    }

    private void EmitThisType(ThisTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        WriteKeyword("this");
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeOperator(TypeOperatorNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(node.Operator, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitTypeNode(node.Type!, node.Operator == SyntaxKind.ReadonlyKeyword ? TypePrecedence.Postfix : TypePrecedence.TypeOperator);
        ExitNode(node.AsNode(), state);
    }

    private void EmitIndexedAccessType(IndexedAccessTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitPostfixTypeOperand(node.ObjectType!, node.AsNode());
        WritePunctuation("[");
        EmitTypeNodeOutsideExtends(node.IndexType!);
        WritePunctuation("]");
        ExitNode(node.AsNode(), state);
    }

    private void EmitMappedTypeParameter(TypeParameterDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        WriteSpace();
        WriteKeyword("in");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.Constraint!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitMappedType(MappedTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        var singleLine = ShouldEmitOnSingleLine(node.AsNode());
        WritePunctuation("{");
        if (singleLine)
        {
            WriteSpace();
        }
        else
        {
            WriteLine();
            IncreaseIndent();
        }
        if (node.ReadonlyToken is not null)
        {
            EmitTokenNode(node.ReadonlyToken);
            if (node.ReadonlyToken.Kind != SyntaxKind.ReadonlyKeyword)
            {
                WriteKeyword("readonly");
            }
            WriteSpace();
        }
        WritePunctuation("[");
        EmitMappedTypeParameter(node.TypeParameter!.AsTypeParameterDeclaration());
        if (node.NameType is not null)
        {
            WriteSpace();
            WriteKeyword("as");
            WriteSpace();
            EmitTypeNodeOutsideExtends(node.NameType);
        }
        WritePunctuation("]");
        if (node.QuestionToken is not null)
        {
            EmitPunctuationNode(node.QuestionToken);
            if (node.QuestionToken.Kind != SyntaxKind.QuestionToken)
            {
                WritePunctuation("?");
            }
        }
        WritePunctuation(":");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.Type!);
        WriteTrailingSemicolon();
        if (node.Members is not null)
        {
            if (node.Members.Nodes.Length > 0)
            {
                if (singleLine)
                {
                    WriteSpace();
                }
                else
                {
                    WriteLine();
                }
                EmitList(static (p, n) => p.EmitTypeElement(n), node.AsNode(), node.Members, ListFormat.PreserveLines);
            }
        }
        if (singleLine)
        {
            WriteSpace();
        }
        else
        {
            WriteLine();
            DecreaseIndent();
        }
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitLiteralType(LiteralTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Literal!, OperatorPrecedence.Comma);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateTypeSpan(TemplateLiteralTypeSpan node)
    {
        var state = EnterNode(node.AsNode());
        EmitTypeNodeOutsideExtends(node.Type!);
        EmitTemplateMiddleTail(node.Literal!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateTypeSpanNode(Node node)
    {
        EmitTemplateTypeSpan(node.AsTemplateLiteralTypeSpan());
    }

    private void EmitTemplateType(TemplateLiteralTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        EmitTemplateHead(node.Head!.AsTemplateHead());
        EmitList(static (p, n) => p.EmitTemplateTypeSpanNode(n), node.AsNode(), node.TemplateSpans, ListFormat.TemplateExpressionSpans);
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportTypeNodeAttributes(ImportAttributes node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("{");
        WriteSpace();
        WriteKeyword(node.Token == SyntaxKind.AssertKeyword ? "assert" : "with");
        WritePunctuation(":");
        WriteSpace();
        EmitList(static (p, n) => p.EmitImportAttributeNode(n), node.AsNode(), node.Attributes, ListFormat.ImportAttributes);
        WriteSpace();
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportTypeNode(ImportTypeNode node)
    {
        var state = EnterNode(node.AsNode());
        if (node.IsTypeOf)
        {
            WriteKeyword("typeof");
            WriteSpace();
        }
        WriteKeyword("import");
        WritePunctuation("(");
        EmitTypeNodeOutsideExtends(node.Argument!);
        if (node.Attributes is not null)
        {
            WritePunctuation(",");
            WriteSpace();
            EmitImportTypeNodeAttributes(node.Attributes.AsImportAttributes());
        }
        WritePunctuation(")");
        if (node.Qualifier is not null)
        {
            WritePunctuation(".");
            EmitEntityName(node.Qualifier);
        }
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        ExitNode(node.AsNode(), state);
    }

    // emits a Type node in the `extends` clause of a ConditionalType
    private void EmitTypeNodeInExtends(Node node)
    {
        var savedInExtends = inExtends;
        inExtends = true;
        EmitTypeNodePreservingExtends(node, TypePrecedence.Lowest);
        inExtends = savedInExtends;
    }

    // emits a Type node not in the `extends` clause of a ConditionalType or InferType
    private void EmitTypeNodeOutsideExtends(Node node)
    {
        var savedInExtends = inExtends;
        inExtends = false;
        EmitTypeNodePreservingExtends(node, TypePrecedence.Lowest);
        inExtends = savedInExtends;
    }

    // emits a Type node preserving whether or not we are currently in the `extends` clause
    private void EmitTypeNodePreservingExtends(Node node, TypePrecedence precedence)
    {
        EmitTypeNode(node, precedence);
    }

    private void EmitTypeNode(Node node, TypePrecedence precedence)
    {
        if (inExtends && precedence <= TypePrecedence.Conditional)
        {
            precedence = TypePrecedence.Function;
        }

        var savedInExtends = inExtends;
        bool parens = global::Ts.Ast.Precedence.GetTypeNodePrecedence(node) < precedence;
        if (parens)
        {
            inExtends = false;
            WritePunctuation("(");
        }

        switch (node.Kind)
        {
            // Keyword Types
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.IntrinsicKeyword:
                EmitKeywordTypeNode(node.AsKeywordTypeNode());
                break;

            // Types
            case SyntaxKind.TypePredicate:
                EmitTypePredicate(node.AsTypePredicateNode());
                break;
            case SyntaxKind.TypeReference:
                EmitTypeReference(node.AsTypeReferenceNode());
                break;
            case SyntaxKind.FunctionType:
                EmitFunctionType(node.AsFunctionTypeNode());
                break;
            case SyntaxKind.ConstructorType:
                EmitConstructorType(node.AsConstructorTypeNode());
                break;
            case SyntaxKind.TypeQuery:
                EmitTypeQuery(node.AsTypeQueryNode());
                break;
            case SyntaxKind.TypeLiteral:
                EmitTypeLiteral(node.AsTypeLiteralNode());
                break;
            case SyntaxKind.ArrayType:
                EmitArrayType(node.AsArrayTypeNode());
                break;
            case SyntaxKind.TupleType:
                EmitTupleType(node.AsTupleTypeNode());
                break;
            case SyntaxKind.OptionalType:
                EmitOptionalType(node.AsOptionalTypeNode());
                break;
            case SyntaxKind.RestType:
                EmitRestType(node.AsRestTypeNode());
                break;
            case SyntaxKind.UnionType:
                EmitUnionType(node.AsUnionTypeNode());
                break;
            case SyntaxKind.IntersectionType:
                EmitIntersectionType(node.AsIntersectionTypeNode());
                break;
            case SyntaxKind.ConditionalType:
                EmitConditionalType(node.AsConditionalTypeNode());
                break;
            case SyntaxKind.InferType:
                EmitInferType(node.AsInferTypeNode());
                break;
            case SyntaxKind.ParenthesizedType:
                EmitParenthesizedType(node.AsParenthesizedTypeNode());
                break;
            case SyntaxKind.ThisType:
                EmitThisType(node.AsThisTypeNode());
                break;
            case SyntaxKind.TypeOperator:
                EmitTypeOperator(node.AsTypeOperatorNode());
                break;
            case SyntaxKind.IndexedAccessType:
                EmitIndexedAccessType(node.AsIndexedAccessTypeNode());
                break;
            case SyntaxKind.MappedType:
                EmitMappedType(node.AsMappedTypeNode());
                break;
            case SyntaxKind.LiteralType:
                EmitLiteralType(node.AsLiteralTypeNode());
                break;
            case SyntaxKind.NamedTupleMember:
                EmitNamedTupleMember(node.AsNamedTupleMember());
                break;
            case SyntaxKind.TemplateLiteralType:
                EmitTemplateType(node.AsTemplateLiteralTypeNode());
                break;
            case SyntaxKind.TemplateLiteralTypeSpan:
                EmitTemplateTypeSpan(node.AsTemplateLiteralTypeSpan());
                break;
            case SyntaxKind.ImportType:
                EmitImportTypeNode(node.AsImportTypeNode());
                break;

            case SyntaxKind.PropertyAccessExpression:
                EmitPropertyAccessExpression(node.AsPropertyAccessExpression());
                break;
            case SyntaxKind.ExpressionWithTypeArguments:
                EmitExpressionWithTypeArguments(node.AsExpressionWithTypeArguments());
                break;

            case SyntaxKind.JSDocAllType:
                EmitJSDocAllType(node);
                break;
            case SyntaxKind.JSDocNonNullableType:
                EmitJSDocNonNullableType(node.AsJSDocNonNullableType());
                break;
            case SyntaxKind.JSDocNullableType:
                EmitJSDocNullableType(node.AsJSDocNullableType());
                break;
            case SyntaxKind.JSDocOptionalType:
                EmitJSDocOptionalType(node.AsJSDocOptionalType());
                break;
            case SyntaxKind.JSDocVariadicType:
                EmitJSDocVariadicType(node.AsJSDocVariadicType());
                break;

            default:
                throw new InvalidOperationException($"unhandled TypeNode: {node.Kind}");
        }

        if (parens)
        {
            WritePunctuation(")");
        }

        inExtends = savedInExtends;
    }

    //
    // Binding patterns
    //

    private void EmitObjectBindingPattern(BindingPattern node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("{");
        EmitList(static (p, n) => p.EmitBindingElementNode(n), node.AsNode(), node.Elements, ListFormat.ObjectBindingPatternElements);
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitArrayBindingPattern(BindingPattern node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("[");
        EmitList(static (p, n) => p.EmitBindingElementNode(n), node.AsNode(), node.Elements, ListFormat.ArrayBindingPatternElements);
        WritePunctuation("]");
        ExitNode(node.AsNode(), state);
    }

    private void EmitBindingElement(BindingElement node)
    {
        var state = EnterNode(node.AsNode());
        EmitTokenNode(node.DotDotDotToken);
        if (node.PropertyName is not null)
        {
            EmitPropertyName(node.PropertyName);
            WritePunctuation(":");
            WriteSpace();
        }
        // Old parser used `OmittedExpression` as a substitute for `Elision`. New parser uses a `BindingElement` with nil members
        if (node.Name() is { } name)
        {
            EmitBindingName(name);
            EmitInitializer(node.Initializer, node.Name()!.End, node.AsNode());
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitBindingElementNode(Node node)
    {
        EmitBindingElement(node.AsBindingElement());
    }

    private void EmitJSDocAllType(Node node)
    {
        EmitKeywordNode(node);
    }

    private void EmitJSDocNonNullableType(JSDocNonNullableType node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("!");
        EmitTypeNode(node.Type!, TypePrecedence.NonArray);
        ExitNode(node.AsNode(), state);
    }

    private void EmitJSDocNullableType(JSDocNullableType node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("?");
        EmitTypeNode(node.Type!, TypePrecedence.NonArray);
        ExitNode(node.AsNode(), state);
    }

    private void EmitJSDocOptionalType(JSDocOptionalType node)
    {
        var state = EnterNode(node.AsNode());
        EmitTypeNode(node.Type!, TypePrecedence.JSDoc);
        WritePunctuation("=");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJSDocVariadicType(JSDocVariadicType node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("...");
        EmitTypeNode(node.Type!, TypePrecedence.JSDoc);
        ExitNode(node.AsNode(), state);
    }
}
