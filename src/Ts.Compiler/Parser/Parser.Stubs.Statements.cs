// Partitioned stub file (cluster: Statements). Replace each stub with the real body in place.
// Build stays green; when all stubs in a cluster are implemented, delete this file.
#nullable enable
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    private Node AddDeepCloneReparse(Node? node) => throw new System.NotImplementedException("addDeepCloneReparse");
    private Node AddTransformedReparse(Node? newNode, Node? old) => throw new System.NotImplementedException("addTransformedReparse");
    private Node CheckJSSyntax(Node? node) => throw new System.NotImplementedException("checkJSSyntax");
    private Node CheckNonIdentifierName(Node? name) => throw new System.NotImplementedException("checkNonIdentifierName");
    private Node MakeNewCast(Node? t, Node? e, bool isAssertion) => throw new System.NotImplementedException("makeNewCast");
    private Node MakeQuestionIfOptional(Node? parameter) => throw new System.NotImplementedException("makeQuestionIfOptional");
    private Node ParseBlock(bool ignoreMissingOpenBrace, Message? diagnosticMessage) => throw new System.NotImplementedException("parseBlock");
    private Node ParseBreakStatement() => throw new System.NotImplementedException("parseBreakStatement");
    private Node ParseCaseBlock() => throw new System.NotImplementedException("parseCaseBlock");
    private Node ParseCaseClause() => throw new System.NotImplementedException("parseCaseClause");
    private Node ParseCaseOrDefaultClause() => throw new System.NotImplementedException("parseCaseOrDefaultClause");
    private Node ParseCatchClause() => throw new System.NotImplementedException("parseCatchClause");
    private Node ParseComputedPropertyName() => throw new System.NotImplementedException("parseComputedPropertyName");
    private Node ParseContinueStatement() => throw new System.NotImplementedException("parseContinueStatement");
    private Node ParseDebuggerStatement() => throw new System.NotImplementedException("parseDebuggerStatement");
    private Node ParseDefaultClause() => throw new System.NotImplementedException("parseDefaultClause");
    private Node ParseDoStatement() => throw new System.NotImplementedException("parseDoStatement");
    private Node ParseEmptyStatement() => throw new System.NotImplementedException("parseEmptyStatement");
    private Node ParseEntityName(bool allowReservedWords, Message? diagnosticMessage) => throw new System.NotImplementedException("parseEntityName");
    private Node ParseForOrForInOrForOfStatement() => throw new System.NotImplementedException("parseForOrForInOrForOfStatement");
    private Node ParseIdentifier() => throw new System.NotImplementedException("parseIdentifier");
    private Node ParseIdentifierName() => throw new System.NotImplementedException("parseIdentifierName");
    private Node ParseIdentifierNameErrorOnUnicodeEscapeSequence() => throw new System.NotImplementedException("parseIdentifierNameErrorOnUnicodeEscapeSequence");
    private Node ParseIdentifierNameWithDiagnostic(Message? diagnosticMessage) => throw new System.NotImplementedException("parseIdentifierNameWithDiagnostic");
    private Node ParseIdentifierOrPattern() => throw new System.NotImplementedException("parseIdentifierOrPattern");
    private Node ParseIdentifierOrPatternWithDiagnostic(Message? privateIdentifierDiagnosticMessage) => throw new System.NotImplementedException("parseIdentifierOrPatternWithDiagnostic");
    private Node ParseIdentifierUnlessAtSemicolon() => throw new System.NotImplementedException("parseIdentifierUnlessAtSemicolon");
    private Node ParseIfStatement() => throw new System.NotImplementedException("parseIfStatement");
    private Node ParseInitializer() => throw new System.NotImplementedException("parseInitializer");
    private Node ParsePropertyName() => throw new System.NotImplementedException("parsePropertyName");
    private Node ParsePropertyNameWorker(bool allowComputedPropertyNames) => throw new System.NotImplementedException("parsePropertyNameWorker");
    private Node ParseReturnStatement() => throw new System.NotImplementedException("parseReturnStatement");
    private Node ParseRightSideOfDot(bool allowIdentifierNames, bool allowPrivateIdentifiers, bool allowUnicodeEscapeSequenceInIdentifierName) => throw new System.NotImplementedException("parseRightSideOfDot");
    private Node ParseStatement() => throw new System.NotImplementedException("parseStatement");
    private Node ParseSwitchStatement() => throw new System.NotImplementedException("parseSwitchStatement");
    private Node ParseThrowStatement() => throw new System.NotImplementedException("parseThrowStatement");
    private Node ParseTryStatement() => throw new System.NotImplementedException("parseTryStatement");
    private Node ParseWhileStatement() => throw new System.NotImplementedException("parseWhileStatement");
    private Node ParseWithStatement() => throw new System.NotImplementedException("parseWithStatement");
    private ResolutionMode ParseResolutionMode(string? mode, int pos, int end) => throw new System.NotImplementedException("parseResolutionMode");
    private SyntaxKind ReScanSlashToken() => throw new System.NotImplementedException("reScanSlashToken");
    private bool CanFollowGetOrSetKeyword() => throw new System.NotImplementedException("canFollowGetOrSetKeyword");
    private bool IsImplementsClause() => throw new System.NotImplementedException("isImplementsClause");
    private bool IsNextNonwhitespaceTokenEndOfFile() => throw new System.NotImplementedException("isNextNonwhitespaceTokenEndOfFile");
    private bool NextIsNotDot() => throw new System.NotImplementedException("nextIsNotDot");
    private bool NextTokenCanFollowDefaultKeyword() => throw new System.NotImplementedException("nextTokenCanFollowDefaultKeyword");
    private bool NextTokenIsColonOrQuestionColon() => throw new System.NotImplementedException("nextTokenIsColonOrQuestionColon");
    private bool NextTokenIsDot() => throw new System.NotImplementedException("nextTokenIsDot");
    private bool NextTokenIsEqualsOrSemicolonOrColonToken() => throw new System.NotImplementedException("nextTokenIsEqualsOrSemicolonOrColonToken");
    private bool NextTokenIsFromKeywordOrEqualsToken() => throw new System.NotImplementedException("nextTokenIsFromKeywordOrEqualsToken");
    private bool NextTokenIsFunctionKeywordOnSameLine() => throw new System.NotImplementedException("nextTokenIsFunctionKeywordOnSameLine");
    private bool NextTokenIsIdentifier() => throw new System.NotImplementedException("nextTokenIsIdentifier");
    private bool NextTokenIsIdentifierOnSameLine() => throw new System.NotImplementedException("nextTokenIsIdentifierOnSameLine");
    private bool NextTokenIsIdentifierOrKeyword() => throw new System.NotImplementedException("nextTokenIsIdentifierOrKeyword");
    private bool NextTokenIsIdentifierOrKeywordOnSameLine() => throw new System.NotImplementedException("nextTokenIsIdentifierOrKeywordOnSameLine");
    private bool NextTokenIsIdentifierOrKeywordOrGreaterThan() => throw new System.NotImplementedException("nextTokenIsIdentifierOrKeywordOrGreaterThan");
    private bool NextTokenIsNewKeyword() => throw new System.NotImplementedException("nextTokenIsNewKeyword");
    private bool NextTokenIsOpenBrace() => throw new System.NotImplementedException("nextTokenIsOpenBrace");
    private bool ScanStartOfNamedTupleElement() => throw new System.NotImplementedException("scanStartOfNamedTupleElement");
    private bool TryReparseOptionalChain(Node? node) => throw new System.NotImplementedException("tryReparseOptionalChain");
    private void FinishMutatedNode(Node? node) => throw new System.NotImplementedException("finishMutatedNode");
    private void FinishReparsedNode(Node? node, Node? locationNode) => throw new System.NotImplementedException("finishReparsedNode");
    private void JsErrorAtRange(TextRange loc, Message? message, params object?[] args) => throw new System.NotImplementedException("jsErrorAtRange");
    private void ParseErrorForInvalidName(Message? nameDiagnostic, Message? blankDiagnostic, SyntaxKind tokenIfBlankName) => throw new System.NotImplementedException("parseErrorForInvalidName");
    private void ParseErrorForMissingSemicolonAfter(Node? node) => throw new System.NotImplementedException("parseErrorForMissingSemicolonAfter");
    private void ParseSemicolonAfterPropertyName(Node? name, Node? typeNode, Node? initializer) => throw new System.NotImplementedException("parseSemicolonAfterPropertyName");
    private void SkipWhitespace() => throw new System.NotImplementedException("skipWhitespace");
}
