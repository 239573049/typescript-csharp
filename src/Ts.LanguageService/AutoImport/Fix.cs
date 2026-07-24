// Ported from tsgo internal/ls/autoimport/fix.go (whole file): auto-import fix construction
// and application (Edits), plus the View methods declared in fix.go (GetFixes,
// tryUseExistingNamespaceImport, tryAddToExistingImport, getExistingImports,
// shouldUseRequire and the fix comparers).
//
// PORT-NOTE(view): the View struct fields fix.go relies on (view.go:21-33) are declared in the
// partial class at the bottom of this file; the registry/projectKey/conditions/allowedEndings
// members and NewView land with the registry/view wave, as does GetModuleSpecifier
// (specifiers.go — it reads registry entrypoints and the specifier cache), which panics until
// then. locale.FromContext(ctx) collapses to Locale.Default until the locale carrier lands.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.Diagnostics;

namespace Ts.Ls.AutoImport;

// fix.go:30-35 — newImportBinding.
internal sealed class NewImportBinding
{
    internal global::Ts.LsProto.ImportKind kind;
    internal string propertyName = "";
    internal string name = "";
    internal global::Ts.LsProto.AddAsTypeOnly addAsTypeOnly;
}

// fix.go:37-44 — Fix. (Go embeds *lsproto.AutoImportFix; the embedded fields are promoted via
// pass-through properties to keep call sites 1:1.)
public sealed class Fix
{
    public global::Ts.LsProto.AutoImportFix AutoImportFix = null!;

    public global::Ts.ModuleSpecifiers.ResultKind ModuleSpecifierKind;
    public bool IsReExport;
    public string ModuleFileName = "";
    public Declaration? TypeOnlyAliasDeclaration;

    // Promoted embedded fields.
    public global::Ts.LsProto.AutoImportFixKind Kind => AutoImportFix.Kind;
    public string Name => AutoImportFix.Name;
    public global::Ts.LsProto.ImportKind ImportKind => AutoImportFix.ImportKind;
    public bool UseRequire => AutoImportFix.UseRequire;
    public global::Ts.LsProto.AddAsTypeOnly AddAsTypeOnly => AutoImportFix.AddAsTypeOnly;
    public string ModuleSpecifier => AutoImportFix.ModuleSpecifier;
    public int ImportIndex => AutoImportFix.ImportIndex;
    public global::Ts.LsProto.Position? UsagePosition => AutoImportFix.UsagePosition;
    public string NamespacePrefix => AutoImportFix.NamespacePrefix;

    // fix.go:53-119 — Edits.
    public (global::Ts.LsProto.TextEdit[]? Edits, string Description) Edits(
        SourceFile file,
        CompilerOptions compilerOptions,
        global::Ts.Format.FormatCodeSettings formatOptions,
        global::Ts.Ls.LsConv.Converters converters,
        UserPreferences preferences)
    {
        var locale = global::Ts.Locale.Locale.Default;
        var tracker = global::Ts.Ls.Change.Tracker.NewTracker(compilerOptions, formatOptions, converters);
        switch (Kind)
        {
            case global::Ts.LsProto.AutoImportFixKind.UseNamespace:
            {
                var description = AutoImportApi.AddNamespaceQualifier(this, tracker, file, locale);
                return (GetFileChanges(tracker, file), description);
            }
            case global::Ts.LsProto.AutoImportFixKind.AddToExisting:
            {
                if ((file.Imports() ?? []).Length <= ImportIndex)
                {
                    throw new InvalidOperationException("import index out of range");
                }
                var existingFix = AutoImportApi.GetAddToExistingImportFix(file, this);
                AutoImportApi.AddToExistingImport(tracker, file, existingFix.importClauseOrBindingPattern, existingFix.defaultImport, CoreUtil.SingleElementSlice(existingFix.namedImport), preferences);
                return (GetFileChanges(tracker, file), global::Ts.Diagnostics.Diagnostics.Update_import_from_0.Localize(locale, ModuleSpecifier));
            }
            case global::Ts.LsProto.AutoImportFixKind.AddNew:
            {
                Statement[] declarations;
                var defaultImport = ImportKind == global::Ts.LsProto.ImportKind.Default ? new NewImportBinding { name = Name, addAsTypeOnly = AddAsTypeOnly } : null;
                var namedImports = ImportKind == global::Ts.LsProto.ImportKind.Named ? new NewImportBinding[] { new() { name = Name, addAsTypeOnly = AddAsTypeOnly } } : null;
                NewImportBinding? namespaceLikeImport = null;
                // qualification := f.qualification()
                if (ImportKind == global::Ts.LsProto.ImportKind.Namespace || ImportKind == global::Ts.LsProto.ImportKind.CommonJS)
                {
                    namespaceLikeImport = new NewImportBinding { kind = ImportKind, name = Name };
                    // if qualification != nil && qualification.namespacePref != "" {
                    // 	namespaceLikeImport.name = qualification.namespacePref
                    // }
                }

                var quotePreference = LsDeps.GetQuotePreference(file, preferences);
                if (UseRequire)
                {
                    declarations = AutoImportApi.GetNewRequires(tracker, ModuleSpecifier, quotePreference, defaultImport, namedImports, namespaceLikeImport, compilerOptions);
                }
                else
                {
                    declarations = AutoImportApi.GetNewImports(tracker, ModuleSpecifier, quotePreference, defaultImport, namedImports, namespaceLikeImport, compilerOptions, preferences);
                }

                AutoImportApi.InsertImports(
                    tracker,
                    file,
                    declarations,
                    /*blankLineBetween*/ true,
                    preferences);
                // if qualification != nil {
                // 	addNamespaceQualifier(tracker, file, qualification)
                // }
                return (GetFileChanges(tracker, file), global::Ts.Diagnostics.Diagnostics.Add_import_from_0.Localize(locale, ModuleSpecifier));
            }
            case global::Ts.LsProto.AutoImportFixKind.PromoteTypeOnly:
            {
                var promotedDeclaration = AutoImportApi.PromoteFromTypeOnly(tracker, TypeOnlyAliasDeclaration!, compilerOptions, file, preferences);
                if (promotedDeclaration.Kind == SyntaxKind.ImportSpecifier)
                {
                    var moduleSpecText = AutoImportApi.GetModuleSpecifierText(promotedDeclaration.Parent!.Parent!);
                    return (GetFileChanges(tracker, file), global::Ts.Diagnostics.Diagnostics.Remove_type_from_import_of_0_from_1.Localize(locale, Name, moduleSpecText));
                }
                var moduleSpec = AutoImportApi.GetModuleSpecifierText(promotedDeclaration);
                return (GetFileChanges(tracker, file), global::Ts.Diagnostics.Diagnostics.Remove_type_from_import_declaration_from_0.Localize(locale, moduleSpec));
            }
            case global::Ts.LsProto.AutoImportFixKind.JsdocTypeImport:
            {
                var description = AutoImportApi.AddImportType(this, file, preferences, tracker, locale);
                return (GetFileChanges(tracker, file), description);
            }
            default:
                throw new InvalidOperationException("unimplemented fix edit");
        }
    }

    private static global::Ts.LsProto.TextEdit[]? GetFileChanges(global::Ts.Ls.Change.Tracker tracker, SourceFile file)
    {
        tracker.GetChanges().TryGetValue(file.FileName(), out var edits);
        return edits?.ToArray();
    }
}

// fix.go:46-51 — addToExistingImportFix.
internal sealed class AddToExistingImportFix
{
    internal Node importClauseOrBindingPattern = null!;
    // One of `defaultImport` or `namedImports` will be present
    internal NewImportBinding? defaultImport;
    internal NewImportBinding? namedImport;
}

public static partial class AutoImportApi
{
    // fix.go:121-133 — addImportType.
    internal static string AddImportType(Fix f, SourceFile file, UserPreferences preferences, global::Ts.Ls.Change.Tracker tracker, global::Ts.Locale.Locale locale)
    {
        if (f.UsagePosition == null)
        {
            throw new InvalidOperationException("UsagePosition must be set for JSDoc type import fix");
        }
        var quotePreference = LsDeps.GetQuotePreference(file, preferences);
        var quoteChar = "\"";
        if (quotePreference == QuotePreference.Single)
        {
            quoteChar = "'";
        }
        var importTypePrefix = $"import({quoteChar}{f.ModuleSpecifier}{quoteChar}).";
        tracker.InsertText(file, f.UsagePosition, importTypePrefix);
        return global::Ts.Diagnostics.Diagnostics.Change_0_to_1.Localize(locale, f.Name, importTypePrefix + f.Name);
    }

