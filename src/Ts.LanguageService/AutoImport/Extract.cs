// Ported from tsgo internal/ls/autoimport/extract.go (whole file): the symbol/export
// extractors that turn module symbols into Export records.
// PORT-NOTE: the registryBuilder.newExportExtractor wrapper (:72-77) lands with the registry
// batch; ExportExtractor's constructor takes the builder-supplied toPath directly until then.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;
using TsPathApi = Ts.TsPath.TsPath;

namespace Ts.Ls.AutoImport;

// extract.go:16-27 — symbolExtractor.
internal class SymbolExtractor
{
    internal string packageName = "";
    internal ExtractorStats stats = null!;

    internal global::Ts.Binder.NameResolver localNameResolver = null!;
    internal TsChecker checker = null!;
    internal Func<string, global::Ts.TsPath.Path>? toPath;
    // realpath, if set, is used to resolve symlinks for ModuleID generation.
    // This ensures that symlinked packages use their realpath as ModuleID,
    // deduplicating exports from files that appear via multiple symlink paths.
    internal Func<string, string>? realpath;

    // extract.go:59-70 — newSymbolExtractor.
    internal static SymbolExtractor NewSymbolExtractor(string packageName, TsChecker checker, Func<string, global::Ts.TsPath.Path>? toPath, Func<string, string>? realpath)
    {
        return new SymbolExtractor
        {
            packageName = packageName,
            checker = checker,
            localNameResolver = new global::Ts.Binder.NameResolver
            {
                CompilerOptions = global::Ts.Core.Core.EmptyCompilerOptions,
            },
            stats = new ExtractorStats(),
            toPath = toPath,
            realpath = realpath,
        };
    }

    // extract.go:79-86 — getModuleID: returns the ModuleID for a file, using realpath if available.
    internal ModuleID GetModuleID(SourceFile file)
    {
        if (realpath != null && toPath != null)
        {
            var real = realpath(file.FileName());
            return new ModuleID(toPath(real).Value);
        }
        return new ModuleID(file.Path().Value);
    }

    // extract.go:88-103 — getModuleIDForSymbol: returns the ModuleID for a module symbol, using
    // realpath normalization when available for source files.
    internal bool GetModuleIDForSymbol(Symbol symbol, out ModuleID moduleID)
    {
        if (!Util.TryGetModuleIDAndFileNameOfModuleSymbol(symbol, out moduleID, out var fileName))
        {
            return false;
        }
        // If fileName is set, this is a source file that may need realpath normalization
        if (fileName.Length != 0 && realpath != null)
        {
            var decl = AstUtilities.GetNonAugmentationDeclaration(symbol);
            if (decl != null && decl.Kind == SyntaxKind.SourceFile)
            {
                moduleID = GetModuleID(decl.AsSourceFile());
                return true;
            }
        }
        return true;
    }

