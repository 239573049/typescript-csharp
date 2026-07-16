// Ported from tsgo internal/ast/utilities.go — module-symbol source-file helpers used by the
// modulespecifiers package. (The checker localized private copies of these; the faithful ast-level
// home is here.)

using Ts.Core;

namespace Ts.Ast;

public static partial class AstUtilities
{
    // ast/utilities.go — IsExternalModuleAugmentation.
    public static bool IsExternalModuleAugmentation(Node node) =>
        IsAmbientModule(node) && IsModuleAugmentationExternal(node);

    // ast/utilities.go — GetNonAugmentationDeclaration.
    public static Node? GetNonAugmentationDeclaration(Symbol symbol) =>
        CoreUtil.Find(symbol.Declarations, static d => !IsExternalModuleAugmentation(d) && !IsGlobalScopeAugmentation(d));

    // ast/utilities.go — GetSourceFileOfModule.
    public static SourceFile? GetSourceFileOfModule(Symbol module) =>
        GetSourceFileOfNode(module.ValueDeclaration ?? GetNonAugmentationDeclaration(module));

    // ast.go:2215 — ImportAttributesNode.GetResolutionModeOverride (grammar-error callback elided; the
    // checker's GetResolutionModeOverride(ImportAttributes, reportErrors) owns the reporting path).
    public static (ResolutionMode, bool) GetResolutionModeOverride(Node? node)
    {
        if (node is null)
        {
            return (global::Ts.Core.Core.ResolutionModeNone, false);
        }
        ImportAttributeList? attributes = node.AsImportAttributes().Attributes;
        if (attributes is null || attributes.Nodes.Length != 1)
        {
            return (global::Ts.Core.Core.ResolutionModeNone, false);
        }
        Node elem = attributes.Nodes[0];
        if (!IsStringLiteralLike(elem.Name()!))
        {
            return (global::Ts.Core.Core.ResolutionModeNone, false);
        }
        if (elem.Name()!.Text() != "resolution-mode")
        {
            return (global::Ts.Core.Core.ResolutionModeNone, false);
        }
        Node value = elem.AsImportAttribute().Value!;
        if (!IsStringLiteralLike(value))
        {
            return (global::Ts.Core.Core.ResolutionModeNone, false);
        }
        if (value.Text() != "import" && value.Text() != "require")
        {
            return (global::Ts.Core.Core.ResolutionModeNone, false);
        }
        if (value.Text() == "import")
        {
            return (ModuleKind.ESNext, true);
        }
        return (ModuleKind.CommonJS, true);
    }

    // ast/utilities.go:3171 — IsEmittableImport.
    public static bool IsEmittableImport(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
                return node.ImportClause() != null && !node.ImportClause()!.IsTypeOnly();
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
                return !node.IsTypeOnly();
            case SyntaxKind.CallExpression:
                return IsImportCall(node);
        }
        return false;
    }

    // ast/utilities.go — IsResolutionModeOverrideHost.
    public static bool IsResolutionModeOverrideHost(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        switch (node.Kind)
        {
            case SyntaxKind.ImportType:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                return true;
        }
        return false;
    }

    // ast/utilities.go — HasResolutionModeOverride.
    public static bool HasResolutionModeOverride(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        ImportAttributesNode? attributes = null;
        switch (node.Kind)
        {
            case SyntaxKind.ImportType:
                attributes = node.AsImportTypeNode().Attributes;
                break;
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                attributes = node.AsImportDeclaration().Attributes;
                break;
            case SyntaxKind.ExportDeclaration:
                attributes = node.AsExportDeclaration().Attributes;
                break;
        }
        if (attributes != null)
        {
            var (_, ok) = GetResolutionModeOverride(attributes);
            return ok;
        }
        return false;
    }
}

// SourceFile structurally satisfies SourceFileForSpecifierGeneration (Path()/FileName()/Imports()/IsJS())
// and, transitively, HasFileName; the inheritance is declared here so instances pass to the
// modulespecifiers entry points and to host methods typed on HasFileName (modulespecifiers/outputpaths).
public sealed partial class SourceFile : global::Ts.ModuleSpecifiers.SourceFileForSpecifierGeneration
{
}
