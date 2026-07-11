// Ported from tsgo internal/transformers/moduletransforms/commonjsmodule.go.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Binder;
using Ts.Collections;
using Ts.Core;

namespace Ts.Transformers.ModuleTransforms;

public sealed partial class CommonJSModuleTransformer : Transformer
{
    private readonly NodeVisitor topLevelVisitor;
    private readonly NodeVisitor topLevelNestedVisitor;
    private readonly NodeVisitor discardedValueVisitor;
    private readonly NodeVisitor assignmentPatternVisitor;
    private readonly CompilerOptions compilerOptions;
    private readonly IReferenceResolver resolver;
    private readonly Func<HasFileName, ModuleKind> getEmitModuleFormatOfFile;
    private readonly ModuleKind moduleKind;
    private readonly ScriptTarget languageVersion;
    private SourceFile? currentSourceFile;
    private ExternalModuleInfo? currentModuleInfo;
    private Node? parentNode;
    private Node? currentNode;

    private CommonJSModuleTransformer(TransformOptions options)
    {
        compilerOptions = options.CompilerOptions!;
        resolver = options.Resolver!;
        getEmitModuleFormatOfFile = options.GetEmitModuleFormatOfFile!;
        var emitContext = options.Context ?? global::Ts.Printer.EmitContext.NewEmitContext();
        topLevelVisitor = emitContext.NewNodeVisitor(VisitTopLevel);
        topLevelNestedVisitor = emitContext.NewNodeVisitor(VisitTopLevelNested);
        discardedValueVisitor = emitContext.NewNodeVisitor(VisitDiscardedValue);
        assignmentPatternVisitor = emitContext.NewNodeVisitor(VisitAssignmentPattern);
        languageVersion = compilerOptions.GetEmitScriptTarget();
        moduleKind = compilerOptions.GetEmitModuleKind();
        NewTransformer(Visit, emitContext);
    }

    public static Transformer NewCommonJSModuleTransformer(TransformOptions options) => new CommonJSModuleTransformer(options);

    private Node? PushNode(Node node)
    {
        var grandparentNode = parentNode;
        parentNode = currentNode;
        currentNode = node;
        return grandparentNode;
    }

    private void PopNode(Node? grandparentNode)
    {
        currentNode = parentNode;
        parentNode = grandparentNode;
    }

