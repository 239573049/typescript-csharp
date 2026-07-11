// Minimal local adapters for AST utility members that are present in tsgo but have not yet
// landed as public members of Ts.Ast. Kept in Transformers so the parallel boundary remains intact.

using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers;

internal static class TransformerAstAdapters
{
    public static HasFileName AsHasFileName(SourceFile sourceFile) => new SourceFileHasFileName(sourceFile);

    private sealed class SourceFileHasFileName : HasFileName
    {
        private readonly SourceFile sourceFile;

        public SourceFileHasFileName(SourceFile sourceFile)
        {
            this.sourceFile = sourceFile;
        }

        public string FileName() => sourceFile.FileName();

        public global::Ts.TsPath.Path Path() => sourceFile.Path();
    }

    public static bool IsAssignmentPattern(Node node) =>
        node.Kind is SyntaxKind.ArrayLiteralExpression or SyntaxKind.ObjectLiteralExpression;

    public static Node[] GetElementsOfBindingOrAssignmentPattern(Node name) => name.Kind switch
    {
        SyntaxKind.ObjectBindingPattern or SyntaxKind.ArrayBindingPattern or SyntaxKind.ArrayLiteralExpression => name.Elements() ?? [],
        SyntaxKind.ObjectLiteralExpression => name.Properties() ?? [],
        _ => [],
    };

    public static bool IsDeclarationBindingElement(Node bindingElement) => bindingElement.Kind is
        SyntaxKind.VariableDeclaration or SyntaxKind.Parameter or SyntaxKind.BindingElement;

    public static Node? GetTargetOfBindingOrAssignmentElement(Node? bindingElement)
    {
        if (bindingElement is null)
        {
            return null;
        }
        if (IsDeclarationBindingElement(bindingElement))
        {
            return bindingElement.Name();
        }
        if (global::Ts.Ast.AstUtilities.IsObjectLiteralElement(bindingElement))
        {
            return bindingElement.Kind switch
            {
                SyntaxKind.PropertyAssignment => GetTargetOfBindingOrAssignmentElement(bindingElement.Initializer()),
                SyntaxKind.ShorthandPropertyAssignment => bindingElement.Name(),
                SyntaxKind.SpreadAssignment => GetTargetOfBindingOrAssignmentElement(bindingElement.Expression()),
                _ => null,
            };
        }
        if (global::Ts.Ast.AstUtilities.IsAssignmentExpression(bindingElement, true))
        {
            return GetTargetOfBindingOrAssignmentElement(bindingElement.AsBinaryExpression().Left);
        }
        if (global::Ts.Ast.NodeTests.IsSpreadElement(bindingElement))
        {
            return GetTargetOfBindingOrAssignmentElement(bindingElement.Expression());
        }
        return bindingElement;
    }

    public static Node? TryGetPropertyNameOfBindingOrAssignmentElement(Node bindingElement)
    {
        if (bindingElement.Kind == SyntaxKind.BindingElement && bindingElement.PropertyName() is { } bindingPropertyName)
        {
            // if IsPrivateIdentifier(propertyName) { return Debug.failBadSyntaxKind(propertyName) } // !!!
            return global::Ts.Ast.NodeTests.IsComputedPropertyName(bindingPropertyName) &&
                global::Ts.Ast.AstUtilities.IsStringOrNumericLiteralLike(bindingPropertyName.Expression()!)
                    ? bindingPropertyName.Expression()
                    : bindingPropertyName;
        }
        if (bindingElement.Kind == SyntaxKind.PropertyAssignment && bindingElement.Name() is { } assignmentPropertyName)
        {
            // if IsPrivateIdentifier(propertyName) { return Debug.failBadSyntaxKind(propertyName) } // !!!
            return global::Ts.Ast.NodeTests.IsComputedPropertyName(assignmentPropertyName) &&
                global::Ts.Ast.AstUtilities.IsStringOrNumericLiteralLike(assignmentPropertyName.Expression()!)
                    ? assignmentPropertyName.Expression()
                    : assignmentPropertyName;
        }
        if (bindingElement.Kind == SyntaxKind.SpreadAssignment)
        {
            // if IsPrivateIdentifier(bindingElement.Name()) { return Debug.failBadSyntaxKind(...) } // !!!
            return bindingElement.Name();
        }
        var target = GetTargetOfBindingOrAssignmentElement(bindingElement);
        return target is not null && global::Ts.Ast.AstUtilities.IsPropertyName(target) ? target : null;
    }

