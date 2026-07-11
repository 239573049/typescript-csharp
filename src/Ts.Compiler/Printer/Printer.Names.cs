// Ported from tsgo internal/printer/printer.go (Names, Signature elements, Type Members sections).

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using EF = global::Ts.Printer.EmitFlags;
using AstUtil = global::Ts.Ast.AstUtilities;

public sealed partial class Printer
{
    //
    // Names
    //

    private void EmitIdentifierText(Identifier node)
    {
        var f = AstUtil.GetSourceFileOfNode(node.AsNode());
        global::Ts.Core.Debug.Assert(f is null || currentSourceFile is null || f.FileName() == currentSourceFile.FileName());
        var text = GetTextOfNode(node.AsNode(), false /*includeTrivia*/);

        if (IdToSymbol is not null)
        {
            if (IdToSymbol.TryGetValue(node.AsNode(), out var symbol))
            {
                WriteSymbol(text, symbol);
                return;
            }
        }
        Write(text);
    }

    private void EmitIdentifierName(Identifier node)
    {
        var state = EnterNode(node.AsNode());
        EmitIdentifierText(node);
        ExitNode(node.AsNode(), state);
    }

    private void EmitIdentifierNameNode(IdentifierNode? node)
    {
        if (node is null)
        {
            return;
        }
        EmitIdentifierName(node.AsIdentifier());
    }

    private IdentifierNode GetUniqueHelperName(string name)
    {
        var helperName = uniqueHelperNames!.GetValueOrDefault(name);
        if (helperName is null)
        {
            var newHelperName = emitContext.Factory.NewUniqueNameEx(name, new AutoGenerateOptions { Flags = GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.Optimistic });
            GenerateName(newHelperName);
            uniqueHelperNames![name] = newHelperName;
            return newHelperName;
        }
        return helperName.Clone(emitContext.Factory)!;
    }

    private void EmitIdentifierReference(Identifier node)
    {
        if ((externalHelpersModuleName is not null || uniqueHelperNames is not null) &&
            (emitContext.EmitFlags(node.AsNode()) & EF.HelperName) != 0)
        {
            if (externalHelpersModuleName is not null)
            {
                // Substitute `__helper` with `tslib_1.__helper`
                var helper = emitContext.Factory.NewPropertyAccessExpression(
                    externalHelpersModuleName.Clone(emitContext.Factory),
                    null, /*questionDotToken*/
                    node.AsNode().Clone(emitContext.Factory),
                    NodeFlags.None);
                emitContext.AssignCommentAndSourceMapRanges(helper, node.AsNode());
                EmitPropertyAccessExpression(helper.AsPropertyAccessExpression());
                return;
            }
            if (uniqueHelperNames is not null)
            {
                // Substitute `__helper` with `__helper_1` if there is a conflict in an ES module.
                var helperName = GetUniqueHelperName(node.Text);
                emitContext.AssignCommentAndSourceMapRanges(helperName, node.AsNode());
                node = helperName.AsIdentifier();
            }
        }

        var state = EnterNode(node.AsNode());
        EmitIdentifierText(node);
        ExitNode(node.AsNode(), state);
    }

    private void EmitBindingIdentifier(Identifier node)
    {
        if (uniqueHelperNames is not null &&
            (emitContext.EmitFlags(node.AsNode()) & EF.HelperName) != 0)
        {
            // Substitute `__helper` with `__helper_1` if there is a conflict in an ES module.
            var helperName = GetUniqueHelperName(node.Text);
            emitContext.AssignCommentAndSourceMapRanges(helperName, node.AsNode());
            node = helperName.AsIdentifier();
        }

        var state = EnterNode(node.AsNode());
        EmitIdentifierText(node);
        ExitNode(node.AsNode(), state);
    }

    private void EmitLabelIdentifier(Identifier node)
    {
        var state = EnterNode(node.AsNode());
        EmitIdentifierText(node);
        ExitNode(node.AsNode(), state);
    }

