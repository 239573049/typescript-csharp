// Ported from tsgo internal/api/encoder/decoder_generated.go (1:1, hand-translated).
// Go's file is produced by _scripts/generate-encoder.ts; the C# port mirrors it by hand
// until the tools/generator bootstrap lands (plan/04 §8 names Decoder.g.cs as the
// eventual generated target). Methods are partial-class parts of AstDecoder (Decoder.cs),
// standing in for Go's astDecoder receiver methods.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Api.Encoder;

// Go: func (d *astDecoder) createStringNode / createExtendedNode / createChildrenNode
// (decoder_generated.go:11-1139).
public sealed partial class AstDecoder
{
    // (decoder_generated.go:11-34)
    private Node CreateStringNode(SyntaxKind kind, uint data, uint commonData)
    {
        uint strIdx = data & NodeDataMasks.NodeDataStringIndexMask;
        string text = GetString(strIdx);

        switch (kind)
        {
            case SyntaxKind.Identifier:
                return factory.NewIdentifier(text);
            case SyntaxKind.PrivateIdentifier:
                return factory.NewPrivateIdentifier(text);
            case SyntaxKind.JsxText:
                bool containsOnlyTriviaWhiteSpaces = (commonData & 1) != 0;
                return factory.NewJsxText(text, containsOnlyTriviaWhiteSpaces);
            case SyntaxKind.JSDocText:
                return factory.NewJSDocText([text]);
            case SyntaxKind.JSDocLink:
                return factory.NewJSDocLink(null, [text]);
            case SyntaxKind.JSDocLinkPlain:
                return factory.NewJSDocLinkPlain(null, [text]);
            case SyntaxKind.JSDocLinkCode:
                return factory.NewJSDocLinkCode(null, [text]);
            default:
                throw new InvalidOperationException($"unknown string node kind {kind}");
        }
    }

    // (decoder_generated.go:36-59)
    private Node CreateExtendedNode(SyntaxKind kind, uint data, List<int> childIndices, uint commonData)
    {
        switch (kind)
        {
            case SyntaxKind.StringLiteral:
                return DecodeExtendedData_StringLiteral(data, childIndices, commonData);
            case SyntaxKind.NumericLiteral:
                return DecodeExtendedData_NumericLiteral(data, childIndices, commonData);
            case SyntaxKind.BigIntLiteral:
                return DecodeExtendedData_BigIntLiteral(data, childIndices, commonData);
            case SyntaxKind.RegularExpressionLiteral:
                return DecodeExtendedData_RegularExpressionLiteral(data, childIndices, commonData);
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return DecodeExtendedData_NoSubstitutionTemplateLiteral(data, childIndices, commonData);
            case SyntaxKind.TemplateHead:
                return DecodeExtendedData_TemplateHead(data, childIndices, commonData);
            case SyntaxKind.TemplateMiddle:
                return DecodeExtendedData_TemplateMiddle(data, childIndices, commonData);
            case SyntaxKind.TemplateTail:
                return DecodeExtendedData_TemplateTail(data, childIndices, commonData);
            case SyntaxKind.SourceFile:
                return DecodeExtendedData_SourceFile(data, childIndices, commonData);
            default:
                throw new InvalidOperationException($"unknown extended data node kind {kind}");
        }
    }