    // extract.go:165-246 — extractFromSymbol.
    internal void ExtractFromSymbol(string name, Symbol symbol, ModuleID moduleID, string moduleFileName, SourceFile file, List<Export> exports)
    {
        if (ShouldIgnoreSymbol(symbol))
        {
            return;
        }

        if (name == InternalSymbolName.ExportStar)
        {
            var starCheckerLease = new CheckerLease { checker = checker };
            var allExports = new List<Symbol>(TsChecker.SymbolsToArray(checker.GetExportsOfModule(symbol.Parent!)));
            // allExports includes named exports from the file that will be processed separately;
            // we want to add only the ones that come from the star
            foreach (var (exportName, namedExport) in symbol.Parent!.Exports ?? []) // map-iter: 序无关 (removals from a set-like list; survivors keyed by identity)
            {
                if (exportName != InternalSymbolName.ExportStar)
                {
                    var idx = allExports.IndexOf(namedExport);
                    if (idx >= 0 || ShouldIgnoreSymbol(namedExport))
                    {
                        allExports.RemoveAt(idx);
                    }
                }
            }

            foreach (var reexportedSymbol in allExports)
            {
                var (starExport, _) = CreateExport(reexportedSymbol, moduleID, moduleFileName, ExportSyntax.Star, file, starCheckerLease);
                if (starExport != null)
                {
                    var parent = starCheckerLease.GetChecker().GetMergedSymbol(reexportedSymbol.Parent);
                    if (parent != null && parent.IsExternalModule())
                    {
                        if (GetModuleIDForSymbol(parent, out var targetModuleID))
                        {
                            starExport.Target = new ExportID(targetModuleID, reexportedSymbol.Name);
                        }
                    }
                    starExport.through = InternalSymbolName.ExportStar;
                    exports.Add(starExport);
                }
            }
            return;
        }

        var syntax = GetSyntax(symbol);
        var checkerLease = new CheckerLease { checker = checker };
        var (export, target) = CreateExport(symbol, moduleID, moduleFileName, syntax, file, checkerLease);
        if (export == null)
        {
            return;
        }

        exports.Add(export);

        if (target != null)
        {
            if (syntax == ExportSyntax.Equals && (target.Flags & SymbolFlags.Namespace) != 0)
            {
                foreach (var (innerName, namedExport) in target.Exports ?? []) // map-iter: 序无关 (export records are keyed by ExportID downstream)
                {
                    if (innerName != InternalSymbolName.ExportStar)
                    {
                        var (innerExport, _) = CreateExport(namedExport, moduleID, moduleFileName, syntax, file, checkerLease);
                        if (innerExport != null)
                        {
                            innerExport.through = name;
                            exports.Add(innerExport);
                        }
                    }
                }
            }
        }
        else if (syntax == ExportSyntax.CommonJSModuleExports)
        {
            var expression = symbol.Declarations![0].AsBinaryExpression().Right!;
            if (expression.Kind == SyntaxKind.ObjectLiteralExpression)
            {
                // what is actually desirable here? I think it would be reasonable to only treat these as exports
                // if *every* property is a shorthand property or identifier: identifier
                // At least, it would be sketchy if there were any methods, computed properties...
                foreach (var prop in expression.AsObjectLiteralExpression().Properties!.Nodes)
                {
                    if (NodeTests.IsShorthandPropertyAssignment(prop) || NodeTests.IsPropertyAssignment(prop) && prop.AsPropertyAssignment().Name()!.Kind == SyntaxKind.Identifier)
                    {
                        var (propExport, _) = CreateExport(expression.Symbol()!.Members![prop.Name()!.Text()], moduleID, moduleFileName, syntax, file, checkerLease);
                        if (propExport != null)
                        {
                            propExport.through = name;
                            exports.Add(propExport);
                        }
                    }
                }
            }
        }
    }