    // fix.go:135-142 — addNamespaceQualifier.
    internal static string AddNamespaceQualifier(Fix f, global::Ts.Ls.Change.Tracker tracker, SourceFile file, global::Ts.Locale.Locale locale)
    {
        if (f.UsagePosition == null || f.NamespacePrefix.Length == 0)
        {
            throw new InvalidOperationException("namespace fix requires usage position and prefix");
        }
        var qualified = $"{f.NamespacePrefix}.{f.Name}";
        tracker.InsertText(file, f.UsagePosition, f.NamespacePrefix + ".");
        return global::Ts.Diagnostics.Diagnostics.Change_0_to_1.Localize(locale, f.Name, qualified);
    }

    // fix.go:144-179 — getAddToExistingImportFix.
    internal static AddToExistingImportFix GetAddToExistingImportFix(SourceFile file, Fix fix)
    {
        if (fix.Kind != global::Ts.LsProto.AutoImportFixKind.AddToExisting)
        {
            throw new InvalidOperationException("expected add to existing import fix");
        }
        var moduleSpecifier = file.Imports()![fix.ImportIndex];
        var importNode = AstUtilities.TryGetImportFromModuleSpecifier(moduleSpecifier);
        if (importNode == null)
        {
            throw new InvalidOperationException("expected import declaration");
        }
        Node? importClauseOrBindingPattern;
        switch (importNode.Kind)
        {
            case SyntaxKind.ImportDeclaration:
                importClauseOrBindingPattern = importNode.ImportClause();
                if (importClauseOrBindingPattern == null)
                {
                    throw new InvalidOperationException("expected import clause");
                }
                break;
            case SyntaxKind.CallExpression:
                if (!AstUtilities.IsVariableDeclarationInitializedToRequire(importNode.Parent!))
                {
                    throw new InvalidOperationException("expected require call expression to be in variable declaration");
                }
                importClauseOrBindingPattern = importNode.Parent!.Name();
                if (importClauseOrBindingPattern == null || !NodeTests.IsObjectBindingPattern(importClauseOrBindingPattern))
                {
                    throw new InvalidOperationException("expected object binding pattern in variable declaration");
                }
                break;
            default:
                throw new InvalidOperationException("expected import declaration or require call expression");
        }

        var defaultImport = fix.ImportKind == global::Ts.LsProto.ImportKind.Default ? new NewImportBinding { kind = global::Ts.LsProto.ImportKind.Default, name = fix.Name, addAsTypeOnly = fix.AddAsTypeOnly } : null;
        var namedImports = fix.ImportKind == global::Ts.LsProto.ImportKind.Named ? new NewImportBinding { kind = global::Ts.LsProto.ImportKind.Named, name = fix.Name, addAsTypeOnly = fix.AddAsTypeOnly } : null;
        return new AddToExistingImportFix
        {
            importClauseOrBindingPattern = importClauseOrBindingPattern,
            defaultImport = defaultImport,
            namedImport = namedImports,
        };
    }

    // fix.go:181-302 — addToExistingImport.
    internal static void AddToExistingImport(
        global::Ts.Ls.Change.Tracker ct,
        SourceFile file,
        Node importClauseOrBindingPattern,
        NewImportBinding? defaultImport,
        NewImportBinding[]? namedImports,
        UserPreferences preferences)
    {
        switch (importClauseOrBindingPattern.Kind)
        {
            case SyntaxKind.ObjectBindingPattern:
            {
                var bindingPattern = importClauseOrBindingPattern.AsBindingPattern();
                if (defaultImport != null)
                {
                    AddElementToBindingPattern(ct, file, bindingPattern, defaultImport.name, "default");
                }
                foreach (var namedImport in namedImports ?? [])
                {
                    AddElementToBindingPattern(ct, file, bindingPattern, namedImport.name, "");
                }
                return;
            }
            case SyntaxKind.ImportClause:
            {
                var importClause = importClauseOrBindingPattern.AsImportClause();

                // promoteFromTypeOnly = true if we need to promote the entire original clause from type only
                var allBindings = new List<NewImportBinding?>(namedImports ?? []) { defaultImport };
                var promoteFromTypeOnly = importClause.IsTypeOnly() && allBindings.Exists(i =>
                {
                    if (i == null)
                    {
                        return false;
                    }
                    return i.addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.NotAllowed;
                });

                Node[]? existingSpecifiers = null;
                if (importClause.NamedBindings != null && importClause.NamedBindings.Kind == SyntaxKind.NamedImports)
                {
                    existingSpecifiers = importClause.NamedBindings.Elements();
                }

                if (defaultImport != null)
                {
                    Debug.Assert(importClause.Name() == null, "Cannot add a default import to an import clause that already has one");
                    ct.InsertNodeAt(file, global::Ts.AstNav.Tokens.GetStartOfNode(importClause, file, false), ct.NodeFactory.NewIdentifier(defaultImport.name), new global::Ts.Ls.Change.NodeOptions { Suffix = ", " });
                }

                if ((namedImports ?? []).Length > 0)
                {
                    var (specifierComparer, isSorted) = OrganizeImports.GetNamedImportSpecifierComparerWithDetection(importClause.Parent!, file, preferences);
                    var newSpecifiers = CoreUtil.Map(namedImports, namedImport =>
                    {
                        Node? identifier = null;
                        if (namedImport.propertyName.Length != 0)
                        {
                            identifier = ct.NodeFactory.NewIdentifier(namedImport.propertyName);
                        }
                        return ct.NodeFactory.NewImportSpecifier(
                            (!importClause.IsTypeOnly() || promoteFromTypeOnly) && ShouldUseTypeOnly(namedImport.addAsTypeOnly, preferences),
                            identifier,
                            ct.NodeFactory.NewIdentifier(namedImport.name));
                    })!;
                    Array.Sort(newSpecifiers, (a, b) => specifierComparer(a, b));
                    if ((existingSpecifiers ?? []).Length > 0 && isSorted != Tristate.False)
                    {
                        // The sorting preference computed earlier may or may not have validated that these particular
                        // import specifiers are sorted. If they aren't, `getImportSpecifierInsertionIndex` will return
                        // nonsense. So if there are existing specifiers, even if we know the sorting preference, we
                        // need to ensure that the existing specifiers are sorted according to the preference in order
                        // to do a sorted insertion.

                        // If we're promoting the clause from type-only, we need to transform the existing imports
                        // before attempting to insert the new named imports (for comparison purposes only)
                        var specsToCompareAgainst = existingSpecifiers!;
                        if (promoteFromTypeOnly && existingSpecifiers!.Length > 0)
                        {
                            specsToCompareAgainst = CoreUtil.Map(existingSpecifiers, e =>
                            {
                                var spec = e.AsImportSpecifier();
                                Node? propertyName = null;
                                if (spec.PropertyName != null)
                                {
                                    propertyName = spec.PropertyName;
                                }
                                var syntheticSpec = ct.NodeFactory.NewImportSpecifier(
                                    true, // isTypeOnly
                                    propertyName,
                                    spec.Name());
                                return syntheticSpec;
                            })!;
                        }

                        foreach (var spec in newSpecifiers)
                        {
                            var insertionIndex = OrganizeImports.GetImportSpecifierInsertionIndex(specsToCompareAgainst, spec, specifierComparer);
                            ct.InsertImportSpecifierAtIndex(file, spec, importClause.NamedBindings!, insertionIndex);
                        }
                    }
                    else if ((existingSpecifiers ?? []).Length > 0)
                    {
                        foreach (var spec in newSpecifiers)
                        {
                            ct.InsertNodeInListAfter(file, existingSpecifiers![^1], spec, null);
                        }
                    }
                    else
                    {
                        if (newSpecifiers.Length > 0)
                        {
                            var newNamedImports = ct.NodeFactory.NewNamedImports(ct.NodeFactory.NewNodeList(newSpecifiers));
                            if (importClause.NamedBindings != null)
                            {
                                ct.ReplaceNode(file, importClause.NamedBindings, newNamedImports, null);
                            }
                            else
                            {
                                if (importClause.Name() == null)
                                {
                                    throw new InvalidOperationException("Import clause must have either named imports or a default import");
                                }
                                ct.InsertNodeAfter(file, importClause.Name()!, newNamedImports);
                            }
                        }
                    }
                }

                if (promoteFromTypeOnly)
                {
                    // Delete the 'type' keyword from the import clause
                    var typeKeyword = GetTypeKeywordOfTypeOnlyImport(importClause, file);
                    ct.Delete(file, typeKeyword);

                    // Add 'type' modifier to existing specifiers (not newly added ones)
                    // We preserve the type-onlyness of existing specifiers regardless of whether
                    // it would make a difference in emit (user preference).
                    if ((existingSpecifiers ?? []).Length > 0)
                    {
                        foreach (var specifier in existingSpecifiers!)
                        {
                            if (!specifier.AsImportSpecifier().IsTypeOnly)
                            {
                                ct.InsertModifierBefore(file, SyntaxKind.TypeKeyword, specifier);
                            }
                        }
                    }
                }
                break;
            }
            default:
                throw new InvalidOperationException("Unsupported clause kind: " + importClauseOrBindingPattern.Kind + " for addToExistingImport");
        }
    }

