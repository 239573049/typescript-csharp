// Ported from tsgo internal/ls/autoimport/util.go — the registry-independent helpers
// (:24-146, :288-300). PORT-NOTE: the registry-scoped tail (getResolvedPackageNames :148-179,
// addProjectReferenceOutputMappings :181-200 [needs Program.Resolved/UnresolvedPackageNames +
// project-reference input/output parsing], createCheckerPool :202-241, getPackageRealpathFuncs
// :243-286 and the resolutionHost/getModuleResolver pair :302-323 [need RegistryCloneHost +
// wrapvfs]) lands with the registry batch.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;
using TsPathApi = Ts.TsPath.TsPath;

namespace Ts.Ls.AutoImport;

public static partial class Util
{
    // util.go:24-39 — tryGetModuleIDAndFileNameOfModuleSymbol.
    internal static bool TryGetModuleIDAndFileNameOfModuleSymbol(Symbol symbol, out ModuleID moduleID, out string fileName)
    {
        moduleID = default;
        fileName = "";
        if (!symbol.IsExternalModule())
        {
            return false;
        }
        var decl = AstUtilities.GetNonAugmentationDeclaration(symbol);
        if (decl == null)
        {
            return false;
        }
        if (decl.Kind == SyntaxKind.SourceFile)
        {
            moduleID = new ModuleID(decl.AsSourceFile().Path().Value);
            fileName = decl.AsSourceFile().FileName();
            return true;
        }
        if (AstUtilities.IsModuleWithStringLiteralName(decl))
        {
            moduleID = new ModuleID(decl.Name()!.Text());
            fileName = "";
            return true;
        }
        return false;
    }

    // util.go:41-56 — getModuleIDAndFileNameOfModuleSymbol.
    internal static (ModuleID ModuleID, string FileName) GetModuleIDAndFileNameOfModuleSymbol(Symbol symbol)
    {
        if (!symbol.IsExternalModule())
        {
            throw new InvalidOperationException("symbol is not an external module");
        }
        var decl = AstUtilities.GetNonAugmentationDeclaration(symbol);
        if (decl == null)
        {
            throw new InvalidOperationException("module symbol has no non-augmentation declaration");
        }
        if (decl.Kind == SyntaxKind.SourceFile)
        {
            return (new ModuleID(decl.AsSourceFile().Path().Value), decl.AsSourceFile().FileName());
        }
        if (AstUtilities.IsModuleWithStringLiteralName(decl))
        {
            return (new ModuleID(decl.Name()!.Text()), "");
        }
        throw new InvalidOperationException("could not determine module ID of module symbol");
    }

