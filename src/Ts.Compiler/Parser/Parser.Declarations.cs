// Ported from tsgo internal/parser/parser.go — the Declarations cluster:
// class/interface/enum/module/function declarations, import/export, members,
// modifiers, decorators, type/value parameters, binding patterns, and the arrow
// function family. Positions are UTF-16 code units (plan/01 §6). Each method is a
// 1:1 translation of the corresponding Go method (function order roughly follows
// parser.go). Cross-cluster calls (parseStatement/parseType/parseExpression/…) go
// to sibling partials; some of those are still stubs (phase-3 parallel work) so a
// few deep runtime paths throw until they land — the AST structure is faithful.
#nullable enable
using System;
using System.Collections.Generic;

using Ts.Ast;
using Ts.Collections;
using Ts.Core;

using Message = Ts.Diagnostics.Message;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Parser;

public sealed partial class Parser
{
    // ── free-function modifier predicates (parser.go:1194/1814/1818/1966) ──

    private static bool IsDeclareModifier(Node modifier) => modifier.Kind == SyntaxKind.DeclareKeyword;

    private static bool IsExportModifier(Node modifier) => modifier.Kind == SyntaxKind.ExportKeyword;

    private static bool IsAsyncModifier(Node modifier) => modifier.Kind == SyntaxKind.AsyncKeyword;

    private static bool ModifierListHasAsync(ModifierList? modifiers) =>
        modifiers != null && CoreUtil.Some(modifiers.Nodes, IsAsyncModifier);

    // ── declarations dispatch (parser.go:1125) ──

    private Node ParseDeclaration()
    {
        // `parseListElement` attempted to get the reused node at this position, but the
        // ambient context flag was not yet set, so the node appeared not reusable there.
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ModifierList? modifiers = ParseModifiersEx(true, false, false);
        bool isAmbient = modifiers != null && CoreUtil.Some(modifiers.Nodes, IsDeclareModifier);
        if (isAmbient)
        {
            // !!! incremental parsing (tryReuseAmbientDeclaration)
            foreach (Node m in modifiers!.Nodes)
            {
                m.Flags |= NodeFlags.Ambient;
            }
            NodeFlags saveContextFlags = contextFlags;
            SetContextFlags(NodeFlags.Ambient, true);
            Node result = ParseDeclarationWorker(pos, jsdoc, modifiers);
            contextFlags = saveContextFlags;
            return result;
        }
        return ParseDeclarationWorker(pos, jsdoc, modifiers);
    }

    private Node ParseDeclarationWorker(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        switch (token)
        {
            case SyntaxKind.VarKeyword:
            case SyntaxKind.LetKeyword:
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.UsingKeyword:
                return ParseVariableStatement(pos, jsdoc, modifiers);
            case SyntaxKind.AwaitKeyword:
                if (IsAwaitUsingDeclaration())
                {
                    return ParseVariableStatement(pos, jsdoc, modifiers);
                }
                break;
            case SyntaxKind.FunctionKeyword:
                return ParseFunctionDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.ClassKeyword:
                return ParseClassDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.InterfaceKeyword:
                return ParseInterfaceDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.TypeKeyword:
                return ParseTypeAliasDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.EnumKeyword:
                return ParseEnumDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.GlobalKeyword:
            case SyntaxKind.ModuleKeyword:
            case SyntaxKind.NamespaceKeyword:
                return ParseModuleDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.ImportKeyword:
                return ParseImportDeclarationOrImportEqualsDeclaration(pos, jsdoc, modifiers);
            case SyntaxKind.ExportKeyword:
                NextToken();
                switch (token)
                {
                    case SyntaxKind.DefaultKeyword:
                    case SyntaxKind.EqualsToken:
                        return ParseExportAssignment(pos, jsdoc, modifiers);
                    case SyntaxKind.AsKeyword:
                        return ParseNamespaceExportDeclaration(pos, jsdoc, modifiers);
                    default:
                        return ParseExportDeclaration(pos, jsdoc, modifiers);
                }
        }
        if (modifiers != null)
        {
            // We encountered decorators and/or modifiers and assumed a declaration would follow.
            // For recovery and error reporting purposes, return an incomplete declaration.
            ParseErrorAt(NodePos(), NodePos(), Diag.Declaration_expected);
            return FinishNode(factory.NewMissingDeclaration(modifiers), pos);
        }
        throw new InvalidOperationException("Unhandled case in parseDeclarationWorker");
    }

    private bool IsLetDeclaration()
    {
        // In ES6 'let' always starts a lexical declaration if followed by an identifier or { or [.
        return LookAhead(NextTokenIsBindingIdentifierOrStartOfDestructuring);
    }

    private bool NextTokenIsBindingIdentifierOrStartOfDestructuring()
    {
        NextToken();
        return IsBindingIdentifier() || token == SyntaxKind.OpenBraceToken || token == SyntaxKind.OpenBracketToken;
    }

    // ── variable declarations (parser.go:1546) ──

    private Node ParseVariableStatement(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        Node declarationList = ParseVariableDeclarationList(false);
        ParseSemicolon();
        Node result = FinishNode(factory.NewVariableStatement(modifiers, declarationList), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private Node ParseVariableDeclarationList(bool inForStatementInitializer)
    {
        int pos = NodePos();
        NodeFlags flags = NodeFlags.None;
        switch (token)
        {
            case SyntaxKind.VarKeyword:
                flags = NodeFlags.None;
                break;
            case SyntaxKind.LetKeyword:
                flags = NodeFlags.Let;
                break;
            case SyntaxKind.ConstKeyword:
                flags = NodeFlags.Const;
                break;
            case SyntaxKind.UsingKeyword:
                flags = NodeFlags.Using;
                break;
            case SyntaxKind.AwaitKeyword:
                if (IsAwaitUsingDeclaration())
                {
                    flags = NodeFlags.AwaitUsing;
                    NextToken();
                }
                break;
            default:
                throw new InvalidOperationException("Unhandled case in parseVariableDeclarationList");
        }
        NextToken();
        // The user may have written `for (let of X) {}`; parse an empty declaration list and
        // treat 'of' as a keyword. We need to look ahead because 'of' is a valid identifier.
        NodeList? declarations;
        if (token == SyntaxKind.OfKeyword && LookAhead(NextIsIdentifierAndCloseParen))
        {
            declarations = CreateMissingList();
        }
        else
        {
            NodeFlags saveContextFlags = contextFlags;
            SetContextFlags(NodeFlags.DisallowInContext, inForStatementInitializer);
            Func<Node?> parseElement = inForStatementInitializer
                ? () => ParseVariableDeclaration()
                : () => ParseVariableDeclarationAllowExclamation();
            declarations = ParseDelimitedList(ParsingContext.VariableDeclarations, parseElement);
            contextFlags = saveContextFlags;
        }
        return FinishNode(factory.NewVariableDeclarationList(declarations, flags), pos);
    }

    private Node ParseVariableDeclaration() => ParseVariableDeclarationWorker(false);

    private Node ParseVariableDeclarationAllowExclamation() => ParseVariableDeclarationWorker(true);

    private Node ParseVariableDeclarationWorker(bool allowExclamation)
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        Node name = ParseIdentifierOrPatternWithDiagnostic(Diag.Private_identifiers_are_not_allowed_in_variable_declarations);
        Node? exclamationToken = null;
        if (allowExclamation && name.Kind == SyntaxKind.Identifier && token == SyntaxKind.ExclamationToken && !HasPrecedingLineBreak())
        {
            exclamationToken = ParseTokenNode();
        }
        Node? typeNode = ParseTypeAnnotation();
        Node? initializer = null;
        if (token != SyntaxKind.InKeyword && token != SyntaxKind.OfKeyword)
        {
            initializer = ParseInitializer();
        }
        Node result = FinishNode(factory.NewVariableDeclaration(name, exclamationToken, typeNode, initializer), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    // ── binding patterns (parser.go:1649) ──

    private Node ParseArrayBindingPattern()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.OpenBracketToken);
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.DisallowInContext, false);
        NodeList? elements = ParseDelimitedList(ParsingContext.ArrayBindingElements, ParseArrayBindingElement);
        contextFlags = saveContextFlags;
        ParseExpected(SyntaxKind.CloseBracketToken);
        return FinishNode(factory.NewBindingPattern(SyntaxKind.ArrayBindingPattern, elements), pos);
    }

    private Node ParseArrayBindingElement()
    {
        int pos = NodePos();
        Node? dotDotDotToken = null;
        Node? name = null;
        Node? initializer = null;
        if (token != SyntaxKind.CommaToken)
        {
            // These are all nil for a missing element.
            dotDotDotToken = ParseOptionalToken(SyntaxKind.DotDotDotToken);
            name = ParseIdentifierOrPattern();
            initializer = ParseInitializer();
        }
        return FinishNode(factory.NewBindingElement(dotDotDotToken, null, name, initializer), pos);
    }

