// Ported from tsgo internal/printer/printer.go (Misc + Statements + Declarations + Module references +
// JSX + Clauses + Property assignments + Enum + JSDoc + Top-level sections).

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using EF = global::Ts.Printer.EmitFlags;
using AstUtil = global::Ts.Ast.AstUtilities;
using Precedence = global::Ts.Ast.Precedence;

public sealed partial class Printer
{
    //
    // Misc
    //

    private void EmitTemplateSpan(TemplateSpan node)
    {
        var state = EnterNode(node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Comma);
        EmitTemplateMiddleTail(node.Literal!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateSpanNode(Node node)
    {
        EmitTemplateSpan(node.AsTemplateSpan());
    }

    private void EmitSemicolonClassElement(SemicolonClassElement node)
    {
        var state = EnterNode(node.AsNode());
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    //
    // Statements
    //

    private bool IsEmptyBlock(Node block, StatementList statements)
    {
        return statements.Nodes.Length == 0 &&
            (currentSourceFile is null || PrinterUtilities.RangeEndIsOnSameLineAsRangeStart(block.Loc, block.Loc, currentSourceFile));
    }

    private void EmitBlock(Block node)
    {
        var state = EnterNode(node.AsNode());
        GenerateNames(node.AsNode());
        EmitToken(SyntaxKind.OpenBraceToken, node.Pos, WriteKind.Punctuation, node.AsNode());

        var format = (!node.MultiLine && IsEmptyBlock(node.AsNode(), node.Statements!)) || ShouldEmitOnSingleLine(node.AsNode())
            ? ListFormat.SingleLineBlockStatements
            : ListFormat.MultiLineBlockStatements;
        EmitList(static (p, n) => p.EmitStatement(n), node.AsNode(), node.Statements, format);

        EmitTokenEx(SyntaxKind.CloseBraceToken, node.Statements!.End, WriteKind.Punctuation, node.AsNode(), (format & ListFormat.MultiLine) != 0 ? TokenEmitFlags.IndentLeadingComments : TokenEmitFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitVariableStatement(VariableStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        EmitVariableDeclarationList(node.DeclarationList!.AsVariableDeclarationList());
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitEmptyStatement(EmptyStatement node, bool isEmbeddedStatement)
    {
        var state = EnterNode(node.AsNode());

        if (isEmbeddedStatement)
        {
            WritePunctuation(";");
        }
        else
        {
            WriteTrailingSemicolon();
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitExpressionStatement(ExpressionStatement node)
    {
        var state = EnterNode(node.AsNode());

        if (currentSourceFile is not null && currentSourceFile.ScriptKind == ScriptKind.Json)
        {
            EmitExpression(node.Expression!, OperatorPrecedence.Comma);
        }
        else if (PrinterUtilities.IsImmediatelyInvokedFunctionExpressionOrArrowFunction(node.Expression!))
        {
            EmitIIFEWithParenthesizedCallee(node.Expression!);
        }
        else
        {
            switch (Precedence.GetLeftmostExpression(node.Expression!, false /*stopAtCallExpression*/).Kind)
            {
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.ObjectLiteralExpression:
                    EmitExpression(node.Expression!, OperatorPrecedence.Parentheses);
                    break;
                default:
                    EmitExpression(node.Expression!, OperatorPrecedence.Comma);
                    break;
            }
        }

        if (currentSourceFile is null ||
            currentSourceFile.ScriptKind != ScriptKind.Json ||
            AstUtil.NodeIsSynthesized(node.Expression!))
        {
            WriteTrailingSemicolon();
        }

        ExitNode(node.AsNode(), state);
    }

    private void EmitIIFEWithParenthesizedCallee(Node node)
    {
        var call = AstUtil.SkipPartiallyEmittedExpressions(node).AsCallExpression();
        var state = EnterNode(call.AsNode());
        WritePunctuation("(");
        EmitExpression(call.Expression!, OperatorPrecedence.Lowest);
        WritePunctuation(")");
        EmitTokenNode(call.QuestionDotToken);
        EmitTypeArguments(call.AsNode(), call.TypeArguments);
        EmitList(static (p, n) => p.EmitArgument(n), call.AsNode(), call.Arguments, ListFormat.CallExpressionArguments);
        ExitNode(call.AsNode(), state);
    }

    private void EmitIfStatement(IfStatement node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.IfKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.OpenParenToken, pos, WriteKind.Punctuation, node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        EmitToken(SyntaxKind.CloseParenToken, node.Expression!.End, WriteKind.Punctuation, node.AsNode());
        EmitEmbeddedStatement(node.AsNode(), node.ThenStatement!);
        if (node.ElseStatement is not null)
        {
            WriteLineOrSpace(node.AsNode(), node.ThenStatement, node.ElseStatement);
            EmitToken(SyntaxKind.ElseKeyword, node.ThenStatement!.End, WriteKind.Keyword, node.AsNode());
            if (node.ElseStatement.Kind == SyntaxKind.IfStatement)
            {
                WriteSpace();
                EmitIfStatement(node.ElseStatement.AsIfStatement());
            }
            else
            {
                EmitEmbeddedStatement(node.AsNode(), node.ElseStatement);
            }
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitWhileClause(Node node, Node expression, int startPos)
    {
        var pos = EmitToken(SyntaxKind.WhileKeyword, startPos, WriteKind.Keyword, node);
        WriteSpace();
        EmitToken(SyntaxKind.OpenParenToken, pos, WriteKind.Punctuation, node);
        EmitExpression(expression, OperatorPrecedence.Lowest);
        EmitToken(SyntaxKind.CloseParenToken, expression.End, WriteKind.Punctuation, node);
    }

    private void EmitDoStatement(DoStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.DoKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        EmitEmbeddedStatement(node.AsNode(), node.Statement!);
        if (NodeTests.IsBlock(node.Statement!) && !Options.PreserveSourceNewlines)
        {
            WriteSpace();
        }
        else
        {
            WriteLineOrSpace(node.AsNode(), node.Statement, node.Expression);
        }

        EmitWhileClause(node.AsNode(), node.Expression!, node.Statement!.End);
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitWhileStatement(WhileStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitWhileClause(node.AsNode(), node.Expression!, node.Pos);
        EmitEmbeddedStatement(node.AsNode(), node.Statement!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitForInitializer(Node node)
    {
        if (node.Kind == SyntaxKind.VariableDeclarationList)
        {
            EmitVariableDeclarationList(node.AsVariableDeclarationList());
        }
        else
        {
            EmitExpression(node, OperatorPrecedence.Lowest);
        }
    }

    private void EmitForStatement(ForStatement node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.ForKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        pos = EmitToken(SyntaxKind.OpenParenToken, pos, WriteKind.Punctuation, node.AsNode());
        if (node.Initializer is not null)
        {
            EmitForInitializer(node.Initializer);
            pos = node.Initializer.End;
        }
        pos = EmitToken(SyntaxKind.SemicolonToken, pos, WriteKind.Punctuation, node.AsNode());
        if (node.Condition is not null)
        {
            WriteSpace();
            EmitExpression(node.Condition, OperatorPrecedence.Lowest);
            pos = node.Condition.End;
        }
        pos = EmitToken(SyntaxKind.SemicolonToken, pos, WriteKind.Punctuation, node.AsNode());
        if (node.Incrementor is not null)
        {
            WriteSpace();
            EmitExpression(node.Incrementor, OperatorPrecedence.Lowest);
            pos = node.Incrementor.End;
        }
        EmitToken(SyntaxKind.CloseParenToken, pos, WriteKind.Punctuation, node.AsNode());
        EmitEmbeddedStatement(node.AsNode(), node.Statement!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitForInStatement(ForInOrOfStatement node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.ForKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.OpenParenToken, pos, WriteKind.Punctuation, node.AsNode());
        EmitForInitializer(node.Initializer!);
        WriteSpace();
        EmitToken(SyntaxKind.InKeyword, node.Initializer!.End, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        EmitToken(SyntaxKind.CloseParenToken, node.Expression!.End, WriteKind.Punctuation, node.AsNode());
        EmitEmbeddedStatement(node.AsNode(), node.Statement!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitForOfStatement(ForInOrOfStatement node)
    {
        var state = EnterNode(node.AsNode());
        var openParenPos = EmitToken(SyntaxKind.ForKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        if (node.AwaitModifier is not null)
        {
            EmitKeywordNode(node.AwaitModifier);
            WriteSpace();
        }
        EmitToken(SyntaxKind.OpenParenToken, openParenPos, WriteKind.Punctuation, node.AsNode());
        EmitForInitializer(node.Initializer!);
        WriteSpace();
        EmitToken(SyntaxKind.OfKeyword, node.Initializer!.End, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        EmitToken(SyntaxKind.CloseParenToken, node.Expression!.End, WriteKind.Punctuation, node.AsNode());
        EmitEmbeddedStatement(node.AsNode(), node.Statement!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitContinueStatement(ContinueStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.ContinueKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        if (node.Label is not null)
        {
            WriteSpace();
            EmitLabelIdentifier(node.Label.AsIdentifier());
        }
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitBreakStatement(BreakStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.BreakKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        if (node.Label is not null)
        {
            WriteSpace();
            EmitLabelIdentifier(node.Label.AsIdentifier());
        }
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitReturnStatement(ReturnStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.ReturnKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        if (node.Expression is not null)
        {
            WriteSpace();
            EmitExpressionNoASI(node.Expression, OperatorPrecedence.Lowest);
        }
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitWithStatement(WithStatement node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.WithKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.OpenParenToken, pos, WriteKind.Punctuation, node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        EmitToken(SyntaxKind.CloseParenToken, node.Expression!.End, WriteKind.Punctuation, node.AsNode());
        EmitEmbeddedStatement(node.AsNode(), node.Statement!);
        ExitNode(node.AsNode(), state);
    }

    private void EmitSwitchStatement(SwitchStatement node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.SwitchKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.OpenParenToken, pos, WriteKind.Punctuation, node.AsNode());
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        EmitToken(SyntaxKind.CloseParenToken, node.Expression!.End, WriteKind.Punctuation, node.AsNode());
        WriteSpace();
        EmitCaseBlock(node.CaseBlock!.AsCaseBlock());
        ExitNode(node.AsNode(), state);
    }

    private void EmitLabeledStatement(LabeledStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitLabelIdentifier(node.Label!.AsIdentifier());
        EmitToken(SyntaxKind.ColonToken, node.Label!.End, WriteKind.Punctuation, node.AsNode());

        WriteSpace();
        EmitStatement(node.Statement!);

        ExitNode(node.AsNode(), state);
    }

    private void EmitThrowStatement(ThrowStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.ThrowKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpressionNoASI(node.Expression!, OperatorPrecedence.Lowest);
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitTryStatement(TryStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.TryKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitBlock(node.TryBlock!.AsBlock());
        if (node.CatchClause is not null)
        {
            WriteLineOrSpace(node.AsNode(), node.TryBlock, node.CatchClause);
            EmitCatchClause(node.CatchClause.AsCatchClause());
        }
        if (node.FinallyBlock is not null)
        {
            WriteLineOrSpace(node.AsNode(), global::Ts.Core.CoreUtil.Coalesce(node.CatchClause, node.TryBlock), node.FinallyBlock);
            EmitToken(SyntaxKind.FinallyKeyword, global::Ts.Core.CoreUtil.Coalesce(node.CatchClause, node.TryBlock)!.End, WriteKind.Keyword, node.AsNode());
            WriteSpace();
            EmitBlock(node.FinallyBlock.AsBlock());
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitDebuggerStatement(DebuggerStatement node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.DebuggerKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitNotEmittedStatement(NotEmittedStatement node)
    {
        ExitNode(node.AsNode(), EnterNode(node.AsNode()));
    }

    private void EmitNotEmittedTypeElement(NotEmittedTypeElement node)
    {
        ExitNode(node.AsNode(), EnterNode(node.AsNode()));
    }

    //
    // Declarations
    //

    private void EmitVariableDeclaration(VariableDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitBindingName(node.Name());
        EmitPunctuationNode(node.ExclamationToken);
        EmitTypeAnnotation(node.Type);
        EmitInitializer(node.Initializer, PrinterUtilities.GreatestEnd(node.Name()!.End, node.Type, emitContext.GetTypeNode(node.Name()!)), node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitVariableDeclarationNode(Node node)
    {
        EmitVariableDeclaration(node.AsVariableDeclaration());
    }

    private void EmitVariableDeclarationList(VariableDeclarationList node)
    {
        var state = EnterNode(node.AsNode());
        if (PrinterUtilities.IsVarLet(node.AsNode()))
        {
            WriteKeyword("let");
        }
        else if (PrinterUtilities.IsVarConst(node.AsNode()))
        {
            WriteKeyword("const");
        }
        else if (PrinterUtilities.IsVarUsing(node.AsNode()))
        {
            WriteKeyword("using");
        }
        else if (AstUtil.IsVarAwaitUsing(node.AsNode()))
        {
            WriteKeyword("await");
            WriteSpace();
            WriteKeyword("using");
        }
        else
        {
            WriteKeyword("var");
        }
        WriteSpace();
        EmitList(static (p, n) => p.EmitVariableDeclarationNode(n), node.AsNode(), node.Declarations, ListFormat.VariableDeclarationList);
        ExitNode(node.AsNode(), state);
    }

    private void EmitFunctionDeclaration(FunctionDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        GenerateNameIfNeeded(node.Name());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("function");
        EmitTokenNode(node.AsteriskToken);
        WriteSpace();
        if (node.Name() is { } name)
        {
            EmitIdentifierName(name.AsIdentifier());
        }
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        EmitFunctionBodyNode(node.Body);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitClassDeclaration(ClassDeclaration node)
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

    private void EmitInterfaceDeclaration(InterfaceDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("interface");
        WriteSpace();
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        EmitTypeParameters(node.AsNode(), node.TypeParameters);
        EmitList(static (p, n) => p.EmitHeritageClauseNode(n), node.AsNode(), node.HeritageClauses, ListFormat.HeritageClauses);
        WriteSpace();
        WritePunctuation("{");
        PushNameGenerationScope(node.AsNode());
        GenerateAllMemberNames(node.Members);
        EmitList(static (p, n) => p.EmitTypeElement(n), node.AsNode(), node.Members, ListFormat.InterfaceMembers);
        PopNameGenerationScope(node.AsNode());
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeAliasDeclaration(TypeAliasDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("type");
        WriteSpace();
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        EmitTypeParameters(node.AsNode(), node.TypeParameters);
        WriteSpace();
        WritePunctuation("=");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node.Type!);
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitEnumDeclaration(EnumDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("enum");
        WriteSpace();
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        WriteSpace();
        WritePunctuation("{");
        EmitList(static (p, n) => p.EmitEnumMemberNode(n), node.AsNode(), node.Members, ListFormat.EnumMembers);
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitModuleDeclaration(ModuleDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        if (node.Keyword != SyntaxKind.GlobalKeyword)
        {
            WriteKeyword(node.Keyword == SyntaxKind.NamespaceKeyword ? "namespace" : "module");
            WriteSpace();
        }
        EmitModuleName(node.Name());
        var body = node.Body;
        while (body is not null && NodeTests.IsModuleDeclaration(body))
        {
            var module = body.AsModuleDeclaration();
            WritePunctuation(".");
            EmitNestedModuleName(module.Name());
            body = module.Body;
        }
        if (body is null)
        {
            WriteTrailingSemicolon();
        }
        else
        {
            WriteSpace();
            EmitModuleBlock(body.AsModuleBlock());
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitModuleBlock(ModuleBlock node)
    {
        var state = EnterNode(node.AsNode());
        GenerateNames(node.AsNode());
        EmitToken(SyntaxKind.OpenBraceToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        var format = (IsEmptyBlock(node.AsNode(), node.Statements!) || ShouldEmitOnSingleLine(node.AsNode()))
            ? ListFormat.SingleLineBlockStatements
            : ListFormat.MultiLineBlockStatements;
        EmitList(static (p, n) => p.EmitStatement(n), node.AsNode(), node.Statements, format);
        EmitTokenEx(SyntaxKind.CloseBraceToken, node.Statements!.End, WriteKind.Punctuation, node.AsNode(), (format & ListFormat.MultiLine) != 0 ? TokenEmitFlags.IndentLeadingComments : TokenEmitFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitCaseBlock(CaseBlock node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.OpenBraceToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        EmitList(static (p, n) => p.EmitCaseOrDefaultClauseNode(n), node.AsNode(), node.Clauses, ListFormat.CaseBlockClauses);
        EmitTokenEx(SyntaxKind.CloseBraceToken, node.Clauses!.End, WriteKind.Punctuation, node.AsNode(), TokenEmitFlags.IndentLeadingComments);
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportEqualsDeclaration(ImportEqualsDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        var pos = EmitToken(SyntaxKind.ImportKeyword, PrinterUtilities.GreatestEnd(node.Pos, node.Modifiers()), WriteKind.Keyword, node.AsNode());
        WriteSpace();
        if (node.IsTypeOnly)
        {
            EmitToken(SyntaxKind.TypeKeyword, pos, WriteKind.Keyword, node.AsNode());
            WriteSpace();
        }
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        WriteSpace();
        EmitToken(SyntaxKind.EqualsToken, node.Name()!.End, WriteKind.Punctuation, node.AsNode());
        WriteSpace();
        EmitModuleReference(node.ModuleReference!);
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitModuleReference(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierReference(node.AsIdentifier());
                break;
            case SyntaxKind.QualifiedName:
                EmitQualifiedName(node.AsQualifiedName());
                break;
            case SyntaxKind.ExternalModuleReference:
                EmitExternalModuleReference(node.AsExternalModuleReference());
                break;
            default:
                throw new InvalidOperationException($"unhandled ModuleReference: {node.Kind}");
        }
    }

    private void EmitImportDeclaration(ImportDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        EmitToken(SyntaxKind.ImportKeyword, PrinterUtilities.GreatestEnd(node.Pos, node.Modifiers()), WriteKind.Keyword, node.AsNode());
        WriteSpace();
        if (node.ImportClause is not null)
        {
            EmitImportClause(node.ImportClause.AsImportClause());
            WriteSpace();
            EmitToken(SyntaxKind.FromKeyword, node.ImportClause.End, WriteKind.Keyword, node.AsNode());
            WriteSpace();
        }
        EmitExpression(node.ModuleSpecifier!, OperatorPrecedence.Lowest);
        if (node.Attributes is not null)
        {
            WriteSpace();
            EmitImportAttributes(node.Attributes.AsImportAttributes());
        }
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportClause(ImportClause node)
    {
        var state = EnterNode(node.AsNode());
        if (node.PhaseModifier != SyntaxKind.Unknown)
        {
            EmitToken(node.PhaseModifier, node.Pos, WriteKind.Keyword, node.AsNode());
            WriteSpace();
        }
        if (node.Name() is { } name)
        {
            EmitBindingIdentifier(node.Name()!.AsIdentifier());
            if (node.NamedBindings is not null)
            {
                EmitToken(SyntaxKind.CommaToken, name.End, WriteKind.Punctuation, node.AsNode());
                WriteSpace();
            }
        }
        EmitNamedImportBindings(node.NamedBindings);
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamespaceImport(NamespaceImport node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.AsteriskToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.AsKeyword, pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamedImports(NamedImports node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("{");
        EmitList(static (p, n) => p.EmitImportSpecifierNode(n), node.AsNode(), node.Elements, ListFormat.NamedImportsOrExportsElements);
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamedImportBindings(Node? node)
    {
        if (node is null)
        {
            return;
        }
        switch (node.Kind)
        {
            case SyntaxKind.NamespaceImport:
                EmitNamespaceImport(node.AsNamespaceImport());
                break;
            case SyntaxKind.NamedImports:
                EmitNamedImports(node.AsNamedImports());
                break;
            default:
                throw new InvalidOperationException($"unhandled NamedImportBindings: {node.Kind}");
        }
    }

    private void EmitImportSpecifier(ImportSpecifier node)
    {
        var state = EnterNode(node.AsNode());
        if (node.IsTypeOnly)
        {
            WriteKeyword("type");
            WriteSpace();
        }
        if (node.PropertyName is not null)
        {
            EmitModuleExportName(node.PropertyName);
            WriteSpace();
            EmitToken(SyntaxKind.AsKeyword, node.PropertyName.End, WriteKind.Keyword, node.AsNode());
            WriteSpace();
        }
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportSpecifierNode(Node node)
    {
        EmitImportSpecifier(node.AsImportSpecifier());
    }

    private void EmitExportAssignment(ExportAssignment node)
    {
        var state = EnterNode(node.AsNode());
        var nextPos = EmitToken(SyntaxKind.ExportKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        if (node.IsExportEquals)
        {
            EmitToken(SyntaxKind.EqualsToken, nextPos, WriteKind.Operator, node.AsNode());
        }
        else
        {
            EmitToken(SyntaxKind.DefaultKeyword, nextPos, WriteKind.Keyword, node.AsNode());
        }
        WriteSpace();
        if (node.IsExportEquals)
        {
            EmitExpression(node.Expression!, OperatorPrecedence.Assignment);
        }
        else
        {
            var expr = Precedence.GetLeftmostExpression(node.Expression!, false /*stopAtCallExpressions*/);
            if (NodeTests.IsClassExpression(expr) || NodeTests.IsFunctionExpression(expr))
            {
                EmitExpression(node.Expression!, OperatorPrecedence.Parentheses);
            }
            else
            {
                EmitExpression(node.Expression!, OperatorPrecedence.Assignment);
            }
        }
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitExportDeclaration(ExportDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        var pos = EmitToken(SyntaxKind.ExportKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        if (node.IsTypeOnly)
        {
            pos = EmitToken(SyntaxKind.TypeKeyword, pos, WriteKind.Keyword, node.AsNode());
            WriteSpace();
        }
        if (node.ExportClause is not null)
        {
            EmitNamedExportBindings(node.ExportClause);
        }
        else
        {
            pos = EmitToken(SyntaxKind.AsteriskToken, pos, WriteKind.Punctuation, node.AsNode());
        }
        if (node.ModuleSpecifier is not null)
        {
            WriteSpace();
            EmitToken(SyntaxKind.FromKeyword, PrinterUtilities.GreatestEnd(pos, node.ExportClause), WriteKind.Keyword, node.AsNode());
            WriteSpace();
            EmitExpression(node.ModuleSpecifier, OperatorPrecedence.Lowest);
        }
        if (node.Attributes is not null)
        {
            WriteSpace();
            EmitImportAttributes(node.Attributes.AsImportAttributes());
        }
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportAttributes(ImportAttributes node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(node.Token, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitList(static (p, n) => p.EmitImportAttributeNode(n), node.AsNode(), node.Attributes, ListFormat.ImportAttributes);
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportAttribute(ImportAttribute node)
    {
        var state = EnterNode(node.AsNode());
        EmitImportAttributeName(node.Name()!);
        WritePunctuation(":");
        WriteSpace();
        var value = node.Value!;
        if ((emitContext.EmitFlags(node.Value!) & EF.NoLeadingComments) == 0)
        {
            var commentRange = emitContext.CommentRange(value);
            EmitTrailingComments(commentRange.Pos, CommentSeparator.After);
        }
        EmitExpression(value, OperatorPrecedence.DisallowComma);
        ExitNode(node.AsNode(), state);
    }

    private void EmitImportAttributeNode(Node node)
    {
        EmitImportAttribute(node.AsImportAttribute());
    }

    private void EmitNamespaceExportDeclaration(NamespaceExportDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.ExportKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        pos = EmitToken(SyntaxKind.AsKeyword, pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.NamespaceKeyword, pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamespaceExport(NamespaceExport node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.AsteriskToken, node.Pos, WriteKind.Punctuation, node.AsNode());
        WriteSpace();
        EmitToken(SyntaxKind.AsKeyword, pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitModuleExportName(node.Name());
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamedExports(NamedExports node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("{");
        EmitList(static (p, n) => p.EmitExportSpecifierNode(n), node.AsNode(), node.Elements, ListFormat.NamedImportsOrExportsElements);
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitNamedExportBindings(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.NamespaceExport:
                EmitNamespaceExport(node.AsNamespaceExport());
                break;
            case SyntaxKind.NamedExports:
                EmitNamedExports(node.AsNamedExports());
                break;
            default:
                throw new InvalidOperationException($"unhandled NamedExportBindings: {node.Kind}");
        }
    }

    private void EmitExportSpecifier(ExportSpecifier node)
    {
        var state = EnterNode(node.AsNode());
        if (node.IsTypeOnly)
        {
            WriteKeyword("type");
            WriteSpace();
        }
        if (node.PropertyName is not null)
        {
            EmitModuleExportName(node.PropertyName);
            WriteSpace();
            EmitToken(SyntaxKind.AsKeyword, node.PropertyName.End, WriteKind.Keyword, node.AsNode());
            WriteSpace();
        }
        EmitModuleExportName(node.Name());
        ExitNode(node.AsNode(), state);
    }

    private void EmitExportSpecifierNode(Node node)
    {
        EmitExportSpecifier(node.AsExportSpecifier());
    }

    private void EmitEmbeddedStatement(Node parentNode, Node node)
    {
        if (NodeTests.IsBlock(node) ||
            ShouldEmitOnSingleLine(parentNode) ||
            Options.PreserveSourceNewlines && GetLeadingLineTerminatorCount(parentNode, node, ListFormat.None) == 0)
        {
            WriteSpace();
            EmitStatement(node);
        }
        else
        {
            WriteLine();
            IncreaseIndent();
            if (node.Kind == SyntaxKind.EmptyStatement)
            {
                EmitEmptyStatement(node.AsEmptyStatement(), true /*isEmbeddedStatement*/);
            }
            else
            {
                EmitStatement(node);
            }
            DecreaseIndent();
        }
    }

    private void EmitStatement(Node node)
    {
        switch (node.Kind)
        {
            // Statements
            case SyntaxKind.Block:
                EmitBlock(node.AsBlock());
                break;
            case SyntaxKind.EmptyStatement:
                EmitEmptyStatement(node.AsEmptyStatement(), false /*isEmbeddedStatement*/);
                break;
            case SyntaxKind.VariableStatement:
                EmitVariableStatement(node.AsVariableStatement());
                break;
            case SyntaxKind.ExpressionStatement:
                EmitExpressionStatement(node.AsExpressionStatement());
                break;
            case SyntaxKind.IfStatement:
                EmitIfStatement(node.AsIfStatement());
                break;
            case SyntaxKind.DoStatement:
                EmitDoStatement(node.AsDoStatement());
                break;
            case SyntaxKind.WhileStatement:
                EmitWhileStatement(node.AsWhileStatement());
                break;
            case SyntaxKind.ForStatement:
                EmitForStatement(node.AsForStatement());
                break;
            case SyntaxKind.ForInStatement:
                EmitForInStatement(node.AsForInOrOfStatement());
                break;
            case SyntaxKind.ForOfStatement:
                EmitForOfStatement(node.AsForInOrOfStatement());
                break;
            case SyntaxKind.ContinueStatement:
                EmitContinueStatement(node.AsContinueStatement());
                break;
            case SyntaxKind.BreakStatement:
                EmitBreakStatement(node.AsBreakStatement());
                break;
            case SyntaxKind.ReturnStatement:
                EmitReturnStatement(node.AsReturnStatement());
                break;
            case SyntaxKind.WithStatement:
                EmitWithStatement(node.AsWithStatement());
                break;
            case SyntaxKind.SwitchStatement:
                EmitSwitchStatement(node.AsSwitchStatement());
                break;
            case SyntaxKind.LabeledStatement:
                EmitLabeledStatement(node.AsLabeledStatement());
                break;
            case SyntaxKind.ThrowStatement:
                EmitThrowStatement(node.AsThrowStatement());
                break;
            case SyntaxKind.TryStatement:
                EmitTryStatement(node.AsTryStatement());
                break;
            case SyntaxKind.DebuggerStatement:
                EmitDebuggerStatement(node.AsDebuggerStatement());
                break;
            case SyntaxKind.NotEmittedStatement:
                EmitNotEmittedStatement(node.AsNotEmittedStatement());
                break;

            // Declaration Statements
            case SyntaxKind.FunctionDeclaration:
                EmitFunctionDeclaration(node.AsFunctionDeclaration());
                break;
            case SyntaxKind.ClassDeclaration:
                EmitClassDeclaration(node.AsClassDeclaration());
                break;
            case SyntaxKind.InterfaceDeclaration:
                EmitInterfaceDeclaration(node.AsInterfaceDeclaration());
                break;
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                EmitTypeAliasDeclaration(node.AsTypeAliasDeclaration());
                break;
            case SyntaxKind.EnumDeclaration:
                EmitEnumDeclaration(node.AsEnumDeclaration());
                break;
            case SyntaxKind.ModuleDeclaration:
                EmitModuleDeclaration(node.AsModuleDeclaration());
                break;
            case SyntaxKind.MissingDeclaration:
                break;

            // Import/Export Statements
            case SyntaxKind.NamespaceExportDeclaration:
                EmitNamespaceExportDeclaration(node.AsNamespaceExportDeclaration());
                break;
            case SyntaxKind.ImportEqualsDeclaration:
                EmitImportEqualsDeclaration(node.AsImportEqualsDeclaration());
                break;
            case SyntaxKind.ImportDeclaration:
                EmitImportDeclaration(node.AsImportDeclaration());
                break;
            case SyntaxKind.ExportAssignment:
                EmitExportAssignment(node.AsExportAssignment());
                break;
            case SyntaxKind.ExportDeclaration:
                EmitExportDeclaration(node.AsExportDeclaration());
                break;

            default:
                throw new InvalidOperationException($"unhandled statement: {node.Kind}");
        }
    }

    //
    // Module references
    //

    private void EmitExternalModuleReference(ExternalModuleReference node)
    {
        var state = EnterNode(node.AsNode());
        WriteKeyword("require");
        WritePunctuation("(");
        EmitExpression(node.Expression!, OperatorPrecedence.DisallowComma);
        WritePunctuation(")");
        ExitNode(node.AsNode(), state);
    }

    //
    // Clauses
    //

    private void EmitCaseOrDefaultClauseStatements(CaseOrDefaultClause node, int colonPos)
    {
        bool emitAsSingleStatement = node.Statements!.Nodes.Length == 1 &&
            (currentSourceFile is null ||
                AstUtil.NodeIsSynthesized(node.AsNode()) ||
                AstUtil.NodeIsSynthesized(node.Statements.Nodes[0]) ||
                PrinterUtilities.RangeStartPositionsAreOnSameLine(node.Loc, node.Statements.Nodes[0].Loc, currentSourceFile));

        var format = ListFormat.CaseOrDefaultClauseStatements;
        if (emitAsSingleStatement)
        {
            WriteTokenText(SyntaxKind.ColonToken, WriteKind.Punctuation, colonPos);
            WriteSpace();
            format &= ~(ListFormat.MultiLine | ListFormat.Indented);
        }
        else
        {
            EmitToken(SyntaxKind.ColonToken, colonPos, WriteKind.Punctuation, node.AsNode());
        }

        EmitList(static (p, n) => p.EmitStatement(n), node.AsNode(), node.Statements, format);
    }

    private void EmitCaseClause(CaseOrDefaultClause node)
    {
        var state = EnterNode(node.AsNode());
        EmitToken(SyntaxKind.CaseKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        EmitCaseOrDefaultClauseStatements(node, node.Expression!.End);
        ExitNode(node.AsNode(), state);
    }

    private void EmitDefaultClause(CaseOrDefaultClause node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitToken(SyntaxKind.DefaultKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        EmitCaseOrDefaultClauseStatements(node, pos);
        ExitNode(node.AsNode(), state);
    }

    private void EmitCaseOrDefaultClauseNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.CaseClause:
                EmitCaseClause(node.AsCaseOrDefaultClause());
                break;
            case SyntaxKind.DefaultClause:
                EmitDefaultClause(node.AsCaseOrDefaultClause());
                break;
            default:
                throw new InvalidOperationException($"unhandled CaseOrDefaultClause: {node.Kind}");
        }
    }

    private void EmitHeritageClause(HeritageClause node)
    {
        var state = EnterNode(node.AsNode());
        WriteSpace();
        EmitToken(node.Token, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitList(static (p, n) => p.EmitExpressionWithTypeArgumentsNode(n), node.AsNode(), node.Types, ListFormat.HeritageClauseTypes);
        ExitNode(node.AsNode(), state);
    }

    private void EmitHeritageClauseNode(Node node)
    {
        EmitHeritageClause(node.AsHeritageClause());
    }

    private void EmitCatchClause(CatchClause node)
    {
        var state = EnterNode(node.AsNode());
        var openParenPos = EmitToken(SyntaxKind.CatchKeyword, node.Pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();

        if (node.VariableDeclaration is not null)
        {
            EmitToken(SyntaxKind.OpenParenToken, openParenPos, WriteKind.Punctuation, node.AsNode());
            EmitVariableDeclaration(node.VariableDeclaration.AsVariableDeclaration());
            EmitToken(SyntaxKind.CloseParenToken, node.VariableDeclaration.End, WriteKind.Punctuation, node.AsNode());
            WriteSpace();
        }

        EmitBlock(node.Block!.AsBlock());
        ExitNode(node.AsNode(), state);
    }

    //
    // Property assignments
    //

    private void EmitPropertyAssignment(PropertyAssignment node)
    {
        var state = EnterNode(node.AsNode());
        EmitPropertyName(node.Name());
        WritePunctuation(":");
        WriteSpace();
        var initializer = node.Initializer!;
        if ((emitContext.EmitFlags(initializer) & EF.NoLeadingComments) == 0)
        {
            var commentRange = emitContext.CommentRange(initializer);
            EmitTrailingComments(commentRange.Pos, CommentSeparator.After);
        }
        EmitExpression(initializer, OperatorPrecedence.DisallowComma);
        ExitNode(node.AsNode(), state);
    }

    private void EmitShorthandPropertyAssignment(ShorthandPropertyAssignment node)
    {
        var state = EnterNode(node.AsNode());
        EmitPropertyName(node.Name());
        if (node.ObjectAssignmentInitializer is not null)
        {
            WriteSpace();
            WritePunctuation("=");
            WriteSpace();
            EmitExpression(node.ObjectAssignmentInitializer, OperatorPrecedence.DisallowComma);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitSpreadAssignment(SpreadAssignment node)
    {
        var state = EnterNode(node.AsNode());
        if (node.Expression is not null)
        {
            EmitToken(SyntaxKind.DotDotDotToken, node.Pos, WriteKind.Punctuation, node.AsNode());
            EmitExpression(node.Expression, OperatorPrecedence.DisallowComma);
        }
        ExitNode(node.AsNode(), state);
    }

    //
    // Enum
    //

    private void EmitEnumMember(EnumMember node)
    {
        var state = EnterNode(node.AsNode());
        EmitPropertyName(node.Name());
        EmitInitializer(node.Initializer, node.Name()!.End, node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitEnumMemberNode(Node node)
    {
        EmitEnumMember(node.AsEnumMember());
    }

    //
    // JSDoc
    //

    private void EmitJSDocNode(Node node)
    {
        // !!!
        throw new InvalidOperationException("not implemented");
    }
}
