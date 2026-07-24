// Ported from tsgo internal/ls/autoimport/import_adder.go (whole file): the ImportAdder that
// coalesces multiple auto-import fixes for the same module into single import statements, and
// the import-type-node → type-reference rewriting helpers.
// PORT-NOTE: ctx drops (Tracker/locale convention, see Fix.cs header); View.SearchByExportID
// lands with the registry/view wave (stub in Fix.cs).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Ls.AutoImport;

// import_adder.go:24-29 — ImportAdder.
public interface ImportAdder
{
    bool HasFixes();

    void AddImportFromExportedSymbol(Symbol symbol, bool isValidTypeOnlyUseSite);

    void AddImportFix(Fix fix);

    global::Ts.LsProto.TextEdit[]? Edits();
}

// import_adder.go:31-36 — addToExistingState tracks modifications to an existing import clause
// or binding pattern.
internal sealed class AddToExistingState
{
    internal Node importClauseOrBindingPattern = null!;
    internal NewImportBinding? defaultImport;
    internal Dictionary<string, NewImportBinding> namedImports = null!;
}

// import_adder.go:38-44 — importsCollection tracks new imports to be created for a given
// module specifier.
internal sealed class ImportsCollection
{
    internal NewImportBinding? defaultImport;
    internal Dictionary<string, NewImportBinding>? namedImports;
    internal NewImportBinding? namespaceLikeImport;
    internal bool useRequire;
}

// import_adder.go:53-68 — importAdder.
internal sealed class ImportAdderImpl : ImportAdder
{
    // Context
    internal global::Ts.Checker.Checker checker = null!;
    internal View view = null!;
    internal global::Ts.Format.FormatCodeSettings formatOptions = null!;
    internal global::Ts.Ls.LsConv.Converters converters = null!;
    internal UserPreferences preferences = null!;

    // State
    internal List<Fix> addToNamespace = new();                                    // Namespace fixes don't conflict, so just build a list
    internal List<Fix> importType = new();                                        // JSDoc type import fixes
    internal Dictionary<Node, AddToExistingState> addToExisting = null!;          // importClauseOrBindingPattern -> default or named bindings
    internal Dictionary<string, ImportsCollection> newImports = null!;            // module specifier + type only -> imports
    // !!! removeExisting, verbatimImports?

    // import_adder.go:94-99 — HasFixes.
    public bool HasFixes()
    {
        return addToNamespace.Count > 0 ||
            importType.Count > 0 ||
            addToExisting.Count > 0 ||
            newImports.Count > 0;
    }

    // !!! referenceImport
    // import_adder.go:102-116 — AddImportFromExportedSymbol.
    public void AddImportFromExportedSymbol(Symbol exportedSymbol, bool isValidTypeOnlyUseSite)
    {
        var symbol = checker.GetMergedSymbol(checker.SkipAliasSymbol(exportedSymbol))!;
        var exportInfos = GetAllExportsForSymbol(symbol);
        if ((exportInfos ?? []).Length == 0)
        {
            // If no exportInfo is found, this means export could not be resolved when we have filtered for autoImportFileExcludePatterns,
            //     so we should not generate an import.
            // debug.Assert(len(adder.ls.UserPreferences().AutoImportFileExcludePatterns) > 0)
            return;
        }
        var fix = GetImportFixForSymbol(view, view.importingFile, exportInfos!, isValidTypeOnlyUseSite);
        if (fix != null)
        {
            // !!! referenceImport -> propertyName
            AddImportFix(fix);
        }
    }