    // fix.go:304-312 — getTypeKeywordOfTypeOnlyImport.
    internal static Node GetTypeKeywordOfTypeOnlyImport(ImportClause importClause, SourceFile sourceFile)
    {
        Debug.Assert(importClause.IsTypeOnly(), "import clause must be type-only");
        // The first child of a type-only import clause is the 'type' keyword
        // import type { foo } from './bar'
        //        ^^^^
        var typeKeyword = global::Ts.AstNav.Tokens.FindChildOfKind(importClause, SyntaxKind.TypeKeyword, sourceFile);
        Debug.Assert(typeKeyword != null, "type-only import clause should have a type keyword");
        return typeKeyword!;
    }

    // fix.go:314-327 — addElementToBindingPattern.
    internal static void AddElementToBindingPattern(
        global::Ts.Ls.Change.Tracker ct,
        SourceFile file,
        BindingPattern bindingPattern,
        string name,
        string propertyName)
    {
        var element = ct.NodeFactory.NewBindingElement(null, null, ct.NodeFactory.NewIdentifier(name), propertyName.Length == 0 ? null : ct.NodeFactory.NewIdentifier(propertyName));
        if (bindingPattern.Elements!.Nodes.Length > 0)
        {
            ct.InsertNodeInListAfter(file, bindingPattern.Elements.Nodes[^1], element, bindingPattern.Elements);
        }
        else
        {
            ct.ReplaceNode(file, bindingPattern, ct.NodeFactory.NewBindingPattern(SyntaxKind.ObjectBindingPattern, ct.NodeFactory.NewNodeList([element])), null);
        }
    }

    // fix.go:329-396 — getNewImports.
    internal static Statement[] GetNewImports(
        global::Ts.Ls.Change.Tracker ct,
        string moduleSpecifier,
        QuotePreference quotePreference,
        NewImportBinding? defaultImport,
        NewImportBinding[]? namedImports,
        NewImportBinding? namespaceLikeImport, // { lsproto.importKind: lsproto.ImportKind.CommonJS | lsproto.ImportKind.Namespace; }
        CompilerOptions compilerOptions,
        UserPreferences preferences)
    {
        var tokenFlags = quotePreference == QuotePreference.Single ? TokenFlags.SingleQuote : TokenFlags.None;
        var moduleSpecifierStringLiteral = ct.NodeFactory.NewStringLiteral(moduleSpecifier, tokenFlags);
        var statements = new List<Statement>();
        if (defaultImport != null || (namedImports ?? []).Length > 0)
        {
            // `verbatimModuleSyntax` should prefer top-level `import type` -
            // even though it's not an error, it would add unnecessary runtime emit.
            var topLevelTypeOnly = (defaultImport == null || NeedsTypeOnly(defaultImport.addAsTypeOnly)) &&
                CoreUtil.Every(namedImports ?? [], i => NeedsTypeOnly(i.addAsTypeOnly)) ||
                (compilerOptions.VerbatimModuleSyntax.IsTrue() || preferences.PreferTypeOnlyAutoImports.IsTrue()) &&
                    (defaultImport == null || defaultImport.addAsTypeOnly != global::Ts.LsProto.AddAsTypeOnly.NotAllowed) &&
                    !CoreUtil.Some(namedImports ?? [], i => i.addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.NotAllowed);

            Node? defaultImportNode = null;
            if (defaultImport != null)
            {
                defaultImportNode = ct.NodeFactory.NewIdentifier(defaultImport.name);
            }

            statements.Add(MakeImport(ct, defaultImportNode, CoreUtil.Map(namedImports ?? [], namedImport =>
            {
                Node? namedImportPropertyName = null;
                if (namedImport.propertyName.Length != 0)
                {
                    namedImportPropertyName = ct.NodeFactory.NewIdentifier(namedImport.propertyName);
                }
                return ct.NodeFactory.NewImportSpecifier(
                    !topLevelTypeOnly && ShouldUseTypeOnly(namedImport.addAsTypeOnly, preferences),
                    namedImportPropertyName,
                    ct.NodeFactory.NewIdentifier(namedImport.name));
            })!, moduleSpecifierStringLiteral, topLevelTypeOnly));
        }

        if (namespaceLikeImport != null)
        {
            Statement declaration;
            if (namespaceLikeImport.kind == global::Ts.LsProto.ImportKind.CommonJS)
            {
                declaration = ct.NodeFactory.NewImportEqualsDeclaration(
                    /*modifiers*/ null,
                    ShouldUseTypeOnly(namespaceLikeImport.addAsTypeOnly, preferences),
                    ct.NodeFactory.NewIdentifier(namespaceLikeImport.name),
                    ct.NodeFactory.NewExternalModuleReference(moduleSpecifierStringLiteral));
            }
            else
            {
                declaration = ct.NodeFactory.NewImportDeclaration(
                    /*modifiers*/ null,
                    ct.NodeFactory.NewImportClause(
                        /*phaseModifier*/ ShouldUseTypeOnly(namespaceLikeImport.addAsTypeOnly, preferences) ? SyntaxKind.TypeKeyword : SyntaxKind.Unknown,
                        /*name*/ null,
                        ct.NodeFactory.NewNamespaceImport(ct.NodeFactory.NewIdentifier(namespaceLikeImport.name))),
                    moduleSpecifierStringLiteral,
                    /*attributes*/ null);
            }
            statements.Add(declaration);
        }
        if (statements.Count == 0)
        {
            throw new InvalidOperationException("No statements to insert for new imports");
        }
        return statements.ToArray();
    }

    // fix.go:398-461 — getNewRequires.
    internal static Statement[] GetNewRequires(
        global::Ts.Ls.Change.Tracker changeTracker,
        string moduleSpecifier,
        QuotePreference quotePreference,
        NewImportBinding? defaultImport,
        NewImportBinding[]? namedImports,
        NewImportBinding? namespaceLikeImport,
        CompilerOptions compilerOptions)
    {
        var quotedModuleSpecifier = changeTracker.NodeFactory.NewStringLiteral(
            moduleSpecifier,
            quotePreference == QuotePreference.Single ? TokenFlags.SingleQuote : TokenFlags.None);
        var statements = new List<Statement>();

        // const { default: foo, bar, etc } = require('./mod');
        if (defaultImport != null || (namedImports ?? []).Length > 0)
        {
            var bindingElements = new List<Node>();
            foreach (var namedImport in namedImports ?? [])
            {
                Node? propertyName = null;
                if (namedImport.propertyName.Length != 0)
                {
                    propertyName = changeTracker.NodeFactory.NewIdentifier(namedImport.propertyName);
                }
                bindingElements.Add(changeTracker.NodeFactory.NewBindingElement(
                    /*dotDotDotToken*/ null,
                    propertyName,
                    changeTracker.NodeFactory.NewIdentifier(namedImport.name),
                    /*initializer*/ null));
            }
            if (defaultImport != null)
            {
                bindingElements.Insert(0, changeTracker.NodeFactory.NewBindingElement(
                    /*dotDotDotToken*/ null,
                    changeTracker.NodeFactory.NewIdentifier("default"),
                    changeTracker.NodeFactory.NewIdentifier(defaultImport.name),
                    /*initializer*/ null));
            }
            var declaration = CreateConstEqualsRequireDeclaration(
                changeTracker,
                changeTracker.NodeFactory.NewBindingPattern(
                    SyntaxKind.ObjectBindingPattern,
                    changeTracker.NodeFactory.NewNodeList(bindingElements.ToArray())),
                quotedModuleSpecifier);
            statements.Add(declaration);
        }

        // const foo = require('./mod');
        if (namespaceLikeImport != null)
        {
            var declaration = CreateConstEqualsRequireDeclaration(
                changeTracker,
                changeTracker.NodeFactory.NewIdentifier(namespaceLikeImport.name),
                quotedModuleSpecifier);
            statements.Add(declaration);
        }

        Debug.Assert(statements.Count > 0);
        return statements.ToArray();
    }

