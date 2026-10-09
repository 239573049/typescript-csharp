// Ported from tsgo internal/ls/importTracker.go (1:1).
//
// The import tracker the deep find-all-references engine uses to follow imports/exports
// of an exported symbol across the request's source files. Checker channels are the
// public surface: GetSymbolAtLocation / GetMergedSymbol / GetImmediateAliasedSymbolEx /
// GetExportSpecifierLocalTargetSymbol (CheckerFindRefsLsExports).

using System;
using System.Collections.Generic;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerFindRefsLsExports extension methods
using Ts.Collections; // Set<T>

namespace Ts.Ls;

// importTracker.go:11-20 — ImpExpKind.
internal enum ImpExpKind
{
    Unknown = 0,
    Import = 1,
    Export = 2,
}

// importTracker.go:22-27 — ImportExportSymbol.
internal sealed class ImportExportSymbol
{
    public ImpExpKind Kind;
    public global::Ts.Ast.Symbol? Symbol;
    public ExportInfo? ExportInfo;
}

// importTracker.go:29-40 — ExportKind.
internal enum ExportKind
{
    Named = 0,
    Default = 1,
    ExportEquals = 2,
    UMD = 3,
    Module = 4,
}

// importTracker.go:32-35 — ExportInfo.
internal sealed record ExportInfo(global::Ts.Ast.Symbol ExportingModuleSymbol, ExportKind ExportKind);

// importTracker.go:42-45 — LocationAndSymbol.
internal sealed record LocationAndSymbol(Node ImportLocation, global::Ts.Ast.Symbol? ImportSymbol);

// importTracker.go:47-51 — ImportsResult.
internal sealed class ImportsResult
{
    public List<LocationAndSymbol> ImportSearches = [];
    public List<Node> SingleReferences = [];
    public List<SourceFile> IndirectUsers = [];
}

// importTracker.go:53 — ImportTracker delegate.
internal delegate ImportsResult ImportTracker(global::Ts.Ast.Symbol exportSymbol, ExportInfo exportInfo, bool isForRename);

// importTracker.go:55-64 — ModuleReferenceKind / ModuleReference.
internal enum ModuleReferenceKind
{
    Import = 0,
    Reference = 1,
    Implicit = 2,
}

// ModuleReference represents a reference to a module, either via import, <reference>, or implicit reference
internal sealed class ModuleReference
{
    public ModuleReferenceKind Kind;
    public Node? Literal; // for import and implicit kinds (StringLiteralLike)
    public SourceFile? ReferencingFile;
    public global::Ts.Ast.FileReference? Ref; // for reference kind
}

internal static class ImportTrackerSupport
{
    // importTracker.go:67 — createImportTracker.
    // Creates the imports map and returns an ImportTracker that uses it. Call this lazily to avoid calling `getDirectImportsMap` unnecessarily.
    internal static ImportTracker CreateImportTracker(
        System.Threading.CancellationToken ctx,
        global::Ts.Compiler.Program program,
        SourceFile[] sourceFiles,
        Set<string> sourceFilesSet,
        global::Ts.Checker.Checker checker)
    {
        var allDirectImports = GetDirectImportsMap(ctx, program, sourceFiles, checker);
        return (exportSymbol, exportInfo, isForRename) =>
        {
            var (directImports, indirectUsers) = GetImportersForExport(sourceFiles, sourceFilesSet, allDirectImports, exportInfo, checker);
            var (importSearches, singleReferences) = GetSearchesFromDirectImports(directImports, exportSymbol, exportInfo.ExportKind, checker, isForRename);
            return new ImportsResult
            {
                ImportSearches = importSearches,
                SingleReferences = singleReferences,
                IndirectUsers = indirectUsers,
            };
        };
    }

    // importTracker.go:75-88 — getDirectImportsMap.
    // Returns a map from a module symbol to all import statements that directly reference the module
    internal static Dictionary<global::Ts.Ast.Symbol, List<Node>> GetDirectImportsMap(
        System.Threading.CancellationToken ctx,
        global::Ts.Compiler.Program program,
        SourceFile[] sourceFiles,
        global::Ts.Checker.Checker checker)
    {
        var result = new Dictionary<global::Ts.Ast.Symbol, List<Node>>();
        foreach (var sourceFile in sourceFiles)
        {
            if (ctx.IsCancellationRequested)
            {
                return result;
            }
            ForEachImport(program, sourceFile, (importDecl, moduleSpecifier) =>
            {
                if (checker.GetSymbolAtLocation(moduleSpecifier) is { } moduleSymbol)
                {
                    if (!result.TryGetValue(moduleSymbol, out var list))
                    {
                        list = new List<Node>();
                        result[moduleSymbol] = list;
                    }
                    list.Add(importDecl);
                }
            });
        }
        return result;
    }