    // import_adder.go:118-173 — Edits.
    public global::Ts.LsProto.TextEdit[]? Edits()
    {
        // !!! organize imports?
        var tracker = global::Ts.Ls.Change.Tracker.NewTracker(view.program.Options(), formatOptions, converters);
        var quotePreference = LsDeps.GetQuotePreference(view.importingFile, preferences);
        foreach (var fix in addToNamespace)
        {
            AutoImportApi.AddNamespaceQualifier(fix, tracker, view.importingFile, global::Ts.Locale.Locale.Default);
        }
        foreach (var fix in importType)
        {
            AutoImportApi.AddImportType(fix, view.importingFile, preferences, tracker, global::Ts.Locale.Locale.Default);
        }
        // map-iter: 序无关 (Go 迭代序随机; 生成的编辑按位置去重/排序于 tracker 内部收敛)
        foreach (var (clauseOrPattern, entry) in addToExisting)
        {
            AutoImportApi.AddToExistingImport(
                tracker,
                view.importingFile,
                clauseOrPattern,
                entry.defaultImport,
                SortedNamedImports(entry.namedImports),
                preferences);
        }

        var newDeclarations = new List<Statement>();
        // map-iter: 序无关 (Go 迭代序随机; insertImports 内部按 comparer 重排)
        foreach (var (key, newImport) in newImports)
        {
            var moduleSpecifier = key[2..]; // From `${0 | 1}|${moduleSpecifier}` format
            Statement[] declarations;
            if (newImport.useRequire)
            {
                declarations = AutoImportApi.GetNewRequires(
                    tracker,
                    moduleSpecifier,
                    quotePreference,
                    newImport.defaultImport,
                    SortedNamedImports(newImport.namedImports ?? new Dictionary<string, NewImportBinding>()),
                    newImport.namespaceLikeImport,
                    view.program.Options());
            }
            else
            {
                declarations = AutoImportApi.GetNewImports(
                    tracker,
                    moduleSpecifier,
                    quotePreference,
                    newImport.defaultImport,
                    SortedNamedImports(newImport.namedImports ?? new Dictionary<string, NewImportBinding>()),
                    newImport.namespaceLikeImport,
                    view.program.Options(),
                    preferences);
            }
            newDeclarations.AddRange(declarations);
        }

        if (newDeclarations.Count > 0)
        {
            AutoImportApi.InsertImports(tracker, view.importingFile, newDeclarations.ToArray(), true /*blankLineBetween*/, preferences);
        }

        tracker.GetChanges().TryGetValue(view.importingFile.FileName(), out var edits);
        return edits?.ToArray();
    }

    // import_adder.go:175-182 — sortedNamedImports.
    internal static NewImportBinding[] SortedNamedImports(Dictionary<string, NewImportBinding> m)
    {
        // map-iter: 已排序 (slices.Sorted(maps.Keys(m)))
        var keys = new List<string>(m.Keys);
        keys.Sort(StringComparer.Ordinal);
        var result = new List<NewImportBinding>(keys.Count);
        foreach (var k in keys)
        {
            result.Add(m[k]);
        }
        return result.ToArray();
    }