    // fix.go:463-484 — createConstEqualsRequireDeclaration.
    internal static Statement CreateConstEqualsRequireDeclaration(global::Ts.Ls.Change.Tracker changeTracker, Node name, Node quotedModuleSpecifier)
    {
        return changeTracker.NodeFactory.NewVariableStatement(
            /*modifiers*/ null,
            changeTracker.NodeFactory.NewVariableDeclarationList(
                changeTracker.NodeFactory.NewNodeList(
                [
                    changeTracker.NodeFactory.NewVariableDeclaration(
                        name,
                        /*exclamationToken*/ null,
                        /*type*/ null,
                        changeTracker.NodeFactory.NewCallExpression(
                            changeTracker.NodeFactory.NewIdentifier("require"),
                            /*questionDotToken*/ null,
                            /*typeArguments*/ null,
                            changeTracker.NodeFactory.NewNodeList([quotedModuleSpecifier]),
                            NodeFlags.None)),
                ]),
                NodeFlags.Const));
    }

    // fix.go:486-523 — insertImports.
    internal static void InsertImports(global::Ts.Ls.Change.Tracker ct, SourceFile sourceFile, Statement[] imports, bool blankLineBetween, UserPreferences preferences)
    {
        Statement[] existingImportStatements;

        if (imports[0].Kind == SyntaxKind.VariableStatement)
        {
            existingImportStatements = CoreUtil.Filter(sourceFile.Statements!.Nodes, AstUtilities.IsRequireVariableStatement) ?? [];
        }
        else
        {
            existingImportStatements = CoreUtil.Filter(sourceFile.Statements!.Nodes, AstUtilities.IsAnyImportSyntax) ?? [];
        }
        var (comparer, isSorted) = OrganizeImports.GetOrganizeImportsStringComparerWithDetection(existingImportStatements, preferences);
        var sortedNewImports = (Statement[])imports.Clone();
        Array.Sort(sortedNewImports, (a, b) => OrganizeImports.CompareImportsOrRequireStatements(a, b, comparer));

        if (existingImportStatements.Length > 0 && isSorted)
        {
            // Existing imports are sorted, insert each new import at the correct position
            foreach (var newImport in sortedNewImports)
            {
                var insertionIndex = OrganizeImports.GetImportDeclarationInsertIndex(existingImportStatements, newImport, (a, b) => OrganizeImports.CompareImportsOrRequireStatements(a, b, comparer));
                if (insertionIndex == 0)
                {
                    // If the first import is top-of-file, insert after the leading comment which is likely the header.
                    var leadingTriviaOption = global::Ts.Ls.Change.LeadingTriviaOption.None;
                    if (existingImportStatements[0] == sourceFile.Statements.Nodes[0])
                    {
                        leadingTriviaOption = global::Ts.Ls.Change.LeadingTriviaOption.Exclude;
                    }
                    ct.InsertNodeBefore(sourceFile, existingImportStatements[0], newImport, false /*blankLineBetween*/, leadingTriviaOption);
                }
                else
                {
                    var prevImport = existingImportStatements[insertionIndex - 1];
                    ct.InsertNodeAfter(sourceFile, prevImport, newImport);
                }
            }
        }
        else if (existingImportStatements.Length > 0)
        {
            ct.InsertNodesAfter(sourceFile, existingImportStatements[^1], sortedNewImports);
        }
        else
        {
            ct.InsertAtTopOfFile(sourceFile, sortedNewImports, blankLineBetween);
        }
    }

    // fix.go:525-535 — makeImport.
    internal static Statement MakeImport(global::Ts.Ls.Change.Tracker ct, Node? defaultImport, Node[] namedImports, Expression moduleSpecifier, bool isTypeOnly)
    {
        Node? newNamedImports = null;
        if (namedImports.Length > 0)
        {
            newNamedImports = ct.NodeFactory.NewNamedImports(ct.NodeFactory.NewNodeList(namedImports));
        }
        Node? importClause = null;
        if (defaultImport != null || newNamedImports != null)
        {
            importClause = ct.NodeFactory.NewImportClause(isTypeOnly ? SyntaxKind.TypeKeyword : SyntaxKind.Unknown, defaultImport, newNamedImports);
        }
        return ct.NodeFactory.NewImportDeclaration( /*modifiers*/ null, importClause, moduleSpecifier, null /*attributes*/);
    }

    // fix.go:605-617 — getAddAsTypeOnly determines if an import should be type-only based on
    // usage context.
    internal static global::Ts.LsProto.AddAsTypeOnly GetAddAsTypeOnly(bool isValidTypeOnlyUseSite, Export export, CompilerOptions compilerOptions)
    {
        if (!isValidTypeOnlyUseSite)
        {
            // Can't use a type-only import if the usage is an emitting position
            return global::Ts.LsProto.AddAsTypeOnly.NotAllowed;
        }
        if (compilerOptions.VerbatimModuleSyntax.IsTrue() && (export.IsTypeOnly || (export.Flags & SymbolFlags.Value) == 0) ||
            export.IsTypeOnly && (export.Flags & SymbolFlags.Value) != 0)
        {
            // A type-only import is required for this symbol if under verbatimModuleSyntax and it's purely a type
            return global::Ts.LsProto.AddAsTypeOnly.Required;
        }
        return global::Ts.LsProto.AddAsTypeOnly.Allowed;
    }

    // fix.go:652-671 — getNamespaceLikeImportText.
    internal static string GetNamespaceLikeImportText(Node declaration)
    {
        switch (declaration.Kind)
        {
            case SyntaxKind.VariableDeclaration:
            {
                var name = declaration.Name();
                if (name != null && name.Kind == SyntaxKind.Identifier)
                {
                    return name.Text();
                }
                return "";
            }
            case SyntaxKind.ImportEqualsDeclaration:
                return declaration.Name()!.Text();
            case SyntaxKind.JSDocImportTag:
            case SyntaxKind.ImportDeclaration:
            {
                var importClause = declaration.ImportClause();
                if (importClause != null && importClause.AsImportClause().NamedBindings != null && importClause.AsImportClause().NamedBindings!.Kind == SyntaxKind.NamespaceImport)
                {
                    return importClause.AsImportClause().NamedBindings!.Name()!.Text();
                }
                return "";
            }
            default:
                return "";
        }
    }

    // fix.go:780-818 — getImportKind.
    internal static global::Ts.LsProto.ImportKind GetImportKind(SourceFile importingFile, Export export, global::Ts.Compiler.Program program)
    {
        if (program.Options().VerbatimModuleSyntax.IsTrue() && program.GetEmitModuleFormatOfFile(importingFile) == ModuleKind.CommonJS)
        {
            return global::Ts.LsProto.ImportKind.CommonJS;
        }
        switch (export.Syntax)
        {
            case ExportSyntax.DefaultModifier:
            case ExportSyntax.DefaultDeclaration:
                return global::Ts.LsProto.ImportKind.Default;
            case ExportSyntax.Named:
                if (export.ExportName == InternalSymbolName.Default)
                {
                    return global::Ts.LsProto.ImportKind.Default;
                }
                goto case ExportSyntax.Modifier;
            case ExportSyntax.Modifier:
            case ExportSyntax.Star:
            case ExportSyntax.CommonJSExportsProperty:
                return global::Ts.LsProto.ImportKind.Named;
            case ExportSyntax.Equals:
            case ExportSyntax.CommonJSModuleExports:
            case ExportSyntax.UMD:
                // export.Syntax will be ExportSyntaxEquals for named exports/properties of an export='s target.
                if (export.ExportName != InternalSymbolName.ExportEquals)
                {
                    return global::Ts.LsProto.ImportKind.Named;
                }
                // !!! cache this?
                foreach (var statement in importingFile.Statements!.Nodes)
                {
                    // `import foo` parses as an ImportEqualsDeclaration even though it could be an ImportDeclaration
                    if (NodeTests.IsImportEqualsDeclaration(statement) && !AstUtilities.NodeIsMissing(statement.AsImportEqualsDeclaration().ModuleReference))
                    {
                        return global::Ts.LsProto.ImportKind.CommonJS;
                    }
                }
                // !!! this logic feels weird; we're basically trying to predict if shouldUseRequire is going to
                //     be true. The meaning of "default import" is different depending on whether we write it as
                //     a require or an es6 import. The latter, compiled to CJS, has interop built in that will
                //     avoid accessing .default, but if we write a require directly and call it a default import,
                //     we emit an unconditional .default access.
                if (importingFile.ExternalModuleIndicator != null || !AstUtilities.IsSourceFileJS(importingFile))
                {
                    return global::Ts.LsProto.ImportKind.Default;
                }
                return global::Ts.LsProto.ImportKind.CommonJS;
            default:
                throw new InvalidOperationException("unhandled export syntax kind: " + export.Syntax.String());
        }
    }

    // fix.go:866-873 — fileSyntaxKind represents the detected module syntax of a source file.
    internal enum FileSyntaxKind
    {
        Ambiguous = 0,
        ESM,
        CJS,
    }