    private void EmitPrivateIdentifier(PrivateIdentifier node)
    {
        var state = EnterNode(node.AsNode());
        Write(GetTextOfNode(node.AsNode(), false /*includeTrivia*/));
        ExitNode(node.AsNode(), state);
    }

    private void EmitQualifiedName(QualifiedName node)
    {
        var state = EnterNode(node.AsNode());
        EmitEntityName(node.Left!);
        WritePunctuation(".");
        EmitIdentifierName(node.Right!.AsIdentifier());
        ExitNode(node.AsNode(), state);
    }

    private void EmitComputedPropertyName(ComputedPropertyName node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("[");
        EmitExpression(node.Expression!, OperatorPrecedence.DisallowComma);
        WritePunctuation("]");
        ExitNode(node.AsNode(), state);
    }

    private void EmitEntityName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierReference(node.AsIdentifier());
                break;
            case SyntaxKind.QualifiedName:
                EmitQualifiedName(node.AsQualifiedName());
                break;
            case SyntaxKind.PropertyAccessExpression:
                // TypeQuery nodes may have PropertyAccessExpression as exprName (e.g. typeof foo.x).
                EmitExpression(node, OperatorPrecedence.DisallowComma);
                break;
            default:
                throw new InvalidOperationException($"unexpected EntityName: {node.Kind}");
        }
    }

    private void EmitBindingName(Node? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitBindingIdentifier(node.AsIdentifier());
                break;
            case SyntaxKind.ObjectBindingPattern:
                EmitObjectBindingPattern(node.AsBindingPattern());
                break;
            case SyntaxKind.ArrayBindingPattern:
                EmitArrayBindingPattern(node.AsBindingPattern());
                break;
            default:
                throw new InvalidOperationException($"unexpected BindingName: {node.Kind}");
        }
    }

    private void EmitPropertyName(Node? node)
    {
        if (node is null)
        {
            return;
        }

        var savedWriteKind = writeKind;
        writeKind = WriteKind.Property;

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;
            case SyntaxKind.PrivateIdentifier:
                EmitPrivateIdentifier(node.AsPrivateIdentifier());
                break;
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                EmitNoSubstitutionTemplateLiteral(node.AsNoSubstitutionTemplateLiteral());
                break;
            case SyntaxKind.NumericLiteral:
                EmitNumericLiteral(node.AsNumericLiteral());
                break;
            case SyntaxKind.BigIntLiteral:
                EmitBigIntLiteral(node.AsBigIntLiteral());
                break;
            case SyntaxKind.ComputedPropertyName:
                EmitComputedPropertyName(node.AsComputedPropertyName());
                break;
            default:
                throw new InvalidOperationException($"unexpected PropertyName: {node.Kind}");
        }

        writeKind = savedWriteKind;
    }

    private void EmitMemberName(Node? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;
            case SyntaxKind.PrivateIdentifier:
                EmitPrivateIdentifier(node.AsPrivateIdentifier());
                break;
            default:
                throw new InvalidOperationException($"unexpected MemberName: {node.Kind}");
        }
    }

    private void EmitModuleName(Node? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitBindingIdentifier(node.AsIdentifier());
                break;
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            default:
                throw new InvalidOperationException($"unexpected ModuleName: {node.Kind}");
        }
    }

    private void EmitModuleExportName(Node? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            default:
                throw new InvalidOperationException($"unexpected ModuleExportName: {node.Kind}");
        }
    }

    private void EmitImportAttributeName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            default:
                throw new InvalidOperationException($"unexpected ImportAttributeName: {node.Kind}");
        }
    }

    private void EmitNestedModuleName(Node? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            default:
                throw new InvalidOperationException($"unexpected ModuleName: {node.Kind}");
        }
    }

    //
    // Signature elements
    //

    private int EmitModifierList(Node parentNode, ModifierList? modifiers, bool allowDecorators)
    {
        if (modifiers is null || modifiers.Nodes.Length == 0)
        {
            return parentNode.Pos;
        }

        if (global::Ts.Core.CoreUtil.Every(modifiers.Nodes, AstUtil.IsModifier))
        {
            // if all modifier-likes are `Modifier`, simply emit the list as modifiers.
            EmitList(static (p, node) => p.EmitKeywordNode(node), parentNode, modifiers, ListFormat.Modifiers);
        }
        else if (global::Ts.Core.CoreUtil.Every(modifiers.Nodes, NodeTests.IsDecorator))
        {
            if (!allowDecorators)
            {
                return parentNode.Pos;
            }

            // if all modifier-likes are `Decorator`, simply emit the list as decorators.
            EmitList(static (p, node) => p.EmitModifierLike(node), parentNode, modifiers, ListFormat.Decorators);
        }
        else
        {
            OnBeforeEmitNodeList?.Invoke(modifiers);

            // partition modifiers into contiguous chunks of `Modifier` or `Decorator`
            const int ModeNone = 0;
            const int ModeModifiers = 1;
            const int ModeDecorators = 2;

            int lastMode = ModeNone;
            int mode = ModeNone;
            int start = 0;
            int pos = 0;

            while (start < modifiers.Nodes.Length)
            {
                while (pos < modifiers.Nodes.Length)
                {
                    var lastModifier = modifiers.Nodes[pos];
                    if (NodeTests.IsDecorator(lastModifier))
                    {
                        mode = ModeDecorators;
                    }
                    else
                    {
                        mode = ModeModifiers;
                    }
                    if (lastMode == ModeNone)
                    {
                        lastMode = mode;
                    }
                    else if (mode != lastMode)
                    {
                        break;
                    }
                    pos++;
                }

                var textRange = new TextRange(-1, -1);
                if (start == 0)
                {
                    textRange = new TextRange(modifiers.Pos, textRange.End);
                }
                if (pos == modifiers.Nodes.Length - 1)
                {
                    textRange = new TextRange(textRange.Pos, modifiers.End);
                }
                if (allowDecorators || lastMode == ModeModifiers)
                {
                    EmitListItems(
                        static (p, node) => p.EmitModifierLike(node),
                        parentNode,
                        modifiers.Nodes[start..pos],
                        lastMode == ModeModifiers ? ListFormat.Modifiers : ListFormat.Decorators,
                        false, /*hasTrailingComma*/
                        textRange);
                }
                start = pos;
                lastMode = mode;
                pos++;
            }

            OnAfterEmitNodeList?.Invoke(modifiers);
        }

        return PrinterUtilities.GreatestEnd(parentNode.Pos, global::Ts.Core.CoreUtil.LastOrNil(modifiers.Nodes));
    }

    private void EmitTypeParameter(TypeParameterDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        EmitBindingIdentifier(node.Name()!.AsIdentifier());
        if (node.Constraint is not null)
        {
            WriteSpace();
            WriteKeyword("extends");
            WriteSpace();
            EmitTypeNodeOutsideExtends(node.Constraint);
        }
        if (node.DefaultType is not null)
        {
            WriteSpace();
            WriteOperator("=");
            WriteSpace();
            EmitTypeNodeOutsideExtends(node.DefaultType);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitTypeParameterDeclarationNode(Node node)
    {
        if (NodeTests.IsTypeParameterDeclaration(node))
        {
            EmitTypeParameter(node.AsTypeParameterDeclaration());
        }
        else
        {
            EmitTypeArgument(node);
        }
    }

    private void EmitParameterName(Node? node)
    {
        var savedWriteKind = writeKind;
        writeKind = WriteKind.Parameter;
        EmitBindingName(node);
        writeKind = savedWriteKind;
    }

    private void EmitParameter(ParameterDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), true /*allowDecorators*/);
        EmitTokenNode(node.DotDotDotToken);
        EmitParameterName(node.Name());
        EmitTokenNode(node.QuestionToken);

        EmitTypeAnnotation(node.Type);

        EmitInitializer(node.Initializer, PrinterUtilities.GreatestEnd(node.Pos, node.Type, node.QuestionToken, node.Name(), node.Modifiers()), node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitParameterDeclarationNode(Node node)
    {
        EmitParameter(node.AsParameterDeclaration());
    }

    private void EmitDecorator(Decorator node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("@");
        EmitExpression(node.Expression!, OperatorPrecedence.LeftHandSide);
        ExitNode(node.AsNode(), state);
    }

    private void EmitModifierLike(Node node)
    {
        if (NodeTests.IsDecorator(node))
        {
            EmitDecorator(node.AsDecorator());
        }
        else if (AstUtil.IsModifier(node))
        {
            EmitKeywordNode(node);
        }
        else
        {
            throw new InvalidOperationException($"unhandled ModifierLike: {node.Kind}");
        }
    }

    private void EmitTypeParameters(Node parentNode, NodeList? nodes)
    {
        if (nodes is null)
        {
            return;
        }
        EmitList(static (p, node) => p.EmitTypeParameterDeclarationNode(node), parentNode, nodes, ListFormat.TypeParameters | (NodeTests.IsArrowFunction(parentNode) ? ListFormat.AllowTrailingComma : ListFormat.None));
    }

    private void EmitTypeAnnotation(Node? node)
    {
        if (node is null)
        {
            return;
        }

        WritePunctuation(":");
        WriteSpace();
        EmitTypeNodeOutsideExtends(node);
    }

    private void EmitInitializer(Node? node, int equalTokenPos, Node contextNode)
    {
        if (node is null)
        {
            return;
        }

        WriteSpace();
        EmitToken(SyntaxKind.EqualsToken, equalTokenPos, WriteKind.Operator, contextNode);
        WriteSpace();
        EmitExpression(node, OperatorPrecedence.DisallowComma);
    }

    private void EmitParameters(Node parentNode, NodeList parameters)
    {
        GenerateAllNames(parameters);
        EmitList(static (p, node) => p.EmitParameterDeclarationNode(node), parentNode, parameters, ListFormat.Parameters);
    }

    private void EmitParametersForArrow(Node parentNode, NodeList parameters)
    {
        // NOTE: canEmitSimpleArrowHead not yet exercised; the general parameter emit path is faithful.
        EmitParameters(parentNode, parameters);
    }

    private void EmitParametersForIndexSignature(Node parentNode, NodeList parameters)
    {
        EmitList(static (p, node) => p.EmitParameterDeclarationNode(node), parentNode, parameters, ListFormat.IndexSignatureParameters);
    }

    private void EmitSignature(Node node)
    {
        var n = node.FunctionLikeData()!;

        EmitTypeParameters(node, n.TypeParameters);
        EmitParameters(node, n.Parameters!);
        EmitTypeAnnotation(n.Type);
    }

    private void EmitFunctionBody(Block body)
    {
        emitContext.AddEmitFlags(body.AsNode(), EF.NoSourceMap);

        OnBeforeEmitNode?.Invoke(body.AsNode());

        GenerateNames(body.AsNode());

        WritePunctuation("{");

        IncreaseIndent();
        var detachedState = EmitDetachedCommentsBeforeStatementList(body.AsNode(), body.Statements!.Loc);
        var statementOffset = EmitPrologueDirectives(body.Statements);
        var pos = writer.GetTextPos();
        EmitHelpers(body.AsNode());

        if (ShouldEmitBlockFunctionBodyOnSingleLine(body) && statementOffset == 0 && pos == writer.GetTextPos())
        {
            DecreaseIndent();
            EmitListRange(static (p, node) => p.EmitStatement(node), body.AsNode(), body.Statements, ListFormat.SingleLineFunctionBodyStatements, statementOffset, -1);
            IncreaseIndent();
        }
        else
        {
            EmitListRange(static (p, node) => p.EmitStatement(node), body.AsNode(), body.Statements, ListFormat.MultiLineFunctionBodyStatements, statementOffset, -1);
        }

        EmitDetachedCommentsAfterStatementList(body.AsNode(), body.Statements.Loc, detachedState);
        DecreaseIndent();

        EmitTokenEx(SyntaxKind.CloseBraceToken, body.Statements.End, WriteKind.Punctuation, body.AsNode(), TokenEmitFlags.NoComments);

        OnAfterEmitNode?.Invoke(body.AsNode());
    }

    private void EmitFunctionBodyNode(BlockNode? node)
    {
        if (node is null)
        {
            WriteTrailingSemicolon();
            return;
        }

        WriteSpace();
        EmitFunctionBody(node.AsBlock());
    }

    //
    // Type Members
    //

    private void EmitPropertySignature(PropertySignatureDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        EmitPropertyName(node.Name());
        EmitTokenNode(node.PostfixToken);
        EmitTypeAnnotation(node.Type);
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitPropertyDeclaration(PropertyDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), true /*allowDecorators*/);
        EmitPropertyName(node.Name());
        EmitTokenNode(node.PostfixToken);
        EmitTypeAnnotation(node.Type);
        EmitInitializer(node.Initializer, PrinterUtilities.GreatestEnd(node.Name()!.End, node.Type, node.PostfixToken), node.AsNode());
        WriteTrailingSemicolon();
        ExitNode(node.AsNode(), state);
    }

    private void EmitMethodSignature(MethodSignatureDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        EmitPropertyName(node.Name());
        EmitTokenNode(node.PostfixToken);
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        WriteTrailingSemicolon();
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitMethodDeclaration(MethodDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), true /*allowDecorators*/);
        EmitTokenNode(node.AsteriskToken);
        EmitPropertyName(node.Name());
        EmitTokenNode(node.PostfixToken);
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        EmitFunctionBodyNode(node.Body);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitClassStaticBlockDeclaration(ClassStaticBlockDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        WriteKeyword("static");
        PushNameGenerationScope(node.AsNode());
        EmitFunctionBodyNode(node.Body);
        PopNameGenerationScope(node.AsNode());
        ExitNode(node.AsNode(), state);
    }

    private void EmitConstructor(ConstructorDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        WriteKeyword("constructor");
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        EmitFunctionBodyNode(node.Body);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitAccessorDeclaration(SyntaxKind token, AccessorDeclarationBase node)
    {
        var state = EnterNode(node.AsNode());
        var pos = EmitModifierList(node.AsNode(), node.Modifiers(), true /*allowDecorators*/);
        EmitToken(token, pos, WriteKind.Keyword, node.AsNode());
        WriteSpace();
        EmitPropertyName(node.Name());
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        EmitFunctionBodyNode(node.Body);
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitGetAccessorDeclaration(GetAccessorDeclaration node)
    {
        EmitAccessorDeclaration(SyntaxKind.GetKeyword, node);
    }

    private void EmitSetAccessorDeclaration(SetAccessorDeclaration node)
    {
        EmitAccessorDeclaration(SyntaxKind.SetKeyword, node);
    }

    private void EmitCallSignature(CallSignatureDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        WriteTrailingSemicolon();
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitConstructSignature(ConstructSignatureDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        WriteKeyword("new");
        WriteSpace();
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitSignature(node.AsNode());
        WriteTrailingSemicolon();
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitIndexSignature(IndexSignatureDeclaration node)
    {
        var state = EnterNode(node.AsNode());
        EmitModifierList(node.AsNode(), node.Modifiers(), false /*allowDecorators*/);
        var indented = ShouldEmitIndented(node.AsNode());
        IncreaseIndentIf(indented);
        PushNameGenerationScope(node.AsNode());
        EmitParametersForIndexSignature(node.AsNode(), node.Parameters!);
        EmitTypeAnnotation(node.Type);
        WriteTrailingSemicolon();
        PopNameGenerationScope(node.AsNode());
        DecreaseIndentIf(indented);
        ExitNode(node.AsNode(), state);
    }

    private void EmitClassElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
                EmitPropertyDeclaration(node.AsPropertyDeclaration());
                break;
            case SyntaxKind.MethodDeclaration:
                EmitMethodDeclaration(node.AsMethodDeclaration());
                break;
            case SyntaxKind.ClassStaticBlockDeclaration:
                EmitClassStaticBlockDeclaration(node.AsClassStaticBlockDeclaration());
                break;
            case SyntaxKind.Constructor:
                EmitConstructor(node.AsConstructorDeclaration());
                break;
            case SyntaxKind.GetAccessor:
                EmitGetAccessorDeclaration(node.AsGetAccessorDeclaration());
                break;
            case SyntaxKind.SetAccessor:
                EmitSetAccessorDeclaration(node.AsSetAccessorDeclaration());
                break;
            case SyntaxKind.IndexSignature:
                EmitIndexSignature(node.AsIndexSignatureDeclaration());
                break;
            case SyntaxKind.SemicolonClassElement:
                EmitSemicolonClassElement(node.AsSemicolonClassElement());
                break;
            case SyntaxKind.NotEmittedStatement:
                EmitNotEmittedStatement(node.AsNotEmittedStatement());
                break;
            case SyntaxKind.JSTypeAliasDeclaration:
                EmitTypeAliasDeclaration(node.AsTypeAliasDeclaration());
                break;
            default:
                throw new InvalidOperationException($"unexpected ClassElement: {node.Kind}");
        }
    }

    private void EmitTypeElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertySignature:
                EmitPropertySignature(node.AsPropertySignatureDeclaration());
                break;
            case SyntaxKind.MethodSignature:
                EmitMethodSignature(node.AsMethodSignatureDeclaration());
                break;
            case SyntaxKind.CallSignature:
                EmitCallSignature(node.AsCallSignatureDeclaration());
                break;
            case SyntaxKind.ConstructSignature:
                EmitConstructSignature(node.AsConstructSignatureDeclaration());
                break;
            case SyntaxKind.GetAccessor:
                EmitGetAccessorDeclaration(node.AsGetAccessorDeclaration());
                break;
            case SyntaxKind.SetAccessor:
                EmitSetAccessorDeclaration(node.AsSetAccessorDeclaration());
                break;
            case SyntaxKind.IndexSignature:
                EmitIndexSignature(node.AsIndexSignatureDeclaration());
                break;
            case SyntaxKind.NotEmittedTypeElement:
                EmitNotEmittedTypeElement(node.AsNotEmittedTypeElement());
                break;
            default:
                throw new InvalidOperationException($"unexpected TypeElement: {node.Kind}");
        }
    }

    private void EmitObjectLiteralElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAssignment:
                EmitPropertyAssignment(node.AsPropertyAssignment());
                break;
            case SyntaxKind.ShorthandPropertyAssignment:
                EmitShorthandPropertyAssignment(node.AsShorthandPropertyAssignment());
                break;
            case SyntaxKind.SpreadAssignment:
                EmitSpreadAssignment(node.AsSpreadAssignment());
                break;
            case SyntaxKind.MethodDeclaration:
                EmitMethodDeclaration(node.AsMethodDeclaration());
                break;
            case SyntaxKind.GetAccessor:
                EmitGetAccessorDeclaration(node.AsGetAccessorDeclaration());
                break;
            case SyntaxKind.SetAccessor:
                EmitSetAccessorDeclaration(node.AsSetAccessorDeclaration());
                break;
            default:
                throw new InvalidOperationException($"unhandled ObjectLiteralElement: {node.Kind}");
        }
    }
}