    // import_adder.go:184-319 — AddImportFix adds a fix to the import adder, accumulating it
    // with other fixes so that multiple imports from the same module are coalesced into a
    // single import statement.
    public void AddImportFix(Fix fix)
    {
        var symbolName = fix.Name;
        var compilerOptions = view.program.Options();

        switch (fix.Kind)
        {
            case global::Ts.LsProto.AutoImportFixKind.UseNamespace:
                addToNamespace.Add(fix);
                break;
            case global::Ts.LsProto.AutoImportFixKind.JsdocTypeImport:
                importType.Add(fix);
                break;
            case global::Ts.LsProto.AutoImportFixKind.AddToExisting:
            {
                var existingFix = AutoImportApi.GetAddToExistingImportFix(view.importingFile, fix);
                addToExisting.TryGetValue(existingFix.importClauseOrBindingPattern, out var entry);
                if (entry == null)
                {
                    entry = new AddToExistingState
                    {
                        importClauseOrBindingPattern = existingFix.importClauseOrBindingPattern,
                        namedImports = new Dictionary<string, NewImportBinding>(StringComparer.Ordinal),
                    };
                    addToExisting[existingFix.importClauseOrBindingPattern] = entry;
                }

                if (fix.ImportKind == global::Ts.LsProto.ImportKind.Named)
                {
                    entry.namedImports.TryGetValue(symbolName, out var prevImport);
                    global::Ts.LsProto.AddAsTypeOnly prevTypeOnly = 0;
                    if (prevImport != null)
                    {
                        prevTypeOnly = prevImport.addAsTypeOnly;
                    }
                    entry.namedImports[symbolName] = new NewImportBinding
                    {
                        kind = global::Ts.LsProto.ImportKind.Named,
                        name = symbolName,
                        addAsTypeOnly = AutoImportApi.ReduceAddAsTypeOnlyValues(prevTypeOnly, fix.AddAsTypeOnly),
                        propertyName = existingFix.namedImport!.propertyName,
                    };
                }
                else
                {
                    // Default import
                    Debug.Assert(
                        entry.defaultImport == null || entry.defaultImport.name == symbolName,
                        "(Add to Existing) Default import should be missing or match symbolName");
                    global::Ts.LsProto.AddAsTypeOnly prevTypeOnly = 0;
                    if (entry.defaultImport != null)
                    {
                        prevTypeOnly = entry.defaultImport.addAsTypeOnly;
                    }
                    entry.defaultImport = new NewImportBinding
                    {
                        kind = global::Ts.LsProto.ImportKind.Default,
                        name = symbolName,
                        addAsTypeOnly = AutoImportApi.ReduceAddAsTypeOnlyValues(prevTypeOnly, fix.AddAsTypeOnly),
                    };
                }
                break;
            }

            case global::Ts.LsProto.AutoImportFixKind.AddNew:
            {
                var entry = GetNewImportEntry(fix.ModuleSpecifier, fix.ImportKind, fix.UseRequire, fix.AddAsTypeOnly);
                Debug.Assert(
                    entry.useRequire == fix.UseRequire,
                    "(Add new) Tried to add an `import` and a `require` for the same module");

                switch (fix.ImportKind)
                {
                    case global::Ts.LsProto.ImportKind.Default:
                    {
                        Debug.Assert(
                            entry.defaultImport == null || entry.defaultImport.name == symbolName,
                            "(Add new) Default import should be missing or match symbolName");
                        global::Ts.LsProto.AddAsTypeOnly prevTypeOnly = 0;
                        if (entry.defaultImport != null)
                        {
                            prevTypeOnly = entry.defaultImport.addAsTypeOnly;
                        }
                        entry.defaultImport = new NewImportBinding
                        {
                            kind = global::Ts.LsProto.ImportKind.Default,
                            name = symbolName,
                            addAsTypeOnly = AutoImportApi.ReduceAddAsTypeOnlyValues(prevTypeOnly, fix.AddAsTypeOnly),
                        };
                        break;
                    }

                    case global::Ts.LsProto.ImportKind.Named:
                    {
                        entry.namedImports ??= new Dictionary<string, NewImportBinding>(StringComparer.Ordinal);
                        entry.namedImports.TryGetValue(symbolName, out var prevImport);
                        global::Ts.LsProto.AddAsTypeOnly prevTypeOnly = 0;
                        if (prevImport != null)
                        {
                            prevTypeOnly = prevImport.addAsTypeOnly;
                        }
                        entry.namedImports[symbolName] = new NewImportBinding
                        {
                            kind = global::Ts.LsProto.ImportKind.Named,
                            name = symbolName,
                            addAsTypeOnly = AutoImportApi.ReduceAddAsTypeOnlyValues(prevTypeOnly, fix.AddAsTypeOnly),
                            // !!! propertyName
                        };
                        break;
                    }

                    case global::Ts.LsProto.ImportKind.CommonJS:
                        if (compilerOptions.VerbatimModuleSyntax == Tristate.True)
                        {
                            entry.namedImports ??= new Dictionary<string, NewImportBinding>(StringComparer.Ordinal);
                            entry.namedImports.TryGetValue(symbolName, out var prevImport);
                            global::Ts.LsProto.AddAsTypeOnly prevTypeOnly = 0;
                            if (prevImport != null)
                            {
                                prevTypeOnly = prevImport.addAsTypeOnly;
                            }
                            entry.namedImports[symbolName] = new NewImportBinding
                            {
                                kind = global::Ts.LsProto.ImportKind.CommonJS,
                                name = symbolName,
                                addAsTypeOnly = AutoImportApi.ReduceAddAsTypeOnlyValues(prevTypeOnly, fix.AddAsTypeOnly),
                                // !!! propertyName
                            };
                        }
                        else
                        {
                            Debug.Assert(
                                entry.namespaceLikeImport == null || entry.namespaceLikeImport.name == symbolName,
                                "Namespacelike import should be missing or match symbolName");
                            entry.namespaceLikeImport = new NewImportBinding
                            {
                                kind = global::Ts.LsProto.ImportKind.CommonJS,
                                name = symbolName,
                                addAsTypeOnly = fix.AddAsTypeOnly,
                            };
                        }
                        break;

                    case global::Ts.LsProto.ImportKind.Namespace:
                        Debug.Assert(
                            entry.namespaceLikeImport == null || entry.namespaceLikeImport.name == symbolName,
                            "Namespacelike import should be missing or match symbolName");
                        entry.namespaceLikeImport = new NewImportBinding
                        {
                            kind = global::Ts.LsProto.ImportKind.Namespace,
                            name = symbolName,
                            addAsTypeOnly = fix.AddAsTypeOnly,
                        };
                        break;
                }
                break;
            }

            case global::Ts.LsProto.AutoImportFixKind.PromoteTypeOnly:
                // Excluding from fix-all
                break;
            default:
                Debug.Fail($"Unexpected fix kind: {fix.Kind}");
                break;
        }
    }