    // util.go:58-88 — wordIndices: splits an identifier into its constituent words based on
    // camelCase and snake_case conventions by returning the starting UTF-16 offsets of each word.
    // The first index is always 0.
    //   - CamelCase
    //     ^    ^
    //   - snake_case
    //     ^     ^
    //   - ParseURL
    //     ^    ^
    //   - __proto__
    //     ^
    // (Go byte indices over UTF-8 become UTF-16 code-unit indices here; consumers index into the
    // same C# string, so the semantics carry over.)
    internal static List<int> WordIndices(string s)
    {
        var indices = new List<int>();
        for (var i = 0; i < s.Length;)
        {
            int runeValue;
            int size;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                runeValue = char.ConvertToUtf32(s[i], s[i + 1]);
                size = 2;
            }
            else
            {
                runeValue = s[i];
                size = 1;
            }
            if (i == 0)
            {
                indices.Add(i);
                i += size;
                continue;
            }
            if (runeValue == '_')
            {
                if (i + 1 < s.Length && s[i + 1] != '_')
                {
                    indices.Add(i + 1);
                }
                i += size;
                continue;
            }
            if (char.IsUpper((char)Math.Min(runeValue, char.MaxValue)) && runeValue <= char.MaxValue &&
                (IsLowerCodePointBefore(s, i) || (i + size < s.Length && IsLowerCodePointAt(s, i + size))))
            {
                indices.Add(i);
            }
            i += size;
        }
        return indices;
    }

    private static bool IsLowerCodePointBefore(string s, int index)
    {
        if (index <= 0)
        {
            return false;
        }
        var c = s[index - 1];
        if (char.IsLowSurrogate(c) && index >= 2 && char.IsHighSurrogate(s[index - 2]))
        {
            return char.IsLower(s, index - 2);
        }
        return char.IsLower(c);
    }

    private static bool IsLowerCodePointAt(string s, int index)
    {
        return index < s.Length && char.IsLower(s, index);
    }

    // util.go:90-118 — getPackageNamesInNodeModules.
    internal static HashSet<string> GetPackageNamesInNodeModules(string nodeModulesDir, global::Ts.Vfs.IFileSystem fs)
    {
        var packageNames = new HashSet<string>(StringComparer.Ordinal);
        if (TsPathApi.GetBaseFileName(nodeModulesDir) != "node_modules")
        {
            throw new InvalidOperationException("nodeModulesDir is not a node_modules directory");
        }
        // A missing node_modules directory yields no entries (GetAccessibleEntries returns
        // empty), so there's no need to check existence first: a deleted node_modules is
        // handled upstream in updateBucketAndDirectoryExistence, which drops the bucket.
        var entries = fs.GetAccessibleEntries(nodeModulesDir);
        foreach (var baseName in entries.Directories ?? [])
        {
            if (baseName[0] == '.')
            {
                continue;
            }
            if (baseName[0] == '@')
            {
                var scopedDirPath = TsPathApi.CombinePaths(nodeModulesDir, baseName);
                foreach (var scopedPackageDirName in fs.GetAccessibleEntries(scopedDirPath).Directories ?? [])
                {
                    var scopedBaseName = TsPathApi.GetBaseFileName(scopedPackageDirName);
                    if (baseName == "@types")
                    {
                        packageNames.Add(global::Ts.Module.Module.GetPackageNameFromTypesPackageName(TsPathApi.CombinePaths("@types", scopedBaseName)));
                    }
                    else
                    {
                        packageNames.Add(TsPathApi.CombinePaths(baseName, scopedBaseName));
                    }
                }
                continue;
            }
            packageNames.Add(baseName);
        }
        return packageNames;
    }

    // util.go:120-146 — getDefaultLikeExportNameFromDeclaration.
    internal static string GetDefaultLikeExportNameFromDeclaration(Symbol symbol)
    {
        foreach (var d in symbol.Declarations ?? [])
        {
            // "export default" in this case. See `ExportAssignment`for more details.
            if (NodeTests.IsExportAssignment(d))
            {
                if (AstUtilities.SkipOuterExpressions(d.Expression()!, global::Ts.Ast.OuterExpressionKinds.All) is { } innerExpression && NodeTests.IsIdentifier(innerExpression))
                {
                    return innerExpression.Text();
                }
                continue;
            }
            // "export { ~ as default }"
            if (NodeTests.IsExportSpecifier(d) && d.Symbol()!.Flags == SymbolFlags.Alias && d.PropertyName() != null)
            {
                if (d.PropertyName()!.Kind == SyntaxKind.Identifier)
                {
                    return d.PropertyName()!.Text();
                }
                continue;
            }
            // GH#52694
            if (AstUtilities.GetNameOfDeclaration(d) is { } name && name.Kind == SyntaxKind.Identifier)
            {
                return name.Text();
            }
            if (symbol.Parent != null && !TsChecker.IsExternalModuleSymbol(symbol.Parent))
            {
                return symbol.Parent.Name;
            }
        }
        return "";
    }

    // util.go:288-300 — addPackageJsonDependencies: adds all dependencies and peerDependencies
    // from a package.json to the given set, canonicalizing @types package names to their base names.
    internal static void AddPackageJsonDependencies(global::Ts.PackageJson.PackageJson contents, HashSet<string> deps)
    {
        contents.Fields.DependencyFields.RangeDependencies((name, _, field) =>
        {
            if (name.Length == 0 || name == "@types/" || name[0] == '.')
            {
                // Edge cases that could make us blow up probably
                return true;
            }
            if (field == "dependencies" || field == "peerDependencies")
            {
                deps.Add(global::Ts.Module.Module.GetPackageNameFromTypesPackageName(name));
            }
            return true;
        });
    }
}