    // extract.go:248-352 — createExport: creates an Export for the given symbol, returning the
    // Export and the target symbol if the export is an alias.
    internal (Export? Export, Symbol? Target) CreateExport(Symbol symbol, ModuleID moduleID, string moduleFileName, ExportSyntax syntax, SourceFile file, CheckerLease checkerLease)
    {
        if (ShouldIgnoreSymbol(symbol))
        {
            return (null, null);
        }

        var export = new Export
        {
            ExportName = symbol.Name,
            ModuleID = moduleID,
            ModuleFileName = moduleFileName,
            Syntax = syntax,
            Flags = symbol.CombinedLocalAndExportSymbolFlags(),
            Path = file.Path(),
            PackageName = packageName,
        };

        if (syntax == ExportSyntax.UMD)
        {
            export.ExportName = InternalSymbolName.ExportEquals;
            export.localName = symbol.Name;
        }

        Symbol? targetSymbol = null;
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            targetSymbol = TryResolveSymbol(symbol, syntax, checkerLease);
            if (targetSymbol != null)
            {
                Node? decl = null;
                if ((targetSymbol.Declarations ?? []).Length > 0)
                {
                    decl = targetSymbol.Declarations![0];
                }
                else if ((targetSymbol.CheckFlags & CheckFlags.Mapped) != 0)
                {
                    if (checkerLease.GetChecker().GetMappedTypeSymbolOfProperty(targetSymbol) is { } mappedDecl && (mappedDecl.Declarations ?? []).Length > 0)
                    {
                        decl = mappedDecl.Declarations![0];
                    }
                }
                if (decl == null)
                {
                    // !!! consider GetImmediateAliasedSymbol to go as far as we can
                    decl = symbol.Declarations![0];
                }
                if (decl == null)
                {
                    throw new InvalidOperationException("no declaration for aliased symbol");
                }

                var parent = targetSymbol.Parent;
                if (checkerLease.TryChecker() is { } usedChecker)
                {
                    export.Flags = usedChecker.GetSymbolFlags(targetSymbol);
                    export.IsTypeOnly = usedChecker.GetTypeOnlyAliasDeclaration(symbol) != null;
                    parent = usedChecker.GetMergedSymbol(parent);
                }
                else
                {
                    export.Flags = targetSymbol.Flags;
                    export.IsTypeOnly = CoreUtil.Some(symbol.Declarations, TsChecker.IsPartOfTypeOnlyImportOrExportDeclaration);
                }
                export.ScriptElementKind = SymbolDisplay.GetSymbolKind(checkerLease.TryChecker(), targetSymbol, decl);
                export.ScriptElementKindModifiers = SymbolDisplay.GetSymbolModifiers(checkerLease.TryChecker(), targetSymbol);
                var targetModuleID = new ModuleID(AstUtilities.GetSourceFileOfNode(decl)!.Path().Value);
                if (parent != null && parent.IsExternalModule())
                {
                    if (GetModuleIDForSymbol(parent, out var id))
                    {
                        targetModuleID = id;
                    }
                }
                export.Target = new ExportID(targetModuleID, targetSymbol.Name);
            }
        }
        else
        {
            export.ScriptElementKind = SymbolDisplay.GetSymbolKind(checkerLease.TryChecker(), symbol, symbol.Declarations![0]);
            export.ScriptElementKindModifiers = SymbolDisplay.GetSymbolModifiers(checkerLease.TryChecker(), symbol);
        }

        if (symbol.Name == InternalSymbolName.Default || symbol.Name == InternalSymbolName.ExportEquals)
        {
            var namedSymbol = symbol;
            if (global::Ts.Binder.NameResolver.GetLocalSymbolForExportDefault(symbol) is { } s)
            {
                namedSymbol = s;
            }
            export.localName = Util.GetDefaultLikeExportNameFromDeclaration(namedSymbol);
            if (IsUnusableName(export.localName))
            {
                export.localName = export.Target.ExportName;
            }
            if (IsUnusableName(export.localName))
            {
                if (targetSymbol != null)
                {
                    namedSymbol = targetSymbol;
                    if (global::Ts.Binder.NameResolver.GetLocalSymbolForExportDefault(targetSymbol) is { } s2)
                    {
                        namedSymbol = s2;
                    }
                    export.localName = Util.GetDefaultLikeExportNameFromDeclaration(namedSymbol);
                }
            }
            if (IsUnusableName(export.localName))
            {
                // Last resort: derive identifier from the file name. Use FileName() (original
                // casing) rather than ModuleID/Path() which is lowercased on case-insensitive
                // file systems, losing PascalCase.
                export.localName = LsDeps.ModuleSpecifierToValidIdentifier(FileNameForDefaultExportName(targetSymbol, moduleFileName, moduleID), false);
            }
        }

        if (IsUnusableName(export.Name()))
        {
            return (null, null);
        }

        Interlocked.Increment(ref stats.exports);
        if (checkerLease.TryChecker() != null)
        {
            Interlocked.Increment(ref stats.usedChecker);
        }

