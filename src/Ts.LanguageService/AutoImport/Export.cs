// Ported from tsgo internal/ls/autoimport/export.go (whole file) and
// export_stringer_generated.go (the ExportSyntax stringer, hand-carried: debug-only
// display, not baseline-bearing, so no generator participation per plan/04).

using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls.AutoImport;

// export.go:14-18 — ModuleID uniquely identifies a module across multiple declarations.
// If the export is from an ambient module declaration, this is the module name.
// If the export is from a module augmentation, this is the Path() of the resolved module file.
// Otherwise this is the Path() of the exporting source file.
// (Go `type ModuleID string` — readonly record struct over string per plan/05 §5.)
public readonly record struct ModuleID(string Value)
{
    public override string ToString() => Value;
}

// export.go:20-23 — ExportID.
public readonly record struct ExportID(ModuleID ModuleID, string ExportName);

// export.go:25-47 — ExportSyntax.
public enum ExportSyntax
{
    None = 0,
    // export const x = {}
    Modifier,
    // export { x }
    Named,
    // export default function f() {}
    DefaultModifier,
    // export default f
    DefaultDeclaration,
    // export = x
    Equals,
    // export as namespace x
    UMD,
    // export * from "module"
    Star,
    // module.exports = {}
    CommonJSModuleExports,
    // exports.x = {}
    CommonJSExportsProperty,
}

public static class ExportSyntaxExtensions
{
    // export_stringer_generated.go — String().
    public static string String(this ExportSyntax i)
    {
        switch (i)
        {
            case ExportSyntax.None: return "ExportSyntaxNone";
            case ExportSyntax.Modifier: return "ExportSyntaxModifier";
            case ExportSyntax.Named: return "ExportSyntaxNamed";
            case ExportSyntax.DefaultModifier: return "ExportSyntaxDefaultModifier";
            case ExportSyntax.DefaultDeclaration: return "ExportSyntaxDefaultDeclaration";
            case ExportSyntax.Equals: return "ExportSyntaxEquals";
            case ExportSyntax.UMD: return "ExportSyntaxUMD";
            case ExportSyntax.Star: return "ExportSyntaxStar";
            case ExportSyntax.CommonJSModuleExports: return "ExportSyntaxCommonJSModuleExports";
            case ExportSyntax.CommonJSExportsProperty: return "ExportSyntaxCommonJSExportsProperty";
            default: return $"ExportSyntax({(int)i})";
        }
    }
}

// export.go:49-70 — Export.
public sealed class Export : Named
{
    // Go embeds ExportID; flattened per plan/02 mixin discipline.
    public ModuleID ModuleID;
    public string ExportName = "";
    public string ModuleFileName = "";
    public ExportSyntax Syntax;
    public SymbolFlags Flags;
    internal string localName = "";
    // through is the name of the module symbol's export that this export was found on,
    // either 'export=', InternalSymbolNameExportStar, or empty string.
    internal string through = "";

    // Checker-set fields

    public ExportID Target;
    public bool IsTypeOnly;
    public ScriptElementKind ScriptElementKind;
    public ScriptElementKindModifier ScriptElementKindModifiers;

    // The file where the export was found.
    public global::Ts.TsPath.Path Path;

    public string PackageName = "";

    public ExportID ExportID => new(ModuleID, ExportName);

    // export.go:72-80 — Name.
    public string Name()
    {
        if (localName.Length != 0)
        {
            return localName;
        }
        if (ExportName == InternalSymbolName.ExportEquals)
        {
            return Target.ExportName;
        }
        return ExportName;
    }

    // export.go:82-84 — IsRenameable.
    public bool IsRenameable()
    {
        return ExportName == InternalSymbolName.ExportEquals || ExportName == InternalSymbolName.Default;
    }

    // export.go:86-91 — AmbientModuleName.
    public string AmbientModuleName()
    {
        if (!global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(ModuleID.Value))
        {
            return ModuleID.Value;
        }
        return "";
    }

    // export.go:93-95 — IsUnresolvedAlias.
    public bool IsUnresolvedAlias()
    {
        return Flags == SymbolFlags.Alias;
    }
}

public static partial class AutoImportApi
{
    // export.go:97-116 — SymbolToExport.
    public static Export? SymbolToExport(Symbol symbol, TsChecker ch)
    {
        if (symbol.Parent != null && TsChecker.IsExternalModuleSymbol(symbol.Parent))
        {
            if (Util.TryGetModuleIDAndFileNameOfModuleSymbol(symbol.Parent, out var moduleID, out var moduleFileName))
            {
                return ExtractFirstExport(symbol, ch, moduleID, moduleFileName, AstUtilities.GetSourceFileOfModule(symbol.Parent)!);
            }
            return null;
        }

        var declaration = CoreUtil.FirstOrNil(symbol.Declarations);
        if (declaration == null)
        {
            return null;
        }

        var file = AstUtilities.GetSourceFileOfNode(declaration)!;
        if (file.Symbol == null)
        {
            return null;
        }

        var moduleSymbol = ch.GetMergedSymbol(file.Symbol)!;
        var fileModuleID = new ModuleID(file.Path().Value);
        var fileModuleFileName = file.FileName();
        var target = ch.GetMergedSymbol(ch.SkipAliasSymbol(symbol))!;

        if (TryGetModuleExport(InternalSymbolName.Default, target, moduleSymbol, ch, fileModuleID, fileModuleFileName, file) is { } defaultExport)
        {
            return defaultExport;
        }
        if (TryGetModuleExport(InternalSymbolName.ExportEquals, target, moduleSymbol, ch, fileModuleID, fileModuleFileName, file) is { } equalsExport)
        {
            return equalsExport;
        }
        return TryGetModuleExport(symbol.Name, target, moduleSymbol, ch, fileModuleID, fileModuleFileName, file);
    }

    // export.go:118-124 — tryGetModuleExport.
    internal static Export? TryGetModuleExport(string exportName, Symbol target, Symbol moduleSymbol, TsChecker ch, ModuleID moduleID, string moduleFileName, SourceFile file)
    {
        var exported = ch.TryGetMemberInModuleExportsAndProperties(exportName, moduleSymbol);
        if (exported != null && ch.GetMergedSymbol(ch.SkipAliasSymbol(exported)) == target)
        {
            return ExtractFirstExport(exported, ch, moduleID, moduleFileName, file);
        }
        return null;
    }

    // export.go:126-131 — extractFirstExport.
    internal static Export? ExtractFirstExport(Symbol symbol, TsChecker ch, ModuleID moduleID, string moduleFileName, SourceFile file)
    {
        var exports = new System.Collections.Generic.List<Export>();
        var extractor = SymbolExtractor.NewSymbolExtractor("", ch, null, null);
        extractor.ExtractFromSymbol(symbol.Name, symbol, moduleID, moduleFileName, file, exports);
        return CoreUtil.FirstOrNil(exports.ToArray());
    }
}