    // import_adder.go:334-370 — getNewImportEntry.
    internal ImportsCollection GetNewImportEntry(string moduleSpecifier, global::Ts.LsProto.ImportKind importKind, bool useRequire, global::Ts.LsProto.AddAsTypeOnly addAsTypeOnly)
    {
        // A default import that requires type-only makes the whole import type-only.
        // (We could add `default` as a named import, but that style seems undesirable.)
        // Under `--preserveValueImports` and `--importsNotUsedAsValues=error`, if a
        // module default-exports a type but named-exports some values (weird), you would
        // have to use a type-only default import and non-type-only named imports. These
        // require two separate import declarations, so we build this into the map key.
        var typeOnlyKey = AutoImportApi.NewImportsKey(moduleSpecifier, true /*topLevelTypeOnly*/);
        var nonTypeOnlyKey = AutoImportApi.NewImportsKey(moduleSpecifier, false /*topLevelTypeOnly*/);
        newImports.TryGetValue(typeOnlyKey, out var typeOnlyEntry);
        newImports.TryGetValue(nonTypeOnlyKey, out var nonTypeOnlyEntry);
        var newEntry = new ImportsCollection
        {
            useRequire = useRequire,
        };

        if (importKind == global::Ts.LsProto.ImportKind.Default && addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.Required)
        {
            if (typeOnlyEntry != null)
            {
                return typeOnlyEntry;
            }
            newImports[typeOnlyKey] = newEntry;
            return newEntry;
        }

        if (addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.Allowed && (typeOnlyEntry != null || nonTypeOnlyEntry != null))
        {
            if (typeOnlyEntry != null)
            {
                return typeOnlyEntry;
            }
            return nonTypeOnlyEntry!;
        }

        if (nonTypeOnlyEntry != null)
        {
            return nonTypeOnlyEntry;
        }

        newImports[nonTypeOnlyKey] = newEntry;
        return newEntry;
    }

    // import_adder.go:372-379 — getAllExportsForSymbol.
    internal Export[]? GetAllExportsForSymbol(Symbol symbol)
    {
        var export = AutoImportApi.SymbolToExport(symbol, checker);
        if (export != null)
        {
            return view.SearchByExportID(export.ExportID);
        }
        return null;
    }

