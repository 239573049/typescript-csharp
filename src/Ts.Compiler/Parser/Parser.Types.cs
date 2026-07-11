// Ported from tsgo internal/parser/parser.go (type-grammar cluster) and the two
// JSDoc type helpers from internal/parser/jsdoc.go (tryParseTypeExpression,
// parseNestedTypeLiteral). Positions are UTF-16 code units (plan/01-architecture.md §6).
//
// PORT-NOTE: parseUnionOrIntersectionType and parseFunctionOrConstructorTypeToError have
// no genstubs entry in any cluster; they are pure type-grammar helpers reached only from
// this cluster (parseUnion/parseIntersection), so they live here. The Go func-pointer
// parameter `parseConstituentType func(p *Parser) *ast.TypeNode` becomes a Func<Node>.
#nullable enable
using System;
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    // ── Test seam (phase-3 Types cluster) ─────────────────────────────────────
    // Parses a single standalone type from source text for unit tests. It enters the
    // grammar at parseUnionTypeOrHigher — the production parseType() itself delegates to for
    // every non-function, non-conditional type — because parseType()'s first act is the
    // function/constructor-type probe isStartOfFunctionTypeOrConstructorType(), which is a
    // NotImplementedException stub owned by the parallel Declarations cluster. This seam
    // therefore exercises the keyword / type-operator (keyof·unique·readonly) / array /
    // postfix(!·?) / this-type shapes whose parse paths are fully contained in the Types
    // cluster plus the already-ported scanner, factory and predicate core. Constructs that
    // reach still-stubbed clusters (type references, tuples, mapped/conditional/template
    // types, multi-constituent unions/intersections, literal types) are covered once those
    // clusters land; see ParserTypeTests for the covered surface and blocked cases.
    internal static Node ParseTypeForTest(string sourceText)
    {
        return LargeStackExecutor.Run(() =>
        {
            Parser p = GetParser();
            try
            {
                p.InitializeState(default, sourceText, ScriptKind.TS);
                p.NextToken();
                return p.ParseUnionTypeOrHigher();
            }
            finally
            {
                PutParser(p);
            }
        });
    }

    // parser.go:1709
    private Node? ParseTypeAnnotation()
    {
        if (ParseOptional(SyntaxKind.ColonToken))
        {
            return ParseType();
        }
        return null;
    }

    // parser.go:2611
    private Node ParseType()
    {
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.TypeExcludesFlags, false);
        Node typeNode;
        if (IsStartOfFunctionTypeOrConstructorType())
        {
            typeNode = ParseFunctionOrConstructorType();
        }
        else
        {
            int pos = NodePos();
            typeNode = ParseUnionTypeOrHigher();
            if (!InDisallowConditionalTypesContext() && !HasPrecedingLineBreak() && ParseOptional(SyntaxKind.ExtendsKeyword))
            {
                // The type following 'extends' is not permitted to be another conditional type
                Node extendsType = DoInContext(NodeFlags.DisallowConditionalTypesContext, true, ParseType);
                ParseExpected(SyntaxKind.QuestionToken);
                Node trueType = DoInContext(NodeFlags.DisallowConditionalTypesContext, false, ParseType);
                ParseExpected(SyntaxKind.ColonToken);
                Node falseType = DoInContext(NodeFlags.DisallowConditionalTypesContext, false, ParseType);
                Node conditionalType = factory.NewConditionalTypeNode(typeNode, extendsType, trueType, falseType);
                FinishNode(conditionalType, pos);
                typeNode = conditionalType;
            }
        }
        contextFlags = saveContextFlags;
        return typeNode;
    }

    // parser.go:2636
    private Node ParseUnionTypeOrHigher() =>
        ParseUnionOrIntersectionType(SyntaxKind.BarToken, ParseIntersectionTypeOrHigher);

    // parser.go:2640
    private Node ParseIntersectionTypeOrHigher() =>
        ParseUnionOrIntersectionType(SyntaxKind.AmpersandToken, ParseTypeOperatorOrHigher);

    // parser.go:2644
    private Node ParseUnionOrIntersectionType(SyntaxKind @operator, Func<Node> parseConstituentType)
    {
        int pos = NodePos();
        bool isUnionType = @operator == SyntaxKind.BarToken;
        bool hasLeadingOperator = ParseOptional(@operator);
        Node typeNode;
        if (hasLeadingOperator)
        {
            typeNode = ParseFunctionOrConstructorTypeToError(isUnionType, parseConstituentType);
        }
        else
        {
            typeNode = parseConstituentType();
        }
        if (token == @operator || hasLeadingOperator)
        {
            var types = new List<Node> { typeNode };
            while (ParseOptional(@operator))
            {
                types.Add(ParseFunctionOrConstructorTypeToError(isUnionType, parseConstituentType));
            }
            typeNode = CreateUnionOrIntersectionTypeNode(@operator, NewNodeList(new TextRange(pos, NodePos()), types.ToArray()));
            FinishNode(typeNode, pos);
        }
        return typeNode;
    }

    // parser.go:2666
    private Node CreateUnionOrIntersectionTypeNode(SyntaxKind @operator, NodeList? types)
    {
        switch (@operator)
        {
            case SyntaxKind.BarToken:
                return factory.NewUnionTypeNode(types);
            case SyntaxKind.AmpersandToken:
                return factory.NewIntersectionTypeNode(types);
            default:
                throw new InvalidOperationException("Unhandled case in createUnionOrIntersectionType");
        }
    }

    // parser.go:2677
    private Node ParseTypeOperatorOrHigher()
    {
        SyntaxKind @operator = token;
        switch (@operator)
        {
            case SyntaxKind.KeyOfKeyword:
            case SyntaxKind.UniqueKeyword:
            case SyntaxKind.ReadonlyKeyword:
                return ParseTypeOperator(@operator);
            case SyntaxKind.InferKeyword:
                return ParseInferType();
        }
        return DoInContext(NodeFlags.DisallowConditionalTypesContext, false, ParsePostfixTypeOrHigher);
    }

    // parser.go:2688
    private Node ParseTypeOperator(SyntaxKind @operator)
    {
        int pos = NodePos();
        ParseExpected(@operator);
        return FinishNode(factory.NewTypeOperatorNode(@operator, ParseTypeOperatorOrHigher()), pos);
    }

    // parser.go:2694
    private Node ParseInferType()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.InferKeyword);
        return FinishNode(factory.NewInferTypeNode(ParseTypeParameterOfInferType()), pos);
    }

    // parser.go:2707
    private Node? TryParseConstraintOfInferType()
    {
        ParserState state = Mark();
        if (ParseOptional(SyntaxKind.ExtendsKeyword))
        {
            Node constraint = DoInContext(NodeFlags.DisallowConditionalTypesContext, true, ParseType);
            if (InDisallowConditionalTypesContext() || token != SyntaxKind.QuestionToken)
            {
                return constraint;
            }
        }
        Rewind(state);
        return null;
    }

    // parser.go:2719
    private Node ParsePostfixTypeOrHigher()
    {
        int pos = NodePos();
        Node typeNode = ParseNonArrayType();
        while (!HasPrecedingLineBreak())
        {
            switch (token)
            {
                case SyntaxKind.ExclamationToken:
                    NextToken();
                    typeNode = FinishNode(factory.NewJSDocNonNullableType(typeNode), pos);
                    break;
                case SyntaxKind.QuestionToken:
                    // If next token is start of a type we have a conditional type
                    if (LookAhead(NextIsStartOfType))
                    {
                        return typeNode;
                    }
                    NextToken();
                    typeNode = FinishNode(factory.NewJSDocNullableType(typeNode), pos);
                    break;
                case SyntaxKind.OpenBracketToken:
                    ParseExpected(SyntaxKind.OpenBracketToken);
                    if (IsStartOfType(false))
                    {
                        Node indexType = ParseType();
                        ParseExpected(SyntaxKind.CloseBracketToken);
                        typeNode = FinishNode(factory.NewIndexedAccessTypeNode(typeNode, indexType), pos);
                    }
                    else
                    {
                        ParseExpected(SyntaxKind.CloseBracketToken);
                        typeNode = FinishNode(factory.NewArrayTypeNode(typeNode), pos);
                    }
                    break;
                default:
                    return typeNode;
            }
        }
        return typeNode;
    }

    // parser.go:2751
    private bool NextIsStartOfType()
    {
        NextToken();
        return IsStartOfType(false);
    }

    // parser.go:2756
    private Node ParseNonArrayType()
    {
        switch (token)
        {
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.ObjectKeyword:
            {
                ParserState state = Mark();
                Node keywordTypeNode = ParseKeywordTypeNode();
                // If these are followed by a dot then parse these out as a dotted type reference instead
                if (token != SyntaxKind.DotToken)
                {
                    return keywordTypeNode;
                }
                Rewind(state);
                return ParseTypeReference();
            }
            case SyntaxKind.AsteriskEqualsToken:
                // If there is '*=', treat it as * followed by postfix =
                scanner.ReScanAsteriskEqualsToken();
                return ParseJSDocAllType(); // fallthrough to KindAsteriskToken
            case SyntaxKind.AsteriskToken:
                return ParseJSDocAllType();
            case SyntaxKind.QuestionQuestionToken:
                // If there is '??', treat it as prefix-'?' in JSDoc type.
                scanner.ReScanQuestionToken();
                return ParseJSDocNullableType(); // fallthrough to KindQuestionToken
            case SyntaxKind.QuestionToken:
                return ParseJSDocNullableType();
            case SyntaxKind.ExclamationToken:
                return ParseJSDocNonNullableType();
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
                return ParseLiteralTypeNode(false);
            case SyntaxKind.MinusToken:
                if (LookAhead(NextTokenIsNumericOrBigIntLiteral))
                {
                    return ParseLiteralTypeNode(true);
                }
                return ParseTypeReference();
            case SyntaxKind.VoidKeyword:
                return ParseKeywordTypeNode();
            case SyntaxKind.ThisKeyword:
            {
                Node thisKeyword = ParseThisTypeNode();
                if (token == SyntaxKind.IsKeyword && !HasPrecedingLineBreak())
                {
                    return ParseThisTypePredicate(thisKeyword);
                }
                return thisKeyword;
            }
            case SyntaxKind.TypeOfKeyword:
                if (LookAhead(NextIsStartOfTypeOfImportType))
                {
                    return ParseImportType();
                }
                return ParseTypeQuery();
            case SyntaxKind.OpenBraceToken:
                if (LookAhead(NextIsStartOfMappedType))
                {
                    return ParseMappedType();
                }
                return ParseTypeLiteral();
            case SyntaxKind.OpenBracketToken:
                return ParseTupleType();
            case SyntaxKind.OpenParenToken:
                return ParseParenthesizedType();
            case SyntaxKind.ImportKeyword:
                return ParseImportType();
            case SyntaxKind.AssertsKeyword:
                if (LookAhead(NextTokenIsIdentifierOrKeywordOnSameLine))
                {
                    return ParseAssertsTypePredicate();
                }
                return ParseTypeReference();
            case SyntaxKind.TemplateHead:
                return ParseTemplateType();
            default:
                return ParseTypeReference();
        }
    }

    // parser.go:2826
    private Node ParseKeywordTypeNode()
    {
        int pos = NodePos();
        Node result = factory.NewKeywordTypeNode(token);
        NextToken();
        return FinishNode(result, pos);
    }

    // parser.go:2833
    private Node ParseThisTypeNode()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewThisTypeNode(), pos);
    }

    // parser.go:2839
    private Node ParseThisTypePredicate(Node? lhs)
    {
        NextToken();
        return FinishNode(factory.NewTypePredicateNode(null /*assertsModifier*/, lhs, ParseType()), lhs!.Pos);
    }

    // parser.go:2880
    private Node ParseLiteralTypeNode(bool negative)
    {
        int pos = NodePos();
        if (negative)
        {
            NextToken();
        }
        Node expression;
        if (token == SyntaxKind.TrueKeyword || token == SyntaxKind.FalseKeyword || token == SyntaxKind.NullKeyword)
        {
            expression = ParseKeywordExpression();
        }
        else
        {
            expression = ParseLiteralExpression(false /*intern*/);
        }
        if (negative)
        {
            expression = FinishNode(factory.NewPrefixUnaryExpression(SyntaxKind.MinusToken, expression), pos);
        }
        return FinishNode(factory.NewLiteralTypeNode(expression), pos);
    }

    // parser.go:2897
    private Node ParseTypeReference()
    {
        int pos = NodePos();
        return FinishNode(factory.NewTypeReferenceNode(ParseEntityNameOfTypeReference(), ParseTypeArgumentsOfTypeReference()), pos);
    }

    // parser.go:2902
    private Node ParseEntityNameOfTypeReference() =>
        ParseEntityName(true /*allowReservedWords*/, global::Ts.Diagnostics.Diagnostics.Type_expected);

    // parser.go:3116
    private Node ParseTypeQuery()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.TypeOfKeyword);
        Node entityName = ParseEntityName(true /*allowReservedWords*/, null);
        // Make sure we perform ASI to prevent parsing the next line's type arguments as part of an instantiation expression
        NodeList? typeArguments = null;
        if (!HasPrecedingLineBreak())
        {
            typeArguments = ParseTypeArguments();
        }
        return FinishNode(factory.NewTypeQueryNode(entityName, typeArguments), pos);
    }

    // parser.go:3128
    private bool NextIsStartOfMappedType()
    {
        NextToken();
        if (token == SyntaxKind.PlusToken || token == SyntaxKind.MinusToken)
        {
            return NextToken() == SyntaxKind.ReadonlyKeyword;
        }
        if (token == SyntaxKind.ReadonlyKeyword)
        {
            NextToken();
        }
        return token == SyntaxKind.OpenBracketToken && NextTokenIsIdentifier() && NextToken() == SyntaxKind.InKeyword;
    }

    // parser.go:3139
    private Node ParseMappedType()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.OpenBraceToken);
        Node? readonlyToken = null; // ReadonlyKeyword | PlusToken | MinusToken
        if (token == SyntaxKind.ReadonlyKeyword || token == SyntaxKind.PlusToken || token == SyntaxKind.MinusToken)
        {
            readonlyToken = ParseTokenNode();
            if (readonlyToken.Kind != SyntaxKind.ReadonlyKeyword)
            {
                ParseExpected(SyntaxKind.ReadonlyKeyword);
            }
        }
        ParseExpected(SyntaxKind.OpenBracketToken);
        Node typeParameter = ParseMappedTypeParameter();
        Node? nameType = null;
        if (ParseOptional(SyntaxKind.AsKeyword))
        {
            nameType = ParseType();
        }
        ParseExpected(SyntaxKind.CloseBracketToken);
        Node? questionToken = null; // QuestionToken | PlusToken | MinusToken
        if (token == SyntaxKind.QuestionToken || token == SyntaxKind.PlusToken || token == SyntaxKind.MinusToken)
        {
            questionToken = ParseTokenNode();
            if (questionToken.Kind != SyntaxKind.QuestionToken)
            {
                ParseExpected(SyntaxKind.QuestionToken);
            }
        }
        Node? typeNode = ParseTypeAnnotation();
        ParseSemicolon();
        NodeList members = ParseList(ParsingContext.TypeMembers, ParseTypeMember);
        ParseExpected(SyntaxKind.CloseBraceToken);
        return FinishNode(factory.NewMappedTypeNode(readonlyToken, typeParameter, nameType, questionToken, typeNode, members), pos);
    }

    // parser.go:3386
    private Node? ParseReturnType(SyntaxKind returnToken, bool isType)
    {
        if (ShouldParseReturnType(returnToken, isType))
        {
            return DoInContext(NodeFlags.DisallowConditionalTypesContext, false, ParseTypeOrTypePredicate);
        }
        return null;
    }

    // parser.go:3393
    private bool ShouldParseReturnType(SyntaxKind returnToken, bool isType)
    {
        if (returnToken == SyntaxKind.EqualsGreaterThanToken)
        {
            ParseExpected(returnToken);
            return true;
        }
        else if (ParseOptional(SyntaxKind.ColonToken))
        {
            return true;
        }
        else if (isType && token == SyntaxKind.EqualsGreaterThanToken)
        {
            // This is easy to get backward, especially in type contexts, so parse the type anyway
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.ColonToken));
            NextToken();
            return true;
        }
        return false;
    }

    // parser.go:3408
    private Node ParseTypeOrTypePredicate()
    {
        if (IsIdentifier())
        {
            ParserState state = Mark();
            int pos = NodePos();
            Node id = ParseIdentifier();
            if (token == SyntaxKind.IsKeyword && !HasPrecedingLineBreak())
            {
                NextToken();
                return FinishNode(factory.NewTypePredicateNode(null /*assertsModifier*/, id, ParseType()), pos);
            }
            Rewind(state);
        }
        return ParseType();
    }

    // parser.go:3604
    private Node ParseTypeLiteral()
    {
        int pos = NodePos();
        Node result = FinishNode(factory.NewTypeLiteralNode(ParseObjectTypeMembers()), pos);
        return result;
    }

    // parser.go:3619
    private Node ParseTupleType()
    {
        int pos = NodePos();
        return FinishNode(factory.NewTupleTypeNode(ParseBracketedList(ParsingContext.TupleElementTypes, ParseTupleElementNameOrTupleElementType, SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken)), pos);
    }

    // parser.go:3624
    private Node ParseTupleElementNameOrTupleElementType()
    {
        if (LookAhead(ScanStartOfNamedTupleElement))
        {
            int pos = NodePos();
            JSDocScannerInfo jsdoc = JsdocScannerInfo();
            Node? dotDotDotToken = ParseOptionalToken(SyntaxKind.DotDotDotToken);
            Node name = ParseIdentifierName();
            Node? questionToken = ParseOptionalToken(SyntaxKind.QuestionToken);
            ParseExpected(SyntaxKind.ColonToken);
            Node typeNode = ParseTupleElementType();
            Node result = FinishNode(factory.NewNamedTupleMember(dotDotDotToken, name, questionToken, typeNode), pos);
            WithJSDoc(result, jsdoc);
            return result;
        }
        return ParseTupleElementType();
    }

    // parser.go:3651
    private Node ParseTupleElementType()
    {
        int pos = NodePos();
        if (ParseOptional(SyntaxKind.DotDotDotToken))
        {
            return FinishNode(factory.NewRestTypeNode(ParseType()), pos);
        }
        Node typeNode = ParseType();
        if (NodeTests.IsJSDocNullableType(typeNode))
        {
            Node inner = typeNode.AsJSDocNullableType().Type!;
            if (typeNode.Pos == inner.Pos)
            {
                Node node = factory.NewOptionalTypeNode(inner);
                node.Flags = typeNode.Flags;
                node.Loc = typeNode.Loc;
                inner.Parent = node;
                return node;
            }
        }
        return typeNode;
    }

    // parser.go:3667
    private Node ParseParenthesizedType()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.OpenParenToken);
        Node typeNode = ParseType();
        ParseExpected(SyntaxKind.CloseParenToken);
        return FinishNode(factory.NewParenthesizedTypeNode(typeNode), pos);
    }

    // parser.go:3675
    private Node ParseAssertsTypePredicate()
    {
        int pos = NodePos();
        Node assertsModifier = ParseExpectedToken(SyntaxKind.AssertsKeyword);
        Node parameterName;
        if (token == SyntaxKind.ThisKeyword)
        {
            parameterName = ParseThisTypeNode();
        }
        else
        {
            parameterName = ParseIdentifier();
        }
        Node? typeNode = null;
        if (ParseOptional(SyntaxKind.IsKeyword))
        {
            typeNode = ParseType();
        }
        return FinishNode(factory.NewTypePredicateNode(assertsModifier, parameterName, typeNode), pos);
    }

    // parser.go:3691
    private Node ParseTemplateType()
    {
        int pos = NodePos();
        return FinishNode(factory.NewTemplateLiteralTypeNode(ParseTemplateHead(false /*isTaggedTemplate*/), ParseTemplateTypeSpans()), pos);
    }

    // parser.go:3714
    private NodeList ParseTemplateTypeSpans()
    {
        int pos = NodePos();
        var list = new List<Node>();
        while (true)
        {
            Node span = ParseTemplateTypeSpan();
            list.Add(span);
            if (span.AsTemplateLiteralTypeSpan().Literal!.Kind != SyntaxKind.TemplateMiddle)
            {
                break;
            }
        }
        return NewNodeList(new TextRange(pos, NodePos()), list.ToArray());
    }

    // parser.go:3727
    private Node ParseTemplateTypeSpan()
    {
        int pos = NodePos();
        return FinishNode(factory.NewTemplateLiteralTypeSpan(ParseType(), ParseLiteralOfTemplateSpan(false /*isTaggedTemplate*/)), pos);
    }

    // parser.go:3753
    private Node ParseFunctionOrConstructorTypeToError(bool isInUnionType, Func<Node> parseConstituentType)
    {
        // the function type and constructor type shorthand notation
        // are not allowed directly in unions and intersections, but we'll
        // try to parse them gracefully and issue a helpful message.
        if (IsStartOfFunctionTypeOrConstructorType())
        {
            Node typeNode = ParseFunctionOrConstructorType();
            Message diagnostic;
            if (typeNode.Kind == SyntaxKind.FunctionType)
            {
                diagnostic = CoreUtil.IfElse(isInUnionType,
                    global::Ts.Diagnostics.Diagnostics.Function_type_notation_must_be_parenthesized_when_used_in_a_union_type,
                    global::Ts.Diagnostics.Diagnostics.Function_type_notation_must_be_parenthesized_when_used_in_an_intersection_type);
            }
            else
            {
                diagnostic = CoreUtil.IfElse(isInUnionType,
                    global::Ts.Diagnostics.Diagnostics.Constructor_type_notation_must_be_parenthesized_when_used_in_a_union_type,
                    global::Ts.Diagnostics.Diagnostics.Constructor_type_notation_must_be_parenthesized_when_used_in_an_intersection_type);
            }
            ParseErrorAtRange(typeNode.Loc, diagnostic);
            return typeNode;
        }
        return parseConstituentType();
    }

    // parser.go:3817
    private bool NextIsUnambiguouslyStartOfFunctionType()
    {
        NextToken();
        if (token == SyntaxKind.CloseParenToken || token == SyntaxKind.DotDotDotToken)
        {
            // ( )
            // ( ...
            return true;
        }
        if (SkipParameterStart())
        {
            // We successfully skipped modifiers (if any) and an identifier or binding pattern,
            // now see if we have something that indicates a parameter declaration
            if (token == SyntaxKind.ColonToken || token == SyntaxKind.CommaToken || token == SyntaxKind.QuestionToken || token == SyntaxKind.EqualsToken)
            {
                // ( xxx :
                // ( xxx ,
                // ( xxx ?
                // ( xxx =
                return true;
            }
            if (token == SyntaxKind.CloseParenToken && NextToken() == SyntaxKind.EqualsGreaterThanToken)
            {
                // ( xxx ) =>
                return true;
            }
        }
        return false;
    }

    // parser.go:5108
    private Node ParseTypeOfExpression()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewTypeOfExpression(ParseSimpleUnaryExpression()), pos);
    }

    // parser.go:5137
    private Node ParseTypeAssertion()
    {
        Debug.Assert(languageVariant != LanguageVariant.Jsx, "Type assertions should never be parsed in JSX; they should be parsed as comparisons or JSX elements/fragments.");
        int pos = NodePos();
        ParseExpected(SyntaxKind.LessThanToken);
        Node typeNode = ParseType();
        ParseExpected(SyntaxKind.GreaterThanToken);
        Node expression = ParseSimpleUnaryExpression();
        return FinishNode(factory.NewTypeAssertion(typeNode, expression), pos);
    }

    // parser.go:6210
    private bool IsStartOfType(bool inStartOfParameter)
    {
        switch (token)
        {
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.ReadonlyKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.UniqueKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.TypeOfKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.OpenBracketToken:
            case SyntaxKind.LessThanToken:
            case SyntaxKind.BarToken:
            case SyntaxKind.AmpersandToken:
            case SyntaxKind.NewKeyword:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.AsteriskToken:
            case SyntaxKind.QuestionToken:
            case SyntaxKind.ExclamationToken:
            case SyntaxKind.DotDotDotToken:
            case SyntaxKind.InferKeyword:
            case SyntaxKind.ImportKeyword:
            case SyntaxKind.AssertsKeyword:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
                return true;
            case SyntaxKind.FunctionKeyword:
                return !inStartOfParameter;
            case SyntaxKind.MinusToken:
                return !inStartOfParameter && LookAhead(NextTokenIsNumericOrBigIntLiteral);
            case SyntaxKind.OpenParenToken:
                // Only consider '(' the start of a type if followed by ')', '...', an identifier, a modifier,
                // or something that starts a type. We don't want to consider things like '(1)' a type.
                return !inStartOfParameter && LookAhead(NextIsParenthesizedOrFunctionType);
        }
        return IsIdentifier();
    }

    // parser.go:6238
    private bool NextIsParenthesizedOrFunctionType()
    {
        NextToken();
        return token == SyntaxKind.CloseParenToken || IsStartOfParameter(false /*isJSDocParameter*/) || IsStartOfType(false /*inStartOfParameter*/);
    }

    // jsdoc.go:784
    private Node? TryParseTypeExpression()
    {
        SkipWhitespaceOrAsterisk();
        if (token == SyntaxKind.OpenBraceToken)
        {
            return ParseJSDocTypeExpression(false /*mayOmitBraces*/);
        }
        else
        {
            return null;
        }
    }

    // jsdoc.go:818
    private static bool IsObjectOrObjectArrayTypeReference(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ObjectKeyword:
                return true;
            case SyntaxKind.ArrayType:
                return IsObjectOrObjectArrayTypeReference(node.AsArrayTypeNode().ElementType!);
            default:
                if (NodeTests.IsTypeReferenceNode(node))
                {
                    TypeReferenceNode reference = node.AsTypeReferenceNode();
                    return NodeTests.IsIdentifier(reference.TypeName!) && reference.TypeName!.AsIdentifier().Text == "Object" && reference.TypeArguments == null;
                }
                return false;
        }
    }

    // jsdoc.go:858
    private Node? ParseNestedTypeLiteral(Node? typeExpression, Node? name, PropertyLikeParse target, int indent)
    {
        if (typeExpression != null && IsObjectOrObjectArrayTypeReference(typeExpression.AsJSDocTypeExpression().Type!))
        {
            int pos = NodePos();
            List<Node>? children = null;
            while (true)
            {
                ParserState state = Mark();
                Node? child = ParseChildParameterOrPropertyTag(target, indent, name);
                if (child == null)
                {
                    Rewind(state);
                    break;
                }
                switch (child.Kind)
                {
                    case SyntaxKind.JSDocParameterTag:
                    case SyntaxKind.JSDocPropertyTag:
                        children ??= new List<Node>();
                        children.Add(child);
                        break;
                    case SyntaxKind.JSDocTemplateTag:
                        ParseErrorAtRange(child.TagName()!.Loc, global::Ts.Diagnostics.Diagnostics.A_JSDoc_template_tag_may_not_follow_a_typedef_callback_or_overload_tag);
                        break;
                }
            }
            if (children != null)
            {
                Node literal = FinishNode(factory.NewJSDocTypeLiteral(children.ToArray(), typeExpression.AsJSDocTypeExpression().Type!.Kind == SyntaxKind.ArrayType), pos);
                return FinishNode(factory.NewJSDocTypeExpression(literal), pos);
            }
        }
        return null;
    }
}