    public static Node? GetRestIndicatorOfBindingOrAssignmentElement(Node bindingElement) => bindingElement.Kind switch
    {
        SyntaxKind.Parameter => bindingElement.AsParameterDeclaration().DotDotDotToken,
        SyntaxKind.BindingElement => bindingElement.AsBindingElement().DotDotDotToken,
        SyntaxKind.SpreadElement or SyntaxKind.SpreadAssignment => bindingElement,
        _ => null,
    };

    public static bool IsEmptyObjectLiteral(Node expression) =>
        global::Ts.Ast.NodeTests.IsObjectLiteralExpression(expression) && (expression.Properties()?.Length ?? 0) == 0;

    public static bool IsJsxOpeningLikeElement(Node node) =>
        global::Ts.Ast.NodeTests.IsJsxOpeningElement(node) || global::Ts.Ast.NodeTests.IsJsxSelfClosingElement(node);

    public static Node[] GetSemanticJsxChildren(Node[]? children)
    {
        if (children is null)
        {
            return [];
        }
        var result = new System.Collections.Generic.List<Node>(children.Length);
        foreach (var child in children)
        {
            var include = child.Kind switch
            {
                SyntaxKind.JsxExpression => child.Expression() is not null,
                SyntaxKind.JsxText => !child.AsJsxText().ContainsOnlyTriviaWhiteSpaces,
                _ => true,
            };
            if (include)
            {
                result.Add(child);
            }
        }
        return result.ToArray();
    }

    public static string GetJsxImplicitImportBase(CompilerOptions options, SourceFile file)
    {
        var jsxImportSourcePragma = GetPragmaFromSourceFile(file, "jsximportsource");
        var jsxRuntimePragma = GetPragmaFromSourceFile(file, "jsxruntime");
        if (string.Equals(GetPragmaArgument(jsxRuntimePragma, "factory"), "classic", System.StringComparison.Ordinal))
        {
            return "";
        }
        if (options.Jsx is JsxEmit.ReactJSX or JsxEmit.ReactJSXDev ||
            options.JsxImportSource.Length != 0 ||
            jsxImportSourcePragma is not null ||
            string.Equals(GetPragmaArgument(jsxRuntimePragma, "factory"), "automatic", System.StringComparison.Ordinal))
        {
            var result = GetPragmaArgument(jsxImportSourcePragma, "factory");
            if (result.Length == 0)
            {
                result = options.JsxImportSource;
            }
            if (result.Length == 0)
            {
                result = "react";
            }
            return result;
        }
        return "";
    }

    public static string GetJsxRuntimeImport(string @base, CompilerOptions options) =>
        @base.Length == 0
            ? @base
            : @base + (options.Jsx == JsxEmit.ReactJSXDev ? "/jsx-dev-runtime" : "/jsx-runtime");

    private static Pragma? GetPragmaFromSourceFile(SourceFile file, string name)
    {
        Pragma? result = null;
        foreach (var pragma in file.Pragmas ?? [])
        {
            if (string.Equals(pragma.Name, name, System.StringComparison.Ordinal))
            {
                result = pragma; // Last one wins.
            }
        }
        return result;
    }

    private static string GetPragmaArgument(Pragma? pragma, string name) =>
        pragma is { } value && value.Args is not null && value.Args.TryGetValue(name, out var argument)
            ? argument.Value
            : "";

    public static Node? GetNamespaceDeclarationNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                var importClause = node.ImportClause();
                var namedBindings = importClause?.AsImportClause().NamedBindings;
                return namedBindings is not null && global::Ts.Ast.NodeTests.IsNamespaceImport(namedBindings) ? namedBindings : null;
            case SyntaxKind.ImportEqualsDeclaration:
                return node;
            case SyntaxKind.ExportDeclaration:
                var exportClause = node.AsExportDeclaration().ExportClause;
                return exportClause is not null && global::Ts.Ast.NodeTests.IsNamespaceExport(exportClause) ? exportClause : null;
            default:
                throw new System.InvalidOperationException("Unhandled case in getNamespaceDeclarationNode");
        }
    }

    public static bool IsDefaultImport(Node node)
    {
        return node.Kind is SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration &&
            node.ImportClause()?.AsImportClause().Name() is not null;
    }

    public static bool IsEffectiveExternalModule(SourceFile file, CompilerOptions options)
    {
        var kind = options.GetEmitModuleKind();
        return global::Ts.Ast.AstUtilities.IsExternalModule(file) ||
            ((kind == ModuleKind.CommonJS || kind is >= ModuleKind.Node16 and <= ModuleKind.NodeNext) && file.CommonJSModuleIndicator is not null);
    }

    public static bool IsExternalModuleIndicator(Node node) =>
        node.Kind is SyntaxKind.ImportDeclaration or SyntaxKind.ImportEqualsDeclaration or SyntaxKind.ExportDeclaration or
            SyntaxKind.ExportAssignment or SyntaxKind.NamespaceExportDeclaration ||
        global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export);

    public static bool IsExportNamespaceAsDefaultDeclaration(Node node)
    {
        if (!global::Ts.Ast.NodeTests.IsExportDeclaration(node))
        {
            return false;
        }
        var clause = node.AsExportDeclaration().ExportClause;
        return clause is not null && global::Ts.Ast.NodeTests.IsNamespaceExport(clause) &&
            global::Ts.Ast.AstUtilities.ModuleExportNameIsDefault(clause.Name()!);
    }

    public static bool IsExternalModuleImportEqualsDeclaration(Node node) =>
        node.Kind == SyntaxKind.ImportEqualsDeclaration && node.AsImportEqualsDeclaration().ModuleReference?.Kind == SyntaxKind.ExternalModuleReference;

    public static bool IsImportCall(Node node)
    {
        if (!global::Ts.Ast.NodeTests.IsCallExpression(node))
        {
            return false;
        }
        var expression = node.Expression()!;
        return expression.Kind == SyntaxKind.ImportKeyword ||
            (global::Ts.Ast.NodeTests.IsMetaProperty(expression) && expression.AsMetaProperty().KeywordToken == SyntaxKind.ImportKeyword &&
            string.Equals(expression.Text(), "defer", System.StringComparison.Ordinal));
    }

    public static bool IsFileLevelUniqueName(SourceFile sourceFile, string name) =>
        sourceFile.Identifiers is null || !sourceFile.Identifiers.ContainsKey(name);

    public static bool ShouldTransformImportCall(string fileName, CompilerOptions options, ModuleKind impliedNodeFormatForEmit)
    {
        _ = fileName;
        var moduleKind = options.GetEmitModuleKind();
        if (moduleKind is >= ModuleKind.Node16 and <= ModuleKind.NodeNext || moduleKind == ModuleKind.Preserve)
        {
            return false;
        }
        return impliedNodeFormatForEmit < ModuleKind.ES2015;
    }

    public static bool IsSuperCall(Node node) =>
        global::Ts.Ast.NodeTests.IsCallExpression(node) && node.Expression()!.Kind == SyntaxKind.SuperKeyword;

    public static bool CanHaveModifiers(Node node) => node.Kind is
        SyntaxKind.TypeParameter or
        SyntaxKind.Parameter or
        SyntaxKind.PropertySignature or
        SyntaxKind.PropertyDeclaration or
        SyntaxKind.MethodSignature or
        SyntaxKind.MethodDeclaration or
        SyntaxKind.Constructor or
        SyntaxKind.GetAccessor or
        SyntaxKind.SetAccessor or
        SyntaxKind.IndexSignature or
        SyntaxKind.ConstructorType or
        SyntaxKind.FunctionExpression or
        SyntaxKind.ArrowFunction or
        SyntaxKind.ClassExpression or
        SyntaxKind.VariableStatement or
        SyntaxKind.FunctionDeclaration or
        SyntaxKind.ClassDeclaration or
        SyntaxKind.InterfaceDeclaration or
        SyntaxKind.TypeAliasDeclaration or
        SyntaxKind.EnumDeclaration or
        SyntaxKind.ModuleDeclaration or
        SyntaxKind.ImportEqualsDeclaration or
        SyntaxKind.ImportDeclaration or
        SyntaxKind.JSImportDeclaration or
        SyntaxKind.ExportAssignment or
        SyntaxKind.ExportDeclaration;
}