    private Node? VisitTopLevel(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return node.Kind switch
            {
                SyntaxKind.ImportDeclaration => VisitTopLevelImportDeclaration(node.AsImportDeclaration()),
                SyntaxKind.ImportEqualsDeclaration => VisitTopLevelImportEqualsDeclaration(node.AsImportEqualsDeclaration()),
                SyntaxKind.ExportDeclaration => VisitTopLevelExportDeclaration(node.AsExportDeclaration()),
                SyntaxKind.ExportAssignment => VisitTopLevelExportAssignment(node.AsExportAssignment()),
                SyntaxKind.FunctionDeclaration => VisitTopLevelFunctionDeclaration(node.AsFunctionDeclaration()),
                SyntaxKind.ClassDeclaration => VisitTopLevelClassDeclaration(node.AsClassDeclaration()),
                SyntaxKind.VariableStatement => VisitTopLevelVariableStatement(node.AsVariableStatement()),
                _ => VisitTopLevelNestedNoStack(node),
            };
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node? VisitTopLevelNested(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return VisitTopLevelNestedNoStack(node);
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node? VisitTopLevelNestedNoStack(Node node)
    {
        return node.Kind switch
        {
            SyntaxKind.VariableStatement => VisitTopLevelVariableStatement(node.AsVariableStatement()),
            SyntaxKind.ForStatement => VisitTopLevelNestedForStatement(node.AsForStatement()),
            SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement => VisitTopLevelNestedForInOrOfStatement(node.AsForInOrOfStatement()),
            SyntaxKind.DoStatement => VisitTopLevelNestedDoStatement(node.AsDoStatement()),
            SyntaxKind.WhileStatement => VisitTopLevelNestedWhileStatement(node.AsWhileStatement()),
            SyntaxKind.LabeledStatement => VisitTopLevelNestedLabeledStatement(node.AsLabeledStatement()),
            SyntaxKind.WithStatement => VisitTopLevelNestedWithStatement(node.AsWithStatement()),
            SyntaxKind.IfStatement => VisitTopLevelNestedIfStatement(node.AsIfStatement()),
            SyntaxKind.SwitchStatement => VisitTopLevelNestedSwitchStatement(node.AsSwitchStatement()),
            SyntaxKind.CaseBlock => VisitTopLevelNestedCaseBlock(node.AsCaseBlock()),
            SyntaxKind.CaseClause or SyntaxKind.DefaultClause => VisitTopLevelNestedCaseOrDefaultClause(node.AsCaseOrDefaultClause()),
            SyntaxKind.TryStatement => VisitTopLevelNestedTryStatement(node.AsTryStatement()),
            SyntaxKind.CatchClause => VisitTopLevelNestedCatchClause(node.AsCatchClause()),
            SyntaxKind.Block => VisitTopLevelNestedBlock(node.AsBlock()),
            _ => VisitNoStack(node, false),
        };
    }

    private Node? Visit(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return VisitNoStack(node, false);
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node? VisitNoStack(Node node, bool resultIsDiscarded)
    {
        if (!global::Ts.Ast.NodeTests.IsSourceFile(node) &&
            (node.SubtreeFacts() & (SubtreeFacts.SubtreeContainsDynamicImport | SubtreeFacts.SubtreeContainsIdentifier)) == 0)
        {
            return node;
        }
        return node.Kind switch
        {
            SyntaxKind.SourceFile => VisitSourceFile(node.AsSourceFile()),
            SyntaxKind.ForStatement => VisitForStatement(node.AsForStatement()),
            SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement => VisitForInOrOfStatement(node.AsForInOrOfStatement()),
            SyntaxKind.ExpressionStatement => VisitExpressionStatement(node.AsExpressionStatement()),
            SyntaxKind.VoidExpression => VisitVoidExpression(node.AsVoidExpression()),
            SyntaxKind.ParenthesizedExpression => VisitParenthesizedExpression(node.AsParenthesizedExpression(), resultIsDiscarded),
            SyntaxKind.PartiallyEmittedExpression => VisitPartiallyEmittedExpression(node.AsPartiallyEmittedExpression(), resultIsDiscarded),
            SyntaxKind.CallExpression => VisitCallExpression(node.AsCallExpression()),
            SyntaxKind.TaggedTemplateExpression => VisitTaggedTemplateExpression(node.AsTaggedTemplateExpression()),
            SyntaxKind.BinaryExpression => VisitBinaryExpression(node.AsBinaryExpression(), resultIsDiscarded),
            SyntaxKind.PrefixUnaryExpression => VisitPrefixUnaryExpression(node.AsPrefixUnaryExpression(), resultIsDiscarded),
            SyntaxKind.PostfixUnaryExpression => VisitPostfixUnaryExpression(node.AsPostfixUnaryExpression(), resultIsDiscarded),
            SyntaxKind.ShorthandPropertyAssignment => VisitShorthandPropertyAssignment(node.AsShorthandPropertyAssignment()),
            SyntaxKind.Identifier => VisitIdentifier(node),
            _ => Visitor().VisitEachChild(node),
        };
    }

    private Node? VisitDiscardedValue(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return VisitNoStack(node, true);
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node? VisitAssignmentPattern(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return VisitAssignmentPatternNoStack(node);
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node? VisitAssignmentPatternNoStack(Node node)
    {
        return node.Kind switch
        {
            SyntaxKind.ObjectLiteralExpression or SyntaxKind.ArrayLiteralExpression => assignmentPatternVisitor.VisitEachChild(node),
            SyntaxKind.PropertyAssignment => VisitAssignmentProperty(node.AsPropertyAssignment()),
            SyntaxKind.ShorthandPropertyAssignment => VisitShorthandAssignmentProperty(node.AsShorthandPropertyAssignment()),
            SyntaxKind.SpreadAssignment => VisitAssignmentRestProperty(node.AsSpreadAssignment()),
            SyntaxKind.SpreadElement => VisitAssignmentRestElement(node.AsSpreadElement()),
            _ when global::Ts.Ast.AstUtilities.IsExpression(node) => VisitAssignmentElement(node),
            _ => VisitNoStack(node, false),
        };
    }

    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile ||
            !(TransformerAstAdapters.IsEffectiveExternalModule(node, compilerOptions) ||
            (node.SubtreeFacts() & SubtreeFacts.SubtreeContainsDynamicImport) != 0))
        {
            return node.AsNode();
        }
        currentSourceFile = node;
        currentModuleInfo = ExternalModuleInfoCollector.CollectExternalModuleInfo(node, compilerOptions, EmitContext(), resolver);
        var updated = TransformCommonJSModule(node);
        currentSourceFile = null;
        currentModuleInfo = null;
        return updated;
    }

    private bool ShouldEmitUnderscoreUnderscoreESModule()
    {
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(currentSourceFile!.FileName(), global::Ts.TsPath.TsPath.SupportedJSExtensionsFlat) &&
            currentSourceFile.CommonJSModuleIndicator is not null &&
            (currentSourceFile.ExternalModuleIndicator is null || currentSourceFile.ExternalModuleIndicator.Kind == SyntaxKind.SourceFile))
        {
            return false;
        }
        return currentModuleInfo!.ExportEquals is null && global::Ts.Ast.AstUtilities.IsExternalModule(currentSourceFile);
    }

    private Statement CreateUnderscoreUnderscoreESModule()
    {
        var statement = Factory().NewExpressionStatement(
            Factory().NewCallExpression(
                Factory().NewPropertyAccessExpression(Factory().NewIdentifier("Object"), null, Factory().NewIdentifier("defineProperty"), NodeFlags.None),
                null,
                null,
                Factory().NewNodeList([
                    Factory().NewIdentifier("exports"),
                    Factory().NewStringLiteral("__esModule", TokenFlags.None),
                    Factory().NewObjectLiteralExpression(Factory().NewNodeList([
                        Factory().NewPropertyAssignment(null, Factory().NewIdentifier("value"), null, null, Factory().NewTrueExpression()),
                    ]), false),
                ]),
                NodeFlags.None));
        EmitContext().SetEmitFlags(statement, global::Ts.Printer.EmitFlags.CustomPrologue);
        return statement;
    }

    private Node TransformCommonJSModule(SourceFile node)
    {
        EmitContext().StartVariableEnvironment();
        var prologue = Factory().SplitStandardPrologue(node.Statements!.Nodes, out var rest);
        List<Node> statements = new(prologue);
        var custom = Factory().SplitCustomPrologue(rest, out rest) ?? [];
        var (visitedCustom, _) = topLevelVisitor.VisitSlice(custom);
        statements.AddRange(visitedCustom ?? []);
        if (ShouldEmitUnderscoreUnderscoreESModule())
        {
            statements.Add(CreateUnderscoreUnderscoreESModule());
        }

        if (currentModuleInfo!.ExportedNames.Count > 0)
        {
            const int ChunkSize = 50;
            for (var i = 0; i < currentModuleInfo.ExportedNames.Count; i += ChunkSize)
            {
                Node right = Factory().NewVoidZeroExpression();
                var end = Math.Min(i + ChunkSize, currentModuleInfo.ExportedNames.Count);
                for (var j = i; j < end; j++)
                {
                    var nextId = currentModuleInfo.ExportedNames[j];
                    Node left;
                    if (nextId.Kind == SyntaxKind.StringLiteral)
                    {
                        left = Factory().NewElementAccessExpression(
                            Factory().NewIdentifier("exports"), null, Factory().NewStringLiteralFromNode(nextId), NodeFlags.None);
                    }
                    else
                    {
                        var name = nextId.Clone(Factory())!;
                        EmitContext().SetEmitFlags(name, global::Ts.Printer.EmitFlags.NoSourceMap | global::Ts.Printer.EmitFlags.NoComments);
                        left = Factory().NewPropertyAccessExpression(Factory().NewIdentifier("exports"), null, name, NodeFlags.None);
                    }
                    right = Factory().NewAssignmentExpression(left, right);
                }
                var statement = Factory().NewExpressionStatement(right);
                EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.CustomPrologue);
                statements.Add(statement);
            }
        }

        var exportedFunctionsStart = statements.Count;
        foreach (var function in currentModuleInfo.ExportedFunctions.Values())
        {
            AppendExportsOfClassOrFunctionDeclaration(statements, function);
        }
        for (var i = exportedFunctionsStart; i < statements.Count; i++)
        {
            EmitContext().AddEmitFlags(statements[i], global::Ts.Printer.EmitFlags.CustomPrologue);
        }

        var (visitedRest, _) = topLevelVisitor.VisitSlice(rest);
        statements.AddRange(visitedRest ?? []);
        AppendExportEqualsIfNeeded(statements);
        var merged = EmitContext().EndAndMergeVariableEnvironment(statements.ToArray()) ?? [];
        var statementList = Factory().NewNodeList(merged);
        statementList.Loc = node.Statements.Loc;
        var result = Factory().UpdateSourceFile(node, statementList, node.EndOfFileToken).AsSourceFile();
        EmitContext().AddEmitHelper(result.AsNode(), EmitContext().ReadEmitHelpers());

        var externalHelpersImportDeclaration = ExternalModuleInfoCollector.CreateExternalHelpersImportDeclarationIfNeeded(
            EmitContext(), result, compilerOptions, getEmitModuleFormatOfFile(TransformerAstAdapters.AsHasFileName(node)), false, false, false);
        if (externalHelpersImportDeclaration is not null)
        {
            prologue = Factory().SplitStandardPrologue(result.Statements!.Nodes, out rest);
            custom = Factory().SplitCustomPrologue(rest, out rest) ?? [];
            statements = new List<Node>(prologue.Length + custom.Length + rest.Length + 1);
            statements.AddRange(prologue);
            statements.AddRange(custom);
            if (topLevelVisitor.VisitNode(externalHelpersImportDeclaration) is { } helper)
            {
                statements.Add(helper);
            }
            statements.AddRange(rest);
            statementList = Factory().NewNodeList(statements.ToArray());
            statementList.Loc = result.Statements.Loc;
            result = Factory().UpdateSourceFile(result, statementList, node.EndOfFileToken).AsSourceFile();
        }
        return result.AsNode();
    }

    private void AppendExportEqualsIfNeeded(List<Node> statements)
    {
        if (currentModuleInfo!.ExportEquals is { } exportEquals && VisitExportEquals(exportEquals.AsExportAssignment()) is { } expressionResult)
        {
            var statement = Factory().NewExpressionStatement(
                Factory().NewAssignmentExpression(
                    Factory().NewPropertyAccessExpression(Factory().NewIdentifier("module"), null, Factory().NewIdentifier("exports"), NodeFlags.None),
                    expressionResult));
            EmitContext().AssignCommentAndSourceMapRanges(statement, exportEquals);
            EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoComments);
            statements.Add(statement);
        }
    }

