// Ported from tsgo internal/ls/organizeimports.go (whole file): the LanguageService.OrganizeImports
// entry point and its import/export coalescing workers. The lsutil half (comparer construction and
// sort-order detection, internal/ls/lsutil/organizeimports.go) is the static OrganizeImports class
// in Ls/OrganizeImports.cs; inside LanguageService the simple name OrganizeImports binds to the
// method, so that class is reached through the LsOrganizeImports alias below.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ts.Ast;
using Ts.Ls.Change;
using LsOrganizeImports = Ts.Ls.OrganizeImports;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // organizeimports.go:20-115 — OrganizeImports organizes imports by:
    //  1. Removing unused imports
    //  2. Coalescing imports from the same module
    //  3. Sorting imports
    internal Dictionary<string, List<global::Ts.LsProto.TextEdit>> OrganizeImports(
        System.Threading.CancellationToken ctx,
        SourceFile sourceFile,
        global::Ts.Compiler.Program program,
        global::Ts.LsProto.CodeActionKind kind)
    {
        var changeTracker = Tracker.NewTracker(program.Options(), FormatOptions(), Converters());
        bool shouldSort = kind == global::Ts.LsProto.CodeActionKind.SourceSortImports || kind == global::Ts.LsProto.CodeActionKind.SourceOrganizeImports;
        bool shouldCombine = shouldSort;
        bool shouldRemove = kind == global::Ts.LsProto.CodeActionKind.SourceRemoveUnusedImports || kind == global::Ts.LsProto.CodeActionKind.SourceOrganizeImports;
        Statement[] topLevelImportDecls = LsOrganizeImports.FilterImportDeclarations(sourceFile.Statements!.Nodes);
        List<Statement[]> topLevelImportGroupDecls = GroupByNewlineContiguous(sourceFile, topLevelImportDecls);

        var preferences = UserPreferences();
        var (comparersToTest, typeOrdersToTest) = LsOrganizeImports.GetDetectionLists(preferences);
        var defaultComparer = comparersToTest[0];
        var sort = LsOrganizeImports.ResolveOrganizeImportsSort(preferences);

        Func<string, string, int>? moduleSpecifierComparer = null;
        Func<string, string, int>? namedImportComparer = null;
        if (sort != OrganizeImportsSort.Auto)
        {
            moduleSpecifierComparer = defaultComparer;
            namedImportComparer = defaultComparer;
        }
        var typeOrder = preferences.OrganizeImportsTypeOrder;

        if (sort == OrganizeImportsSort.Auto)
        {
            var (result, _) = LsOrganizeImports.DetectModuleSpecifierCaseBySort(topLevelImportGroupDecls, comparersToTest);
            moduleSpecifierComparer = result;
        }

        if (typeOrder == OrganizeImportsTypeOrder.Auto || sort == OrganizeImportsSort.Auto)
        {
            var (namedImportComparer2, typeOrder2, found) = LsOrganizeImports.DetectNamedImportOrganizationBySort(topLevelImportDecls, comparersToTest, typeOrdersToTest);
            if (found)
            {
                if (namedImportComparer == null || sort == OrganizeImportsSort.Auto)
                {
                    namedImportComparer = namedImportComparer2;
                }
                if (typeOrder == OrganizeImportsTypeOrder.Auto)
                {
                    typeOrder = typeOrder2;
                }
            }
        }

        var comparer = new OrganizeImportsComparerSettings
        {
            moduleSpecifierComparer = moduleSpecifierComparer!,
            namedImportComparer = namedImportComparer,
            typeOrder = typeOrder,
        };

        foreach (var importGroupDecl in topLevelImportGroupDecls)
        {
            OrganizeImportsWorker(importGroupDecl, comparer, shouldSort, shouldCombine, shouldRemove, sourceFile, program, changeTracker, ctx);
        }

        if (kind != global::Ts.LsProto.CodeActionKind.SourceRemoveUnusedImports)
        {
            var topLevelExportGroupDecls = GetTopLevelExportGroups(sourceFile);
            foreach (var exportGroupDecl in topLevelExportGroupDecls)
            {
                OrganizeExportsWorker(exportGroupDecl, comparer, sourceFile, changeTracker);
            }
        }

        foreach (var stmt in sourceFile.Statements!.Nodes)
        {
            if (!AstUtilities.IsAmbientModule(stmt))
            {
                continue;
            }

            var ambientModule = stmt.AsModuleDeclaration();
            if (ambientModule.Body == null)
            {
                continue;
            }

            var moduleBody = ambientModule.Body.AsModuleBlock();

            Statement[] ambientModuleImportDecls = LsOrganizeImports.FilterImportDeclarations(moduleBody.Statements!.Nodes);
            var ambientModuleImportGroupDecls = GroupByNewlineContiguous(sourceFile, ambientModuleImportDecls);

            foreach (var importGroupDecl in ambientModuleImportGroupDecls)
            {
                OrganizeImportsWorker(importGroupDecl, comparer, shouldSort, shouldCombine, shouldRemove, sourceFile, program, changeTracker, ctx);
            }

            if (kind != global::Ts.LsProto.CodeActionKind.SourceRemoveUnusedImports)
            {
                var ambientModuleExportDecls = new List<Statement>();
                foreach (var s in moduleBody.Statements!.Nodes)
                {
                    if (s.Kind == SyntaxKind.ExportDeclaration)
                    {
                        ambientModuleExportDecls.Add(s);
                    }
                }
                OrganizeExportsWorker([.. ambientModuleExportDecls], comparer, sourceFile, changeTracker);
            }
        }

        return changeTracker.GetChanges();
    }

    // organizeimports.go:117-121 — organizeImportsComparerSettings.
    private sealed class OrganizeImportsComparerSettings
    {
        internal Func<string, string, int> moduleSpecifierComparer = null!;
        internal Func<string, string, int>? namedImportComparer;
        internal OrganizeImportsTypeOrder typeOrder;
    }

    // organizeimports.go:123-215 — organizeImportsWorker.
    private static void OrganizeImportsWorker(
        Statement[] oldImportDecls,
        OrganizeImportsComparerSettings comparer,
        bool shouldSort,
        bool shouldCombine,
        bool shouldRemove,
        SourceFile sourceFile,
        global::Ts.Compiler.Program program,
        Tracker changeTracker,
        System.Threading.CancellationToken ctx)
    {
        if (oldImportDecls.Length == 0)
        {
            return;
        }

        // Header comment preservation is handled via LeadingTriviaOptionExclude in the change tracker below

        List<Statement> processedImports = [.. oldImportDecls];
        if (shouldRemove)
        {
            var (typeChecker, done) = program.GetTypeCheckerForFile(ctx, sourceFile);
            try
            {
                processedImports = RemoveUnusedImports(processedImports, sourceFile, typeChecker, program, changeTracker);
            }
            finally
            {
                done();
            }
        }

        List<Statement> newImportDecls;
        if (shouldCombine)
        {
            newImportDecls = [];
            var grouped = GroupByModuleSpecifier(processedImports);
            if (shouldSort)
            {
                grouped.Sort((a, b) =>
                {
                    if (a.Count == 0 || b.Count == 0)
                    {
                        return 0;
                    }
                    return LsOrganizeImports.CompareModuleSpecifiers(
                        a[0].ModuleSpecifier(),
                        b[0].ModuleSpecifier(),
                        comparer.moduleSpecifierComparer);
                });
            }

            var specifierComparer = LsOrganizeImports.GetNamedImportSpecifierComparer(
                new UserPreferences { OrganizeImportsTypeOrder = comparer.typeOrder },
                comparer.namedImportComparer);

            foreach (var importGroup in grouped)
            {
                var coalesced = CoalesceImportsWorker(importGroup, comparer.moduleSpecifierComparer, specifierComparer, sourceFile, changeTracker);
                if (shouldSort)
                {
                    coalesced.Sort((a, b) => LsOrganizeImports.CompareImportsOrRequireStatements(a, b, comparer.moduleSpecifierComparer));
                }
                newImportDecls.AddRange(coalesced);
            }
        }
        else
        {
            newImportDecls = processedImports;
        }

        if (shouldSort && !shouldCombine)
        {
            newImportDecls.Sort((a, b) => LsOrganizeImports.CompareImportsOrRequireStatements(a, b, comparer.moduleSpecifierComparer));
        }

        if (newImportDecls.Count == 0)
        {
            changeTracker.DeleteNodeRange(
                sourceFile,
                oldImportDecls[0],
                oldImportDecls[^1],
                LeadingTriviaOption.Exclude, // Preserve header comment
                TrailingTriviaOption.Include);
        }
        else
        {
            foreach (var imp in newImportDecls)
            {
                changeTracker.EmitContext.SetEmitFlags(imp, global::Ts.Printer.EmitFlags.NoLeadingComments);
            }

            var options = new NodeOptions
            {
                LeadingTriviaOption = LeadingTriviaOption.Exclude, // Preserve header comment
                TrailingTriviaOption = TrailingTriviaOption.Include,
                Suffix = "\n",
            };

            changeTracker.ReplaceNodeWithNodes(sourceFile, oldImportDecls[0], [.. newImportDecls], options);

            if (oldImportDecls.Length > 1)
            {
                for (int i = 1; i < oldImportDecls.Length; i++)
                {
                    changeTracker.Delete(sourceFile, oldImportDecls[i]);
                }
            }
        }
    }

    // organizeimports.go:217-234 — groupByModuleSpecifier.
    private static List<List<Statement>> GroupByModuleSpecifier(List<Statement> imports)
    {
        var groups = new Dictionary<string, List<Statement>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var imp in imports)
        {
            string specifier = LsOrganizeImports.GetExternalModuleName(imp.ModuleSpecifier());
            if (!groups.TryGetValue(specifier, out var group))
            {
                order.Add(specifier);
                group = [];
                groups[specifier] = group;
            }
            group.Add(imp);
        }

        var result = new List<List<Statement>>(order.Count);
        foreach (var key in order)
        {
            result.Add(groups[key]);
        }
        return result;
    }

    // organizeimports.go:236-315 — removeUnusedImports.
    private static List<Statement> RemoveUnusedImports(List<Statement> oldImports, SourceFile sourceFile, global::Ts.Checker.Checker typeChecker, global::Ts.Compiler.Program program, Tracker changeTracker)
    {
        var compilerOptions = program.Options();
        bool jsxElementsPresent = (sourceFile.AsNode().SubtreeFacts() & SubtreeFacts.SubtreeContainsJsx) != 0;
        bool jsxModeNeedsExplicitImport = compilerOptions.Jsx == global::Ts.Core.JsxEmit.React || compilerOptions.Jsx == global::Ts.Core.JsxEmit.ReactNative;

        var factory = new NodeFactory(new NodeFactoryHooks());
        var usedImports = new List<Statement>(oldImports.Count);

        foreach (var importDecl in oldImports)
        {
            var importClause = importDecl.AsImportDeclaration().ImportClause;
            if (importClause == null)
            {
                usedImports.Add(importDecl);
                continue;
            }

            var clause = importClause.AsImportClause();
            var name = clause.Name();
            var namedBindings = clause.NamedBindings;

            if (name != null && !typeChecker.IsDeclarationUsed(sourceFile, name.AsIdentifier(), jsxElementsPresent, jsxModeNeedsExplicitImport))
            {
                name = null;
            }

            if (namedBindings != null)
            {
                switch (namedBindings.Kind)
                {
                    case SyntaxKind.NamespaceImport:
                        var nsImport = namedBindings.AsNamespaceImport();
                        if (!typeChecker.IsDeclarationUsed(sourceFile, nsImport.Name()!.AsIdentifier(), jsxElementsPresent, jsxModeNeedsExplicitImport))
                        {
                            namedBindings = null;
                        }
                        break;
                    case SyntaxKind.NamedImports:
                        var namedImports = namedBindings.AsNamedImports();
                        var originalBindings = namedBindings;
                        var newElements = FilterUsedImportSpecifiers(namedImports.Elements!.Nodes, typeChecker, sourceFile, jsxElementsPresent, jsxModeNeedsExplicitImport);
                        if (newElements.Count == 0)
                        {
                            namedBindings = null;
                        }
                        else if (newElements.Count < namedImports.Elements.Nodes.Length)
                        {
                            var newList = factory.NewNodeList([.. newElements]);
                            var updatedNamedImports = factory.UpdateNamedImports(namedImports, newList);
                            namedBindings = updatedNamedImports;
                        }
                        if (namedBindings != null && !AstUtilities.NodeIsSynthesized(originalBindings) && !global::Ts.Checker.CheckerFindRefsLsExports.RangeIsOnSingleLine(originalBindings.Loc, sourceFile))
                        {
                            changeTracker.EmitContext.SetEmitFlags(namedBindings, global::Ts.Printer.EmitFlags.MultiLine);
                        }
                        break;
                }
            }

            if (name != null || namedBindings != null)
            {
                var importDeclNode = importDecl.AsImportDeclaration();
                var newClause = factory.UpdateImportClause(clause, clause.PhaseModifier, name, namedBindings);
                var newImportDecl = factory.UpdateImportDeclaration(
                    importDeclNode,
                    importDeclNode.Modifiers(),
                    newClause,
                    importDeclNode.ModuleSpecifier,
                    importDeclNode.Attributes);
                usedImports.Add(newImportDecl);
            }
            else
            {
                var moduleSpecifier = importDecl.ModuleSpecifier();
                if (HasModuleDeclarationMatchingSpecifier(sourceFile, moduleSpecifier))
                {
                    if (sourceFile.IsDeclarationFile)
                    {
                        var importDeclNode = importDecl.AsImportDeclaration();
                        var newImportDecl = factory.UpdateImportDeclaration(
                            importDeclNode,
                            importDeclNode.Modifiers(),
                            null, // no import clause
                            importDeclNode.ModuleSpecifier,
                            importDeclNode.Attributes);
                        usedImports.Add(newImportDecl);
                    }
                    else
                    {
                        usedImports.Add(importDecl);
                    }
                }
            }
        }

        return usedImports;
    }

    // organizeimports.go:317-333 — filterUsedImportSpecifiers.
    private static List<Statement> FilterUsedImportSpecifiers(
        Statement[] elements,
        global::Ts.Checker.Checker typeChecker,
        SourceFile sourceFile,
        bool jsxElementsPresent,
        bool jsxModeNeedsExplicitImport)
    {
        var result = new List<Statement>();
        foreach (var elem in elements)
        {
            var spec = elem.AsImportSpecifier();
            if (typeChecker.IsDeclarationUsed(sourceFile, spec.Name()!.AsIdentifier(), jsxElementsPresent, jsxModeNeedsExplicitImport))
            {
                result.Add(elem);
            }
        }
        return result;
    }

    // organizeimports.go:335-349 — hasModuleDeclarationMatchingSpecifier.
    private static bool HasModuleDeclarationMatchingSpecifier(SourceFile sourceFile, Expression? moduleSpecifier)
    {
        if (moduleSpecifier == null || !NodeTests.IsStringLiteral(moduleSpecifier))
        {
            return false;
        }
        string moduleSpecifierText = moduleSpecifier.Text();

        foreach (var moduleName in sourceFile.ModuleAugmentations ?? [])
        {
            if (NodeTests.IsStringLiteral(moduleName) && moduleName.Text() == moduleSpecifierText)
            {
                return true;
            }
        }

        return false;
    }

    // organizeimports.go:351-386 — getImportAttributesKey returns a key for grouping imports by
    // their attributes. (Go writes Token.String(), the stringer name; the enum name serves the same
    // role here — the key is only compared for equality, never emitted.)
    private static string GetImportAttributesKey(ImportAttributesNode? attributes)
    {
        if (attributes == null)
        {
            return "";
        }

        var importAttrs = attributes.AsImportAttributes();
        var key = new StringBuilder();
        key.Append(importAttrs.Token.ToString());
        key.Append(' ');

        var attrNodes = (Node[])importAttrs.Attributes!.Nodes.Clone();
        Array.Sort(attrNodes, (a, b) =>
        {
            string aName = a.AsImportAttribute().Name()!.Text();
            string bName = b.AsImportAttribute().Name()!.Text();
            return global::Ts.StringUtil.StringUtil.CompareStringsCaseSensitive(aName, bName);
        });

        foreach (var attrNode in attrNodes)
        {
            var attr = attrNode.AsImportAttribute();
            key.Append(attr.Name()!.Text());
            key.Append(':');
            if (AstUtilities.IsStringLiteralLike(attr.Value!))
            {
                key.Append('"');
                key.Append(attr.Value!.Text());
                key.Append('"');
            }
            else
            {
                key.Append(attr.Value!.Text());
            }
            key.Append(' ');
        }

        return key.ToString();
    }

    // organizeimports.go:388-410 — groupByNewlineContiguous groups declarations by blank lines
    // between them.
    private static List<Statement[]> GroupByNewlineContiguous(SourceFile sourceFile, Statement[] decls)
    {
        var s = new global::Ts.Scanner.Scanner();
        s.SetSkipTrivia(false); // Must not skip trivia to detect newlines
        var groups = new List<Statement[]>();
        var currentGroup = new List<Statement>();

        foreach (var decl in decls)
        {
            if (currentGroup.Count > 0 && IsNewGroup(sourceFile, decl, s))
            {
                groups.Add([.. currentGroup]);
                currentGroup = [];
            }
            currentGroup.Add(decl);
        }

        if (currentGroup.Count > 0)
        {
            groups.Add([.. currentGroup]);
        }

        return groups;
    }

    // organizeimports.go:412-440 — isNewGroup.
    private static bool IsNewGroup(SourceFile sourceFile, Statement decl, global::Ts.Scanner.Scanner s)
    {
        int fullStart = decl.Pos;
        if (fullStart < 0)
        {
            return false;
        }

        string text = sourceFile.Text();
        int textLen = text.Length;

        if (fullStart >= textLen)
        {
            return false;
        }

        int startPos = global::Ts.Scanner.Scanner.SkipTrivia(text, fullStart);
        if (startPos <= fullStart)
        {
            return false;
        }

        int triviaLen = startPos - fullStart;
        s.SetText(text[fullStart..startPos]);

        int numberOfNewLines = 0;
        while (s.TokenStart() < triviaLen)
        {
            var tokenKind = s.Scan();
            if (tokenKind == SyntaxKind.NewLineTrivia)
            {
                numberOfNewLines++;
                if (numberOfNewLines >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // organizeimports.go:442-630 — coalesceImportsWorker.
    private static List<Statement> CoalesceImportsWorker(
        List<Statement> importDecls,
        Func<string, string, int> comparer,
        Func<Node, Node, int> specifierComparer,
        SourceFile? sourceFile,
        Tracker changeTracker)
    {
        if (importDecls.Count == 0)
        {
            return importDecls;
        }

        var importGroupsByAttributes = new Dictionary<string, List<Statement>>(StringComparer.Ordinal);
        var attributeKeys = new List<string>();

        foreach (var importDecl in importDecls)
        {
            string key = GetImportAttributesKey(importDecl.AsImportDeclaration().Attributes);
            if (!importGroupsByAttributes.TryGetValue(key, out var bucket))
            {
                attributeKeys.Add(key);
                bucket = [];
                importGroupsByAttributes[key] = bucket;
            }
            bucket.Add(importDecl);
        }

        var coalescedImports = new List<Statement>();

        foreach (var attributeKey in attributeKeys)
        {
            var importGroupSameAttrs = importGroupsByAttributes[attributeKey];
            var categorized = GetCategorizedImports(importGroupSameAttrs);

            if (categorized.importWithoutClause != null)
            {
                coalescedImports.Add(categorized.importWithoutClause);
            }

            var factory = new NodeFactory(new NodeFactoryHooks());

            ImportGroup[] groups = [categorized.regularImports, categorized.typeOnlyImports];
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group.IsEmpty())
                {
                    continue;
                }

                bool isTypeOnly = i == 1;

                if (!isTypeOnly && group.defaultImports.Count == 1 && group.namespaceImports.Count == 1 && group.namedImports.Count == 0)
                {
                    var defaultImport = group.defaultImports[0];
                    var namespaceImport = group.namespaceImports[0];

                    var defaultClause = defaultImport.AsImportDeclaration().ImportClause!.AsImportClause();
                    var namespaceBindings = namespaceImport.AsImportDeclaration().ImportClause!.AsImportClause().NamedBindings;

                    var mergedClause = factory.UpdateImportClause(defaultClause, defaultClause.PhaseModifier, defaultClause.Name(), namespaceBindings);
                    var defaultDeclNode = defaultImport.AsImportDeclaration();
                    var mergedImportDecl = factory.UpdateImportDeclaration(
                        defaultDeclNode,
                        defaultDeclNode.Modifiers(),
                        mergedClause,
                        defaultDeclNode.ModuleSpecifier,
                        defaultDeclNode.Attributes);
                    coalescedImports.Add(mergedImportDecl);
                    continue;
                }

                group.namespaceImports.Sort((a, b) =>
                {
                    var n1 = a.AsImportDeclaration().ImportClause!.AsImportClause().NamedBindings!.AsNamespaceImport().Name()!;
                    var n2 = b.AsImportDeclaration().ImportClause!.AsImportClause().NamedBindings!.AsNamespaceImport().Name()!;
                    return comparer(n1.Text(), n2.Text());
                });

                foreach (var nsImport in group.namespaceImports)
                {
                    var nsImportDecl = nsImport.AsImportDeclaration();
                    var clause = nsImportDecl.ImportClause!.AsImportClause();
                    var nsClause = factory.UpdateImportClause(clause, clause.PhaseModifier, null, clause.NamedBindings);
                    var nsNewImportDecl = factory.UpdateImportDeclaration(
                        nsImportDecl,
                        nsImportDecl.Modifiers(),
                        nsClause,
                        nsImportDecl.ModuleSpecifier,
                        nsImportDecl.Attributes);
                    coalescedImports.Add(nsNewImportDecl);
                }

                Statement? firstDefaultImport = null;
                Statement? firstNamedImport = null;

                if (group.defaultImports.Count > 0)
                {
                    firstDefaultImport = group.defaultImports[0];
                }
                if (group.namedImports.Count > 0)
                {
                    firstNamedImport = group.namedImports[0];
                }

                var importDecl = firstDefaultImport;
                if (importDecl == null)
                {
                    importDecl = firstNamedImport;
                }
                if (importDecl == null)
                {
                    continue;
                }

                IdentifierNode? newDefaultImport = null;
                var newImportSpecifiers = new List<Node>();

                if (group.defaultImports.Count == 1)
                {
                    newDefaultImport = group.defaultImports[0].AsImportDeclaration().ImportClause!.AsImportClause().Name();
                }
                else
                {
                    foreach (var defaultImport in group.defaultImports)
                    {
                        var defaultClause = defaultImport.AsImportDeclaration().ImportClause!.AsImportClause();
                        var defaultName = defaultClause.Name();
                        var propertyName = factory.NewIdentifier("default");
                        var importSpec = factory.NewImportSpecifier(false, propertyName, defaultName);
                        newImportSpecifiers.Add(importSpec);
                    }
                }

                newImportSpecifiers.AddRange(GetNewImportSpecifiers(group.namedImports, factory));
                newImportSpecifiers = SortStable(newImportSpecifiers, specifierComparer);

                NamedImportBindings? newNamedImports;
                if (newImportSpecifiers.Count == 0)
                {
                    if (newDefaultImport != null)
                    {
                        newNamedImports = null;
                    }
                    else
                    {
                        newNamedImports = factory.NewNamedImports(factory.NewNodeList([]));
                    }
                }
                else
                {
                    var sortedList = factory.NewNodeList([.. newImportSpecifiers]);
                    if (firstNamedImport != null)
                    {
                        var firstNamedBindings = firstNamedImport.AsImportDeclaration().ImportClause!.AsImportClause().NamedBindings!.AsNamedImports();
                        var originalElements = firstNamedBindings.Elements!;
                        if (originalElements.HasTrailingComma())
                        {
                            sortedList.Loc = originalElements.Loc;
                        }
                        newNamedImports = factory.UpdateNamedImports(firstNamedBindings, sortedList);
                    }
                    else
                    {
                        newNamedImports = factory.NewNamedImports(sortedList);
                    }
                }

                if (sourceFile != null && newNamedImports != null && firstNamedImport != null)
                {
                    var firstNamedBindings = firstNamedImport.AsImportDeclaration().ImportClause!.AsImportClause().NamedBindings!;
                    if (!AstUtilities.NodeIsSynthesized(firstNamedBindings) && !global::Ts.Checker.CheckerFindRefsLsExports.RangeIsOnSingleLine(firstNamedBindings.Loc, sourceFile))
                    {
                        changeTracker.EmitContext.SetEmitFlags(newNamedImports, global::Ts.Printer.EmitFlags.MultiLine);
                    }
                }

                if (isTypeOnly && newDefaultImport != null && newNamedImports != null)
                {
                    var importDeclNode = importDecl.AsImportDeclaration();

                    var defaultClause = factory.NewImportClause(importDeclNode.ImportClause!.AsImportClause().PhaseModifier, newDefaultImport, null);
                    var defaultImportDecl = factory.UpdateImportDeclaration(
                        importDeclNode,
                        importDeclNode.Modifiers(),
                        defaultClause,
                        importDeclNode.ModuleSpecifier,
                        importDeclNode.Attributes);
                    coalescedImports.Add(defaultImportDecl);

                    var namedDeclNode = firstNamedImport;
                    if (namedDeclNode == null)
                    {
                        namedDeclNode = importDecl;
                    }
                    var namedImportDeclNode = namedDeclNode.AsImportDeclaration();
                    var namedClause = factory.NewImportClause(namedImportDeclNode.ImportClause!.AsImportClause().PhaseModifier, null, newNamedImports);
                    var namedImportDecl = factory.UpdateImportDeclaration(
                        namedImportDeclNode,
                        namedImportDeclNode.Modifiers(),
                        namedClause,
                        namedImportDeclNode.ModuleSpecifier,
                        namedImportDeclNode.Attributes);
                    coalescedImports.Add(namedImportDecl);
                }
                else
                {
                    var importDeclNode = importDecl.AsImportDeclaration();
                    var clauseNode = importDeclNode.ImportClause!.AsImportClause();
                    var newClause = factory.UpdateImportClause(clauseNode, clauseNode.PhaseModifier, newDefaultImport, newNamedImports);
                    var newImportDecl = factory.UpdateImportDeclaration(
                        importDeclNode,
                        importDeclNode.Modifiers(),
                        newClause,
                        importDeclNode.ModuleSpecifier,
                        importDeclNode.Attributes);
                    coalescedImports.Add(newImportDecl);
                }
            }
        }
        return coalescedImports;
    }

    // Go slices.SortStableFunc: LINQ OrderBy is a stable sort.
    private static List<Node> SortStable(List<Node> list, Func<Node, Node, int> comparer) =>
        list.OrderBy(static n => n, Comparer<Node>.Create((a, b) => comparer(a, b))).ToList();

    // organizeimports.go:632-636 — categorizedImports.
    private sealed class CategorizedImports
    {
        internal Statement? importWithoutClause;
        internal ImportGroup typeOnlyImports = new();
        internal ImportGroup regularImports = new();
    }

    // organizeimports.go:638-646 — importGroup.
    private sealed class ImportGroup
    {
        internal List<Statement> defaultImports = [];
        internal List<Statement> namespaceImports = [];
        internal List<Statement> namedImports = [];

        internal bool IsEmpty() =>
            defaultImports.Count == 0 && namespaceImports.Count == 0 && namedImports.Count == 0;
    }

    // organizeimports.go:648-688 — getCategorizedImports.
    private static CategorizedImports GetCategorizedImports(List<Statement> importDecls)
    {
        var result = new CategorizedImports();

        foreach (var importDecl in importDecls)
        {
            if (importDecl.AsImportDeclaration().ImportClause == null)
            {
                if (result.importWithoutClause == null)
                {
                    result.importWithoutClause = importDecl;
                }
                continue;
            }

            var clause = importDecl.AsImportDeclaration().ImportClause!.AsImportClause();
            var group = result.regularImports;
            if (clause.IsTypeOnly())
            {
                group = result.typeOnlyImports;
            }

            var name = clause.Name();
            var namedBindings = clause.NamedBindings;

            if (name != null)
            {
                group.defaultImports.Add(importDecl);
            }

            if (namedBindings != null)
            {
                switch (namedBindings.Kind)
                {
                    case SyntaxKind.NamespaceImport:
                        group.namespaceImports.Add(importDecl);
                        break;
                    case SyntaxKind.NamedImports:
                        group.namedImports.Add(importDecl);
                        break;
                }
            }
        }

        return result;
    }

    // organizeimports.go:690-718 — getNewImportSpecifiers.
    private static List<Node> GetNewImportSpecifiers(List<Statement> namedImports, NodeFactory factory)
    {
        var result = new List<Node>();

        foreach (var namedImport in namedImports)
        {
            var elements = TryGetNamedBindingElements(namedImport);
            if (elements == null)
            {
                continue;
            }

            foreach (var elem in elements)
            {
                var spec = elem.AsImportSpecifier();

                if (spec.PropertyName != null && spec.Name() != null)
                {
                    string propertyText = spec.PropertyName.Text();
                    string nameText = spec.Name()!.Text();

                    if (propertyText == nameText)
                    {
                        var normalized = factory.UpdateImportSpecifier(spec, spec.IsTypeOnly, null, spec.Name());
                        result.Add(normalized);
                        continue;
                    }
                }

                result.Add(elem);
            }
        }

        return result;
    }

    // organizeimports.go:720-739 — tryGetNamedBindingElements.
    private static Statement[]? TryGetNamedBindingElements(Statement namedImport)
    {
        if (namedImport.Kind != SyntaxKind.ImportDeclaration)
        {
            return null;
        }

        var importDecl = namedImport.AsImportDeclaration();
        if (importDecl.ImportClause == null)
        {
            return null;
        }

        var clause = importDecl.ImportClause.AsImportClause();
        var namedBindings = clause.NamedBindings;

        if (namedBindings != null && namedBindings.Kind == SyntaxKind.NamedImports)
        {
            var namedImportsNode = namedBindings.AsNamedImports();
            return namedImportsNode.Elements?.Nodes;
        }

        return null;
    }

    // organizeimports.go:741-779 — getTopLevelExportGroups.
    private static List<Statement[]> GetTopLevelExportGroups(SourceFile sourceFile)
    {
        var topLevelExportGroups = new List<List<Statement>>();
        var statements = sourceFile.Statements!.Nodes;
        int statementsLen = statements.Length;

        int i = 0;
        int groupIndex = 0;
        while (i < statementsLen)
        {
            if (statements[i].Kind == SyntaxKind.ExportDeclaration)
            {
                if (groupIndex >= topLevelExportGroups.Count)
                {
                    topLevelExportGroups.Add([]);
                }
                var exportDecl = statements[i].AsExportDeclaration();
                if (exportDecl.ModuleSpecifier != null)
                {
                    topLevelExportGroups[groupIndex].Add(statements[i]);
                    i++;
                }
                else
                {
                    while (i < statementsLen && statements[i].Kind == SyntaxKind.ExportDeclaration)
                    {
                        topLevelExportGroups[groupIndex].Add(statements[i]);
                        i++;
                    }
                    groupIndex++;
                }
            }
            else
            {
                i++;
                if (groupIndex < topLevelExportGroups.Count && topLevelExportGroups[groupIndex].Count > 0)
                {
                    groupIndex++;
                }
            }
        }

        var result = new List<Statement[]>();
        foreach (var exportGroup in topLevelExportGroups)
        {
            var subGroups = GroupByNewlineContiguous(sourceFile, [.. exportGroup]);
            result.AddRange(subGroups);
        }

        return result;
    }

    // organizeimports.go:781-828 — organizeExportsWorker.
    private static void OrganizeExportsWorker(
        Statement[] oldExportDecls,
        OrganizeImportsComparerSettings comparer,
        SourceFile sourceFile,
        Tracker changeTracker)
    {
        if (oldExportDecls.Length == 0)
        {
            return;
        }

        var specifierComparerFunc = LsOrganizeImports.GetNamedImportSpecifierComparer(
            new UserPreferences { OrganizeImportsTypeOrder = comparer.typeOrder },
            comparer.namedImportComparer);

        var newExportDecls = CoalesceExportsWorker(oldExportDecls, specifierComparerFunc, comparer.moduleSpecifierComparer, sourceFile, changeTracker);

        if (oldExportDecls.Length > 0)
        {
            if (newExportDecls.Count == 0)
            {
                changeTracker.DeleteNodeRange(
                    sourceFile,
                    oldExportDecls[0],
                    oldExportDecls[^1],
                    LeadingTriviaOption.Exclude,
                    TrailingTriviaOption.Include);
            }
            else
            {
                foreach (var exp in newExportDecls)
                {
                    changeTracker.EmitContext.AddEmitFlags(exp, global::Ts.Printer.EmitFlags.NoLeadingComments);
                }

                var options = new NodeOptions
                {
                    LeadingTriviaOption = LeadingTriviaOption.Exclude,
                    TrailingTriviaOption = TrailingTriviaOption.Include,
                    Suffix = "\n",
                };

                changeTracker.ReplaceNodeWithNodes(sourceFile, oldExportDecls[0], [.. newExportDecls], options);

                if (oldExportDecls.Length > 1)
                {
                    for (int i = 1; i < oldExportDecls.Length; i++)
                    {
                        changeTracker.Delete(sourceFile, oldExportDecls[i]);
                    }
                }
            }
        }
    }

    // organizeimports.go:830-924 — coalesceExportsWorker.
    private static List<Statement> CoalesceExportsWorker(
        Statement[] exportGroup,
        Func<Node, Node, int> specifierComparer,
        Func<string, string, int> moduleSpecifierComparer,
        SourceFile? sourceFile,
        Tracker changeTracker)
    {
        if (exportGroup.Length == 0)
        {
            return [.. exportGroup];
        }

        var exportsByModuleSpecifier = new Dictionary<string, List<Statement>>(StringComparer.Ordinal);
        var moduleSpecifierOrder = new List<string>();

        foreach (var exportDecl in exportGroup)
        {
            var export = exportDecl.AsExportDeclaration();
            string moduleSpecifier = "";
            if (export.ModuleSpecifier != null)
            {
                moduleSpecifier = export.ModuleSpecifier.Text();
            }
            if (!exportsByModuleSpecifier.TryGetValue(moduleSpecifier, out var bucket))
            {
                moduleSpecifierOrder.Add(moduleSpecifier);
                bucket = [];
                exportsByModuleSpecifier[moduleSpecifier] = bucket;
            }
            bucket.Add(exportDecl);
        }

        moduleSpecifierOrder = moduleSpecifierOrder.OrderBy(static s => s, Comparer<string>.Create((a, b) =>
        {
            if (a == "" && b != "")
            {
                return 1;
            }
            if (a != "" && b == "")
            {
                return -1;
            }
            return moduleSpecifierComparer(a, b);
        })).ToList();

        var coalescedExports = new List<Statement>();
        var factory = new NodeFactory(new NodeFactoryHooks());

        foreach (var moduleSpecifier in moduleSpecifierOrder)
        {
            var group = exportsByModuleSpecifier[moduleSpecifier];

            var categorized = GetCategorizedExports(group);

            if (categorized.exportWithoutClause != null)
            {
                coalescedExports.Add(categorized.exportWithoutClause);
            }

            foreach (var subGroup in new[] { categorized.namedExports, categorized.typeOnlyExports })
            {
                if (subGroup.Count == 0)
                {
                    continue;
                }

                var newExportSpecifiers = new List<Node>();
                foreach (var exportDeclNode in subGroup)
                {
                    var exportClause = exportDeclNode.AsExportDeclaration().ExportClause;
                    if (exportClause != null && exportClause.Kind == SyntaxKind.NamedExports)
                    {
                        var namedExports = exportClause.AsNamedExports();
                        newExportSpecifiers.AddRange(namedExports.Elements!.Nodes);
                    }
                }

                newExportSpecifiers = SortStable(newExportSpecifiers, specifierComparer);

                var exportDecl = subGroup[0].AsExportDeclaration();

                NamedExportBindings? updatedExportClause = null;
                if (exportDecl.ExportClause != null)
                {
                    if (exportDecl.ExportClause.Kind == SyntaxKind.NamedExports)
                    {
                        var namedExports = exportDecl.ExportClause.AsNamedExports();
                        var sortedList = factory.NewNodeList([.. newExportSpecifiers]);
                        updatedExportClause = factory.UpdateNamedExports(namedExports, sortedList);

                        if (sourceFile != null && !AstUtilities.NodeIsSynthesized(namedExports) && !global::Ts.Checker.CheckerFindRefsLsExports.RangeIsOnSingleLine(namedExports.Loc, sourceFile))
                        {
                            changeTracker.EmitContext.SetEmitFlags(updatedExportClause, global::Ts.Printer.EmitFlags.MultiLine);
                        }
                    }
                    else
                    {
                        updatedExportClause = exportDecl.ExportClause;
                    }
                }

                var newExportDecl = factory.UpdateExportDeclaration(
                    exportDecl,
                    exportDecl.Modifiers(),
                    exportDecl.IsTypeOnly,
                    updatedExportClause,
                    exportDecl.ModuleSpecifier,
                    exportDecl.Attributes);
                coalescedExports.Add(newExportDecl);
            }
        }

        return coalescedExports;
    }

    // organizeimports.go:926-930 — categorizedExports.
    private sealed class CategorizedExports
    {
        internal Statement? exportWithoutClause;
        internal List<Statement> namedExports = [];
        internal List<Statement> typeOnlyExports = [];
    }

    // organizeimports.go:932-954 — getCategorizedExports.
    private static CategorizedExports GetCategorizedExports(List<Statement> exportGroup)
    {
        var result = new CategorizedExports();

        foreach (var exportDecl in exportGroup)
        {
            var export = exportDecl.AsExportDeclaration();
            if (export.ExportClause == null)
            {
                if (result.exportWithoutClause == null)
                {
                    result.exportWithoutClause = exportDecl;
                }
            }
            else if (export.IsTypeOnly)
            {
                result.typeOnlyExports.Add(exportDecl);
            }
            else
            {
                result.namedExports.Add(exportDecl);
            }
        }

        return result;
    }
}
