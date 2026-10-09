// Ported from tsgo internal/api/encoder/encoder_generated.go (1:1, hand-translated).
// Go's file is produced by _scripts/generate-encoder.ts; the C# port mirrors it by hand
// until the tools/generator bootstrap lands (plan/04 §8 names Encoder.g.cs/Decoder.g.cs
// as the eventual generated targets — regenerating from the generator must reproduce
// this file byte-for-byte in spirit: same dispatch, same bit packing, same order).

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Api.Encoder;

// Go: func getNodeDataType(node *ast.Node) uint32 (encoder_generated.go:11-34).
internal static class EncoderGenerated
{
    internal static uint GetNodeDataType(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.JsxText:
            case SyntaxKind.JSDocText:
            case SyntaxKind.JSDocLink:
            case SyntaxKind.JSDocLinkPlain:
            case SyntaxKind.JSDocLinkCode:
                return NodeDataTypes.NodeDataTypeString;
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.RegularExpressionLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
            case SyntaxKind.TemplateMiddle:
            case SyntaxKind.TemplateTail:
            case SyntaxKind.SourceFile:
                return NodeDataTypes.NodeDataTypeExtendedData;
            default:
                return NodeDataTypes.NodeDataTypeChildren;
        }
    }

    // Go: func getChildrenPropertyMask(node *ast.Node) uint8 (encoder_generated.go:36-539).
    internal static uint GetChildrenPropertyMask(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.QualifiedName:
                {
                    QualifiedName n = node.AsQualifiedName();
                    return (Encoder.BoolToByte(n.Left != null) << 0) | (Encoder.BoolToByte(n.Right != null) << 1);
                }
            case SyntaxKind.ComputedPropertyName:
                {
                    ComputedPropertyName n = node.AsComputedPropertyName();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.Decorator:
                {
                    Decorator n = node.AsDecorator();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.IfStatement:
                {
                    IfStatement n = node.AsIfStatement();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.ThenStatement != null) << 1) | (Encoder.BoolToByte(n.ElseStatement != null) << 2);
                }
            case SyntaxKind.DoStatement:
                {
                    DoStatement n = node.AsDoStatement();
                    return (Encoder.BoolToByte(n.Statement != null) << 0) | (Encoder.BoolToByte(n.Expression != null) << 1);
                }
            case SyntaxKind.WhileStatement:
                {
                    WhileStatement n = node.AsWhileStatement();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.Statement != null) << 1);
                }
            case SyntaxKind.ForStatement:
                {
                    ForStatement n = node.AsForStatement();
                    return (Encoder.BoolToByte(n.Initializer != null) << 0) | (Encoder.BoolToByte(n.Condition != null) << 1) | (Encoder.BoolToByte(n.Incrementor != null) << 2) | (Encoder.BoolToByte(n.Statement != null) << 3);
                }
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                {
                    ForInOrOfStatement n = node.AsForInOrOfStatement();
                    return (Encoder.BoolToByte(n.AwaitModifier != null) << 0) | (Encoder.BoolToByte(n.Initializer != null) << 1) | (Encoder.BoolToByte(n.Expression != null) << 2) | (Encoder.BoolToByte(n.Statement != null) << 3);
                }
            case SyntaxKind.BreakStatement:
                {
                    BreakStatement n = node.AsBreakStatement();
                    return Encoder.BoolToByte(n.Label != null) << 0;
                }
            case SyntaxKind.ContinueStatement:
                {
                    ContinueStatement n = node.AsContinueStatement();
                    return Encoder.BoolToByte(n.Label != null) << 0;
                }
            case SyntaxKind.ReturnStatement:
                {
                    ReturnStatement n = node.AsReturnStatement();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.WithStatement:
                {
                    WithStatement n = node.AsWithStatement();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.Statement != null) << 1);
                }
            case SyntaxKind.SwitchStatement:
                {
                    SwitchStatement n = node.AsSwitchStatement();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.CaseBlock != null) << 1);
                }
            case SyntaxKind.CaseBlock:
                {
                    CaseBlock n = node.AsCaseBlock();
                    return Encoder.BoolToByte(n.Clauses != null) << 0;
                }
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                {
                    CaseOrDefaultClause n = node.AsCaseOrDefaultClause();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.Statements != null) << 1);
                }
            case SyntaxKind.ThrowStatement:
                {
                    ThrowStatement n = node.AsThrowStatement();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.TryStatement:
                {
                    TryStatement n = node.AsTryStatement();
                    return (Encoder.BoolToByte(n.TryBlock != null) << 0) | (Encoder.BoolToByte(n.CatchClause != null) << 1) | (Encoder.BoolToByte(n.FinallyBlock != null) << 2);
                }
            case SyntaxKind.CatchClause:
                {
                    CatchClause n = node.AsCatchClause();
                    return (Encoder.BoolToByte(n.VariableDeclaration != null) << 0) | (Encoder.BoolToByte(n.Block != null) << 1);
                }
            case SyntaxKind.LabeledStatement:
                {
                    LabeledStatement n = node.AsLabeledStatement();
                    return (Encoder.BoolToByte(n.Label != null) << 0) | (Encoder.BoolToByte(n.Statement != null) << 1);
                }
            case SyntaxKind.ExpressionStatement:
                {
                    ExpressionStatement n = node.AsExpressionStatement();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.Block:
                {
                    Block n = node.AsBlock();
                    return Encoder.BoolToByte(n.Statements != null) << 0;
                }
            case SyntaxKind.VariableStatement:
                {
                    VariableStatement n = node.AsVariableStatement();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.DeclarationList != null) << 1);
                }
            case SyntaxKind.VariableDeclaration:
                {
                    VariableDeclaration n = node.AsVariableDeclaration();
                    return (Encoder.BoolToByte(n.Name() != null) << 0) | (Encoder.BoolToByte(n.ExclamationToken != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2) | (Encoder.BoolToByte(n.Initializer != null) << 3);
                }
            case SyntaxKind.VariableDeclarationList:
                {
                    VariableDeclarationList n = node.AsVariableDeclarationList();
                    return Encoder.BoolToByte(n.Declarations != null) << 0;
                }
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                {
                    BindingPattern n = node.AsBindingPattern();
                    return Encoder.BoolToByte(n.Elements != null) << 0;
                }
            case SyntaxKind.Parameter:
                {
                    ParameterDeclaration n = node.AsParameterDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.DotDotDotToken != null) << 1) | (Encoder.BoolToByte(n.Name() != null) << 2) | (Encoder.BoolToByte(n.QuestionToken != null) << 3) | (Encoder.BoolToByte(n.Type != null) << 4) | (Encoder.BoolToByte(n.Initializer != null) << 5);
                }
            case SyntaxKind.BindingElement:
                {
                    BindingElement n = node.AsBindingElement();
                    return (Encoder.BoolToByte(n.DotDotDotToken != null) << 0) | (Encoder.BoolToByte(n.PropertyName != null) << 1) | (Encoder.BoolToByte(n.Name() != null) << 2) | (Encoder.BoolToByte(n.Initializer != null) << 3);
                }
            case SyntaxKind.MissingDeclaration:
                {
                    MissingDeclaration n = node.AsMissingDeclaration();
                    return Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0;
                }
            case SyntaxKind.FunctionDeclaration:
                {
                    FunctionDeclaration n = node.AsFunctionDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.AsteriskToken != null) << 1) | (Encoder.BoolToByte(n.Name() != null) << 2) | (Encoder.BoolToByte(n.TypeParameters != null) << 3) | (Encoder.BoolToByte(n.Parameters != null) << 4) | (Encoder.BoolToByte(n.Type != null) << 5) | (Encoder.BoolToByte(n.Body != null) << 6);
                }
            case SyntaxKind.ClassDeclaration:
                {
                    ClassDeclaration n = node.AsClassDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.HeritageClauses != null) << 3) | (Encoder.BoolToByte(n.Members != null) << 4);
                }
            case SyntaxKind.ClassExpression:
                {
                    ClassExpression n = node.AsClassExpression();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.HeritageClauses != null) << 3) | (Encoder.BoolToByte(n.Members != null) << 4);
                }
            case SyntaxKind.HeritageClause:
                {
                    HeritageClause n = node.AsHeritageClause();
                    return Encoder.BoolToByte(n.Types != null) << 0;
                }
            case SyntaxKind.InterfaceDeclaration:
                {
                    InterfaceDeclaration n = node.AsInterfaceDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.HeritageClauses != null) << 3) | (Encoder.BoolToByte(n.Members != null) << 4);
                }
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                {
                    TypeAliasDeclaration n = node.AsTypeAliasDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3);
                }
            case SyntaxKind.EnumMember:
                {
                    EnumMember n = node.AsEnumMember();
                    return (Encoder.BoolToByte(n.Name() != null) << 0) | (Encoder.BoolToByte(n.Initializer != null) << 1);
                }
            case SyntaxKind.EnumDeclaration:
                {
                    EnumDeclaration n = node.AsEnumDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.Members != null) << 2);
                }
            case SyntaxKind.ModuleBlock:
                {
                    ModuleBlock n = node.AsModuleBlock();
                    return Encoder.BoolToByte(n.Statements != null) << 0;
                }
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                {
                    ImportDeclaration n = node.AsImportDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.ImportClause != null) << 1) | (Encoder.BoolToByte(n.ModuleSpecifier != null) << 2) | (Encoder.BoolToByte(n.Attributes != null) << 3);
                }
            case SyntaxKind.ExternalModuleReference:
                {
                    ExternalModuleReference n = node.AsExternalModuleReference();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.NamespaceImport:
                {
                    NamespaceImport n = node.AsNamespaceImport();
                    return Encoder.BoolToByte(n.Name() != null) << 0;
                }
            case SyntaxKind.NamedImports:
                {
                    NamedImports n = node.AsNamedImports();
                    return Encoder.BoolToByte(n.Elements != null) << 0;
                }
            case SyntaxKind.ExportAssignment:
                {
                    ExportAssignment n = node.AsExportAssignment();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Type != null) << 1) | (Encoder.BoolToByte(n.Expression != null) << 2);
                }
            case SyntaxKind.NamespaceExportDeclaration:
                {
                    NamespaceExportDeclaration n = node.AsNamespaceExportDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1);
                }
            case SyntaxKind.NamespaceExport:
                {
                    NamespaceExport n = node.AsNamespaceExport();
                    return Encoder.BoolToByte(n.Name() != null) << 0;
                }
            case SyntaxKind.NamedExports:
                {
                    NamedExports n = node.AsNamedExports();
                    return Encoder.BoolToByte(n.Elements != null) << 0;
                }
            case SyntaxKind.ExportSpecifier:
                {
                    ExportSpecifier n = node.AsExportSpecifier();
                    return (Encoder.BoolToByte(n.PropertyName != null) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1);
                }
            case SyntaxKind.CallSignature:
                {
                    CallSignatureDeclaration n = node.AsCallSignatureDeclaration();
                    return (Encoder.BoolToByte(n.TypeParameters != null) << 0) | (Encoder.BoolToByte(n.Parameters != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2);
                }
            case SyntaxKind.ConstructSignature:
                {
                    ConstructSignatureDeclaration n = node.AsConstructSignatureDeclaration();
                    return (Encoder.BoolToByte(n.TypeParameters != null) << 0) | (Encoder.BoolToByte(n.Parameters != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2);
                }
            case SyntaxKind.Constructor:
                {
                    ConstructorDeclaration n = node.AsConstructorDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.TypeParameters != null) << 1) | (Encoder.BoolToByte(n.Parameters != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3) | (Encoder.BoolToByte(n.Body != null) << 4);
                }
            case SyntaxKind.GetAccessor:
                {
                    GetAccessorDeclaration n = node.AsGetAccessorDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.Parameters != null) << 3) | (Encoder.BoolToByte(n.Type != null) << 4) | (Encoder.BoolToByte(n.Body != null) << 5);
                }
            case SyntaxKind.SetAccessor:
                {
                    SetAccessorDeclaration n = node.AsSetAccessorDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.Parameters != null) << 3) | (Encoder.BoolToByte(n.Type != null) << 4) | (Encoder.BoolToByte(n.Body != null) << 5);
                }
            case SyntaxKind.IndexSignature:
                {
                    IndexSignatureDeclaration n = node.AsIndexSignatureDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Parameters != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2);
                }
            case SyntaxKind.MethodSignature:
                {
                    MethodSignatureDeclaration n = node.AsMethodSignatureDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.PostfixToken != null) << 2) | (Encoder.BoolToByte(n.TypeParameters != null) << 3) | (Encoder.BoolToByte(n.Parameters != null) << 4) | (Encoder.BoolToByte(n.Type != null) << 5);
                }
            case SyntaxKind.MethodDeclaration:
                {
                    MethodDeclaration n = node.AsMethodDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.AsteriskToken != null) << 1) | (Encoder.BoolToByte(n.Name() != null) << 2) | (Encoder.BoolToByte(n.PostfixToken != null) << 3) | (Encoder.BoolToByte(n.TypeParameters != null) << 4) | (Encoder.BoolToByte(n.Parameters != null) << 5) | (Encoder.BoolToByte(n.Type != null) << 6) | (Encoder.BoolToByte(n.Body != null) << 7);
                }
            case SyntaxKind.PropertySignature:
                {
                    PropertySignatureDeclaration n = node.AsPropertySignatureDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.PostfixToken != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3) | (Encoder.BoolToByte(n.Initializer != null) << 4);
                }
            case SyntaxKind.PropertyDeclaration:
                {
                    PropertyDeclaration n = node.AsPropertyDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.PostfixToken != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3) | (Encoder.BoolToByte(n.Initializer != null) << 4);
                }
            case SyntaxKind.ClassStaticBlockDeclaration:
                {
                    ClassStaticBlockDeclaration n = node.AsClassStaticBlockDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Body != null) << 1);
                }
            case SyntaxKind.BinaryExpression:
                {
                    BinaryExpression n = node.AsBinaryExpression();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Left != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2) | (Encoder.BoolToByte(n.OperatorToken != null) << 3) | (Encoder.BoolToByte(n.Right != null) << 4);
                }
            case SyntaxKind.PrefixUnaryExpression:
                {
                    PrefixUnaryExpression n = node.AsPrefixUnaryExpression();
                    return Encoder.BoolToByte(n.Operand != null) << 0;
                }
            case SyntaxKind.PostfixUnaryExpression:
                {
                    PostfixUnaryExpression n = node.AsPostfixUnaryExpression();
                    return Encoder.BoolToByte(n.Operand != null) << 0;
                }
            case SyntaxKind.YieldExpression:
                {
                    YieldExpression n = node.AsYieldExpression();
                    return (Encoder.BoolToByte(n.AsteriskToken != null) << 0) | (Encoder.BoolToByte(n.Expression != null) << 1);
                }
            case SyntaxKind.ArrowFunction:
                {
                    ArrowFunction n = node.AsArrowFunction();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.TypeParameters != null) << 1) | (Encoder.BoolToByte(n.Parameters != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3) | (Encoder.BoolToByte(n.EqualsGreaterThanToken != null) << 4) | (Encoder.BoolToByte(n.Body != null) << 5);
                }
            case SyntaxKind.FunctionExpression:
                {
                    FunctionExpression n = node.AsFunctionExpression();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.AsteriskToken != null) << 1) | (Encoder.BoolToByte(n.Name() != null) << 2) | (Encoder.BoolToByte(n.TypeParameters != null) << 3) | (Encoder.BoolToByte(n.Parameters != null) << 4) | (Encoder.BoolToByte(n.Type != null) << 5) | (Encoder.BoolToByte(n.Body != null) << 6);
                }
            case SyntaxKind.AsExpression:
                {
                    AsExpression n = node.AsAsExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.Type != null) << 1);
                }
            case SyntaxKind.SatisfiesExpression:
                {
                    SatisfiesExpression n = node.AsSatisfiesExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.Type != null) << 1);
                }
            case SyntaxKind.ConditionalExpression:
                {
                    ConditionalExpression n = node.AsConditionalExpression();
                    return (Encoder.BoolToByte(n.Condition != null) << 0) | (Encoder.BoolToByte(n.QuestionToken != null) << 1) | (Encoder.BoolToByte(n.WhenTrue != null) << 2) | (Encoder.BoolToByte(n.ColonToken != null) << 3) | (Encoder.BoolToByte(n.WhenFalse != null) << 4);
                }
            case SyntaxKind.PropertyAccessExpression:
                {
                    PropertyAccessExpression n = node.AsPropertyAccessExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.QuestionDotToken != null) << 1) | (Encoder.BoolToByte(n.Name() != null) << 2);
                }
            case SyntaxKind.ElementAccessExpression:
                {
                    ElementAccessExpression n = node.AsElementAccessExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.QuestionDotToken != null) << 1) | (Encoder.BoolToByte(n.ArgumentExpression != null) << 2);
                }
            case SyntaxKind.CallExpression:
                {
                    CallExpression n = node.AsCallExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.QuestionDotToken != null) << 1) | (Encoder.BoolToByte(n.TypeArguments != null) << 2) | (Encoder.BoolToByte(n.Arguments != null) << 3);
                }
            case SyntaxKind.NewExpression:
                {
                    NewExpression n = node.AsNewExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.TypeArguments != null) << 1) | (Encoder.BoolToByte(n.Arguments != null) << 2);
                }
            case SyntaxKind.MetaProperty:
                {
                    MetaProperty n = node.AsMetaProperty();
                    return Encoder.BoolToByte(n.Name() != null) << 0;
                }
            case SyntaxKind.NonNullExpression:
                {
                    NonNullExpression n = node.AsNonNullExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.SpreadElement:
                {
                    SpreadElement n = node.AsSpreadElement();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.TemplateExpression:
                {
                    TemplateExpression n = node.AsTemplateExpression();
                    return (Encoder.BoolToByte(n.Head != null) << 0) | (Encoder.BoolToByte(n.TemplateSpans != null) << 1);
                }
            case SyntaxKind.TemplateSpan:
                {
                    TemplateSpan n = node.AsTemplateSpan();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.Literal != null) << 1);
                }
            case SyntaxKind.TaggedTemplateExpression:
                {
                    TaggedTemplateExpression n = node.AsTaggedTemplateExpression();
                    return (Encoder.BoolToByte(n.Tag != null) << 0) | (Encoder.BoolToByte(n.QuestionDotToken != null) << 1) | (Encoder.BoolToByte(n.TypeArguments != null) << 2) | (Encoder.BoolToByte(n.Template != null) << 3);
                }
            case SyntaxKind.ParenthesizedExpression:
                {
                    ParenthesizedExpression n = node.AsParenthesizedExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.ArrayLiteralExpression:
                {
                    ArrayLiteralExpression n = node.AsArrayLiteralExpression();
                    return Encoder.BoolToByte(n.Elements != null) << 0;
                }
            case SyntaxKind.ObjectLiteralExpression:
                {
                    ObjectLiteralExpression n = node.AsObjectLiteralExpression();
                    return Encoder.BoolToByte(n.Properties != null) << 0;
                }
            case SyntaxKind.SpreadAssignment:
                {
                    SpreadAssignment n = node.AsSpreadAssignment();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.PropertyAssignment:
                {
                    PropertyAssignment n = node.AsPropertyAssignment();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.PostfixToken != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3) | (Encoder.BoolToByte(n.Initializer != null) << 4);
                }
            case SyntaxKind.ShorthandPropertyAssignment:
                {
                    ShorthandPropertyAssignment n = node.AsShorthandPropertyAssignment();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.PostfixToken != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3) | (Encoder.BoolToByte(n.EqualsToken != null) << 4) | (Encoder.BoolToByte(n.ObjectAssignmentInitializer != null) << 5);
                }
            case SyntaxKind.DeleteExpression:
                {
                    DeleteExpression n = node.AsDeleteExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.TypeOfExpression:
                {
                    TypeOfExpression n = node.AsTypeOfExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.VoidExpression:
                {
                    VoidExpression n = node.AsVoidExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.AwaitExpression:
                {
                    AwaitExpression n = node.AsAwaitExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.TypeAssertionExpression:
                {
                    TypeAssertion n = node.AsTypeAssertion();
                    return (Encoder.BoolToByte(n.Type != null) << 0) | (Encoder.BoolToByte(n.Expression != null) << 1);
                }
            case SyntaxKind.UnionType:
                {
                    UnionTypeNode n = node.AsUnionTypeNode();
                    return Encoder.BoolToByte(n.Types != null) << 0;
                }
            case SyntaxKind.IntersectionType:
                {
                    IntersectionTypeNode n = node.AsIntersectionTypeNode();
                    return Encoder.BoolToByte(n.Types != null) << 0;
                }
            case SyntaxKind.ConditionalType:
                {
                    ConditionalTypeNode n = node.AsConditionalTypeNode();
                    return (Encoder.BoolToByte(n.CheckType != null) << 0) | (Encoder.BoolToByte(n.ExtendsType != null) << 1) | (Encoder.BoolToByte(n.TrueType != null) << 2) | (Encoder.BoolToByte(n.FalseType != null) << 3);
                }
            case SyntaxKind.TypeOperator:
                {
                    TypeOperatorNode n = node.AsTypeOperatorNode();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.InferType:
                {
                    InferTypeNode n = node.AsInferTypeNode();
                    return Encoder.BoolToByte(n.TypeParameter != null) << 0;
                }
            case SyntaxKind.ArrayType:
                {
                    ArrayTypeNode n = node.AsArrayTypeNode();
                    return Encoder.BoolToByte(n.ElementType != null) << 0;
                }
            case SyntaxKind.IndexedAccessType:
                {
                    IndexedAccessTypeNode n = node.AsIndexedAccessTypeNode();
                    return (Encoder.BoolToByte(n.ObjectType != null) << 0) | (Encoder.BoolToByte(n.IndexType != null) << 1);
                }
            case SyntaxKind.TypeReference:
                {
                    TypeReferenceNode n = node.AsTypeReferenceNode();
                    return (Encoder.BoolToByte(n.TypeName != null) << 0) | (Encoder.BoolToByte(n.TypeArguments != null) << 1);
                }
            case SyntaxKind.ExpressionWithTypeArguments:
                {
                    ExpressionWithTypeArguments n = node.AsExpressionWithTypeArguments();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.TypeArguments != null) << 1);
                }
            case SyntaxKind.LiteralType:
                {
                    LiteralTypeNode n = node.AsLiteralTypeNode();
                    return Encoder.BoolToByte(n.Literal != null) << 0;
                }
            case SyntaxKind.TypePredicate:
                {
                    TypePredicateNode n = node.AsTypePredicateNode();
                    return (Encoder.BoolToByte(n.AssertsModifier != null) << 0) | (Encoder.BoolToByte(n.ParameterName != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2);
                }
            case SyntaxKind.ImportAttribute:
                {
                    ImportAttribute n = node.AsImportAttribute();
                    return (Encoder.BoolToByte(n.Name() != null) << 0) | (Encoder.BoolToByte(n.Value != null) << 1);
                }
            case SyntaxKind.ImportAttributes:
                {
                    ImportAttributes n = node.AsImportAttributes();
                    return Encoder.BoolToByte(n.Attributes != null) << 0;
                }
            case SyntaxKind.TypeQuery:
                {
                    TypeQueryNode n = node.AsTypeQueryNode();
                    return (Encoder.BoolToByte(n.ExprName != null) << 0) | (Encoder.BoolToByte(n.TypeArguments != null) << 1);
                }
            case SyntaxKind.MappedType:
                {
                    MappedTypeNode n = node.AsMappedTypeNode();
                    return (Encoder.BoolToByte(n.ReadonlyToken != null) << 0) | (Encoder.BoolToByte(n.TypeParameter != null) << 1) | (Encoder.BoolToByte(n.NameType != null) << 2) | (Encoder.BoolToByte(n.QuestionToken != null) << 3) | (Encoder.BoolToByte(n.Type != null) << 4) | (Encoder.BoolToByte(n.Members != null) << 5);
                }
            case SyntaxKind.TypeLiteral:
                {
                    TypeLiteralNode n = node.AsTypeLiteralNode();
                    return Encoder.BoolToByte(n.Members != null) << 0;
                }
            case SyntaxKind.TupleType:
                {
                    TupleTypeNode n = node.AsTupleTypeNode();
                    return Encoder.BoolToByte(n.Elements != null) << 0;
                }
            case SyntaxKind.NamedTupleMember:
                {
                    NamedTupleMember n = node.AsNamedTupleMember();
                    return (Encoder.BoolToByte(n.DotDotDotToken != null) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.QuestionToken != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3);
                }
            case SyntaxKind.OptionalType:
                {
                    OptionalTypeNode n = node.AsOptionalTypeNode();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.RestType:
                {
                    RestTypeNode n = node.AsRestTypeNode();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.ParenthesizedType:
                {
                    ParenthesizedTypeNode n = node.AsParenthesizedTypeNode();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.FunctionType:
                {
                    FunctionTypeNode n = node.AsFunctionTypeNode();
                    return (Encoder.BoolToByte(n.TypeParameters != null) << 0) | (Encoder.BoolToByte(n.Parameters != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2);
                }
            case SyntaxKind.ConstructorType:
                {
                    ConstructorTypeNode n = node.AsConstructorTypeNode();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.TypeParameters != null) << 1) | (Encoder.BoolToByte(n.Parameters != null) << 2) | (Encoder.BoolToByte(n.Type != null) << 3);
                }
            case SyntaxKind.TemplateLiteralType:
                {
                    TemplateLiteralTypeNode n = node.AsTemplateLiteralTypeNode();
                    return (Encoder.BoolToByte(n.Head != null) << 0) | (Encoder.BoolToByte(n.TemplateSpans != null) << 1);
                }
            case SyntaxKind.TemplateLiteralTypeSpan:
                {
                    TemplateLiteralTypeSpan n = node.AsTemplateLiteralTypeSpan();
                    return (Encoder.BoolToByte(n.Type != null) << 0) | (Encoder.BoolToByte(n.Literal != null) << 1);
                }
            case SyntaxKind.SyntheticExpression:
                {
                    SyntheticExpression n = node.AsSyntheticExpression();
                    return Encoder.BoolToByte(n.TupleNameSource != null) << 0;
                }
            case SyntaxKind.PartiallyEmittedExpression:
                {
                    PartiallyEmittedExpression n = node.AsPartiallyEmittedExpression();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.JsxElement:
                {
                    JsxElement n = node.AsJsxElement();
                    return (Encoder.BoolToByte(n.OpeningElement != null) << 0) | (Encoder.BoolToByte(n.Children != null) << 1) | (Encoder.BoolToByte(n.ClosingElement != null) << 2);
                }
            case SyntaxKind.JsxAttributes:
                {
                    JsxAttributes n = node.AsJsxAttributes();
                    return Encoder.BoolToByte(n.Properties != null) << 0;
                }
            case SyntaxKind.JsxNamespacedName:
                {
                    JsxNamespacedName n = node.AsJsxNamespacedName();
                    return (Encoder.BoolToByte(n.Namespace != null) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1);
                }
            case SyntaxKind.JsxOpeningElement:
                {
                    JsxOpeningElement n = node.AsJsxOpeningElement();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeArguments != null) << 1) | (Encoder.BoolToByte(n.Attributes != null) << 2);
                }
            case SyntaxKind.JsxSelfClosingElement:
                {
                    JsxSelfClosingElement n = node.AsJsxSelfClosingElement();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeArguments != null) << 1) | (Encoder.BoolToByte(n.Attributes != null) << 2);
                }
            case SyntaxKind.JsxFragment:
                {
                    JsxFragment n = node.AsJsxFragment();
                    return (Encoder.BoolToByte(n.OpeningFragment != null) << 0) | (Encoder.BoolToByte(n.Children != null) << 1) | (Encoder.BoolToByte(n.ClosingFragment != null) << 2);
                }
            case SyntaxKind.JsxAttribute:
                {
                    JsxAttribute n = node.AsJsxAttribute();
                    return (Encoder.BoolToByte(n.Name() != null) << 0) | (Encoder.BoolToByte(n.Initializer != null) << 1);
                }
            case SyntaxKind.JsxSpreadAttribute:
                {
                    JsxSpreadAttribute n = node.AsJsxSpreadAttribute();
                    return Encoder.BoolToByte(n.Expression != null) << 0;
                }
            case SyntaxKind.JsxClosingElement:
                {
                    JsxClosingElement n = node.AsJsxClosingElement();
                    return Encoder.BoolToByte(n.TagName != null) << 0;
                }
            case SyntaxKind.JsxExpression:
                {
                    JsxExpression n = node.AsJsxExpression();
                    return (Encoder.BoolToByte(n.DotDotDotToken != null) << 0) | (Encoder.BoolToByte(n.Expression != null) << 1);
                }
            case SyntaxKind.SyntaxList:
                {
                    SyntaxList n = node.AsSyntaxList();
                    return Encoder.BoolToByte(n.Children is { Length: > 0 }) << 0;
                }
            case SyntaxKind.JSDoc:
                {
                    JSDoc n = node.AsJSDoc();
                    return (Encoder.BoolToByte(n.Comment != null) << 0) | (Encoder.BoolToByte(n.Tags != null) << 1);
                }
            case SyntaxKind.JSDocTypeExpression:
                {
                    JSDocTypeExpression n = node.AsJSDocTypeExpression();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.JSDocNonNullableType:
                {
                    JSDocNonNullableType n = node.AsJSDocNonNullableType();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.JSDocNullableType:
                {
                    JSDocNullableType n = node.AsJSDocNullableType();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.JSDocVariadicType:
                {
                    JSDocVariadicType n = node.AsJSDocVariadicType();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.JSDocOptionalType:
                {
                    JSDocOptionalType n = node.AsJSDocOptionalType();
                    return Encoder.BoolToByte(n.Type != null) << 0;
                }
            case SyntaxKind.JSDocTypeTag:
                {
                    JSDocTypeTag n = node.AsJSDocTypeTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocUnknownTag:
                {
                    JSDocUnknownTag n = node.AsJSDocUnknownTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocTemplateTag:
                {
                    JSDocTemplateTag n = node.AsJSDocTemplateTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Constraint != null) << 1) | (Encoder.BoolToByte(n.TypeParameters != null) << 2) | (Encoder.BoolToByte(n.Comment != null) << 3);
                }
            case SyntaxKind.JSDocReturnTag:
                {
                    JSDocReturnTag n = node.AsJSDocReturnTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocPublicTag:
                {
                    JSDocPublicTag n = node.AsJSDocPublicTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocPrivateTag:
                {
                    JSDocPrivateTag n = node.AsJSDocPrivateTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocProtectedTag:
                {
                    JSDocProtectedTag n = node.AsJSDocProtectedTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocReadonlyTag:
                {
                    JSDocReadonlyTag n = node.AsJSDocReadonlyTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocOverrideTag:
                {
                    JSDocOverrideTag n = node.AsJSDocOverrideTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocDeprecatedTag:
                {
                    JSDocDeprecatedTag n = node.AsJSDocDeprecatedTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Comment != null) << 1);
                }
            case SyntaxKind.JSDocSeeTag:
                {
                    JSDocSeeTag n = node.AsJSDocSeeTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.NameExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocImplementsTag:
                {
                    JSDocImplementsTag n = node.AsJSDocImplementsTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.ClassName != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocAugmentsTag:
                {
                    JSDocAugmentsTag n = node.AsJSDocAugmentsTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.ClassName != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocSatisfiesTag:
                {
                    JSDocSatisfiesTag n = node.AsJSDocSatisfiesTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocThrowsTag:
                {
                    JSDocThrowsTag n = node.AsJSDocThrowsTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocThisTag:
                {
                    JSDocThisTag n = node.AsJSDocThisTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocImportTag:
                {
                    JSDocImportTag n = node.AsJSDocImportTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.ImportClause != null) << 1) | (Encoder.BoolToByte(n.ModuleSpecifier != null) << 2) | (Encoder.BoolToByte(n.Attributes != null) << 3) | (Encoder.BoolToByte(n.Comment != null) << 4);
                }
            case SyntaxKind.JSDocCallbackTag:
                {
                    JSDocCallbackTag n = node.AsJSDocCallbackTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Name != null) << 2) | (Encoder.BoolToByte(n.Comment != null) << 3);
                }
            case SyntaxKind.JSDocOverloadTag:
                {
                    JSDocOverloadTag n = node.AsJSDocOverloadTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Comment != null) << 2);
                }
            case SyntaxKind.JSDocTypedefTag:
                {
                    JSDocTypedefTag n = node.AsJSDocTypedefTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.TypeExpression != null) << 1) | (Encoder.BoolToByte(n.Name != null) << 2) | (Encoder.BoolToByte(n.Comment != null) << 3);
                }
            case SyntaxKind.JSDocSignature:
                {
                    JSDocSignature n = node.AsJSDocSignature();
                    return (Encoder.BoolToByte(n.TypeParameters != null) << 0) | (Encoder.BoolToByte(n.Parameters != null) << 1) | (Encoder.BoolToByte(n.Type != null) << 2);
                }
            case SyntaxKind.JSDocNameReference:
                {
                    JSDocNameReference n = node.AsJSDocNameReference();
                    return Encoder.BoolToByte(n.Name != null) << 0;
                }
            case SyntaxKind.ModuleDeclaration:
                {
                    ModuleDeclaration n = node.AsModuleDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.Body != null) << 2);
                }
            case SyntaxKind.ImportEqualsDeclaration:
                {
                    ImportEqualsDeclaration n = node.AsImportEqualsDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.ModuleReference != null) << 2);
                }
            case SyntaxKind.ExportDeclaration:
                {
                    ExportDeclaration n = node.AsExportDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.ExportClause != null) << 1) | (Encoder.BoolToByte(n.ModuleSpecifier != null) << 2) | (Encoder.BoolToByte(n.Attributes != null) << 3);
                }
            case SyntaxKind.ImportType:
                {
                    ImportTypeNode n = node.AsImportTypeNode();
                    return (Encoder.BoolToByte(n.Argument != null) << 0) | (Encoder.BoolToByte(n.Attributes != null) << 1) | (Encoder.BoolToByte(n.Qualifier != null) << 2) | (Encoder.BoolToByte(n.TypeArguments != null) << 3);
                }
            case SyntaxKind.ImportClause:
                {
                    ImportClause n = node.AsImportClause();
                    return (Encoder.BoolToByte(n.Name() != null) << 0) | (Encoder.BoolToByte(n.NamedBindings != null) << 1);
                }
            case SyntaxKind.ImportSpecifier:
                {
                    ImportSpecifier n = node.AsImportSpecifier();
                    return (Encoder.BoolToByte(n.PropertyName != null) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1);
                }
            case SyntaxKind.JSDocLink:
                {
                    JSDocLink n = node.AsJSDocLink();
                    return Encoder.BoolToByte(n.Name() != null) << 0;
                }
            case SyntaxKind.JSDocLinkPlain:
                {
                    JSDocLinkPlain n = node.AsJSDocLinkPlain();
                    return Encoder.BoolToByte(n.Name() != null) << 0;
                }
            case SyntaxKind.JSDocLinkCode:
                {
                    JSDocLinkCode n = node.AsJSDocLinkCode();
                    return Encoder.BoolToByte(n.Name() != null) << 0;
                }
            case SyntaxKind.TypeParameter:
                {
                    TypeParameterDeclaration n = node.AsTypeParameterDeclaration();
                    return (Encoder.BoolToByte(Encoder.HasModifiers(n.Modifiers())) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.Constraint != null) << 2) | (Encoder.BoolToByte(n.Expression != null) << 3) | (Encoder.BoolToByte(n.DefaultType != null) << 4);
                }
            case SyntaxKind.SyntheticReferenceExpression:
                {
                    SyntheticReferenceExpression n = node.AsSyntheticReferenceExpression();
                    return (Encoder.BoolToByte(n.Expression != null) << 0) | (Encoder.BoolToByte(n.ThisArg != null) << 1);
                }
            case SyntaxKind.JSDocTypeLiteral:
                {
                    JSDocTypeLiteral n = node.AsJSDocTypeLiteral();
                    return Encoder.BoolToByte(n.JSDocPropertyTags is { Length: > 0 }) << 0;
                }
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                {
                    JSDocParameterOrPropertyTag n = node.AsJSDocParameterOrPropertyTag();
                    return (Encoder.BoolToByte(n.TagName != null) << 0) | (Encoder.BoolToByte(n.Name() != null) << 1) | (Encoder.BoolToByte(n.TypeExpression != null) << 2) | (Encoder.BoolToByte(n.Comment != null) << 3);
                }
            default:
                return 0;
        }
    }

    // Go: func getNodeCommonData(node *ast.Node) uint32 (encoder_generated.go:541-659).
    internal static uint GetNodeCommonData(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Block:
                {
                    Block n = node.AsBlock();
                    return Encoder.BoolToByte(n.MultiLine) << 24;
                }
            case SyntaxKind.HeritageClause:
                {
                    HeritageClause n = node.AsHeritageClause();
                    uint tokenIdx = n.Token switch
                    {
                        SyntaxKind.ImplementsKeyword => 1,
                        _ => 0,
                    };
                    return tokenIdx << 24;
                }
            case SyntaxKind.ExportAssignment:
                {
                    ExportAssignment n = node.AsExportAssignment();
                    return Encoder.BoolToByte(n.IsExportEquals) << 24;
                }
            case SyntaxKind.ExportSpecifier:
                {
                    ExportSpecifier n = node.AsExportSpecifier();
                    return Encoder.BoolToByte(n.IsTypeOnly) << 24;
                }
            case SyntaxKind.PrefixUnaryExpression:
                {
                    PrefixUnaryExpression n = node.AsPrefixUnaryExpression();
                    uint operatorIdx = n.Operator switch
                    {
                        SyntaxKind.MinusToken => 1,
                        SyntaxKind.TildeToken => 2,
                        SyntaxKind.ExclamationToken => 3,
                        SyntaxKind.PlusPlusToken => 4,
                        SyntaxKind.MinusMinusToken => 5,
                        _ => 0,
                    };
                    return operatorIdx << 24;
                }
            case SyntaxKind.PostfixUnaryExpression:
                {
                    PostfixUnaryExpression n = node.AsPostfixUnaryExpression();
                    uint operatorIdx = n.Operator switch
                    {
                        SyntaxKind.MinusMinusToken => 1,
                        _ => 0,
                    };
                    return operatorIdx << 24;
                }
            case SyntaxKind.MetaProperty:
                {
                    MetaProperty n = node.AsMetaProperty();
                    uint keywordTokenIdx = n.KeywordToken switch
                    {
                        SyntaxKind.NewKeyword => 1,
                        _ => 0,
                    };
                    return keywordTokenIdx << 24;
                }
            case SyntaxKind.ArrayLiteralExpression:
                {
                    ArrayLiteralExpression n = node.AsArrayLiteralExpression();
                    return Encoder.BoolToByte(n.MultiLine) << 24;
                }
            case SyntaxKind.ObjectLiteralExpression:
                {
                    ObjectLiteralExpression n = node.AsObjectLiteralExpression();
                    return Encoder.BoolToByte(n.MultiLine) << 24;
                }
            case SyntaxKind.TypeOperator:
                {
                    TypeOperatorNode n = node.AsTypeOperatorNode();
                    uint operatorIdx = n.Operator switch
                    {
                        SyntaxKind.ReadonlyKeyword => 1,
                        SyntaxKind.UniqueKeyword => 2,
                        _ => 0,
                    };
                    return operatorIdx << 24;
                }
            case SyntaxKind.ImportAttributes:
                {
                    ImportAttributes n = node.AsImportAttributes();
                    uint tokenIdx = n.Token switch
                    {
                        SyntaxKind.AssertKeyword => 1,
                        _ => 0,
                    };
                    return (Encoder.BoolToByte(n.MultiLine) << 24) | (tokenIdx << 25);
                }
            case SyntaxKind.SyntheticExpression:
                return Encoder.GetNodeCommonData_SyntheticExpression(node);
            case SyntaxKind.JsxText:
                {
                    JsxText n = node.AsJsxText();
                    return Encoder.BoolToByte(n.ContainsOnlyTriviaWhiteSpaces) << 24;
                }
            case SyntaxKind.ModuleDeclaration:
                {
                    ModuleDeclaration n = node.AsModuleDeclaration();
                    uint keywordIdx = n.Keyword switch
                    {
                        SyntaxKind.NamespaceKeyword => 1,
                        _ => 0,
                    };
                    return keywordIdx << 24;
                }
            case SyntaxKind.ImportEqualsDeclaration:
                {
                    ImportEqualsDeclaration n = node.AsImportEqualsDeclaration();
                    return Encoder.BoolToByte(n.IsTypeOnly) << 24;
                }
            case SyntaxKind.ExportDeclaration:
                {
                    ExportDeclaration n = node.AsExportDeclaration();
                    return Encoder.BoolToByte(n.IsTypeOnly) << 24;
                }
            case SyntaxKind.ImportType:
                {
                    ImportTypeNode n = node.AsImportTypeNode();
                    return Encoder.BoolToByte(n.IsTypeOf) << 24;
                }
            case SyntaxKind.ImportClause:
                {
                    ImportClause n = node.AsImportClause();
                    uint phaseModifierIdx = n.PhaseModifier switch
                    {
                        SyntaxKind.TypeKeyword => 1,
                        SyntaxKind.DeferKeyword => 2,
                        _ => 0,
                    };
                    return phaseModifierIdx << 24;
                }
            case SyntaxKind.ImportSpecifier:
                {
                    ImportSpecifier n = node.AsImportSpecifier();
                    return Encoder.BoolToByte(n.IsTypeOnly) << 24;
                }
            case SyntaxKind.JSDocTypeLiteral:
                {
                    JSDocTypeLiteral n = node.AsJSDocTypeLiteral();
                    return Encoder.BoolToByte(n.IsArrayType) << 24;
                }
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                {
                    JSDocParameterOrPropertyTag n = node.AsJSDocParameterOrPropertyTag();
                    return (Encoder.BoolToByte(n.IsBracketed) << 24) | (Encoder.BoolToByte(n.IsNameFirst) << 25);
                }
            default:
                return 0;
        }
    }

    // Go: func recordNodeStrings(node *ast.Node, strs *stringTable) uint32
    // (encoder_generated.go:661-680).
    internal static uint RecordNodeStrings(Node node, StringTable strs)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                return strs.Add(node.AsIdentifier().Text, node.Kind, node.Pos, node.End);
            case SyntaxKind.PrivateIdentifier:
                return strs.Add(node.AsPrivateIdentifier().Text, node.Kind, node.Pos, node.End);
            case SyntaxKind.JsxText:
                return strs.Add(node.AsJsxText().Text, node.Kind, node.Pos, node.End);
            case SyntaxKind.JSDocText:
                return strs.Add(node.AsJSDocText().Text(), node.Kind, node.Pos, node.End);
            case SyntaxKind.JSDocLink:
                return strs.Add(node.AsJSDocLink().Text(), node.Kind, node.Pos, node.End);
            case SyntaxKind.JSDocLinkPlain:
                return strs.Add(node.AsJSDocLinkPlain().Text(), node.Kind, node.Pos, node.End);
            case SyntaxKind.JSDocLinkCode:
                return strs.Add(node.AsJSDocLinkCode().Text(), node.Kind, node.Pos, node.End);
            default:
                throw new InvalidOperationException($"Unexpected node kind {node.Kind}");
        }
    }

    // Go: func recordExtendedData(node *ast.Node, strs *stringTable, positionMap *ast.PositionMap,
    // extendedData *[]byte, structuredData *[]byte) uint32 (encoder_generated.go:682-707).
    // PORT-NOTE (UTF-16): Go threads a PositionMap through the extended-data recorders to
    // convert byte offsets; the C# port's positions are already UTF-16 code units, so the
    // map is dropped (plan/05 §8.1).
    internal static uint RecordExtendedData(Node node, StringTable strs, List<uint> extendedData, List<byte> structuredData)
    {
        // Go appends to a []byte and returns len(*extendedData) — a BYTE offset, which is
        // what the node data word carries and the decoder adds to extData. extendedData is
        // word-addressed here, so the byte offset is count*4.
        uint offset = (uint)extendedData.Count * 4;
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
                Encoder.RecordExtendedData_StringLiteral(node, strs, extendedData);
                break;
            case SyntaxKind.NumericLiteral:
                Encoder.RecordExtendedData_NumericLiteral(node, strs, extendedData);
                break;
            case SyntaxKind.BigIntLiteral:
                Encoder.RecordExtendedData_BigIntLiteral(node, strs, extendedData);
                break;
            case SyntaxKind.RegularExpressionLiteral:
                Encoder.RecordExtendedData_RegularExpressionLiteral(node, strs, extendedData);
                break;
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                Encoder.RecordExtendedData_NoSubstitutionTemplateLiteral(node, strs, extendedData);
                break;
            case SyntaxKind.TemplateHead:
                Encoder.RecordExtendedData_TemplateHead(node, strs, extendedData);
                break;
            case SyntaxKind.TemplateMiddle:
                Encoder.RecordExtendedData_TemplateMiddle(node, strs, extendedData);
                break;
            case SyntaxKind.TemplateTail:
                Encoder.RecordExtendedData_TemplateTail(node, strs, extendedData);
                break;
            case SyntaxKind.SourceFile:
                Encoder.RecordExtendedData_SourceFile(node, strs, extendedData, structuredData);
                break;
            default:
                throw new InvalidOperationException($"unknown extended data node kind {node.Kind}");
        }
        return offset;
    }
}