    // fix.go:875-889 — detectSyntax returns whether a source file has unambiguous ESM or CJS
    // syntax. When moduleDetection is "force", ExternalModuleIndicator may be set to the
    // source file node itself rather than a genuine syntax indicator, so we fall back
    // to inspecting the file's Imports() to find actual import/export declarations.
    internal static FileSyntaxKind DetectSyntax(SourceFile file, CompilerOptions options)
    {
        var (hasESM, hasCJS) = DetectSyntaxIndicators(file, options);
        if (hasCJS && !hasESM)
        {
            return FileSyntaxKind.CJS;
        }
        if (hasESM && !hasCJS)
        {
            return FileSyntaxKind.ESM;
        }
        return FileSyntaxKind.Ambiguous;
    }

    // fix.go:891-927 — detectSyntaxIndicators checks whether a source file contains genuine ESM
    // and/or CJS syntax. Under moduleDetection "force", the cached
    // ExternalModuleIndicator may be the source file itself rather than a real
    // statement, so we look at Imports() for actual import/export declarations.
    internal static (bool HasESM, bool HasCJS) DetectSyntaxIndicators(SourceFile file, CompilerOptions options)
    {
        var hasCJS = file.CommonJSModuleIndicator != null;
        bool hasESM;
        if (options.GetEmitModuleDetectionKind() != ModuleDetectionKind.Force)
        {
            // ExternalModuleIndicator is reliable when moduleDetection is not "force"
            hasESM = file.ExternalModuleIndicator != null;
            return (hasESM, hasCJS);
        }
        // Under moduleDetection "force", ExternalModuleIndicator is set to
        // file.AsNode() when there is no genuine ESM syntax, so only trust it
        // when it points to a real statement node.
        if (file.ExternalModuleIndicator != null && file.ExternalModuleIndicator != file)
        {
            return (true, hasCJS);
        }
        // Fall back to scanning Imports() for actual import/export declarations
        // (not require() calls or dynamic imports).
        foreach (var imp in file.Imports() ?? [])
        {
            if ((imp.Flags & NodeFlags.Synthesized) != 0)
            {
                continue;
            }
            var parent = imp.Parent;
            if (parent == null)
            {
                continue;
            }
            switch (parent.Kind)
            {
                case SyntaxKind.ImportDeclaration:
                case SyntaxKind.JSImportDeclaration:
                case SyntaxKind.ExportDeclaration:
                    return (true, hasCJS);
                case SyntaxKind.ExternalModuleReference:
                    // import x = require("...") — this is ESM-ish syntax
                    return (true, hasCJS);
            }
        }
        return (false, hasCJS);
    }

    // fix.go:975-977 — needsTypeOnly.
    internal static bool NeedsTypeOnly(global::Ts.LsProto.AddAsTypeOnly addAsTypeOnly)
    {
        return addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.Required;
    }

    // fix.go:979-981 — shouldUseTypeOnly.
    internal static bool ShouldUseTypeOnly(global::Ts.LsProto.AddAsTypeOnly addAsTypeOnly, UserPreferences preferences)
    {
        return NeedsTypeOnly(addAsTypeOnly) || addAsTypeOnly != global::Ts.LsProto.AddAsTypeOnly.NotAllowed && preferences.PreferTypeOnlyAutoImports.IsTrue();
    }

    // fix.go:1004-1006 — compareFixKinds.
    internal static int CompareFixKinds(global::Ts.LsProto.AutoImportFixKind a, global::Ts.LsProto.AutoImportFixKind b)
    {
        return (int)a - (int)b;
    }

    // fix.go:1071-1082 — isFixPossiblyReExportingImportingFile: a simple heuristic to try to
    // avoid creating an import cycle with a barrel re-export.
    // E.g., do not `import { Foo } from ".."` when you could `import { Foo } from "../Foo"`.
    // This can produce false positives or negatives if re-exports cross into sibling directories
    // (e.g. `export * from "../whatever"`) or are not named "index". Technically this should do
    // a tspath.Path comparison, but it's not worth it to run a heuristic in such a hot path.
    internal static bool IsFixPossiblyReExportingImportingFile(Fix fix, string importingFileName)
    {
        if (fix.IsReExport && IsIndexFileName(fix.ModuleFileName))
        {
            var reExportDir = global::Ts.TsPath.TsPath.GetDirectoryPath(fix.ModuleFileName);
            return importingFileName.StartsWith(reExportDir, StringComparison.Ordinal);
        }
        return false;
    }

    // fix.go:1084-1095 — isIndexFileName.
    internal static bool IsIndexFileName(string fileName)
    {
        var lastSlash = fileName.LastIndexOf('/');
        if (lastSlash < 0 || fileName.Length <= lastSlash + 1)
        {
            return false;
        }
        fileName = fileName[(lastSlash + 1)..];
        switch (fileName)
        {
            case "index.js":
            case "index.jsx":
            case "index.d.ts":
            case "index.ts":
            case "index.tsx":
                return true;
        }
        return false;
    }

