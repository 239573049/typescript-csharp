// Ported from tsgo internal/checker/symbolaccessibility.go — symbol accessibility, backing the
// nodebuilder's type-alias-name rendering arm (IsTypeSymbolAccessible gates whether a type prints as
// its alias name vs its expanded structure) and .d.ts declaration emit.
//
// PORT-NOTE (deviation, AGENTS.md §6): Go's `symbolTableID` — a discriminated identifier for
// locals/exports/members/globals tables used for (1) the visited-set cycle guard, (2) the isGlobals
// check, and (3) per-table alias/chain caching — is replaced by SymbolTable *reference identity*
// (SymbolTable is `Dictionary<string,Symbol>`, a reference type): the visited-set is a
// HashSet<SymbolTable>; isGlobals is `ReferenceEquals(table, globals)`; the per-table alias/chain caches
// are dropped (recomputed each call, behaviorally identical, only slower), but the *symbol-container*
// links (ContainingSymbolLinks.extendedContainers/extendedContainersByFile/accessibleChainCache, keyed
// off the symbol, not the table) are retained. The dropped callback param `tableId` is removed from the
// someSymbolTableInScope callback signature.
//
// PORT-NOTE (deferred): JSDoc reparse (GetReparsedNodeForNode) is deferred (TS conformance does not hit
// it); the raw node is used. accessibleChainCache is present but not consulted (recomputed each call).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using PrinterNs = global::Ts.Printer;

namespace Ts.Checker;

public partial class Checker
{
    // symbolaccessibility.go accessibleSymbolChainContext (visited map keyed by SymbolTable reference).
    private sealed class AccessibleSymbolChainContext
    {
        internal Symbol? symbol;
        internal readonly Node? enclosingDeclaration;
        internal readonly SymbolFlags meaning;
        internal readonly bool useOnlyExternalAliasing;
        internal readonly Dictionary<SymbolId, HashSet<SymbolTable>> visitedSymbolTablesMap;

        internal AccessibleSymbolChainContext(Symbol? symbol, Node? enclosingDeclaration, SymbolFlags meaning,
            bool useOnlyExternalAliasing, Dictionary<SymbolId, HashSet<SymbolTable>> visited)
        {
            this.symbol = symbol;
            this.enclosingDeclaration = enclosingDeclaration;
            this.meaning = meaning;
            this.useOnlyExternalAliasing = useOnlyExternalAliasing;
            this.visitedSymbolTablesMap = visited;
        }
    }

    // ---- leaves ----

    // symbolaccessibility.go — qualified-left meaning.
    internal static SymbolFlags GetQualifiedLeftMeaning(SymbolFlags rightMeaning) =>
        rightMeaning == SymbolFlags.Value ? SymbolFlags.Value : SymbolFlags.Namespace;

    // symbolaccessibility.go:_ hasNonGlobalAugmentationExternalModuleSymbol.
    internal static bool HasNonGlobalAugmentationExternalModuleSymbol(Node declaration) =>
        AstUtilities.IsModuleWithStringLiteralName(declaration) ||
        (declaration.Kind == SyntaxKind.SourceFile && AstUtilities.IsExternalOrCommonJSModule(declaration.AsSourceFile()));

    // symbolaccessibility.go isUMDExportSymbol.
    private static bool IsUMDExportSymbol(Symbol? symbol) =>
        symbol != null && symbol.Declarations is { Length: > 0 } && symbol.Declarations[0] != null &&
        NodeTests.IsNamespaceExportDeclaration(symbol.Declarations[0]);

    // symbolaccessibility.go isNamespaceReexportDeclaration.
    private static bool IsNamespaceReexportDeclaration(Node node) =>
        NodeTests.IsNamespaceExport(node) && node.Parent!.ModuleSpecifier() != null;

    // ast/utilities.go IsExternalModuleImportEqualsDeclaration.
    private static bool IsExternalModuleImportEqualsDeclaration(Node node) =>
        node.Kind == SyntaxKind.ImportEqualsDeclaration &&
        node.AsImportEqualsDeclaration().ModuleReference!.Kind == SyntaxKind.ExternalModuleReference;