    private Expression? VisitExportEquals(ExportAssignment node)
    {
        var grandparentNode = PushNode(node.AsNode());
        try
        {
            return Visitor().VisitNode(node.Expression);
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private void AppendExportsOfImportDeclaration(List<Node> statements, ImportDeclaration declaration)
    {
        if (currentModuleInfo!.ExportEquals is not null || declaration.ImportClause is null)
        {
            return;
        }
        Set<string> seen = new();
        if (declaration.ImportClause.Name() is not null)
        {
            AppendExportsOfDeclaration(statements, declaration.ImportClause, seen, false);
        }
        var namedBindings = declaration.ImportClause.AsImportClause().NamedBindings;
        if (namedBindings?.Kind == SyntaxKind.NamespaceImport)
        {
            AppendExportsOfDeclaration(statements, namedBindings, seen, false);
        }
        else if (namedBindings?.Kind == SyntaxKind.NamedImports)
        {
            foreach (var importBinding in namedBindings.Elements() ?? [])
            {
                AppendExportsOfDeclaration(statements, importBinding, seen, true);
            }
        }
    }

    private void AppendExportsOfVariableStatement(List<Node> statements, VariableStatement node) =>
        AppendExportsOfVariableDeclarationList(statements, node.DeclarationList!.AsVariableDeclarationList(), false);

    private void AppendExportsOfVariableDeclarationList(List<Node> statements, VariableDeclarationList node, bool isForInOrOfInitializer)
    {
        if (currentModuleInfo!.ExportEquals is not null)
        {
            return;
        }
        foreach (var declaration in node.Declarations!.Nodes)
        {
            AppendExportsOfBindingElement(statements, declaration, isForInOrOfInitializer);
        }
    }

    private void AppendExportsOfBindingElement(List<Node> statements, Node declaration, bool isForInOrOfInitializer)
    {
        if (currentModuleInfo!.ExportEquals is not null || declaration.Name() is null)
        {
            return;
        }
        if (global::Ts.Ast.AstUtilities.IsBindingPattern(declaration.Name()))
        {
            foreach (var element in declaration.Name()!.Elements() ?? [])
            {
                if (!global::Ts.Ast.NodeTests.IsOmittedExpression(element))
                {
                    AppendExportsOfBindingElement(statements, element, isForInOrOfInitializer);
                }
            }
        }
        else if (!Transformers.IsGeneratedIdentifier(EmitContext(), declaration.Name()) &&
            (!global::Ts.Ast.NodeTests.IsVariableDeclaration(declaration) || declaration.Initializer() is not null || isForInOrOfInitializer))
        {
            AppendExportsOfDeclaration(statements, declaration, null, false);
        }
    }

    private void AppendExportsOfClassOrFunctionDeclaration(List<Node> statements, Declaration declaration)
    {
        if (currentModuleInfo!.ExportEquals is not null)
        {
            return;
        }
        Set<string> seen = new();
        if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Export))
        {
            var exportName = global::Ts.Ast.AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Default)
                ? Factory().NewIdentifier("default")
                : Factory().GetDeclarationName(declaration);
            AppendExportStatement(statements, seen, exportName, Factory().GetLocalName(declaration), declaration.Loc, false, false);
        }
        if (declaration.Name() is not null)
        {
            AppendExportsOfDeclaration(statements, declaration, seen, false);
        }
    }

    private void AppendExportsOfDeclaration(List<Node> statements, Declaration declaration, Set<string>? seen, bool liveBinding)
    {
        if (currentModuleInfo!.ExportEquals is not null)
        {
            return;
        }
        seen ??= new Set<string>();
        var name = declaration.Name();
        if (currentModuleInfo.ExportSpecifiers.Len() > 0 && name is not null && global::Ts.Ast.NodeTests.IsIdentifier(name))
        {
            name = Factory().GetDeclarationName(declaration);
            var exportSpecifiers = currentModuleInfo.ExportSpecifiers.Get(name.Text());
            if (exportSpecifiers is not null)
            {
                var exportValue = VisitExpressionIdentifier(name);
                foreach (var exportSpecifier in exportSpecifiers)
                {
                    AppendExportStatement(
                        statements, seen, exportSpecifier.Name()!, exportValue, exportSpecifier.Name()!.Loc, false, liveBinding);
                }
            }
        }
    }

    private void AppendExportStatement(
        List<Node> statements,
        Set<string> seen,
        ModuleExportName exportName,
        Expression expression,
        TextRange? location,
        bool allowComments,
        bool liveBinding)
    {
        if (exportName.Kind != SyntaxKind.StringLiteral)
        {
            if (seen.Has(exportName.Text()))
            {
                return;
            }
            seen.Add(exportName.Text());
        }
        statements.Add(CreateExportStatement(exportName, expression, location, allowComments, liveBinding));
    }

    private Statement CreateExportStatement(ModuleExportName name, Expression value, TextRange? location, bool allowComments, bool liveBinding)
    {
        var statement = Factory().NewExpressionStatement(CreateExportExpression(name, value, null, liveBinding));
        if (location is { } range)
        {
            EmitContext().SetCommentRange(statement, range);
        }
        EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.StartOnNewLine);
        if (!allowComments)
        {
            EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoComments);
        }
        return statement;
    }

    private Expression CreateExportExpression(ModuleExportName name, Expression value, TextRange? location, bool liveBinding)
    {
        Node expression;
        if (liveBinding)
        {
            expression = Factory().NewCallExpression(
                Factory().NewPropertyAccessExpression(Factory().NewIdentifier("Object"), null, Factory().NewIdentifier("defineProperty"), NodeFlags.None),
                null,
                null,
                Factory().NewNodeList([
                    Factory().NewIdentifier("exports"),
                    Factory().NewStringLiteralFromNode(name),
                    Factory().NewObjectLiteralExpression(Factory().NewNodeList([
                        Factory().NewPropertyAssignment(null, Factory().NewIdentifier("enumerable"), null, null, Factory().NewTrueExpression()),
                        Factory().NewPropertyAssignment(
                            null,
                            Factory().NewIdentifier("get"),
                            null,
                            null,
                            Factory().NewFunctionExpression(
                                null, null, null, null, Factory().NewNodeList([]), null, null,
                                Factory().NewBlock(Factory().NewNodeList([Factory().NewReturnStatement(value)]), false))),
                    ]), false),
                ]),
                NodeFlags.None);
        }
        else
        {
            var left = name.Kind == SyntaxKind.StringLiteral
                ? Factory().NewElementAccessExpression(Factory().NewIdentifier("exports"), null, Factory().NewStringLiteralFromNode(name), NodeFlags.None)
                : Factory().NewPropertyAccessExpression(Factory().NewIdentifier("exports"), null, name.Clone(Factory()), NodeFlags.None);
            expression = Factory().NewAssignmentExpression(left, value);
        }
        if (location is { } range)
        {
            EmitContext().SetCommentRange(expression, range);
        }
        return expression;
    }

    private Node CreateRequireCall(Node node)
    {
        var moduleName = ModuleTransformUtilities.GetExternalModuleNameLiteral(
            Factory(), node, currentSourceFile!, null, null, compilerOptions);
        var arguments = moduleName is null
            ? Array.Empty<Node>()
            : new Node[] { ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), moduleName, compilerOptions)! };
        return Factory().NewCallExpression(Factory().NewIdentifier("require"), null, null, Factory().NewNodeList(arguments), NodeFlags.None);
    }

    private Expression GetHelperExpressionForExport(ExportDeclaration node, Expression innerExpression) =>
        ExternalModuleInfoCollector.GetExportNeedsImportStarHelper(node)
            ? Visitor().VisitNode(Factory().NewImportStarHelper(innerExpression))!
            : innerExpression;

    private Expression GetHelperExpressionForImport(ImportDeclaration node, Expression innerExpression)
    {
        if (ExternalModuleInfoCollector.GetImportNeedsImportStarHelper(node))
        {
            return Visitor().VisitNode(Factory().NewImportStarHelper(innerExpression))!;
        }
        if (ExternalModuleInfoCollector.GetImportNeedsImportDefaultHelper(node))
        {
            return Visitor().VisitNode(Factory().NewImportDefaultHelper(innerExpression))!;
        }
        return innerExpression;
    }

    private Node VisitTopLevelImportDeclaration(ImportDeclaration node)
    {
        if (node.ImportClause is null)
        {
            var statement = Factory().NewExpressionStatement(CreateRequireCall(node.AsNode()));
            EmitContext().SetOriginal(statement, node.AsNode());
            EmitContext().AssignCommentAndSourceMapRanges(statement, node.AsNode());
            return statement;
        }

        List<Node> statements = [];
        List<Node> variables = [];
        var namespaceDeclaration = TransformerAstAdapters.GetNamespaceDeclarationNode(node.AsNode());
        if (namespaceDeclaration is not null && !TransformerAstAdapters.IsDefaultImport(node.AsNode()))
        {
            variables.Add(Factory().NewVariableDeclaration(
                namespaceDeclaration.Name()!.Clone(Factory()), null, null,
                GetHelperExpressionForImport(node, CreateRequireCall(node.AsNode()))));
        }
        else
        {
            variables.Add(Factory().NewVariableDeclaration(
                Factory().NewGeneratedNameForNode(node.AsNode()), null, null,
                GetHelperExpressionForImport(node, CreateRequireCall(node.AsNode()))));
            if (namespaceDeclaration is not null && TransformerAstAdapters.IsDefaultImport(node.AsNode()))
            {
                variables.Add(Factory().NewVariableDeclaration(
                    namespaceDeclaration.Name()!.Clone(Factory()), null, null,
                    Factory().NewGeneratedNameForNode(node.AsNode())));
            }
        }
        var variableStatement = Factory().NewVariableStatement(
            null,
            Factory().NewVariableDeclarationList(Factory().NewNodeList(variables.ToArray()), NodeFlags.Const));
        EmitContext().SetOriginal(variableStatement, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(variableStatement, node.AsNode());
        statements.Add(variableStatement);
        AppendExportsOfImportDeclaration(statements, node);
        return Transformers.SingleOrMany(statements.ToArray(), Factory())!;
    }

    private Node VisitTopLevelImportEqualsDeclaration(ImportEqualsDeclaration node)
    {
        if (!TransformerAstAdapters.IsExternalModuleImportEqualsDeclaration(node.AsNode()))
        {
            throw new InvalidOperationException("import= for internal module references should be handled in an earlier transformer.");
        }
        List<Node> statements = [];
        if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export))
        {
            var statement = Factory().NewExpressionStatement(
                CreateExportExpression(node.Name()!, CreateRequireCall(node.AsNode()), node.Loc, false));
            EmitContext().SetOriginal(statement, node.AsNode());
            EmitContext().AssignCommentAndSourceMapRanges(statement, node.AsNode());
            statements.Add(statement);
        }
        else
        {
            var statement = Factory().NewVariableStatement(
                null,
                Factory().NewVariableDeclarationList(
                    Factory().NewNodeList([
                        Factory().NewVariableDeclaration(node.Name()!.Clone(Factory()), null, null, CreateRequireCall(node.AsNode())),
                    ]),
                    NodeFlags.Const));
            EmitContext().SetOriginal(statement, node.AsNode());
            EmitContext().AssignCommentAndSourceMapRanges(statement, node.AsNode());
            statements.Add(statement);
        }
        AppendExportsOfDeclaration(statements, node.AsNode(), null, false);
        return Transformers.SingleOrMany(statements.ToArray(), Factory())!;
    }

    private Node? VisitTopLevelExportDeclaration(ExportDeclaration node)
    {
        if (node.ModuleSpecifier is null)
        {
            return null;
        }
        var generatedName = Factory().NewGeneratedNameForNode(node.AsNode());
        if (node.ExportClause is not null && global::Ts.Ast.NodeTests.IsNamedExports(node.ExportClause))
        {
            List<Node> statements = [];
            var variableStatement = Factory().NewVariableStatement(
                null,
                Factory().NewVariableDeclarationList(
                    Factory().NewNodeList([
                        Factory().NewVariableDeclaration(generatedName, null, null, CreateRequireCall(node.AsNode())),
                    ]),
                    NodeFlags.None));
            EmitContext().SetOriginal(variableStatement, node.AsNode());
            EmitContext().AssignCommentAndSourceMapRanges(variableStatement, node.AsNode());
            statements.Add(variableStatement);
            foreach (var specifier in node.ExportClause.Elements() ?? [])
            {
                var specifierName = specifier.PropertyNameOrName()!;
                var target = global::Ts.Ast.AstUtilities.ModuleExportNameIsDefault(specifierName)
                    ? Factory().NewImportDefaultHelper(generatedName)
                    : generatedName;
                var exportName = global::Ts.Ast.NodeTests.IsStringLiteral(specifier.Name()!)
                    ? Factory().NewStringLiteralFromNode(specifier.Name()!)
                    : Factory().GetExportName(specifier);
                var exportedValue = global::Ts.Ast.NodeTests.IsStringLiteral(specifierName)
                    ? Factory().NewElementAccessExpression(target, null, specifierName, NodeFlags.None)
                    : Factory().NewPropertyAccessExpression(target, null, specifierName, NodeFlags.None);
                var statement = Factory().NewExpressionStatement(CreateExportExpression(exportName, exportedValue, null, true));
                EmitContext().SetOriginal(statement, specifier);
                EmitContext().AssignCommentAndSourceMapRanges(statement, specifier);
                statements.Add(statement);
            }
            return Transformers.SingleOrMany(statements.ToArray(), Factory());
        }
        if (node.ExportClause is not null)
        {
            var exportName = global::Ts.Ast.NodeTests.IsStringLiteral(node.ExportClause.Name()!)
                ? Factory().NewStringLiteralFromNode(node.ExportClause.Name()!)
                : node.ExportClause.Name()!.Clone(Factory())!;
            var statement = Factory().NewExpressionStatement(
                CreateExportExpression(exportName, GetHelperExpressionForExport(node, CreateRequireCall(node.AsNode())), null, false));
            EmitContext().SetOriginal(statement, node.AsNode());
            EmitContext().AssignCommentAndSourceMapRanges(statement, node.AsNode());
            return statement;
        }
        var exportStarStatement = Factory().NewExpressionStatement(
            Visitor().VisitNode(Factory().NewExportStarHelper(CreateRequireCall(node.AsNode()), Factory().NewIdentifier("exports")))!);
        EmitContext().SetOriginal(exportStarStatement, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(exportStarStatement, node.AsNode());
        return exportStarStatement;
    }

    private Node? VisitTopLevelExportAssignment(ExportAssignment node)
    {
        return node.IsExportEquals
            ? null
            : CreateExportStatement(Factory().NewIdentifier("default"), Visitor().VisitNode(node.Expression)!, node.Loc, true, false);
    }

    private Node VisitTopLevelFunctionDeclaration(FunctionDeclaration node)
    {
        return global::Ts.Ast.AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export)
            ? Factory().UpdateFunctionDeclaration(
                node,
                Transformers.ExtractModifiers(EmitContext(), node.Modifiers(), ~ModifierFlags.ExportDefault),
                node.AsteriskToken,
                Factory().GetDeclarationName(node.AsNode()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                Visitor().VisitNode(node.Body))
            : Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitTopLevelClassDeclaration(ClassDeclaration node)
    {
        List<Node> statements = [];
        statements.Add(global::Ts.Ast.AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export)
            ? Factory().UpdateClassDeclaration(
                node,
                Visitor().VisitModifiers(Transformers.ExtractModifiers(EmitContext(), node.Modifiers(), ~ModifierFlags.ExportDefault)),
                Factory().GetDeclarationName(node.AsNode()),
                null,
                Visitor().VisitNodes(node.HeritageClauses),
                Visitor().VisitNodes(node.Members))
            : Visitor().VisitEachChild(node.AsNode())!);
        AppendExportsOfClassOrFunctionDeclaration(statements, node.AsNode());
        return Transformers.SingleOrMany(statements.ToArray(), Factory())!;
    }

    private Node VisitTopLevelVariableStatement(VariableStatement node)
    {
        if (!global::Ts.Ast.AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export))
        {
            return VisitTopLevelNestedVariableStatement(node);
        }

        List<Node> statements = [];
        List<Node> variables = [];
        List<Node> expressions = [];
        ModifierList? modifiers = null;

        void CommitPendingVariables()
        {
            if (variables.Count == 0)
            {
                return;
            }
            var statement = Factory().UpdateVariableStatement(
                node,
                modifiers,
                Factory().UpdateVariableDeclarationList(
                    node.DeclarationList!.AsVariableDeclarationList(),
                    Factory().NewNodeList(variables.ToArray()),
                    node.DeclarationList.Flags));
            if (statements.Count > 0)
            {
                EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoComments);
            }
            statements.Add(statement);
            variables.Clear();
        }

        void CommitPendingExpressions()
        {
            if (expressions.Count == 0)
            {
                return;
            }
            var statement = Factory().NewExpressionStatement(Factory().InlineExpressions(expressions.ToArray())!);
            EmitContext().AssignCommentAndSourceMapRanges(statement, node.AsNode());
            if (statements.Count > 0)
            {
                EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoComments);
            }
            statements.Add(statement);
            expressions.Clear();
        }

        void PushVariable(Node variable)
        {
            CommitPendingExpressions();
            variables.Add(variable);
        }

        void PushExpression(Node expression)
        {
            CommitPendingVariables();
            expressions.Add(expression);
        }

        foreach (var variableNode in node.DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
        {
            var variable = variableNode.AsVariableDeclaration();
            if (global::Ts.Ast.NodeTests.IsIdentifier(variable.Name()!) && Transformers.IsLocalName(EmitContext(), variable.Name()!))
            {
                modifiers ??= Transformers.ExtractModifiers(EmitContext(), node.Modifiers(), ~ModifierFlags.ExportDefault);
                Node updatedVariable = variableNode;
                if (variable.Initializer is not null)
                {
                    updatedVariable = Factory().UpdateVariableDeclaration(
                        variable,
                        variable.Name(),
                        null,
                        null,
                        CreateExportExpression(variable.Name()!, Visitor().VisitNode(variable.Initializer)!, null, false));
                }
                PushVariable(updatedVariable);
            }
            else if (variable.Initializer is not null && !global::Ts.Ast.AstUtilities.IsBindingPattern(variable.Name()) &&
                (global::Ts.Ast.NodeTests.IsArrowFunction(variable.Initializer) ||
                global::Ts.Ast.NodeTests.IsFunctionExpression(variable.Initializer) ||
                global::Ts.Ast.NodeTests.IsClassExpression(variable.Initializer)))
            {
                PushVariable(Factory().NewVariableDeclaration(
                    variable.Name(), variable.ExclamationToken, variable.Type, Visitor().VisitNode(variable.Initializer)));
                var propertyAccess = Factory().NewPropertyAccessExpression(
                    Factory().NewIdentifier("exports"), null, variable.Name(), NodeFlags.None);
                EmitContext().AssignCommentAndSourceMapRanges(propertyAccess, variable.Name()!);
                PushExpression(Factory().NewAssignmentExpression(propertyAccess, variable.Name()!.Clone(Factory())!));
            }
            else if (global::Ts.Ast.NodeTests.IsIdentifier(variable.Name()!))
            {
                if (TransformInitializedVariable(variable) is { } expression)
                {
                    PushExpression(Visitor().VisitNode(expression)!);
                }
            }
            else if (global::Ts.Ast.AstUtilities.IsBindingPattern(variable.Name()))
            {
                if (TransformInitializedVariable(variable) is { } expression)
                {
                    PushExpression(expression);
                }
            }
            else if (Transformers.ConvertVariableDeclarationToAssignmentExpression(EmitContext(), variable) is { } expression)
            {
                PushExpression(Visitor().VisitNode(expression)!);
            }
        }
        CommitPendingVariables();
        CommitPendingExpressions();
        AppendExportsOfVariableStatement(statements, node);
        return Transformers.SingleOrMany(statements.ToArray(), Factory())!;
    }

    private Expression? TransformInitializedVariable(VariableDeclaration node)
    {
        if (node.Initializer is null)
        {
            return null;
        }
        var name = node.Name()!;
        if (global::Ts.Ast.AstUtilities.IsBindingPattern(name))
        {
            var assignment = Transformers.ConvertVariableDeclarationToAssignmentExpression(EmitContext(), node)!;
            var grandparentNode = PushNode(assignment);
            try
            {
                return VisitDestructuringAssignment(assignment.AsBinaryExpression(), true);
            }
            finally
            {
                PopNode(grandparentNode);
            }
        }
        var propertyAccess = Factory().NewPropertyAccessExpression(Factory().NewIdentifier("exports"), null, name, NodeFlags.None);
        EmitContext().AssignCommentAndSourceMapRanges(propertyAccess, name);
        return Factory().NewAssignmentExpression(propertyAccess, node.Initializer);
    }

    private Node VisitTopLevelNestedVariableStatement(VariableStatement node)
    {
        List<Node> statements = [Visitor().VisitEachChild(node.AsNode())!];
        AppendExportsOfVariableStatement(statements, node);
        return Transformers.SingleOrMany(statements.ToArray(), Factory())!;
    }

    private Node VisitTopLevelNestedForStatement(ForStatement node)
    {
        if (node.Initializer is not null && global::Ts.Ast.NodeTests.IsVariableDeclarationList(node.Initializer) &&
            (node.Initializer.Flags & NodeFlags.BlockScoped) == 0)
        {
            List<Node> exportStatements = [];
            AppendExportsOfVariableDeclarationList(exportStatements, node.Initializer.AsVariableDeclarationList(), false);
            if (exportStatements.Count > 0)
            {
                List<Node> statements = [];
                var declarationList = discardedValueVisitor.VisitNode(node.Initializer);
                statements.Add(Factory().NewVariableStatement(null, declarationList));
                statements.AddRange(exportStatements);
                statements.Add(Factory().UpdateForStatement(
                    node,
                    null,
                    Visitor().VisitNode(node.Condition),
                    discardedValueVisitor.VisitNode(node.Incrementor),
                    EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor)));
                return Transformers.SingleOrMany(statements.ToArray(), Factory())!;
            }
        }
        return Factory().UpdateForStatement(
            node,
            discardedValueVisitor.VisitNode(node.Initializer),
            Visitor().VisitNode(node.Condition),
            discardedValueVisitor.VisitNode(node.Incrementor),
            EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor));
    }

    private Node VisitTopLevelNestedForInOrOfStatement(ForInOrOfStatement node)
    {
        if (global::Ts.Ast.NodeTests.IsVariableDeclarationList(node.Initializer!) &&
            (node.Initializer!.Flags & NodeFlags.BlockScoped) == 0)
        {
            List<Node> exportStatements = [];
            AppendExportsOfVariableDeclarationList(exportStatements, node.Initializer.AsVariableDeclarationList(), true);
            if (exportStatements.Count > 0)
            {
                var initializer = discardedValueVisitor.VisitNode(node.Initializer);
                var expression = Visitor().VisitNode(node.Expression);
                var body = EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor)!;
                if (global::Ts.Ast.NodeTests.IsBlock(body))
                {
                    var block = body.AsBlock();
                    var bodyStatements = new List<Node>(exportStatements);
                    bodyStatements.AddRange(block.Statements?.Nodes ?? []);
                    var bodyStatementList = Factory().NewNodeList(bodyStatements.ToArray());
                    bodyStatementList.Loc = block.Statements!.Loc;
                    body = Factory().UpdateBlock(block, bodyStatementList, block.MultiLine);
                }
                else
                {
                    exportStatements.Add(body);
                    body = Factory().NewBlock(Factory().NewNodeList(exportStatements.ToArray()), true);
                }
                return Factory().UpdateForInOrOfStatement(node, node.AwaitModifier, initializer, expression, body);
            }
        }
        return Factory().UpdateForInOrOfStatement(
            node,
            node.AwaitModifier,
            discardedValueVisitor.VisitNode(node.Initializer),
            Visitor().VisitNode(node.Expression),
            EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor));
    }

    private Node VisitTopLevelNestedDoStatement(DoStatement node) =>
        Factory().UpdateDoStatement(node, EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor), Visitor().VisitNode(node.Expression));

    private Node VisitTopLevelNestedWhileStatement(WhileStatement node) =>
        Factory().UpdateWhileStatement(node, Visitor().VisitNode(node.Expression), EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor));

    private Node VisitTopLevelNestedLabeledStatement(LabeledStatement node)
    {
        var statement = topLevelNestedVisitor.VisitEmbeddedStatement(node.Statement) ?? Factory().NewEmptyStatement();
        return Factory().UpdateLabeledStatement(node, node.Label, statement);
    }

    private Node VisitTopLevelNestedWithStatement(WithStatement node) =>
        Factory().UpdateWithStatement(node, Visitor().VisitNode(node.Expression), topLevelNestedVisitor.VisitEmbeddedStatement(node.Statement));

    private Node VisitTopLevelNestedIfStatement(IfStatement node)
    {
        var thenStatement = topLevelNestedVisitor.VisitEmbeddedStatement(node.ThenStatement) ??
            Factory().NewBlock(Factory().NewNodeList([]), false);
        return Factory().UpdateIfStatement(
            node,
            Visitor().VisitNode(node.Expression),
            thenStatement,
            topLevelNestedVisitor.VisitEmbeddedStatement(node.ElseStatement));
    }

    private Node VisitTopLevelNestedSwitchStatement(SwitchStatement node) =>
        Factory().UpdateSwitchStatement(node, Visitor().VisitNode(node.Expression), topLevelNestedVisitor.VisitNode(node.CaseBlock));

    private Node VisitTopLevelNestedCaseBlock(CaseBlock node) => topLevelNestedVisitor.VisitEachChild(node.AsNode())!;

    private Node VisitTopLevelNestedCaseOrDefaultClause(CaseOrDefaultClause node) =>
        Factory().UpdateCaseOrDefaultClause(node, Visitor().VisitNode(node.Expression), topLevelNestedVisitor.VisitNodes(node.Statements));

    private Node VisitTopLevelNestedTryStatement(TryStatement node) => topLevelNestedVisitor.VisitEachChild(node.AsNode())!;

    private Node VisitTopLevelNestedCatchClause(CatchClause node) =>
        Factory().UpdateCatchClause(node, node.VariableDeclaration, topLevelNestedVisitor.VisitNode(node.Block));

    private Node VisitTopLevelNestedBlock(Block node) => topLevelNestedVisitor.VisitEachChild(node.AsNode())!;

    private Node VisitForStatement(ForStatement node) =>
        Factory().UpdateForStatement(
            node,
            discardedValueVisitor.VisitNode(node.Initializer),
            Visitor().VisitNode(node.Condition),
            discardedValueVisitor.VisitNode(node.Incrementor),
            EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor));

    private Node VisitForInOrOfStatement(ForInOrOfStatement node) =>
        Factory().UpdateForInOrOfStatement(
            node,
            node.AwaitModifier,
            discardedValueVisitor.VisitNode(node.Initializer),
            Visitor().VisitNode(node.Expression),
            EmitContext().VisitIterationBody(node.Statement, topLevelNestedVisitor));

    private Node VisitExpressionStatement(ExpressionStatement node) => discardedValueVisitor.VisitEachChild(node.AsNode())!;

    private Node VisitVoidExpression(VoidExpression node) => discardedValueVisitor.VisitEachChild(node.AsNode())!;

    private Node VisitParenthesizedExpression(ParenthesizedExpression node, bool resultIsDiscarded)
    {
        var visitor = resultIsDiscarded ? discardedValueVisitor : Visitor();
        return Factory().UpdateParenthesizedExpression(node, visitor.VisitNode(node.Expression));
    }

    private Node VisitPartiallyEmittedExpression(PartiallyEmittedExpression node, bool resultIsDiscarded)
    {
        var visitor = resultIsDiscarded ? discardedValueVisitor : Visitor();
        return Factory().UpdatePartiallyEmittedExpression(node, visitor.VisitNode(node.Expression));
    }

    private Node VisitBinaryExpression(BinaryExpression node, bool resultIsDiscarded)
    {
        if (global::Ts.Ast.AstUtilities.IsDestructuringAssignment(node.AsNode()))
        {
            return VisitDestructuringAssignment(node, resultIsDiscarded);
        }
        if (global::Ts.Ast.AstUtilities.IsAssignmentExpression(node.AsNode(), false))
        {
            return VisitAssignmentExpression(node);
        }
        if (global::Ts.Ast.AstUtilities.IsCommaExpression(node.AsNode()))
        {
            return VisitCommaExpression(node, resultIsDiscarded);
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitAssignmentExpression(BinaryExpression node)
    {
        var left = node.Left!;
        if (global::Ts.Ast.NodeTests.IsIdentifier(left) &&
            (!Transformers.IsGeneratedIdentifier(EmitContext(), left) || ModuleTransformUtilities.IsFileLevelReservedGeneratedIdentifier(EmitContext(), left)) &&
            !Transformers.IsLocalName(EmitContext(), left))
        {
            var exportedNames = GetExports(left);
            if (exportedNames.Length > 0)
            {
                var expression = Visitor().VisitEachChild(node.AsNode())!;
                foreach (var exportName in exportedNames)
                {
                    expression = CreateExportExpression(exportName, expression, node.Loc, false);
                }
                return expression;
            }
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitDestructuringAssignment(BinaryExpression node, bool valueIsDiscarded)
    {
        return DestructuringNeedsFlattening(node.Left!)
            ? Transformers.FlattenDestructuringAssignment(this, node.AsNode(), !valueIsDiscarded, FlattenLevel.All, CreateAllExportExpressions)
            : Visitor().VisitEachChild(node.AsNode())!;
    }

    private bool DestructuringNeedsFlattening(Node node)
    {
        if (global::Ts.Ast.NodeTests.IsObjectLiteralExpression(node))
        {
            foreach (var element in node.Properties() ?? [])
            {
                switch (element.Kind)
                {
                    case SyntaxKind.PropertyAssignment:
                        if (DestructuringNeedsFlattening(element.Initializer()!))
                        {
                            return true;
                        }
                        break;
                    case SyntaxKind.ShorthandPropertyAssignment:
                        if (DestructuringNeedsFlattening(element.Name()!))
                        {
                            return true;
                        }
                        break;
                    case SyntaxKind.SpreadAssignment:
                        if (DestructuringNeedsFlattening(element.Expression()!))
                        {
                            return true;
                        }
                        break;
                    case SyntaxKind.MethodDeclaration:
                    case SyntaxKind.GetAccessor:
                    case SyntaxKind.SetAccessor:
                        return false;
                }
            }
        }
        else if (global::Ts.Ast.NodeTests.IsArrayLiteralExpression(node))
        {
            foreach (var element in node.AsArrayLiteralExpression().Elements!.Nodes)
            {
                if (global::Ts.Ast.NodeTests.IsSpreadElement(element))
                {
                    if (DestructuringNeedsFlattening(element.Expression()!))
                    {
                        return true;
                    }
                }
                else if (DestructuringNeedsFlattening(element))
                {
                    return true;
                }
            }
        }
        else if (global::Ts.Ast.NodeTests.IsIdentifier(node))
        {
            var exportedNames = GetExports(node);
            if (Transformers.IsExportName(EmitContext(), node))
            {
                return exportedNames.Length > 1;
            }
            if (exportedNames.Length == 0)
            {
                return false;
            }
            if (exportedNames.Length == 1 && IsDirectExport(node) &&
                string.Equals(exportedNames[0].Text(), node.Text(), StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }
        return false;
    }

    private Expression CreateAllExportExpressions(IdentifierNode name, Expression value, TextRange location)
    {
        var exportedNames = GetExports(name);
        if (exportedNames.Length > 0)
        {
            Node expression;
            if (IsDirectExport(name))
            {
                var exportName = name.Clone(Factory())!;
                EmitContext().AddEmitFlags(exportName, global::Ts.Printer.EmitFlags.NoComments | global::Ts.Printer.EmitFlags.NoSourceMap);
                var propertyAccess = Factory().NewPropertyAccessExpression(Factory().NewIdentifier("exports"), null, exportName, NodeFlags.None);
                EmitContext().AddEmitFlags(propertyAccess, global::Ts.Printer.EmitFlags.NoComments);
                expression = Factory().NewAssignmentExpression(propertyAccess, value);
                EmitContext().AssignCommentAndSourceMapRanges(expression, name);
            }
            else
            {
                expression = Factory().NewAssignmentExpression(name, value);
            }
            foreach (var exportName in exportedNames)
            {
                expression = CreateExportExpression(exportName, expression, location, false);
            }
            return expression;
        }
        if (IsDirectExport(name))
        {
            var exportName = name.Clone(Factory())!;
            EmitContext().AddEmitFlags(exportName, global::Ts.Printer.EmitFlags.NoComments | global::Ts.Printer.EmitFlags.NoSourceMap);
            var propertyAccess = Factory().NewPropertyAccessExpression(Factory().NewIdentifier("exports"), null, exportName, NodeFlags.None);
            EmitContext().AddEmitFlags(propertyAccess, global::Ts.Printer.EmitFlags.NoComments);
            var result = Factory().NewAssignmentExpression(propertyAccess, value);
            EmitContext().AssignCommentAndSourceMapRanges(result, name);
            return result;
        }
        return Factory().NewAssignmentExpression(name, value);
    }

    private bool IsDirectExport(IdentifierNode name)
    {
        var exportContainer = resolver.GetReferencedExportContainer(EmitContext().MostOriginal(name)!, false);
        return exportContainer is not null && global::Ts.Ast.NodeTests.IsSourceFile(exportContainer);
    }

    private Node VisitAssignmentProperty(PropertyAssignment node) =>
        Factory().UpdatePropertyAssignment(
            node, null, Visitor().VisitNode(node.Name()), null, null, assignmentPatternVisitor.VisitNode(node.Initializer));

    private Node VisitShorthandAssignmentProperty(ShorthandPropertyAssignment node)
    {
        var target = VisitDestructuringAssignmentTargetNoStack(node.Name()!);
        if (global::Ts.Ast.NodeTests.IsIdentifier(target))
        {
            return Factory().UpdateShorthandPropertyAssignment(
                node, null, target, null, null, node.EqualsToken, Visitor().VisitNode(node.ObjectAssignmentInitializer));
        }
        if (node.ObjectAssignmentInitializer is not null)
        {
            var equalsToken = node.EqualsToken ?? Factory().NewToken(SyntaxKind.EqualsToken);
            target = Factory().NewBinaryExpression(null, target, null, equalsToken, Visitor().VisitNode(node.ObjectAssignmentInitializer));
        }
        var updated = Factory().NewPropertyAssignment(null, node.Name(), null, null, target);
        EmitContext().SetOriginal(updated, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(updated, node.AsNode());
        return updated;
    }

    private Node VisitAssignmentRestProperty(SpreadAssignment node) =>
        Factory().UpdateSpreadAssignment(node, VisitDestructuringAssignmentTarget(node.Expression!));

    private Node VisitAssignmentRestElement(SpreadElement node) =>
        Factory().UpdateSpreadElement(node, VisitDestructuringAssignmentTarget(node.Expression!));

    private Node VisitAssignmentElement(Node node)
    {
        if (global::Ts.Ast.NodeTests.IsBinaryExpression(node))
        {
            var binary = node.AsBinaryExpression();
            if (binary.OperatorToken!.Kind == SyntaxKind.EqualsToken)
            {
                return Factory().UpdateBinaryExpression(
                    binary, null, VisitDestructuringAssignmentTarget(binary.Left!), null, binary.OperatorToken, Visitor().VisitNode(binary.Right));
            }
        }
        return VisitDestructuringAssignmentTargetNoStack(node);
    }

    private Node VisitDestructuringAssignmentTarget(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return node.Kind is SyntaxKind.ObjectLiteralExpression or SyntaxKind.ArrayLiteralExpression
                ? VisitAssignmentPatternNoStack(node)!
                : VisitDestructuringAssignmentTargetNoStack(node);
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    private Node VisitDestructuringAssignmentTargetNoStack(Node node)
    {
        if (global::Ts.Ast.NodeTests.IsIdentifier(node) &&
            (!Transformers.IsGeneratedIdentifier(EmitContext(), node) || ModuleTransformUtilities.IsFileLevelReservedGeneratedIdentifier(EmitContext(), node)) &&
            !Transformers.IsLocalName(EmitContext(), node))
        {
            Node expression = VisitExpressionIdentifier(node);
            var exportedNames = GetExports(node);
            if (exportedNames.Length > 0)
            {
                var value = Factory().NewUniqueNameEx("value", new global::Ts.Printer.AutoGenerateOptions
                {
                    Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic,
                });
                expression = Factory().NewAssignmentExpression(expression, value);
                foreach (var exportName in exportedNames)
                {
                    expression = CreateExportExpression(exportName, expression, null, false);
                }
                var statement = Factory().NewExpressionStatement(expression);
                var parameter = Factory().NewParameterDeclaration(null, null, value, null, null, null);
                var valueSetter = Factory().NewSetAccessorDeclaration(
                    null,
                    Factory().NewIdentifier("value"),
                    null,
                    Factory().NewNodeList([parameter]),
                    null,
                    null,
                    Factory().NewBlock(Factory().NewNodeList([statement]), false));
                expression = Factory().NewObjectLiteralExpression(Factory().NewNodeList([valueSetter]), false);
                expression = Factory().NewPropertyAccessExpression(expression, null, Factory().NewIdentifier("value"), NodeFlags.None);
            }
            return expression;
        }
        return VisitNoStack(node, false)!;
    }

    private Node VisitCommaExpression(BinaryExpression node, bool resultIsDiscarded)
    {
        var rightVisitor = resultIsDiscarded ? discardedValueVisitor : Visitor();
        return Factory().UpdateBinaryExpression(
            node, null, discardedValueVisitor.VisitNode(node.Left), null, node.OperatorToken, rightVisitor.VisitNode(node.Right));
    }

    private Node VisitPrefixUnaryExpression(PrefixUnaryExpression node, bool resultIsDiscarded)
    {
        _ = resultIsDiscarded;
        if (node.Operator is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken &&
            global::Ts.Ast.NodeTests.IsIdentifier(node.Operand!) && !Transformers.IsLocalName(EmitContext(), node.Operand!))
        {
            var exportedNames = GetExports(node.Operand!);
            if (exportedNames.Length > 0)
            {
                Node expression = Factory().UpdatePrefixUnaryExpression(node, node.Operator, Visitor().VisitNode(node.Operand));
                foreach (var exportName in exportedNames)
                {
                    expression = CreateExportExpression(exportName, expression, null, false);
                    EmitContext().AssignCommentAndSourceMapRanges(expression, node.AsNode());
                }
                return expression;
            }
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitPostfixUnaryExpression(PostfixUnaryExpression node, bool resultIsDiscarded)
    {
        if (node.Operator is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken &&
            global::Ts.Ast.NodeTests.IsIdentifier(node.Operand!) && !Transformers.IsLocalName(EmitContext(), node.Operand!))
        {
            var exportedNames = GetExports(node.Operand!);
            if (exportedNames.Length > 0)
            {
                Node? temp = null;
                Node expression = Factory().UpdatePostfixUnaryExpression(node, Visitor().VisitNode(node.Operand), node.Operator);
                if (!resultIsDiscarded)
                {
                    temp = Factory().NewTempVariable();
                    EmitContext().AddVariableDeclaration(temp);
                    expression = Factory().NewAssignmentExpression(temp, expression);
                    EmitContext().AssignCommentAndSourceMapRanges(expression, node.AsNode());
                }
                expression = Factory().NewCommaExpression(expression, node.Operand!.Clone(Factory())!);
                EmitContext().AssignCommentAndSourceMapRanges(expression, node.AsNode());
                foreach (var exportName in exportedNames)
                {
                    expression = CreateExportExpression(exportName, expression, null, false);
                    EmitContext().AssignCommentAndSourceMapRanges(expression, node.AsNode());
                }
                if (temp is not null)
                {
                    expression = Factory().NewCommaExpression(expression, temp);
                    EmitContext().AssignCommentAndSourceMapRanges(expression, node.AsNode());
                }
                return expression;
            }
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitCallExpression(CallExpression node)
    {
        var needsRewrite = false;
        if (compilerOptions.RewriteRelativeImportExtensions.IsTrue() &&
            ((TransformerAstAdapters.IsImportCall(node.AsNode()) && node.Arguments!.Nodes.Length > 0) ||
            (global::Ts.Ast.AstUtilities.IsInJSFile(node.AsNode()) && global::Ts.Ast.AstUtilities.IsRequireCall(node.AsNode(), false))))
        {
            needsRewrite = true;
        }
        if (TransformerAstAdapters.IsImportCall(node.AsNode()) && ShouldTransformImportCall())
        {
            return VisitImportCallExpression(node, needsRewrite);
        }
        if (needsRewrite)
        {
            return ShimOrRewriteImportOrRequireCall(node);
        }
        if (global::Ts.Ast.NodeTests.IsIdentifier(node.Expression!))
        {
            var expression = VisitExpressionIdentifier(node.Expression!);
            var updated = Factory().UpdateCallExpression(
                node, expression, node.QuestionDotToken, null, Visitor().VisitNodes(node.Arguments), node.Flags);
            if (!global::Ts.Ast.NodeTests.IsIdentifier(expression) && !Transformers.IsHelperName(EmitContext(), node.Expression!))
            {
                EmitContext().AddEmitFlags(updated, global::Ts.Printer.EmitFlags.IndirectCall);
            }
            return updated;
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private bool ShouldTransformImportCall() => TransformerAstAdapters.ShouldTransformImportCall(
        currentSourceFile!.FileName(), compilerOptions,
        getEmitModuleFormatOfFile(TransformerAstAdapters.AsHasFileName(currentSourceFile)));

    private Node VisitImportCallExpression(CallExpression node, bool rewriteOrShim)
    {
        if (moduleKind == ModuleKind.None && languageVersion >= ScriptTarget.ES2020)
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }
        var externalModuleName = ModuleTransformUtilities.GetExternalModuleNameLiteral(
            Factory(), node.AsNode(), currentSourceFile!, null, null, compilerOptions);
        var firstArgument = Visitor().VisitNode(global::Ts.Core.CoreUtil.FirstOrNil(node.Arguments!.Nodes));
        Expression? argument;
        if (externalModuleName is not null &&
            (firstArgument is null || !global::Ts.Ast.NodeTests.IsStringLiteral(firstArgument) ||
            !string.Equals(firstArgument.Text(), externalModuleName.Text(), StringComparison.Ordinal)))
        {
            argument = externalModuleName;
        }
        else if (firstArgument is not null && rewriteOrShim)
        {
            argument = global::Ts.Ast.NodeTests.IsStringLiteral(firstArgument)
                ? ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), firstArgument, compilerOptions)
                : Factory().NewRewriteRelativeImportExtensionsHelper(firstArgument, compilerOptions.Jsx == JsxEmit.Preserve);
        }
        else
        {
            argument = firstArgument;
        }
        return CreateImportCallExpressionCommonJS(argument);
    }

    private Expression CreateImportCallExpressionCommonJS(Expression? argument)
    {
        var needSyncEval = argument is not null && !ModuleTransformUtilities.IsSimpleInlineableExpression(argument);
        Node[] promiseResolveArguments = needSyncEval
            ? [Factory().NewTemplateExpression(
                Factory().NewTemplateHead("", "", TokenFlags.None),
                Factory().NewNodeList([
                    Factory().NewTemplateSpan(argument, Factory().NewTemplateTail("", "", TokenFlags.None)),
                ]))]
            : [];
        var promiseResolveCall = Factory().NewCallExpression(
            Factory().NewPropertyAccessExpression(Factory().NewIdentifier("Promise"), null, Factory().NewIdentifier("resolve"), NodeFlags.None),
            null, null, Factory().NewNodeList(promiseResolveArguments), NodeFlags.None);
        Node[] requireArguments = needSyncEval ? [Factory().NewIdentifier("s")] : argument is null ? [] : [argument];
        var requireCall = Factory().NewImportStarHelper(
            Factory().NewCallExpression(Factory().NewIdentifier("require"), null, null, Factory().NewNodeList(requireArguments), NodeFlags.None));
        Node[] parameters = needSyncEval
            ? [Factory().NewParameterDeclaration(null, null, Factory().NewIdentifier("s"), null, null, null)]
            : [];
        var function = Factory().NewArrowFunction(
            null, null, Factory().NewNodeList(parameters), null, null, Factory().NewToken(SyntaxKind.EqualsGreaterThanToken), requireCall);
        return Factory().NewCallExpression(
            Factory().NewPropertyAccessExpression(promiseResolveCall, null, Factory().NewIdentifier("then"), NodeFlags.None),
            null, null, Factory().NewNodeList([function]), NodeFlags.None);
    }

    private Node ShimOrRewriteImportOrRequireCall(CallExpression node)
    {
        var expression = Visitor().VisitNode(node.Expression);
        var argumentsList = node.Arguments;
        if (node.Arguments!.Nodes.Length > 0)
        {
            var firstArgument = node.Arguments.Nodes[0];
            var firstArgumentChanged = false;
            if (global::Ts.Ast.AstUtilities.IsStringLiteralLike(firstArgument))
            {
                var rewritten = ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), firstArgument, compilerOptions)!;
                firstArgumentChanged = rewritten != firstArgument;
                firstArgument = rewritten;
            }
            else
            {
                firstArgument = Factory().NewRewriteRelativeImportExtensionsHelper(firstArgument, compilerOptions.Jsx == JsxEmit.Preserve);
                firstArgumentChanged = true;
            }
            var (rest, restChanged) = Visitor().VisitSlice(node.Arguments.Nodes[1..]);
            if (firstArgumentChanged || restChanged)
            {
                var arguments = new Node[(rest?.Length ?? 0) + 1];
                arguments[0] = firstArgument;
                if (rest is not null)
                {
                    Array.Copy(rest, 0, arguments, 1, rest.Length);
                }
                argumentsList = Factory().NewNodeList(arguments);
                argumentsList.Loc = node.Arguments.Loc;
            }
        }
        return Factory().UpdateCallExpression(node, expression, node.QuestionDotToken, null, argumentsList, node.Flags);
    }

    private Node VisitTaggedTemplateExpression(TaggedTemplateExpression node)
    {
        if (global::Ts.Ast.NodeTests.IsIdentifier(node.Tag!))
        {
            var expression = VisitExpressionIdentifier(node.Tag!);
            var updated = Factory().UpdateTaggedTemplateExpression(
                node, expression, null, null, Visitor().VisitNode(node.Template), node.Flags);
            if (!global::Ts.Ast.NodeTests.IsIdentifier(expression) && !Transformers.IsHelperName(EmitContext(), node.Tag!))
            {
                EmitContext().AddEmitFlags(updated, global::Ts.Printer.EmitFlags.IndirectCall);
            }
            return updated;
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitShorthandPropertyAssignment(ShorthandPropertyAssignment node)
    {
        var name = node.Name()!;
        var exportedOrImportedName = VisitExpressionIdentifier(name);
        if (exportedOrImportedName != name)
        {
            Node expression = exportedOrImportedName;
            if (node.ObjectAssignmentInitializer is not null)
            {
                expression = Factory().NewAssignmentExpression(expression, Visitor().VisitNode(node.ObjectAssignmentInitializer)!);
            }
            var assignment = Factory().NewPropertyAssignment(null, name, null, null, expression);
            assignment.Loc = node.Loc;
            EmitContext().AssignCommentAndSourceMapRanges(assignment, node.AsNode());
            return assignment;
        }
        return Factory().UpdateShorthandPropertyAssignment(
            node, null, exportedOrImportedName, null, null, node.EqualsToken, Visitor().VisitNode(node.ObjectAssignmentInitializer));
    }

    private Node VisitIdentifier(IdentifierNode node) =>
        parentNode is not null && Transformers.IsIdentifierReference(node, parentNode) ? VisitExpressionIdentifier(node) : node;

    private Node VisitExpressionIdentifier(IdentifierNode node)
    {
        var info = EmitContext().GetAutoGenerateInfo(node);
        if (!(info is not null && (info.Flags & global::Ts.Printer.GeneratedIdentifierFlags.AllowNameSubstitution) == 0) &&
            !Transformers.IsHelperName(EmitContext(), node) &&
            !Transformers.IsLocalName(EmitContext(), node) &&
            !ModuleTransformUtilities.IsDeclarationNameOfEnumOrNamespace(EmitContext(), node))
        {
            var original = EmitContext().MostOriginal(node)!;
            var exportContainer = resolver.GetReferencedExportContainer(original, Transformers.IsExportName(EmitContext(), node));
            if (exportContainer is not null && global::Ts.Ast.NodeTests.IsSourceFile(exportContainer))
            {
                var reference = Factory().NewPropertyAccessExpression(
                    Factory().NewIdentifier("exports"), null, node.Clone(Factory()), NodeFlags.None);
                EmitContext().AssignCommentAndSourceMapRanges(reference, node);
                reference.Loc = node.Loc;
                return reference;
            }
            var importDeclaration = resolver.GetReferencedImportDeclaration(original);
            if (importDeclaration is not null)
            {
                if (global::Ts.Ast.NodeTests.IsImportClause(importDeclaration))
                {
                    var reference = Factory().NewPropertyAccessExpression(
                        Factory().NewGeneratedNameForNode(importDeclaration.Parent!), null, Factory().NewIdentifier("default"), NodeFlags.None);
                    EmitContext().AssignCommentAndSourceMapRanges(reference, node);
                    reference.Loc = node.Loc;
                    return reference;
                }
                if (global::Ts.Ast.NodeTests.IsImportSpecifier(importDeclaration))
                {
                    var name = importDeclaration.AsImportSpecifier().PropertyNameOrName()!;
                    var declaration = global::Ts.Ast.AstUtilities.FindAncestor(importDeclaration, global::Ts.Ast.NodeTests.IsImportDeclaration);
                    var target = Factory().NewGeneratedNameForNode(declaration ?? importDeclaration);
                    Node reference;
                    if (global::Ts.Ast.NodeTests.IsStringLiteral(name))
                    {
                        reference = Factory().NewElementAccessExpression(target, null, Factory().NewStringLiteralFromNode(name), NodeFlags.None);
                    }
                    else
                    {
                        var referenceName = name.Clone(Factory())!;
                        EmitContext().AddEmitFlags(referenceName, global::Ts.Printer.EmitFlags.NoSourceMap | global::Ts.Printer.EmitFlags.NoComments);
                        reference = Factory().NewPropertyAccessExpression(target, null, referenceName, NodeFlags.None);
                    }
                    EmitContext().AssignCommentAndSourceMapRanges(reference, node);
                    reference.Loc = node.Loc;
                    return reference;
                }
            }
        }
        return node;
    }

    private Node[] GetExports(IdentifierNode name)
    {
        if (!Transformers.IsGeneratedIdentifier(EmitContext(), name))
        {
            var original = EmitContext().MostOriginal(name)!;
            var importDeclaration = resolver.GetReferencedImportDeclaration(original);
            if (importDeclaration is not null)
            {
                return currentModuleInfo!.ExportedBindings.Get(importDeclaration)?.ToArray() ?? [];
            }
            Set<Node> bindingsSet = new();
            List<Node> bindings = [];
            var declarations = resolver.GetReferencedValueDeclarations(original);
            if (declarations is not null)
            {
                foreach (var declaration in declarations)
                {
                    foreach (var binding in currentModuleInfo!.ExportedBindings.Get(declaration) ?? [])
                    {
                        if (!bindingsSet.Has(binding))
                        {
                            bindingsSet.Add(binding);
                            bindings.Add(binding);
                        }
                    }
                }
                return bindings.ToArray();
            }
        }
        else if (ModuleTransformUtilities.IsFileLevelReservedGeneratedIdentifier(EmitContext(), name))
        {
            var exportSpecifiers = currentModuleInfo!.ExportSpecifiers.Get(name.Text());
            if (exportSpecifiers is not null)
            {
                var exportedNames = new Node[exportSpecifiers.Count];
                for (var i = 0; i < exportSpecifiers.Count; i++)
                {
                    exportedNames[i] = exportSpecifiers[i].Name()!;
                }
                return exportedNames;
            }
        }
        return [];
    }
}