    // fix.go:1097-1187 — promoteFromTypeOnly.
    internal static Declaration PromoteFromTypeOnly(
        global::Ts.Ls.Change.Tracker changes,
        Declaration aliasDeclaration,
        CompilerOptions compilerOptions,
        SourceFile sourceFile,
        UserPreferences preferences)
    {
        // See comment in `doAddExistingFix` on constant with the same name.
        var convertExistingToTypeOnly = compilerOptions.VerbatimModuleSyntax;

        switch (aliasDeclaration.Kind)
        {
            case SyntaxKind.ImportSpecifier:
            {
                var spec = aliasDeclaration.AsImportSpecifier();
                if (spec.IsTypeOnly)
                {
                    if (spec.Parent != null && spec.Parent.Kind == SyntaxKind.NamedImports)
                    {
                        var namedImportsNode = spec.Parent.AsNamedImports();
                        var elements = namedImportsNode.Elements!.Nodes;
                        if (elements.Length > 1)
                        {
                            // Create a synthetic specifier with isTypeOnly=false to compute sorted position
                            Node? propertyName = null;
                            if (spec.PropertyName != null)
                            {
                                propertyName = changes.NodeFactory.NewIdentifier(spec.PropertyName.Text());
                            }
                            var newSpecifier = changes.NodeFactory.NewImportSpecifier(
                                false, // isTypeOnly = false
                                propertyName,
                                changes.NodeFactory.NewIdentifier(spec.Name()!.Text()));
                            var (specifierComparer, _) = OrganizeImports.GetNamedImportSpecifierComparerWithDetection(
                                spec.Parent.Parent!.Parent!, // ImportDeclaration
                                sourceFile,
                                preferences);
                            var insertionIndex = OrganizeImports.GetImportSpecifierInsertionIndex(elements, newSpecifier, specifierComparer);
                            var currentIndex = Array.IndexOf(elements, aliasDeclaration);
                            if (insertionIndex != currentIndex)
                            {
                                changes.Delete(sourceFile, aliasDeclaration);
                                changes.InsertImportSpecifierAtIndex(sourceFile, newSpecifier, spec.Parent, insertionIndex);
                                return aliasDeclaration;
                            }
                        }
                        // If no re-sorting needed, just remove the 'type' keyword
                        var firstToken = LsDeps.GetFirstToken(aliasDeclaration, sourceFile);
                        var typeKeywordPos = global::Ts.Scanner.Scanner.GetTokenPosOfNode(firstToken!, sourceFile, false);
                        Node targetNode;
                        if (spec.PropertyName != null)
                        {
                            targetNode = spec.PropertyName;
                        }
                        else
                        {
                            targetNode = spec.Name()!;
                        }
                        var targetPos = global::Ts.Scanner.Scanner.GetTokenPosOfNode(targetNode, sourceFile, false);
                        changes.DeleteRange(sourceFile, new TextRange(typeKeywordPos, targetPos));
                    }
                    return aliasDeclaration;
                }
                else
                {
                    // The parent import clause is type-only
                    if (spec.Parent == null || spec.Parent.Kind != SyntaxKind.NamedImports)
                    {
                        throw new InvalidOperationException("ImportSpecifier parent must be NamedImports");
                    }
                    if (spec.Parent.Parent == null || spec.Parent.Parent.Kind != SyntaxKind.ImportClause)
                    {
                        throw new InvalidOperationException("NamedImports parent must be ImportClause");
                    }
                    PromoteImportClause(changes, spec.Parent.Parent.AsImportClause(), compilerOptions, sourceFile, preferences, convertExistingToTypeOnly, aliasDeclaration);
                    return spec.Parent.Parent;
                }
            }

            case SyntaxKind.ImportClause:
                PromoteImportClause(changes, aliasDeclaration.AsImportClause(), compilerOptions, sourceFile, preferences, convertExistingToTypeOnly, aliasDeclaration);
                return aliasDeclaration;

            case SyntaxKind.NamespaceImport:
                // Promote the parent import clause
                if (aliasDeclaration.Parent == null || aliasDeclaration.Parent.Kind != SyntaxKind.ImportClause)
                {
                    throw new InvalidOperationException("NamespaceImport parent must be ImportClause");
                }
                PromoteImportClause(changes, aliasDeclaration.Parent.AsImportClause(), compilerOptions, sourceFile, preferences, convertExistingToTypeOnly, aliasDeclaration);
                return aliasDeclaration.Parent;

            case SyntaxKind.ImportEqualsDeclaration:
            {
                // Remove the 'type' keyword (which is the second token: 'import' 'type' name '=' ...)
                var importEqDecl = aliasDeclaration.AsImportEqualsDeclaration();
                // The type keyword is after 'import' and before the name
                var scan = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, importEqDecl.Pos);
                // Skip 'import' keyword to get to 'type'
                scan.Scan();
                DeleteTypeKeyword(changes, sourceFile, scan.TokenStart());
                return aliasDeclaration;
            }
            default:
                throw new InvalidOperationException($"Unexpected alias declaration kind: {aliasDeclaration.Kind}");
        }
    }

    // fix.go:1189-1267 — promoteImportClause removes the type keyword from an import clause.
    internal static void PromoteImportClause(
        global::Ts.Ls.Change.Tracker changes,
        ImportClause importClause,
        CompilerOptions compilerOptions,
        SourceFile sourceFile,
        UserPreferences preferences,
        Tristate convertExistingToTypeOnly,
        Declaration? aliasDeclaration)
    {
        // Delete the 'type' keyword
        if (importClause.PhaseModifier == SyntaxKind.TypeKeyword)
        {
            DeleteTypeKeyword(changes, sourceFile, importClause.Pos);
        }

        // Handle .ts extension conversion to .js if necessary
        if (compilerOptions.AllowImportingTsExtensions.IsFalse())
        {
            var moduleSpecifier = global::Ts.Checker.Checker.TryGetModuleSpecifierFromDeclaration(importClause.Parent!);
            if (moduleSpecifier != null)
            {
                // Note: We can't check ResolvedUsingTsExtension without program, so we'll skip this optimization
                // The fix will still work, just might not change .ts to .js extensions in all cases
            }
        }

        // Handle verbatimModuleSyntax conversion
        // If convertExistingToTypeOnly is true, we need to add 'type' to other specifiers
        // in the same import declaration
        if (convertExistingToTypeOnly.IsTrue())
        {
            var namedImports = importClause.NamedBindings;
            if (namedImports != null && namedImports.Kind == SyntaxKind.NamedImports)
            {
                var namedImportsData = namedImports.AsNamedImports();
                if (namedImportsData.Elements!.Nodes.Length > 1)
                {
                    // Check if the list is sorted and if we need to reorder
                    var (_, isSorted) = OrganizeImports.GetNamedImportSpecifierComparerWithDetection(
                        importClause.Parent!,
                        sourceFile,
                        preferences);

                    // If the alias declaration is an ImportSpecifier and the list is sorted,
                    // move it to index 0 (since it will be the only non-type-only import)
                    if (isSorted.IsFalse() == false && // isSorted !== false
                        aliasDeclaration != null &&
                        aliasDeclaration.Kind == SyntaxKind.ImportSpecifier)
                    {
                        // Find the index of the alias declaration
                        var aliasIndex = -1;
                        for (var i = 0; i < namedImportsData.Elements.Nodes.Length; i++)
                        {
                            if (namedImportsData.Elements.Nodes[i] == aliasDeclaration)
                            {
                                aliasIndex = i;
                                break;
                            }
                        }
                        // If not already at index 0, move it there
                        if (aliasIndex > 0)
                        {
                            // Delete the specifier from its current position
                            changes.Delete(sourceFile, aliasDeclaration);
                            // Insert it at index 0
                            changes.InsertImportSpecifierAtIndex(sourceFile, aliasDeclaration, namedImports, 0);
                        }
                    }

                    // Add 'type' keyword to all other import specifiers that aren't already type-only
                    foreach (var element in namedImportsData.Elements.Nodes)
                    {
                        var spec = element.AsImportSpecifier();
                        // Skip the specifier being promoted (if aliasDeclaration is an ImportSpecifier)
                        if (aliasDeclaration != null && aliasDeclaration.Kind == SyntaxKind.ImportSpecifier)
                        {
                            if (element == aliasDeclaration)
                            {
                                continue;
                            }
                        }
                        // Skip if already type-only
                        if (!spec.IsTypeOnly)
                        {
                            changes.InsertModifierBefore(sourceFile, SyntaxKind.TypeKeyword, element);
                        }
                    }
                }
            }
        }
    }

    // fix.go:1269-1284 — deleteTypeKeyword deletes the 'type' keyword token starting at the
    // given position, including any trailing whitespace.
    internal static void DeleteTypeKeyword(global::Ts.Ls.Change.Tracker changes, SourceFile sourceFile, int startPos)
    {
        var scan = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, startPos);
        if (scan.Token() != SyntaxKind.TypeKeyword)
        {
            return;
        }
        var typeStart = scan.TokenStart();
        var typeEnd = scan.TokenEnd();
        // Skip trailing whitespace
        var text = sourceFile.Text();
        while (typeEnd < text.Length && (text[typeEnd] == ' ' || text[typeEnd] == '\t'))
        {
            typeEnd++;
        }
        changes.DeleteRange(sourceFile, new TextRange(typeStart, typeEnd));
    }

    // fix.go:1286-1305 — getModuleSpecifierText.
    internal static string GetModuleSpecifierText(Node promotedDeclaration)
    {
        if (promotedDeclaration.Kind == SyntaxKind.ImportEqualsDeclaration)
        {
            var importEqualsDeclaration = promotedDeclaration.AsImportEqualsDeclaration();
            if (NodeTests.IsExternalModuleReference(importEqualsDeclaration.ModuleReference!))
            {
                var expr = importEqualsDeclaration.ModuleReference!.Expression();
                if (expr != null)
                {
                    if (AstUtilities.IsStringLiteralLike(expr))
                    {
                        return expr.Text();
                    }
                    return global::Ts.Scanner.Scanner.GetTextOfNode(expr);
                }
            }
            return global::Ts.Scanner.Scanner.GetTextOfNode(importEqualsDeclaration.ModuleReference!);
        }
        var moduleSpecifier = promotedDeclaration.Parent!.ModuleSpecifier();
        if (AstUtilities.IsStringLiteralLike(moduleSpecifier!))
        {
            return moduleSpecifier!.Text();
        }
        return global::Ts.Scanner.Scanner.GetTextOfNode(moduleSpecifier!);
    }

    // fix.go:1307-1314 — compareModuleSpecifierRelativity: returns `-1` if `a` is better
    // than `b`.
    internal static int CompareModuleSpecifierRelativity(Fix a, Fix b, global::Ts.ModuleSpecifiers.UserPreferences preferences)
    {
        if (preferences.ImportModuleSpecifierPreference == global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.NonRelative ||
            preferences.ImportModuleSpecifierPreference == global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.ProjectRelative)
        {
            return CoreUtil.CompareBooleans(a.ModuleSpecifierKind == global::Ts.ModuleSpecifiers.ResultKind.Relative, b.ModuleSpecifierKind == global::Ts.ModuleSpecifiers.ResultKind.Relative);
        }
        return 0;
    }
}

// fix.go:820-824 — existingImport.
internal sealed class ExistingImport
{
    internal Node node = null!;
    internal string moduleSpecifier = "";
    internal int index;
}

// The View methods declared in fix.go, plus the view.go:21-33 fields they rely on (see the
// file-header PORT-NOTE — the registry-related members and NewView land with the registry
// wave).
public sealed partial class View
{
    internal SourceFile importingFile = null!;
    internal global::Ts.Compiler.Program program = null!;
    internal global::Ts.ModuleSpecifiers.UserPreferences preferences;

    internal Tristate shouldUseUriStyleNodeCoreModules;
    internal MultiMap<ModuleID, ExistingImport>? existingImports;
    internal bool? shouldUseRequireForFixes;

    // specifiers.go:9-75 — GetModuleSpecifier. PORT-NOTE: reads registry entrypoints and the
    // per-file specifier cache; lands with the registry wave.
    public (string Specifier, global::Ts.ModuleSpecifiers.ResultKind Kind) GetModuleSpecifier(Export export, global::Ts.ModuleSpecifiers.UserPreferences userPreferences)
    {
        throw new NotImplementedException("autoimport.View.GetModuleSpecifier: registry wave");
    }