    // checker/utilities.go:161 canHaveLocals.
    private static bool CanHaveLocals(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.Block:
            case SyntaxKind.CallSignature:
            case SyntaxKind.CaseBlock:
            case SyntaxKind.CatchClause:
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.ConditionalType:
            case SyntaxKind.Constructor:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionType:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.JSDocSignature:
            case SyntaxKind.MappedType:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.SourceFile:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return true;
        }
        return false;
    }

    // symbolaccessibility.go getExternalModuleContainer.
    private Symbol? GetExternalModuleContainer(Node declaration)
    {
        var node = AstUtilities.FindAncestor(declaration, static n =>
            AstUtilities.IsModuleWithStringLiteralName(n) ||
            (n.Kind == SyntaxKind.SourceFile && AstUtilities.IsExternalOrCommonJSModule(n.AsSourceFile())));
        if (node == null)
        {
            return null;
        }
        return GetSymbolOfDeclaration(node);
    }

    // symbolaccessibility.go:261 — getFileSymbolIfFileSymbolExportEqualsContainer.
    internal Symbol? GetFileSymbolIfFileSymbolExportEqualsContainer(Node d, Symbol container)
    {
        Symbol? fileSymbol = GetExternalModuleContainer(d);
        if (fileSymbol?.Exports == null)
        {
            return null;
        }
        if (!fileSymbol.Exports.TryGetValue(InternalSymbolName.ExportEquals, out Symbol? exported) || exported == null)
        {
            return null;
        }
        if (GetSymbolIfSameReference(exported, container) != null)
        {
            return fileSymbol;
        }
        return null;
    }

    // symbolaccessibility.go isPropertyOrMethodDeclarationSymbol.
    private static bool IsPropertyOrMethodDeclarationSymbol(Symbol symbol)
    {
        if (symbol.Declarations is { Length: > 0 })
        {
            foreach (var declaration in symbol.Declarations)
            {
                switch (declaration.Kind)
                {
                    case SyntaxKind.PropertyDeclaration:
                    case SyntaxKind.MethodDeclaration:
                    case SyntaxKind.GetAccessor:
                    case SyntaxKind.SetAccessor:
                        continue;
                    default:
                        return false;
                }
            }
            return true;
        }
        return false;
    }

    // symbolaccessibility.go getClassExpressionNameTable (cache dropped — recomputed).
    private SymbolTable? GetClassExpressionNameTable(Node location)
    {
        var classSymbol = GetSymbolOfDeclaration(location);
        var nameText = location.AsClassExpression().Name()?.Text();
        if (string.IsNullOrEmpty(nameText) || classSymbol == null)
        {
            return null;
        }
        return new SymbolTable { [nameText] = classSymbol };
    }

    // ---- scope iteration ----

    // symbolaccessibility.go someSymbolTableInScope. Callback: (table, ignoreQualification, isLocalNameLookup,
    // scopeNode) -> stop? The Go `tableId` param is dropped (SymbolTable-reference deviation).
    private bool SomeSymbolTableInScope(Node? enclosingDeclaration, Func<SymbolTable, bool, bool, Node?, bool> callback)
    {
        for (var location = enclosingDeclaration; location != null; location = location.Parent)
        {
            // Locals of a source file are not in scope (they get merged into the global symbol table).
            if (CanHaveLocals(location) && location.Locals() != null && !AstUtilities.IsGlobalSourceFile(location))
            {
                if (callback(location.Locals()!, false, true, location))
                {
                    return true;
                }
            }
            switch (location.Kind)
            {
                case SyntaxKind.SourceFile:
                case SyntaxKind.ModuleDeclaration:
                {
                    if (NodeTests.IsSourceFile(location) && !AstUtilities.IsExternalOrCommonJSModule(location.AsSourceFile()))
                    {
                        break;
                    }
                    // PORT-NOTE: GetReparsedNodeForNode (JSDoc reparse) deferred; raw location used.
                    var sym = GetSymbolOfDeclaration(location);
                    if (sym?.Exports != null && callback(sym.Exports, false, true, location))
                    {
                        return true;
                    }
                    break;
                }
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                case SyntaxKind.InterfaceDeclaration:
                {
                    // Type parameters are bound into `members`; filter to type-meaning symbols.
                    SymbolTable? table = null;
                    var sym = GetSymbolOfDeclaration(location);
                    if (sym?.Members != null)
                    {
                        // map-iter: 序无关 — builds a filtered table keyed by member name; no ordered output.
                        foreach (var (key, memberSymbol) in sym.Members)
                        {
                            if ((memberSymbol.Flags & (SymbolFlags.Type & ~SymbolFlags.Assignment)) != 0)
                            {
                                table ??= new SymbolTable();
                                table[key] = memberSymbol;
                            }
                        }
                    }
                    if (table != null && callback(table, false, false, location))
                    {
                        return true;
                    }
                    if (NodeTests.IsClassExpression(location) && location.AsClassExpression().Name() != null)
                    {
                        var nameTable = GetClassExpressionNameTable(location);
                        if (nameTable != null && callback(nameTable, false, true, location))
                        {
                            return true;
                        }
                    }
                    break;
                }
            }
        }
        return callback(globals, false, true, null);
    }

    // ---- accessibility core ----

    // symbolaccessibility.go isAccessible.
    private bool IsAccessible(AccessibleSymbolChainContext ctx, Symbol symbolFromSymbolTable, Symbol? resolvedAliasSymbol, bool ignoreQualification)
    {
        var likeSymbols = ctx.symbol == resolvedAliasSymbol || ctx.symbol == symbolFromSymbolTable;
        var symbol = GetMergedSymbol(ctx.symbol);
        if (symbol == GetMergedSymbol(resolvedAliasSymbol))
        {
            likeSymbols = true;
        }
        if (symbol == GetMergedSymbol(symbolFromSymbolTable))
        {
            likeSymbols = true;
        }
        if (!likeSymbols)
        {
            return false;
        }
        return !global::Ts.Core.CoreUtil.Some(symbolFromSymbolTable.Declarations, HasNonGlobalAugmentationExternalModuleSymbol) &&
            (ignoreQualification || CanQualifySymbol(ctx, GetMergedSymbol(symbolFromSymbolTable)!, ctx.meaning));
    }

    // symbolaccessibility.go needsQualification.
    internal bool NeedsQualification(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning)
    {
        var qualify = false;
        SomeSymbolTableInScope(enclosingDeclaration, (symbolTable, _, _, _) =>
        {
            if (!symbolTable.TryGetValue(symbol.Name, out var res) || res == null)
            {
                return false;
            }
            var symbolFromSymbolTable = GetMergedSymbol(res);
            if (symbolFromSymbolTable == null)
            {
                return false;
            }
            if (symbolFromSymbolTable == symbol)
            {
                return true; // No need to qualify.
            }
            var shouldResolveAlias = (symbolFromSymbolTable.Flags & SymbolFlags.Alias) != 0 &&
                AstUtilities.GetDeclarationOfKind(symbolFromSymbolTable, SyntaxKind.ExportSpecifier) == null;
            var flags = symbolFromSymbolTable.Flags;
            if (shouldResolveAlias)
            {
                symbolFromSymbolTable = ResolveAliasCore(symbolFromSymbolTable);
                flags = GetSymbolFlags(symbolFromSymbolTable);
            }
            if ((flags & meaning) != 0)
            {
                qualify = true;
                return true;
            }
            return false;
        });
        return qualify;
    }

    // symbolaccessibility.go canQualifySymbol.
    private bool CanQualifySymbol(AccessibleSymbolChainContext ctx, Symbol symbolFromSymbolTable, SymbolFlags meaning)
    {
        return !NeedsQualification(symbolFromSymbolTable, ctx.enclosingDeclaration, meaning) ||
            GetAccessibleSymbolChainEx(new AccessibleSymbolChainContext(symbolFromSymbolTable.Parent,
                ctx.enclosingDeclaration, GetQualifiedLeftMeaning(meaning), ctx.useOnlyExternalAliasing,
                ctx.visitedSymbolTablesMap)).Count > 0;
    }

    // symbolaccessibility.go getCandidateListForSymbol.
    private List<Symbol> GetCandidateListForSymbol(AccessibleSymbolChainContext ctx, Symbol symbolFromSymbolTable, Symbol? resolvedImportedSymbol, bool ignoreQualification)
    {
        if (IsAccessible(ctx, symbolFromSymbolTable, resolvedImportedSymbol, ignoreQualification))
        {
            return new List<Symbol> { symbolFromSymbolTable };
        }
        var candidateTable = resolvedImportedSymbol != null ? GetExportsOfSymbol(resolvedImportedSymbol) : null;
        if (candidateTable == null)
        {
            return new List<Symbol>();
        }
        var accessibleSymbolsFromExports = GetAccessibleSymbolChainFromSymbolTable(ctx, candidateTable, true, false);
        if (accessibleSymbolsFromExports.Count == 0)
        {
            return new List<Symbol>();
        }
        if (!CanQualifySymbol(ctx, symbolFromSymbolTable, GetQualifiedLeftMeaning(ctx.meaning)))
        {
            return new List<Symbol>();
        }
        var result = new List<Symbol> { symbolFromSymbolTable };
        result.AddRange(accessibleSymbolsFromExports);
        return result;
    }

    // symbolaccessibility.go getSymbolTableAliases (cache dropped — inline filter).
    private static List<Symbol> GetSymbolTableAliases(SymbolTable symbols)
    {
        var aliases = new List<Symbol>();
        // map-iter: 序无关 — the caller sorts candidate chains via compareSymbolChains.
        foreach (var sym in symbols.Values)
        {
            if ((sym.Flags & SymbolFlags.Alias) != 0)
            {
                aliases.Add(sym);
            }
        }
        return aliases;
    }

    // symbolaccessibility.go trySymbolTable.
    private List<Symbol>? TrySymbolTable(AccessibleSymbolChainContext ctx, SymbolTable symbols, bool ignoreQualification, bool isLocalNameLookup)
    {
        var isGlobals = ReferenceEquals(symbols, globals);
        var ok = symbols.TryGetValue(ctx.symbol!.Name, out var res);
        if (ok && res != null && IsAccessible(ctx, res, null, ignoreQualification))
        {
            return new List<Symbol> { ctx.symbol };
        }

        var candidateChains = new List<List<Symbol>>();

        if (ok && res != null && res.ExportSymbol != null)
        {
            if (IsAccessible(ctx, GetMergedSymbol(res.ExportSymbol)!, null, ignoreQualification))
            {
                candidateChains.Add(new List<Symbol> { ctx.symbol });
            }
        }

        foreach (var symbolFromSymbolTable in GetSymbolTableAliases(symbols))
        {
            if (symbolFromSymbolTable.Name != InternalSymbolName.ExportEquals &&
                symbolFromSymbolTable.Name != InternalSymbolName.Default &&
                !(IsUMDExportSymbol(symbolFromSymbolTable) && ctx.enclosingDeclaration != null && AstUtilities.IsExternalModule(AstUtilities.GetSourceFileOfNode(ctx.enclosingDeclaration)!)) &&
                (!ctx.useOnlyExternalAliasing || global::Ts.Core.CoreUtil.Some(symbolFromSymbolTable.Declarations, IsExternalModuleImportEqualsDeclaration)) &&
                (isLocalNameLookup && !global::Ts.Core.CoreUtil.Some(symbolFromSymbolTable.Declarations, IsNamespaceReexportDeclaration) || !isLocalNameLookup) &&
                (ignoreQualification || GetDeclarationsOfKind(symbolFromSymbolTable, SyntaxKind.ExportSpecifier).Length == 0))
            {
                var resolvedImportedSymbol = ResolveAliasCore(symbolFromSymbolTable);
                var candidate = GetCandidateListForSymbol(ctx, symbolFromSymbolTable, resolvedImportedSymbol, ignoreQualification);
                if (candidate.Count > 0)
                {
                    candidateChains.Add(candidate);
                }
            }
        }

        if (candidateChains.Count > 0)
        {
            candidateChains.Sort((a, b) => compareSymbolChains(a.ToArray(), b.ToArray()));
            return candidateChains[0];
        }

        if (isGlobals)
        {
            return GetCandidateListForSymbol(ctx, globalThisSymbol, globalThisSymbol, ignoreQualification);
        }
        return null;
    }

    // symbolaccessibility.go getAccessibleSymbolChainFromSymbolTable (visited-set keyed by table reference).
    private List<Symbol> GetAccessibleSymbolChainFromSymbolTable(AccessibleSymbolChainContext ctx, SymbolTable t, bool ignoreQualification, bool isLocalNameLookup)
    {
        var symId = AstUtilities.GetSymbolId(ctx.symbol!);
        if (!ctx.visitedSymbolTablesMap.TryGetValue(symId, out var visitedSymbolTables))
        {
            visitedSymbolTables = new HashSet<SymbolTable>();
            ctx.visitedSymbolTablesMap[symId] = visitedSymbolTables;
        }
        if (!visitedSymbolTables.Add(t))
        {
            return new List<Symbol>();
        }
        var res = TrySymbolTable(ctx, t, ignoreQualification, isLocalNameLookup) ?? new List<Symbol>();
        visitedSymbolTables.Remove(t);
        return res;
    }

    // symbolaccessibility.go getAccessibleSymbolChainEx (chain cache dropped).
    private List<Symbol> GetAccessibleSymbolChainEx(AccessibleSymbolChainContext ctx)
    {
        if (ctx.symbol == null)
        {
            return new List<Symbol>();
        }
        if (IsPropertyOrMethodDeclarationSymbol(ctx.symbol))
        {
            return new List<Symbol>();
        }
        var result = new List<Symbol>();
        SomeSymbolTableInScope(ctx.enclosingDeclaration, (t, ignoreQualification, isLocalNameLookup, _) =>
        {
            var r = GetAccessibleSymbolChainFromSymbolTable(ctx, t, ignoreQualification, isLocalNameLookup);
            if (r.Count > 0)
            {
                result = r;
                return true;
            }
            return false;
        });
        return result;
    }

    // symbolaccessibility.go getAccessibleSymbolChain.
    public List<Symbol> GetAccessibleSymbolChain(Symbol? symbol, Node? enclosingDeclaration, SymbolFlags meaning, bool useOnlyExternalAliasing)
    {
        return GetAccessibleSymbolChainEx(new AccessibleSymbolChainContext(symbol, enclosingDeclaration, meaning,
            useOnlyExternalAliasing, new Dictionary<SymbolId, HashSet<SymbolTable>>()));
    }

    // symbolaccessibility.go:117 — getWithAlternativeContainers.
    private List<Symbol> GetWithAlternativeContainers(Symbol container, Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning)
    {
        // core.MapNonNil(container.Declarations, d => getFileSymbolIfFileSymbolExportEqualsContainer(d, container))
        var additionalContainers = new List<Symbol>();
        foreach (var d in container.Declarations ?? [])
        {
            var additional = GetFileSymbolIfFileSymbolExportEqualsContainer(d, container);
            if (additional != null)
            {
                additionalContainers.Add(additional);
            }
        }
        var reexportContainers = enclosingDeclaration != null ? GetAlternativeContainingModules(symbol, enclosingDeclaration) : new List<Symbol>();
        var objectLiteralContainer = GetVariableDeclarationOfObjectLiteral(container, meaning);
        var leftMeaning = GetQualifiedLeftMeaning(meaning);
        if (enclosingDeclaration != null &&
            (container.Flags & leftMeaning) != 0 &&
            GetAccessibleSymbolChain(container, enclosingDeclaration, SymbolFlags.Namespace /*useOnlyExternalAliasing*/, false).Count > 0)
        {
            // This order expresses a preference for the real container if it is in scope.
            var preferred = new List<Symbol> { container };
            preferred.AddRange(additionalContainers);
            preferred.AddRange(reexportContainers);
            if (objectLiteralContainer != null)
            {
                preferred.Add(objectLiteralContainer);
            }
            return preferred;
        }
        // The container is a member of the instance side of something; look for a variable in scope with
        // the container's type that may be acting like a namespace (e.g. `Symbol` → `Symbol.toStringTag`).
        var variableMatches = new List<Symbol>();
        if (meaning == SymbolFlags.Value &&
            (container.Flags & leftMeaning) == 0 &&
            (container.Flags & SymbolFlags.Type) != 0 &&
            (GetDeclaredTypeOfSymbol(container).flags & TypeFlags.Object) != 0)
        {
            var declaredType = GetDeclaredTypeOfSymbol(container);
            SomeSymbolTableInScope(enclosingDeclaration, (t, _, _, _) =>
            {
                var found = false;
                foreach (var s in t.Values)
                {
                    if ((s.Flags & leftMeaning) != 0 && GetTypeOfSymbol(s) == declaredType)
                    {
                        variableMatches.Add(s);
                        found = true;
                    }
                }
                return found;
            });
            SortSymbols(variableMatches, 0, variableMatches.Count);
        }
        var res = new List<Symbol>();
        res.AddRange(variableMatches);
        res.AddRange(additionalContainers);
        res.Add(container);
        if (objectLiteralContainer != null)
        {
            res.Add(objectLiteralContainer);
        }
        res.AddRange(reexportContainers);
        return res;
    }

    // symbolaccessibility.go:171 — getAlternativeContainingModules.
    private List<Symbol> GetAlternativeContainingModules(Symbol symbol, Node enclosingDeclaration)
    {
        var containingFile = AstUtilities.GetSourceFileOfNode(enclosingDeclaration);
        var id = AstUtilities.GetNodeId(containingFile!.AsNode());
        var links = symbolContainerLinks.Get(symbol);
        links.extendedContainersByFile ??= new Dictionary<NodeId, Symbol[]>();
        if (links.extendedContainersByFile.TryGetValue(id, out var existing) && existing != null)
        {
            return existing.ToList();
        }
        var results = new List<Symbol>();
        var imports = containingFile.Imports();
        if (imports is { Length: > 0 })
        {
            // Try to make an import using an import already in the enclosing file, if possible.
            foreach (var importRef in imports)
            {
                if (AstUtilities.NodeIsSynthesized(importRef))
                {
                    // Synthetic names can't be resolved by `resolveExternalModuleName` - they'd error.
                    continue;
                }
                var resolvedModule = ResolveExternalModuleName(enclosingDeclaration, importRef /*ignoreErrors*/, true);
                if (resolvedModule == null)
                {
                    continue;
                }
                var reference = GetAliasForSymbolInContainer(resolvedModule, symbol);
                if (reference == null)
                {
                    continue;
                }
                results.Add(resolvedModule);
            }
            if (results.Count > 0)
            {
                links.extendedContainersByFile[id] = results.ToArray();
                return results;
            }
        }

        if (links.extendedContainers != null)
        {
            return links.extendedContainers.ToList();
        }
        // No results from files already being imported by this file - expand the search (expensive, but not
        // location-specific, so cached).
        foreach (var file in program.SourceFiles())
        {
            if (!AstUtilities.IsExternalModule(file))
            {
                continue;
            }
            var sym = GetSymbolOfDeclaration(file.AsNode())!;
            var reference = GetAliasForSymbolInContainer(sym, symbol);
            if (reference == null)
            {
                continue;
            }
            results.Add(sym);
        }
        links.extendedContainers = results.ToArray();
        return results;
    }

    // symbolaccessibility.go:222 — getVariableDeclarationOfObjectLiteral.
    private Symbol? GetVariableDeclarationOfObjectLiteral(Symbol symbol, SymbolFlags meaning)
    {
        // If we're trying to reference some object literal in, eg `var a = { x: 1 }`, the symbol for the
        // literal, `__object`, is distinct from the symbol of the declaration it is assigned to. We can use
        // the declaration to refer to the literal, however.
        if ((meaning & SymbolFlags.Value) == 0)
        {
            return null;
        }
        if (symbol.Declarations is not { Length: > 0 })
        {
            return null;
        }
        var firstDecl = symbol.Declarations[0];
        if (firstDecl.Parent == null)
        {
            return null;
        }
        if (!NodeTests.IsVariableDeclaration(firstDecl.Parent))
        {
            return null;
        }
        var parent = firstDecl.Parent.AsVariableDeclaration();
        if ((NodeTests.IsObjectLiteralExpression(firstDecl) && firstDecl == parent.Initializer) ||
            (NodeTests.IsTypeLiteralNode(firstDecl) && firstDecl == parent.Type))
        {
            return GetSymbolOfDeclaration(firstDecl.Parent);
        }
        return null;
    }

    // symbolaccessibility.go:342 — getAliasForSymbolInContainer
    internal Symbol? GetAliasForSymbolInContainer(Symbol container, Symbol symbol)
    {
        if (container == GetParentOfSymbol(symbol))
        {
            // fast path, `symbol` is either already the alias or isn't aliased
            return symbol;
        }
        // Check if container is a thing with an `export=` which points directly at `symbol`, and if so,
        // return the container itself as the alias for the symbol
        if (container.Exports != null)
        {
            if (container.Exports.TryGetValue(InternalSymbolName.ExportEquals, out var exportEquals) && exportEquals != null && GetSymbolIfSameReference(exportEquals, symbol) != null)
            {
                return container;
            }
        }
        var exports = GetExportsOfSymbol(container) ?? new SymbolTable();
        if (exports.TryGetValue(symbol.Name, out var quick) && quick != null && GetSymbolIfSameReference(quick, symbol) != null)
        {
            return quick;
        }
        List<Symbol>? candidates = null;
        foreach (var exported in exports.Values) // map-iter: 已排序 — sortSymbols below stabilizes result
        {
            if (GetSymbolIfSameReference(exported, symbol) != null)
            {
                (candidates ??= new()).Add(exported);
            }
        }
        if (candidates != null && candidates.Count > 0)
        {
            SortSymbols(candidates, 0, candidates.Count); // _must_ sort exports for stable results - symbol table is randomly iterated
            return candidates[0];
        }
        return null;
    }

    // symbolaccessibility.go:280-340 — getContainersOfSymbol.
    internal List<Symbol> GetContainersOfSymbol(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning)
    {
        var container = GetParentOfSymbol(symbol);
        // Type parameters end up in the `members` lists but are not externally visible
        if (container != null && (symbol.Flags & SymbolFlags.TypeParameter) == 0)
        {
            return GetWithAlternativeContainers(container, symbol, enclosingDeclaration, meaning);
        }
        var candidates = new List<Symbol>();
        foreach (var d in symbol.Declarations ?? [])
        {
            if (!AstUtilities.IsAmbientModule(d) && d.Parent != null)
            {
                // direct children of a module
                if (HasNonGlobalAugmentationExternalModuleSymbol(d.Parent))
                {
                    var sym = GetSymbolOfDeclaration(d.Parent);
                    if (sym != null && !candidates.Contains(sym))
                    {
                        candidates.Add(sym);
                    }
                    continue;
                }
                // export ='d member of an ambient module
                if (NodeTests.IsModuleBlock(d.Parent) && d.Parent.Parent != null &&
                    ResolveExternalModuleSymbol(GetSymbolOfDeclaration(d.Parent.Parent), false) == symbol)
                {
                    var sym = GetSymbolOfDeclaration(d.Parent.Parent);
                    if (sym != null && !candidates.Contains(sym))
                    {
                        candidates.Add(sym);
                    }
                    continue;
                }
            }
            if (NodeTests.IsClassExpression(d) && d.Parent != null && NodeTests.IsBinaryExpression(d.Parent) &&
                d.Parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken &&
                AstUtilities.IsAccessExpression(d.Parent.AsBinaryExpression().Left!) &&
                AstUtilities.IsEntityNameExpression(d.Parent.AsBinaryExpression().Left!.Expression()!))
            {
                if (AstUtilities.IsModuleExportsAccessExpression(d.Parent.AsBinaryExpression().Left!) ||
                    AstUtilities.IsExportsIdentifier(d.Parent.AsBinaryExpression().Left!.Expression()!))
                {
                    var sym = GetSymbolOfDeclaration(AstUtilities.GetSourceFileOfNode(d)!.AsNode());
                    if (sym != null && !candidates.Contains(sym))
                    {
                        candidates.Add(sym);
                    }
                    continue;
                }
                CheckExpressionCached(d.Parent.AsBinaryExpression().Left!.Expression()!);
                var resolved = symbolNodeLinks.Get(d.Parent.AsBinaryExpression().Left!.Expression()!).resolvedSymbol;
                if (resolved != null && !candidates.Contains(resolved))
                {
                    candidates.Add(resolved);
                }
                continue;
            }
        }
        if (candidates.Count == 0)
        {
            return null!;
        }

        var bestContainers = new List<Symbol>();
        var alternativeContainers = new List<Symbol>();
        foreach (var candidate in candidates)
        {
            if (GetAliasForSymbolInContainer(candidate, symbol) == null)
            {
                continue;
            }
            var allAlts = GetWithAlternativeContainers(candidate, symbol, enclosingDeclaration, meaning);
            if (allAlts.Count == 0)
            {
                continue;
            }
            bestContainers.Add(allAlts[0]);
            alternativeContainers.AddRange(allAlts.Skip(1));
        }
        bestContainers.AddRange(alternativeContainers);
        return bestContainers;
    }

    // symbolaccessibility.go IsAnySymbolAccessible.
    private PrinterNs.SymbolAccessibilityResult? IsAnySymbolAccessible(List<Symbol> symbols, Node? enclosingDeclaration, Symbol initialSymbol, SymbolFlags meaning, bool shouldComputeAliasesToMakeVisible, bool allowModules)
    {
        if (symbols is not { Count: > 0 })
        {
            return null;
        }
        Symbol? hadAccessibleChain = null;
        var earlyModuleBail = false;
        foreach (var symbol in symbols)
        {
            var accessibleSymbolChain = GetAccessibleSymbolChain(symbol, enclosingDeclaration, meaning, false);
            if (accessibleSymbolChain.Count > 0)
            {
                hadAccessibleChain = symbol;
                var hasAccessibleDeclarations = GetEmitResolver().HasVisibleDeclarations(accessibleSymbolChain[0], shouldComputeAliasesToMakeVisible);
                if (hasAccessibleDeclarations != null)
                {
                    return hasAccessibleDeclarations;
                }
            }
            if (allowModules)
            {
                if (global::Ts.Core.CoreUtil.Some(symbol.Declarations, HasNonGlobalAugmentationExternalModuleSymbol))
                {
                    if (shouldComputeAliasesToMakeVisible)
                    {
                        earlyModuleBail = true;
                        continue;
                    }
                    return new PrinterNs.SymbolAccessibilityResult { Accessibility = PrinterNs.SymbolAccessibility.Accessible };
                }
            }
            var containers = GetContainersOfSymbol(symbol, enclosingDeclaration, meaning);
            var nextMeaning = initialSymbol == symbol ? GetQualifiedLeftMeaning(meaning) : meaning;
            var parentResult = IsAnySymbolAccessible(containers, enclosingDeclaration, initialSymbol, nextMeaning, shouldComputeAliasesToMakeVisible, allowModules);
            if (parentResult != null)
            {
                return parentResult;
            }
        }
        if (earlyModuleBail)
        {
            return new PrinterNs.SymbolAccessibilityResult { Accessibility = PrinterNs.SymbolAccessibility.Accessible };
        }
        if (hadAccessibleChain != null)
        {
            string? moduleName = null;
            if (hadAccessibleChain != initialSymbol)
            {
                moduleName = SymbolToStringEx(hadAccessibleChain, enclosingDeclaration, SymbolFlags.Namespace, SymbolFormatFlags.AllowAnyNodeKind);
            }
            return new PrinterNs.SymbolAccessibilityResult
            {
                Accessibility = PrinterNs.SymbolAccessibility.NotAccessible,
                ErrorSymbolName = SymbolToStringEx(initialSymbol, enclosingDeclaration, meaning, SymbolFormatFlags.AllowAnyNodeKind),
                ErrorModuleName = moduleName,
            };
        }
        return null;
    }

    // symbolaccessibility.go isSymbolAccessibleWorker.
    private PrinterNs.SymbolAccessibilityResult IsSymbolAccessibleWorkerCore(Symbol? symbol, Node? enclosingDeclaration, SymbolFlags meaning, bool shouldComputeAliasesToMakeVisible, bool allowModules)
    {
        if (symbol != null && enclosingDeclaration != null)
        {
            var result = IsAnySymbolAccessible(new List<Symbol> { symbol }, enclosingDeclaration, symbol, meaning, shouldComputeAliasesToMakeVisible, allowModules);
            if (result != null)
            {
                return result.Value;
            }
            var symbolExternalModule = global::Ts.Core.CoreUtil.FirstNonNil(symbol.Declarations, GetExternalModuleContainer);
            if (symbolExternalModule != null)
            {
                var enclosingExternalModule = GetExternalModuleContainer(enclosingDeclaration);
                if (symbolExternalModule != enclosingExternalModule)
                {
                    return new PrinterNs.SymbolAccessibilityResult
                    {
                        Accessibility = PrinterNs.SymbolAccessibility.CannotBeNamed,
                        ErrorSymbolName = SymbolToStringEx(symbol, enclosingDeclaration, meaning, SymbolFormatFlags.AllowAnyNodeKind),
                        ErrorModuleName = SymbolToString(symbolExternalModule),
                        ErrorNode = AstUtilities.IsInJSFile(enclosingDeclaration) ? enclosingDeclaration : null,
                    };
                }
            }
            return new PrinterNs.SymbolAccessibilityResult
            {
                Accessibility = PrinterNs.SymbolAccessibility.NotAccessible,
                ErrorSymbolName = SymbolToStringEx(symbol, enclosingDeclaration, meaning, SymbolFormatFlags.AllowAnyNodeKind),
            };
        }
        return new PrinterNs.SymbolAccessibilityResult { Accessibility = PrinterNs.SymbolAccessibility.Accessible };
    }

    // symbolaccessibility.go IsTypeSymbolAccessible.
    internal bool IsTypeSymbolAccessible(Symbol typeSymbol, Node? enclosingDeclaration) =>
        IsSymbolAccessibleWorkerCore(typeSymbol, enclosingDeclaration, SymbolFlags.Type, false, true).Accessibility == PrinterNs.SymbolAccessibility.Accessible;

    // symbolaccessibility.go IsValueSymbolAccessible.
    internal bool IsValueSymbolAccessibleCore(Symbol symbol, Node? enclosingDeclaration) =>
        IsSymbolAccessibleWorkerCore(symbol, enclosingDeclaration, SymbolFlags.Value, false, true).Accessibility == PrinterNs.SymbolAccessibility.Accessible;
}
