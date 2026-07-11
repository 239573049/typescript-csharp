// Ported from tsgo internal/printer/printer.go (Expressions section + emitExpression dispatch + NoASI).

using System;
using System.Linq;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using EF = global::Ts.Printer.EmitFlags;
using AstUtil = global::Ts.Ast.AstUtilities;
using Precedence = global::Ts.Ast.Precedence;

public sealed partial class Printer
{
    private void EmitKeywordExpression(KeywordExpression node)
    {
        EmitKeywordNode(node.AsNode());
    }

    private void EmitArrayLiteralExpressionElement(Node node)
    {
        EmitExpression(node, OperatorPrecedence.Spread);
    }

    private void EmitArrayLiteralExpression(ArrayLiteralExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitList(static (p, n) => p.EmitArrayLiteralExpressionElement(n), node.AsNode(), node.Elements, ListFormat.ArrayLiteralExpressionElements | (node.MultiLine ? ListFormat.PreferNewLine : ListFormat.None));
        ExitNode(node.AsNode(), state);
    }

    private void EmitObjectLiteralExpression(ObjectLiteralExpression node)
    {
        var state = EnterNode(node.AsNode());
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        GenerateAllMemberNames(node.Properties);
        EmitList(static (p, n) => p.EmitObjectLiteralElement(n), node.AsNode(), node.Properties, ListFormat.ObjectLiteralExpressionProperties |
            (node.MultiLine ? ListFormat.PreferNewLine : ListFormat.None) |
            (ShouldAllowTrailingComma(node.AsNode(), node.Properties!) ? ListFormat.AllowTrailingComma : ListFormat.None));
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    // 1..toString is a valid property access, emit a dot after the literal
    private bool MayNeedDotDotForPropertyAccess(Node expression)
    {
        expression = AstUtil.SkipPartiallyEmittedExpressions(expression);
        if (NodeTests.IsNumericLiteral(expression))
        {
            var text = GetLiteralTextOfNode(expression, null /*sourceFile*/, GetLiteralTextFlags.NeverAsciiEscape);
            return (expression.AsNumericLiteral().TokenFlags & TokenFlags.WithSpecifier) == 0 &&
                !text.Contains(global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.DotToken), StringComparison.Ordinal) &&
                !text.Contains("E", StringComparison.Ordinal) &&
                !text.Contains("e", StringComparison.Ordinal);
        }
        return false;
    }

