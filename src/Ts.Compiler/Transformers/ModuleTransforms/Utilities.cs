// Ported from tsgo internal/transformers/moduletransforms/utilities.go.

using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.ModuleTransforms;

internal static partial class ModuleTransformUtilities
{
    internal static bool IsDeclarationNameOfEnumOrNamespace(global::Ts.Printer.EmitContext emitContext, IdentifierNode node)
    {
        var original = emitContext.MostOriginal(node);
        if (original?.Parent is { } parent && parent.Kind is SyntaxKind.EnumDeclaration or SyntaxKind.ModuleDeclaration)
        {
            return original == parent.Name();
        }
        return false;
    }

    internal static Expression? RewriteModuleSpecifier(
        global::Ts.Printer.EmitContext emitContext,
        Expression? node,
        CompilerOptions compilerOptions)
    {
        if (node is null || !global::Ts.Ast.NodeTests.IsStringLiteral(node) || !ShouldRewriteModuleSpecifier(node.Text(), compilerOptions))
        {
            return node;
        }
        var updatedText = global::Ts.TsPath.TsPath.ChangeExtension(
            node.Text(),
            global::Ts.OutputPaths.OutputPaths.GetOutputExtension(node.Text(), compilerOptions.Jsx));
        if (!string.Equals(updatedText, node.Text(), System.StringComparison.Ordinal))
        {
            var updated = emitContext.Factory.NewStringLiteral(updatedText, node.AsStringLiteral().TokenFlags);
            emitContext.SetOriginal(updated, node);
            emitContext.AssignCommentAndSourceMapRanges(updated, node);
            return updated;
        }
        return node;
    }

    private static bool ShouldRewriteModuleSpecifier(string specifier, CompilerOptions compilerOptions) =>
        compilerOptions.RewriteRelativeImportExtensions.IsTrue() &&
        global::Ts.TsPath.TsPath.PathIsRelative(specifier) &&
        !global::Ts.TsPath.TsPath.IsDeclarationFileName(specifier) &&
        global::Ts.TsPath.TsPath.HasTSFileExtension(specifier);

    internal static Statement CreateEmptyImports(global::Ts.Printer.NodeFactory factory) =>
        factory.NewExportDeclaration(null, false, factory.NewNamedExports(factory.NewNodeList([])), null, null);

    // Get the name of a target module from an import/export declaration as should be written in the emitted output.
    internal static StringLiteralNode? GetExternalModuleNameLiteral(
        global::Ts.Printer.NodeFactory factory,
        Node importNode,
        SourceFile sourceFile,
        object? host,
        global::Ts.Printer.EmitResolver? resolver,
        CompilerOptions compilerOptions)
    {
        var moduleName = GetExternalModuleName(importNode);
        if (moduleName is not null && global::Ts.Ast.NodeTests.IsStringLiteral(moduleName))
        {
            var name = TryGetModuleNameFromDeclaration(importNode, host, factory, resolver, compilerOptions) ??
                TryRenameExternalModule(factory, moduleName, sourceFile);
            if (name is null) // !!! propagate token flags (will produce new diffs)
            {
                name = factory.NewStringLiteral(moduleName.Text(), TokenFlags.None);
            }
            return name;
        }
        return null;
    }

    private static Node? GetExternalModuleName(Node node) => node.Kind switch
    {
        SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration or SyntaxKind.ExportDeclaration => node.ModuleSpecifier(),
        SyntaxKind.ImportEqualsDeclaration when node.AsImportEqualsDeclaration().ModuleReference?.Kind == SyntaxKind.ExternalModuleReference =>
            node.AsImportEqualsDeclaration().ModuleReference!.Expression(),
        SyntaxKind.CallExpression => global::Ts.Core.CoreUtil.FirstOrNil(node.Arguments()),
        SyntaxKind.ModuleDeclaration when global::Ts.Ast.NodeTests.IsStringLiteral(node.AsModuleDeclaration().Name()!) => node.AsModuleDeclaration().Name(),
        _ => null,
    };

    private static StringLiteralNode? TryGetModuleNameFromFile(
        global::Ts.Printer.NodeFactory factory,
        SourceFile? file,
        object? host,
        CompilerOptions options)
    {
        if (file is null)
        {
            return null;
        }
        // !!!
        // if file.moduleName { return factory.createStringLiteral(file.moduleName) }
        _ = factory;
        _ = host;
        _ = options;
        return null;
    }

    private static StringLiteralNode? TryGetModuleNameFromDeclaration(
        Node declaration,
        object? host,
        global::Ts.Printer.NodeFactory factory,
        global::Ts.Printer.EmitResolver? resolver,
        CompilerOptions compilerOptions)
    {
        return resolver is null
            ? null
            : TryGetModuleNameFromFile(factory, resolver.GetExternalModuleFileFromDeclaration(declaration), host, compilerOptions);
    }

    // Resolves a local path to a path which is absolute to the base of the emit.
    internal static string GetExternalModuleNameFromPath(object? host, string fileName, string referencePath)
    {
        // !!!
        _ = host;
        _ = fileName;
        _ = referencePath;
        return "";
    }

    // Some bundlers (SystemJS builder) sometimes want to rename dependencies.
    private static StringLiteralNode? TryRenameExternalModule(
        global::Ts.Printer.NodeFactory factory,
        LiteralExpression moduleName,
        SourceFile sourceFile)
    {
        // !!!
        _ = factory;
        _ = moduleName;
        _ = sourceFile;
        return null;
    }

    internal static bool IsFileLevelReservedGeneratedIdentifier(global::Ts.Printer.EmitContext emitContext, IdentifierNode name)
    {
        var info = emitContext.GetAutoGenerateInfo(name);
        return info is not null &&
            (info.Flags & global::Ts.Printer.GeneratedIdentifierFlags.FileLevel) != 0 &&
            (info.Flags & global::Ts.Printer.GeneratedIdentifierFlags.Optimistic) != 0 &&
            (info.Flags & global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes) != 0;
    }

    internal static bool IsSimpleInlineableExpression(Expression expression) =>
        !global::Ts.Ast.NodeTests.IsIdentifier(expression) && Transformers.IsSimpleCopiableExpression(expression);
}