        return (export, targetSymbol);
    }

    // extract.go:354-397 — tryResolveSymbol.
    internal Symbol? TryResolveSymbol(Symbol symbol, ExportSyntax syntax, CheckerLease checkerLease)
    {
        if (!AstUtilities.IsNonLocalAlias(symbol, SymbolFlags.None))
        {
            return symbol;
        }

        Node? loc = null;
        var name = "";
        switch (syntax)
        {
            case ExportSyntax.Named:
            {
                var decl = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.ExportSpecifier)!;
                if (decl.Parent!.Parent!.AsExportDeclaration().ModuleSpecifier == null)
                {
                    var n = decl.Name() ?? decl.PropertyName();
                    if (n!.Kind == SyntaxKind.Identifier)
                    {
                        loc = n;
                        name = n.Text();
                    }
                }
                break;
            }
            // !!! check if module.exports = foo is marked as an alias
            case ExportSyntax.Equals:
                if (symbol.Name != InternalSymbolName.ExportEquals)
                {
                    break;
                }
                goto case ExportSyntax.DefaultDeclaration;
            case ExportSyntax.DefaultDeclaration:
            {
                var decl = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.ExportAssignment)!;
                if (decl.Expression()!.Kind == SyntaxKind.Identifier)
                {
                    loc = decl.Expression();
                    name = loc!.Text();
                }
                break;
            }
        }

        if (loc != null)
        {
            var local = localNameResolver.Resolve(loc, name, SymbolFlags.All, null, false, false);
            if (local != null && !AstUtilities.IsNonLocalAlias(local, SymbolFlags.None))
            {
                return local;
            }
        }

        var ch = checkerLease.GetChecker();
        if (ch.GetAliasedSymbol(symbol) is { } resolved && !ch.IsUnknownSymbol(resolved))
        {
            return resolved;
        }
        return null;
    }

    // extract.go:399-404 — shouldIgnoreSymbol.
    internal static bool ShouldIgnoreSymbol(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.Prototype) != 0)
        {
            return true;
        }
        return false;
    }

    // extract.go:406-434 — getSyntax.
    internal static ExportSyntax GetSyntax(Symbol symbol)
    {
        foreach (var decl in symbol.Declarations ?? [])
        {
            switch (decl.Kind)
            {
                case SyntaxKind.ExportSpecifier:
                    return ExportSyntax.Named;
                case SyntaxKind.ExportAssignment:
                    return decl.AsExportAssignment().IsExportEquals
                        ? ExportSyntax.Equals
                        : ExportSyntax.DefaultDeclaration;
                case SyntaxKind.NamespaceExportDeclaration:
                    return ExportSyntax.UMD;
                case SyntaxKind.BinaryExpression:
                    switch (AstUtilities.GetAssignmentDeclarationKind(decl))
                    {
                        case JSDeclarationKind.ModuleExports:
                            return ExportSyntax.CommonJSModuleExports;
                        case JSDeclarationKind.ExportsProperty:
                            return ExportSyntax.CommonJSExportsProperty;
                    }
                    break;
                default:
                    if ((AstUtilities.GetCombinedModifierFlags(decl) & ModifierFlags.Default) != 0)
                    {
                        return ExportSyntax.DefaultModifier;
                    }
                    else
                    {
                        return ExportSyntax.Modifier;
                    }
            }
        }
        return ExportSyntax.None;
    }

    // extract.go:436-443 — isUnusableName.
    internal static bool IsUnusableName(string name)
    {
        return name.Length == 0 ||
            name == "_default" ||
            name == InternalSymbolName.ExportStar ||
            name == InternalSymbolName.Default ||
            name == InternalSymbolName.ExportEquals;
    }

    // extract.go:445-461 — fileNameForDefaultExportName: returns the best file name to use when
    // deriving a fallback identifier for a default-like export. It prefers the target symbol's
    // source file (closest to the export origin), falls back to the module's original file name,
    // and uses the lowercased moduleID only for ambient modules where no original file name is
    // available.
    internal static string FileNameForDefaultExportName(Symbol? targetSymbol, string moduleFileName, ModuleID moduleID)
    {
        if (targetSymbol != null && (targetSymbol.Declarations ?? []).Length > 0)
        {
            var fn = AstUtilities.GetSourceFileOfNode(targetSymbol.Declarations![0])!.FileName();
            if (fn.Length != 0)
            {
                return fn;
            }
        }
        if (moduleFileName.Length != 0)
        {
            return moduleFileName;
        }
        return moduleID.Value;
    }
}

// extract.go:29-32 — exportExtractor.
internal sealed class ExportExtractor : SymbolExtractor
{
    internal global::Ts.Module.Resolver moduleResolver = null!;