    private Node ParseObjectBindingPattern()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.OpenBraceToken);
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.DisallowInContext, false);
        NodeList? elements = ParseDelimitedList(ParsingContext.ObjectBindingElements, ParseObjectBindingElement);
        contextFlags = saveContextFlags;
        ParseExpected(SyntaxKind.CloseBraceToken);
        return FinishNode(factory.NewBindingPattern(SyntaxKind.ObjectBindingPattern, elements), pos);
    }

    private Node ParseObjectBindingElement()
    {
        int pos = NodePos();
        Node? dotDotDotToken = ParseOptionalToken(SyntaxKind.DotDotDotToken);
        bool tokenIsIdentifier = IsBindingIdentifier();
        Node? propertyName = ParsePropertyName();
        Node? name;
        if (tokenIsIdentifier && token != SyntaxKind.ColonToken)
        {
            name = propertyName;
            propertyName = null;
        }
        else
        {
            ParseExpected(SyntaxKind.ColonToken);
            name = ParseIdentifierOrPattern();
        }
        Node? initializer = ParseInitializer();
        return FinishNode(factory.NewBindingElement(dotDotDotToken, propertyName, name, initializer), pos);
    }

    // ── functions (parser.go:1716) ──

    private Node ParseFunctionDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParseExpected(SyntaxKind.FunctionKeyword);
        Node? asteriskToken = ParseOptionalToken(SyntaxKind.AsteriskToken);
        // We don't parse the name here in await context; a grammar error is reported in the checker.
        Node? name = null;
        if (modifiers == null || (modifiers.ModifierFlags & ModifierFlags.Default) == 0 || IsBindingIdentifier())
        {
            name = ParseBindingIdentifier();
        }
        ParseFlags signatureFlags = (asteriskToken != null ? ParseFlags.Yield : ParseFlags.None) |
            (modifiers != null && (modifiers.ModifierFlags & ModifierFlags.Async) != 0 ? ParseFlags.Await : ParseFlags.None);
        NodeList? typeParameters = ParseTypeParameters();
        NodeFlags saveContextFlags = contextFlags;
        if (modifiers != null && (modifiers.ModifierFlags & ModifierFlags.Export) != 0)
        {
            SetContextFlags(NodeFlags.AwaitContext, true);
        }
        NodeList parameters = ParseParameters(signatureFlags);
        Node? returnType = ParseReturnType(SyntaxKind.ColonToken, false);
        Node? body = ParseFunctionBlockOrSemicolon(signatureFlags, Diag.X_or_expected);
        contextFlags = saveContextFlags;
        Node result = FinishNode(factory.NewFunctionDeclaration(modifiers, asteriskToken, name, typeParameters, parameters, returnType, null, body), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    // ── classes (parser.go:1740) ──

    private Node ParseClassDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers) =>
        ParseClassDeclarationOrExpression(pos, jsdoc, modifiers, SyntaxKind.ClassDeclaration);

    private Node ParseClassExpression() =>
        ParseClassDeclarationOrExpression(NodePos(), JsdocScannerInfo(), null, SyntaxKind.ClassExpression);

    private Node ParseClassDeclarationOrExpression(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers, SyntaxKind kind)
    {
        NodeFlags saveContextFlags = contextFlags;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        ParseExpected(SyntaxKind.ClassKeyword);
        // We don't parse the name here in await context; a grammar error is reported in the checker.
        Node? name = ParseNameOfClassDeclarationOrExpression();
        NodeList? typeParameters = ParseTypeParameters();
        if (modifiers != null && CoreUtil.Some(modifiers.Nodes, IsExportModifier))
        {
            SetContextFlags(NodeFlags.AwaitContext, true);
        }
        NodeList? heritageClauses = ParseHeritageClauses();
        NodeList members;
        if (ParseExpected(SyntaxKind.OpenBraceToken))
        {
            // ClassTail : ClassHeritage opt { ClassBody opt }
            members = ParseList(ParsingContext.ClassMembers, ParseClassElement);
            ParseExpected(SyntaxKind.CloseBraceToken);
        }
        else
        {
            members = CreateMissingList();
        }
        contextFlags = saveContextFlags;
        Node result;
        if (modifiers != null && (AstUtilities.ModifiersToFlags(modifiers.Nodes) & ModifierFlags.Ambient) != 0)
        {
            statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        }
        if (kind == SyntaxKind.ClassDeclaration)
        {
            result = factory.NewClassDeclaration(modifiers, name, typeParameters, heritageClauses, members);
        }
        else
        {
            result = factory.NewClassExpression(modifiers, name, typeParameters, heritageClauses, members);
        }
        FinishNode(result, pos);
        WithJSDoc(result, jsdoc);
        if ((result.Flags & NodeFlags.JavaScriptFile) != 0)
        {
            CheckJSSyntax(result);
            if (heritageClauses != null)
            {
                foreach (Node clause in heritageClauses.Nodes)
                {
                    if (clause.AsHeritageClause().Token == SyntaxKind.ExtendsKeyword)
                    {
                        foreach (Node expr in clause.AsHeritageClause().Types!.Nodes)
                        {
                            CheckJSSyntax(expr);
                        }
                    }
                }
            }
        }
        return result;
    }

    private Node? ParseNameOfClassDeclarationOrExpression()
    {
        // 'implements' is a future reserved word so 'class implements' is ambiguous:
        //   - class expression with omitted name, 'implements' starts heritage clause
        //   - class named 'implements'
        // isImplementsClause disambiguates.
        if (IsBindingIdentifier() && !IsImplementsClause())
        {
            bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
            Node id = CreateIdentifier(IsBindingIdentifier());
            statementHasAwaitIdentifier = saveHasAwaitIdentifier;
            return id;
        }
        return null;
    }

    private NodeList? ParseHeritageClauses()
    {
        // ClassTail : ClassHeritage opt { ClassBody opt }
        if (IsHeritageClause())
        {
            return ParseList(ParsingContext.HeritageClauses, ParseHeritageClause);
        }
        return null;
    }

    private Node ParseHeritageClause()
    {
        int pos = NodePos();
        SyntaxKind kind = token;
        NextToken();
        NodeList? types = ParseDelimitedList(ParsingContext.HeritageClauseElement, ParseExpressionWithTypeArguments);
        return CheckJSSyntax(FinishNode(factory.NewHeritageClause(kind, types), pos));
    }

    private Node ParseExpressionWithTypeArguments()
    {
        int pos = NodePos();
        Node expression = ParseLeftHandSideExpressionOrHigher();
        if (NodeTests.IsExpressionWithTypeArguments(expression))
        {
            return expression;
        }
        NodeList? typeArguments = ParseTypeArguments();
        return FinishNode(factory.NewExpressionWithTypeArguments(expression, typeArguments), pos);
    }

    private Node ParseClassElement()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        if (token == SyntaxKind.SemicolonToken)
        {
            NextToken();
            Node semicolon = FinishNode(factory.NewSemicolonClassElement(), pos);
            WithJSDoc(semicolon, jsdoc);
            return semicolon;
        }
        ModifierList? modifiers = ParseModifiersEx(true, true, true);
        if (token == SyntaxKind.StaticKeyword && LookAhead(NextTokenIsOpenBrace))
        {
            return ParseClassStaticBlockDeclaration(pos, jsdoc, modifiers);
        }
        if (ParseContextualModifier(SyntaxKind.GetKeyword))
        {
            return ParseAccessorDeclaration(pos, jsdoc, modifiers, SyntaxKind.GetAccessor, ParseFlags.None);
        }
        if (ParseContextualModifier(SyntaxKind.SetKeyword))
        {
            return ParseAccessorDeclaration(pos, jsdoc, modifiers, SyntaxKind.SetAccessor, ParseFlags.None);
        }
        if (token == SyntaxKind.ConstructorKeyword || token == SyntaxKind.StringLiteral)
        {
            Node? constructorDeclaration = TryParseConstructorDeclaration(pos, jsdoc, modifiers);
            if (constructorDeclaration != null)
            {
                return constructorDeclaration;
            }
        }
        if (IsIndexSignature())
        {
            return CheckJSSyntax(ParseIndexSignatureDeclaration(pos, jsdoc, modifiers));
        }
        // It is very important that we check this *after* checking indexers because the [ token
        // can start an index signature or a computed property name.
        if (Utilities.TokenIsIdentifierOrKeyword(token) || token == SyntaxKind.StringLiteral || token == SyntaxKind.NumericLiteral ||
            token == SyntaxKind.BigIntLiteral || token == SyntaxKind.AsteriskToken || token == SyntaxKind.OpenBracketToken)
        {
            bool isAmbient = modifiers != null && CoreUtil.Some(modifiers.Nodes, IsDeclareModifier);
            if (isAmbient)
            {
                foreach (Node m in modifiers!.Nodes)
                {
                    m.Flags |= NodeFlags.Ambient;
                }
                NodeFlags saveContextFlags = contextFlags;
                SetContextFlags(NodeFlags.Ambient, true);
                Node result = ParsePropertyOrMethodDeclaration(pos, jsdoc, modifiers);
                contextFlags = saveContextFlags;
                return result;
            }
            return ParsePropertyOrMethodDeclaration(pos, jsdoc, modifiers);
        }
        if (modifiers != null)
        {
            // Treat this as a property declaration with a missing name.
            ParseErrorAt(NodePos(), NodePos(), Diag.Declaration_expected);
            Node name = CreateMissingIdentifier();
            return ParsePropertyDeclaration(pos, jsdoc, modifiers, name, null);
        }
        // 'isClassMemberStart' should have hinted not to attempt parsing.
        throw new InvalidOperationException("Should not have attempted to parse class member declaration.");
    }

    private Node ParseClassStaticBlockDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParseExpectedToken(SyntaxKind.StaticKeyword);
        Node body = ParseClassStaticBlockBody();
        Node result = FinishNode(factory.NewClassStaticBlockDeclaration(modifiers, body), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseClassStaticBlockBody()
    {
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.YieldContext, false);
        SetContextFlags(NodeFlags.AwaitContext, true);
        Node body = ParseBlock(false, null);
        contextFlags = saveContextFlags;
        return body;
    }

    private Node? TryParseConstructorDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParserState state = Mark();
        if (token == SyntaxKind.ConstructorKeyword ||
            (token == SyntaxKind.StringLiteral && scanner.TokenValue() == "constructor" && LookAhead(NextTokenIsOpenParen)))
        {
            NextToken();
            NodeList? typeParameters = ParseTypeParameters();
            NodeList parameters = ParseParameters(ParseFlags.None);
            Node? returnType = ParseReturnType(SyntaxKind.ColonToken, false);
            Node? body = ParseFunctionBlockOrSemicolon(ParseFlags.None, Diag.X_or_expected);
            Node result = FinishNode(factory.NewConstructorDeclaration(modifiers, typeParameters, parameters, returnType, null, body), pos);
            WithJSDoc(result, jsdoc);
            CheckJSSyntax(result);
            return result;
        }
        Rewind(state);
        return null;
    }

    private Node ParsePropertyOrMethodDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        Node? asteriskToken = ParseOptionalToken(SyntaxKind.AsteriskToken);
        Node name = ParsePropertyName();
        // Not legal per the grammar, but allowed here and reported by the grammar checker.
        Node? questionToken = ParseOptionalToken(SyntaxKind.QuestionToken);
        if (asteriskToken != null || token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken)
        {
            return ParseMethodDeclaration(pos, jsdoc, modifiers, asteriskToken, name, questionToken, Diag.X_or_expected);
        }
        return ParsePropertyDeclaration(pos, jsdoc, modifiers, name, questionToken);
    }

    private Node ParseMethodDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers, Node? asteriskToken, Node? name, Node? questionToken, Message? diagnosticMessage)
    {
        ParseFlags signatureFlags = (asteriskToken != null ? ParseFlags.Yield : ParseFlags.None) |
            (ModifierListHasAsync(modifiers) ? ParseFlags.Await : ParseFlags.None);
        NodeList? typeParameters = ParseTypeParameters();
        NodeList parameters = ParseParameters(signatureFlags);
        Node? typeNode = ParseReturnType(SyntaxKind.ColonToken, false);
        Node? body = ParseFunctionBlockOrSemicolon(signatureFlags, diagnosticMessage);
        Node result = FinishNode(factory.NewMethodDeclaration(modifiers, asteriskToken, name, questionToken, typeParameters, parameters, typeNode, null, body), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private Node ParsePropertyDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers, Node? name, Node? questionToken)
    {
        Node? postfixToken = questionToken;
        if (postfixToken == null && !HasPrecedingLineBreak())
        {
            postfixToken = ParseOptionalToken(SyntaxKind.ExclamationToken);
        }
        Node? typeNode = ParseTypeAnnotation();
        Node? initializer = DoInContext(NodeFlags.YieldContext | NodeFlags.AwaitContext | NodeFlags.DisallowInContext, false, ParseInitializer);
        ParseSemicolonAfterPropertyName(name, typeNode, initializer);
        Node result = FinishNode(factory.NewPropertyDeclaration(modifiers, name, postfixToken, typeNode, initializer), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    // ── interface / type alias / enum (parser.go:2085) ──

    private Node ParseInterfaceDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParseExpected(SyntaxKind.InterfaceKeyword);
        Node name = ParseIdentifier();
        NodeList? typeParameters = ParseTypeParameters();
        NodeList? heritageClauses = ParseHeritageClauses();
        NodeList members = ParseObjectTypeMembers();
        Node result = FinishNode(factory.NewInterfaceDeclaration(modifiers, name, typeParameters, heritageClauses, members), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private Node ParseTypeAliasDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParseExpected(SyntaxKind.TypeKeyword);
        if (HasPrecedingLineBreak())
        {
            ParseErrorAtCurrentToken(Diag.Line_break_not_permitted_here);
        }
        Node name = ParseIdentifier();
        NodeList? typeParameters = ParseTypeParameters();
        ParseExpected(SyntaxKind.EqualsToken);
        Node? typeNode;
        if (token == SyntaxKind.IntrinsicKeyword && LookAhead(NextIsNotDot))
        {
            typeNode = ParseKeywordTypeNode();
        }
        else
        {
            typeNode = ParseType();
        }
        ParseSemicolon();
        Node result = FinishNode(factory.NewTypeAliasDeclaration(modifiers, name, typeParameters, typeNode), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    // In an ambient declaration the grammar only allows integer literals as initializers.
    private Node ParseEnumMember()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        Node name = ParsePropertyName();
        Node? initializer = DoInContext(NodeFlags.DisallowInContext, false, ParseInitializer);
        Node result = FinishNode(factory.NewEnumMember(name, initializer), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseEnumDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        ParseExpected(SyntaxKind.EnumKeyword);
        Node name = ParseIdentifier();
        NodeList? members;
        if (ParseExpected(SyntaxKind.OpenBraceToken))
        {
            NodeFlags saveContextFlags = contextFlags;
            SetContextFlags(NodeFlags.YieldContext | NodeFlags.AwaitContext, false);
            members = ParseDelimitedList(ParsingContext.EnumMembers, ParseEnumMember);
            contextFlags = saveContextFlags;
            ParseExpected(SyntaxKind.CloseBraceToken);
        }
        else
        {
            members = CreateMissingList();
        }
        Node result = FinishNode(factory.NewEnumDeclaration(modifiers, name, members), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return result;
    }

    // ── modules / namespaces (parser.go:2157) ──

    private Node ParseModuleDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        SyntaxKind keyword = SyntaxKind.ModuleKeyword;
        if (token == SyntaxKind.GlobalKeyword)
        {
            // global augmentation
            return ParseAmbientExternalModuleDeclaration(pos, jsdoc, modifiers);
        }
        else if (ParseOptional(SyntaxKind.NamespaceKeyword))
        {
            keyword = SyntaxKind.NamespaceKeyword;
        }
        else
        {
            ParseExpected(SyntaxKind.ModuleKeyword);
            if (token == SyntaxKind.StringLiteral)
            {
                return ParseAmbientExternalModuleDeclaration(pos, jsdoc, modifiers);
            }
        }
        return ParseModuleOrNamespaceDeclaration(pos, jsdoc, modifiers, false, keyword);
    }

    private Node ParseAmbientExternalModuleDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        Node name;
        SyntaxKind keyword = SyntaxKind.ModuleKeyword;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        if (token == SyntaxKind.GlobalKeyword)
        {
            // parse 'global' as name of global scope augmentation
            name = ParseIdentifier();
            keyword = SyntaxKind.GlobalKeyword;
        }
        else
        {
            // parse string literal
            name = ParseLiteralExpression(true);
        }
        Node? body = null;
        if (token == SyntaxKind.OpenBraceToken)
        {
            body = ParseModuleBlock();
        }
        else
        {
            ParseSemicolon();
        }
        Node result = FinishNode(factory.NewModuleDeclaration(modifiers, keyword, name, body), pos);
        WithJSDoc(result, jsdoc);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return result;
    }

    private Node ParseModuleBlock()
    {
        int pos = NodePos();
        NodeList? statements;
        if (ParseExpected(SyntaxKind.OpenBraceToken))
        {
            statements = ParseList(ParsingContext.BlockStatements, ParseStatement);
            ParseExpected(SyntaxKind.CloseBraceToken);
        }
        else
        {
            statements = CreateMissingList();
        }
        return FinishNode(factory.NewModuleBlock(statements), pos);
    }

    private Node ParseModuleOrNamespaceDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers, bool nested, SyntaxKind keyword)
    {
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        Node name = nested ? ParseIdentifierName() : ParseIdentifier();
        Node? body;
        if (ParseOptional(SyntaxKind.DotToken))
        {
            Node implicitExport = factory.NewModifier(SyntaxKind.ExportKeyword);
            implicitExport.Loc = new TextRange(NodePos(), NodePos());
            implicitExport.Flags = NodeFlags.Reparsed;
            ModifierList implicitModifiers = NewModifierList(implicitExport.Loc, new Node[] { implicitExport });
            body = ParseModuleOrNamespaceDeclaration(NodePos(), JSDocScannerInfo.None, implicitModifiers, true, keyword);
        }
        else
        {
            body = ParseModuleBlock();
        }
        Node result = FinishNode(factory.NewModuleDeclaration(modifiers, keyword, name, body), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return result;
    }

    // ── import / export (parser.go:2234) ──

    private Node ParseImportDeclarationOrImportEqualsDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParseExpected(SyntaxKind.ImportKeyword);
        int afterImportPos = NodePos();
        // We don't parse the identifier here in await context; a grammar error is reported in the checker.
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        Node? identifier = null;
        if (IsIdentifier())
        {
            identifier = ParseIdentifier();
        }
        SyntaxKind phaseModifier = SyntaxKind.Unknown;
        if (identifier != null && identifier.Text() == "type" &&
            (token != SyntaxKind.FromKeyword || (IsIdentifier() && LookAhead(NextTokenIsFromKeywordOrEqualsToken))) &&
            (IsIdentifier() || TokenAfterImportDefinitelyProducesImportDeclaration()))
        {
            phaseModifier = SyntaxKind.TypeKeyword;
            identifier = null;
            if (IsIdentifier())
            {
                identifier = ParseIdentifier();
            }
        }
        else if (identifier != null && identifier.Text() == "defer")
        {
            bool shouldParseAsDeferModifier;
            if (token == SyntaxKind.FromKeyword)
            {
                shouldParseAsDeferModifier = !LookAhead(NextTokenIsTokenStringLiteral);
            }
            else
            {
                shouldParseAsDeferModifier = token != SyntaxKind.CommaToken && token != SyntaxKind.EqualsToken;
            }
            if (shouldParseAsDeferModifier)
            {
                phaseModifier = SyntaxKind.DeferKeyword;
                identifier = null;
                if (IsIdentifier())
                {
                    identifier = ParseIdentifier();
                }
            }
        }
        if (identifier != null && !TokenAfterImportedIdentifierDefinitelyProducesImportDeclaration() && phaseModifier != SyntaxKind.DeferKeyword)
        {
            Node importEquals = CheckJSSyntax(ParseImportEqualsDeclaration(pos, jsdoc, modifiers, identifier, phaseModifier == SyntaxKind.TypeKeyword));
            statementHasAwaitIdentifier = saveHasAwaitIdentifier; // Import= is always parsed in an Await context
            return importEquals;
        }
        Node? importClause = TryParseImportClause(identifier, afterImportPos, phaseModifier, false);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier; // import clause is always parsed in an Await context
        Node moduleSpecifier = ParseModuleSpecifier();
        Node? attributes = TryParseImportAttributes();
        ParseSemicolon();
        Node result = FinishNode(factory.NewImportDeclaration(modifiers, importClause, moduleSpecifier, attributes), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private bool TokenAfterImportDefinitelyProducesImportDeclaration() =>
        token == SyntaxKind.AsteriskToken || token == SyntaxKind.OpenBraceToken;

    private bool TokenAfterImportedIdentifierDefinitelyProducesImportDeclaration() =>
        // In `import id ___`, the current token decides ImportDeclaration vs ImportEqualsDeclaration.
        token == SyntaxKind.CommaToken || token == SyntaxKind.FromKeyword;

    private Node ParseImportEqualsDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers, Node? identifier, bool isTypeOnly)
    {
        ParseExpected(SyntaxKind.EqualsToken);
        Node moduleReference = ParseModuleReference();
        ParseSemicolon();
        Node result = FinishNode(factory.NewImportEqualsDeclaration(modifiers, isTypeOnly, identifier, moduleReference), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseModuleReference()
    {
        if (token == SyntaxKind.RequireKeyword && LookAhead(NextTokenIsOpenParen))
        {
            return ParseExternalModuleReference();
        }
        return ParseEntityName(false, null);
    }

    private Node ParseExternalModuleReference()
    {
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        int pos = NodePos();
        ParseExpected(SyntaxKind.RequireKeyword);
        ParseExpected(SyntaxKind.OpenParenToken);
        Node expression = ParseModuleSpecifier();
        ParseExpected(SyntaxKind.CloseParenToken);
        Node result = FinishNode(factory.NewExternalModuleReference(expression), pos);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return result;
    }

    private Node ParseModuleSpecifier()
    {
        if (token == SyntaxKind.StringLiteral)
        {
            return ParseLiteralExpression(true);
        }
        // Arbitrary expressions are allowed here even though the grammar only allows string
        // literals; the grammar check pass verifies it is a string literal.
        return ParseExpression();
    }

    private Node? TryParseImportClause(Node? identifier, int pos, SyntaxKind phaseModifier, bool skipJSDocLeadingAsterisks)
    {
        // ImportDeclaration:
        //   import ImportClause from ModuleSpecifier ;
        //   import ModuleSpecifier;
        if (identifier != null || token == SyntaxKind.AsteriskToken || token == SyntaxKind.OpenBraceToken)
        {
            Node importClause = ParseImportClause(identifier, pos, phaseModifier, skipJSDocLeadingAsterisks);
            ParseExpected(SyntaxKind.FromKeyword);
            return importClause;
        }
        return null;
    }

    private Node ParseImportClause(Node? identifier, int pos, SyntaxKind phaseModifier, bool skipJSDocLeadingAsterisks)
    {
        // ImportClause:
        //   ImportedDefaultBinding | NameSpaceImport | NamedImports
        //   ImportedDefaultBinding, NameSpaceImport | ImportedDefaultBinding, NamedImports
        Node? namedBindings = null;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        if (identifier == null || ParseOptional(SyntaxKind.CommaToken))
        {
            if (skipJSDocLeadingAsterisks)
            {
                scanner.SetSkipJSDocLeadingAsterisks(true);
            }
            if (token == SyntaxKind.AsteriskToken)
            {
                namedBindings = ParseNamespaceImport();
            }
            else
            {
                namedBindings = ParseNamedImports();
            }
            if (skipJSDocLeadingAsterisks)
            {
                scanner.SetSkipJSDocLeadingAsterisks(false);
            }
        }
        Node result = FinishNode(factory.NewImportClause(phaseModifier, identifier, namedBindings), pos);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return result;
    }

    private Node ParseNamespaceImport()
    {
        // NameSpaceImport: * as ImportedBinding
        int pos = NodePos();
        ParseExpected(SyntaxKind.AsteriskToken);
        ParseExpected(SyntaxKind.AsKeyword);
        Node name = ParseIdentifier();
        return FinishNode(factory.NewNamespaceImport(name), pos);
    }

    private Node ParseNamedImports()
    {
        int pos = NodePos();
        // NamedImports: { } | { ImportsList } | { ImportsList, }
        NodeList? imports = ParseBracketedList(ParsingContext.ImportOrExportSpecifiers, ParseImportSpecifier, SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken);
        return FinishNode(factory.NewNamedImports(imports), pos);
    }

    private Node ParseImportSpecifier()
    {
        int pos = NodePos();
        (bool isTypeOnly, Node? propertyName, Node name) = ParseImportOrExportSpecifier(SyntaxKind.ImportSpecifier);
        Node identifierName;
        if (name.Kind == SyntaxKind.Identifier)
        {
            identifierName = name;
        }
        else
        {
            ParseErrorAtRange(SkipRangeTrivia(name.Loc), Diag.Identifier_expected);
            identifierName = NewIdentifier("");
            FinishNode(identifierName, name.Loc.Pos);
        }
        return CheckJSSyntax(FinishNode(factory.NewImportSpecifier(isTypeOnly, propertyName, identifierName), pos));
    }

    private (bool isTypeOnly, Node? propertyName, Node name) ParseImportOrExportSpecifier(SyntaxKind kind)
    {
        // ImportSpecifier: BindingIdentifier | ModuleExportName as BindingIdentifier
        // ExportSpecifier: ModuleExportName | ModuleExportName as ModuleExportName
        bool canParseAsKeyword = true;
        bool disallowKeywords = kind == SyntaxKind.ImportSpecifier;
        bool isTypeOnly = false;
        Node? propertyName = null;
        (Node name, bool nameOk) = ParseModuleExportName(disallowKeywords);
        if (name.Kind == SyntaxKind.Identifier && name.Text() == "type")
        {
            // If the first token of an import specifier is 'type' there are many possibilities,
            // especially with 'as' afterwards. See parser.go for the truth table.
            if (token == SyntaxKind.AsKeyword)
            {
                // { type as ...? }
                Node firstAs = ParseIdentifierName();
                if (token == SyntaxKind.AsKeyword)
                {
                    // { type as as ...? }
                    Node secondAs = ParseIdentifierName();
                    if (CanParseModuleExportName())
                    {
                        // { type as as something | "something" }
                        isTypeOnly = true;
                        propertyName = firstAs;
                        (name, nameOk) = ParseModuleExportName(disallowKeywords);
                        canParseAsKeyword = false;
                    }
                    else
                    {
                        // { type as as }
                        propertyName = name;
                        name = secondAs;
                        canParseAsKeyword = false;
                    }
                }
                else if (CanParseModuleExportName())
                {
                    // { type as something | "something" }
                    propertyName = name;
                    canParseAsKeyword = false;
                    (name, nameOk) = ParseModuleExportName(disallowKeywords);
                }
                else
                {
                    // { type as }
                    isTypeOnly = true;
                    name = firstAs;
                }
            }
            else if (CanParseModuleExportName())
            {
                // { type something | "something" ...? }
                isTypeOnly = true;
                (name, nameOk) = ParseModuleExportName(disallowKeywords);
            }
        }
        if (canParseAsKeyword && token == SyntaxKind.AsKeyword)
        {
            propertyName = name;
            ParseExpected(SyntaxKind.AsKeyword);
            (name, nameOk) = ParseModuleExportName(disallowKeywords);
        }
        if (!nameOk)
        {
            ParseErrorAtRange(SkipRangeTrivia(name.Loc), Diag.Identifier_expected);
        }
        return (isTypeOnly, propertyName, name);
    }

    private bool CanParseModuleExportName() =>
        Utilities.TokenIsIdentifierOrKeyword(token) || token == SyntaxKind.StringLiteral;

    private (Node, bool) ParseModuleExportName(bool disallowKeywords)
    {
        bool nameOk = true;
        if (token == SyntaxKind.StringLiteral)
        {
            return (ParseLiteralExpression(false), nameOk);
        }
        if (disallowKeywords && NodeTests.IsKeywordKind(token) && !IsIdentifier())
        {
            nameOk = false;
        }
        return (ParseIdentifierName(), nameOk);
    }

    private Node? TryParseImportAttributes()
    {
        if (token == SyntaxKind.WithKeyword || (token == SyntaxKind.AssertKeyword && !HasPrecedingLineBreak()))
        {
            if (token == SyntaxKind.AssertKeyword)
            {
                ParseErrorAtCurrentToken(Diag.Import_assertions_have_been_replaced_by_import_attributes_Use_with_instead_of_assert);
            }
            return ParseImportAttributes(token, false);
        }
        return null;
    }

    private Node ParseExportAssignment(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        NodeFlags saveContextFlags = contextFlags;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        SetContextFlags(NodeFlags.AwaitContext, true);
        bool isExportEquals = false;
        if (ParseOptional(SyntaxKind.EqualsToken))
        {
            isExportEquals = true;
        }
        else
        {
            ParseExpected(SyntaxKind.DefaultKeyword);
        }
        Node expression = ParseAssignmentExpressionOrHigher();
        ParseSemicolon();
        contextFlags = saveContextFlags;
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        Node result = FinishNode(factory.NewExportAssignment(modifiers, isExportEquals, null, expression), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private Node ParseNamespaceExportDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        ParseExpected(SyntaxKind.AsKeyword);
        ParseExpected(SyntaxKind.NamespaceKeyword);
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        Node name = ParseIdentifier();
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        ParseSemicolon();
        // NamespaceExportDeclaration cannot have decorators/modifiers; attach them for the grammar checker.
        Node result = FinishNode(factory.NewNamespaceExportDeclaration(modifiers, name), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParseExportDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        NodeFlags saveContextFlags = contextFlags;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        SetContextFlags(NodeFlags.AwaitContext, true);
        Node? exportClause = null;
        Node? moduleSpecifier = null;
        Node? attributes = null;
        bool isTypeOnly = ParseOptional(SyntaxKind.TypeKeyword);
        int namespaceExportPos = NodePos();
        if (ParseOptional(SyntaxKind.AsteriskToken))
        {
            if (ParseOptional(SyntaxKind.AsKeyword))
            {
                exportClause = ParseNamespaceExport(namespaceExportPos);
            }
            ParseExpected(SyntaxKind.FromKeyword);
            moduleSpecifier = ParseModuleSpecifier();
        }
        else
        {
            exportClause = ParseNamedExports();
            // It's common to omit 'from'. Additionally, in editing scenarios the 'from' keyword can be
            // parsed as a named export when the clause is unterminated. If no 'from', see if we have a
            // string literal such that ASI won't take effect.
            if (token == SyntaxKind.FromKeyword || (token == SyntaxKind.StringLiteral && !HasPrecedingLineBreak()))
            {
                ParseExpected(SyntaxKind.FromKeyword);
                moduleSpecifier = ParseModuleSpecifier();
            }
        }
        if (moduleSpecifier != null && (token == SyntaxKind.WithKeyword || token == SyntaxKind.AssertKeyword) && !HasPrecedingLineBreak())
        {
            if (token == SyntaxKind.AssertKeyword)
            {
                ParseErrorAtCurrentToken(Diag.Import_assertions_have_been_replaced_by_import_attributes_Use_with_instead_of_assert);
            }
            attributes = ParseImportAttributes(token, false);
        }
        ParseSemicolon();
        contextFlags = saveContextFlags;
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        Node result = FinishNode(factory.NewExportDeclaration(modifiers, isTypeOnly, exportClause, moduleSpecifier, attributes), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private Node ParseNamespaceExport(int pos)
    {
        (Node exportName, _) = ParseModuleExportName(false);
        return FinishNode(factory.NewNamespaceExport(exportName), pos);
    }

    private Node ParseNamedExports()
    {
        int pos = NodePos();
        // NamedExports: { } | { ExportsList } | { ExportsList, }
        NodeList? exports = ParseBracketedList(ParsingContext.ImportOrExportSpecifiers, ParseExportSpecifier, SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken);
        return FinishNode(factory.NewNamedExports(exports), pos);
    }

    private Node ParseExportSpecifier()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        (bool isTypeOnly, Node? propertyName, Node name) = ParseImportOrExportSpecifier(SyntaxKind.ExportSpecifier);
        Node result = FinishNode(factory.NewExportSpecifier(isTypeOnly, propertyName, name), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    // ── import type / import attributes (parser.go:3031) ──

    private bool NextIsStartOfTypeOfImportType()
    {
        NextToken();
        return token == SyntaxKind.ImportKeyword;
    }

    private Node ParseImportType()
    {
        sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
        int pos = NodePos();
        bool isTypeOf = ParseOptional(SyntaxKind.TypeOfKeyword);
        ParseExpected(SyntaxKind.ImportKeyword);
        ParseExpected(SyntaxKind.OpenParenToken);
        Node typeNode = ParseType();
        Node? attributes = null;
        if (ParseOptional(SyntaxKind.CommaToken))
        {
            int openBracePosition = scanner.TokenStart();
            ParseExpected(SyntaxKind.OpenBraceToken);
            SyntaxKind currentToken = token;
            if (currentToken == SyntaxKind.WithKeyword || currentToken == SyntaxKind.AssertKeyword)
            {
                if (currentToken == SyntaxKind.AssertKeyword)
                {
                    ParseErrorAtCurrentToken(Diag.Import_assertions_have_been_replaced_by_import_attributes_Use_with_instead_of_assert);
                }
                NextToken();
            }
            else
            {
                ParseErrorAtCurrentToken(Diag.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.WithKeyword));
            }
            ParseExpected(SyntaxKind.ColonToken);
            attributes = ParseImportAttributes(currentToken, true);
            ParseOptional(SyntaxKind.CommaToken);
            if (!ParseExpected(SyntaxKind.CloseBraceToken))
            {
                if (diagnostics.Count != 0)
                {
                    Diagnostic lastDiagnostic = diagnostics[^1];
                    if (lastDiagnostic.Code() == Diag.X_0_expected.Code)
                    {
                        Diagnostic related = Diagnostic.NewDiagnostic(null, new TextRange(openBracePosition, openBracePosition),
                            Diag.The_parser_expected_to_find_a_1_to_match_the_0_token_here, "{", "}");
                        lastDiagnostic.AddRelatedInfo(related);
                    }
                }
            }
        }
        ParseExpected(SyntaxKind.CloseParenToken);
        Node? qualifier = null;
        if (ParseOptional(SyntaxKind.DotToken))
        {
            qualifier = ParseEntityNameOfTypeReference();
        }
        NodeList? typeArguments = ParseTypeArgumentsOfTypeReference();
        return FinishNode(factory.NewImportTypeNode(isTypeOf, typeNode, attributes, qualifier, typeArguments), pos);
    }

    private Node ParseImportAttribute()
    {
        int pos = NodePos();
        Node? name = null;
        if (Utilities.TokenIsIdentifierOrKeyword(token))
        {
            name = ParseIdentifierName();
        }
        else if (token == SyntaxKind.StringLiteral)
        {
            name = ParseLiteralExpression(false);
        }
        if (name != null)
        {
            ParseExpected(SyntaxKind.ColonToken);
        }
        else
        {
            ParseErrorAtCurrentToken(Diag.Identifier_or_string_literal_expected);
        }
        Node value = ParseAssignmentExpressionOrHigher();
        return FinishNode(factory.NewImportAttribute(name, value), pos);
    }

    private Node ParseImportAttributes(SyntaxKind openToken, bool skipKeyword)
    {
        int pos = NodePos();
        if (!skipKeyword)
        {
            ParseExpected(openToken);
        }
        NodeList? elements;
        bool multiLine = false;
        int openBracePosition = scanner.TokenStart();
        if (ParseExpected(SyntaxKind.OpenBraceToken))
        {
            multiLine = HasPrecedingLineBreak();
            elements = ParseDelimitedList(ParsingContext.ImportAttributes, ParseImportAttribute);
            if (!ParseExpected(SyntaxKind.CloseBraceToken))
            {
                if (diagnostics.Count != 0)
                {
                    Diagnostic lastDiagnostic = diagnostics[^1];
                    if (lastDiagnostic.Code() == Diag.X_0_expected.Code)
                    {
                        Diagnostic related = Diagnostic.NewDiagnostic(null, new TextRange(openBracePosition, openBracePosition),
                            Diag.The_parser_expected_to_find_a_1_to_match_the_0_token_here, "{", "}");
                        lastDiagnostic.AddRelatedInfo(related);
                    }
                }
            }
        }
        else
        {
            elements = ParseEmptyNodeList();
        }
        return FinishNode(factory.NewImportAttributes(openToken, elements, multiLine), pos);
    }

    // ── type parameters (parser.go:3170) ──

    private Node ParseMappedTypeParameter()
    {
        int pos = NodePos();
        Node name = ParseIdentifierName();
        ParseExpected(SyntaxKind.InKeyword);
        Node typeNode = ParseType();
        return FinishNode(factory.NewTypeParameterDeclaration(null, name, typeNode, null, null), pos);
    }

    private Node ParseTypeMember()
    {
        if (token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken)
        {
            return ParseSignatureMember(SyntaxKind.CallSignature);
        }
        if (token == SyntaxKind.NewKeyword && LookAhead(NextTokenIsOpenParenOrLessThan))
        {
            return ParseSignatureMember(SyntaxKind.ConstructSignature);
        }
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ModifierList? modifiers = ParseModifiers();
        if (ParseContextualModifier(SyntaxKind.GetKeyword))
        {
            return ParseAccessorDeclaration(pos, jsdoc, modifiers, SyntaxKind.GetAccessor, ParseFlags.Type);
        }
        if (ParseContextualModifier(SyntaxKind.SetKeyword))
        {
            return ParseAccessorDeclaration(pos, jsdoc, modifiers, SyntaxKind.SetAccessor, ParseFlags.Type);
        }
        if (IsIndexSignature())
        {
            return ParseIndexSignatureDeclaration(pos, jsdoc, modifiers);
        }
        return ParsePropertyOrMethodSignature(pos, jsdoc, modifiers);
    }

    private NodeList? ParseTypeParameters()
    {
        if (token == SyntaxKind.LessThanToken)
        {
            return ParseBracketedList(ParsingContext.TypeParameters, ParseTypeParameter, SyntaxKind.LessThanToken, SyntaxKind.GreaterThanToken);
        }
        return null;
    }

    private Node ParseTypeParameter()
    {
        int pos = NodePos();
        ModifierList? modifiers = ParseModifiersEx(false, true, false);
        Node name = ParseIdentifier();
        Node? constraint = null;
        Node? expression = null;
        if (ParseOptional(SyntaxKind.ExtendsKeyword))
        {
            // People often write improper constraints. If the constraint is an expression and not
            // an actual type, parse it as an expression (to recover well) but report that a type is needed.
            if (IsStartOfType(false) || !IsStartOfExpression())
            {
                constraint = ParseType();
            }
            else
            {
                // It was an expression. Parse a unary expression (not a full expression) so we don't
                // consume the closing `>` in cases like `<T extends "">`.
                expression = ParseUnaryExpressionOrHigher();
            }
        }
        Node? defaultType = null;
        if (ParseOptional(SyntaxKind.EqualsToken))
        {
            defaultType = ParseType();
        }
        Node result = factory.NewTypeParameterDeclaration(modifiers, name, constraint, expression, defaultType);
        return FinishNode(result, pos);
    }

    private Node ParseTypeParameterOfInferType()
    {
        int pos = NodePos();
        Node name = ParseIdentifier();
        Node? constraint = TryParseConstraintOfInferType();
        return FinishNode(factory.NewTypeParameterDeclaration(null, name, constraint, null, null), pos);
    }

    // ── parameters (parser.go:3265) ──

    private NodeList ParseParameters(ParseFlags flags)
    {
        // FormalParameters: [empty] | FormalParameterList
        if (ParseExpected(SyntaxKind.OpenParenToken))
        {
            NodeList parameters = ParseParametersWorker(flags, true)!;
            ParseExpected(SyntaxKind.CloseParenToken);
            return parameters;
        }
        return CreateMissingList();
    }

    private NodeList? ParseParametersWorker(ParseFlags flags, bool allowAmbiguity)
    {
        bool inAwaitContext = (contextFlags & NodeFlags.AwaitContext) != 0;
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.YieldContext, (flags & ParseFlags.Yield) != 0);
        SetContextFlags(NodeFlags.AwaitContext, (flags & ParseFlags.Await) != 0);
        NodeList? parameters = ParseDelimitedList(ParsingContext.Parameters, () =>
        {
            Node? parameter = ParseParameterEx(inAwaitContext, allowAmbiguity);
            if (parameter != null && (flags & ParseFlags.Type) == 0)
            {
                CheckJSSyntax(parameter);
            }
            return parameter;
        });
        contextFlags = saveContextFlags;
        return parameters;
    }

    private Node ParseParameter() => ParseParameterEx(false, true)!;

    private Node? ParseParameterEx(bool inOuterAwaitContext, bool allowAmbiguity)
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        // Decorators are parsed in the outer [Await] context, the rest of the parameter in the function's.
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.AwaitContext, inOuterAwaitContext);
        ModifierList? modifiers = ParseModifiersEx(true, false, false);
        contextFlags = saveContextFlags;
        if (token == SyntaxKind.ThisKeyword)
        {
            Node thisResult = factory.NewParameterDeclaration(
                modifiers,
                null,
                CreateIdentifier(true),
                null,
                ParseTypeAnnotation(),
                null);
            if (modifiers != null)
            {
                ParseErrorAtRange(modifiers.Nodes[0].Loc, Diag.Neither_decorators_nor_modifiers_may_be_applied_to_this_parameters);
            }
            WithJSDoc(FinishNode(thisResult, pos), jsdoc);
            return thisResult;
        }
        Node? dotDotDotToken = ParseOptionalToken(SyntaxKind.DotDotDotToken);
        if (!allowAmbiguity && !IsParameterNameStart())
        {
            return null;
        }
        Node result = factory.NewParameterDeclaration(
            modifiers,
            dotDotDotToken,
            ParseNameOfParameter(modifiers),
            ParseOptionalToken(SyntaxKind.QuestionToken),
            ParseTypeAnnotation(),
            ParseInitializer());
        WithJSDoc(FinishNode(result, pos), jsdoc);
        return result;
    }

    private bool IsParameterNameStart()
    {
        // Be permissive about await/yield by calling isBindingIdentifier instead of isIdentifier;
        // disallowing them during a speculative parse leads to many more follow-on errors.
        return IsBindingIdentifier() || token == SyntaxKind.OpenBracketToken || token == SyntaxKind.OpenBraceToken;
    }

    private Node ParseNameOfParameter(ModifierList? modifiers)
    {
        Node name = ParseIdentifierOrPatternWithDiagnostic(Diag.Private_identifiers_cannot_be_used_as_parameters);
        if (name.Loc.Len == 0 && modifiers == null && NodeTests.IsModifierKind(token))
        {
            // For cases like `function foo(static)` in strict mode: isParameter('static') is true
            // because isModifier('static') is true, but 'static' is not a legal identifier in strict
            // mode. To avoid an infinite loop we advance the cursor to the next token.
            NextToken();
        }
        return name;
    }

    private void ParseTypeMemberSemicolon()
    {
        // Type members may be separated by commas or (possibly ASI) semicolons.
        if (ParseOptional(SyntaxKind.CommaToken))
        {
            return;
        }
        ParseSemicolon();
    }

    private Node ParseAccessorDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers, SyntaxKind kind, ParseFlags flags)
    {
        Node name = ParsePropertyName();
        NodeList? typeParameters = ParseTypeParameters();
        NodeList parameters = ParseParameters(ParseFlags.None);
        Node? returnType = ParseReturnType(SyntaxKind.ColonToken, false);
        Node? body = ParseFunctionBlockOrSemicolon(flags, null);
        Node result;
        // Track typeParameters (for both) and type (for setters) if parsed — they indicate grammar errors.
        if (kind == SyntaxKind.GetAccessor)
        {
            result = factory.NewGetAccessorDeclaration(modifiers, name, typeParameters, parameters, returnType, null, body);
        }
        else
        {
            result = factory.NewSetAccessorDeclaration(modifiers, name, typeParameters, parameters, returnType, null, body);
        }
        WithJSDoc(FinishNode(result, pos), jsdoc);
        if ((flags & ParseFlags.Type) == 0)
        {
            CheckJSSyntax(result);
        }
        return result;
    }

    // ── function bodies (parser.go:3487) ──

    private Node? ParseFunctionBlockOrSemicolon(ParseFlags flags, Message? diagnosticMessage)
    {
        if (token != SyntaxKind.OpenBraceToken)
        {
            if ((flags & ParseFlags.Type) != 0)
            {
                ParseTypeMemberSemicolon();
                return null;
            }
            if (CanParseSemicolon())
            {
                ParseSemicolon();
                return null;
            }
        }
        return ParseFunctionBlock(flags, diagnosticMessage);
    }

    private Node ParseFunctionBlock(ParseFlags flags, Message? diagnosticMessage)
    {
        NodeFlags saveContextFlags = contextFlags;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        SetContextFlags(NodeFlags.YieldContext, (flags & ParseFlags.Yield) != 0);
        SetContextFlags(NodeFlags.AwaitContext, (flags & ParseFlags.Await) != 0);
        // We may be in a [Decorator] context when parsing a function/arrow; the body is not.
        SetContextFlags(NodeFlags.DecoratorContext, false);
        Node block = ParseBlock((flags & ParseFlags.IgnoreMissingOpenBrace) != 0, diagnosticMessage);
        contextFlags = saveContextFlags;
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return block;
    }

    // ── index signatures (parser.go:3515) ──

    private bool IsIndexSignature() =>
        token == SyntaxKind.OpenBracketToken && LookAhead(NextIsUnambiguouslyIndexSignature);

    private bool NextIsUnambiguouslyIndexSignature()
    {
        // The only allowed sequence is `[id:`; the rest are error-recovery cases.
        NextToken();
        if (token == SyntaxKind.DotDotDotToken || token == SyntaxKind.CloseBracketToken)
        {
            return true;
        }
        if (NodeTests.IsModifierKind(token))
        {
            NextToken();
            if (IsIdentifier())
            {
                return true;
            }
        }
        else if (!IsIdentifier())
        {
            return false;
        }
        else
        {
            // Skip the identifier.
            NextToken();
        }
        // A colon signifies a well-formed indexer; a comma is a badly-formed one (comma expressions
        // are not allowed in computed properties).
        if (token == SyntaxKind.ColonToken || token == SyntaxKind.CommaToken)
        {
            return true;
        }
        // A question mark could be an optional-property indexer or a conditional computed property.
        if (token != SyntaxKind.QuestionToken)
        {
            return false;
        }
        // If any of these follow the '?', it cannot be a conditional, so treat it as an indexer.
        NextToken();
        return token == SyntaxKind.ColonToken || token == SyntaxKind.CommaToken || token == SyntaxKind.CloseBracketToken;
    }

    private Node ParseIndexSignatureDeclaration(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        NodeList? parameters = ParseBracketedList(ParsingContext.Parameters, ParseParameter, SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken);
        Node? typeNode = ParseTypeAnnotation();
        ParseTypeMemberSemicolon();
        Node result = FinishNode(factory.NewIndexSignatureDeclaration(modifiers, parameters, typeNode), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private Node ParsePropertyOrMethodSignature(int pos, JSDocScannerInfo jsdoc, ModifierList? modifiers)
    {
        Node name = ParsePropertyName();
        Node? questionToken = ParseOptionalToken(SyntaxKind.QuestionToken);
        Node result;
        if (token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken)
        {
            // Method signatures don't exist in expression contexts; neither [Yield] nor [Await].
            NodeList? typeParameters = ParseTypeParameters();
            NodeList parameters = ParseParameters(ParseFlags.Type);
            Node? returnType = ParseReturnType(SyntaxKind.ColonToken, true);
            result = factory.NewMethodSignatureDeclaration(modifiers, name, questionToken, typeParameters, parameters, returnType);
        }
        else
        {
            Node? typeNode = ParseTypeAnnotation();
            // Type-literal properties can't have initializers, but we parse one to report the error.
            Node? initializer = null;
            if (token == SyntaxKind.EqualsToken)
            {
                initializer = ParseInitializer();
            }
            result = factory.NewPropertySignatureDeclaration(modifiers, name, questionToken, typeNode, initializer);
        }
        ParseTypeMemberSemicolon();
        WithJSDoc(FinishNode(result, pos), jsdoc);
        return result;
    }

    private NodeList ParseObjectTypeMembers()
    {
        if (ParseExpected(SyntaxKind.OpenBraceToken))
        {
            NodeList members = ParseList(ParsingContext.TypeMembers, ParseTypeMember);
            ParseExpected(SyntaxKind.CloseBraceToken);
            return members;
        }
        return CreateMissingList();
    }

    // ── function / constructor types (parser.go:3775) ──

    private bool IsStartOfFunctionTypeOrConstructorType()
    {
        return token == SyntaxKind.LessThanToken ||
            (token == SyntaxKind.OpenParenToken && LookAhead(NextIsUnambiguouslyStartOfFunctionType)) ||
            token == SyntaxKind.NewKeyword ||
            (token == SyntaxKind.AbstractKeyword && LookAhead(NextTokenIsNewKeyword));
    }

    private Node ParseFunctionOrConstructorType()
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ModifierList? modifiers = ParseModifiersForConstructorType();
        bool isConstructorType = ParseOptional(SyntaxKind.NewKeyword);
        Debug.Assert(modifiers == null || isConstructorType, "Per isStartOfFunctionOrConstructorType, a function type cannot have modifiers.");
        NodeList? typeParameters = ParseTypeParameters();
        NodeList parameters = ParseParameters(ParseFlags.Type);
        Node? returnType = ParseReturnType(SyntaxKind.EqualsGreaterThanToken, false);
        Node result;
        if (isConstructorType)
        {
            result = factory.NewConstructorTypeNode(modifiers, typeParameters, parameters, returnType);
        }
        else
        {
            result = factory.NewFunctionTypeNode(typeParameters, parameters, returnType);
        }
        FinishNode(result, pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    private ModifierList? ParseModifiersForConstructorType()
    {
        if (token == SyntaxKind.AbstractKeyword)
        {
            int pos = NodePos();
            Node modifier = factory.NewModifier(token);
            NextToken();
            FinishNode(modifier, pos);
            return NewModifierList(modifier.Loc, new Node[] { modifier });
        }
        return null;
    }

    // NOTE: nextIsUnambiguouslyStartOfFunctionType is owned by the Types cluster
    // (Parser.Types.cs); isStartOfFunctionTypeOrConstructorType above only calls it.

    private bool SkipParameterStart()
    {
        if (NodeTests.IsModifierKind(token))
        {
            // Skip modifiers.
            ParseModifiers();
        }
        ParseOptional(SyntaxKind.DotDotDotToken);
        if (IsIdentifier() || token == SyntaxKind.ThisKeyword)
        {
            NextToken();
            return true;
        }
        if (token == SyntaxKind.OpenBracketToken || token == SyntaxKind.OpenBraceToken)
        {
            // Return true if we can parse an array or object binding pattern with no errors.
            int previousErrorCount = diagnostics.Count;
            ParseIdentifierOrPattern();
            return previousErrorCount == diagnostics.Count;
        }
        return false;
    }

    // ── modifiers / decorators (parser.go:3861) ──

    private ModifierList? ParseModifiers() => ParseModifiersEx(false, false, false);

    private ModifierList? ParseModifiersEx(bool allowDecorators, bool permitConstAsModifier, bool stopOnStartOfClassStaticBlock)
    {
        bool hasLeadingModifier = false;
        bool hasTrailingDecorator = false;
        bool hasTrailingModifier = false;
        bool hasStaticModifier = false;
        // Decorators should be contiguous but can appear in two places:
        // [...leadingDecorators, ...leadingModifiers, ...trailingDecorators, ...trailingModifiers].
        // Errors for illegal placements are reported by the checker.
        int pos = NodePos();
        var list = new List<Node>(16);
        while (true)
        {
            if (allowDecorators && token == SyntaxKind.AtToken && !hasTrailingModifier)
            {
                Node decorator = ParseDecorator();
                list.Add(decorator);
                if (hasLeadingModifier)
                {
                    hasTrailingDecorator = true;
                }
            }
            else
            {
                Node? modifier = TryParseModifier(hasStaticModifier, permitConstAsModifier, stopOnStartOfClassStaticBlock);
                if (modifier == null)
                {
                    break;
                }
                if (modifier.Kind == SyntaxKind.StaticKeyword)
                {
                    hasStaticModifier = true;
                }
                list.Add(modifier);
                if (hasTrailingDecorator)
                {
                    hasTrailingModifier = true;
                }
                else
                {
                    hasLeadingModifier = true;
                }
            }
        }
        if (list.Count != 0)
        {
            return NewModifierList(new TextRange(pos, NodePos()), list.ToArray());
        }
        return null;
    }

    private Node ParseDecorator()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.AtToken);
        Node expression = DoInContext(NodeFlags.DecoratorContext, true, ParseDecoratorExpression);
        return FinishNode(factory.NewDecorator(expression), pos);
    }

    private Node ParseDecoratorExpression()
    {
        if (InAwaitContext() && token == SyntaxKind.AwaitKeyword)
        {
            // `@await` is disallowed in an [Await] context but can derail parsing. Parse the missing
            // identifier and move on.
            int pos = NodePos();
            Node awaitExpression = ParseIdentifierWithDiagnostic(Diag.Expression_expected, null);
            NextToken();
            Node memberExpression = ParseMemberExpressionRest(pos, awaitExpression, true);
            return ParseCallExpressionRest(pos, memberExpression);
        }
        return ParseLeftHandSideExpressionOrHigher();
    }

    private Node? TryParseModifier(bool hasSeenStaticModifier, bool permitConstAsModifier, bool stopOnStartOfClassStaticBlock)
    {
        int pos = NodePos();
        SyntaxKind kind = token;
        if (token == SyntaxKind.ConstKeyword && permitConstAsModifier)
        {
            // Ensure subsequent modifiers are on the same line so a standalone 'const' isn't an error.
            if (!LookAhead(NextTokenIsOnSameLineAndCanFollowModifier))
            {
                return null;
            }
            NextToken();
        }
        else if (stopOnStartOfClassStaticBlock && token == SyntaxKind.StaticKeyword && LookAhead(NextTokenIsOpenBrace))
        {
            return null;
        }
        else if (hasSeenStaticModifier && token == SyntaxKind.StaticKeyword)
        {
            return null;
        }
        else
        {
            if (!ParseAnyContextualModifier())
            {
                return null;
            }
        }
        return FinishNode(factory.NewModifier(kind), pos);
    }

    private bool ParseContextualModifier(SyntaxKind t)
    {
        ParserState state = Mark();
        if (token == t && NextTokenCanFollowModifier())
        {
            return true;
        }
        Rewind(state);
        return false;
    }

    private bool ParseAnyContextualModifier()
    {
        ParserState state = Mark();
        if (NodeTests.IsModifierKind(token) && NextTokenCanFollowModifier())
        {
            return true;
        }
        Rewind(state);
        return false;
    }

    private bool NextTokenCanFollowModifier()
    {
        switch (token)
        {
            case SyntaxKind.ConstKeyword:
                // 'const' is only a modifier if followed by 'enum'.
                return NextToken() == SyntaxKind.EnumKeyword;
            case SyntaxKind.ExportKeyword:
                NextToken();
                if (token == SyntaxKind.DefaultKeyword)
                {
                    return LookAhead(NextTokenCanFollowDefaultKeyword);
                }
                if (token == SyntaxKind.TypeKeyword)
                {
                    return LookAhead(NextTokenCanFollowExportModifier);
                }
                return CanFollowExportModifier();
            case SyntaxKind.DefaultKeyword:
                return NextTokenCanFollowDefaultKeyword();
            case SyntaxKind.StaticKeyword:
                NextToken();
                return CanFollowModifier();
            case SyntaxKind.GetKeyword:
            case SyntaxKind.SetKeyword:
                NextToken();
                return CanFollowGetOrSetKeyword();
            default:
                return NextTokenIsOnSameLineAndCanFollowModifier();
        }
    }

    private bool NextTokenCanFollowExportModifier()
    {
        NextToken();
        return CanFollowExportModifier();
    }

    private bool CanFollowExportModifier()
    {
        return token == SyntaxKind.AtToken ||
            (token != SyntaxKind.AsteriskToken && token != SyntaxKind.AsKeyword && token != SyntaxKind.OpenBraceToken && CanFollowModifier());
    }

    private bool CanFollowModifier()
    {
        return token == SyntaxKind.OpenBracketToken || token == SyntaxKind.OpenBraceToken || token == SyntaxKind.AsteriskToken ||
            token == SyntaxKind.DotDotDotToken || IsLiteralPropertyName();
    }

    private bool NextTokenIsClassKeywordOnSameLine()
    {
        return NextToken() == SyntaxKind.ClassKeyword && !HasPrecedingLineBreak();
    }

    private bool NextTokenIsOnSameLineAndCanFollowModifier()
    {
        NextToken();
        if (HasPrecedingLineBreak())
        {
            return false;
        }
        return CanFollowModifier();
    }

    // ── arrow functions (parser.go:4201) ──

    private Tristate IsParenthesizedArrowFunctionExpression()
    {
        if (token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken || token == SyntaxKind.AsyncKeyword)
        {
            ParserState state = Mark();
            Tristate result = NextIsParenthesizedArrowFunctionExpression();
            Rewind(state);
            return result;
        }
        if (token == SyntaxKind.EqualsGreaterThanToken)
        {
            // ERROR RECOVERY: a standalone => is likely intended as an arrow function.
            return Tristate.True;
        }
        return Tristate.False;
    }

    private Tristate NextIsParenthesizedArrowFunctionExpression()
    {
        if (token == SyntaxKind.AsyncKeyword)
        {
            NextToken();
            if (HasPrecedingLineBreak())
            {
                return Tristate.False;
            }
            if (token != SyntaxKind.OpenParenToken && token != SyntaxKind.LessThanToken)
            {
                return Tristate.False;
            }
        }
        SyntaxKind first = token;
        SyntaxKind second = NextToken();
        if (first == SyntaxKind.OpenParenToken)
        {
            if (second == SyntaxKind.CloseParenToken)
            {
                // "() =>", "(): ", and "() {" — the last isn't really an arrow but is likely intended.
                SyntaxKind third = NextToken();
                switch (third)
                {
                    case SyntaxKind.EqualsGreaterThanToken:
                    case SyntaxKind.ColonToken:
                    case SyntaxKind.OpenBraceToken:
                        return Tristate.True;
                }
                return Tristate.False;
            }
            // "([" or "({" could be the start of a binding pattern.
            if (second == SyntaxKind.OpenBracketToken || second == SyntaxKind.OpenBraceToken)
            {
                return Tristate.Unknown;
            }
            // "(..." is an arrow function with a rest parameter.
            if (second == SyntaxKind.DotDotDotToken)
            {
                return Tristate.True;
            }
            // "(xxx yyy" where xxx is a modifier and yyy an identifier: not legal, but treat as a
            // lambda for a good error message.
            if (NodeTests.IsModifierKind(second) && second != SyntaxKind.AsyncKeyword && LookAhead(NextTokenIsIdentifier))
            {
                if (NextToken() == SyntaxKind.AsKeyword)
                {
                    // https://github.com/microsoft/TypeScript/issues/44466
                    return Tristate.False;
                }
                return Tristate.True;
            }
            // "(" followed by a non-identifier (and not 'this') is not a lambda.
            if (!IsIdentifier() && second != SyntaxKind.ThisKeyword)
            {
                return Tristate.False;
            }
            switch (NextToken())
            {
                case SyntaxKind.ColonToken:
                    // "(a:" is a type-annotated parameter in an arrow function.
                    return Tristate.True;
                case SyntaxKind.QuestionToken:
                    NextToken();
                    // "(a?:", "(a?,", "(a?=" or "(a?)" is definitely a lambda.
                    if (token == SyntaxKind.ColonToken || token == SyntaxKind.CommaToken || token == SyntaxKind.EqualsToken || token == SyntaxKind.CloseParenToken)
                    {
                        return Tristate.True;
                    }
                    return Tristate.False;
                case SyntaxKind.CommaToken:
                case SyntaxKind.EqualsToken:
                case SyntaxKind.CloseParenToken:
                    // "(a,", "(a=" or "(a)" *could* be an arrow function.
                    return Tristate.Unknown;
            }
            return Tristate.False;
        }
        else
        {
            Debug.Assert(first == SyntaxKind.LessThanToken);
            // "<" not followed by an identifier is definitely not an arrow function.
            if (!IsIdentifier() && token != SyntaxKind.ConstKeyword)
            {
                return Tristate.False;
            }
            // JSX overrides.
            if (languageVariant == LanguageVariant.Jsx)
            {
                bool isArrowFunctionInJsx = LookAhead(() =>
                {
                    ParseOptional(SyntaxKind.ConstKeyword);
                    SyntaxKind third = NextToken();
                    if (third == SyntaxKind.ExtendsKeyword)
                    {
                        SyntaxKind fourth = NextToken();
                        switch (fourth)
                        {
                            case SyntaxKind.EqualsToken:
                            case SyntaxKind.GreaterThanToken:
                            case SyntaxKind.SlashToken:
                                return false;
                        }
                        return true;
                    }
                    else if (third == SyntaxKind.CommaToken || third == SyntaxKind.EqualsToken)
                    {
                        return true;
                    }
                    return false;
                });
                if (isArrowFunctionInJsx)
                {
                    return Tristate.True;
                }
                return Tristate.False;
            }
            // This *could* be a parenthesized arrow function.
            return Tristate.Unknown;
        }
    }

    private Node? TryParseParenthesizedArrowFunctionExpression(bool allowReturnTypeInArrowFunction)
    {
        Tristate tristate = IsParenthesizedArrowFunctionExpression();
        if (tristate == Tristate.False)
        {
            return null;
        }
        // If it's definitely an arrow function, parse one without requiring a following => or {.
        // Otherwise it *might* be one; parse without ambiguity and return null if it could be an expression.
        if (tristate == Tristate.True)
        {
            return ParseParenthesizedArrowFunctionExpression(true, true);
        }
        ParserState state = Mark();
        Node? result = ParsePossibleParenthesizedArrowFunctionExpression(allowReturnTypeInArrowFunction);
        if (result == null)
        {
            Rewind(state);
        }
        return result;
    }

    private Node? ParseParenthesizedArrowFunctionExpression(bool allowAmbiguity, bool allowReturnTypeInArrowFunction)
    {
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ModifierList? modifiers = ParseModifiersForArrowFunction();
        bool isAsync = ModifierListHasAsync(modifiers);
        ParseFlags signatureFlags = isAsync ? ParseFlags.Await : ParseFlags.None;
        // Arrow functions are never generators. When speculatively parsing a signature we must have a
        // complete parameter list, otherwise `a => (b => c)` could look like a missing close paren.
        NodeList? typeParameters = ParseTypeParameters();
        NodeList? parameters;
        if (!ParseExpected(SyntaxKind.OpenParenToken))
        {
            if (!allowAmbiguity)
            {
                return null;
            }
            parameters = CreateMissingList();
        }
        else
        {
            if (!allowAmbiguity)
            {
                NodeList? maybeParameters = ParseParametersWorker(signatureFlags, allowAmbiguity);
                if (maybeParameters == null)
                {
                    return null;
                }
                parameters = maybeParameters;
            }
            else
            {
                parameters = ParseParametersWorker(signatureFlags, allowAmbiguity);
            }
            if (!ParseExpected(SyntaxKind.CloseParenToken) && !allowAmbiguity)
            {
                return null;
            }
        }
        bool hasReturnColon = token == SyntaxKind.ColonToken;
        Node? returnType = ParseReturnType(SyntaxKind.ColonToken, false);
        if (returnType != null && !allowAmbiguity && TypeHasArrowFunctionBlockingParseError(returnType))
        {
            return null;
        }
        // Parenthesized arrow signatures often look like other valid expressions. A bit of lookahead
        // ensures it can only be a signature. (See parser.go for the enumerated cases.)
        Node? unwrappedType = returnType;
        while (unwrappedType != null && unwrappedType.Kind == SyntaxKind.ParenthesizedType)
        {
            unwrappedType = unwrappedType.Type(); // Skip parens if need be
        }
        if (!allowAmbiguity && token != SyntaxKind.EqualsGreaterThanToken && token != SyntaxKind.OpenBraceToken)
        {
            // Returning null causes our caller to rewind.
            return null;
        }
        // If we have an arrow, parse the body. Even if not, try if we have an opening brace (error state).
        SyntaxKind lastToken = token;
        Node equalsGreaterThanToken = ParseExpectedToken(SyntaxKind.EqualsGreaterThanToken);
        Node body;
        if (lastToken == SyntaxKind.EqualsGreaterThanToken || lastToken == SyntaxKind.OpenBraceToken)
        {
            body = ParseArrowFunctionExpressionBody(isAsync, allowReturnTypeInArrowFunction);
        }
        else
        {
            body = ParseIdentifier();
        }
        // Guard against consuming a return type across a conditional-expression colon when we aren't
        // certain the preceding text was a parameter list. (See parser.go for the worked example.)
        if (!allowReturnTypeInArrowFunction && hasReturnColon)
        {
            if (token != SyntaxKind.ColonToken)
            {
                return null;
            }
        }
        Node result = FinishNode(factory.NewArrowFunction(modifiers, typeParameters, parameters, returnType, null, equalsGreaterThanToken, body), pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private ModifierList? ParseModifiersForArrowFunction()
    {
        if (token == SyntaxKind.AsyncKeyword)
        {
            int pos = NodePos();
            NextToken();
            Node modifier = FinishNode(factory.NewModifier(SyntaxKind.AsyncKeyword), pos);
            return NewModifierList(modifier.Loc, new Node[] { modifier });
        }
        return null;
    }

    // If true, we should abort parsing an arrow function.
    private static bool TypeHasArrowFunctionBlockingParseError(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TypeReference:
                return AstUtilities.NodeIsMissing(node.AsTypeReferenceNode().TypeName);
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
                return IsMissingNodeList(node.FunctionLikeData()!.Parameters) || TypeHasArrowFunctionBlockingParseError(node.Type()!);
            case SyntaxKind.ParenthesizedType:
                return TypeHasArrowFunctionBlockingParseError(node.Type()!);
        }
        return false;
    }

    private Node ParseArrowFunctionExpressionBody(bool isAsync, bool allowReturnTypeInArrowFunction)
    {
        if (token == SyntaxKind.OpenBraceToken)
        {
            return ParseFunctionBlock(isAsync ? ParseFlags.Await : ParseFlags.None, null);
        }
        if (token != SyntaxKind.SemicolonToken && token != SyntaxKind.FunctionKeyword && token != SyntaxKind.ClassKeyword &&
            IsStartOfStatement() && !IsStartOfExpressionStatement())
        {
            // The user may have meant to supply a block (missing open brace). Recover better. Even when
            // 'IgnoreMissingOpenBrace' is passed, parseBody will still error.
            return ParseFunctionBlock(ParseFlags.IgnoreMissingOpenBrace | (isAsync ? ParseFlags.Await : ParseFlags.None), null);
        }
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.AwaitContext, isAsync);
        SetContextFlags(NodeFlags.YieldContext, false);
        Node node = ParseAssignmentExpressionOrHigherWorker(allowReturnTypeInArrowFunction);
        contextFlags = saveContextFlags;
        return node;
    }

    private Node? ParsePossibleParenthesizedArrowFunctionExpression(bool allowReturnTypeInArrowFunction)
    {
        int tokenPos = scanner.TokenStart();
        if (notParenthesizedArrow.Has(tokenPos))
        {
            return null;
        }
        Node? result = ParseParenthesizedArrowFunctionExpression(false, allowReturnTypeInArrowFunction);
        if (result == null)
        {
            notParenthesizedArrow.Add(tokenPos);
        }
        return result;
    }

    private Node? TryParseAsyncSimpleArrowFunctionExpression(bool allowReturnTypeInArrowFunction)
    {
        // Avoid an unnecessary lookAhead by checking the async keyword first.
        if (token == SyntaxKind.AsyncKeyword && LookAhead(NextIsUnParenthesizedAsyncArrowFunction))
        {
            int pos = NodePos();
            JSDocScannerInfo jsdoc = JsdocScannerInfo();
            ModifierList? asyncModifier = ParseModifiersForArrowFunction();
            Node expr = ParseBinaryExpressionOrHigher(OperatorPrecedence.Lowest);
            return ParseSimpleArrowFunctionExpression(pos, expr, allowReturnTypeInArrowFunction, jsdoc, asyncModifier);
        }
        return null;
    }

    private Node ParseSimpleArrowFunctionExpression(int pos, Node? identifier, bool allowReturnTypeInArrowFunction, JSDocScannerInfo jsdoc, ModifierList? asyncModifier)
    {
        Debug.Assert(token == SyntaxKind.EqualsGreaterThanToken, "parseSimpleArrowFunctionExpression should only have been called if we had a =>");
        Node parameter = FinishNode(factory.NewParameterDeclaration(null, null, identifier, null, null, null), identifier!.Loc.Pos);
        NodeList parameters = NewNodeList(parameter.Loc, new Node[] { parameter });
        Node equalsGreaterThanToken = ParseExpectedToken(SyntaxKind.EqualsGreaterThanToken);
        Node body = ParseArrowFunctionExpressionBody(asyncModifier != null, allowReturnTypeInArrowFunction);
        Node result = FinishNode(factory.NewArrowFunction(asyncModifier, null, parameters, null, null, equalsGreaterThanToken, body), pos);
        WithJSDoc(result, jsdoc);
        return result;
    }

    // ── function expression (parser.go:5692) ──

    private Node ParseFunctionExpression()
    {
        // FunctionExpression: function BindingIdentifier?(FormalParameters){ FunctionBody }
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.DecoratorContext, false);
        int pos = NodePos();
        JSDocScannerInfo jsdoc = JsdocScannerInfo();
        ModifierList? modifiers = ParseModifiers();
        ParseExpected(SyntaxKind.FunctionKeyword);
        Node? asteriskToken = ParseOptionalToken(SyntaxKind.AsteriskToken);
        bool isGenerator = asteriskToken != null;
        bool isAsync = ModifierListHasAsync(modifiers);
        ParseFlags signatureFlags = (isGenerator ? ParseFlags.Yield : ParseFlags.None) | (isAsync ? ParseFlags.Await : ParseFlags.None);
        Node? name;
        if (isGenerator && isAsync)
        {
            name = DoInContext(NodeFlags.YieldContext | NodeFlags.AwaitContext, true, ParseOptionalBindingIdentifier);
        }
        else if (isGenerator)
        {
            name = DoInContext(NodeFlags.YieldContext, true, ParseOptionalBindingIdentifier);
        }
        else if (isAsync)
        {
            name = DoInContext(NodeFlags.AwaitContext, true, ParseOptionalBindingIdentifier);
        }
        else
        {
            name = ParseOptionalBindingIdentifier();
        }
        NodeList? typeParameters = ParseTypeParameters();
        NodeList parameters = ParseParameters(signatureFlags);
        Node? returnType = ParseReturnType(SyntaxKind.ColonToken, false);
        Node? body = ParseFunctionBlock(signatureFlags, null);
        contextFlags = saveContextFlags;
        Node result = factory.NewFunctionExpression(modifiers, asteriskToken, name, typeParameters, parameters, returnType, null, body);
        FinishNode(result, pos);
        WithJSDoc(result, jsdoc);
        CheckJSSyntax(result);
        return result;
    }

    private Node? ParseOptionalBindingIdentifier()
    {
        if (IsBindingIdentifier())
        {
            return ParseBindingIdentifier();
        }
        return null;
    }

    private void UnparseExpressionWithTypeArguments(Node? expression, NodeList? typeArguments, Node? result)
    {
        // Force overwrite the .Parent of the expression/type arguments to erase the fact that they may
        // have originally been parsed as an ExpressionWithTypeArguments and parented to such.
        if (expression != null)
        {
            expression.Parent = result;
        }
        if (typeArguments != null)
        {
            foreach (Node a in typeArguments.Nodes)
            {
                a.Parent = result;
            }
        }
    }

    // ── binding identifiers (parser.go:5828) ──

    private Node ParseBindingIdentifier() => ParseBindingIdentifierWithDiagnostic(null);

    private Node ParseBindingIdentifierWithDiagnostic(Message? privateIdentifierDiagnosticMessage)
    {
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;
        Node id = CreateIdentifierWithDiagnostic(IsBindingIdentifier(), null, privateIdentifierDiagnosticMessage);
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;
        return id;
    }

    // ── type arguments in expression position (parser.go:5247) ──

    private NodeList? TryParseTypeArgumentsInExpression()
    {
        // TypeArguments must not be parsed in JS files (ambiguity with binary operators). Check cheap
        // preconditions before saving state: unless the token is `<` (or `<<`), there's nothing to do.
        if ((contextFlags & NodeFlags.JavaScriptFile) != 0 || (token != SyntaxKind.LessThanToken && token != SyntaxKind.LessThanLessThanToken))
        {
            return null;
        }
        ParserState state = Mark();
        if (ReScanLessThanToken() == SyntaxKind.LessThanToken)
        {
            NextToken();
            NodeList? typeArguments = ParseDelimitedList(ParsingContext.TypeArguments, ParseType);
            // Without a closing `>` it's definitely not a type argument list.
            if (ReScanGreaterThanToken() == SyntaxKind.GreaterThanToken)
            {
                NextToken();
                // If followed by `(` or a template literal, favor the type-argument interpretation.
                if (CanFollowTypeArgumentsInExpression())
                {
                    return typeArguments;
                }
            }
        }
        Rewind(state);
        return null;
    }

    private bool CanFollowTypeArgumentsInExpression()
    {
        switch (token)
        {
            // foo<x>(   foo<T> `...`   foo<T> `...${100}...`
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
                return true;
            // `<` never makes sense; `>` is ambiguous with a re-scanned `>>`; `+`/`-` are unary here.
            case SyntaxKind.LessThanToken:
            case SyntaxKind.GreaterThanToken:
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
                return false;
        }
        // Favor the type-argument interpretation before a line break, a binary operator, or something
        // that can't start an expression.
        return HasPrecedingLineBreak() || IsBinaryOperator() || !IsStartOfExpression();
    }

    private NodeList? ParseTypeArgumentsOfTypeReference()
    {
        if (!HasPrecedingLineBreak() && ReScanLessThanToken() == SyntaxKind.LessThanToken)
        {
            return ParseTypeArguments();
        }
        return null;
    }

    private NodeList? ParseTypeArguments()
    {
        if (token == SyntaxKind.LessThanToken)
        {
            return ParseBracketedList(ParsingContext.TypeArguments, ParseType, SyntaxKind.LessThanToken, SyntaxKind.GreaterThanToken);
        }
        return null;
    }

    // ── scan-start predicates (parser.go:5955) ──

    private bool ScanTypeMemberStart()
    {
        // Return true if we have the start of a signature member.
        if (token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken || token == SyntaxKind.GetKeyword || token == SyntaxKind.SetKeyword)
        {
            return true;
        }
        bool idToken = false;
        // Eat up all modifiers, but keep the last in case it is actually an identifier.
        while (NodeTests.IsModifierKind(token))
        {
            idToken = true;
            NextToken();
        }
        // Index signatures and computed property names are type members.
        if (token == SyntaxKind.OpenBracketToken)
        {
            return true;
        }
        // Try to get the first property-like token following all modifiers.
        if (IsLiteralPropertyName())
        {
            idToken = true;
            NextToken();
        }
        // If we got a potential identifier, check that it starts a member declaration.
        if (idToken)
        {
            return token == SyntaxKind.OpenParenToken || token == SyntaxKind.LessThanToken || token == SyntaxKind.QuestionToken ||
                token == SyntaxKind.ColonToken || token == SyntaxKind.CommaToken || CanParseSemicolon();
        }
        return false;
    }

    private bool ScanClassMemberStart()
    {
        SyntaxKind idToken = SyntaxKind.Unknown;
        if (token == SyntaxKind.AtToken)
        {
            return true;
        }
        // Eat up all modifiers, but keep the last in case it is actually an identifier.
        while (NodeTests.IsModifierKind(token))
        {
            idToken = token;
            // A class member modifier (protected/private/public/static) means we're certainly starting
            // a class member; this allows better error recovery.
            if (AstUtilities.IsClassMemberModifier(idToken))
            {
                return true;
            }
            NextToken();
        }
        if (token == SyntaxKind.AsteriskToken)
        {
            return true;
        }
        // Try to get the first property-like token following all modifiers ('get'/'set' too).
        if (IsLiteralPropertyName())
        {
            idToken = token;
            NextToken();
        }
        // Index signatures and computed properties are class members; we can parse.
        if (token == SyntaxKind.OpenBracketToken)
        {
            return true;
        }
        if (idToken != SyntaxKind.Unknown)
        {
            // A non-keyword identifier or an accessor is safe to parse.
            if (!NodeTests.IsKeywordKind(idToken) || idToken == SyntaxKind.SetKeyword || idToken == SyntaxKind.GetKeyword)
            {
                return true;
            }
            // If it *is* a keyword (but not an accessor) check farther along.
            switch (token)
            {
                case SyntaxKind.OpenParenToken:    // Method declaration
                case SyntaxKind.LessThanToken:     // Generic method declaration
                case SyntaxKind.ExclamationToken:  // Non-null assertion on property name
                case SyntaxKind.ColonToken:        // Type annotation for declaration
                case SyntaxKind.EqualsToken:       // Initializer for declaration
                case SyntaxKind.QuestionToken:     // Not valid, but permitted to be caught later
                    return true;
            }
            // Semicolons / closing braces / EOF / line-breaks (ASI).
            return CanParseSemicolon();
        }
        return false;
    }

    // ── start-of-declaration (parser.go:6096) ──

    private bool IsStartOfDeclaration() => LookAhead(ScanStartOfDeclaration);

    private bool ScanStartOfDeclaration()
    {
        while (true)
        {
            switch (token)
            {
                case SyntaxKind.VarKeyword:
                case SyntaxKind.LetKeyword:
                case SyntaxKind.ConstKeyword:
                case SyntaxKind.FunctionKeyword:
                case SyntaxKind.ClassKeyword:
                case SyntaxKind.EnumKeyword:
                    return true;
                case SyntaxKind.UsingKeyword:
                    return IsUsingDeclaration();
                case SyntaxKind.AwaitKeyword:
                    return IsAwaitUsingDeclaration();
                case SyntaxKind.InterfaceKeyword:
                case SyntaxKind.TypeKeyword:
                case SyntaxKind.DeferKeyword:
                    return NextTokenIsIdentifierOnSameLine();
                case SyntaxKind.ModuleKeyword:
                case SyntaxKind.NamespaceKeyword:
                    return NextTokenIsIdentifierOrStringLiteralOnSameLine();
                case SyntaxKind.AbstractKeyword:
                case SyntaxKind.AccessorKeyword:
                case SyntaxKind.AsyncKeyword:
                case SyntaxKind.DeclareKeyword:
                case SyntaxKind.PrivateKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.ReadonlyKeyword:
                    SyntaxKind previousToken = token;
                    NextToken();
                    // ASI takes effect for this modifier.
                    if (HasPrecedingLineBreak())
                    {
                        return false;
                    }
                    if (previousToken == SyntaxKind.DeclareKeyword && token == SyntaxKind.TypeKeyword)
                    {
                        // 'declare type' commits to a type alias; parseTypeAliasDeclaration reports the
                        // Line_break_not_permitted_here error if needed.
                        return true;
                    }
                    continue;
                case SyntaxKind.GlobalKeyword:
                    NextToken();
                    return token == SyntaxKind.OpenBraceToken || token == SyntaxKind.Identifier || token == SyntaxKind.ExportKeyword;
                case SyntaxKind.ImportKeyword:
                    NextToken();
                    return token == SyntaxKind.DeferKeyword || token == SyntaxKind.StringLiteral || token == SyntaxKind.AsteriskToken ||
                        token == SyntaxKind.OpenBraceToken || Utilities.TokenIsIdentifierOrKeyword(token);
                case SyntaxKind.ExportKeyword:
                    NextToken();
                    if (token == SyntaxKind.EqualsToken || token == SyntaxKind.AsteriskToken || token == SyntaxKind.OpenBraceToken ||
                        token == SyntaxKind.DefaultKeyword || token == SyntaxKind.AsKeyword || token == SyntaxKind.AtToken)
                    {
                        return true;
                    }
                    if (token == SyntaxKind.TypeKeyword)
                    {
                        NextToken();
                        return token == SyntaxKind.AsteriskToken || token == SyntaxKind.OpenBraceToken || (IsIdentifier() && !HasPrecedingLineBreak());
                    }
                    continue;
                case SyntaxKind.StaticKeyword:
                    NextToken();
                    continue;
            }
            return false;
        }
    }

    // ── parameter/binding predicates (parser.go:6243) ──

    private bool IsStartOfParameter(bool isJSDocParameter)
    {
        return token == SyntaxKind.DotDotDotToken ||
            IsBindingIdentifierOrPrivateIdentifierOrPattern() ||
            NodeTests.IsModifierKind(token) ||
            token == SyntaxKind.AtToken ||
            IsStartOfType(!isJSDocParameter);
    }

    private bool IsBindingIdentifierOrPrivateIdentifierOrPattern()
    {
        return token == SyntaxKind.OpenBraceToken || token == SyntaxKind.OpenBracketToken || token == SyntaxKind.PrivateIdentifier || IsBindingIdentifier();
    }

    private bool IsBindingIdentifier()
    {
        // `let await`/`let yield` in [Yield]/[Await] are allowed here and disallowed in the binder.
        return token == SyntaxKind.Identifier || token > SyntaxKind.LastReservedWord;
    }

    private bool IsImportAttributeName()
    {
        return Utilities.TokenIsIdentifierOrKeyword(token) || token == SyntaxKind.StringLiteral;
    }

    // ── heritage clause predicates (parser.go:6308) ──

    private bool IsValidHeritageClauseObjectLiteral() => LookAhead(NextIsValidHeritageClauseObjectLiteral);

    private bool NextIsValidHeritageClauseObjectLiteral()
    {
        if (NextToken() == SyntaxKind.CloseBraceToken)
        {
            // "extends {}" only treats the {} as what we're extending (not the class body) for:
            //   extends {} {   extends {},   extends {} extends   extends {} implements
            SyntaxKind next = NextToken();
            return next == SyntaxKind.CommaToken || next == SyntaxKind.OpenBraceToken || next == SyntaxKind.ExtendsKeyword || next == SyntaxKind.ImplementsKeyword;
        }
        return true;
    }

    private bool IsHeritageClause()
    {
        return token == SyntaxKind.ExtendsKeyword || token == SyntaxKind.ImplementsKeyword;
    }

    private bool IsHeritageClauseExtendsOrImplementsKeyword()
    {
        return IsHeritageClause() && LookAhead(NextIsStartOfExpression);
    }

    // ── using / await-using predicates (parser.go:6340) ──

    private bool IsUsingDeclaration()
    {
        // 'using' starts a lexical declaration if followed by an identifier. We eagerly parse
        // ObjectBindingPattern (grammar error reported in check) but not ArrayBindingPattern (conflicts
        // with element access like `using[x]`).
        return LookAhead(() => NextTokenIsBindingIdentifierOrStartOfDestructuringOnSameLine(false));
    }

    private bool NextTokenIsBindingIdentifierOrStartOfDestructuringOnSameLine(bool disallowOf)
    {
        NextToken();
        if (disallowOf && token == SyntaxKind.OfKeyword)
        {
            return LookAhead(NextTokenIsEqualsOrSemicolonOrColonToken);
        }
        return (IsBindingIdentifier() || token == SyntaxKind.OpenBraceToken) && !HasPrecedingLineBreak();
    }

    private bool NextTokenIsBindingIdentifierOrStartOfDestructuringOnSameLineDisallowOf() =>
        NextTokenIsBindingIdentifierOrStartOfDestructuringOnSameLine(true);

    private bool IsAwaitUsingDeclaration()
    {
        return LookAhead(NextIsUsingKeywordThenBindingIdentifierOrStartOfObjectDestructuringOnSameLine);
    }

    private bool NextIsUsingKeywordThenBindingIdentifierOrStartOfObjectDestructuringOnSameLine()
    {
        return NextToken() == SyntaxKind.UsingKeyword && NextTokenIsBindingIdentifierOrStartOfDestructuringOnSameLine(false);
    }

    // ── decorator grammar check (parser.go:6688) ──

    private void CheckJSDecoratorSyntax(Node? node)
    {
        Node n = node!;
        Node[]? modifiers = n.ModifierNodes();
        if (modifiers == null || modifiers.Length == 0)
        {
            return;
        }
        if (AstUtilities.CanHaveIllegalDecorators(n))
        {
            foreach (Node modifier in modifiers)
            {
                if (NodeTests.IsDecorator(modifier))
                {
                    JsErrorAtRange(modifier.Loc, Diag.Decorators_are_not_valid_here);
                    break;
                }
            }
        }
        else if (AstUtilities.CanHaveDecorators(n))
        {
            int decoratorIndex = CoreUtil.FindIndex(modifiers, NodeTests.IsDecorator);
            if (decoratorIndex >= 0)
            {
                if (NodeTests.IsClassDeclaration(n))
                {
                    int exportIndex = CoreUtil.FindIndex(modifiers, IsExportModifier);
                    if (exportIndex >= 0)
                    {
                        int defaultIndex = CoreUtil.FindIndex(modifiers, static m => m.Kind == SyntaxKind.DefaultKeyword);
                        if (decoratorIndex > exportIndex && defaultIndex >= 0 && decoratorIndex < defaultIndex)
                        {
                            // Decorator between `export` and `default`.
                            JsErrorAtRange(modifiers[decoratorIndex].Loc, Diag.Decorators_are_not_valid_here);
                        }
                        else if (decoratorIndex < exportIndex)
                        {
                            // Find a trailing decorator after the export keyword.
                            int trailingDecoratorIndex = -1;
                            for (int i = exportIndex; i < modifiers.Length; i++)
                            {
                                if (NodeTests.IsDecorator(modifiers[i]))
                                {
                                    trailingDecoratorIndex = i;
                                    break;
                                }
                            }
                            if (trailingDecoratorIndex >= 0)
                            {
                                Diagnostic diag = Diagnostic.NewDiagnostic(
                                    null,
                                    new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, modifiers[trailingDecoratorIndex].Loc.Pos), modifiers[trailingDecoratorIndex].Loc.End),
                                    Diag.Decorators_may_not_appear_after_export_or_export_default_if_they_also_appear_before_export);
                                diag.AddRelatedInfo(Diagnostic.NewDiagnostic(
                                    null,
                                    new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, modifiers[decoratorIndex].Loc.Pos), modifiers[decoratorIndex].Loc.End),
                                    Diag.Decorator_used_before_export_here));
                                jsDiagnostics.Add(diag);
                            }
                        }
                    }
                }
            }
        }
    }

    // ── reparse/export helper (reparser.go:696) ──

    private ModifierList CreateExportModifier(Node? locationNode)
    {
        Node exportModifier = factory.NewModifier(SyntaxKind.ExportKeyword);
        exportModifier.Loc = locationNode!.Loc;
        exportModifier.Flags = contextFlags | NodeFlags.Reparsed;
        Node[] nodes = new Node[] { exportModifier };
        return NewModifierList(locationNode.Loc, nodes);
    }

    // ── remaining JSDoc/reparser deep paths (jsdoc.go / reparser.go) — still stubs ──
    // These belong to the @template/@callback/@augments reparse surface which pulls in a large
    // JSDoc-cluster dependency set; left as faithful stubs (breadth-first, per task铁律10).
    private NodeList? GatherTypeParameters(Node? j, bool typedefOrCallback) => throw new System.NotImplementedException("gatherTypeParameters");
    private NodeList ParseCallbackTagParameters(int indent) => throw new System.NotImplementedException("parseCallbackTagParameters");
    private Node ParseTemplateTagTypeParameter() => throw new System.NotImplementedException("parseTemplateTagTypeParameter");
    private NodeList ParseTemplateTagTypeParameters() => throw new System.NotImplementedException("parseTemplateTagTypeParameters");
    private Node ParseExpressionWithTypeArgumentsForAugments() => throw new System.NotImplementedException("parseExpressionWithTypeArgumentsForAugments");
}