    private void EmitPropertyAccessExpression(PropertyAccessExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, AstUtil.IsOptionalChain(node.AsNode()) ? OperatorPrecedence.OptionalChain : OperatorPrecedence.Member);
        var token = node.QuestionDotToken;
        if (token is null)
        {
            token = emitContext.Factory.NewToken(SyntaxKind.DotToken);
            token.Loc = new TextRange(node.Expression!.End, node.Name()!.Pos);
            emitContext.AddEmitFlags(token, EF.NoSourceMap);
        }
        var linesBeforeDot = GetLinesBetweenNodes(node.AsNode(), node.Expression, token);
        WriteLineRepeat(linesBeforeDot);
        IncreaseIndentIf(linesBeforeDot > 0);
        bool shouldEmitDotDot = token.Kind != SyntaxKind.QuestionDotToken &&
            MayNeedDotDotForPropertyAccess(node.Expression!) &&
            !writer.HasTrailingComment() &&
            !writer.HasTrailingWhitespace();
        if (shouldEmitDotDot)
        {
            WritePunctuation(".");
        }
        if (node.QuestionDotToken is not null)
        {
            EmitTokenNode(token);
        }
        else
        {
            EmitToken(SyntaxKind.DotToken, node.Expression!.End, WriteKind.Punctuation, node.AsNode());
        }
        var linesAfterDot = GetLinesBetweenNodes(node.AsNode(), token, node.Name());
        WriteLineRepeat(linesAfterDot);
        IncreaseIndentIf(linesAfterDot > 0);
        EmitMemberName(node.Name());
        DecreaseIndentIf(linesAfterDot > 0);
        DecreaseIndentIf(linesBeforeDot > 0);
        ExitNode(node.AsNode(), state);
    }

    private void EmitElementAccessExpression(ElementAccessExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, AstUtil.IsOptionalChain(node.AsNode()) ? OperatorPrecedence.OptionalChain : OperatorPrecedence.Member);
        EmitTokenNode(node.QuestionDotToken);
        EmitToken(SyntaxKind.OpenBracketToken, PrinterUtilities.GreatestEnd(-1, node.Expression, node.QuestionDotToken), WriteKind.Punctuation, node.AsNode());
        EmitExpression(node.ArgumentExpression!, OperatorPrecedence.Comma);
        EmitToken(SyntaxKind.CloseBracketToken, node.ArgumentExpression!.End, WriteKind.Punctuation, node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitArgument(Node node)
    {
        EmitExpression(node, OperatorPrecedence.Spread);
    }

    private void EmitCallee(Node callee, Node parentNode)
    {
        if (ShouldEmitIndirectCall(parentNode))
        {
            WritePunctuation("(");
            WriteLiteral("0");
            WritePunctuation(",");
            WriteSpace();
            EmitExpression(callee, OperatorPrecedence.Comma);
            WritePunctuation(")");
        }
        else if (parentNode.Kind == SyntaxKind.CallExpression && PrinterUtilities.IsNewExpressionWithoutArguments(AstUtil.SkipPartiallyEmittedExpressions(callee)))
        {
            // Parenthesize `new C` inside of a CallExpression so it is treated as `(new C)()` and not `new C()`
            EmitExpression(callee, OperatorPrecedence.Parentheses);
        }
        else
        {
            EmitExpression(callee, AstUtil.IsOptionalChain(parentNode) ? OperatorPrecedence.OptionalChain : OperatorPrecedence.Member);
        }
    }

    private void EmitCallExpression(CallExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitCallee(node.Expression!, node.AsNode());
        EmitTokenNode(node.QuestionDotToken);
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        EmitList(static (p, n) => p.EmitArgument(n), node.AsNode(), node.Arguments, ListFormat.CallExpressionArguments);
        ExitNode(node.AsNode(), state);
    }

    private void EmitNewExpression(NewExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.NewKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        if (AstUtil.SkipPartiallyEmittedExpressions(node.Expression!).Kind == SyntaxKind.CallExpression)
        {
            // Parenthesize `C()` inside of a NewExpression so it is treated as `new (C())` and not `new C()`
            EmitExpression(node.Expression!, OperatorPrecedence.Parentheses);
        }
        else
        {
            EmitExpression(node.Expression!, OperatorPrecedence.Member);
        }
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        EmitList(static (p, n) => p.EmitArgument(n), node.AsNode(), node.Arguments, ListFormat.NewExpressionArguments);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateLiteral(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                EmitNoSubstitutionTemplateLiteral(node.AsNoSubstitutionTemplateLiteral());
                break;
            case SyntaxKind.TemplateExpression:
                EmitTemplateExpression(node.AsTemplateExpression());
                break;
            default:
                throw new InvalidOperationException($"unhandled TemplateLiteral: {node.Kind}");
        }
    }

    private void EmitTaggedTemplateExpression(TaggedTemplateExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitCallee(node.Tag!, node.AsNode());
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        WriteSpace();
        EmitTemplateLiteral(node.Template!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeAssertionExpression(TypeAssertion node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("<");
        EmitTypeNodeOutsideExtends(node.Type!);
        WritePunctuation(">");
        EmitExpression(node.Expression!, OperatorPrecedence.Update);
        ExitNode(node.AsNode(), state);
    }

    private void EmitParenthesizedExpression(ParenthesizedExpression node)
    {
        var state = EnterNode(node.AsNode());
        var openParenPos = EmitToken(SyntaxKind.OpenParenToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        var indented = WriteLineSeparatorsAndIndentBefore(node.Expression!, node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Comma);
        WriteLineSeparatorsAfter(node.Expression!, node.AsNode());
        DecreaseIndentIf(indented);
        int closeParenPos = openParenPos;
        if (node.Expression is not null)
        {
            closeParenPos = node.Expression.End;
        }
        EmitToken(SyntaxKind.CloseParenToken, closeParenPos, WriteKind.Punctuation, node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitFunctionExpression(FunctionExpression node)
    {
        var state = EnterNode(node.AsNode());
        GenerateNameIfNeeded(node.Name());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("function");
        EmitTokenNode(node.AsteriskToken);
        WriteSpace();
        EmitIdentifierNameNode(node.Name());
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        EmitFunctionBodyNode(node.Body);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitConciseBody(Node node)
    {
        if (NodeTests.IsBlock(node))
        {
            EmitFunctionBody(node.AsBlock());
        }
        else if (NodeTests.IsObjectLiteralExpression(Precedence.GetLeftmostExpression(node, false /*stopAtCallExpressions*/)))
        {
            var paren = emitContext.Factory.NewParenthesizedExpression(node);
            paren.Loc = node.Loc;
            EmitExpression(paren, OperatorPrecedence.Lowest);
        }
        else if (AstUtil.IsExpression(node))
        {
            EmitExpression(node, OperatorPrecedence.Yield);
        }
        else
        {
            throw new InvalidOperationException($"unexpected ConciseBody: {node.Kind}");
        }
    }

    private void EmitArrowFunction(ArrowFunction node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitTypeParameters(node.AsNode(), node.TypeParameters);
        EmitParametersForArrow(node.AsNode(), node.Parameters!);
        EmitTypeAnnotation(node.Type);
        WriteSpace();
        EmitTokenNode(node.EqualsGreaterThanToken);
        WriteSpace();
        EmitConciseBody(node.Body!);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitDeleteExpression(DeleteExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.DeleteKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Unary);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeOfExpression(TypeOfExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.TypeOfKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Unary);
        ExitNode(node.AsNode(), state);
    }

    private void EmitVoidExpression(VoidExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.VoidKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Unary);
        ExitNode(node.AsNode(), state);
    }

    private void EmitAwaitExpression(AwaitExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.AwaitKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Unary);
        ExitNode(node.AsNode(), state);
    }

    private void EmitPrefixUnaryExpression(PrefixUnaryExpression node)
    {
        var state = EnterNode(node.AsNode());
        var @operator = node.Operator;
        var operand = node.Operand!;
        EmitToken(@operator, node.Pos, WriteKind.Operator, node.AsNode());

        if (operand.Kind == SyntaxKind.PrefixUnaryExpression)
        {
            var inner = operand.AsPrefixUnaryExpression().Operator;
            if ((@operator == SyntaxKind.PlusToken && (inner == SyntaxKind.PlusToken || inner == SyntaxKind.PlusPlusToken)) ||
                (@operator == SyntaxKind.MinusToken && (inner == SyntaxKind.MinusToken || inner == SyntaxKind.MinusMinusToken)))
            {
                WriteSpace();
            }
        }

        EmitExpression(node.Operand!, OperatorPrecedence.Unary);
        ExitNode(node.AsNode(), state);
    }

    private void EmitPostfixUnaryExpression(PostfixUnaryExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Operand!, OperatorPrecedence.LeftHandSide);
        EmitToken(node.Operator, node.Operand!.End, WriteKind.Operator, node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private SyntaxKind GetLiteralKindOfBinaryPlusOperand(Node node)
    {
        node = AstUtil.SkipPartiallyEmittedExpressions(node);

        if (NodeTests.IsLiteralKind(node.Kind))
        {
            return node.Kind;
        }

        if (node.Kind == SyntaxKind.BinaryExpression)
        {
            var n = node.AsBinaryExpression();
            if (n.OperatorToken!.Kind == SyntaxKind.PlusToken)
            {
                var leftKind = GetLiteralKindOfBinaryPlusOperand(n.Left!);
                var literalKind = SyntaxKind.Unknown;
                if (NodeTests.IsLiteralKind(leftKind) && leftKind == GetLiteralKindOfBinaryPlusOperand(n.Right!))
                {
                    literalKind = leftKind;
                }

                return literalKind;
            }
        }

        return SyntaxKind.Unknown;
    }

    private (OperatorPrecedence LeftPrec, OperatorPrecedence RightPrec) GetBinaryExpressionPrecedence(BinaryExpression node)
    {
        var precedence = Precedence.GetExpressionPrecedence(node.AsNode());
        var leftPrec = precedence;
        var rightPrec = precedence;
        switch (precedence)
        {
            case OperatorPrecedence.Comma:
                break;
            case OperatorPrecedence.Assignment:
                // assignment is right-associative
                leftPrec = OperatorPrecedence.Conditional;
                rightPrec = OperatorPrecedence.Yield;
                break;
            case OperatorPrecedence.LogicalOR:
                rightPrec = OperatorPrecedence.LogicalAND;
                break;
            case OperatorPrecedence.LogicalAND:
                rightPrec = OperatorPrecedence.BitwiseOR;
                break;
            case OperatorPrecedence.BitwiseOR:
                break;
            case OperatorPrecedence.BitwiseXOR:
                break;
            case OperatorPrecedence.BitwiseAND:
                break;
            case OperatorPrecedence.Equality:
                rightPrec = OperatorPrecedence.Relational;
                break;
            case OperatorPrecedence.Relational:
                rightPrec = OperatorPrecedence.Shift;
                break;
            case OperatorPrecedence.Shift:
                rightPrec = OperatorPrecedence.Additive;
                break;
            case OperatorPrecedence.Additive:
                if (node.OperatorToken!.Kind == SyntaxKind.PlusToken && PrinterUtilities.IsBinaryOperation(node.Right!, SyntaxKind.PlusToken))
                {
                    var leftKind = GetLiteralKindOfBinaryPlusOperand(node.Left!);
                    if (NodeTests.IsLiteralKind(leftKind) && leftKind == GetLiteralKindOfBinaryPlusOperand(node.Right!))
                    {
                        break;
                    }
                }
                rightPrec = OperatorPrecedence.Multiplicative;
                break;
            case OperatorPrecedence.Multiplicative:
                if (node.OperatorToken!.Kind == SyntaxKind.AsteriskToken && PrinterUtilities.IsBinaryOperation(node.Right!, SyntaxKind.AsteriskToken))
                {
                    break;
                }
                rightPrec = OperatorPrecedence.Exponentiation;
                break;
            case OperatorPrecedence.Exponentiation:
                // exponentiation is right-associative
                leftPrec = OperatorPrecedence.Update;
                break;
            default:
                throw new InvalidOperationException($"unhandled precedence: {precedence}");
        }
        return (leftPrec, rightPrec);
    }

    private void EmitBinaryExpression(BinaryExpression node)
    {
        var (leftPrec, rightPrec) = GetBinaryExpressionPrecedence(node);
        var emittedLeft = AstUtil.SkipPartiallyEmittedExpressions(node.Left!);
        if (AstUtil.NodeIsSynthesized(emittedLeft) && emittedLeft.Kind == SyntaxKind.BinaryExpression && PrinterUtilities.MixingBinaryOperatorsRequiresParentheses(node.OperatorToken!.Kind, emittedLeft.AsBinaryExpression().OperatorToken!.Kind))
        {
            leftPrec = OperatorPrecedence.Highest;
        }
        var emittedRight = AstUtil.SkipPartiallyEmittedExpressions(node.Right!);
        if (AstUtil.NodeIsSynthesized(emittedRight) && emittedRight.Kind == SyntaxKind.BinaryExpression && PrinterUtilities.MixingBinaryOperatorsRequiresParentheses(node.OperatorToken!.Kind, emittedRight.AsBinaryExpression().OperatorToken!.Kind))
        {
            rightPrec = OperatorPrecedence.Highest;
        }
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Left!, leftPrec);
        var linesBeforeOperator = GetLinesBetweenNodes(node.AsNode(), node.Left, node.OperatorToken);
        var linesAfterOperator = GetLinesBetweenNodes(node.AsNode(), node.OperatorToken, node.Right);
        WriteLinesAndIndent(linesBeforeOperator, node.OperatorToken!.Kind != SyntaxKind.CommaToken /*writeSpaceIfNotIndenting*/);
        EmitTokenNodeEx(node.OperatorToken, TokenEmitFlags.NoSourceMaps);
        WriteLinesAndIndent(linesAfterOperator, true /*writeSpaceIfNotIndenting*/);
        EmitExpression(node.Right!, rightPrec);
        DecreaseIndentIf(linesAfterOperator > 0);
        DecreaseIndentIf(linesBeforeOperator > 0);
        ExitNode(node.AsNode(), state);
    }

    private void EmitShortCircuitExpression(Node node)
    {
        if (PrinterUtilities.IsBinaryOperation(AstUtil.SkipPartiallyEmittedExpressions(node), SyntaxKind.QuestionQuestionToken))
        {
            EmitExpression(node, OperatorPrecedence.Coalesce);
        }
        else
        {
            EmitExpression(node, OperatorPrecedence.LogicalOR);
        }
    }

    private void EmitConditionalExpression(ConditionalExpression node)
    {
        var state = EnterNode(node.AsNode());
        var linesBeforeQuestion = GetLinesBetweenNodes(node.AsNode(), node.Condition, node.QuestionToken);
        var linesAfterQuestion = GetLinesBetweenNodes(node.AsNode(), node.QuestionToken, node.WhenTrue);
        var linesBeforeColon = GetLinesBetweenNodes(node.AsNode(), node.WhenTrue, node.ColonToken);
        var linesAfterColon = GetLinesBetweenNodes(node.AsNode(), node.ColonToken, node.WhenFalse);
        EmitShortCircuitExpression(node.Condition!);
        WriteLinesAndIndent(linesBeforeQuestion, true /*writeSpaceIfNotIndenting*/);
        EmitPunctuationNode(node.QuestionToken);
        WriteLinesAndIndent(linesAfterQuestion, true /*writeSpaceIfNotIndenting*/);
        EmitExpression(node.WhenTrue!, OperatorPrecedence.Yield);
        DecreaseIndentIf(linesAfterQuestion > 0);
        DecreaseIndentIf(linesBeforeQuestion > 0);
        WriteLinesAndIndent(linesBeforeColon, true /*writeSpaceIfNotIndenting*/);
        EmitPunctuationNode(node.ColonToken);
        WriteLinesAndIndent(linesAfterColon, true /*writeSpaceIfNotIndenting*/);
        EmitExpression(node.WhenFalse!, OperatorPrecedence.Yield);
        DecreaseIndentIf(linesAfterColon > 0);
        DecreaseIndentIf(linesBeforeColon > 0);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateExpression(TemplateExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitTemplateHead(node.Head!.AsTemplateHead());
        EmitList(static (p, n) => p.EmitTemplateSpanNode(n), node.AsNode(), node.TemplateSpans, ListFormat.TemplateExpressionSpans);
        ExitNode(node.AsNode(), state);
    }

    private void EmitYieldExpression(YieldExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.YieldKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        EmitPunctuationNode(node.AsteriskToken);
        if (node.Expression is not null)
        {
            WriteSpace();
            EmitExpressionNoASI(node.Expression, OperatorPrecedence.DisallowComma);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitSpreadElement(SpreadElement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.DotDotDotToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.DisallowComma);
        ExitNode(node.AsNode(), state);
    }

    private void EmitClassExpression(ClassExpression node)
    {
        var state = EnterNode(node.AsNode());
        GenerateNameIfNeeded(node.Name());

        var pos = EmitModifierList(node.AsNode(), node.Modifiers(), true /*allowDecorators*/);
        EmitToken(SyntaxKind.ClassKeyword, pos, WriteKind.Keyword, node.AsNode());

        if (node.Name() is not null)
        {
            WriteSpace();
            EmitIdentifierName(node.Name()!.AsIdentifier());
        }

        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);

        EmitTypeParameters(node.AsNode(), node.TypeParameters);
        EmitList(static (p, n) => p.EmitHeritageClauseNode(n), node.AsNode(), node.HeritageClauses, ListFormat.ClassHeritageClauses);
        WriteSpace();
        WritePunctuation("{");
        PushNameGenerationScope(node.AsNode());
        GenerateAllMemberNames(node.Members);
        EmitList(static (p, n) => p.EmitClassElement(n), node.AsNode(), node.Members, ListFormat.ClassMembers);
        PopNameGenerationScope(node.AsNode());
        WritePunctuation("}");

        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitOmittedExpression(Node node)
    {
        ExitNode(node, EnterNode(node));
    }

    private void EmitExpressionWithTypeArguments(ExpressionWithTypeArguments node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Member);
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        ExitNode(node.AsNode(), state);
    }

    private void EmitExpressionWithTypeArgumentsNode(Node node)
    {
        EmitExpressionWithTypeArguments(node.AsExpressionWithTypeArguments());
    }

    private void EmitAsExpression(AsExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Relational);
        WriteSpace();
        WriteKeyword("as");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.Type!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitSatisfiesExpression(SatisfiesExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Relational);
        WriteSpace();
        WriteKeyword("satisfies");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.Type!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitNonNullExpression(NonNullExpression node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Member);
        WriteOperator("!");
        ExitNode(node.AsNode(), state);
    }

    private void EmitMetaProperty(MetaProperty node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(node.KeywordToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        WritePunctuation(".");
        EmitIdentifierName(node.Name()!.AsIdentifier());
        ExitNode(node.AsNode(), state);
    }

    private void EmitPartiallyEmittedExpression(PartiallyEmittedExpression node)
    {
        var stack = new global::Ts.Core.Stack<(PartiallyEmittedExpression Node, PrinterState State)>();
        while (true)
        {
            var state = EnterNode(node.AsNode());
            var emitFlags = emitContext.EmitFlags(node.AsNode());
            if ((emitFlags & EF.NoLeadingComments) == 0 && node.Pos != node.Expression!.Pos)
            {
                EmitTrailingCommentsOfPosition(node.Expression.Pos, false /*prefixSpace*/, false /*forceNoNewline*/);
            }
            stack.Push((node, state));
            if (!NodeTests.IsPartiallyEmittedExpression(node.Expression!))
            {
                break;
            }
            node = node.Expression!.AsPartiallyEmittedExpression();
        }

        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);

        // unwind stack
        while (stack.Len > 0)
        {
            var entry = stack.Pop();
            var emitFlags = emitContext.EmitFlags(node.AsNode());
            if ((emitFlags & EF.NoTrailingComments) == 0 && node.End != node.Expression!.End)
            {
                EmitLeadingCommentsOfPosition(node.Expression.End);
            }
            ExitNode(node.AsNode(), entry.State);
            node = entry.Node;
        }
    }

    private bool CommentWillEmitNewLine(CommentRange comment)
    {
        return comment.Kind == SyntaxKind.SingleLineCommentTrivia || comment.HasTrailingNewLine;
    }

    private bool SyntheticCommentWillEmitNewLine(SynthesizedComment comment)
    {
        return comment.Kind == SyntaxKind.SingleLineCommentTrivia || comment.HasTrailingNewLine;
    }

    private bool WillEmitLeadingNewLine(Node node)
    {
        if (currentSourceFile is null)
        {
            return false;
        }
        bool hasLeadingCommentRanges = false;
        bool hasNewLineComment = false;
        foreach (var comment in global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile.Text(), node.Pos))
        {
            hasLeadingCommentRanges = true;
            if (CommentWillEmitNewLine(comment))
            {
                hasNewLineComment = true;
            }
        }
        if (hasLeadingCommentRanges)
        {
            var parseNode = emitContext.ParseNode(node);
            if (parseNode is not null && NodeTests.IsParenthesizedExpression(parseNode.Parent!))
            {
                return true;
            }
        }
        if (hasNewLineComment)
        {
            return true;
        }
        var synth = emitContext.GetSyntheticLeadingComments(node);
        if (synth is not null && synth.Any(SyntheticCommentWillEmitNewLine))
        {
            return true;
        }
        if (NodeTests.IsPartiallyEmittedExpression(node))
        {
            var pee = node.AsPartiallyEmittedExpression();
            if (node.Pos != pee.Expression!.Pos)
            {
                foreach (var comment in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile.Text(), pee.Expression.Pos))
                {
                    if (CommentWillEmitNewLine(comment))
                    {
                        return true;
                    }
                }
            }
            return WillEmitLeadingNewLine(pee.Expression!);
        }
        return false;
    }

    private Node ParenthesizeExpressionForNoAsi(Node node)
    {
        if (!commentsDisabled)
        {
            switch (node.Kind)
            {
                case SyntaxKind.PartiallyEmittedExpression:
                {
                    if (WillEmitLeadingNewLine(node))
                    {
                        var pee = node.AsPartiallyEmittedExpression();
                        var parseNode = emitContext.ParseNode(node);
                        if (parseNode is not null && NodeTests.IsParenthesizedExpression(parseNode))
                        {
                            var parens0 = emitContext.Factory.NewParenthesizedExpression(pee.Expression);
                            emitContext.SetOriginal(parens0, node);
                            parens0.Loc = parseNode.Loc;
                            return parens0;
                        }
                        return emitContext.Factory.NewParenthesizedExpression(node);
                    }
                    var pee2 = node.AsPartiallyEmittedExpression();
                    return emitContext.Factory.AsNodeFactory().UpdatePartiallyEmittedExpression(
                        pee2,
                        ParenthesizeExpressionForNoAsi(pee2.Expression!));
                }
                case SyntaxKind.PropertyAccessExpression:
                {
                    var pae = node.AsPropertyAccessExpression();
                    return emitContext.Factory.AsNodeFactory().UpdatePropertyAccessExpression(
                        pae,
                        ParenthesizeExpressionForNoAsi(pae.Expression!),
                        pae.QuestionDotToken,
                        pae.Name(),
                        pae.Flags);
                }
                case SyntaxKind.ElementAccessExpression:
                {
                    var eae = node.AsElementAccessExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateElementAccessExpression(
                        eae,
                        ParenthesizeExpressionForNoAsi(eae.Expression!),
                        eae.QuestionDotToken,
                        eae.ArgumentExpression,
                        eae.Flags);
                }
                case SyntaxKind.CallExpression:
                {
                    var ce = node.AsCallExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateCallExpression(
                        ce,
                        ParenthesizeExpressionForNoAsi(ce.Expression!),
                        ce.QuestionDotToken,
                        ce.TypeArguments,
                        ce.Arguments,
                        ce.Flags);
                }
                case SyntaxKind.TaggedTemplateExpression:
                {
                    var tte = node.AsTaggedTemplateExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateTaggedTemplateExpression(
                        tte,
                        ParenthesizeExpressionForNoAsi(tte.Tag!),
                        tte.QuestionDotToken,
                        tte.TypeArguments,
                        tte.Template,
                        tte.Flags);
                }
                case SyntaxKind.PostfixUnaryExpression:
                {
                    var pue = node.AsPostfixUnaryExpression();
                    return emitContext.Factory.AsNodeFactory().UpdatePostfixUnaryExpression(
                        pue,
                        ParenthesizeExpressionForNoAsi(pue.Operand!),
                        pue.Operator);
                }
                case SyntaxKind.BinaryExpression:
                {
                    var be = node.AsBinaryExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateBinaryExpression(
                        be,
                        be.Modifiers(),
                        ParenthesizeExpressionForNoAsi(be.Left!),
                        be.Type,
                        be.OperatorToken,
                        be.Right);
                }
                case SyntaxKind.ConditionalExpression:
                {
                    var ce = node.AsConditionalExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateConditionalExpression(
                        ce,
                        ParenthesizeExpressionForNoAsi(ce.Condition!),
                        ce.QuestionToken,
                        ce.WhenTrue,
                        ce.ColonToken,
                        ce.WhenFalse);
                }
                case SyntaxKind.AsExpression:
                {
                    var ae = node.AsAsExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateAsExpression(
                        ae,
                        ParenthesizeExpressionForNoAsi(ae.Expression!),
                        ae.Type);
                }
                case SyntaxKind.SatisfiesExpression:
                {
                    var se = node.AsSatisfiesExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateSatisfiesExpression(
                        se,
                        ParenthesizeExpressionForNoAsi(se.Expression!),
                        se.Type);
                }
                case SyntaxKind.NonNullExpression:
                {
                    var nne = node.AsNonNullExpression();
                    return emitContext.Factory.AsNodeFactory().UpdateNonNullExpression(
                        nne,
                        ParenthesizeExpressionForNoAsi(nne.Expression!),
                        nne.Flags);
                }
            }
        }
        return node;
    }

    private void EmitExpressionNoASI(Node node, OperatorPrecedence precedence)
    {
        node = ParenthesizeExpressionForNoAsi(node);
        EmitExpression(node, precedence);
    }

    private void EmitExpression(Node node, OperatorPrecedence precedence)
    {
        bool parens = Precedence.GetExpressionPrecedence(AstUtil.SkipPartiallyEmittedExpressions(node)) < precedence;
        if (parens)
        {
            WritePunctuation("(");
        }

        switch (node.Kind)
        {
            // Keywords
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
                EmitTokenNode(node);
                break;
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.ImportKeyword:
                EmitKeywordExpression(node.AsKeywordExpression());
                break;

            // Literals
            case SyntaxKind.NumericLiteral:
                EmitNumericLiteral(node.AsNumericLiteral());
                break;
            case SyntaxKind.BigIntLiteral:
                EmitBigIntLiteral(node.AsBigIntLiteral());
                break;
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            case SyntaxKind.RegularExpressionLiteral:
                EmitRegularExpressionLiteral(node.AsRegularExpressionLiteral());
                break;
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                EmitNoSubstitutionTemplateLiteral(node.AsNoSubstitutionTemplateLiteral());
                break;

            // Identifiers
            case SyntaxKind.Identifier:
                EmitIdentifierReference(node.AsIdentifier());
                break;
            case SyntaxKind.PrivateIdentifier:
                EmitPrivateIdentifier(node.AsPrivateIdentifier());
                break;

            // Expressions
            case SyntaxKind.ArrayLiteralExpression:
                EmitArrayLiteralExpression(node.AsArrayLiteralExpression());
                break;
            case SyntaxKind.ObjectLiteralExpression:
                EmitObjectLiteralExpression(node.AsObjectLiteralExpression());
                break;
            case SyntaxKind.PropertyAccessExpression:
                EmitPropertyAccessExpression(node.AsPropertyAccessExpression());
                break;
            case SyntaxKind.ElementAccessExpression:
                EmitElementAccessExpression(node.AsElementAccessExpression());
                break;
            case SyntaxKind.CallExpression:
                EmitCallExpression(node.AsCallExpression());
                break;
            case SyntaxKind.NewExpression:
                EmitNewExpression(node.AsNewExpression());
                break;
            case SyntaxKind.TaggedTemplateExpression:
                EmitTaggedTemplateExpression(node.AsTaggedTemplateExpression());
                break;
            case SyntaxKind.TypeAssertionExpression:
                EmitTypeAssertionExpression(node.AsTypeAssertion());
                break;
            case SyntaxKind.ParenthesizedExpression:
                EmitParenthesizedExpression(node.AsParenthesizedExpression());
                break;
            case SyntaxKind.FunctionExpression:
                EmitFunctionExpression(node.AsFunctionExpression());
                break;
            case SyntaxKind.ArrowFunction:
                EmitArrowFunction(node.AsArrowFunction());
                break;
            case SyntaxKind.DeleteExpression:
                EmitDeleteExpression(node.AsDeleteExpression());
                break;
            case SyntaxKind.TypeOfExpression:
                EmitTypeOfExpression(node.AsTypeOfExpression());
                break;
            case SyntaxKind.VoidExpression:
                EmitVoidExpression(node.AsVoidExpression());
                break;
            case SyntaxKind.AwaitExpression:
                EmitAwaitExpression(node.AsAwaitExpression());
                break;
            case SyntaxKind.PrefixUnaryExpression:
                EmitPrefixUnaryExpression(node.AsPrefixUnaryExpression());
                break;
            case SyntaxKind.PostfixUnaryExpression:
                EmitPostfixUnaryExpression(node.AsPostfixUnaryExpression());
                break;
            case SyntaxKind.BinaryExpression:
                EmitBinaryExpression(node.AsBinaryExpression());
                break;
            case SyntaxKind.ConditionalExpression:
                EmitConditionalExpression(node.AsConditionalExpression());
                break;
            case SyntaxKind.TemplateExpression:
                EmitTemplateExpression(node.AsTemplateExpression());
                break;
            case SyntaxKind.YieldExpression:
                EmitYieldExpression(node.AsYieldExpression());
                break;
            case SyntaxKind.SpreadElement:
                EmitSpreadElement(node.AsSpreadElement());
                break;
            case SyntaxKind.ClassExpression:
                EmitClassExpression(node.AsClassExpression());
                break;
            case SyntaxKind.OmittedExpression:
                EmitOmittedExpression(node);
                break;
            case SyntaxKind.AsExpression:
                EmitAsExpression(node.AsAsExpression());
                break;
            case SyntaxKind.NonNullExpression:
                EmitNonNullExpression(node.AsNonNullExpression());
                break;
            case SyntaxKind.ExpressionWithTypeArguments:
                EmitExpressionWithTypeArguments(node.AsExpressionWithTypeArguments());
                break;
            case SyntaxKind.SatisfiesExpression:
                EmitSatisfiesExpression(node.AsSatisfiesExpression());
                break;
            case SyntaxKind.MetaProperty:
                EmitMetaProperty(node.AsMetaProperty());
                break;
            case SyntaxKind.SyntheticExpression:
                throw new InvalidOperationException("SyntheticExpression should never be printed.");
            case SyntaxKind.MissingDeclaration:
                break;

            // JSX
            case SyntaxKind.JsxElement:
                EmitJsxElement(node.AsJsxElement());
                break;
            case SyntaxKind.JsxSelfClosingElement:
                EmitJsxSelfClosingElement(node.AsJsxSelfClosingElement());
                break;
            case SyntaxKind.JsxFragment:
                EmitJsxFragment(node.AsJsxFragment());
                break;

            // Synthesized list
            case SyntaxKind.SyntaxList:
                throw new InvalidOperationException("SyntaxList should not be printed");

            // Transformation nodes
            case SyntaxKind.NotEmittedStatement:
                if (parens)
                {
                    WritePunctuation(")");
                }
                return;
            case SyntaxKind.PartiallyEmittedExpression:
                EmitPartiallyEmittedExpression(node.AsPartiallyEmittedExpression());
                break;
            case SyntaxKind.SyntheticReferenceExpression:
                throw new InvalidOperationException("SyntheticReferenceExpression should not be printed");

            default:
                throw new InvalidOperationException($"unexpected Expression: {node.Kind}");
        }

        if (parens)
        {
            WritePunctuation(")");
        }
    }
}