    // extract.go:72-77 — (registryBuilder) newExportExtractor. PORT-NOTE: the registry batch owns
    // the builder; until then the builder-supplied toPath is passed directly.
    internal static ExportExtractor NewExportExtractor(string packageName, TsChecker checker, global::Ts.Module.Resolver moduleResolver, Func<string, global::Ts.TsPath.Path> toPath, Func<string, string>? realpath)
    {
        var baseExtractor = NewSymbolExtractor(packageName, checker, toPath, realpath);
        return new ExportExtractor
        {
            packageName = baseExtractor.packageName,
            stats = baseExtractor.stats,
            localNameResolver = baseExtractor.localNameResolver,
            checker = baseExtractor.checker,
            toPath = baseExtractor.toPath,
            realpath = baseExtractor.realpath,
            moduleResolver = moduleResolver,
        };
    }

    // extract.go:38-40 — Stats.
    internal ExtractorStats Stats() => stats;

    // extract.go:105-124 — extractFromFile.
    internal List<Export> ExtractFromFile(SourceFile file)
    {
        if (file.Symbol != null)
        {
            return ExtractFromModule(file);
        }
        if ((file.AmbientModuleNames ?? []).Length > 0)
        {
            var moduleDeclarations = CoreUtil.Filter(file.Statements!.Nodes, AstUtilities.IsModuleWithStringLiteralName) ?? [];
            var exports = new List<Export>();
            foreach (var decl in moduleDeclarations)
            {
                ExtractFromModuleDeclaration(decl, file, new ModuleID(decl.Name()!.Text()), "", exports);
            }
            return exports;
        }
        return [];
    }

    // extract.go:126-157 — extractFromModule.
    internal List<Export> ExtractFromModule(SourceFile file)
    {
        var moduleAugmentations = CoreUtil.MapNonNil<Node, Node>(file.ModuleAugmentations, name =>
        {
            var decl = name.Parent!;
            if (AstUtilities.IsGlobalScopeAugmentation(decl))
            {
                return null!;
            }
            return decl;
        }) ?? [];
        var moduleID = GetModuleID(file);
        var exports = new List<Export>();
        foreach (var (name, symbol) in file.Symbol!.Exports ?? []) // map-iter: 序无关 (export records keyed by ExportID downstream)
        {
            ExtractFromSymbol(name, symbol, moduleID, file.FileName(), file, exports);
        }
        foreach (var decl in moduleAugmentations)
        {
            var name = decl.Name()!.AsStringLiteral().Text!;
            var augModuleID = new ModuleID(name);
            var moduleFileName = "";
            if (TsPathApi.IsExternalModuleNameRelative(name))
            {
                var (resolved, _) = moduleResolver.ResolveModuleName(name, file.FileName(), global::Ts.Core.ModuleKind.CommonJS, null);
                if (global::Ts.Module.ResolvedModuleExtensions.IsResolved(resolved))
                {
                    moduleFileName = resolved.ResolvedFileName;
                    augModuleID = new ModuleID(toPath!(moduleFileName).Value);
                }
                else
                {
                    // :shrug:
                    moduleFileName = TsPathApi.ResolvePath(TsPathApi.GetDirectoryPath(file.FileName()), name);
                    augModuleID = new ModuleID(toPath!(moduleFileName).Value);
                }
            }
            ExtractFromModuleDeclaration(decl, file, augModuleID, moduleFileName, exports);
        }
        return exports;
    }

    // extract.go:159-163 — extractFromModuleDeclaration.
    internal void ExtractFromModuleDeclaration(Node decl, SourceFile file, ModuleID moduleID, string moduleFileName, List<Export> exports)
    {
        foreach (var (name, symbol) in decl.Symbol()!.Exports ?? []) // map-iter: 序无关 (export records keyed by ExportID downstream)
        {
            ExtractFromSymbol(name, symbol, moduleID, moduleFileName, file, exports);
        }
    }
}

// extract.go:34-37 — extractorStats.
internal sealed class ExtractorStats
{
    internal int exports;
    internal int usedChecker;
}

// extract.go:42-57 — checkerLease.
internal sealed class CheckerLease
{
    internal bool used;
    internal TsChecker checker = null!;

    internal TsChecker GetChecker()
    {
        used = true;
        return checker;
    }

    internal TsChecker? TryChecker()
    {
        if (used)
        {
            return checker;
        }
        return null;
    }
}