    // (decoder_generated.go:61-1139)
    private Node CreateChildrenNode(SyntaxKind kind, uint data, List<int> childIndices, uint commonData)
    {
        uint mask = data & NodeDataMasks.NodeDataChildMask;

        switch (kind)
        {
            case SyntaxKind.Unknown:
            case SyntaxKind.EndOfFile:
            case SyntaxKind.SingleLineCommentTrivia:
            case SyntaxKind.MultiLineCommentTrivia:
            case SyntaxKind.NewLineTrivia:
            case SyntaxKind.WhitespaceTrivia:
            case SyntaxKind.ConflictMarkerTrivia:
            case SyntaxKind.NonTextFileMarkerTrivia:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.JsxText:
            case SyntaxKind.JsxTextAllWhiteSpaces:
            case SyntaxKind.RegularExpressionLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
            case SyntaxKind.TemplateMiddle:
            case SyntaxKind.TemplateTail:
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.CloseBraceToken:
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.CloseParenToken:
            case SyntaxKind.OpenBracketToken:
            case SyntaxKind.CloseBracketToken:
            case SyntaxKind.DotToken:
            case SyntaxKind.DotDotDotToken:
            case SyntaxKind.SemicolonToken:
            case SyntaxKind.CommaToken:
            case SyntaxKind.QuestionDotToken:
            case SyntaxKind.LessThanToken:
            case SyntaxKind.LessThanSlashToken:
            case SyntaxKind.GreaterThanToken:
            case SyntaxKind.LessThanEqualsToken:
            case SyntaxKind.GreaterThanEqualsToken:
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
            case SyntaxKind.EqualsGreaterThanToken:
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.AsteriskToken:
            case SyntaxKind.AsteriskAsteriskToken:
            case SyntaxKind.SlashToken:
            case SyntaxKind.PercentToken:
            case SyntaxKind.PlusPlusToken:
            case SyntaxKind.MinusMinusToken:
            case SyntaxKind.LessThanLessThanToken:
            case SyntaxKind.GreaterThanGreaterThanToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
            case SyntaxKind.AmpersandToken:
            case SyntaxKind.BarToken:
            case SyntaxKind.CaretToken:
            case SyntaxKind.ExclamationToken:
            case SyntaxKind.TildeToken:
            case SyntaxKind.AmpersandAmpersandToken:
            case SyntaxKind.BarBarToken:
            case SyntaxKind.QuestionToken:
            case SyntaxKind.ColonToken:
            case SyntaxKind.AtToken:
            case SyntaxKind.QuestionQuestionToken:
            case SyntaxKind.BacktickToken:
            case SyntaxKind.HashToken:
            case SyntaxKind.EqualsToken:
            case SyntaxKind.PlusEqualsToken:
            case SyntaxKind.MinusEqualsToken:
            case SyntaxKind.AsteriskEqualsToken:
            case SyntaxKind.AsteriskAsteriskEqualsToken:
            case SyntaxKind.SlashEqualsToken:
            case SyntaxKind.PercentEqualsToken:
            case SyntaxKind.LessThanLessThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
            case SyntaxKind.AmpersandEqualsToken:
            case SyntaxKind.BarEqualsToken:
            case SyntaxKind.BarBarEqualsToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
            case SyntaxKind.QuestionQuestionEqualsToken:
            case SyntaxKind.CaretEqualsToken:
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.JSDocCommentTextToken:
            case SyntaxKind.BreakKeyword:
            case SyntaxKind.CaseKeyword:
            case SyntaxKind.CatchKeyword:
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.ContinueKeyword:
            case SyntaxKind.DebuggerKeyword:
            case SyntaxKind.DefaultKeyword:
            case SyntaxKind.DeleteKeyword:
            case SyntaxKind.DoKeyword:
            case SyntaxKind.ElseKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.ExportKeyword:
            case SyntaxKind.ExtendsKeyword:
            case SyntaxKind.FinallyKeyword:
            case SyntaxKind.ForKeyword:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.IfKeyword:
            case SyntaxKind.InKeyword:
            case SyntaxKind.InstanceOfKeyword:
            case SyntaxKind.NewKeyword:
            case SyntaxKind.ReturnKeyword:
            case SyntaxKind.SwitchKeyword:
            case SyntaxKind.ThrowKeyword:
            case SyntaxKind.TryKeyword:
            case SyntaxKind.TypeOfKeyword:
            case SyntaxKind.VarKeyword:
            case SyntaxKind.WhileKeyword:
            case SyntaxKind.WithKeyword:
            case SyntaxKind.ImplementsKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.LetKeyword:
            case SyntaxKind.PackageKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.StaticKeyword:
            case SyntaxKind.YieldKeyword:
            case SyntaxKind.AbstractKeyword:
            case SyntaxKind.AccessorKeyword:
            case SyntaxKind.AsKeyword:
            case SyntaxKind.AssertsKeyword:
            case SyntaxKind.AssertKeyword:
            case SyntaxKind.AsyncKeyword:
            case SyntaxKind.AwaitKeyword:
            case SyntaxKind.ConstructorKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.GetKeyword:
            case SyntaxKind.ImmediateKeyword:
            case SyntaxKind.InferKeyword:
            case SyntaxKind.IsKeyword:
            case SyntaxKind.KeyOfKeyword:
            case SyntaxKind.ModuleKeyword:
            case SyntaxKind.NamespaceKeyword:
            case SyntaxKind.OutKeyword:
            case SyntaxKind.ReadonlyKeyword:
            case SyntaxKind.RequireKeyword:
            case SyntaxKind.SatisfiesKeyword:
            case SyntaxKind.SetKeyword:
            case SyntaxKind.TypeKeyword:
            case SyntaxKind.UniqueKeyword:
            case SyntaxKind.UsingKeyword:
            case SyntaxKind.FromKeyword:
            case SyntaxKind.GlobalKeyword:
            case SyntaxKind.OverrideKeyword:
            case SyntaxKind.OfKeyword:
            case SyntaxKind.DeferKeyword:
                return factory.NewToken(kind);
            case SyntaxKind.QualifiedName:
                {
                    var it = new ChildIterator(childIndices);
                    Node? left = NodeAt(it.NextIf(mask, 0));
                    Node? right = NodeAt(it.NextIf(mask, 1));
                    return factory.NewQualifiedName(left, right);
                }
            case SyntaxKind.ComputedPropertyName:
                return factory.NewComputedPropertyName(SingleChild(childIndices));
            case SyntaxKind.Decorator:
                return factory.NewDecorator(SingleChild(childIndices));
            case SyntaxKind.EmptyStatement:
                return factory.NewEmptyStatement();
            case SyntaxKind.IfStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? thenStatement = NodeAt(it.NextIf(mask, 1));
                    Node? elseStatement = NodeAt(it.NextIf(mask, 2));
                    return factory.NewIfStatement(expression, thenStatement, elseStatement);
                }
            case SyntaxKind.DoStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? statement = NodeAt(it.NextIf(mask, 0));
                    Node? expression = NodeAt(it.NextIf(mask, 1));
                    return factory.NewDoStatement(statement, expression);
                }
            case SyntaxKind.WhileStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? statement = NodeAt(it.NextIf(mask, 1));
                    return factory.NewWhileStatement(expression, statement);
                }
            case SyntaxKind.ForStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? initializer = NodeAt(it.NextIf(mask, 0));
                    Node? condition = NodeAt(it.NextIf(mask, 1));
                    Node? incrementor = NodeAt(it.NextIf(mask, 2));
                    Node? statement = NodeAt(it.NextIf(mask, 3));
                    return factory.NewForStatement(initializer, condition, incrementor, statement);
                }
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? awaitModifier = NodeAt(it.NextIf(mask, 0));
                    Node? initializer = NodeAt(it.NextIf(mask, 1));
                    Node? expression = NodeAt(it.NextIf(mask, 2));
                    Node? statement = NodeAt(it.NextIf(mask, 3));
                    return factory.NewForInOrOfStatement(kind, awaitModifier, initializer, expression, statement);
                }
            case SyntaxKind.BreakStatement:
                return factory.NewBreakStatement(SingleChild(childIndices));
            case SyntaxKind.ContinueStatement:
                return factory.NewContinueStatement(SingleChild(childIndices));
            case SyntaxKind.ReturnStatement:
                return factory.NewReturnStatement(SingleChild(childIndices));
            case SyntaxKind.WithStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? statement = NodeAt(it.NextIf(mask, 1));
                    return factory.NewWithStatement(expression, statement);
                }
            case SyntaxKind.SwitchStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? caseBlock = NodeAt(it.NextIf(mask, 1));
                    return factory.NewSwitchStatement(expression, caseBlock);
                }
            case SyntaxKind.CaseBlock:
                return factory.NewCaseBlock(SingleNodeListChild(childIndices));
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    NodeList? statements = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewCaseOrDefaultClause(kind, expression, statements);
                }
            case SyntaxKind.ThrowStatement:
                return factory.NewThrowStatement(SingleChild(childIndices));
            case SyntaxKind.TryStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tryBlock = NodeAt(it.NextIf(mask, 0));
                    Node? catchClause = NodeAt(it.NextIf(mask, 1));
                    Node? finallyBlock = NodeAt(it.NextIf(mask, 2));
                    return factory.NewTryStatement(tryBlock, catchClause, finallyBlock);
                }
            case SyntaxKind.CatchClause:
                {
                    var it = new ChildIterator(childIndices);
                    Node? variableDeclaration = NodeAt(it.NextIf(mask, 0));
                    Node? block = NodeAt(it.NextIf(mask, 1));
                    return factory.NewCatchClause(variableDeclaration, block);
                }
            case SyntaxKind.DebuggerStatement:
                return factory.NewDebuggerStatement();
            case SyntaxKind.LabeledStatement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? label = NodeAt(it.NextIf(mask, 0));
                    Node? statement = NodeAt(it.NextIf(mask, 1));
                    return factory.NewLabeledStatement(label, statement);
                }
            case SyntaxKind.ExpressionStatement:
                return factory.NewExpressionStatement(SingleChild(childIndices));
            case SyntaxKind.Block:
                {
                    bool multiLine = (commonData & 1) != 0;
                    NodeList? list = childIndices.Count > 0 ? NodeListAt(childIndices[0]) : null;
                    return factory.NewBlock(list, multiLine);
                }
            case SyntaxKind.VariableStatement:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? declarationList = NodeAt(it.NextIf(mask, 1));
                    return factory.NewVariableStatement(modifiers, declarationList);
                }
            case SyntaxKind.VariableDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    Node? name = NodeAt(it.NextIf(mask, 0));
                    Node? exclamationToken = NodeAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    Node? initializer = NodeAt(it.NextIf(mask, 3));
                    return factory.NewVariableDeclaration(name, exclamationToken, typeNode, initializer);
                }
            case SyntaxKind.VariableDeclarationList:
                return factory.NewVariableDeclarationList(SingleNodeListChild(childIndices), (NodeFlags)0);
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                return factory.NewBindingPattern(kind, SingleNodeListChild(childIndices));
            case SyntaxKind.Parameter:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? dotDotDotToken = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    Node? questionToken = NodeAt(it.NextIf(mask, 3));
                    Node? typeNode = NodeAt(it.NextIf(mask, 4));
                    Node? initializer = NodeAt(it.NextIf(mask, 5));
                    return factory.NewParameterDeclaration(modifiers, dotDotDotToken, name, questionToken, typeNode, initializer);
                }
            case SyntaxKind.BindingElement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? dotDotDotToken = NodeAt(it.NextIf(mask, 0));
                    Node? propertyName = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    Node? initializer = NodeAt(it.NextIf(mask, 3));
                    return factory.NewBindingElement(dotDotDotToken, propertyName, name, initializer);
                }
            case SyntaxKind.MissingDeclaration:
                {
                    ModifierList? mods = childIndices.Count > 0 ? ModifierListAt(childIndices[0]) : null;
                    return factory.NewMissingDeclaration(mods);
                }
            case SyntaxKind.FunctionDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? asteriskToken = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 3));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 4));
                    Node? typeNode = NodeAt(it.NextIf(mask, 5));
                    Node? body = NodeAt(it.NextIf(mask, 6));
                    return factory.NewFunctionDeclaration(modifiers, asteriskToken, name, typeParameters, parameters, typeNode, null, body);
                }
            case SyntaxKind.ClassDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    NodeList? heritageClauses = NodeListAt(it.NextIf(mask, 3));
                    NodeList? members = NodeListAt(it.NextIf(mask, 4));
                    return factory.NewClassDeclaration(modifiers, name, typeParameters, heritageClauses, members);
                }
            case SyntaxKind.ClassExpression:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    NodeList? heritageClauses = NodeListAt(it.NextIf(mask, 3));
                    NodeList? members = NodeListAt(it.NextIf(mask, 4));
                    return factory.NewClassExpression(modifiers, name, typeParameters, heritageClauses, members);
                }
            case SyntaxKind.HeritageClause:
                {
                    SyntaxKind token = SyntaxKind.ExtendsKeyword;
                    if ((commonData & 1) != 0)
                    {
                        token = SyntaxKind.ImplementsKeyword;
                    }
                    return factory.NewHeritageClause(token, SingleNodeListChild(childIndices));
                }
            case SyntaxKind.InterfaceDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    NodeList? heritageClauses = NodeListAt(it.NextIf(mask, 3));
                    NodeList? members = NodeListAt(it.NextIf(mask, 4));
                    return factory.NewInterfaceDeclaration(modifiers, name, typeParameters, heritageClauses, members);
                }
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    if (kind == SyntaxKind.JSTypeAliasDeclaration)
                    {
                        return factory.NewJSTypeAliasDeclaration(modifiers, name, typeParameters, typeNode);
                    }
                    return factory.NewTypeAliasDeclaration(modifiers, name, typeParameters, typeNode);
                }
            case SyntaxKind.EnumMember:
                {
                    var it = new ChildIterator(childIndices);
                    Node? name = NodeAt(it.NextIf(mask, 0));
                    Node? initializer = NodeAt(it.NextIf(mask, 1));
                    return factory.NewEnumMember(name, initializer);
                }
            case SyntaxKind.EnumDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? members = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewEnumDeclaration(modifiers, name, members);
                }
            case SyntaxKind.ModuleBlock:
                return factory.NewModuleBlock(SingleNodeListChild(childIndices));
            case SyntaxKind.NotEmittedStatement:
                return factory.NewNotEmittedStatement();
            case SyntaxKind.NotEmittedTypeElement:
                return factory.NewNotEmittedTypeElement();
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? importClause = NodeAt(it.NextIf(mask, 1));
                    Node? moduleSpecifier = NodeAt(it.NextIf(mask, 2));
                    Node? attributes = NodeAt(it.NextIf(mask, 3));
                    if (kind == SyntaxKind.JSImportDeclaration)
                    {
                        return factory.NewJSImportDeclaration(modifiers, importClause, moduleSpecifier, attributes);
                    }
                    return factory.NewImportDeclaration(modifiers, importClause, moduleSpecifier, attributes);
                }
            case SyntaxKind.ExternalModuleReference:
                return factory.NewExternalModuleReference(SingleChild(childIndices));
            case SyntaxKind.NamespaceImport:
                return factory.NewNamespaceImport(SingleChild(childIndices));
            case SyntaxKind.NamedImports:
                return factory.NewNamedImports(SingleNodeListChild(childIndices));
            case SyntaxKind.ExportAssignment:
                {
                    bool isExportEquals = (commonData & 1) != 0;
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? typeNode = NodeAt(it.NextIf(mask, 1));
                    Node? expression = NodeAt(it.NextIf(mask, 2));
                    return factory.NewExportAssignment(modifiers, isExportEquals, typeNode, expression);
                }
            case SyntaxKind.NamespaceExportDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    return factory.NewNamespaceExportDeclaration(modifiers, name);
                }
            case SyntaxKind.NamespaceExport:
                return factory.NewNamespaceExport(SingleChild(childIndices));
            case SyntaxKind.NamedExports:
                return factory.NewNamedExports(SingleNodeListChild(childIndices));
            case SyntaxKind.ExportSpecifier:
                {
                    bool isTypeOnly = (commonData & 1) != 0;
                    var it = new ChildIterator(childIndices);
                    Node? propertyName = NodeAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    return factory.NewExportSpecifier(isTypeOnly, propertyName, name);
                }
            case SyntaxKind.CallSignature:
                {
                    var it = new ChildIterator(childIndices);
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 0));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    return factory.NewCallSignatureDeclaration(typeParameters, parameters, typeNode);
                }
            case SyntaxKind.ConstructSignature:
                {
                    var it = new ChildIterator(childIndices);
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 0));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    return factory.NewConstructSignatureDeclaration(typeParameters, parameters, typeNode);
                }
            case SyntaxKind.Constructor:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 1));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    Node? body = NodeAt(it.NextIf(mask, 4));
                    return factory.NewConstructorDeclaration(modifiers, typeParameters, parameters, typeNode, null, body);
                }
            case SyntaxKind.GetAccessor:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 3));
                    Node? typeNode = NodeAt(it.NextIf(mask, 4));
                    Node? body = NodeAt(it.NextIf(mask, 5));
                    return factory.NewGetAccessorDeclaration(modifiers, name, typeParameters, parameters, typeNode, null, body);
                }
            case SyntaxKind.SetAccessor:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 3));
                    Node? typeNode = NodeAt(it.NextIf(mask, 4));
                    Node? body = NodeAt(it.NextIf(mask, 5));
                    return factory.NewSetAccessorDeclaration(modifiers, name, typeParameters, parameters, typeNode, null, body);
                }
            case SyntaxKind.IndexSignature:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    return factory.NewIndexSignatureDeclaration(modifiers, parameters, typeNode);
                }
            case SyntaxKind.MethodSignature:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? postfixToken = NodeAt(it.NextIf(mask, 2));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 3));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 4));
                    Node? typeNode = NodeAt(it.NextIf(mask, 5));
                    return factory.NewMethodSignatureDeclaration(modifiers, name, postfixToken, typeParameters, parameters, typeNode);
                }
            case SyntaxKind.MethodDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? asteriskToken = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    Node? postfixToken = NodeAt(it.NextIf(mask, 3));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 4));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 5));
                    Node? typeNode = NodeAt(it.NextIf(mask, 6));
                    Node? body = NodeAt(it.NextIf(mask, 7));
                    return factory.NewMethodDeclaration(modifiers, asteriskToken, name, postfixToken, typeParameters, parameters, typeNode, null, body);
                }
            case SyntaxKind.PropertySignature:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? postfixToken = NodeAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    Node? initializer = NodeAt(it.NextIf(mask, 4));
                    return factory.NewPropertySignatureDeclaration(modifiers, name, postfixToken, typeNode, initializer);
                }
            case SyntaxKind.PropertyDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? postfixToken = NodeAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    Node? initializer = NodeAt(it.NextIf(mask, 4));
                    return factory.NewPropertyDeclaration(modifiers, name, postfixToken, typeNode, initializer);
                }
            case SyntaxKind.SemicolonClassElement:
                return factory.NewSemicolonClassElement();
            case SyntaxKind.ClassStaticBlockDeclaration:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? body = NodeAt(it.NextIf(mask, 1));
                    return factory.NewClassStaticBlockDeclaration(modifiers, body);
                }
            case SyntaxKind.OmittedExpression:
                return factory.NewOmittedExpression();
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.ImportKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.TrueKeyword:
                return factory.NewKeywordExpression(kind);
            case SyntaxKind.BinaryExpression:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? left = NodeAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    Node? operatorToken = NodeAt(it.NextIf(mask, 3));
                    Node? right = NodeAt(it.NextIf(mask, 4));
                    return factory.NewBinaryExpression(modifiers, left, typeNode, operatorToken, right);
                }
            case SyntaxKind.PrefixUnaryExpression:
                {
                    SyntaxKind @operator;
                    switch (commonData & 7)
                    {
                        case 0:
                            @operator = SyntaxKind.PlusToken;
                            break;
                        case 1:
                            @operator = SyntaxKind.MinusToken;
                            break;
                        case 2:
                            @operator = SyntaxKind.TildeToken;
                            break;
                        case 3:
                            @operator = SyntaxKind.ExclamationToken;
                            break;
                        case 4:
                            @operator = SyntaxKind.PlusPlusToken;
                            break;
                        case 5:
                            @operator = SyntaxKind.MinusMinusToken;
                            break;
                        default:
                            @operator = (SyntaxKind)0;
                            break;
                    }
                    return factory.NewPrefixUnaryExpression(@operator, SingleChild(childIndices));
                }
            case SyntaxKind.PostfixUnaryExpression:
                {
                    SyntaxKind @operator = SyntaxKind.PlusPlusToken;
                    if ((commonData & 1) != 0)
                    {
                        @operator = SyntaxKind.MinusMinusToken;
                    }
                    return factory.NewPostfixUnaryExpression(SingleChild(childIndices), @operator);
                }
            case SyntaxKind.YieldExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? asteriskToken = NodeAt(it.NextIf(mask, 0));
                    Node? expression = NodeAt(it.NextIf(mask, 1));
                    return factory.NewYieldExpression(asteriskToken, expression);
                }
            case SyntaxKind.ArrowFunction:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 1));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    Node? equalsGreaterThanToken = NodeAt(it.NextIf(mask, 4));
                    Node? body = NodeAt(it.NextIf(mask, 5));
                    return factory.NewArrowFunction(modifiers, typeParameters, parameters, typeNode, null, equalsGreaterThanToken, body);
                }
            case SyntaxKind.FunctionExpression:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? asteriskToken = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 3));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 4));
                    Node? typeNode = NodeAt(it.NextIf(mask, 5));
                    Node? body = NodeAt(it.NextIf(mask, 6));
                    return factory.NewFunctionExpression(modifiers, asteriskToken, name, typeParameters, parameters, typeNode, null, body);
                }
            case SyntaxKind.AsExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? typeNode = NodeAt(it.NextIf(mask, 1));
                    return factory.NewAsExpression(expression, typeNode);
                }
            case SyntaxKind.SatisfiesExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? typeNode = NodeAt(it.NextIf(mask, 1));
                    return factory.NewSatisfiesExpression(expression, typeNode);
                }
            case SyntaxKind.ConditionalExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? condition = NodeAt(it.NextIf(mask, 0));
                    Node? questionToken = NodeAt(it.NextIf(mask, 1));
                    Node? whenTrue = NodeAt(it.NextIf(mask, 2));
                    Node? colonToken = NodeAt(it.NextIf(mask, 3));
                    Node? whenFalse = NodeAt(it.NextIf(mask, 4));
                    return factory.NewConditionalExpression(condition, questionToken, whenTrue, colonToken, whenFalse);
                }
            case SyntaxKind.PropertyAccessExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? questionDotToken = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    return factory.NewPropertyAccessExpression(expression, questionDotToken, name, (NodeFlags)0);
                }
            case SyntaxKind.ElementAccessExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? questionDotToken = NodeAt(it.NextIf(mask, 1));
                    Node? argumentExpression = NodeAt(it.NextIf(mask, 2));
                    return factory.NewElementAccessExpression(expression, questionDotToken, argumentExpression, (NodeFlags)0);
                }
            case SyntaxKind.CallExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? questionDotToken = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 2));
                    NodeList? arguments = NodeListAt(it.NextIf(mask, 3));
                    return factory.NewCallExpression(expression, questionDotToken, typeArguments, arguments, (NodeFlags)0);
                }
            case SyntaxKind.NewExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 1));
                    NodeList? arguments = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewNewExpression(expression, typeArguments, arguments);
                }
            case SyntaxKind.MetaProperty:
                {
                    SyntaxKind keywordToken = SyntaxKind.ImportKeyword;
                    if ((commonData & 1) != 0)
                    {
                        keywordToken = SyntaxKind.NewKeyword;
                    }
                    return factory.NewMetaProperty(keywordToken, SingleChild(childIndices));
                }
            case SyntaxKind.NonNullExpression:
                return factory.NewNonNullExpression(SingleChild(childIndices), (NodeFlags)0);
            case SyntaxKind.SpreadElement:
                return factory.NewSpreadElement(SingleChild(childIndices));
            case SyntaxKind.TemplateExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? head = NodeAt(it.NextIf(mask, 0));
                    NodeList? templateSpans = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewTemplateExpression(head, templateSpans);
                }
            case SyntaxKind.TemplateSpan:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? literal = NodeAt(it.NextIf(mask, 1));
                    return factory.NewTemplateSpan(expression, literal);
                }
            case SyntaxKind.TaggedTemplateExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tag = NodeAt(it.NextIf(mask, 0));
                    Node? questionDotToken = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 2));
                    Node? template = NodeAt(it.NextIf(mask, 3));
                    return factory.NewTaggedTemplateExpression(tag, questionDotToken, typeArguments, template, (NodeFlags)0);
                }
            case SyntaxKind.ParenthesizedExpression:
                return factory.NewParenthesizedExpression(SingleChild(childIndices));
            case SyntaxKind.ArrayLiteralExpression:
                {
                    bool multiLine = (commonData & 1) != 0;
                    NodeList? list = childIndices.Count > 0 ? NodeListAt(childIndices[0]) : null;
                    return factory.NewArrayLiteralExpression(list, multiLine);
                }
            case SyntaxKind.ObjectLiteralExpression:
                {
                    bool multiLine = (commonData & 1) != 0;
                    NodeList? list = childIndices.Count > 0 ? NodeListAt(childIndices[0]) : null;
                    return factory.NewObjectLiteralExpression(list, multiLine);
                }
            case SyntaxKind.SpreadAssignment:
                return factory.NewSpreadAssignment(SingleChild(childIndices));
            case SyntaxKind.PropertyAssignment:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? postfixToken = NodeAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    Node? initializer = NodeAt(it.NextIf(mask, 4));
                    return factory.NewPropertyAssignment(modifiers, name, postfixToken, typeNode, initializer);
                }
            case SyntaxKind.ShorthandPropertyAssignment:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? postfixToken = NodeAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    Node? equalsToken = NodeAt(it.NextIf(mask, 4));
                    Node? objectAssignmentInitializer = NodeAt(it.NextIf(mask, 5));
                    return factory.NewShorthandPropertyAssignment(modifiers, name, postfixToken, typeNode, equalsToken, objectAssignmentInitializer);
                }
            case SyntaxKind.DeleteExpression:
                return factory.NewDeleteExpression(SingleChild(childIndices));
            case SyntaxKind.TypeOfExpression:
                return factory.NewTypeOfExpression(SingleChild(childIndices));
            case SyntaxKind.VoidExpression:
                return factory.NewVoidExpression(SingleChild(childIndices));
            case SyntaxKind.AwaitExpression:
                return factory.NewAwaitExpression(SingleChild(childIndices));
            case SyntaxKind.TypeAssertionExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? typeNode = NodeAt(it.NextIf(mask, 0));
                    Node? expression = NodeAt(it.NextIf(mask, 1));
                    return factory.NewTypeAssertion(typeNode, expression);
                }
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.IntrinsicKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.BigIntKeyword:
                return factory.NewKeywordTypeNode(kind);
            case SyntaxKind.UnionType:
                return factory.NewUnionTypeNode(SingleNodeListChild(childIndices));
            case SyntaxKind.IntersectionType:
                return factory.NewIntersectionTypeNode(SingleNodeListChild(childIndices));
            case SyntaxKind.ConditionalType:
                {
                    var it = new ChildIterator(childIndices);
                    Node? checkType = NodeAt(it.NextIf(mask, 0));
                    Node? extendsType = NodeAt(it.NextIf(mask, 1));
                    Node? trueType = NodeAt(it.NextIf(mask, 2));
                    Node? falseType = NodeAt(it.NextIf(mask, 3));
                    return factory.NewConditionalTypeNode(checkType, extendsType, trueType, falseType);
                }
            case SyntaxKind.TypeOperator:
                {
                    SyntaxKind @operator;
                    switch (commonData & 3)
                    {
                        case 0:
                            @operator = SyntaxKind.KeyOfKeyword;
                            break;
                        case 1:
                            @operator = SyntaxKind.ReadonlyKeyword;
                            break;
                        case 2:
                            @operator = SyntaxKind.UniqueKeyword;
                            break;
                        default:
                            @operator = (SyntaxKind)0;
                            break;
                    }
                    return factory.NewTypeOperatorNode(@operator, SingleChild(childIndices));
                }
            case SyntaxKind.InferType:
                return factory.NewInferTypeNode(SingleChild(childIndices));
            case SyntaxKind.ArrayType:
                return factory.NewArrayTypeNode(SingleChild(childIndices));
            case SyntaxKind.IndexedAccessType:
                {
                    var it = new ChildIterator(childIndices);
                    Node? objectType = NodeAt(it.NextIf(mask, 0));
                    Node? indexType = NodeAt(it.NextIf(mask, 1));
                    return factory.NewIndexedAccessTypeNode(objectType, indexType);
                }
            case SyntaxKind.TypeReference:
                {
                    var it = new ChildIterator(childIndices);
                    Node? typeName = NodeAt(it.NextIf(mask, 0));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewTypeReferenceNode(typeName, typeArguments);
                }
            case SyntaxKind.ExpressionWithTypeArguments:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewExpressionWithTypeArguments(expression, typeArguments);
                }
            case SyntaxKind.LiteralType:
                return factory.NewLiteralTypeNode(SingleChild(childIndices));
            case SyntaxKind.ThisType:
                return factory.NewThisTypeNode();
            case SyntaxKind.TypePredicate:
                {
                    var it = new ChildIterator(childIndices);
                    Node? assertsModifier = NodeAt(it.NextIf(mask, 0));
                    Node? parameterName = NodeAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    return factory.NewTypePredicateNode(assertsModifier, parameterName, typeNode);
                }
            case SyntaxKind.ImportAttribute:
                {
                    var it = new ChildIterator(childIndices);
                    Node? name = NodeAt(it.NextIf(mask, 0));
                    Node? value = NodeAt(it.NextIf(mask, 1));
                    return factory.NewImportAttribute(name, value);
                }
            case SyntaxKind.ImportAttributes:
                {
                    bool multiLine = (commonData & 1) != 0;
                    SyntaxKind token = SyntaxKind.WithKeyword;
                    if (((commonData >> 1) & 1) != 0)
                    {
                        token = SyntaxKind.AssertKeyword;
                    }
                    NodeList? list = childIndices.Count > 0 ? NodeListAt(childIndices[0]) : null;
                    return factory.NewImportAttributes(token, list, multiLine);
                }
            case SyntaxKind.TypeQuery:
                {
                    var it = new ChildIterator(childIndices);
                    Node? exprName = NodeAt(it.NextIf(mask, 0));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewTypeQueryNode(exprName, typeArguments);
                }
            case SyntaxKind.MappedType:
                {
                    var it = new ChildIterator(childIndices);
                    Node? readonlyToken = NodeAt(it.NextIf(mask, 0));
                    Node? typeParameter = NodeAt(it.NextIf(mask, 1));
                    Node? nameType = NodeAt(it.NextIf(mask, 2));
                    Node? questionToken = NodeAt(it.NextIf(mask, 3));
                    Node? typeNode = NodeAt(it.NextIf(mask, 4));
                    NodeList? members = NodeListAt(it.NextIf(mask, 5));
                    return factory.NewMappedTypeNode(readonlyToken, typeParameter, nameType, questionToken, typeNode, members);
                }
            case SyntaxKind.TypeLiteral:
                return factory.NewTypeLiteralNode(SingleNodeListChild(childIndices));
            case SyntaxKind.TupleType:
                return factory.NewTupleTypeNode(SingleNodeListChild(childIndices));
            case SyntaxKind.NamedTupleMember:
                {
                    var it = new ChildIterator(childIndices);
                    Node? dotDotDotToken = NodeAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? questionToken = NodeAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    return factory.NewNamedTupleMember(dotDotDotToken, name, questionToken, typeNode);
                }
            case SyntaxKind.OptionalType:
                return factory.NewOptionalTypeNode(SingleChild(childIndices));
            case SyntaxKind.RestType:
                return factory.NewRestTypeNode(SingleChild(childIndices));
            case SyntaxKind.ParenthesizedType:
                return factory.NewParenthesizedTypeNode(SingleChild(childIndices));
            case SyntaxKind.FunctionType:
                {
                    var it = new ChildIterator(childIndices);
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 0));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    return factory.NewFunctionTypeNode(typeParameters, parameters, typeNode);
                }
            case SyntaxKind.ConstructorType:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 1));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 2));
                    Node? typeNode = NodeAt(it.NextIf(mask, 3));
                    return factory.NewConstructorTypeNode(modifiers, typeParameters, parameters, typeNode);
                }
            case SyntaxKind.TemplateLiteralType:
                {
                    var it = new ChildIterator(childIndices);
                    Node? head = NodeAt(it.NextIf(mask, 0));
                    NodeList? templateSpans = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewTemplateLiteralTypeNode(head, templateSpans);
                }
            case SyntaxKind.TemplateLiteralTypeSpan:
                {
                    var it = new ChildIterator(childIndices);
                    Node? typeNode = NodeAt(it.NextIf(mask, 0));
                    Node? literal = NodeAt(it.NextIf(mask, 1));
                    return factory.NewTemplateLiteralTypeSpan(typeNode, literal);
                }
            case SyntaxKind.SyntheticExpression:
                {
                    (object? typeNode, bool isSpread) = DecodeNodeCommonData_SyntheticExpression(commonData);
                    return factory.NewSyntheticExpression(typeNode, isSpread, SingleChild(childIndices));
                }
            case SyntaxKind.PartiallyEmittedExpression:
                return factory.NewPartiallyEmittedExpression(SingleChild(childIndices));
            case SyntaxKind.JsxElement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? openingElement = NodeAt(it.NextIf(mask, 0));
                    NodeList? children = NodeListAt(it.NextIf(mask, 1));
                    Node? closingElement = NodeAt(it.NextIf(mask, 2));
                    return factory.NewJsxElement(openingElement, children, closingElement);
                }
            case SyntaxKind.JsxAttributes:
                return factory.NewJsxAttributes(SingleNodeListChild(childIndices));
            case SyntaxKind.JsxNamespacedName:
                {
                    var it = new ChildIterator(childIndices);
                    Node? @namespace = NodeAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    return factory.NewJsxNamespacedName(@namespace, name);
                }
            case SyntaxKind.JsxOpeningElement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 1));
                    Node? attributes = NodeAt(it.NextIf(mask, 2));
                    return factory.NewJsxOpeningElement(tagName, typeArguments, attributes);
                }
            case SyntaxKind.JsxSelfClosingElement:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 1));
                    Node? attributes = NodeAt(it.NextIf(mask, 2));
                    return factory.NewJsxSelfClosingElement(tagName, typeArguments, attributes);
                }
            case SyntaxKind.JsxFragment:
                {
                    var it = new ChildIterator(childIndices);
                    Node? openingFragment = NodeAt(it.NextIf(mask, 0));
                    NodeList? children = NodeListAt(it.NextIf(mask, 1));
                    Node? closingFragment = NodeAt(it.NextIf(mask, 2));
                    return factory.NewJsxFragment(openingFragment, children, closingFragment);
                }
            case SyntaxKind.JsxOpeningFragment:
                return factory.NewJsxOpeningFragment();
            case SyntaxKind.JsxClosingFragment:
                return factory.NewJsxClosingFragment();
            case SyntaxKind.JsxAttribute:
                {
                    var it = new ChildIterator(childIndices);
                    Node? name = NodeAt(it.NextIf(mask, 0));
                    Node? initializer = NodeAt(it.NextIf(mask, 1));
                    return factory.NewJsxAttribute(name, initializer);
                }
            case SyntaxKind.JsxSpreadAttribute:
                return factory.NewJsxSpreadAttribute(SingleChild(childIndices));
            case SyntaxKind.JsxClosingElement:
                return factory.NewJsxClosingElement(SingleChild(childIndices));
            case SyntaxKind.JsxExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? dotDotDotToken = NodeAt(it.NextIf(mask, 0));
                    Node? expression = NodeAt(it.NextIf(mask, 1));
                    return factory.NewJsxExpression(dotDotDotToken, expression);
                }
            case SyntaxKind.SyntaxList:
                {
                    var children = new Node[childIndices.Count];
                    for (int i = 0; i < childIndices.Count; i++)
                    {
                        children[i] = nodes[childIndices[i]]!;
                    }
                    return factory.NewSyntaxList(children);
                }
            case SyntaxKind.JSDoc:
                {
                    var it = new ChildIterator(childIndices);
                    NodeList? comment = NodeListAt(it.NextIf(mask, 0));
                    NodeList? tags = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDoc(comment, tags);
                }
            case SyntaxKind.JSDocTypeExpression:
                return factory.NewJSDocTypeExpression(SingleChild(childIndices));
            case SyntaxKind.JSDocNonNullableType:
                return factory.NewJSDocNonNullableType(SingleChild(childIndices));
            case SyntaxKind.JSDocNullableType:
                return factory.NewJSDocNullableType(SingleChild(childIndices));
            case SyntaxKind.JSDocAllType:
                return factory.NewJSDocAllType();
            case SyntaxKind.JSDocVariadicType:
                return factory.NewJSDocVariadicType(SingleChild(childIndices));
            case SyntaxKind.JSDocOptionalType:
                return factory.NewJSDocOptionalType(SingleChild(childIndices));
            case SyntaxKind.JSDocTypeTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocTypeTag(tagName, typeExpression, comment);
                }
            case SyntaxKind.JSDocUnknownTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocUnknownTag(tagName, comment);
                }
            case SyntaxKind.JSDocTemplateTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? constraint = NodeAt(it.NextIf(mask, 1));
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 2));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 3));
                    return factory.NewJSDocTemplateTag(tagName, constraint, typeParameters, comment);
                }
            case SyntaxKind.JSDocReturnTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocReturnTag(tagName, typeExpression, comment);
                }
            case SyntaxKind.JSDocPublicTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocPublicTag(tagName, comment);
                }
            case SyntaxKind.JSDocPrivateTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocPrivateTag(tagName, comment);
                }
            case SyntaxKind.JSDocProtectedTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocProtectedTag(tagName, comment);
                }
            case SyntaxKind.JSDocReadonlyTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocReadonlyTag(tagName, comment);
                }
            case SyntaxKind.JSDocOverrideTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocOverrideTag(tagName, comment);
                }
            case SyntaxKind.JSDocDeprecatedTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 1));
                    return factory.NewJSDocDeprecatedTag(tagName, comment);
                }
            case SyntaxKind.JSDocSeeTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? nameExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocSeeTag(tagName, nameExpression, comment);
                }
            case SyntaxKind.JSDocImplementsTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? className = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocImplementsTag(tagName, className, comment);
                }
            case SyntaxKind.JSDocAugmentsTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? className = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocAugmentsTag(tagName, className, comment);
                }
            case SyntaxKind.JSDocSatisfiesTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocSatisfiesTag(tagName, typeExpression, comment);
                }
            case SyntaxKind.JSDocThrowsTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocThrowsTag(tagName, typeExpression, comment);
                }
            case SyntaxKind.JSDocThisTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocThisTag(tagName, typeExpression, comment);
                }
            case SyntaxKind.JSDocImportTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? importClause = NodeAt(it.NextIf(mask, 1));
                    Node? moduleSpecifier = NodeAt(it.NextIf(mask, 2));
                    Node? attributes = NodeAt(it.NextIf(mask, 3));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 4));
                    return factory.NewJSDocImportTag(tagName, importClause, moduleSpecifier, attributes, comment);
                }
            case SyntaxKind.JSDocCallbackTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 3));
                    return factory.NewJSDocCallbackTag(tagName, typeExpression, name, comment);
                }
            case SyntaxKind.JSDocOverloadTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 2));
                    return factory.NewJSDocOverloadTag(tagName, typeExpression, comment);
                }
            case SyntaxKind.JSDocTypedefTag:
                {
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 1));
                    Node? name = NodeAt(it.NextIf(mask, 2));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 3));
                    return factory.NewJSDocTypedefTag(tagName, typeExpression, name, comment);
                }
            case SyntaxKind.JSDocSignature:
                {
                    var it = new ChildIterator(childIndices);
                    NodeList? typeParameters = NodeListAt(it.NextIf(mask, 0));
                    NodeList? parameters = NodeListAt(it.NextIf(mask, 1));
                    Node? typeNode = NodeAt(it.NextIf(mask, 2));
                    return factory.NewJSDocSignature(typeParameters, parameters, typeNode);
                }
            case SyntaxKind.JSDocNameReference:
                return factory.NewJSDocNameReference(SingleChild(childIndices));
            case SyntaxKind.ModuleDeclaration:
                {
                    SyntaxKind keyword = SyntaxKind.ModuleKeyword;
                    if ((commonData & 1) != 0)
                    {
                        keyword = SyntaxKind.NamespaceKeyword;
                    }
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? body = NodeAt(it.NextIf(mask, 2));
                    return factory.NewModuleDeclaration(modifiers, keyword, name, body);
                }
            case SyntaxKind.ImportEqualsDeclaration:
                {
                    bool isTypeOnly = (commonData & 1) != 0;
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? moduleReference = NodeAt(it.NextIf(mask, 2));
                    return factory.NewImportEqualsDeclaration(modifiers, isTypeOnly, name, moduleReference);
                }
            case SyntaxKind.ExportDeclaration:
                {
                    bool isTypeOnly = (commonData & 1) != 0;
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? exportClause = NodeAt(it.NextIf(mask, 1));
                    Node? moduleSpecifier = NodeAt(it.NextIf(mask, 2));
                    Node? attributes = NodeAt(it.NextIf(mask, 3));
                    return factory.NewExportDeclaration(modifiers, isTypeOnly, exportClause, moduleSpecifier, attributes);
                }
            case SyntaxKind.ImportType:
                {
                    bool isTypeOf = (commonData & 1) != 0;
                    var it = new ChildIterator(childIndices);
                    Node? argument = NodeAt(it.NextIf(mask, 0));
                    Node? attributes = NodeAt(it.NextIf(mask, 1));
                    Node? qualifier = NodeAt(it.NextIf(mask, 2));
                    NodeList? typeArguments = NodeListAt(it.NextIf(mask, 3));
                    return factory.NewImportTypeNode(isTypeOf, argument, attributes, qualifier, typeArguments);
                }
            case SyntaxKind.ImportClause:
                {
                    SyntaxKind phaseModifier;
                    switch (commonData & 3)
                    {
                        case 1:
                            phaseModifier = SyntaxKind.TypeKeyword;
                            break;
                        case 2:
                            phaseModifier = SyntaxKind.DeferKeyword;
                            break;
                        default:
                            phaseModifier = (SyntaxKind)0;
                            break;
                    }
                    var it = new ChildIterator(childIndices);
                    Node? name = NodeAt(it.NextIf(mask, 0));
                    Node? namedBindings = NodeAt(it.NextIf(mask, 1));
                    return factory.NewImportClause(phaseModifier, name, namedBindings);
                }
            case SyntaxKind.ImportSpecifier:
                {
                    bool isTypeOnly = (commonData & 1) != 0;
                    var it = new ChildIterator(childIndices);
                    Node? propertyName = NodeAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    return factory.NewImportSpecifier(isTypeOnly, propertyName, name);
                }
            case SyntaxKind.TypeParameter:
                {
                    var it = new ChildIterator(childIndices);
                    ModifierList? modifiers = ModifierListAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? constraint = NodeAt(it.NextIf(mask, 2));
                    Node? expression = NodeAt(it.NextIf(mask, 3));
                    Node? defaultType = NodeAt(it.NextIf(mask, 4));
                    return factory.NewTypeParameterDeclaration(modifiers, name, constraint, expression, defaultType);
                }
            case SyntaxKind.SyntheticReferenceExpression:
                {
                    var it = new ChildIterator(childIndices);
                    Node? expression = NodeAt(it.NextIf(mask, 0));
                    Node? thisArg = NodeAt(it.NextIf(mask, 1));
                    return factory.NewSyntheticReferenceExpression(expression, thisArg);
                }
            case SyntaxKind.JSDocTypeLiteral:
                {
                    bool isArrayType = (commonData & 1) != 0;
                    var nodesArr = new Node[childIndices.Count];
                    for (int i = 0; i < childIndices.Count; i++)
                    {
                        nodesArr[i] = nodes[childIndices[i]]!;
                    }
                    return factory.NewJSDocTypeLiteral(nodesArr, isArrayType);
                }
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                {
                    bool isBracketed = (commonData & 1) != 0;
                    bool isNameFirst = (commonData & 2) != 0;
                    var it = new ChildIterator(childIndices);
                    Node? tagName = NodeAt(it.NextIf(mask, 0));
                    Node? name = NodeAt(it.NextIf(mask, 1));
                    Node? typeExpression = NodeAt(it.NextIf(mask, 2));
                    NodeList? comment = NodeListAt(it.NextIf(mask, 3));
                    return factory.NewJSDocParameterOrPropertyTag(kind, tagName, name, isBracketed, typeExpression, isNameFirst, comment);
                }
            default:
                throw new InvalidOperationException($"unhandled node kind {kind} with {childIndices.Count} children");
        }
    }
}