    // importTracker.go:91-132 — forEachImport.
    // Calls `action` for each import, re-export, or require() in a file
    internal static void ForEachImport(global::Ts.Compiler.Program program, SourceFile sourceFile, Action<Node, Node> action)
    {
        var implicitImports = new List<Node>();
        var (_, jsxSpecifier) = program.GetJSXRuntimeImportSpecifier(sourceFile.Path());
        if (jsxSpecifier != null)
        {
            implicitImports.Add(jsxSpecifier);
        }
        var importHelpersSpecifier = program.GetImportHelpersImportSpecifier(sourceFile.Path());
        if (importHelpersSpecifier != null)
        {
            implicitImports.Add(importHelpersSpecifier);
        }
        if (sourceFile.ExternalModuleIndicator != null || (sourceFile.Imports()?.Length ?? 0) + implicitImports.Count != 0)
        {
            foreach (var i in sourceFile.Imports() ?? [])
            {
                action(AstUtilities.ImportFromModuleSpecifier(i), i);
            }
            foreach (var i in implicitImports)
            {
                action(AstUtilities.ImportFromModuleSpecifier(i), i);
            }
        }
        else
        {
            ForEachPossibleImportOrExportStatement(sourceFile.AsNode(), node =>
            {
                switch (node.Kind)
                {
                    case SyntaxKind.ExportDeclaration or SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration:
                        if (node.ModuleSpecifier() is { } specifier && NodeTests.IsStringLiteral(specifier))
                        {
                            action(node, specifier);
                        }
                        break;
                    case SyntaxKind.ImportEqualsDeclaration:
                        if (IsExternalModuleImportEquals(node))
                        {
                            action(node, node.AsImportEqualsDeclaration().ModuleReference!.Expression()!);
                        }
                        break;
                }
                return false;
            });
        }
    }

    // importTracker.go:134-141 — forEachPossibleImportOrExportStatement.
    internal static bool ForEachPossibleImportOrExportStatement(Node sourceFileLike, Func<Node, bool> action)
    {
        foreach (var statement in GetStatementsOfSourceFileLike(sourceFileLike))
        {
            if (action(statement) || IsAmbientModuleDeclaration(statement) && ForEachPossibleImportOrExportStatement(statement, action))
            {
                return true;
            }
        }
        return false;
    }

    // importTracker.go:143-153 — getSourceFileLikeForImportDeclaration.
    internal static Node GetSourceFileLikeForImportDeclaration(Node node)
    {
        if (NodeTests.IsCallExpression(node) || NodeTests.IsJSDocImportTag(node))
        {
            return AstUtilities.GetSourceFileOfNode(node)!.AsNode();
        }
        var parent = node.Parent!;
        if (NodeTests.IsSourceFile(parent))
        {
            return parent;
        }
        global::Ts.Core.Debug.Assert(NodeTests.IsModuleBlock(parent) && IsAmbientModuleDeclaration(parent.Parent!));
        return parent.Parent!;
    }

    // importTracker.go:155-157 — isAmbientModuleDeclaration.
    internal static bool IsAmbientModuleDeclaration(Node node)
    {
        return NodeTests.IsModuleDeclaration(node) && NodeTests.IsStringLiteral(node.Name()!);
    }

    // importTracker.go:159-167 — getStatementsOfSourceFileLike.
    internal static Node[] GetStatementsOfSourceFileLike(Node node)
    {
        if (NodeTests.IsSourceFile(node))
        {
            return node.Statements() ?? [];
        }
        if (node.Body() is { } body)
        {
            return body.Statements() ?? [];
        }
        return [];
    }

