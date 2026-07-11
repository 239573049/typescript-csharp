// Ported from tsgo internal/parser/parser.go — Expressions cluster (assignment/binary/unary
// precedence ladder, call/member/optional chains, templates, array/object literals, arguments,
// and the associated start-of/next-token predicates). Positions are UTF-16 code units (plan/01).
// Deep recursion is reached only under the LargeStackExecutor entry (Parser.ParseSourceFile, R01).
//
// Methods for other clusters (Statements/Types/Declarations/Jsx/JsDoc) referenced here are
// implemented in their own Parser.*.cs partials; this file only calls them.
using System;
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    // ── Test seam (Ts.Compiler.Tests via InternalsVisibleTo; 05-conventions.md §16) ──
    // The public ParseSourceFile entry routes through statement parsing, and the full assignment/
    // binary ladder recurses into arrow-function / type-argument / optional-chain helpers that are
    // still NotImplementedException stubs in the sibling Declarations/Statements/Types clusters.
    // This shallow hook exercises the primary-expression surface (literals, keyword expressions,
    // identifiers, private identifiers, empty array/object literals) directly, without crossing into
    // those stubs. Runs on the 64MB large-stack thread to honor the deep-recursion entry rule
    // (AGENTS §8 / R01). Delete once the cluster's integration path is unblocked.
    internal static (Node Node, Diagnostic[] Diagnostics) ParsePrimaryExpressionForTest(string sourceText, ScriptKind scriptKind = ScriptKind.TS)
    {
        return LargeStackExecutor.Run(() =>
        {
            Parser p = GetParser();
            try
            {
                p.InitializeState(default, sourceText, scriptKind);
                p.NextToken();
                Node node = p.ParsePrimaryExpression();
                return (node, p.diagnostics.ToArray());
            }
            finally
            {
                PutParser(p);
            }
        });
    }

    private Node ParseExpression()
    {
        // Expression[in]:
        //      AssignmentExpression[in]
        //      Expression[in] , AssignmentExpression[in]

        // clear the decorator context when parsing Expression, as it should be unambiguous when parsing a decorator
        NodeFlags saveContextFlags = contextFlags;
        contextFlags &= ~NodeFlags.DecoratorContext;
        int pos = NodePos();
        Node expr = ParseAssignmentExpressionOrHigher();
        while (true)
        {
            Node? operatorToken = ParseOptionalToken(SyntaxKind.CommaToken);
            if (operatorToken == null)
            {
                break;
            }
            expr = MakeBinaryExpression(expr, operatorToken, ParseAssignmentExpressionOrHigher(), pos);
        }
        contextFlags = saveContextFlags;
        return expr;
    }

    private Node ParseExpressionAllowIn() =>
        DoInContext(NodeFlags.DisallowInContext, false, ParseExpression);

    private Node ParseAssignmentExpressionOrHigher() =>
        ParseAssignmentExpressionOrHigherWorker(true /*allowReturnTypeInArrowFunction*/);

    private Node ParseAssignmentExpressionOrHigherWorker(bool allowReturnTypeInArrowFunction)
    {
        //  AssignmentExpression[in,yield]:
        //      1) ConditionalExpression[?in,?yield]
        //      2) LeftHandSideExpression = AssignmentExpression[?in,?yield]
        //      3) LeftHandSideExpression AssignmentOperator AssignmentExpression[?in,?yield]
        //      4) ArrowFunctionExpression[?in,?yield]
        //      5) AsyncArrowFunctionExpression[in,yield,await]
        //      6) [+Yield] YieldExpression[?In]
        //
        // Note: for ease of implementation we treat productions '2' and '3' as the same thing.
        // First, do the simple check if we have a YieldExpression (production '6').
        if (IsYieldExpression())
        {
            return ParseYieldExpression();
        }
        // Then, check if we have an arrow function (production '4' and '5') that starts with a parenthesized
        // parameter list or is an async arrow function.
        Node? arrowExpression = TryParseParenthesizedArrowFunctionExpression(allowReturnTypeInArrowFunction);
        if (arrowExpression != null)
        {
            return arrowExpression;
        }
        arrowExpression = TryParseAsyncSimpleArrowFunctionExpression(allowReturnTypeInArrowFunction);
        if (arrowExpression != null)
        {
            return arrowExpression;
        }
        // Now try to see if we're in production '1', '2' or '3'.  A conditional expression can
        // start with a LogicalOrExpression, while the assignment productions can only start with
        // LeftHandSideExpressions.
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        Node expr = ParseBinaryExpressionOrHigher(OperatorPrecedence.Lowest);
        // To avoid a look-ahead, we did not handle the case of an arrow function with a single un-parenthesized
        // parameter ('x => ...') above. We handle it here by checking if the parsed expression was a single
        // identifier and the current token is an arrow.
        if (expr.Kind == SyntaxKind.Identifier && token == SyntaxKind.EqualsGreaterThanToken)
        {
            return ParseSimpleArrowFunctionExpression(pos, expr, allowReturnTypeInArrowFunction, jsdoc, null /*asyncModifier*/);
        }
        // Now see if we might be in cases '2' or '3'.
        // Note: we call reScanGreaterToken so that we get an appropriately merged token
        // for cases like `> > =` becoming `>>=`
        if (AstUtilities.IsLeftHandSideExpression(expr) && NodeTests.IsAssignmentOperator(ReScanGreaterThanToken()))
        {
            return MakeBinaryExpression(expr, ParseTokenNode(), ParseAssignmentExpressionOrHigherWorker(allowReturnTypeInArrowFunction), pos);
        }
        // It wasn't an assignment or a lambda.  This is a conditional expression:
        return ParseConditionalExpressionRest(expr, pos, allowReturnTypeInArrowFunction);
    }

    private bool IsYieldExpression()
    {
        if (token == SyntaxKind.YieldKeyword)
        {
            // If we have a 'yield' keyword, and this is a context where yield expressions are
            // allowed, then definitely parse out a yield expression.
            if (InYieldContext())
            {
                return true;
            }
            // for now we just check if the next token is an identifier.  More heuristics
            // can be added here later as necessary.
            return LookAhead(NextTokenIsIdentifierOrKeywordOrLiteralOnSameLine);
        }
        return false;
    }

    private Node ParseYieldExpression()
    {
        int pos = NodePos();
        // YieldExpression[In] :
        //      yield
        //      yield [no LineTerminator here] [Lexical goal InputElementRegExp]AssignmentExpression[?In, Yield]
        //      yield [no LineTerminator here] * [Lexical goal InputElementRegExp]AssignmentExpression[?In, Yield]
        NextToken();
        Node result;
        if (!HasPrecedingLineBreak() && (token == SyntaxKind.AsteriskToken || IsStartOfExpression()))
        {
            result = factory.NewYieldExpression(ParseOptionalToken(SyntaxKind.AsteriskToken), ParseAssignmentExpressionOrHigher());
        }
        else
        {
            // if the next token is not on the same line as yield.  or we don't have an '*' or
            // the start of an expression, then this is just a simple "yield" expression.
            result = factory.NewYieldExpression(null /*asteriskToken*/, null /*expression*/);
        }
        return FinishNode(result, pos);
    }

    private Node ParseConditionalExpressionRest(Node? leftOperand, int pos, bool allowReturnTypeInArrowFunction)
    {
        // Note: we are passed in an expression which was produced from parseBinaryExpressionOrHigher.
        Node? questionToken = ParseOptionalToken(SyntaxKind.QuestionToken);
        if (questionToken == null)
        {
            return leftOperand!;
        }
        // Note: we explicitly 'allowIn' in the whenTrue part of the condition expression, and
        // we do not that for the 'whenFalse' part.
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.DisallowInContext, false);
        Node trueExpression = ParseAssignmentExpressionOrHigherWorker(false /*allowReturnTypeInArrowFunction*/);
        contextFlags = saveContextFlags;
        Node colonToken = ParseExpectedToken(SyntaxKind.ColonToken);
        Node falseExpression;
        if (AstUtilities.NodeIsPresent(colonToken))
        {
            falseExpression = ParseAssignmentExpressionOrHigherWorker(allowReturnTypeInArrowFunction);
        }
        else
        {
            falseExpression = CreateMissingIdentifier();
        }
        return FinishNode(factory.NewConditionalExpression(leftOperand, questionToken, trueExpression, colonToken, falseExpression), pos);
    }

    private Node ParseBinaryExpressionOrHigher(OperatorPrecedence precedence)
    {
        int pos = NodePos();
        Node leftOperand = ParseUnaryExpressionOrHigher();
        return ParseBinaryExpressionRest(precedence, leftOperand, pos);
    }

    private Node ParseBinaryExpressionRest(OperatorPrecedence precedence, Node? leftOperand, int pos)
    {
        Node? lastOperand = leftOperand;
        while (true)
        {
            // We either have a binary operator here, or we're finished.  We call
            // reScanGreaterToken so that we merge token sequences like > and = into >=
            SyntaxKind operatorKind = ReScanGreaterThanToken();
            OperatorPrecedence newPrecedence = Precedence.GetBinaryOperatorPrecedence(operatorKind);
            // Check the precedence to see if we should "take" this operator
            // - For left associative operators (all but **), consume if newPrecedence >= precedence.
            // - For the right associative operator (**), consume if newPrecedence > precedence.
            bool consumeCurrentOperator;
            if (operatorKind == SyntaxKind.AsteriskAsteriskToken)
            {
                consumeCurrentOperator = newPrecedence >= precedence;
            }
            else
            {
                consumeCurrentOperator = newPrecedence > precedence;
            }
            if (!consumeCurrentOperator)
            {
                break;
            }
            if (operatorKind == SyntaxKind.InKeyword && InDisallowInContext())
            {
                break;
            }
            if (operatorKind == SyntaxKind.AsKeyword || operatorKind == SyntaxKind.SatisfiesKeyword)
            {
                // Make sure we *do* perform ASI for constructs like this:
                //    var x = foo
                //    as (Bar)
                // This should be parsed as an initialized variable, followed
                // by a function call to 'as' with the argument 'Bar'
                if (HasPrecedingLineBreak())
                {
                    break;
                }
                else
                {
                    NextToken();
                    // When we have 'a ## b as SomeType' or 'a ## b satisfies SomeType', where ## is some binary
                    // operator, we want to stop parsing on any following operator with a higher precedence than ##.
                    OperatorPrecedence lastPrecedence = OperatorPrecedence.Highest;
                    if (NodeTests.IsBinaryExpression(lastOperand!))
                    {
                        lastPrecedence = Precedence.GetBinaryOperatorPrecedence(lastOperand!.AsBinaryExpression().OperatorToken!.Kind);
                    }
                    if (operatorKind == SyntaxKind.SatisfiesKeyword)
                    {
                        leftOperand = MakeSatisfiesExpression(leftOperand, ParseType());
                    }
                    else
                    {
                        leftOperand = MakeAsExpression(leftOperand, ParseType());
                    }
                    // Stop if the precedence of the next operator is too high.
                    if (Precedence.GetBinaryOperatorPrecedence(ReScanGreaterThanToken()) > lastPrecedence)
                    {
                        break;
                    }
                }
            }
            else
            {
                leftOperand = MakeBinaryExpression(leftOperand, ParseTokenNode(), ParseBinaryExpressionOrHigher(newPrecedence), pos);
                lastOperand = leftOperand;
            }
        }
        return leftOperand!;
    }

    private Node MakeSatisfiesExpression(Node? expression, Node? typeNode) =>
        CheckJSSyntax(FinishNode(factory.NewSatisfiesExpression(expression, typeNode), expression!.Pos));

    private Node MakeAsExpression(Node? left, Node? right) =>
        CheckJSSyntax(FinishNode(factory.NewAsExpression(left, right), left!.Pos));

    private Node MakeBinaryExpression(Node? left, Node? operatorToken, Node? right, int pos) =>
        FinishNode(factory.NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, operatorToken, right), pos);

    private Node ParseUnaryExpressionOrHigher()
    {
        // ES7 UpdateExpression:
        //      1) LeftHandSideExpression[?Yield]
        //      2) LeftHandSideExpression[?Yield][no LineTerminator here]++
        //      3) LeftHandSideExpression[?Yield][no LineTerminator here]--
        //      4) ++UnaryExpression[?Yield]
        //      5) --UnaryExpression[?Yield]
        if (IsUpdateExpression())
        {
            int pos = NodePos();
            Node updateExpression = ParseUpdateExpression();
            if (token == SyntaxKind.AsteriskAsteriskToken)
            {
                return ParseBinaryExpressionRest(Precedence.GetBinaryOperatorPrecedence(token), updateExpression, pos);
            }
            return updateExpression;
        }
        // ES7 UnaryExpression:
        //      1) UpdateExpression[?yield]
        //      2) delete UpdateExpression[?yield]
        //      3) void UpdateExpression[?yield]
        //      4) typeof UpdateExpression[?yield]
        //      5) + UpdateExpression[?yield]
        //      6) - UpdateExpression[?yield]
        //      7) ~ UpdateExpression[?yield]
        //      8) ! UpdateExpression[?yield]
        SyntaxKind unaryOperator = token;
        Node simpleUnaryExpression = ParseSimpleUnaryExpression();
        if (token == SyntaxKind.AsteriskAsteriskToken)
        {
            int pos = global::Ts.Scanner.Scanner.SkipTrivia(sourceText, simpleUnaryExpression.Pos);
            int end = simpleUnaryExpression.End;
            if (simpleUnaryExpression.Kind == SyntaxKind.TypeAssertionExpression)
            {
                ParseErrorAt(pos, end, global::Ts.Diagnostics.Diagnostics.A_type_assertion_expression_is_not_allowed_in_the_left_hand_side_of_an_exponentiation_expression_Consider_enclosing_the_expression_in_parentheses);
            }
            else
            {
                Debug.Assert(Utilities.IsKeywordOrPunctuation(unaryOperator));
                ParseErrorAt(pos, end, global::Ts.Diagnostics.Diagnostics.An_unary_expression_with_the_0_operator_is_not_allowed_in_the_left_hand_side_of_an_exponentiation_expression_Consider_enclosing_the_expression_in_parentheses, global::Ts.Scanner.Scanner.TokenToString(unaryOperator));
            }
        }
        return simpleUnaryExpression;
    }

    private bool IsUpdateExpression()
    {
        switch (token)
        {
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.TildeToken:
            case SyntaxKind.ExclamationToken:
            case SyntaxKind.DeleteKeyword:
            case SyntaxKind.TypeOfKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.AwaitKeyword:
                return false;
            case SyntaxKind.LessThanToken:
                return languageVariant == LanguageVariant.Jsx;
        }
        return true;
    }

    private Node ParseUpdateExpression()
    {
        int pos = NodePos();
        if (token == SyntaxKind.PlusPlusToken || token == SyntaxKind.MinusMinusToken)
        {
            SyntaxKind operatorKind = token;
            NextToken();
            return FinishNode(factory.NewPrefixUnaryExpression(operatorKind, ParseLeftHandSideExpressionOrHigher()), pos);
        }
        else if (languageVariant == LanguageVariant.Jsx && token == SyntaxKind.LessThanToken && LookAhead(NextTokenIsIdentifierOrKeywordOrGreaterThan))
        {
            // JSXElement is part of primaryExpression
            return ParseJsxElementOrSelfClosingElementOrFragment(true /*inExpressionContext*/, -1 /*topInvalidNodePosition*/, null /*openingTag*/, false /*mustBeUnary*/);
        }
        Node expression = ParseLeftHandSideExpressionOrHigher();
        if ((token == SyntaxKind.PlusPlusToken || token == SyntaxKind.MinusMinusToken) && !HasPrecedingLineBreak())
        {
            SyntaxKind operatorKind = token;
            NextToken();
            return FinishNode(factory.NewPostfixUnaryExpression(expression, operatorKind), pos);
        }
        return expression;
    }

    private Node ParseSimpleUnaryExpression()
    {
        switch (token)
        {
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.TildeToken:
            case SyntaxKind.ExclamationToken:
                return ParsePrefixUnaryExpression();
            case SyntaxKind.DeleteKeyword:
                return ParseDeleteExpression();
            case SyntaxKind.TypeOfKeyword:
                return ParseTypeOfExpression();
            case SyntaxKind.VoidKeyword:
                return ParseVoidExpression();
            case SyntaxKind.LessThanToken:
                // Just like in parseUpdateExpression, we need to avoid parsing type assertions when
                // in JSX and we see an expression like "+ <foo> bar".
                if (languageVariant == LanguageVariant.Jsx)
                {
                    return ParseJsxElementOrSelfClosingElementOrFragment(true /*inExpressionContext*/, -1 /*topInvalidNodePosition*/, null /*openingTag*/, true /*mustBeUnary*/);
                }
                // UnaryExpression (modified): < type > UnaryExpression
                return ParseTypeAssertion();
            case SyntaxKind.AwaitKeyword:
                if (IsAwaitExpression())
                {
                    return ParseAwaitExpression();
                }
                goto default;
            default:
                return ParseUpdateExpression();
        }
    }

    private Node ParsePrefixUnaryExpression()
    {
        int pos = NodePos();
        SyntaxKind operatorKind = token;
        NextToken();
        return FinishNode(factory.NewPrefixUnaryExpression(operatorKind, ParseSimpleUnaryExpression()), pos);
    }

    private Node ParseDeleteExpression()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewDeleteExpression(ParseSimpleUnaryExpression()), pos);
    }

    private Node ParseVoidExpression()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewVoidExpression(ParseSimpleUnaryExpression()), pos);
    }

    private bool IsAwaitExpression()
    {
        if (token == SyntaxKind.AwaitKeyword)
        {
            if (InAwaitContext())
            {
                return true;
            }
            // here we are using similar heuristics as 'isYieldExpression'
            return LookAhead(NextTokenIsIdentifierOrKeywordOrLiteralOnSameLine);
        }
        return false;
    }

    private Node ParseAwaitExpression()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewAwaitExpression(ParseSimpleUnaryExpression()), pos);
    }

    private Node ParseLeftHandSideExpressionOrHigher()
    {
        // See comment in parseMemberExpressionOrHigher on how we replaced NewExpression with
        // MemberExpression to make our lives easier.
        //
        // Because there are three bottom-out states: 1) We see 'super' which must start either of
        // the last two CallExpression productions. 2) We see 'import' which must start import call.
        // 3) we have a MemberExpression which either completes the LeftHandSideExpression,
        // or starts the beginning of the first four CallExpression productions.
        int pos = NodePos();
        Node expression;
        if (token == SyntaxKind.ImportKeyword)
        {
            if (LookAhead(NextTokenIsOpenParenOrLessThan))
            {
                // We don't want to eagerly consume all import keyword as import call expression so we look ahead to find "("
                sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
                expression = ParseKeywordExpression();
            }
            else if (LookAhead(NextTokenIsDot))
            {
                // This is an 'import.*' metaproperty (i.e. 'import.meta')
                NextToken(); // advance past the 'import'
                NextToken(); // advance past the dot
                expression = FinishNode(factory.NewMetaProperty(SyntaxKind.ImportKeyword, ParseIdentifierName()), pos);
                if (expression.Text() == "defer")
                {
                    if (token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken)
                    {
                        sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
                    }
                }
                else
                {
                    sourceFlags |= NodeFlags.PossiblyContainsImportMeta;
                }
            }
            else
            {
                expression = ParseMemberExpressionOrHigher();
            }
        }
        else if (token == SyntaxKind.SuperKeyword)
        {
            expression = ParseSuperExpression();
        }
        else
        {
            expression = ParseMemberExpressionOrHigher();
        }
        // Now, we *may* be complete.  However, we might have consumed the start of a
        // CallExpression or OptionalExpression.  As such, we need to consume the rest of it here.
        return ParseCallExpressionRest(pos, expression);
    }

    private Node ParseSuperExpression()
    {
        int pos = NodePos();
        Node expression = ParseKeywordExpression();
        if (token == SyntaxKind.LessThanToken)
        {
            int startPos = NodePos();
            NodeList? typeArguments = TryParseTypeArgumentsInExpression();
            if (typeArguments != null)
            {
                ParseErrorAt(startPos, NodePos(), global::Ts.Diagnostics.Diagnostics.X_super_may_not_use_type_arguments);
                if (!IsTemplateStartOfTaggedTemplate())
                {
                    expression = FinishNode(factory.NewExpressionWithTypeArguments(expression, typeArguments), pos);
                }
            }
        }
        if (token == SyntaxKind.OpenParenToken || token == SyntaxKind.DotToken || token == SyntaxKind.OpenBracketToken)
        {
            return expression;
        }
        // If we have seen "super" it must be followed by '(' or '.'.
        // If it wasn't then just try to parse out a '.' and report an error.
        ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_super_must_be_followed_by_an_argument_list_or_member_access);
        // private names will never work with `super` (`super.#foo`), but that's a semantic error, not syntactic
        return FinishNode(factory.NewPropertyAccessExpression(expression, null /*questionDotToken*/, ParseRightSideOfDot(true /*allowIdentifierNames*/, true /*allowPrivateIdentifiers*/, true /*allowUnicodeEscapeSequenceInIdentifierName*/), NodeFlags.None), pos);
    }

    private bool IsTemplateStartOfTaggedTemplate() =>
        token == SyntaxKind.NoSubstitutionTemplateLiteral || token == SyntaxKind.TemplateHead;

    private Node ParseMemberExpressionOrHigher()
    {
        // Because CallExpression and MemberExpression are left recursive, we need to bottom out
        // of the recursion immediately.  So we parse out a primary expression to start with.
        int pos = NodePos();
        Node expression = ParsePrimaryExpression();
        return ParseMemberExpressionRest(pos, expression, true /*allowOptionalChain*/);
    }

    private Node ParseMemberExpressionRest(int pos, Node? expression, bool allowOptionalChain)
    {
        while (true)
        {
            Node? questionDotToken = null;
            bool isPropertyAccess;
            if (allowOptionalChain && IsStartOfOptionalPropertyOrElementAccessChain())
            {
                questionDotToken = ParseExpectedToken(SyntaxKind.QuestionDotToken);
                isPropertyAccess = Utilities.TokenIsIdentifierOrKeyword(token);
            }
            else
            {
                isPropertyAccess = ParseOptional(SyntaxKind.DotToken);
            }
            if (isPropertyAccess)
            {
                expression = ParsePropertyAccessExpressionRest(pos, expression, questionDotToken);
                continue;
            }
            // when in the [Decorator] context, we do not parse ElementAccess as it could be part of a ComputedPropertyName
            if ((questionDotToken != null || !InDecoratorContext()) && ParseOptional(SyntaxKind.OpenBracketToken))
            {
                expression = ParseElementAccessExpressionRest(pos, expression, questionDotToken);
                continue;
            }
            if (IsTemplateStartOfTaggedTemplate())
            {
                // Absorb type arguments into TemplateExpression when preceding expression is ExpressionWithTypeArguments
                if (questionDotToken == null && NodeTests.IsExpressionWithTypeArguments(expression!))
                {
                    ExpressionWithTypeArguments original = expression!.AsExpressionWithTypeArguments();
                    expression = ParseTaggedTemplateRest(pos, original.Expression, questionDotToken, original.TypeArguments);
                    UnparseExpressionWithTypeArguments(original.Expression, original.TypeArguments, expression);
                }
                else
                {
                    expression = ParseTaggedTemplateRest(pos, expression, questionDotToken, null /*typeArguments*/);
                }
                continue;
            }
            if (questionDotToken == null)
            {
                if (token == SyntaxKind.ExclamationToken && !HasPrecedingLineBreak())
                {
                    NextToken();
                    expression = CheckJSSyntax(FinishNode(factory.NewNonNullExpression(expression, NodeFlags.None), pos));
                    continue;
                }
                NodeList? typeArguments = TryParseTypeArgumentsInExpression();
                if (typeArguments != null)
                {
                    expression = FinishNode(factory.NewExpressionWithTypeArguments(expression, typeArguments), pos);
                    continue;
                }
            }
            return expression!;
        }
    }

    private bool IsStartOfOptionalPropertyOrElementAccessChain() =>
        token == SyntaxKind.QuestionDotToken && LookAhead(NextTokenIsIdentifierOrKeywordOrOpenBracketOrTemplate);

    private bool NextTokenIsIdentifierOrKeywordOrOpenBracketOrTemplate()
    {
        NextToken();
        return Utilities.TokenIsIdentifierOrKeyword(token) || token == SyntaxKind.OpenBracketToken || IsTemplateStartOfTaggedTemplate();
    }

    private Node ParsePropertyAccessExpressionRest(int pos, Node? expression, Node? questionDotToken)
    {
        Node name = ParseRightSideOfDot(true /*allowIdentifierNames*/, true /*allowPrivateIdentifiers*/, true /*allowUnicodeEscapeSequenceInIdentifierName*/);
        bool isOptionalChain = questionDotToken != null || TryReparseOptionalChain(expression);
        Node propertyAccess = factory.NewPropertyAccessExpression(expression, questionDotToken, name, CoreUtil.IfElse(isOptionalChain, NodeFlags.OptionalChain, NodeFlags.None));
        if (isOptionalChain && NodeTests.IsPrivateIdentifier(name))
        {
            ParseErrorAtRange(SkipRangeTrivia(name.Loc), global::Ts.Diagnostics.Diagnostics.An_optional_chain_cannot_contain_private_identifiers);
        }
        if (NodeTests.IsExpressionWithTypeArguments(expression!))
        {
            NodeList? typeArguments = expression!.TypeArgumentList();
            if (typeArguments != null)
            {
                TextRange loc = new(typeArguments.Pos - 1, global::Ts.Scanner.Scanner.SkipTrivia(sourceText, typeArguments.End) + 1);
                ParseErrorAtRange(loc, global::Ts.Diagnostics.Diagnostics.An_instantiation_expression_cannot_be_followed_by_a_property_access);
            }
        }
        return FinishNode(propertyAccess, pos);
    }

    private Node ParseElementAccessExpressionRest(int pos, Node? expression, Node? questionDotToken)
    {
        Node argumentExpression;
        if (token == SyntaxKind.CloseBracketToken)
        {
            ParseErrorAt(NodePos(), NodePos(), global::Ts.Diagnostics.Diagnostics.An_element_access_expression_should_take_an_argument);
            argumentExpression = CreateMissingIdentifier();
        }
        else
        {
            Node argument = ParseExpressionAllowIn();
            switch (argument.Kind)
            {
                case SyntaxKind.StringLiteral:
                    argument.AsStringLiteral().Text = InternIdentifier(argument.Text());
                    break;
                case SyntaxKind.NoSubstitutionTemplateLiteral:
                    argument.AsNoSubstitutionTemplateLiteral().Text = InternIdentifier(argument.Text());
                    break;
                case SyntaxKind.NumericLiteral:
                    argument.AsNumericLiteral().Text = InternIdentifier(argument.Text());
                    break;
            }
            argumentExpression = argument;
        }
        ParseExpected(SyntaxKind.CloseBracketToken);
        bool isOptionalChain = questionDotToken != null || TryReparseOptionalChain(expression);
        return FinishNode(factory.NewElementAccessExpression(expression, questionDotToken, argumentExpression, CoreUtil.IfElse(isOptionalChain, NodeFlags.OptionalChain, NodeFlags.None)), pos);
    }

    private Node ParseCallExpressionRest(int pos, Node? expression)
    {
        while (true)
        {
            expression = ParseMemberExpressionRest(pos, expression, true /*allowOptionalChain*/);
            NodeList? typeArguments = null;
            Node? questionDotToken = ParseOptionalToken(SyntaxKind.QuestionDotToken);
            if (questionDotToken != null)
            {
                typeArguments = TryParseTypeArgumentsInExpression();
                if (IsTemplateStartOfTaggedTemplate())
                {
                    expression = ParseTaggedTemplateRest(pos, expression, questionDotToken, typeArguments);
                    continue;
                }
            }
            if (typeArguments != null || token == SyntaxKind.OpenParenToken)
            {
                // Absorb type arguments into CallExpression when preceding expression is ExpressionWithTypeArguments
                if (questionDotToken == null && expression!.Kind == SyntaxKind.ExpressionWithTypeArguments)
                {
                    typeArguments = expression.TypeArgumentList();
                    expression = expression.AsExpressionWithTypeArguments().Expression;
                }
                Node? inner = expression;
                NodeList argumentList = ParseArgumentList();
                bool isOptionalChain = questionDotToken != null || TryReparseOptionalChain(expression);
                expression = CheckJSSyntax(FinishNode(factory.NewCallExpression(expression, questionDotToken, typeArguments, argumentList, CoreUtil.IfElse(isOptionalChain, NodeFlags.OptionalChain, NodeFlags.None)), pos));
                UnparseExpressionWithTypeArguments(inner, typeArguments, expression);
                continue;
            }
            if (questionDotToken != null)
            {
                // We parsed `?.` but then failed to parse anything, so report a missing identifier here.
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
                Node name = CreateMissingIdentifier();
                expression = FinishNode(factory.NewPropertyAccessExpression(expression, questionDotToken, name, NodeFlags.OptionalChain), pos);
            }
            break;
        }
        return expression!;
    }

    private NodeList ParseArgumentList()
    {
        ParseExpected(SyntaxKind.OpenParenToken);
        NodeList? result = ParseDelimitedList(ParsingContext.ArgumentExpressions, ParseArgumentExpression);
        ParseExpected(SyntaxKind.CloseParenToken);
        return result!;
    }

    private Node ParseArgumentExpression() =>
        DoInContext(NodeFlags.DisallowInContext | NodeFlags.DecoratorContext, false, ParseArgumentOrArrayLiteralElement);

    private Node ParseArgumentOrArrayLiteralElement()
    {
        switch (token)
        {
            case SyntaxKind.DotDotDotToken:
                return ParseSpreadElement();
            case SyntaxKind.CommaToken:
                return FinishNode(factory.NewOmittedExpression(), NodePos());
        }
        return ParseAssignmentExpressionOrHigher();
    }

    private Node ParseSpreadElement()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.DotDotDotToken);
        Node expression = ParseAssignmentExpressionOrHigher();
        return FinishNode(factory.NewSpreadElement(expression), pos);
    }

    private Node ParseTaggedTemplateRest(int pos, Node? tag, Node? questionDotToken, NodeList? typeArguments)
    {
        Node template;
        if (token == SyntaxKind.NoSubstitutionTemplateLiteral)
        {
            ReScanTemplateToken(true /*isTaggedTemplate*/);
            template = ParseLiteralExpression(false /*intern*/);
        }
        else
        {
            template = ParseTemplateExpression(true /*isTaggedTemplate*/);
        }
        bool isOptionalChain = questionDotToken != null || (tag!.Flags & NodeFlags.OptionalChain) != 0;
        return CheckJSSyntax(FinishNode(factory.NewTaggedTemplateExpression(tag, questionDotToken, typeArguments, template, CoreUtil.IfElse(isOptionalChain, NodeFlags.OptionalChain, NodeFlags.None)), pos));
    }

    private Node ParseTemplateExpression(bool isTaggedTemplate)
    {
        int pos = NodePos();
        return FinishNode(factory.NewTemplateExpression(ParseTemplateHead(isTaggedTemplate), ParseTemplateSpans(isTaggedTemplate)), pos);
    }

    private NodeList ParseTemplateSpans(bool isTaggedTemplate)
    {
        int pos = NodePos();
        var list = new List<Node>();
        while (true)
        {
            Node span = ParseTemplateSpan(isTaggedTemplate);
            list.Add(span);
            if (span.AsTemplateSpan().Literal!.Kind != SyntaxKind.TemplateMiddle)
            {
                break;
            }
        }
        return NewNodeList(new TextRange(pos, NodePos()), list.ToArray());
    }

    private Node ParseTemplateSpan(bool isTaggedTemplate)
    {
        int pos = NodePos();
        Node expression = ParseExpressionAllowIn();
        Node literal = ParseLiteralOfTemplateSpan(isTaggedTemplate);
        return FinishNode(factory.NewTemplateSpan(expression, literal), pos);
    }

    private Node ParseTemplateHead(bool isTaggedTemplate)
    {
        if (!isTaggedTemplate && (scanner.TokenFlags() & TokenFlags.IsInvalid) != 0)
        {
            ReScanTemplateToken(false /*isTaggedTemplate*/);
        }
        int pos = NodePos();
        Node result = factory.NewTemplateHead(scanner.TokenValue(), GetTemplateLiteralRawText(2 /*endLength*/), scanner.TokenFlags());
        NextToken();
        return FinishNode(result, pos);
    }

    private string GetTemplateLiteralRawText(int endLength)
    {
        string tokenText = scanner.TokenText();
        if ((scanner.TokenFlags() & TokenFlags.Unterminated) != 0)
        {
            endLength = 0;
        }
        // Go: tokenText[1 : len(tokenText)-endLength]
        return tokenText.Substring(1, tokenText.Length - endLength - 1);
    }

    private Node ParseLiteralOfTemplateSpan(bool isTaggedTemplate)
    {
        if (token == SyntaxKind.CloseBraceToken)
        {
            ReScanTemplateToken(isTaggedTemplate);
            return ParseTemplateMiddleOrTail();
        }
        ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.CloseBraceToken));
        return FinishNode(factory.NewTemplateTail("", "", TokenFlags.None), NodePos());
    }

    private Node ParseTemplateMiddleOrTail()
    {
        int pos = NodePos();
        Node result;
        if (token == SyntaxKind.TemplateMiddle)
        {
            result = factory.NewTemplateMiddle(scanner.TokenValue(), GetTemplateLiteralRawText(2 /*endLength*/), scanner.TokenFlags());
        }
        else
        {
            result = factory.NewTemplateTail(scanner.TokenValue(), GetTemplateLiteralRawText(1 /*endLength*/), scanner.TokenFlags());
        }
        NextToken();
        return FinishNode(result, pos);
    }

    private SyntaxKind ReScanTemplateToken(bool isTaggedTemplate)
    {
        token = scanner.ReScanTemplateToken(isTaggedTemplate);
        return token;
    }

    private Node ParsePrimaryExpression()
    {
        switch (token)
        {
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                if ((scanner.TokenFlags() & TokenFlags.IsInvalid) != 0)
                {
                    ReScanTemplateToken(false /*isTaggedTemplate*/);
                }
                goto case SyntaxKind.StringLiteral;
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
                return ParseLiteralExpression(false /*intern*/);
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
                return ParseKeywordExpression();
            case SyntaxKind.OpenParenToken:
                return ParseParenthesizedExpression();
            case SyntaxKind.OpenBracketToken:
                return ParseArrayLiteralExpression();
            case SyntaxKind.OpenBraceToken:
                return ParseObjectLiteralExpression();
            case SyntaxKind.AsyncKeyword:
                // Async arrow functions are parsed earlier in parseAssignmentExpressionOrHigher.
                // If we encounter `async [no LineTerminator here] function` then this is an async
                // function; otherwise, its an identifier.
                if (!LookAhead(NextTokenIsFunctionKeywordOnSameLine))
                {
                    break;
                }
                return ParseFunctionExpression();
            case SyntaxKind.AtToken:
                return ParseDecoratedExpression();
            case SyntaxKind.ClassKeyword:
                return ParseClassExpression();
            case SyntaxKind.FunctionKeyword:
                return ParseFunctionExpression();
            case SyntaxKind.NewKeyword:
                return ParseNewExpressionOrNewDotTarget();
            case SyntaxKind.SlashToken:
            case SyntaxKind.SlashEqualsToken:
                if (ReScanSlashToken() == SyntaxKind.RegularExpressionLiteral)
                {
                    return ParseLiteralExpression(false /*intern*/);
                }
                break;
            case SyntaxKind.TemplateHead:
                return ParseTemplateExpression(false /*isTaggedTemplate*/);
            case SyntaxKind.PrivateIdentifier:
                return ParsePrivateIdentifier();
        }
        return ParseIdentifierWithDiagnostic(global::Ts.Diagnostics.Diagnostics.Expression_expected, null);
    }

    private Node ParseParenthesizedExpression()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseExpressionAllowIn();
        ParseExpected(SyntaxKind.CloseParenToken);
        Node result = FinishNode(factory.NewParenthesizedExpression(expression), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseArrayLiteralExpression()
    {
        int pos = NodePos();
        int openBracketPosition = scanner.TokenStart();
        bool openBracketParsed = ParseExpected(SyntaxKind.OpenBracketToken);
        bool multiLine = HasPrecedingLineBreak();
        NodeList? elements = ParseDelimitedList(ParsingContext.ArrayLiteralMembers, ParseArgumentOrArrayLiteralElement);
        ParseExpectedMatchingBrackets(SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken, openBracketParsed, openBracketPosition);
        return FinishNode(factory.NewArrayLiteralExpression(elements, multiLine), pos);
    }

    private Node ParseObjectLiteralExpression()
    {
        int pos = NodePos();
        int openBracePosition = scanner.TokenStart();
        bool openBraceParsed = ParseExpected(SyntaxKind.OpenBraceToken);
        bool multiLine = HasPrecedingLineBreak();
        NodeList? properties = ParseDelimitedList(ParsingContext.ObjectLiteralMembers, ParseObjectLiteralElement);
        ParseExpectedMatchingBrackets(SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, openBraceParsed, openBracePosition);
        return FinishNode(factory.NewObjectLiteralExpression(properties, multiLine), pos);
    }

    private Node ParseObjectLiteralElement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        if (ParseOptional(SyntaxKind.DotDotDotToken))
        {
            Node spreadExpression = ParseAssignmentExpressionOrHigher();
            Node spreadResult = FinishNode(factory.NewSpreadAssignment(spreadExpression), pos);
            WithJSDoc(spreadResult, jsdoc);
            return spreadResult;
        }
        ModifierList? modifiers = ParseModifiersEx(true /*allowDecorators*/, false /*permitConstAsModifier*/, false /*stopOnStartOfClassStaticBlock*/);
        if (ParseContextualModifier(SyntaxKind.GetKeyword))
        {
            return ParseAccessorDeclaration(pos, jsdoc, modifiers, SyntaxKind.GetAccessor, ParseFlags.None);
        }
        if (ParseContextualModifier(SyntaxKind.SetKeyword))
        {
            return ParseAccessorDeclaration(pos, jsdoc, modifiers, SyntaxKind.SetAccessor, ParseFlags.None);
        }
        Node? asteriskToken = ParseOptionalToken(SyntaxKind.AsteriskToken);
        bool tokenIsIdentifier = IsIdentifier();
        Node name = ParsePropertyName();
        // Disallowing of optional property assignments and definite assignment assertion happens in the grammar checker.
        Node? postfixToken = ParseOptionalToken(SyntaxKind.QuestionToken);
        // Decorators, Modifiers, questionToken, and exclamationToken are not supported by property assignments and are reported in the grammar checker
        if (postfixToken == null)
        {
            postfixToken = ParseOptionalToken(SyntaxKind.ExclamationToken);
        }
        if (asteriskToken != null || token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken)
        {
            return ParseMethodDeclaration(pos, jsdoc, modifiers, asteriskToken, name, postfixToken, null /*diagnosticMessage*/);
        }
        // check if it is short-hand property assignment or normal property assignment
        // NOTE: if token is EqualsToken it is interpreted as CoverInitializedName production
        Node node;
        bool isShorthandPropertyAssignment = tokenIsIdentifier && token != SyntaxKind.ColonToken;
        if (isShorthandPropertyAssignment)
        {
            Node? equalsToken = ParseOptionalToken(SyntaxKind.EqualsToken);
            Node? initializer = null;
            if (equalsToken != null)
            {
                initializer = DoInContext(NodeFlags.DisallowInContext, false, ParseAssignmentExpressionOrHigher);
            }
            node = factory.NewShorthandPropertyAssignment(modifiers, name, postfixToken, null /*typeNode*/, equalsToken, initializer);
        }
        else
        {
            ParseExpected(SyntaxKind.ColonToken);
            Node initializer = DoInContext(NodeFlags.DisallowInContext, false, ParseAssignmentExpressionOrHigher);
            node = factory.NewPropertyAssignment(modifiers, name, postfixToken, null /*typeNode*/, initializer);
        }
        FinishNode(node, pos);
        WithJSDoc(node, jsdoc);
        return node;
    }

    private Node ParseDecoratedExpression()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ModifierList? modifiers = ParseModifiersEx(true /*allowDecorators*/, false /*permitConstAsModifier*/, false /*stopOnStartOfClassStaticBlock*/);
        if (token == SyntaxKind.ClassKeyword)
        {
            return ParseClassDeclarationOrExpression(pos, jsdoc, modifiers, SyntaxKind.ClassExpression);
        }
        ParseErrorAt(NodePos(), NodePos(), global::Ts.Diagnostics.Diagnostics.Expression_expected);
        return FinishNode(factory.NewMissingDeclaration(modifiers), pos);
    }

    private Node ParseNewExpressionOrNewDotTarget()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.NewKeyword);
        if (ParseOptional(SyntaxKind.DotToken))
        {
            Node metaName = ParseIdentifierName();
            return FinishNode(factory.NewMetaProperty(SyntaxKind.NewKeyword, metaName), pos);
        }
        int expressionPos = NodePos();
        Node expression = ParseMemberExpressionRest(expressionPos, ParsePrimaryExpression(), false /*allowOptionalChain*/);
        NodeList? typeArguments = null;
        // Absorb type arguments into NewExpression when preceding expression is ExpressionWithTypeArguments
        if (expression.Kind == SyntaxKind.ExpressionWithTypeArguments)
        {
            typeArguments = expression.TypeArgumentList();
            expression = expression.AsExpressionWithTypeArguments().Expression!;
        }
        if (token == SyntaxKind.QuestionDotToken)
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Invalid_optional_chain_from_new_expression_Did_you_mean_to_call_0, global::Ts.Scanner.Scanner.GetTextOfNodeFromSourceText(sourceText, expression, false /*includeTrivia*/));
        }
        NodeList? argumentList = null;
        if (token == SyntaxKind.OpenParenToken)
        {
            argumentList = ParseArgumentList();
        }
        Node result = CheckJSSyntax(FinishNode(factory.NewNewExpression(expression, typeArguments, argumentList), pos));
        UnparseExpressionWithTypeArguments(expression, typeArguments, result);
        return result;
    }

    private Node ParseKeywordExpression()
    {
        int pos = NodePos();
        Node result = factory.NewKeywordExpression(token);
        NextToken();
        return FinishNode(result, pos);
    }

    private Node ParseLiteralExpression(bool intern)
    {
        int pos = NodePos();
        string text = scanner.TokenValue();
        if (intern)
        {
            text = InternIdentifier(text);
        }
        TokenFlags tokenFlags = scanner.TokenFlags();
        Node result;
        switch (token)
        {
            case SyntaxKind.StringLiteral:
                result = factory.NewStringLiteral(text, tokenFlags);
                break;
            case SyntaxKind.NumericLiteral:
                result = factory.NewNumericLiteral(text, tokenFlags);
                break;
            case SyntaxKind.BigIntLiteral:
                result = factory.NewBigIntLiteral(text, tokenFlags);
                break;
            case SyntaxKind.RegularExpressionLiteral:
                result = factory.NewRegularExpressionLiteral(text, tokenFlags);
                break;
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                result = factory.NewNoSubstitutionTemplateLiteral(text, tokenFlags);
                break;
            default:
                throw new InvalidOperationException("Unhandled case in parseLiteralExpression");
        }
        NextToken();
        return FinishNode(result, pos);
    }

    private Node ParseExpressionOrLabeledStatement()
    {
        // Avoiding having to do the lookahead for a labeled statement by just trying to parse
        // out an expression, seeing if it is identifier and then seeing if it is followed by a colon.
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        bool hasParen = token == SyntaxKind.OpenParenToken;
        Node expression = ParseExpression();

        if (expression.Kind == SyntaxKind.Identifier && ParseOptional(SyntaxKind.ColonToken))
        {
            Node labeled = FinishNode(factory.NewLabeledStatement(expression, ParseStatement()), pos);
            WithJSDoc(labeled, jsdoc);
            return labeled;
        }

        if (!TryParseSemicolon())
        {
            ParseErrorForMissingSemicolonAfter(expression);
        }
        Node result = FinishNode(factory.NewExpressionStatement(expression), pos);
        if (hasParen)
        {
            jsdoc &= ~JSDocScannerInfo.HasJSDoc;
        }
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseSignatureMember(SyntaxKind kind)
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        if (kind == SyntaxKind.ConstructSignature)
        {
            ParseExpected(SyntaxKind.NewKeyword);
        }
        NodeList? typeParameters = ParseTypeParameters();
        NodeList? parameters = ParseParameters(ParseFlags.Type);
        Node? typeNode = ParseReturnType(SyntaxKind.ColonToken /*isType*/, true);
        ParseTypeMemberSemicolon();
        Node result;
        if (kind == SyntaxKind.CallSignature)
        {
            result = factory.NewCallSignatureDeclaration(typeParameters, parameters, typeNode);
        }
        else
        {
            result = factory.NewConstructSignatureDeclaration(typeParameters, parameters, typeNode);
        }
        FinishNode(result, pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    // Go: jsdoc.go — used by @typedef/@callback property-access name parsing (JSDoc cluster caller).
    private Node ParsePropertyAccessEntityNameExpression()
    {
        int pos = NodePos();
        Node node = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
        while (ParseOptional(SyntaxKind.DotToken))
        {
            Node name = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
            node = FinishNode(factory.NewPropertyAccessExpression(node, null, name, NodeFlags.None), pos);
        }
        return node;
    }

    // ── start-of / next-token predicates ──

    private bool IsStartOfExpressionStatement() =>
        // As per the grammar, none of '{' or 'function' or 'class' can start an expression statement.
        token != SyntaxKind.OpenBraceToken && token != SyntaxKind.FunctionKeyword && token != SyntaxKind.ClassKeyword && token != SyntaxKind.AtToken && IsStartOfExpression();

    private bool NextIsUnParenthesizedAsyncArrowFunction()
    {
        // AsyncArrowFunctionExpression:
        //      1) async[no LineTerminator here]AsyncArrowBindingIdentifier[?Yield][no LineTerminator here]=>AsyncConciseBody[?In]
        //      2) CoverCallExpressionAndAsyncArrowHead[?Yield, ?Await][no LineTerminator here]=>AsyncConciseBody[?In]
        if (token == SyntaxKind.AsyncKeyword)
        {
            NextToken();
            // If the "async" is followed by "=>" token then it is not a beginning of an async arrow-function
            // but instead a simple arrow-function which will be parsed inside "parseAssignmentExpressionOrHigher"
            if (HasPrecedingLineBreak() || token == SyntaxKind.EqualsGreaterThanToken)
            {
                return false;
            }
            // Check for un-parenthesized AsyncArrowFunction
            Node expr = ParseBinaryExpressionOrHigher(OperatorPrecedence.Lowest);
            if (!HasPrecedingLineBreak() && expr.Kind == SyntaxKind.Identifier && token == SyntaxKind.EqualsGreaterThanToken)
            {
                return true;
            }
        }
        return false;
    }

    private bool NextIsIdentifierAndCloseParen() =>
        NextTokenIsIdentifier() && NextToken() == SyntaxKind.CloseParenToken;

    private bool NextIsStartOfExpression()
    {
        NextToken();
        return IsStartOfExpression();
    }

    private bool NextTokenIsIdentifierOrKeywordOrLiteralOnSameLine() =>
        (NextTokenIsIdentifierOrKeyword() || token == SyntaxKind.NumericLiteral || token == SyntaxKind.BigIntLiteral || token == SyntaxKind.StringLiteral) && !HasPrecedingLineBreak();

    private bool NextTokenIsIdentifierOrStringLiteralOnSameLine()
    {
        NextToken();
        return (IsIdentifier() || token == SyntaxKind.StringLiteral) && !HasPrecedingLineBreak();
    }

    private bool NextTokenIsNumericOrBigIntLiteral()
    {
        NextToken();
        return token == SyntaxKind.NumericLiteral || token == SyntaxKind.BigIntLiteral;
    }

    private bool NextTokenIsOpenParen() =>
        NextToken() == SyntaxKind.OpenParenToken;

    private bool NextTokenIsOpenParenOrLessThan()
    {
        NextToken();
        return token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken;
    }

    private bool IsNextTokenOpenParenOrLessThanOrDot() =>
        LookAhead(NextTokenIsOpenParenOrLessThanOrDot);

    private bool NextTokenIsOpenParenOrLessThanOrDot()
    {
        switch (NextToken())
        {
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.LessThanToken:
            case SyntaxKind.DotToken:
                return true;
        }
        return false;
    }

    private bool NextTokenIsTokenStringLiteral() =>
        NextToken() == SyntaxKind.StringLiteral;
}
