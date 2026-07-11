// Ported from tsgo internal/transformers/moduletransforms/esmodule.go.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Binder;
using Ts.Core;

namespace Ts.Transformers.ModuleTransforms;

public sealed class ESModuleTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;
    private readonly IReferenceResolver resolver;
    private readonly Func<HasFileName, ModuleKind> getEmitModuleFormatOfFile;
    private SourceFile? currentSourceFile;
    private ImportRequireStatements? importRequireStatements;
    private readonly Dictionary<string, IdentifierNode> helperNameSubstitutions = new(StringComparer.Ordinal);

    private sealed class ImportRequireStatements
    {
        public required Node[] Statements;
        public required IdentifierNode RequireHelperName;
    }

    private ESModuleTransformer(TransformOptions options)
    {
        compilerOptions = options.CompilerOptions!;
        resolver = options.Resolver!;
        getEmitModuleFormatOfFile = options.GetEmitModuleFormatOfFile!;
        NewTransformer(Visit, options.Context);
    }

    public static Transformer NewESModuleTransformer(TransformOptions options) => new ESModuleTransformer(options);

    // Visits source elements that are not top-level or top-level nested statements.
    private Node? Visit(Node node)
    {
        return node.Kind switch
        {
            SyntaxKind.SourceFile => VisitSourceFile(node.AsSourceFile()),
            SyntaxKind.ImportDeclaration => VisitImportDeclaration(node.AsImportDeclaration()),
            SyntaxKind.ImportEqualsDeclaration => VisitImportEqualsDeclaration(node.AsImportEqualsDeclaration()),
            SyntaxKind.ExportAssignment => VisitExportAssignment(node.AsExportAssignment()),
            SyntaxKind.ExportDeclaration => VisitExportDeclaration(node.AsExportDeclaration()),
            SyntaxKind.CallExpression => VisitCallExpression(node.AsCallExpression()),
            _ => Visitor().VisitEachChild(node),
        };
    }

    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile || !(global::Ts.Ast.AstUtilities.IsExternalModule(node) || compilerOptions.GetIsolatedModules()))
        {
            return node.AsNode();
        }

        currentSourceFile = node;
        importRequireStatements = null;

        var result = Visitor().VisitEachChild(node.AsNode())!.AsSourceFile();
        EmitContext().AddEmitHelper(result.AsNode(), EmitContext().ReadEmitHelpers());

        var externalHelpersImportDeclaration = ExternalModuleInfoCollector.CreateExternalHelpersImportDeclarationIfNeeded(
            EmitContext(),
            result,
            compilerOptions,
            getEmitModuleFormatOfFile(TransformerAstAdapters.AsHasFileName(node)),
            false,
            false,
            false);
        if (externalHelpersImportDeclaration is not null || importRequireStatements is not null)
        {
            var prologue = Factory().SplitStandardPrologue(result.Statements!.Nodes, out var restAfterStandard);
            var custom = Factory().SplitCustomPrologue(restAfterStandard, out var rest) ?? [];
            List<Node> statements = new(prologue.Length + custom.Length + rest.Length + 3);
            statements.AddRange(prologue);
            statements.AddRange(custom);
            if (externalHelpersImportDeclaration is not null)
            {
                // The helpers import must be visited so import= is transformed for CJS output files.
                if (Visitor().VisitNode(externalHelpersImportDeclaration) is { } visitedHelper)
                {
                    statements.Add(visitedHelper);
                }
            }
            if (importRequireStatements is not null)
            {
                statements.AddRange(importRequireStatements.Statements);
            }
            statements.AddRange(rest);
            var statementList = Factory().NewNodeList(statements.ToArray());
            statementList.Loc = result.Statements.Loc;
            result = Factory().UpdateSourceFile(result, statementList, node.EndOfFileToken).AsSourceFile();
        }

        if (global::Ts.Ast.AstUtilities.IsExternalModule(result) &&
            compilerOptions.GetEmitModuleKind() != ModuleKind.Preserve &&
            !global::Ts.Core.CoreUtil.Some(result.Statements!.Nodes, TransformerAstAdapters.IsExternalModuleIndicator))
        {
            var statements = new Node[result.Statements.Nodes.Length + 1];
            Array.Copy(result.Statements.Nodes, statements, result.Statements.Nodes.Length);
            statements[^1] = ModuleTransformUtilities.CreateEmptyImports(Factory());
            var statementList = Factory().NewNodeList(statements);
            statementList.Loc = result.Statements.Loc;
            result = Factory().UpdateSourceFile(result, statementList, node.EndOfFileToken).AsSourceFile();
        }

        importRequireStatements = null;
        currentSourceFile = null;
        _ = resolver;
        _ = helperNameSubstitutions;
        return result.AsNode();
    }

    private Node VisitImportDeclaration(ImportDeclaration node)
    {
        if (!compilerOptions.RewriteRelativeImportExtensions.IsTrue())
        {
            return node.AsNode();
        }
        var updatedModuleSpecifier = ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), node.ModuleSpecifier, compilerOptions);
        return Factory().UpdateImportDeclaration(
            node,
            null,
            Visitor().VisitNode(node.ImportClause),
            updatedModuleSpecifier,
            Visitor().VisitNode(node.Attributes));
    }

    private Node? VisitImportEqualsDeclaration(ImportEqualsDeclaration node)
    {
        if (compilerOptions.GetEmitModuleKind() < ModuleKind.Node16)
        {
            return null;
        }
        if (!TransformerAstAdapters.IsExternalModuleImportEqualsDeclaration(node.AsNode()))
        {
            throw new InvalidOperationException("import= for internal module references should be handled in an earlier transformer.");
        }

        var variableStatement = Factory().NewVariableStatement(
            null,
            Factory().NewVariableDeclarationList(
                Factory().NewNodeList([
                    Factory().NewVariableDeclaration(node.Name()!.Clone(Factory()), null, null, CreateRequireCall(node.AsNode())),
                ]),
                NodeFlags.Const));
        EmitContext().SetOriginal(variableStatement, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(variableStatement, node.AsNode());
        List<Node> statements = [variableStatement];
        AppendExportsOfImportEqualsDeclaration(statements, node);
        return Transformers.SingleOrMany(statements.ToArray(), Factory());
    }

    private void AppendExportsOfImportEqualsDeclaration(List<Node> statements, ImportEqualsDeclaration node)
    {
        if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export))
        {
            statements.Add(Factory().NewExportDeclaration(
                null,
                false,
                Factory().NewNamedExports(Factory().NewNodeList([
                    Factory().NewExportSpecifier(false, null, node.Name()!.Clone(Factory())),
                ])),
                null,
                null));
        }
    }

    private Node? VisitExportAssignment(ExportAssignment node)
    {
        if (!node.IsExportEquals)
        {
            return Visitor().VisitEachChild(node.AsNode());
        }
        if (compilerOptions.GetEmitModuleKind() != ModuleKind.Preserve)
        {
            return null;
        }
        var statement = Factory().NewExpressionStatement(
            Factory().NewAssignmentExpression(
                Factory().NewPropertyAccessExpression(
                    Factory().NewIdentifier("module"),
                    null,
                    Factory().NewIdentifier("exports"),
                    NodeFlags.None),
                Visitor().VisitNode(node.Expression)!));
        EmitContext().SetOriginal(statement, node.AsNode());
        return statement;
    }

    private Node VisitExportDeclaration(ExportDeclaration node)
    {
        if (node.ModuleSpecifier is null)
        {
            return node.AsNode();
        }
        var updatedModuleSpecifier = ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), node.ModuleSpecifier, compilerOptions);
        if (compilerOptions.Module > ModuleKind.ES2015 || node.ExportClause is null || !global::Ts.Ast.NodeTests.IsNamespaceExport(node.ExportClause))
        {
            return Factory().UpdateExportDeclaration(
                node,
                null,
                false,
                node.ExportClause,
                updatedModuleSpecifier,
                Visitor().VisitNode(node.Attributes));
        }

        var oldIdentifier = node.ExportClause.Name()!;
        var synthesizedName = Factory().NewGeneratedNameForNode(oldIdentifier);
        var importDeclaration = Factory().NewImportDeclaration(
            null,
            Factory().NewImportClause(SyntaxKind.Unknown, null, Factory().NewNamespaceImport(synthesizedName)),
            updatedModuleSpecifier,
            Visitor().VisitNode(node.Attributes));
        EmitContext().SetOriginal(importDeclaration, node.ExportClause);

        Node exportDeclaration;
        if (TransformerAstAdapters.IsExportNamespaceAsDefaultDeclaration(node.AsNode()))
        {
            exportDeclaration = Factory().NewExportAssignment(null, false, null, synthesizedName);
        }
        else
        {
            exportDeclaration = Factory().NewExportDeclaration(
                null,
                false,
                Factory().NewNamedExports(Factory().NewNodeList([
                    Factory().NewExportSpecifier(false, synthesizedName, oldIdentifier),
                ])),
                null,
                null);
        }
        EmitContext().SetOriginal(exportDeclaration, node.AsNode());
        return Transformers.SingleOrMany([importDeclaration, exportDeclaration], Factory())!;
    }

    private Node VisitCallExpression(CallExpression node)
    {
        if (compilerOptions.RewriteRelativeImportExtensions.IsTrue() &&
            ((TransformerAstAdapters.IsImportCall(node.AsNode()) && node.Arguments!.Nodes.Length > 0) ||
            (global::Ts.Ast.AstUtilities.IsInJSFile(node.AsNode()) &&
            global::Ts.Ast.AstUtilities.IsRequireCall(node.AsNode(), false))))
        {
            return VisitImportOrRequireCall(node);
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitImportOrRequireCall(CallExpression node)
    {
        if (node.Arguments!.Nodes.Length == 0)
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }
        var expression = Visitor().VisitNode(node.Expression);
        Expression argument = global::Ts.Ast.AstUtilities.IsStringLiteralLike(node.Arguments.Nodes[0])
            ? ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), node.Arguments.Nodes[0], compilerOptions)!
            : Factory().NewRewriteRelativeImportExtensionsHelper(
                node.Arguments.Nodes[0],
                compilerOptions.Jsx == JsxEmit.Preserve);

        List<Node> arguments = [argument];
        var rest = node.Arguments.Nodes[1..];
        var (visitedRest, _) = Visitor().VisitSlice(rest);
        arguments.AddRange(visitedRest ?? []);
        var argumentList = Factory().NewNodeList(arguments.ToArray());
        argumentList.Loc = node.Arguments.Loc;
        return Factory().UpdateCallExpression(node, expression, node.QuestionDotToken, null, argumentList, node.Flags);
    }

    private Expression CreateRequireCall(Node node)
    {
        var moduleName = ModuleTransformUtilities.GetExternalModuleNameLiteral(
            Factory(), node, currentSourceFile!, null, null, compilerOptions);
        List<Node> arguments = [];
        if (moduleName is not null)
        {
            arguments.Add(ModuleTransformUtilities.RewriteModuleSpecifier(EmitContext(), moduleName, compilerOptions)!);
        }
        if (compilerOptions.GetEmitModuleKind() == ModuleKind.Preserve)
        {
            return Factory().NewCallExpression(
                Factory().NewIdentifier("require"), null, null, Factory().NewNodeList(arguments.ToArray()), NodeFlags.None);
        }

        if (importRequireStatements is null)
        {
            var createRequireName = Factory().NewUniqueNameEx("_createRequire", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            });
            var importStatement = Factory().NewImportDeclaration(
                null,
                Factory().NewImportClause(
                    SyntaxKind.Unknown,
                    null,
                    Factory().NewNamedImports(Factory().NewNodeList([
                        Factory().NewImportSpecifier(false, Factory().NewIdentifier("createRequire"), createRequireName),
                    ]))),
                Factory().NewStringLiteral("module", TokenFlags.None),
                null);
            EmitContext().AddEmitFlags(importStatement, global::Ts.Printer.EmitFlags.CustomPrologue);

            var requireHelperName = Factory().NewUniqueNameEx("__require", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            });
            var requireStatement = Factory().NewVariableStatement(
                null,
                Factory().NewVariableDeclarationList(
                    Factory().NewNodeList([
                        Factory().NewVariableDeclaration(
                            requireHelperName,
                            null,
                            null,
                            Factory().NewCallExpression(
                                createRequireName.Clone(Factory()),
                                null,
                                null,
                                Factory().NewNodeList([
                                    Factory().NewPropertyAccessExpression(
                                        Factory().NewMetaProperty(SyntaxKind.ImportKeyword, Factory().NewIdentifier("meta")),
                                        null,
                                        Factory().NewIdentifier("url"),
                                        NodeFlags.None),
                                ]),
                                NodeFlags.None)),
                    ]),
                    NodeFlags.Const));
            EmitContext().AddEmitFlags(requireStatement, global::Ts.Printer.EmitFlags.CustomPrologue);
            importRequireStatements = new ImportRequireStatements
            {
                Statements = [importStatement, requireStatement],
                RequireHelperName = requireHelperName,
            };
        }

        return Factory().NewCallExpression(
            importRequireStatements.RequireHelperName.Clone(Factory()),
            null,
            null,
            Factory().NewNodeList(arguments.ToArray()),
            NodeFlags.None);
    }
}
