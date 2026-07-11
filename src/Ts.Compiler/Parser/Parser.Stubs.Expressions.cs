// Partitioned stub file (cluster: Expressions). Replace each stub with the real body in place.
// Build stays green; when all stubs in a cluster are implemented, delete this file.
#nullable enable
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    private Node MakeAsExpression(Node? left, Node? right) => throw new System.NotImplementedException("makeAsExpression");
    private Node MakeBinaryExpression(Node? left, Node? operatorToken, Node? right, int pos) => throw new System.NotImplementedException("makeBinaryExpression");
    private Node MakeSatisfiesExpression(Node? expression, Node? typeNode) => throw new System.NotImplementedException("makeSatisfiesExpression");
    private Node ParseArgumentExpression() => throw new System.NotImplementedException("parseArgumentExpression");
    private Node ParseArgumentOrArrayLiteralElement() => throw new System.NotImplementedException("parseArgumentOrArrayLiteralElement");
    private Node ParseArrayLiteralExpression() => throw new System.NotImplementedException("parseArrayLiteralExpression");
    private Node ParseAssignmentExpressionOrHigher() => throw new System.NotImplementedException("parseAssignmentExpressionOrHigher");
    private Node ParseAssignmentExpressionOrHigherWorker(bool allowReturnTypeInArrowFunction) => throw new System.NotImplementedException("parseAssignmentExpressionOrHigherWorker");
    private Node ParseAwaitExpression() => throw new System.NotImplementedException("parseAwaitExpression");
    private Node ParseBinaryExpressionOrHigher(OperatorPrecedence precedence) => throw new System.NotImplementedException("parseBinaryExpressionOrHigher");
    private Node ParseBinaryExpressionRest(OperatorPrecedence precedence, Node? leftOperand, int pos) => throw new System.NotImplementedException("parseBinaryExpressionRest");
    private Node ParseCallExpressionRest(int pos, Node? expression) => throw new System.NotImplementedException("parseCallExpressionRest");
    private Node ParseConditionalExpressionRest(Node? leftOperand, int pos, bool allowReturnTypeInArrowFunction) => throw new System.NotImplementedException("parseConditionalExpressionRest");
    private Node ParseDecoratedExpression() => throw new System.NotImplementedException("parseDecoratedExpression");
    private Node ParseDeleteExpression() => throw new System.NotImplementedException("parseDeleteExpression");
    private Node ParseElementAccessExpressionRest(int pos, Node? expression, Node? questionDotToken) => throw new System.NotImplementedException("parseElementAccessExpressionRest");
    private Node ParseExpression() => throw new System.NotImplementedException("parseExpression");
    private Node ParseExpressionAllowIn() => throw new System.NotImplementedException("parseExpressionAllowIn");
    private Node ParseExpressionOrLabeledStatement() => throw new System.NotImplementedException("parseExpressionOrLabeledStatement");
    private Node ParseKeywordExpression() => throw new System.NotImplementedException("parseKeywordExpression");
    private Node ParseLeftHandSideExpressionOrHigher() => throw new System.NotImplementedException("parseLeftHandSideExpressionOrHigher");
    private Node ParseLiteralExpression(bool intern) => throw new System.NotImplementedException("parseLiteralExpression");
    private Node ParseLiteralOfTemplateSpan(bool isTaggedTemplate) => throw new System.NotImplementedException("parseLiteralOfTemplateSpan");
    private Node ParseMemberExpressionOrHigher() => throw new System.NotImplementedException("parseMemberExpressionOrHigher");
    private Node ParseMemberExpressionRest(int pos, Node? expression, bool allowOptionalChain) => throw new System.NotImplementedException("parseMemberExpressionRest");
    private Node ParseNewExpressionOrNewDotTarget() => throw new System.NotImplementedException("parseNewExpressionOrNewDotTarget");
    private Node ParseObjectLiteralElement() => throw new System.NotImplementedException("parseObjectLiteralElement");
    private Node ParseObjectLiteralExpression() => throw new System.NotImplementedException("parseObjectLiteralExpression");
    private Node ParseParenthesizedExpression() => throw new System.NotImplementedException("parseParenthesizedExpression");
    private Node ParsePrefixUnaryExpression() => throw new System.NotImplementedException("parsePrefixUnaryExpression");
    private Node ParsePrimaryExpression() => throw new System.NotImplementedException("parsePrimaryExpression");
    private Node ParsePropertyAccessEntityNameExpression() => throw new System.NotImplementedException("parsePropertyAccessEntityNameExpression");
    private Node ParsePropertyAccessExpressionRest(int pos, Node? expression, Node? questionDotToken) => throw new System.NotImplementedException("parsePropertyAccessExpressionRest");
    private Node ParseSignatureMember(SyntaxKind kind) => throw new System.NotImplementedException("parseSignatureMember");
    private Node ParseSimpleUnaryExpression() => throw new System.NotImplementedException("parseSimpleUnaryExpression");
    private Node ParseSpreadElement() => throw new System.NotImplementedException("parseSpreadElement");
    private Node ParseSuperExpression() => throw new System.NotImplementedException("parseSuperExpression");
    private Node ParseTaggedTemplateRest(int pos, Node? tag, Node? questionDotToken, NodeList? typeArguments) => throw new System.NotImplementedException("parseTaggedTemplateRest");
    private Node ParseTemplateExpression(bool isTaggedTemplate) => throw new System.NotImplementedException("parseTemplateExpression");
    private Node ParseTemplateHead(bool isTaggedTemplate) => throw new System.NotImplementedException("parseTemplateHead");
    private Node ParseTemplateMiddleOrTail() => throw new System.NotImplementedException("parseTemplateMiddleOrTail");
    private Node ParseTemplateSpan(bool isTaggedTemplate) => throw new System.NotImplementedException("parseTemplateSpan");
    private Node ParseUnaryExpressionOrHigher() => throw new System.NotImplementedException("parseUnaryExpressionOrHigher");
    private Node ParseUpdateExpression() => throw new System.NotImplementedException("parseUpdateExpression");
    private Node ParseVoidExpression() => throw new System.NotImplementedException("parseVoidExpression");
    private Node ParseYieldExpression() => throw new System.NotImplementedException("parseYieldExpression");
    private NodeList ParseArgumentList() => throw new System.NotImplementedException("parseArgumentList");
    private NodeList ParseTemplateSpans(bool isTaggedTemplate) => throw new System.NotImplementedException("parseTemplateSpans");
    private SyntaxKind ReScanTemplateToken(bool isTaggedTemplate) => throw new System.NotImplementedException("reScanTemplateToken");
    private bool IsAwaitExpression() => throw new System.NotImplementedException("isAwaitExpression");
    private bool IsNextTokenOpenParenOrLessThanOrDot() => throw new System.NotImplementedException("isNextTokenOpenParenOrLessThanOrDot");
    private bool IsStartOfExpressionStatement() => throw new System.NotImplementedException("isStartOfExpressionStatement");
    private bool IsStartOfOptionalPropertyOrElementAccessChain() => throw new System.NotImplementedException("isStartOfOptionalPropertyOrElementAccessChain");
    private bool IsTemplateStartOfTaggedTemplate() => throw new System.NotImplementedException("isTemplateStartOfTaggedTemplate");
    private bool IsUpdateExpression() => throw new System.NotImplementedException("isUpdateExpression");
    private bool IsYieldExpression() => throw new System.NotImplementedException("isYieldExpression");
    private bool NextIsIdentifierAndCloseParen() => throw new System.NotImplementedException("nextIsIdentifierAndCloseParen");
    private bool NextIsStartOfExpression() => throw new System.NotImplementedException("nextIsStartOfExpression");
    private bool NextIsUnParenthesizedAsyncArrowFunction() => throw new System.NotImplementedException("nextIsUnParenthesizedAsyncArrowFunction");
    private bool NextTokenIsIdentifierOrKeywordOrLiteralOnSameLine() => throw new System.NotImplementedException("nextTokenIsIdentifierOrKeywordOrLiteralOnSameLine");
    private bool NextTokenIsIdentifierOrKeywordOrOpenBracketOrTemplate() => throw new System.NotImplementedException("nextTokenIsIdentifierOrKeywordOrOpenBracketOrTemplate");
    private bool NextTokenIsIdentifierOrStringLiteralOnSameLine() => throw new System.NotImplementedException("nextTokenIsIdentifierOrStringLiteralOnSameLine");
    private bool NextTokenIsNumericOrBigIntLiteral() => throw new System.NotImplementedException("nextTokenIsNumericOrBigIntLiteral");
    private bool NextTokenIsOpenParen() => throw new System.NotImplementedException("nextTokenIsOpenParen");
    private bool NextTokenIsOpenParenOrLessThan() => throw new System.NotImplementedException("nextTokenIsOpenParenOrLessThan");
    private bool NextTokenIsOpenParenOrLessThanOrDot() => throw new System.NotImplementedException("nextTokenIsOpenParenOrLessThanOrDot");
    private bool NextTokenIsTokenStringLiteral() => throw new System.NotImplementedException("nextTokenIsTokenStringLiteral");
    private string GetTemplateLiteralRawText(int endLength) => throw new System.NotImplementedException("getTemplateLiteralRawText");
}