    // fix.go:537-603 — GetFixes.
    public Fix[]? GetFixes(Export export, bool forJSX, bool isValidTypeOnlyUseSite, global::Ts.LsProto.Position? usagePosition)
    {
        var fixes = new List<Fix>();
        var namespaceFix = TryUseExistingNamespaceImport(export, usagePosition);
        if (namespaceFix != null)
        {
            fixes.Add(namespaceFix);
        }

        var addFix = TryAddToExistingImport(export, isValidTypeOnlyUseSite);
        if (addFix != null)
        {
            fixes.Add(addFix);
            return fixes.ToArray();
        }

        // !!! getNewImportFromExistingSpecifier - even worth it?

        var (moduleSpecifier, moduleSpecifierKind) = GetModuleSpecifier(export, preferences);
        if (moduleSpecifier.Length == 0)
        {
            if (fixes.Count > 0)
            {
                return fixes.ToArray();
            }
            return null;
        }

        // Check if we need a JSDoc import type fix (for JS files with type-only imports)
        var isJs = global::Ts.TsPath.TsPath.HasJSFileExtension(importingFile.FileName());
        var importedSymbolHasValueMeaning = (export.Flags & SymbolFlags.Value) != 0 || export.IsUnresolvedAlias();
        if (!importedSymbolHasValueMeaning && isJs && usagePosition != null)
        {
            // For pure types in JS files, use JSDoc import type syntax
            return
            [
                new Fix
                {
                    AutoImportFix = new global::Ts.LsProto.AutoImportFix
                    {
                        Kind = global::Ts.LsProto.AutoImportFixKind.JsdocTypeImport,
                        ModuleSpecifier = moduleSpecifier,
                        Name = export.Name(),
                        UsagePosition = usagePosition,
                    },
                    ModuleSpecifierKind = moduleSpecifierKind,
                    IsReExport = export.Target.ModuleID != export.ModuleID,
                    ModuleFileName = export.ModuleFileName,
                },
            ];
        }

        var importKind = AutoImportApi.GetImportKind(importingFile, export, program);
        var addAsTypeOnly = AutoImportApi.GetAddAsTypeOnly(isValidTypeOnlyUseSite, export, program.Options());

        var name = export.Name();
        var startsWithUpper = System.Text.Rune.IsUpper(new System.Text.Rune(name[0]));
        if (forJSX && !startsWithUpper)
        {
            if (export.IsRenameable())
            {
                name = (char)global::Ts.StringUtil.StringUtil.ToUpperRune(name[0]) + name[1..];
            }
            else
            {
                return null;
            }
        }

        fixes.Add(new Fix
        {
            AutoImportFix = new global::Ts.LsProto.AutoImportFix
            {
                Kind = global::Ts.LsProto.AutoImportFixKind.AddNew,
                ImportKind = importKind,
                ModuleSpecifier = moduleSpecifier,
                Name = name,
                UseRequire = ShouldUseRequire(),
                AddAsTypeOnly = addAsTypeOnly,
            },
            ModuleSpecifierKind = moduleSpecifierKind,
            IsReExport = export.Target.ModuleID != export.ModuleID,
            ModuleFileName = export.ModuleFileName,
        });
        return fixes.ToArray();
    }

    // fix.go:619-650 — tryUseExistingNamespaceImport.
    internal Fix? TryUseExistingNamespaceImport(Export export, global::Ts.LsProto.Position? usagePosition)
    {
        if (usagePosition == null)
        {
            return null;
        }

        if (AutoImportApi.GetImportKind(importingFile, export, program) != global::Ts.LsProto.ImportKind.Named)
        {
            return null;
        }

        var existingImportsMap = GetExistingImports();
        var matchingDeclarations = existingImportsMap.Get(export.ModuleID);
        foreach (var existingImport in matchingDeclarations ?? [])
        {
            var namespacePrefix = AutoImportApi.GetNamespaceLikeImportText(existingImport.node);
            if (namespacePrefix.Length == 0 || existingImport.moduleSpecifier.Length == 0)
            {
                continue;
            }
            return new Fix
            {
                AutoImportFix = new global::Ts.LsProto.AutoImportFix
                {
                    Kind = global::Ts.LsProto.AutoImportFixKind.UseNamespace,
                    Name = export.Name(),
                    ModuleSpecifier = existingImport.moduleSpecifier,
                    ImportKind = global::Ts.LsProto.ImportKind.Namespace,
                    AddAsTypeOnly = global::Ts.LsProto.AddAsTypeOnly.Allowed,
                    ImportIndex = existingImport.index,
                    UsagePosition = usagePosition,
                    NamespacePrefix = namespacePrefix,
                },
            };
        }

        return null;
    }

    // fix.go:673-778 — tryAddToExistingImport.
    internal Fix? TryAddToExistingImport(Export export, bool isValidTypeOnlyUseSite)
    {
        var existingImportsMap = GetExistingImports();
        var matchingDeclarations = existingImportsMap.Get(export.ModuleID);
        if ((matchingDeclarations ?? []).Count == 0)
        {
            return null;
        }

        // Can't use an es6 import for a type in JS.
        if (AstUtilities.IsSourceFileJS(importingFile) && (export.Flags & SymbolFlags.Value) == 0 && !matchingDeclarations!.TrueForAll(i => NodeTests.IsJSDocImportTag(i.node)))
        {
            return null;
        }

        var importKind = AutoImportApi.GetImportKind(importingFile, export, program);
        if (importKind == global::Ts.LsProto.ImportKind.CommonJS || importKind == global::Ts.LsProto.ImportKind.Namespace)
        {
            return null;
        }

        var addAsTypeOnly = AutoImportApi.GetAddAsTypeOnly(isValidTypeOnlyUseSite, export, program.Options());

        Fix? best = null;
        foreach (var existingImport in matchingDeclarations!)
        {
            if (existingImport.node.Kind == SyntaxKind.ImportEqualsDeclaration)
            {
                continue;
            }

            if (existingImport.node.Kind == SyntaxKind.VariableDeclaration)
            {
                if ((importKind == global::Ts.LsProto.ImportKind.Named || importKind == global::Ts.LsProto.ImportKind.Default) && existingImport.node.Name()!.Kind == SyntaxKind.ObjectBindingPattern)
                {
                    var varFix = new Fix
                    {
                        AutoImportFix = new global::Ts.LsProto.AutoImportFix
                        {
                            Kind = global::Ts.LsProto.AutoImportFixKind.AddToExisting,
                            Name = export.Name(),
                            ImportKind = importKind,
                            ImportIndex = existingImport.index,
                            ModuleSpecifier = existingImport.moduleSpecifier,
                            AddAsTypeOnly = addAsTypeOnly,
                        },
                    };
                    // Variable declarations are never type-only.
                    // Give preference to putting types in existing type-only imports and avoiding conversions
                    // of import statements to/from type-only.
                    if (addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.NotAllowed)
                    {
                        return varFix;
                    }
                    if (best == null)
                    {
                        best = varFix;
                    }
                }
                continue;
            }

            var importClauseNode = existingImport.node.ImportClause();
            if (importClauseNode == null || !AstUtilities.IsStringLiteralLike(existingImport.node.ModuleSpecifier()!))
            {
                // Side-effect import (no import clause) - can't add to it
                continue;
            }
            var importClause = importClauseNode.AsImportClause();

            var namedBindings = importClause.NamedBindings;
            // A type-only import may not have both a default and named imports, so the only way a name can
            // be added to an existing type-only import is adding a named import to existing named bindings.
            if (importClause.IsTypeOnly() && !(importKind == global::Ts.LsProto.ImportKind.Named && namedBindings != null))
            {
                continue;
            }

            if (importKind == global::Ts.LsProto.ImportKind.Default && (importClause.Name() != null ||
                // Cannot add a default import as type-only if the import already has named bindings
                addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.Required && namedBindings != null))
            {
                continue;
            }

            // Cannot add a named import to a declaration that has a namespace import
            if (importKind == global::Ts.LsProto.ImportKind.Named && namedBindings != null && namedBindings.Kind == SyntaxKind.NamespaceImport)
            {
                continue;
            }

            var fix = new Fix
            {
                AutoImportFix = new global::Ts.LsProto.AutoImportFix
                {
                    Kind = global::Ts.LsProto.AutoImportFixKind.AddToExisting,
                    Name = export.Name(),
                    ImportKind = importKind,
                    ImportIndex = existingImport.index,
                    ModuleSpecifier = existingImport.moduleSpecifier,
                    AddAsTypeOnly = addAsTypeOnly,
                },
            };

            var isTypeOnly = importClause.IsTypeOnly();
            // Give preference to putting types in existing type-only imports and avoiding conversions
            // of import statements to/from type-only.
            if ((addAsTypeOnly != global::Ts.LsProto.AddAsTypeOnly.NotAllowed && isTypeOnly) ||
                (addAsTypeOnly == global::Ts.LsProto.AddAsTypeOnly.NotAllowed && !isTypeOnly))
            {
                return fix;
            }
            if (best == null)
            {
                best = fix;
            }
        }

        return best;
    }

