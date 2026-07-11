// Ported from tsgo internal/parser/parser.go (statements cluster) and the reparser/jsdoc
// helpers that co-locate with it (internal/parser/reparser.go, internal/parser/jsdoc.go).
// Positions are UTF-16 code units (plan/01-architecture.md §6). Functions are translated
// 1:1 with tsgo; ordering roughly follows the Go source.
#nullable enable
using System;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    // ── statements ──

    private Node ParseStatement()
    {
        switch (token)
        {
            case SyntaxKind.SemicolonToken:
                return ParseEmptyStatement();
            case SyntaxKind.OpenBraceToken:
                return ParseBlock(false /*ignoreMissingOpenBrace*/, null);
            case SyntaxKind.VarKeyword:
                return ParseVariableStatement(NodePos(), JsdocScannerInfo(), null /*modifiers*/);
            case SyntaxKind.LetKeyword:
                if (IsLetDeclaration())
                {
                    return ParseVariableStatement(NodePos(), JsdocScannerInfo(), null /*modifiers*/);
                }
                break;
            case SyntaxKind.AwaitKeyword:
                if (IsAwaitUsingDeclaration())
                {
                    return ParseVariableStatement(NodePos(), JsdocScannerInfo(), null /*modifiers*/);
                }
                break;
            case SyntaxKind.UsingKeyword:
                if (IsUsingDeclaration())
                {
                    return ParseVariableStatement(NodePos(), JsdocScannerInfo(), null /*modifiers*/);
                }
                break;
            case SyntaxKind.FunctionKeyword:
                return ParseFunctionDeclaration(NodePos(), JsdocScannerInfo(), null /*modifiers*/);
            case SyntaxKind.ClassKeyword:
                return ParseClassDeclaration(NodePos(), JsdocScannerInfo(), null /*modifiers*/);
            case SyntaxKind.IfKeyword:
                return ParseIfStatement();
            case SyntaxKind.DoKeyword:
                return ParseDoStatement();
            case SyntaxKind.WhileKeyword:
                return ParseWhileStatement();
            case SyntaxKind.ForKeyword:
                return ParseForOrForInOrForOfStatement();
            case SyntaxKind.ContinueKeyword:
                return ParseContinueStatement();
            case SyntaxKind.BreakKeyword:
                return ParseBreakStatement();
            case SyntaxKind.ReturnKeyword:
                return ParseReturnStatement();
            case SyntaxKind.WithKeyword:
                return ParseWithStatement();
            case SyntaxKind.SwitchKeyword:
                return ParseSwitchStatement();
            case SyntaxKind.ThrowKeyword:
                return ParseThrowStatement();
            case SyntaxKind.TryKeyword:
            case SyntaxKind.CatchKeyword:
            case SyntaxKind.FinallyKeyword:
                return ParseTryStatement();
            case SyntaxKind.DebuggerKeyword:
                return ParseDebuggerStatement();
            case SyntaxKind.AtToken:
                return ParseDeclaration();
            case SyntaxKind.AsyncKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.TypeKeyword:
            case SyntaxKind.ModuleKeyword:
            case SyntaxKind.NamespaceKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.ExportKeyword:
            case SyntaxKind.ImportKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.AbstractKeyword:
            case SyntaxKind.AccessorKeyword:
            case SyntaxKind.StaticKeyword:
            case SyntaxKind.ReadonlyKeyword:
            case SyntaxKind.GlobalKeyword:
                if (IsStartOfDeclaration())
                {
                    return ParseDeclaration();
                }
                break;
        }
        return ParseExpressionOrLabeledStatement();
    }

    private Node ParseBlock(bool ignoreMissingOpenBrace, Message? diagnosticMessage)
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        int openBracePosition = scanner.TokenStart();
        bool openBraceParsed = ParseExpectedWithDiagnostic(SyntaxKind.OpenBraceToken, diagnosticMessage, true /*shouldAdvance*/);
        bool multiline = false;
        if (openBraceParsed || ignoreMissingOpenBrace)
        {
            multiline = HasPrecedingLineBreak();
            NodeList statements = ParseList(ParsingContext.BlockStatements, ParseStatement);
            ParseExpectedMatchingBrackets(SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, openBraceParsed, openBracePosition);
            Node result = FinishNode(factory.NewBlock(statements, multiline), pos);
            WithJSDoc(result, jsdoc);
            if (token == SyntaxKind.EqualsToken)
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Declaration_or_statement_expected_This_follows_a_block_of_statements_so_if_you_intended_to_write_a_destructuring_assignment_you_might_need_to_wrap_the_whole_assignment_in_parentheses);
                NextToken();
            }
            return result;
        }
        Node missing = FinishNode(factory.NewBlock(CreateMissingList(), multiline), pos);
        WithJSDoc(missing, jsdoc);
        return missing;
    }

    private Node ParseEmptyStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.SemicolonToken);
        Node result = FinishNode(factory.NewEmptyStatement(), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseIfStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.IfKeyword);
        int openParenPosition = scanner.TokenStart();
        bool openParenParsed = ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseExpressionAllowIn();
        ParseExpectedMatchingBrackets(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken, openParenParsed, openParenPosition);
        Node thenStatement = ParseStatement();
        Node? elseStatement = null;
        if (ParseOptional(SyntaxKind.ElseKeyword))
        {
            elseStatement = ParseStatement();
        }
        Node result = FinishNode(factory.NewIfStatement(expression, thenStatement, elseStatement), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseDoStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.DoKeyword);
        Node statement = ParseStatement();
        ParseExpected(SyntaxKind.WhileKeyword);
        int openParenPosition = scanner.TokenStart();
        bool openParenParsed = ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseExpressionAllowIn();
        ParseExpectedMatchingBrackets(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken, openParenParsed, openParenPosition);
        // From: https://mail.mozilla.org/pipermail/es-discuss/2011-August/016188.html
        // 157 min --- All allen at wirfs-brock.com CONF --- "do{;}while(false)false" prohibited in
        // spec but allowed in consensus reality. Approved -- this is the de-facto standard whereby
        //  do;while(0)x will have a semicolon inserted before x.
        ParseOptional(SyntaxKind.SemicolonToken);
        Node result = FinishNode(factory.NewDoStatement(statement, expression), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseWhileStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.WhileKeyword);
        int openParenPosition = scanner.TokenStart();
        bool openParenParsed = ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseExpressionAllowIn();
        ParseExpectedMatchingBrackets(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken, openParenParsed, openParenPosition);
        Node statement = ParseStatement();
        Node result = FinishNode(factory.NewWhileStatement(expression, statement), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseForOrForInOrForOfStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.ForKeyword);
        Node? awaitToken = ParseOptionalToken(SyntaxKind.AwaitKeyword);
        ParseExpected(SyntaxKind.OpenParenToken);
        Node? initializer = null;
        if (token != SyntaxKind.SemicolonToken)
        {
            if (token == SyntaxKind.VarKeyword || token == SyntaxKind.LetKeyword || token == SyntaxKind.ConstKeyword ||
                token == SyntaxKind.UsingKeyword && LookAhead(NextTokenIsBindingIdentifierOrStartOfDestructuringOnSameLineDisallowOf) ||
                // this one is meant to allow of
                token == SyntaxKind.AwaitKeyword && LookAhead(NextIsUsingKeywordThenBindingIdentifierOrStartOfObjectDestructuringOnSameLine))
            {
                initializer = ParseVariableDeclarationList(true /*inForStatementInitializer*/);
            }
            else
            {
                initializer = DoInContext(NodeFlags.DisallowInContext, true, ParseExpression);
            }
        }
        Node result;
        if ((awaitToken != null && ParseExpected(SyntaxKind.OfKeyword)) || (awaitToken == null && ParseOptional(SyntaxKind.OfKeyword)))
        {
            Node expression = DoInContext(NodeFlags.DisallowInContext, false, ParseAssignmentExpressionOrHigher);
            ParseExpected(SyntaxKind.CloseParenToken);
            result = factory.NewForInOrOfStatement(SyntaxKind.ForOfStatement, awaitToken, initializer, expression, ParseStatement());
        }
        else if (ParseOptional(SyntaxKind.InKeyword))
        {
            Node expression = ParseExpressionAllowIn();
            ParseExpected(SyntaxKind.CloseParenToken);
            result = factory.NewForInOrOfStatement(SyntaxKind.ForInStatement, null /*awaitToken*/, initializer, expression, ParseStatement());
        }
        else
        {
            ParseExpected(SyntaxKind.SemicolonToken);
            Node? condition = null;
            if (token != SyntaxKind.SemicolonToken && token != SyntaxKind.CloseParenToken)
            {
                condition = ParseExpressionAllowIn();
            }
            ParseExpected(SyntaxKind.SemicolonToken);
            Node? incrementor = null;
            if (token != SyntaxKind.CloseParenToken)
            {
                incrementor = ParseExpressionAllowIn();
            }
            ParseExpected(SyntaxKind.CloseParenToken);
            result = factory.NewForStatement(initializer, condition, incrementor, ParseStatement());
        }
        FinishNode(result, pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseBreakStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.BreakKeyword);
        Node? label = ParseIdentifierUnlessAtSemicolon();
        ParseSemicolon();
        Node result = FinishNode(factory.NewBreakStatement(label), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseContinueStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.ContinueKeyword);
        Node? label = ParseIdentifierUnlessAtSemicolon();
        ParseSemicolon();
        Node result = FinishNode(factory.NewContinueStatement(label), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node? ParseIdentifierUnlessAtSemicolon()
    {
        if (!CanParseSemicolon())
        {
            return ParseIdentifier();
        }
        return null;
    }

    private Node ParseReturnStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.ReturnKeyword);
        Node? expression = null;
        if (!CanParseSemicolon())
        {
            expression = ParseExpressionAllowIn();
        }
        ParseSemicolon();
        Node result = FinishNode(factory.NewReturnStatement(expression), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseWithStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.WithKeyword);
        int openParenPosition = scanner.TokenStart();
        bool openParenParsed = ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseExpressionAllowIn();
        ParseExpectedMatchingBrackets(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken, openParenParsed, openParenPosition);
        Node statement = DoInContext(NodeFlags.InWithStatement, true, ParseStatement);
        Node result = FinishNode(factory.NewWithStatement(expression, statement), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseCaseClause()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.CaseKeyword);
        Node expression = ParseExpressionAllowIn();
        ParseExpected(SyntaxKind.ColonToken);
        NodeList statements = ParseList(ParsingContext.SwitchClauseStatements, ParseStatement);
        Node result = FinishNode(factory.NewCaseOrDefaultClause(SyntaxKind.CaseClause, expression, statements), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseDefaultClause()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.DefaultKeyword);
        ParseExpected(SyntaxKind.ColonToken);
        NodeList statements = ParseList(ParsingContext.SwitchClauseStatements, ParseStatement);
        Node result = FinishNode(factory.NewCaseOrDefaultClause(SyntaxKind.DefaultClause, null /*expression*/, statements), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseCaseOrDefaultClause()
    {
        if (token == SyntaxKind.CaseKeyword)
        {
            return ParseCaseClause();
        }
        return ParseDefaultClause();
    }

    private Node ParseCaseBlock()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.OpenBraceToken);
        NodeList clauses = ParseList(ParsingContext.SwitchClauses, ParseCaseOrDefaultClause);
        ParseExpected(SyntaxKind.CloseBraceToken);
        Node result = FinishNode(factory.NewCaseBlock(clauses), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseSwitchStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.SwitchKeyword);
        ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseExpressionAllowIn();
        ParseExpected(SyntaxKind.CloseParenToken);
        Node caseBlock = ParseCaseBlock();
        Node result = FinishNode(factory.NewSwitchStatement(expression, caseBlock), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseThrowStatement()
    {
        // ThrowStatement[Yield] :
        //      throw [no LineTerminator here]Expression[In, ?Yield];
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.ThrowKeyword);
        // Because of automatic semicolon insertion, we need to report error if this
        // throw could be terminated with a semicolon.  Note: we can't call 'parseExpression'
        // directly as that might consume an expression on the following line.
        // Instead, we create a "missing" identifier, but don't report an error. The actual error
        // will be reported in the grammar walker.
        Node expression;
        if (!HasPrecedingLineBreak())
        {
            expression = ParseExpressionAllowIn();
        }
        else
        {
            expression = CreateMissingIdentifier();
        }
        if (!TryParseSemicolon())
        {
            ParseErrorForMissingSemicolonAfter(expression);
        }
        Node result = FinishNode(factory.NewThrowStatement(expression), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    // TODO: Review for error recovery
    private Node ParseTryStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.TryKeyword);
        Node tryBlock = ParseBlock(false /*ignoreMissingOpenBrace*/, null);
        Node? catchClause = null;
        if (token == SyntaxKind.CatchKeyword)
        {
            catchClause = ParseCatchClause();
        }
        // If we don't have a catch clause, then we must have a finally clause.  Try to parse
        // one out no matter what.
        Node? finallyBlock = null;
        if (catchClause == null || token == SyntaxKind.FinallyKeyword)
        {
            ParseExpectedWithDiagnostic(SyntaxKind.FinallyKeyword, global::Ts.Diagnostics.Diagnostics.X_catch_or_finally_expected, true /*shouldAdvance*/);
            finallyBlock = ParseBlock(false /*ignoreMissingOpenBrace*/, null);
        }
        Node result = FinishNode(factory.NewTryStatement(tryBlock, catchClause, finallyBlock), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseCatchClause()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.CatchKeyword);
        Node? variableDeclaration = null;
        if (ParseOptional(SyntaxKind.OpenParenToken))
        {
            variableDeclaration = ParseVariableDeclaration();
            ParseExpected(SyntaxKind.CloseParenToken);
        }
        Node block = ParseBlock(false /*ignoreMissingOpenBrace*/, null);
        Node result = FinishNode(factory.NewCatchClause(variableDeclaration, block), pos);
        return result;
    }

    private Node ParseDebuggerStatement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ParseExpected(SyntaxKind.DebuggerKeyword);
        ParseSemicolon();
        Node result = FinishNode(factory.NewDebuggerStatement(), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    // ── binding names / initializers ──

    private Node ParseIdentifierOrPattern()
    {
        return ParseIdentifierOrPatternWithDiagnostic(null);
    }

    private Node ParseIdentifierOrPatternWithDiagnostic(Message? privateIdentifierDiagnosticMessage)
    {
        if (token == SyntaxKind.OpenBracketToken)
        {
            return ParseArrayBindingPattern();
        }
        if (token == SyntaxKind.OpenBraceToken)
        {
            return ParseObjectBindingPattern();
        }
        return ParseBindingIdentifierWithDiagnostic(privateIdentifierDiagnosticMessage);
    }

    private Node? ParseInitializer()
    {
        if (ParseOptional(SyntaxKind.EqualsToken))
        {
            return ParseAssignmentExpressionOrHigher();
        }
        return null;
    }

    // ── entity / property names ──

    private Node ParseEntityName(bool allowReservedWords, Message? diagnosticMessage)
    {
        int pos = NodePos();
        Node entity;
        if (allowReservedWords)
        {
            entity = ParseIdentifierNameWithDiagnostic(diagnosticMessage);
        }
        else
        {
            entity = ParseIdentifierWithDiagnostic(diagnosticMessage, null);
        }
        while (ParseOptional(SyntaxKind.DotToken))
        {
            if (token == SyntaxKind.LessThanToken)
            {
                // The entity is part of a JSDoc-style generic. We will use the gap between `typeName` and
                // `typeArguments` to report it as a grammar error in the checker.
                break;
            }
            entity = FinishNode(factory.NewQualifiedName(entity, ParseRightSideOfDot(allowReservedWords, false /*allowPrivateIdentifiers*/, true /*allowUnicodeEscapeSequenceInIdentifierName*/)), pos);
        }
        return entity;
    }

    private Node ParseRightSideOfDot(bool allowIdentifierNames, bool allowPrivateIdentifiers, bool allowUnicodeEscapeSequenceInIdentifierName)
    {
        // Technically a keyword is valid here as all identifiers and keywords are identifier names.
        // However, often we'll encounter this in error situations when the identifier or keyword
        // is actually starting another valid construct.
        //
        // So, we check for the following specific case:
        //
        //      name.
        //      identifierOrKeyword identifierNameOrKeyword
        //
        // Note: the newlines are important here.  For example, if that above code
        // were rewritten into:
        //
        //      name.identifierOrKeyword
        //      identifierNameOrKeyword
        //
        // Then we would consider it valid.  That's because ASI would take effect and
        // the code would be implicitly: "name.identifierOrKeyword; identifierNameOrKeyword".
        // In the first case though, ASI will not take effect because there is not a
        // line terminator after the identifier or keyword.
        if (HasPrecedingLineBreak() && Utilities.TokenIsIdentifierOrKeyword(token) && LookAhead(NextTokenIsIdentifierOrKeywordOnSameLine))
        {
            // Report that we need an identifier.  However, report it right after the dot,
            // and not on the next token.  This is because the next token might actually
            // be an identifier and the error would be quite confusing.
            ParseErrorAt(NodePos(), NodePos(), global::Ts.Diagnostics.Diagnostics.Identifier_expected);
            return CreateMissingIdentifier();
        }
        if (token == SyntaxKind.PrivateIdentifier)
        {
            Node node = ParsePrivateIdentifier();
            if (allowPrivateIdentifiers)
            {
                return node;
            }
            ParseErrorAt(NodePos(), NodePos(), global::Ts.Diagnostics.Diagnostics.Identifier_expected);
            return CreateMissingIdentifier();
        }
        if (allowIdentifierNames)
        {
            if (allowUnicodeEscapeSequenceInIdentifierName)
            {
                return ParseIdentifierName();
            }
            return ParseIdentifierNameErrorOnUnicodeEscapeSequence();
        }
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        Node id = ParseIdentifier();
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return id;
    }

    private SyntaxKind ReScanSlashToken()
    {
        token = scanner.ReScanSlashToken();
        return token;
    }

    private Node ParsePropertyName()
    {
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        Node prop = ParsePropertyNameWorker(true /*allowComputedPropertyNames*/);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return prop;
    }

    private Node ParsePropertyNameWorker(bool allowComputedPropertyNames)
    {
        if (token == SyntaxKind.StringLiteral || token == SyntaxKind.NumericLiteral || token == SyntaxKind.BigIntLiteral)
        {
            Node literal = ParseLiteralExpression(true /*intern*/);
            return literal;
        }
        if (allowComputedPropertyNames && token == SyntaxKind.OpenBracketToken)
        {
            return ParseComputedPropertyName();
        }
        if (token == SyntaxKind.PrivateIdentifier)
        {
            return ParsePrivateIdentifier();
        }
        return ParseIdentifierName();
    }

    private Node ParseComputedPropertyName()
    {
        // PropertyName [Yield]:
        //      LiteralPropertyName
        //      ComputedPropertyName[?Yield]
        int pos = NodePos();
        ParseExpected(SyntaxKind.OpenBracketToken);
        // We parse any expression (including a comma expression). But the grammar
        // says that only an assignment expression is allowed, so the grammar checker
        // will error if it sees a comma expression.
        Node expression = ParseExpressionAllowIn();
        ParseExpected(SyntaxKind.CloseBracketToken);
        return FinishNode(factory.NewComputedPropertyName(expression), pos);
    }

    // ── identifiers ──

    private Node ParseIdentifierNameErrorOnUnicodeEscapeSequence()
    {
        if (scanner.HasUnicodeEscape() || scanner.HasExtendedUnicodeEscape())
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Unicode_escape_sequence_cannot_appear_here);
        }
        return CreateIdentifier(Utilities.TokenIsIdentifierOrKeyword(token));
    }

    private Node ParseIdentifierName()
    {
        return ParseIdentifierNameWithDiagnostic(null);
    }

    private Node ParseIdentifierNameWithDiagnostic(Message? diagnosticMessage)
    {
        return CreateIdentifierWithDiagnostic(Utilities.TokenIsIdentifierOrKeyword(token), diagnosticMessage, null);
    }

    private Node ParseIdentifier()
    {
        return ParseIdentifierWithDiagnostic(null, null);
    }

    // ── predicates / lookaheads ──

    private bool IsImplementsClause()
    {
        return token == SyntaxKind.ImplementsKeyword && LookAhead(NextTokenIsIdentifierOrKeyword);
    }

    private bool NextIsNotDot()
    {
        return NextToken() != SyntaxKind.DotToken;
    }

    private bool NextTokenIsFromKeywordOrEqualsToken()
    {
        NextToken();
        return token == SyntaxKind.FromKeyword || token == SyntaxKind.EqualsToken;
    }

    private bool NextTokenIsIdentifier()
    {
        NextToken();
        return IsIdentifier();
    }

    private bool NextTokenIsIdentifierOnSameLine()
    {
        NextToken();
        return IsIdentifier() && !HasPrecedingLineBreak();
    }

    private bool NextTokenIsEqualsOrSemicolonOrColonToken()
    {
        NextToken();
        return token == SyntaxKind.EqualsToken || token == SyntaxKind.SemicolonToken || token == SyntaxKind.ColonToken;
    }

    private bool NextTokenCanFollowDefaultKeyword()
    {
        switch (NextToken())
        {
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.AtToken:
                return true;
            case SyntaxKind.AbstractKeyword:
                return LookAhead(NextTokenIsClassKeywordOnSameLine);
            case SyntaxKind.AsyncKeyword:
                return LookAhead(NextTokenIsFunctionKeywordOnSameLine);
        }
        return false;
    }

    private bool NextTokenIsIdentifierOrKeyword()
    {
        return Utilities.TokenIsIdentifierOrKeyword(NextToken());
    }

    private bool NextTokenIsIdentifierOrKeywordOrGreaterThan()
    {
        return Utilities.TokenIsIdentifierOrKeywordOrGreaterThan(NextToken());
    }

    private bool NextTokenIsIdentifierOrKeywordOnSameLine()
    {
        return NextTokenIsIdentifierOrKeyword() && !HasPrecedingLineBreak();
    }

    private bool NextTokenIsFunctionKeywordOnSameLine()
    {
        return NextToken() == SyntaxKind.FunctionKeyword && !HasPrecedingLineBreak();
    }

    private bool CanFollowGetOrSetKeyword()
    {
        return token == SyntaxKind.OpenBracketToken || IsLiteralPropertyName();
    }

    private bool NextTokenIsOpenBrace()
    {
        return NextToken() == SyntaxKind.OpenBraceToken;
    }

    private bool NextTokenIsNewKeyword()
    {
        return NextToken() == SyntaxKind.NewKeyword;
    }

    private bool NextTokenIsDot()
    {
        return NextToken() == SyntaxKind.DotToken;
    }

    private bool ScanStartOfNamedTupleElement()
    {
        if (token == SyntaxKind.DotDotDotToken)
        {
            return Utilities.TokenIsIdentifierOrKeyword(NextToken()) && NextTokenIsColonOrQuestionColon();
        }
        return Utilities.TokenIsIdentifierOrKeyword(token) && NextTokenIsColonOrQuestionColon();
    }

    private bool NextTokenIsColonOrQuestionColon()
    {
        return NextToken() == SyntaxKind.ColonToken || token == SyntaxKind.QuestionToken && NextToken() == SyntaxKind.ColonToken;
    }

    // ── error helpers ──

    private void ParseSemicolonAfterPropertyName(Node? name, Node? typeNode, Node? initializer)
    {
        if (token == SyntaxKind.AtToken && !HasPrecedingLineBreak())
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Decorators_must_precede_the_name_and_all_keywords_of_property_declarations);
            return;
        }
        if (token == SyntaxKind.OpenParenToken)
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Cannot_start_a_function_call_in_a_type_annotation);
            NextToken();
            return;
        }
        if (typeNode != null && !CanParseSemicolon())
        {
            if (initializer != null)
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.SemicolonToken));
            }
            else
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Expected_for_property_initializer);
            }
            return;
        }
        if (TryParseSemicolon())
        {
            return;
        }
        if (initializer != null)
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.SemicolonToken));
            return;
        }
        ParseErrorForMissingSemicolonAfter(name);
    }

    private void ParseErrorForMissingSemicolonAfter(Node? node)
    {
        Node n = node!;
        // Tagged template literals are sometimes used in places where only simple strings are allowed, i.e.:
        //   module `M1` {
        //   ^^^^^^^^^^^ This block is parsed as a template literal like module`M1`.
        if (n.Kind == SyntaxKind.TaggedTemplateExpression)
        {
            ParseErrorAtRange(SkipRangeTrivia(n.AsTaggedTemplateExpression().Template!.Loc), global::Ts.Diagnostics.Diagnostics.Module_declaration_names_may_only_use_or_quoted_strings);
            return;
        }
        // Otherwise, if this isn't a well-known keyword-like identifier, give the generic fallback message.
        string expressionText = "";
        if (n.Kind == SyntaxKind.Identifier)
        {
            expressionText = n.Text();
        }
        if (expressionText == "")
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.SemicolonToken));
            return;
        }
        int pos = global::Ts.Scanner.Scanner.SkipTrivia(sourceText, n.Pos);
        // Some known keywords are likely signs of syntax being used improperly.
        switch (expressionText)
        {
            case "const":
            case "let":
            case "var":
                ParseErrorAt(pos, n.End, global::Ts.Diagnostics.Diagnostics.Variable_declaration_not_allowed_at_this_location);
                return;
            case "declare":
                // If a declared node failed to parse, it would have emitted a diagnostic already.
                return;
            case "interface":
                ParseErrorForInvalidName(global::Ts.Diagnostics.Diagnostics.Interface_name_cannot_be_0, global::Ts.Diagnostics.Diagnostics.Interface_must_be_given_a_name, SyntaxKind.OpenBraceToken);
                return;
            case "is":
                ParseErrorAt(pos, scanner.TokenStart(), global::Ts.Diagnostics.Diagnostics.A_type_predicate_is_only_allowed_in_return_type_position_for_functions_and_methods);
                return;
            case "module":
            case "namespace":
                ParseErrorForInvalidName(global::Ts.Diagnostics.Diagnostics.Namespace_name_cannot_be_0, global::Ts.Diagnostics.Diagnostics.Namespace_must_be_given_a_name, SyntaxKind.OpenBraceToken);
                return;
            case "type":
                ParseErrorForInvalidName(global::Ts.Diagnostics.Diagnostics.Type_alias_name_cannot_be_0, global::Ts.Diagnostics.Diagnostics.Type_alias_must_be_given_a_name, SyntaxKind.EqualsToken);
                return;
        }
        // The user alternatively might have misspelled or forgotten to add a space after a common keyword.
        string suggestion = CoreUtil.GetSpellingSuggestionForStrings(expressionText, viableKeywordSuggestions);
        if (suggestion == "")
        {
            suggestion = GetSpaceSuggestion(expressionText);
        }
        if (suggestion != "")
        {
            ParseErrorAt(pos, n.End, global::Ts.Diagnostics.Diagnostics.Unknown_keyword_or_identifier_Did_you_mean_0, suggestion);
            return;
        }
        // Unknown tokens are handled with their own errors in the scanner
        if (token == SyntaxKind.Unknown)
        {
            return;
        }
        // Otherwise, we know this some kind of unknown word, not just a missing expected semicolon.
        ParseErrorAt(pos, n.End, global::Ts.Diagnostics.Diagnostics.Unexpected_keyword_or_identifier);
    }

    // Go: getSpaceSuggestion (package-level helper in parser.go).
    private static string GetSpaceSuggestion(string expressionText)
    {
        foreach (string keyword in viableKeywordSuggestions)
        {
            if (expressionText.Length > keyword.Length + 2 && expressionText.StartsWith(keyword, StringComparison.Ordinal))
            {
                return keyword + " " + expressionText.Substring(keyword.Length);
            }
        }
        return "";
    }

    private void ParseErrorForInvalidName(Message? nameDiagnostic, Message? blankDiagnostic, SyntaxKind tokenIfBlankName)
    {
        if (token == tokenIfBlankName)
        {
            ParseErrorAtCurrentToken(blankDiagnostic!);
        }
        else
        {
            ParseErrorAtCurrentToken(nameDiagnostic!, scanner.TokenValue());
        }
    }

    // ── JSDoc whitespace (jsdoc.go) ──

    private bool IsNextNonwhitespaceTokenEndOfFile()
    {
        // We must use infinite lookahead, as there could be any number of newlines :(
        while (true)
        {
            NextTokenJSDoc();
            if (token == SyntaxKind.EndOfFile)
            {
                return true;
            }
            if (!(token == SyntaxKind.WhitespaceTrivia || token == SyntaxKind.NewLineTrivia))
            {
                return false;
            }
        }
    }

    private void SkipWhitespace()
    {
        if (token == SyntaxKind.WhitespaceTrivia || token == SyntaxKind.NewLineTrivia)
        {
            if (LookAhead(IsNextNonwhitespaceTokenEndOfFile))
            {
                return;
                // Don't skip whitespace prior to EoF (or end of comment) - that shouldn't be included in any node's range
            }
        }
        while (token == SyntaxKind.WhitespaceTrivia || token == SyntaxKind.NewLineTrivia)
        {
            NextTokenJSDoc();
        }
    }

    // ── resolution mode / JS syntax checks ──

    private ResolutionMode ParseResolutionMode(string? mode, int pos, int end)
    {
        if (mode == "import")
        {
            return ModuleKind.ESNext;
        }
        if (mode == "require")
        {
            return ModuleKind.CommonJS;
        }
        ParseErrorAt(pos, end, global::Ts.Diagnostics.Diagnostics.X_resolution_mode_should_be_either_require_or_import);
        return default;
    }

    private void JsErrorAtRange(TextRange loc, Message? message, params object?[] args)
    {
        jsDiagnostics.Add(Diagnostic.NewDiagnostic(null, new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, loc.Pos), loc.End), message!, args));
    }

    private Node CheckJSSyntax(Node? node)
    {
        Node n = node!;
        if ((n.Flags & NodeFlags.JavaScriptFile) == 0 || (n.Flags & (NodeFlags.JSDoc | NodeFlags.Reparsed)) != 0)
        {
            return n;
        }
        switch (n.Kind)
        {
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.MethodDeclaration:
                {
                    Node? questionToken = n.QuestionToken();
                    if (questionToken != null && (questionToken.Flags & NodeFlags.Reparsed) == 0 && ParserStatementsAstGaps.IsQuestionToken(questionToken))
                    {
                        JsErrorAtRange(questionToken.Loc, global::Ts.Diagnostics.Diagnostics.The_0_modifier_can_only_be_used_in_TypeScript_files, "?");
                    }
                }
                goto case SyntaxKind.MethodSignature; // fallthrough
            case SyntaxKind.MethodSignature:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.IndexSignature:
                {
                    Node? t;
                    if (AstUtilities.IsFunctionLike(n) && n.Body() == null)
                    {
                        JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.Signature_declarations_can_only_be_used_in_TypeScript_files);
                    }
                    else if ((t = n.Type()) != null && (t.Flags & NodeFlags.Reparsed) == 0)
                    {
                        JsErrorAtRange(t.Loc, global::Ts.Diagnostics.Diagnostics.Type_annotations_can_only_be_used_in_TypeScript_files);
                    }
                }
                break;
            case SyntaxKind.ImportDeclaration:
                {
                    Node? clause = n.ImportClause();
                    if (clause != null && clause.IsTypeOnly())
                    {
                        JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, "import type");
                    }
                }
                break;
            case SyntaxKind.ExportDeclaration:
                if (n.IsTypeOnly())
                {
                    JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, "export type");
                }
                break;
            case SyntaxKind.ImportSpecifier:
                if (n.IsTypeOnly())
                {
                    JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, "import...type");
                }
                break;
            case SyntaxKind.ExportSpecifier:
                if (n.IsTypeOnly())
                {
                    JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, "export...type");
                }
                break;
            case SyntaxKind.ImportEqualsDeclaration:
                JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_import_can_only_be_used_in_TypeScript_files);
                break;
            case SyntaxKind.ExportAssignment:
                if (n.AsExportAssignment().IsExportEquals)
                {
                    JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_export_can_only_be_used_in_TypeScript_files);
                }
                break;
            case SyntaxKind.HeritageClause:
                if (n.AsHeritageClause().Token == SyntaxKind.ImplementsKeyword)
                {
                    JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.X_implements_clauses_can_only_be_used_in_TypeScript_files);
                }
                break;
            case SyntaxKind.InterfaceDeclaration:
                JsErrorAtRange(n.Name()!.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, "interface");
                break;
            case SyntaxKind.ModuleDeclaration:
                JsErrorAtRange(n.Name()!.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, global::Ts.Scanner.Scanner.TokenToString(n.AsModuleDeclaration().Keyword));
                break;
            case SyntaxKind.TypeAliasDeclaration:
                JsErrorAtRange(n.Name()!.Loc, global::Ts.Diagnostics.Diagnostics.Type_aliases_can_only_be_used_in_TypeScript_files);
                break;
            case SyntaxKind.EnumDeclaration:
                JsErrorAtRange(n.Name()!.Loc, global::Ts.Diagnostics.Diagnostics.X_0_declarations_can_only_be_used_in_TypeScript_files, "enum");
                break;
            case SyntaxKind.NonNullExpression:
                JsErrorAtRange(n.Loc, global::Ts.Diagnostics.Diagnostics.Non_null_assertions_can_only_be_used_in_TypeScript_files);
                break;
            case SyntaxKind.AsExpression:
                JsErrorAtRange(n.Type()!.Loc, global::Ts.Diagnostics.Diagnostics.Type_assertion_expressions_can_only_be_used_in_TypeScript_files);
                break;
            case SyntaxKind.SatisfiesExpression:
                JsErrorAtRange(n.Type()!.Loc, global::Ts.Diagnostics.Diagnostics.Type_satisfaction_expressions_can_only_be_used_in_TypeScript_files);
                break;
        }
        // Check decorator placement in JS files
        CheckJSDecoratorSyntax(n);
        // Check absence of type parameters, type arguments and non-JavaScript modifiers
        switch (n.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ArrowFunction:
                {
                    NodeList? list = n.TypeParameterList();
                    if (list != null && CoreUtil.Some(list.Nodes, static x => (x.Flags & NodeFlags.Reparsed) == 0))
                    {
                        JsErrorAtRange(list.Loc, global::Ts.Diagnostics.Diagnostics.Type_parameter_declarations_can_only_be_used_in_TypeScript_files);
                    }
                }
                goto case SyntaxKind.VariableStatement; // fallthrough
            case SyntaxKind.VariableStatement:
            case SyntaxKind.PropertyDeclaration:
                foreach (Node modifier in n.ModifierNodes() ?? [])
                {
                    if ((modifier.Flags & NodeFlags.Reparsed) == 0 && modifier.Kind != SyntaxKind.Decorator && (AstUtilities.ModifierToFlag(modifier.Kind) & ModifierFlags.JavaScript) == 0)
                    {
                        JsErrorAtRange(modifier.Loc, global::Ts.Diagnostics.Diagnostics.The_0_modifier_can_only_be_used_in_TypeScript_files, global::Ts.Scanner.Scanner.TokenToString(modifier.Kind));
                    }
                }
                break;
            case SyntaxKind.Parameter:
                if (CoreUtil.Some(n.ModifierNodes(), AstUtilities.IsModifier))
                {
                    JsErrorAtRange(n.Modifiers()!.Loc, global::Ts.Diagnostics.Diagnostics.Parameter_modifiers_can_only_be_used_in_TypeScript_files);
                }
                break;
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.TaggedTemplateExpression:
                {
                    NodeList? list = n.TypeArgumentList();
                    if (list != null && CoreUtil.Some(list.Nodes, static x => (x.Flags & NodeFlags.Reparsed) == 0))
                    {
                        JsErrorAtRange(list.Loc, global::Ts.Diagnostics.Diagnostics.Type_arguments_can_only_be_used_in_TypeScript_files);
                    }
                }
                break;
        }
        return n;
    }

    // ── reparser helpers (reparser.go) ──

    private void FinishReparsedNode(Node? node, Node? locationNode)
    {
        Node n = node!;
        n.Flags = contextFlags | NodeFlags.Reparsed;
        n.Loc = locationNode!.Loc;
        OverrideParentInImmediateChildren(n);
    }

    private void FinishMutatedNode(Node? node)
    {
        OverrideParentInImmediateChildren(node!);
    }

    // Deep-clone the given node and add the clone to the reparsed clone list. The list is used by
    // ast.GetReparsedNodeForNode to locate reparsed clones of JSDoc nodes. Since the binder attaches
    // symbols to reparsed nodes and not to JSDoc nodes, we need the mapping when obtaining symbols and
    // types from JSDoc nodes.
    private Node? AddDeepCloneReparse(Node? node)
    {
        Node? clone = factory.DeepCloneReparse(node);
        if (clone != null)
        {
            reparsedClones.Add(clone);
        }
        return clone;
    }

    private Node AddTransformedReparse(Node? newNode, Node? old)
    {
        FinishReparsedNode(newNode, old);
        newNode!.Flags |= NodeFlags.ReparserTransformedLiteral;
        reparsedClones.Add(newNode);
        return newNode;
    }

    private Node? CheckNonIdentifierName(Node? name)
    {
        if (NodeTests.IsIdentifier(name!) && !global::Ts.Scanner.Scanner.IsValidIdentifier(name!.AsIdentifier().Text))
        {
            TextRange errLoc = name.Loc;
            if (errLoc.Len == 0) // missing name, emit error on the character before the missing name node
            {
                errLoc = new TextRange(name.Loc.Pos - 1, name.Loc.Pos);
            }
            ParseErrorAtRange(errLoc, global::Ts.Diagnostics.Diagnostics.Identifier_expected);
        }
        return name;
    }

    private Node? MakeQuestionIfOptional(Node? parameter)
    {
        JSDocParameterOrPropertyTag tag = parameter!.AsJSDocParameterOrPropertyTag();
        Node? questionToken = null;
        if (tag.IsBracketed || tag.TypeExpression != null && tag.TypeExpression.Type()!.Kind == SyntaxKind.JSDocOptionalType)
        {
            questionToken = factory.NewToken(SyntaxKind.QuestionToken);
            questionToken.Loc = parameter.Loc;
            questionToken.Flags = contextFlags | NodeFlags.Reparsed;
        }
        return questionToken;
    }

    private Node MakeNewCast(Node? t, Node? e, bool isAssertion)
    {
        Node assert;
        if (isAssertion)
        {
            assert = factory.NewAsExpression(e, t);
        }
        else
        {
            assert = factory.NewSatisfiesExpression(e, t);
        }
        FinishNodeWithEnd(assert, e!.Pos, e.End);
        return assert;
    }

    private bool TryReparseOptionalChain(Node? node)
    {
        Node n = node!;
        if ((n.Flags & NodeFlags.OptionalChain) != 0)
        {
            return true;
        }
        // check for an optional chain in a non-null expression
        if (NodeTests.IsNonNullExpression(n))
        {
            Node expr = n.Expression()!;
            while (NodeTests.IsNonNullExpression(expr) && (expr.Flags & NodeFlags.OptionalChain) == 0)
            {
                expr = expr.Expression()!;
            }
            if ((expr.Flags & NodeFlags.OptionalChain) != 0)
            {
                // this is part of an optional chain. Walk down from `node` to `expression` and set the flag.
                while (NodeTests.IsNonNullExpression(n))
                {
                    n.Flags |= NodeFlags.OptionalChain;
                    n = n.Expression()!;
                }
                return true;
            }
        }
        return false;
    }
}