    // import_adder.go:490-501 — getImportFixForSymbol.
    internal Fix? GetImportFixForSymbol(View viewArg, SourceFile file, Export[] exports, bool isValidTypeOnlyUseSite)
    {
        var fixes = CoreUtil.FlatMap(exports, export =>
            viewArg.GetFixes(export, false /*forJSX*/, isValidTypeOnlyUseSite, null /*usagePosition*/)) ?? [];
        Array.Sort(fixes, (a, b) => viewArg.CompareFixesForRanking(a, b));
        if (fixes.Length > 0)
        {
            return fixes[0];
        }
        return null;
    }
}

public static partial class AutoImportApi
{
    // import_adder.go:46-51 — newImportsKey.
    internal static string NewImportsKey(string moduleSpecifier, bool topLevelTypeOnly)
    {
        if (topLevelTypeOnly)
        {
            return "1|" + moduleSpecifier;
        }
        return "0|" + moduleSpecifier;
    }

    // import_adder.go:70-92 — NewImportAdder.
    public static ImportAdder NewImportAdder(
        global::Ts.Compiler.Program program,
        global::Ts.Checker.Checker checker,
        SourceFile file,
        View view,
        global::Ts.Format.FormatCodeSettings formatOptions,
        global::Ts.Ls.LsConv.Converters converters,
        UserPreferences preferences)
    {
        return new ImportAdderImpl
        {
            checker = checker,
            view = view,
            formatOptions = formatOptions,
            converters = converters,
            preferences = preferences,
            addToNamespace = new List<Fix>(),
            importType = new List<Fix>(),
            addToExisting = new Dictionary<Node, AddToExistingState>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance),
            newImports = new Dictionary<string, ImportsCollection>(StringComparer.Ordinal),
        };
    }

    // import_adder.go:321-332 — reduceAddAsTypeOnlyValues:
    // `NotAllowed` overrides `Required` because one addition of a new import might be required to be type-only
    // because of `--importsNotUsedAsValues=error`, but if a second addition of the same import is `NotAllowed`
    // to be type-only, the reason the first one was `Required` - the unused runtime dependency - is now moot.
    // Alternatively, if one addition is `Required` because it has no value meaning under `--preserveValueImports`
    // and `--isolatedModules`, it should be impossible for another addition to be `NotAllowed` since that would
    // mean a type is being referenced in a value location.
    internal static global::Ts.LsProto.AddAsTypeOnly ReduceAddAsTypeOnlyValues(global::Ts.LsProto.AddAsTypeOnly prevValue, global::Ts.LsProto.AddAsTypeOnly newValue)
    {
        if (newValue > prevValue)
        {
            return newValue;
        }
        return prevValue;
    }

    // import_adder.go:381-393 — TypeToAutoImportableTypeNode.
    public static Node? TypeToAutoImportableTypeNode(
        global::Ts.Checker.Checker c,
        ImportAdder importAdder,
        global::Ts.Checker.Type t,
        Node contextNode) // !!! flags
    {
        var idToSymbol = new Dictionary<Node, Symbol>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        var typeNode = c.TypeToTypeNode(t, contextNode, global::Ts.NodeBuilder.Flags.None, idToSymbol);
        if (typeNode == null)
        {
            return null;
        }
        return TypeNodeToAutoImportableTypeNode(typeNode, importAdder, idToSymbol);
    }

    // import_adder.go:395-412 — TypeNodeToAutoImportableTypeNode converts import type
    // references in a type node to simple type references and registers needed imports with
    // the import adder.
    public static Node? TypeNodeToAutoImportableTypeNode(
        Node typeNode,
        ImportAdder? importAdder,
        Dictionary<Node, Symbol> idToSymbol)
    {
        var (referenceTypeNode, importableSymbols) = TryGetAutoImportableReferenceFromTypeNode(typeNode, idToSymbol);
        if (referenceTypeNode != null)
        {
            if (importAdder != null)
            {
                ImportSymbols(importAdder, importableSymbols);
            }
            typeNode = referenceTypeNode;
        }

        // !!! handle type node reuse: nodes needs to be fresh here but also preserve symbols
        return typeNode;
    }

    // import_adder.go:414-418 — importSymbols.
    internal static void ImportSymbols(ImportAdder importAdder, List<Symbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            importAdder.AddImportFromExportedSymbol(symbol, true /*isValidTypeOnlyUseSite*/);
        }
    }

    // import_adder.go:420-460 — TryGetAutoImportableReferenceFromTypeNode: given a type node
    // containing 'import("./a").SomeType<import("./b").OtherType<...>>', returns an equivalent
    // type reference node with any nested ImportTypeNodes also replaced with type references,
    // and a list of symbols that must be imported to use the type reference.
    public static (Node? TypeNode, List<Symbol> Symbols) TryGetAutoImportableReferenceFromTypeNode(Node importTypeNodeArg, Dictionary<Node, Symbol> idToSymbol)
    {
        var symbols = new List<Symbol>();
        NodeVisitor visitor = null!;
        var factory = new NodeFactory(new NodeFactoryHooks());
        Func<Node, Node?> visit = node =>
        {
            if (AstUtilities.IsLiteralImportTypeNode(node) && node.AsImportTypeNode().Qualifier != null)
            {
                var importTypeNode = node.AsImportTypeNode();
                // Symbol for the left-most thing after the dot
                var firstIdentifier = global::Ts.Checker.Checker.GetFirstIdentifier(importTypeNode.Qualifier!);
                idToSymbol.TryGetValue(firstIdentifier, out var symbol);
                if (symbol == null)
                {
                    // if symbol is missing then this doesn't come from a synthesized import type node
                    // it has to be an import type node authored by the user and thus it has to be valid
                    // it can't refer to reserved internal symbol names and such
                    return visitor.VisitEachChild(node);
                }
                var name = GetNameForExportedSymbol(symbol, false /*preferCapitalized*/);
                Node? qualifier;
                if (name != firstIdentifier.Text())
                {
                    qualifier = ReplaceFirstIdentifierOfEntityName(factory, importTypeNode.Qualifier!, factory.NewIdentifier(name));
                }
                else
                {
                    qualifier = importTypeNode.Qualifier;
                }
                symbols.Add(symbol);
                var typeArguments = visitor.VisitNodes(importTypeNode.TypeArguments);
                return factory.NewTypeReferenceNode(qualifier, typeArguments);
            }
            return visitor.VisitEachChild(node);
        };
        visitor = new NodeVisitor(visit, factory, new NodeVisitorHooks());

        var typeNode = visitor.VisitNode(importTypeNodeArg);
        Debug.Assert(typeNode == null || AstUtilities.IsTypeNode(typeNode), "expected a type node");
        return (typeNode, symbols);
    }

    // import_adder.go:462-478 — getNameForExportedSymbol: if a type checker and multiple files
    // are available, consider using `forEachNameOfDefaultExport` instead, which searches for
    // names of re-exported defaults/namespaces in target files.
    internal static string GetNameForExportedSymbol(Symbol symbol, bool preferCapitalized)
    {
        if (symbol.Name == InternalSymbolName.ExportEquals || symbol.Name == InternalSymbolName.Default)
        {
            // Names for default exports:
            // - export default foo => foo
            // - export { foo as default } => foo
            // - export default 0 => filename converted to camelCase
            var name = Util.GetDefaultLikeExportNameFromDeclaration(symbol);
            if (name.Length != 0)
            {
                return name;
            }
            Debug.Assert(symbol.Parent != null, "Expected exported symbol to have module symbol as parent");
            return LsDeps.ModuleSymbolToValidIdentifier(symbol.Parent!, preferCapitalized);
        }
        return symbol.Name;
    }

    // import_adder.go:480-488 — replaceFirstIdentifierOfEntityName.
    internal static Node ReplaceFirstIdentifierOfEntityName(NodeFactory factory, Node name, Node newIdentifier)
    {
        if (name.Kind == SyntaxKind.Identifier)
        {
            return newIdentifier;
        }
        return factory.NewQualifiedName(
            ReplaceFirstIdentifierOfEntityName(factory, name.AsQualifiedName().Left!, newIdentifier),
            name.AsQualifiedName().Right);
    }
}