    // fix.go:826-855 — getExistingImports.
    internal MultiMap<ModuleID, ExistingImport> GetExistingImports()
    {
        if (existingImports != null)
        {
            return existingImports;
        }

        var result = new MultiMap<ModuleID, ExistingImport>((importingFile.Imports() ?? []).Length); // NewMultiMapWithSizeHint
        var (ch, done) = program.GetTypeChecker(default);
        try
        {
            var imports = importingFile.Imports() ?? [];
            for (var i = 0; i < imports.Length; i++)
            {
                var moduleSpecifier = imports[i];
                var node = AstUtilities.TryGetImportFromModuleSpecifier(moduleSpecifier);
                if (node == null)
                {
                    throw new InvalidOperationException("error: did not expect node kind " + moduleSpecifier.Kind);
                }
                else if (AstUtilities.IsVariableDeclarationInitializedToRequire(node.Parent!))
                {
                    var moduleSymbol = ch.ResolveExternalModuleName(moduleSpecifier);
                    if (moduleSymbol != null)
                    {
                        if (Util.TryGetModuleIDAndFileNameOfModuleSymbol(moduleSymbol, out var moduleID, out _))
                        {
                            result.Add(moduleID, new ExistingImport { node = node.Parent!, moduleSpecifier = moduleSpecifier.Text(), index = i });
                        }
                    }
                }
                else if (node.Kind == SyntaxKind.ImportDeclaration || node.Kind == SyntaxKind.ImportEqualsDeclaration || node.Kind == SyntaxKind.JSDocImportTag)
                {
                    var moduleSymbol = ch.GetSymbolAtLocation(moduleSpecifier);
                    if (moduleSymbol != null)
                    {
                        if (Util.TryGetModuleIDAndFileNameOfModuleSymbol(moduleSymbol, out var moduleID, out _))
                        {
                            result.Add(moduleID, new ExistingImport { node = node, moduleSpecifier = moduleSpecifier.Text(), index = i });
                        }
                    }
                }
            }
        }
        finally
        {
            done();
        }
        existingImports = result;
        return result;
    }

    // fix.go:857-864 — shouldUseRequire.
    internal bool ShouldUseRequire()
    {
        if (shouldUseRequireForFixes != null)
        {
            return shouldUseRequireForFixes.Value;
        }
        var shouldUseRequire = ComputeShouldUseRequire();
        shouldUseRequireForFixes = shouldUseRequire;
        return shouldUseRequire;
    }

    // fix.go:929-973 — computeShouldUseRequire.
    internal bool ComputeShouldUseRequire()
    {
        // 1. TypeScript files don't use require variable declarations
        if (!global::Ts.TsPath.TsPath.HasJSFileExtension(importingFile.FileName()))
        {
            return false;
        }

        // 2. If the current source file is unambiguously CJS or ESM, go with that
        switch (AutoImportApi.DetectSyntax(importingFile, program.Options()))
        {
            case AutoImportApi.FileSyntaxKind.CJS:
                return true;
            case AutoImportApi.FileSyntaxKind.ESM:
                return false;
        }

        // 3. Use the implied node format to determine CJS vs ESM
        //    TODO: consider removing `impliedNodeFormatForEmit`
        switch (program.GetImpliedNodeFormatForEmit(importingFile))
        {
            case ModuleKind.CommonJS:
                return true;
            case ModuleKind.ESNext:
                return false;
        }

        // 4. If there's a tsconfig/jsconfig, use its module setting
        if (program.Options().ConfigFilePath.Length != 0)
        {
            return program.Options().GetEmitModuleKind() < ModuleKind.ES2015;
        }

        // 5. Match the first other JS file in the program that's unambiguously CJS or ESM
        foreach (var otherFile in program.SourceFiles())
        {
            if (otherFile == importingFile || !AstUtilities.IsSourceFileJS(otherFile) || program.IsSourceFileFromExternalLibrary(otherFile))
            {
                continue;
            }
            switch (AutoImportApi.DetectSyntax(otherFile, program.Options()))
            {
                case AutoImportApi.FileSyntaxKind.CJS:
                    return true;
                case AutoImportApi.FileSyntaxKind.ESM:
                    return false;
            }
        }

        // 6. Literally nothing to go on
        return true;
    }

    // fix.go:983-992 — CompareFixesForSorting returns negative if `a` is better than `b`.
    // Sorting with this comparator will place the best fix first.
    // After rank sorting, fixes will be sorted by arbitrary but stable criteria
    // to ensure a deterministic order.
    public int CompareFixesForSorting(Fix a, Fix b)
    {
        var res = CompareFixesForRanking(a, b);
        if (res != 0)
        {
            return res;
        }
        return CompareModuleSpecifiersForSorting(a, b);
    }

    // fix.go:994-1002 — CompareFixesForRanking returns negative if `a` is better than `b`.
    // Sorting with this comparator will place the best fix first.
    // Fixes of equal desirability will be considered equal.
    public int CompareFixesForRanking(Fix a, Fix b)
    {
        var res = AutoImportApi.CompareFixKinds(a.Kind, b.Kind);
        if (res != 0)
        {
            return res;
        }
        return CompareModuleSpecifiersForRanking(a, b);
    }

    // fix.go:1008-1029 — compareModuleSpecifiersForRanking.
    internal int CompareModuleSpecifiersForRanking(Fix a, Fix b)
    {
        var comparison = AutoImportApi.CompareModuleSpecifierRelativity(a, b, preferences);
        if (comparison != 0)
        {
            return comparison;
        }
        if (a.ModuleSpecifierKind == global::Ts.ModuleSpecifiers.ResultKind.Ambient && b.ModuleSpecifierKind == global::Ts.ModuleSpecifiers.ResultKind.Ambient)
        {
            comparison = CompareNodeCoreModuleSpecifiers(a.ModuleSpecifier, b.ModuleSpecifier, importingFile, program);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        if (a.ModuleSpecifierKind == global::Ts.ModuleSpecifiers.ResultKind.Relative && b.ModuleSpecifierKind == global::Ts.ModuleSpecifiers.ResultKind.Relative)
        {
            comparison = CoreUtil.CompareBooleans(
                AutoImportApi.IsFixPossiblyReExportingImportingFile(a, importingFile.FileName()),
                AutoImportApi.IsFixPossiblyReExportingImportingFile(b, importingFile.FileName()));
            if (comparison != 0)
            {
                return comparison;
            }
        }
        {
            var cmp = global::Ts.TsPath.TsPath.CompareNumberOfDirectorySeparators(a.ModuleSpecifier, b.ModuleSpecifier);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return 0;
    }

    // fix.go:1031-1050 — compareModuleSpecifiersForSorting.
    internal int CompareModuleSpecifiersForSorting(Fix a, Fix b)
    {
        var res = CompareModuleSpecifiersForRanking(a, b);
        if (res != 0)
        {
            return res;
        }
        // Sort ./foo before ../foo for equal-length specifiers
        if (a.ModuleSpecifier.StartsWith("./", StringComparison.Ordinal) && !b.ModuleSpecifier.StartsWith("./", StringComparison.Ordinal))
        {
            return -1;
        }
        if (b.ModuleSpecifier.StartsWith("./", StringComparison.Ordinal) && !a.ModuleSpecifier.StartsWith("./", StringComparison.Ordinal))
        {
            return 1;
        }
        var comparison = string.CompareOrdinal(a.ModuleSpecifier, b.ModuleSpecifier);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = ((int)a.ImportKind).CompareTo((int)b.ImportKind);
        if (comparison != 0)
        {
            return comparison;
        }
        // !!! further tie-breakers? In practice this is only called on fixes with the same name
        return 0;
    }

    // fix.go:1052-1069 — compareNodeCoreModuleSpecifiers.
    internal int CompareNodeCoreModuleSpecifiers(string a, string b, SourceFile importingFileArg, global::Ts.Compiler.Program programArg)
    {
        if (a.StartsWith("node:", StringComparison.Ordinal) && !b.StartsWith("node:", StringComparison.Ordinal))
        {
            if (shouldUseUriStyleNodeCoreModules.IsTrue())
            {
                return -1;
            }
            else if (shouldUseUriStyleNodeCoreModules.IsFalse())
            {
                return 1;
            }
            return 0;
        }
        if (b.StartsWith("node:", StringComparison.Ordinal) && !a.StartsWith("node:", StringComparison.Ordinal))
        {
            if (shouldUseUriStyleNodeCoreModules.IsTrue())
            {
                return 1;
            }
            else if (shouldUseUriStyleNodeCoreModules.IsFalse())
            {
                return -1;
            }
        }
        return 0;
    }
}