    // importTracker.go:169-322 — getImportersForExport.
    internal static (List<Node> DirectImports, List<SourceFile> IndirectUsers) GetImportersForExport(
        SourceFile[] sourceFiles,
        Set<string> sourceFilesSet,
        Dictionary<global::Ts.Ast.Symbol, List<Node>> allDirectImports,
        ExportInfo exportInfo,
        global::Ts.Checker.Checker checker)
    {
        var directImports = new List<Node>();
        var indirectUserDeclarations = new List<Node>();
        Node? lastSeenDirectImport = null; // (closure state for markSeenDirectImport is carried by the set below)
        var markSeenDirectImport = NodeSeenTracker();
        var markSeenIndirectUser = NodeSeenTracker();
        var isAvailableThroughGlobal = FindRefsSupport.IsSourceFileWithGlobalExports(exportInfo.ExportingModuleSymbol.ValueDeclaration);

        List<Node> GetDirectImports(global::Ts.Ast.Symbol moduleSymbol)
        {
            return allDirectImports.GetValueOrDefault(moduleSymbol) ?? [];
        }

        // Adds a module and all of its transitive dependencies as possible indirect users
        void AddIndirectUser(Node sourceFileLike, bool addTransitiveDependencies)
        {
            // When isAvailableThroughGlobal, getIndirectUsers already returns all source files,
            // so indirectUserDeclarations is never consulted. Nothing to do here.
            if (isAvailableThroughGlobal)
            {
                return;
            }
            if (!markSeenIndirectUser(sourceFileLike))
            {
                return;
            }
            indirectUserDeclarations.Add(sourceFileLike);
            if (!addTransitiveDependencies)
            {
                return;
            }
            var moduleSymbol = checker.GetMergedSymbol(sourceFileLike.Symbol());
            if (moduleSymbol == null)
            {
                return;
            }
            global::Ts.Core.Debug.Assert((moduleSymbol.Flags & SymbolFlags.Module) != 0);
            foreach (var directImport in GetDirectImports(moduleSymbol))
            {
                if (!NodeTests.IsImportTypeNode(directImport))
                {
                    AddIndirectUser(GetSourceFileLikeForImportDeclaration(directImport), true /*addTransitiveDependencies*/);
                }
            }
        }

        bool IsExported(Node? node, bool stopAtAmbientModule)
        {
            while (node != null && !(stopAtAmbientModule && IsAmbientModuleDeclaration(node)))
            {
                if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export))
                {
                    return true;
                }
                node = node.Parent;
            }
            return false;
        }

        void HandleImportCall(Node importCall)
        {
            var top = AstUtilities.FindAncestor(importCall, IsAmbientModuleDeclaration);
            if (top == null)
            {
                top = AstUtilities.GetSourceFileOfNode(importCall)!.AsNode();
            }
            AddIndirectUser(top, IsExported(importCall, true /*stopAtAmbientModule*/));
        }

        void HandleNamespaceImport(Node importDeclaration, Node name, bool isReExport, bool alreadyAddedDirect)
        {
            if (exportInfo.ExportKind == ExportKind.ExportEquals)
            {
                // This is a direct import, not import-as-namespace.
                if (!alreadyAddedDirect)
                {
                    directImports.Add(importDeclaration);
                }
            }
            else if (!isAvailableThroughGlobal)
            {
                var sourceFileLike = GetSourceFileLikeForImportDeclaration(importDeclaration);
                global::Ts.Core.Debug.Assert(NodeTests.IsSourceFile(sourceFileLike) || NodeTests.IsModuleDeclaration(sourceFileLike));
                AddIndirectUser(sourceFileLike, isReExport || FindNamespaceReExports(sourceFileLike, name, checker));
            }
        }

        void HandleDirectImports(global::Ts.Ast.Symbol exportingModuleSymbol)
        {
            var theseDirectImports = GetDirectImports(exportingModuleSymbol);
            foreach (var direct in theseDirectImports)
            {
                if (!markSeenDirectImport(direct))
                {
                    continue;
                }
                // !!! cancellation
                switch (direct.Kind)
                {
                    case SyntaxKind.CallExpression:
                        if (AstUtilities.IsImportCall(direct))
                        {
                            HandleImportCall(direct);
                        }
                        else if (!isAvailableThroughGlobal)
                        {
                            var parent = direct.Parent!;
                            if (exportInfo.ExportKind == ExportKind.ExportEquals && NodeTests.IsVariableDeclaration(parent))
                            {
                                var name = parent.Name();
                                if (NodeTests.IsIdentifier(name!))
                                {
                                    directImports.Add(name!);
                                }
                            }
                        }
                        break;
                    case SyntaxKind.Identifier:
                        // Nothing
                        break;
                    case SyntaxKind.ImportEqualsDeclaration:
                        HandleNamespaceImport(direct, direct.Name()!, AstUtilities.HasSyntacticModifier(direct, ModifierFlags.Export), false /*alreadyAddedDirect*/);
                        break;
                    case SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration or SyntaxKind.JSDocImportTag:
                        directImports.Add(direct);
                        if (direct.ImportClause() is { } importClause)
                        {
                            if (importClause.AsImportClause().NamedBindings is { } namedBindings && NodeTests.IsNamespaceImport(namedBindings))
                            {
                                HandleNamespaceImport(direct, namedBindings.Name()!, false /*isReExport*/, true /*alreadyAddedDirect*/);
                                break;
                            }
                        }
                        if (!isAvailableThroughGlobal && SourceDefinitionDeps.IsDefaultImport(direct))
                        {
                            AddIndirectUser(GetSourceFileLikeForImportDeclaration(direct), false);
                            // Add a check for indirect uses to handle synthetic default imports
                        }
                        break;
                    case SyntaxKind.ExportDeclaration:
                        var exportClause = direct.AsExportDeclaration().ExportClause;
                        if (exportClause == null)
                        {
                            // This is `export * from "foo"`, so imports of this module may import the export too.
                            HandleDirectImports(GetContainingModuleSymbol(direct, checker)!);
                        }
                        else if (NodeTests.IsNamespaceExport(exportClause))
                        {
                            // `export * as foo from "foo"` add to indirect uses
                            AddIndirectUser(GetSourceFileLikeForImportDeclaration(direct), true /*addTransitiveDependencies*/);
                        }
                        else
                        {
                            // This is `export { foo } from "foo"` and creates an alias symbol, so recursive search will get handle re-exports.
                            directImports.Add(direct);
                        }
                        break;
                    case SyntaxKind.ImportType:
                        // Only check for typeof import('xyz')
                        if (!isAvailableThroughGlobal && direct.AsImportTypeNode().IsTypeOf && direct.AsImportTypeNode().Qualifier == null && IsExported(direct, false))
                        {
                            AddIndirectUser(AstUtilities.GetSourceFileOfNode(direct)!.AsNode(), true /*addTransitiveDependencies*/);
                        }
                        directImports.Add(direct);
                        break;
                    default:
                        global::Ts.Core.Debug.FailBadSyntaxKind(direct, "Unexpected import kind.");
                        break;
                }
            }
        }

        List<SourceFile> GetIndirectUsers()
        {
            if (isAvailableThroughGlobal)
            {
                // It has `export as namespace`, so anything could potentially use it.
                return new List<SourceFile>(sourceFiles);
            }
            // Module augmentations may use this module's exports without importing it.
            foreach (var decl in exportInfo.ExportingModuleSymbol.Declarations ?? [])
            {
                if (AstUtilities.IsExternalModuleAugmentation(decl) && sourceFilesSet.Has(AstUtilities.GetSourceFileOfNode(decl)!.FileName()))
                {
                    AddIndirectUser(decl, false);
                }
            }
            // This may return duplicates (if there are multiple module declarations in a single source file, all importing the same thing as a namespace), but `State.markSearchedSymbol` will handle that.
            var result = new List<SourceFile>(indirectUserDeclarations.Count);
            foreach (var declaration in indirectUserDeclarations)
            {
                result.Add(AstUtilities.GetSourceFileOfNode(declaration)!);
            }
            return result;
        }

        _ = lastSeenDirectImport;
        HandleDirectImports(exportInfo.ExportingModuleSymbol);
        return (directImports, GetIndirectUsers());
    }

    // ls/utilities.go:1285 — nodeSeenTracker.
    private static Func<Node, bool> NodeSeenTracker()
    {
        var seen = new Set<Node>();
        return node => seen.AddIfAbsent(node);
    }

    // importTracker.go:324-326 — getContainingModuleSymbol.
    internal static global::Ts.Ast.Symbol? GetContainingModuleSymbol(Node importer, global::Ts.Checker.Checker checker)
    {
        return checker.GetMergedSymbol(GetSourceFileLikeForImportDeclaration(importer).Symbol());
    }

    // importTracker.go:328-341 — findNamespaceReExports.
    // Returns 'true' is the namespace 'name' is re-exported from this module, and 'false' if it is only used locally
    internal static bool FindNamespaceReExports(Node sourceFileLike, Node name, global::Ts.Checker.Checker checker)
    {
        var namespaceImportSymbol = checker.GetSymbolAtLocation(name);
        return ForEachPossibleImportOrExportStatement(sourceFileLike, statement =>
        {
            if (!NodeTests.IsExportDeclaration(statement))
            {
                return false;
            }
            var exportClause = statement.AsExportDeclaration().ExportClause;
            var moduleSpecifier = statement.ModuleSpecifier();
            if (moduleSpecifier == null && exportClause != null && NodeTests.IsNamedExports(exportClause))
            {
                foreach (var element in exportClause.Elements() ?? [])
                {
                    if (checker.GetExportSpecifierLocalTargetSymbol(element) == namespaceImportSymbol)
                    {
                        return true;
                    }
                }
            }
            return false;
        });
    }

    // importTracker.go:343-460 — getSearchesFromDirectImports.
    internal static (List<LocationAndSymbol> ImportSearches, List<Node> SingleReferences) GetSearchesFromDirectImports(
        List<Node> directImports,
        global::Ts.Ast.Symbol exportSymbol,
        ExportKind exportKind,
        global::Ts.Checker.Checker checker,
        bool isForRename)
    {
        var importSearches = new List<LocationAndSymbol>();
        var singleReferences = new List<Node>();

        void AddSearch(Node location, global::Ts.Ast.Symbol? symbol)
        {
            importSearches.Add(new LocationAndSymbol(location, symbol));
        }

        bool IsNameMatch(string name)
        {
            // Use name of "default" even in `export =` case because we may have allowSyntheticDefaultImports
            return name == exportSymbol.Name || exportKind != ExportKind.Named && name == global::Ts.Ast.InternalSymbolName.Default;
        }

        // `import x = require("./x")` or `import * as x from "./x"`.
        // An `export =` may be imported by this syntax, so it may be a direct import.
        // If it's not a direct import, it will be in `indirectUsers`, so we don't have to do anything here.
        void HandleNamespaceImportLike(Node importName)
        {
            // Don't rename an import that already has a different name than the export.
            if (exportKind == ExportKind.ExportEquals && (!isForRename || IsNameMatch(importName.Text())))
            {
                AddSearch(importName, checker.GetSymbolAtLocation(importName));
            }
        }

        void SearchForNamedImport(Node? namedBindings)
        {
            if (namedBindings == null)
            {
                return;
            }
            foreach (var element in namedBindings.Elements() ?? [])
            {
                var name = element.Name()!;
                var propertyName = element.PropertyName();
                if (!IsNameMatch((propertyName ?? name).Text()))
                {
                    continue;
                }
                if (propertyName != null)
                {
                    // This is `import { foo as bar } from "./a"` or `export { foo as bar } from "./a"`. `foo` isn't a local in the file, so just add it as a single reference.
                    singleReferences.Add(propertyName);
                    // If renaming `{ foo as bar }`, don't touch `bar`, just `foo`.
                    // But do rename `foo` in ` { default as foo }` if that's the original export name.
                    if (!isForRename || name.Text() == exportSymbol.Name)
                    {
                        // Search locally for `bar`.
                        AddSearch(name, checker.GetSymbolAtLocation(name));
                    }
                }
                else
                {
                    global::Ts.Ast.Symbol? localSymbol;
                    if (NodeTests.IsExportSpecifier(element) && element.PropertyName() != null)
                    {
                        localSymbol = checker.GetExportSpecifierLocalTargetSymbol(element);
                    }
                    else
                    {
                        localSymbol = checker.GetSymbolAtLocation(name);
                    }
                    AddSearch(name, localSymbol);
                }
            }
        }

        void HandleImport(Node decl)
        {
            if (NodeTests.IsImportEqualsDeclaration(decl))
            {
                if (IsExternalModuleImportEquals(decl))
                {
                    HandleNamespaceImportLike(decl.Name()!);
                }
                return;
            }
            if (NodeTests.IsIdentifier(decl))
            {
                HandleNamespaceImportLike(decl);
                return;
            }
            if (NodeTests.IsImportTypeNode(decl))
            {
                if (decl.AsImportTypeNode().Qualifier is { } qualifier)
                {
                    var firstIdentifier = global::Ts.Checker.Checker.GetFirstIdentifier(qualifier);
                    if (firstIdentifier.Text() == AstUtilities.SymbolName(exportSymbol))
                    {
                        singleReferences.Add(firstIdentifier);
                    }
                }
                else if (exportKind == ExportKind.ExportEquals)
                {
                    singleReferences.Add(decl.AsImportTypeNode().Argument!.AsLiteralTypeNode().Literal!);
                }
                return;
            }
            // Ignore if there's a grammar error
            if (!NodeTests.IsStringLiteral(decl.ModuleSpecifier()!))
            {
                return;
            }
            if (NodeTests.IsExportDeclaration(decl))
            {
                if (decl.AsExportDeclaration().ExportClause is { } exportClause && NodeTests.IsNamedExports(exportClause))
                {
                    SearchForNamedImport(exportClause);
                }
                return;
            }
            if (decl.ImportClause() is { } importClause)
            {
                if (importClause.AsImportClause().NamedBindings is { } namedBindings)
                {
                    switch (namedBindings.Kind)
                    {
                        case SyntaxKind.NamespaceImport:
                            HandleNamespaceImportLike(namedBindings.Name()!);
                            break;
                        case SyntaxKind.NamedImports:
                            // 'default' might be accessed as a named import `{ default as foo }`.
                            if (exportKind == ExportKind.Named || exportKind == ExportKind.Default)
                            {
                                SearchForNamedImport(namedBindings);
                            }
                            break;
                    }
                }
                // `export =` might be imported by a default import if `--allowSyntheticDefaultImports` is on, so this handles both ExportKind.Default and ExportKind.ExportEquals.
                // If a default import has the same name as the default export, allow to rename it.
                // Given `import f` and `export default function f`, we will rename both, but for `import g` we will rename just that.
                if (importClause.Name() is { } name && (exportKind == ExportKind.Default || exportKind == ExportKind.ExportEquals) && (!isForRename || name.Text() == SymbolNameNoDefault(exportSymbol)))
                {
                    var defaultImportAlias = checker.GetSymbolAtLocation(name);
                    AddSearch(name, defaultImportAlias);
                }
            }
        }
        foreach (var decl in directImports)
        {
            HandleImport(decl);
        }
        return (importSearches, singleReferences);
    }

    // importTracker.go:462-609 — getImportOrExportSymbol.
    internal static ImportExportSymbol? GetImportOrExportSymbol(Node node, global::Ts.Ast.Symbol symbol, global::Ts.Checker.Checker checker, bool comingFromExport)
    {
        ImportExportSymbol? ExportInfoOf(global::Ts.Ast.Symbol sym, ExportKind kind)
        {
            var exportInfo = GetExportInfo(sym, kind, checker);
            if (exportInfo != null)
            {
                return new ImportExportSymbol
                {
                    Kind = ImpExpKind.Export,
                    Symbol = sym,
                    ExportInfo = exportInfo,
                };
            }
            return null;
        }

        ImportExportSymbol? GetExportAssignmentExport(Node ex)
        {
            // Get the symbol for the `export =` node; its parent is the module it's the export of.
            if (ex.Symbol()!.Parent == null)
            {
                return null;
            }
            var exportKind = ex.AsExportAssignment().IsExportEquals ? ExportKind.ExportEquals : ExportKind.Default;
            return new ImportExportSymbol
            {
                Kind = ImpExpKind.Export,
                Symbol = symbol,
                ExportInfo = new ExportInfo(
                    ExportingModuleSymbol: ex.Symbol()!.Parent!,
                    ExportKind: exportKind),
            };
        }

        // Not meant for use with export specifiers or export assignment.
        static ExportKind GetExportKindForDeclaration(Node node)
        {
            if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default))
            {
                return ExportKind.Default;
            }
            return ExportKind.Named;
        }

        ImportExportSymbol? GetSpecialPropertyExport(Node node, bool useLhsSymbol)
        {
            ExportKind kind;
            switch (AstUtilities.GetAssignmentDeclarationKind(node))
            {
                case JSDeclarationKind.ExportsProperty:
                    kind = ExportKind.Named;
                    break;
                case JSDeclarationKind.ModuleExports:
                    kind = ExportKind.ExportEquals;
                    break;
                default:
                    return null;
            }
            var sym = symbol;
            if (useLhsSymbol)
            {
                sym = node.Symbol();
            }
            if (sym == null)
            {
                return null;
            }
            return ExportInfoOf(sym, kind);
        }

        ImportExportSymbol? GetExport()
        {
            var parent = node.Parent!;
            var grandparent = parent.Parent!;
            if (symbol.ExportSymbol != null)
            {
                if (NodeTests.IsPropertyAccessExpression(parent))
                {
                    // When accessing an export of a JS module, there's no alias. The symbol will still be flagged as an export even though we're at the use.
                    // So check that we are at the declaration.
                    if (NodeTests.IsBinaryExpression(grandparent) && ContainsDeclaration(symbol.Declarations, parent))
                    {
                        return GetSpecialPropertyExport(grandparent, false /*useLhsSymbol*/);
                    }
                    return null;
                }
                return ExportInfoOf(symbol.ExportSymbol, GetExportKindForDeclaration(parent));
            }
            else
            {
                var exportNode = GetExportNode(parent, node);
                if (exportNode != null && (AstUtilities.HasSyntacticModifier(exportNode, ModifierFlags.Export) || AstUtilities.IsImplicitlyExportedJSDocDeclaration(exportNode)))
                {
                    if (NodeTests.IsImportEqualsDeclaration(exportNode) && exportNode.AsImportEqualsDeclaration().ModuleReference == node)
                    {
                        // We're at `Y` in `export import X = Y`. This is not the exported symbol, the left-hand-side is. So treat this as an import statement.
                        if (comingFromExport)
                        {
                            return null;
                        }
                        var lhsSymbol = checker.GetSymbolAtLocation(exportNode.Name()!);
                        return new ImportExportSymbol
                        {
                            Kind = ImpExpKind.Import,
                            Symbol = lhsSymbol,
                        };
                    }
                    return ExportInfoOf(symbol, GetExportKindForDeclaration(exportNode));
                }
                else if (NodeTests.IsNamespaceExport(parent))
                {
                    return ExportInfoOf(symbol, ExportKind.Named);
                }
                else if (NodeTests.IsExportAssignment(parent))
                {
                    return GetExportAssignmentExport(parent);
                }
                else if (NodeTests.IsExportAssignment(grandparent))
                {
                    return GetExportAssignmentExport(grandparent);
                }
                else if (NodeTests.IsBinaryExpression(parent))
                {
                    return GetSpecialPropertyExport(parent, true /*useLhsSymbol*/);
                }
                else if (NodeTests.IsBinaryExpression(grandparent))
                {
                    return GetSpecialPropertyExport(grandparent, true /*useLhsSymbol*/);
                }
                else if (NodeTests.IsJSDocTypedefTag(parent) || NodeTests.IsJSDocCallbackTag(parent))
                {
                    return ExportInfoOf(symbol, ExportKind.Named);
                }
            }
            return null;
        }

        ImportExportSymbol? GetImport()
        {
            if (!IsNodeImport(node))
            {
                return null;
            }
            // JS destructuring from `require(...)` is import-like for references, but the binding element
            // itself is still a local variable symbol rather than an alias.
            global::Ts.Ast.Symbol? importedSymbol;
            if ((symbol.Flags & SymbolFlags.Alias) != 0)
            {
                importedSymbol = checker.GetImmediateAliasedSymbolEx(symbol);
            }
            else
            {
                importedSymbol = FindRefsSupport.GetPropertySymbolOfObjectBindingPatternWithoutPropertyName(symbol, checker);
            }
            if (importedSymbol == null)
            {
                return null;
            }
            // Search on the local symbol in the exporting module, not the exported symbol.
            importedSymbol = SkipExportSpecifierSymbol(importedSymbol, checker);
            if (importedSymbol == null)
            {
                return null;
            }
            // Similarly, skip past the symbol for 'export ='
            if (importedSymbol.Name == "export=")
            {
                importedSymbol = GetExportEqualsLocalSymbol(importedSymbol, checker);
                if (importedSymbol == null)
                {
                    return null;
                }
            }
            // If the import has a different name than the export, do not continue searching.
            // If `importedName` is undefined, do continue searching as the export is anonymous.
            // (All imports returned from this function will be ignored anyway if we are in rename and this is a not a named export.)
            var importedName = SymbolNameNoDefault(importedSymbol);
            if (importedName == "" || importedName == global::Ts.Ast.InternalSymbolName.Default || importedName == symbol.Name)
            {
                return new ImportExportSymbol
                {
                    Kind = ImpExpKind.Import,
                    Symbol = importedSymbol,
                };
            }
            return null;
        }

        var result = GetExport();
        if (result == null && !comingFromExport)
        {
            result = GetImport();
        }
        return result;
    }

    private static bool ContainsDeclaration(Node[]? declarations, Node declaration)
    {
        foreach (var d in declarations ?? [])
        {
            if (d == declaration)
            {
                return true;
            }
        }
        return false;
    }

    // importTracker.go:611-624 — getExportInfo.
    internal static ExportInfo? GetExportInfo(global::Ts.Ast.Symbol exportSymbol, ExportKind exportKind, global::Ts.Checker.Checker c)
    {
        // Parent can be nil if an `export` is not at the top-level (which is a compile error).
        if (exportSymbol.Parent != null)
        {
            var exportingModuleSymbol = c.GetMergedSymbol(exportSymbol.Parent)!;
            // `export` may appear in a namespace. In that case, just rely on global search.
            if (global::Ts.Checker.Checker.IsExternalModuleSymbol(exportingModuleSymbol))
            {
                return new ExportInfo(
                    ExportingModuleSymbol: exportingModuleSymbol,
                    ExportKind: exportKind);
            }
        }
        return null;
    }

    // importTracker.go:626-643 — getExportNode.
    // If a reference is a class expression, the exported node would be its parent.
    // If a reference is a variable declaration, the exported node would be the variable statement.
    internal static Node? GetExportNode(Node parent, Node node)
    {
        Node? declaration = null;
        if (NodeTests.IsVariableDeclaration(parent))
        {
            declaration = parent;
        }
        else if (NodeTests.IsBindingElement(parent))
        {
            declaration = global::Ts.Checker.CheckerFindRefsLsExports.WalkUpBindingElementsAndPatternsForFindRefs(parent);
        }
        if (declaration != null)
        {
            if (parent.Name() == node && !NodeTests.IsCatchClause(declaration.Parent!) && NodeTests.IsVariableStatement(declaration.Parent!.Parent!))
            {
                return declaration.Parent!.Parent;
            }
            return null;
        }
        return parent;
    }

    // importTracker.go:645-660 — isNodeImport.
    internal static bool IsNodeImport(Node node)
    {
        var parent = node.Parent!;
        switch (parent.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
                return parent.Name() == node && IsExternalModuleImportEquals(parent);
            case SyntaxKind.ImportSpecifier:
                // For a rename import `{ foo as bar }`, don't search for the imported symbol. Just find local uses of `bar`.
                return parent.PropertyName() == null;
            case SyntaxKind.ImportClause or SyntaxKind.NamespaceImport:
                global::Ts.Core.Debug.Assert(parent.Name() == node);
                return true;
            case SyntaxKind.BindingElement:
                return AstUtilities.IsInJSFile(node) && AstUtilities.IsVariableDeclarationInitializedToBareOrAccessedRequire(parent.Parent!.Parent!);
        }
        return false;
    }

    // importTracker.go:662-665 — isExternalModuleImportEquals.
    internal static bool IsExternalModuleImportEquals(Node node)
    {
        var moduleReference = node.AsImportEqualsDeclaration().ModuleReference!;
        return NodeTests.IsExternalModuleReference(moduleReference) && moduleReference.Expression()!.Kind == SyntaxKind.StringLiteral;
    }

    // importTracker.go:667-682 — skipExportSpecifierSymbol.
    // If at an export specifier, go to the symbol it refers to.
    internal static global::Ts.Ast.Symbol? SkipExportSpecifierSymbol(global::Ts.Ast.Symbol symbol, global::Ts.Checker.Checker checker)
    {
        // For `export { foo } from './bar", there's nothing to skip, because it does not create a new alias. But `export { foo } does.
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (NodeTests.IsExportSpecifier(declaration) && declaration.PropertyName() == null && declaration.Parent!.Parent!.ModuleSpecifier() == null)
            {
                return checker.GetExportSpecifierLocalTargetSymbol(declaration) ?? symbol;
            }
            else if (NodeTests.IsPropertyAccessExpression(declaration) && AstUtilities.IsModuleExportsAccessExpression(declaration.Expression()!) && !NodeTests.IsPrivateIdentifier(declaration.Name()!))
            {
                // Export of form 'module.exports.propName = expr';
                return checker.GetSymbolAtLocation(declaration);
            }
            else if (NodeTests.IsShorthandPropertyAssignment(declaration) && NodeTests.IsBinaryExpression(declaration.Parent!.Parent!) && AstUtilities.GetAssignmentDeclarationKind(declaration.Parent!.Parent!) == JSDeclarationKind.ModuleExports)
            {
                return checker.GetExportSpecifierLocalTargetSymbol(declaration.Name()!);
            }
        }
        return symbol;
    }

    // importTracker.go:684-699 — getExportEqualsLocalSymbol.
    internal static global::Ts.Ast.Symbol? GetExportEqualsLocalSymbol(global::Ts.Ast.Symbol importedSymbol, global::Ts.Checker.Checker checker)
    {
        if ((importedSymbol.Flags & SymbolFlags.Alias) != 0)
        {
            return checker.GetImmediateAliasedSymbolEx(importedSymbol);
        }
        var decl = importedSymbol.ValueDeclaration!;
        global::Ts.Core.Debug.Assert(decl != null);
        if (NodeTests.IsExportAssignment(decl))
        {
            return decl.Expression()!.Symbol();
        }
        else if (NodeTests.IsBinaryExpression(decl))
        {
            return decl.AsBinaryExpression().Right!.Symbol();
        }
        else if (NodeTests.IsSourceFile(decl))
        {
            return decl.Symbol();
        }
        return null;
    }

    // importTracker.go:701-712 — symbolNameNoDefault.
    internal static string SymbolNameNoDefault(global::Ts.Ast.Symbol symbol)
    {
        if (symbol.Name != global::Ts.Ast.InternalSymbolName.Default)
        {
            return symbol.Name;
        }
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (AstUtilities.GetNameOfDeclaration(decl) is { } name && NodeTests.IsIdentifier(name))
            {
                return name.Text();
            }
        }
        return "";
    }

    // importTracker.go:714-767 — findModuleReferences.
    // findModuleReferences finds all references to a module symbol across the given source files.
    // This includes import statements, <reference> directives, and implicit references (e.g., JSX runtime imports).
    internal static List<ModuleReference> FindModuleReferences(global::Ts.Compiler.Program program, SourceFile[] sourceFiles, global::Ts.Ast.Symbol searchModuleSymbol, global::Ts.Checker.Checker checker)
    {
        var refs = new List<ModuleReference>();

        foreach (var referencingFile in sourceFiles)
        {
            var searchSourceFile = searchModuleSymbol.ValueDeclaration;
            if (searchSourceFile != null && searchSourceFile.Kind == SyntaxKind.SourceFile)
            {
                // Check <reference path> directives
                foreach (var @ref in referencingFile.ReferencedFiles ?? [])
                {
                    if (program.GetSourceFileFromReference(referencingFile, @ref) == searchSourceFile.AsSourceFile())
                    {
                        refs.Add(new ModuleReference
                        {
                            Kind = ModuleReferenceKind.Reference,
                            ReferencingFile = referencingFile,
                            Ref = @ref,
                        });
                    }
                }

                // Check <reference types> directives
                // Deferred: program.GetResolvedTypeReferenceDirectiveFromTypeReferenceDirective
                // is not on the C# Program surface yet (same deferral as
                // SourceDefinitionDeps.GetReferenceAtPosition's types arm).
            }

            // Check all imports (including require() calls)
            ForEachImport(program, referencingFile, (importDecl, moduleSpecifier) =>
            {
                var moduleSymbol = checker.GetSymbolAtLocation(moduleSpecifier);
                if (moduleSymbol == searchModuleSymbol)
                {
                    if (AstUtilities.NodeIsSynthesized(importDecl))
                    {
                        refs.Add(new ModuleReference
                        {
                            Kind = ModuleReferenceKind.Implicit,
                            Literal = moduleSpecifier,
                            ReferencingFile = referencingFile,
                        });
                    }
                    else
                    {
                        refs.Add(new ModuleReference
                        {
                            Kind = ModuleReferenceKind.Import,
                            Literal = moduleSpecifier,
                        });
                    }
                }
            });
        }

        return refs;
    }
}
