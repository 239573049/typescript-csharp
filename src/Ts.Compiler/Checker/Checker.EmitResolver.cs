// Ported from tsgo internal/checker/emitresolver.go
//
// The Go `EmitResolver` is a distinct struct wrapping *Checker that implements
// printer.EmitResolver. This is the faithful C# port of that struct: a sealed class in
// `Ts.Checker` implementing `global::Ts.Printer.EmitResolver`, wrapping a Checker. The
// checker's `GetEmitResolver()` (checker.go:31916) lazily constructs it.
//
// PORT-NOTE (§1.5 cross-package qualification): the EmitResolver interface + EmitContext +
// SymbolAccessibility* live in `Ts.Printer`; the NodeBuilder Flags/InternalFlags/ISymbolTracker
// live in `Ts.NodeBuilder`; Evaluator.Result lives in `Ts.Evaluator`. Since class names such as
// `Printer`/`NodeBuilder` collide with sibling namespace segments, all cross-package references
// use `global::` qualification. Host helpers with no namespace collision (NodeTests, AstUtilities,
// CoreUtil) are used unqualified.
//
// PORT-NOTE (locking): Go locks r.checkerMu (== &checker.mu) on every external API method. C#
// mirrors this with `lock (checker.mu)`. The `*Unsafe` variants and node-construction methods that
// document "already holds a lock" via the symbol tracker are NOT wrapped (faithful to Go).
//
// COVERAGE: The reference/visibility half (JS emit) is fully ported. The type-node synthesis half
// (.d.ts) depends on the NodeBuilder request-node serialize entry points
// (NewNodeBuilder(...).SerializeTypeForDeclaration/SerializeReturnTypeForSignature/etc.) which are
// not yet ported in the NodeBuilder wave; those method BODIES call `checker.NewNodeBuilder(...)`
// which throws NotImplementedException until that wave lands. Signatures match the interface exactly.

using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Checker;

// emitresolver.go:20-22 — Links for jsx.
internal sealed class JSXLinks
{
    internal Node? importRef;
}

// emitresolver.go:26-28 — Links for declarations.
internal sealed class DeclarationLinks
{
    // if declaration is depended upon by exported declarations
    internal Tristate isVisible;
}

// emitresolver.go:30-32
internal sealed class DeclarationFileLinks
{
    // if file has had alias visibility marked
    internal bool aliasesMarked;
}

// emitresolver.go:34-43 — public emit-time resolver.
public sealed partial class EmitResolver : global::Ts.Printer.EmitResolver
{
    private readonly Checker checker;
    private readonly Lock checkerMu;
    private global::Ts.Binder.IReferenceResolver? referenceResolver;
    private readonly LinkStore<Node, JSXLinks> jsxLinks = new();
    private readonly LinkStore<Node, DeclarationLinks> declarationLinks = new();
    private readonly LinkStore<Node, DeclarationFileLinks> declarationFileLinks = new();

    // emitresolver.go:45-51 — newEmitResolver.
    internal EmitResolver(Checker checker)
    {
        this.checker = checker;
        checkerMu = checker.mu;
        // Go binds e.isValueAliasDeclaration/e.aliasMarkingVisitor to worker methods; in C# these
        // are just instance methods referenced directly by name.
    }

    // emitresolver.go:53-57
    public Node? GetJsxFactoryEntity(Node location)
    {
        lock (checkerMu)
        {
            return checker.GetJsxFactoryEntity(location);
        }
    }

    // emitresolver.go:59-63
    public Node? GetJsxFragmentFactoryEntity(Node location)
    {
        lock (checkerMu)
        {
            return checker.GetJsxFragmentFactoryEntity(location);
        }
    }

    // emitresolver.go:65-69
    public bool IsOptionalParameter(Node node)
    {
        lock (checkerMu)
        {
            return IsOptionalParameterWorker(node);
        }
    }

    // emitresolver.go:71-87
    public bool IsLateBound(Node node)
    {
        // TODO: Require an emitContext to construct an EmitResolver, remove all emitContext arguments
        // node = r.emitContext.ParseNode(node)
        if (node is null)
        {
            return false;
        }
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return false;
        }
        lock (checkerMu)
        {
            var symbol = checker.GetSymbolOfDeclaration(node);
            if (symbol is null)
            {
                return false;
            }
            return (symbol.CheckFlags & CheckFlags.Late) != 0;
        }
    }

    // emitresolver.go:89-102
    public global::Ts.Evaluator.Result GetEnumMemberValue(Node node)
    {
        // node = r.emitContext.ParseNode(node)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return global::Ts.Evaluator.Result.NewResult(null, false, false, false);
        }
        lock (checkerMu)
        {
            checker.ComputeEnumMemberValues(node.Parent!);
            if (!checker.enumMemberLinks.Has(node))
            {
                return global::Ts.Evaluator.Result.NewResult(null, false, false, false);
            }
            return checker.enumMemberLinks.Get(node).value;
        }
    }

    // emitresolver.go:104-109
    public bool IsDeclarationVisible(Node node)
    {
        // Only lock on external API func to prevent deadlocks
        lock (checkerMu)
        {
            return IsDeclarationVisibleWorker(node);
        }
    }

    // emitresolver.go:111-129
    private bool IsDeclarationVisibleWorker(Node? node)
    {
        // node = r.emitContext.ParseNode(node)
        if (node is null || !global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return false;
        }

        var links = declarationLinks.Get(node);
        if (links.isVisible == Tristate.Unknown)
        {
            links.isVisible = DetermineIfDeclarationIsVisible(node) ? Tristate.True : Tristate.False;
        }
        return links.isVisible == Tristate.True;
    }

    // emitresolver.go:131-234
    private bool DetermineIfDeclarationIsVisible(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.JSDocCallbackTag:
            // case SyntaxKind.JSDocEnumTag: // !!! TODO: JSDoc @enum support?
            case SyntaxKind.JSDocTypedefTag:
                // Top-level jsdoc type aliases are considered exported
                // First parent is comment node, second is hosting declaration or token; we only care
                // about those tokens or declarations whose parent is a source file
                return node.Parent is not null && node.Parent.Parent is not null &&
                    node.Parent.Parent.Parent is not null && NodeTests.IsSourceFile(node.Parent.Parent.Parent);
            case SyntaxKind.BindingElement:
                return IsDeclarationVisibleWorker(node.Parent!.Parent);
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            {
                if (NodeTests.IsVariableDeclaration(node))
                {
                    if (AstUtilities.IsBindingPattern(node.Name()) &&
                        (node.Name()!.Elements()?.Length ?? 0) == 0)
                    {
                        // If the binding pattern is empty, this variable declaration is not visible
                        return false;
                    }
                    // falls through
                }
                // External module augmentation is always visible
                // A @typedef at top-level in an external module is always visible
                if (Checker.IsExternalModuleAugmentation(node) || AstUtilities.IsImplicitlyExportedJSDocDeclaration(node))
                {
                    return true;
                }
                var parent = AstUtilities.GetDeclarationContainer(node)!;
                // If the node is not exported or it is not ambient module element (except import declaration)
                if ((checker.GetCombinedModifierFlagsCached(node) & ModifierFlags.Export) == 0 &&
                    !(node.Kind != SyntaxKind.ImportEqualsDeclaration && parent.Kind != SyntaxKind.SourceFile &&
                      (parent.Flags & NodeFlags.Ambient) != 0))
                {
                    return AstUtilities.IsGlobalSourceFile(parent);
                }
                // Exported members/ambient module elements (exception import declaration) are visible
                // if parent is visible
                return IsDeclarationVisibleWorker(parent);
            }

            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                if (checker.GetEffectiveDeclarationFlags(node, ModifierFlags.Private | ModifierFlags.Protected) != 0)
                {
                    // Private/protected properties/methods are not visible
                    return false;
                }
                // Public properties/methods are visible if its parents are visible, so:
                return IsDeclarationVisibleWorker(node.Parent);

            case SyntaxKind.Constructor:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.Parameter:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.TypeReference:
            case SyntaxKind.ArrayType:
            case SyntaxKind.TupleType:
            case SyntaxKind.UnionType:
            case SyntaxKind.IntersectionType:
            case SyntaxKind.ParenthesizedType:
            case SyntaxKind.NamedTupleMember:
                return IsDeclarationVisibleWorker(node.Parent);

            // Default binding, import specifier and namespace import is visible
            // only on demand so by default it is not visible
            case SyntaxKind.ImportClause:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.ImportSpecifier:
                return false;

            // Type parameters are always visible
            case SyntaxKind.TypeParameter:
                return true;
            // Source file and namespace export are always visible
            case SyntaxKind.SourceFile:
            case SyntaxKind.NamespaceExportDeclaration:
                return true;

            // Export assignments do not create name bindings outside the module
            case SyntaxKind.ExportAssignment:
                return false;

            // An `export {X}` (without a module specifier) is itself a visible re-export of
            // the named binding; it contributes to the symbol's external visibility.
            case SyntaxKind.ExportSpecifier:
            {
                var exportDecl = node.Parent!.Parent!;
                if (NodeTests.IsExportDeclaration(exportDecl) &&
                    exportDecl.AsExportDeclaration().ModuleSpecifier is null)
                {
                    return IsDeclarationVisibleWorker(exportDecl.Parent);
                }
                return false;
            }

            default:
                return false;
        }
    }

    // emitresolver.go:236-247
    public void PrecalculateDeclarationEmitVisibility(SourceFile file)
    {
        lock (checkerMu)
        {
            if (declarationFileLinks.Get(file.AsNode()).aliasesMarked)
            {
                return;
            }
            declarationFileLinks.Get(file.AsNode()).aliasesMarked = true;
            // TODO: Does this even *have* to be an upfront walk? If it's not possible for an
            // import a = a.b.c statement to chain into exposing a statement in a sibling scope,
            // it could at least be pushed into scope entry - then it wouldn't need to be recursive.
            file.AsNode().ForEachChild(AliasMarkingVisitorWorker);
        }
    }

    // emitresolver.go:249-258
    private static bool IsCommonJSModuleExports(Node node)
    {
        if (NodeTests.IsBinaryExpression(node) && NodeTests.IsExpressionStatement(node.Parent!) &&
            NodeTests.IsSourceFile(node.Parent!.Parent!) &&
            node.Parent!.Parent!.AsSourceFile().CommonJSModuleIndicator is not null)
        {
            switch (AstUtilities.GetAssignmentDeclarationKind(node))
            {
                case JSDeclarationKind.ModuleExports:
                case JSDeclarationKind.ExportsProperty:
                    return true;
            }
        }
        return false;
    }

    // emitresolver.go:260-274
    private bool AliasMarkingVisitorWorker(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.BinaryExpression:
                if (IsCommonJSModuleExports(node) && NodeTests.IsIdentifier(node.AsBinaryExpression().Right!))
                {
                    MarkLinkedAliases(node.AsBinaryExpression().Right!);
                }
                break;
            case SyntaxKind.ExportAssignment:
                if (node.Expression()!.Kind == SyntaxKind.Identifier)
                {
                    MarkLinkedAliases(node.Expression()!);
                }
                break;
            case SyntaxKind.ExportSpecifier:
                MarkLinkedAliases(node.PropertyNameOrName()!);
                break;
        }
        return node.ForEachChild(AliasMarkingVisitorWorker);
    }

    // emitresolver.go:276-309 — Sets the isVisible link on statements the Identifier or ExportName
    // node points at. Follows chains of import d = a.b.c.
    private void MarkLinkedAliases(Node node)
    {
        Symbol? exportSymbol = null;
        if (node.Kind != SyntaxKind.StringLiteral && node.Parent is not null &&
            (NodeTests.IsExportAssignment(node.Parent) || IsCommonJSModuleExports(node.Parent)))
        {
            exportSymbol = checker.ResolveName(node, node.Text(),
                SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias,
                null /*nameNotFoundMessage*/, false /*isUse*/, false);
        }
        else if (node.Parent!.Kind == SyntaxKind.ExportSpecifier)
        {
            exportSymbol = checker.GetTargetOfExportSpecifier(node.Parent,
                SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias, false);
        }

        var visited = new HashSet<SymbolId>(); // guard against circular imports
        while (exportSymbol is not null)
        {
            if (!visited.Add(AstUtilities.GetSymbolId(exportSymbol)))
            {
                break;
            }

            Symbol? nextSymbol = null;
            foreach (var declaration in exportSymbol.Declarations ?? [])
            {
                declarationLinks.Get(declaration).isVisible = Tristate.True;

                if (Checker.IsInternalModuleImportEqualsDeclaration(declaration))
                {
                    // Add the referenced top container visible
                    var internalModuleReference = declaration.AsImportEqualsDeclaration().ModuleReference;
                    var firstIdentifier = Checker.GetFirstIdentifier(internalModuleReference!);
                    var importSymbol = checker.ResolveName(declaration, firstIdentifier.Text(),
                        SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias,
                        null /*nameNotFoundMessage*/, false /*isUse*/, false);
                    nextSymbol = importSymbol;
                }
            }

            exportSymbol = nextSymbol;
        }
    }

    // emitresolver.go:334-338
    public global::Ts.Printer.SymbolAccessibilityResult IsEntityNameVisible(Node entityName, Node enclosingDeclaration)
    {
        lock (checkerMu)
        {
            return IsEntityNameVisibleWorker(entityName, enclosingDeclaration, true);
        }
    }

    // emitresolver.go:340-380
    private global::Ts.Printer.SymbolAccessibilityResult IsEntityNameVisibleWorker(
        Node entityName, Node enclosingDeclaration, bool shouldComputeAliasToMakeVisible)
    {
        // node = r.emitContext.ParseNode(entityName)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(entityName))
        {
            return new global::Ts.Printer.SymbolAccessibilityResult
            {
                Accessibility = global::Ts.Printer.SymbolAccessibility.NotAccessible,
            };
        }

        var meaning = GetMeaningOfEntityNameReference(entityName);
        var firstIdentifier = Checker.GetFirstIdentifier(entityName);

        var symbol = checker.ResolveName(enclosingDeclaration, firstIdentifier.Text(), meaning, null, false, false);

        if (symbol is not null && (symbol.Flags & SymbolFlags.TypeParameter) != 0 && (meaning & SymbolFlags.Type) != 0)
        {
            return new global::Ts.Printer.SymbolAccessibilityResult
            {
                Accessibility = global::Ts.Printer.SymbolAccessibility.Accessible,
            };
        }

        if (symbol is null && JSDocReparseAstGaps.IsThisIdentifier(firstIdentifier))
        {
            var sym = checker.GetSymbolOfDeclaration(AstUtilities.GetThisContainer(firstIdentifier, false, false));
            if (IsSymbolAccessibleWorker(sym, enclosingDeclaration, meaning, false).Accessibility ==
                global::Ts.Printer.SymbolAccessibility.Accessible)
            {
                return new global::Ts.Printer.SymbolAccessibilityResult
                {
                    Accessibility = global::Ts.Printer.SymbolAccessibility.Accessible,
                };
            }
        }

        if (symbol is null)
        {
            return new global::Ts.Printer.SymbolAccessibilityResult
            {
                Accessibility = global::Ts.Printer.SymbolAccessibility.NotResolved,
                ErrorSymbolName = firstIdentifier.Text(),
                ErrorNode = firstIdentifier,
            };
        }

        var visible = HasVisibleDeclarations(symbol, shouldComputeAliasToMakeVisible);
        if (visible is not null)
        {
            return visible.Value;
        }

        return new global::Ts.Printer.SymbolAccessibilityResult
        {
            Accessibility = global::Ts.Printer.SymbolAccessibility.NotAccessible,
            ErrorSymbolName = firstIdentifier.Text(),
            ErrorNode = firstIdentifier,
        };
    }

    // emitresolver.go:384-465
    private global::Ts.Printer.SymbolAccessibilityResult? HasVisibleDeclarations(
        Symbol symbol, bool shouldComputeAliasToMakeVisible)
    {
        // map-iter: 已排序 — Go collects a map keyed by NodeId then materializes its values via
        // maps.Values into AliasesToMakeVisible. Order across a NodeId-keyed map is deterministic by
        // ascending NodeId; we insertion-order the OrderedMap and materialize in that order.
        OrderedMap<NodeId, Node>? aliasesToMakeVisibleSet = null;

        void AddVisibleAlias(Node declaration, Node aliasingStatement)
        {
            if (!shouldComputeAliasToMakeVisible)
            {
                return;
            }
            declarationLinks.Get(declaration).isVisible = Tristate.True;
            aliasesToMakeVisibleSet ??= new OrderedMap<NodeId, Node>();
            aliasesToMakeVisibleSet.Set(AstUtilities.GetNodeId(declaration), aliasingStatement);
        }

        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (NodeTests.IsIdentifier(declaration))
            {
                continue;
            }
            if (!IsDeclarationVisibleWorker(declaration))
            {
                // Mark the unexported alias as visible if its parent is visible
                // because these kind of aliases can be used to name types in declaration file
                var anyImportSyntax = Checker.GetAnyImportSyntax(declaration);
                if (anyImportSyntax is not null &&
                    !AstUtilities.HasSyntacticModifier(anyImportSyntax, ModifierFlags.Export) && // import clause without export
                    IsDeclarationVisibleWorker(anyImportSyntax.Parent))
                {
                    AddVisibleAlias(declaration, anyImportSyntax);
                    continue;
                }
                if (NodeTests.IsVariableDeclaration(declaration) && NodeTests.IsVariableStatement(declaration.Parent!.Parent!) &&
                    !AstUtilities.HasSyntacticModifier(declaration.Parent!.Parent!, ModifierFlags.Export) && // unexported variable statement
                    IsDeclarationVisibleWorker(declaration.Parent!.Parent!.Parent))
                {
                    AddVisibleAlias(declaration, declaration.Parent!.Parent!);
                    continue;
                }
                if (Checker.IsLateVisibilityPaintedStatement(declaration) && // unexported top-level statement
                    !AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Export) &&
                    IsDeclarationVisibleWorker(declaration.Parent))
                {
                    AddVisibleAlias(declaration, declaration);
                    continue;
                }
                if (NodeTests.IsBindingElement(declaration))
                {
                    if ((symbol.Flags & SymbolFlags.Alias) != 0 && AstUtilities.IsInJSFile(declaration) &&
                        declaration.Parent is not null && declaration.Parent.Parent is not null && // exported import-like top-level JS require statement
                        NodeTests.IsVariableDeclaration(declaration.Parent.Parent) &&
                        declaration.Parent.Parent.Parent?.Parent is not null &&
                        NodeTests.IsVariableStatement(declaration.Parent.Parent.Parent.Parent) &&
                        !AstUtilities.HasSyntacticModifier(declaration.Parent.Parent.Parent.Parent, ModifierFlags.Export) &&
                        declaration.Parent.Parent.Parent.Parent.Parent is not null && // check if the thing containing the variable statement is visible (ie, the file)
                        IsDeclarationVisibleWorker(declaration.Parent.Parent.Parent.Parent.Parent))
                    {
                        AddVisibleAlias(declaration, declaration.Parent.Parent.Parent.Parent);
                        continue;
                    }
                    if ((symbol.Flags & SymbolFlags.BlockScopedVariable) != 0)
                    {
                        var rootDeclaration = Checker.WalkUpBindingElementsAndPatterns(declaration);
                        if (NodeTests.IsParameterDeclaration(rootDeclaration))
                        {
                            return null;
                        }
                        var variableStatement = rootDeclaration.Parent!.Parent!;
                        if (!NodeTests.IsVariableStatement(variableStatement))
                        {
                            return null;
                        }
                        if (AstUtilities.HasSyntacticModifier(variableStatement, ModifierFlags.Export))
                        {
                            continue; // no alias to add, already exported
                        }
                        if (!IsDeclarationVisibleWorker(variableStatement.Parent))
                        {
                            return null; // not visible
                        }
                        AddVisibleAlias(declaration, variableStatement);
                        continue;
                    }
                }

                // Declaration is not visible
                return null;
            }
        }

        return new global::Ts.Printer.SymbolAccessibilityResult
        {
            Accessibility = global::Ts.Printer.SymbolAccessibility.Accessible,
            AliasesToMakeVisible = aliasesToMakeVisibleSet is null
                ? null
                : System.Linq.Enumerable.ToArray<Node>(aliasesToMakeVisibleSet.Values()),
        };
    }

    // emitresolver.go:467-502
    public bool IsImplementationOfOverload(SignatureDeclaration node)
    {
        // node = r.emitContext.ParseNode(node)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return false;
        }
        if (AstUtilities.NodeIsPresent(node.Body()))
        {
            if (NodeTests.IsGetAccessorDeclaration(node) || NodeTests.IsSetAccessorDeclaration(node))
            {
                return false; // Get or set accessors can never be overload implementations, but can have up to 2 signatures
            }
            lock (checkerMu)
            {
                var symbol = checker.GetSymbolOfDeclaration(node);
                var signaturesOfSymbol = checker.GetSignaturesOfSymbol(symbol);
                // If this function body corresponds to function with multiple signature, it is
                // implementation of overload
                if (signaturesOfSymbol.Length > 1)
                {
                    return true;
                }
                // If there is single signature for the symbol, it is overload if that signature
                // isn't coming from the node
                if (signaturesOfSymbol.Length == 1)
                {
                    var declaration = signaturesOfSymbol[0].Declaration();
                    if (declaration != node && (declaration!.Flags & NodeFlags.JSDoc) == 0)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // emitresolver.go:504-538
    public bool IsImportRequiredByAugmentation(ImportDeclaration decl)
    {
        // node = r.emitContext.ParseNode(node)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(decl.AsNode()))
        {
            return false;
        }
        var file = AstUtilities.GetSourceFileOfNode(decl.AsNode())!;
        if (file.Symbol is null)
        {
            // script file
            return false;
        }
        var importTarget = GetExternalModuleFileFromDeclaration(decl.AsNode());
        if (importTarget is null)
        {
            return false;
        }
        if (importTarget == file)
        {
            return false;
        }
        lock (checkerMu)
        {
            var exports = checker.GetExportsOfModule(file.Symbol);
            // map-iter: 序无关 — early-return on first match; membership test only.
            foreach (var s in exports.Values)
            {
                var merged = checker.GetMergedSymbol(s);
                if (merged != s)
                {
                    if ((merged?.Declarations?.Length ?? 0) > 0)
                    {
                        foreach (var d in merged!.Declarations!)
                        {
                            var declFile = AstUtilities.GetSourceFileOfNode(d);
                            if (declFile == importTarget)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        return false;
    }

    // emitresolver.go:540-562
    public bool IsDefinitelyReferenceToGlobalSymbolObject(Node node)
    {
        if (!NodeTests.IsPropertyAccessExpression(node) ||
            !NodeTests.IsIdentifier(node.Name()!) ||
            (!NodeTests.IsPropertyAccessExpression(node.Expression()!) && !NodeTests.IsIdentifier(node.Expression()!)))
        {
            return false;
        }
        if (node.Expression()!.Kind == SyntaxKind.Identifier)
        {
            if (node.Expression()!.Text() != "Symbol")
            {
                return false;
            }
            lock (checkerMu)
            {
                // Exactly `Symbol.something` and `Symbol` either does not resolve or definitely
                // resolves to the global Symbol
                return checker.GetResolvedSymbol(node.Expression()!) ==
                    checker.GetGlobalSymbol("Symbol", SymbolFlags.Value | SymbolFlags.ExportValue, null /*diagnostic*/);
            }
        }
        if (node.Expression()!.Expression()!.Kind != SyntaxKind.Identifier ||
            node.Expression()!.Expression()!.Text() != "globalThis" ||
            node.Expression()!.Name()!.Text() != "Symbol")
        {
            return false;
        }
        lock (checkerMu)
        {
            // Exactly `globalThis.Symbol.something` and `globalThis` resolves to the global `globalThis`
            return checker.GetResolvedSymbol(node.Expression()!.Expression()!) == checker.globalThisSymbol;
        }
    }

    // emitresolver.go:564-571
    public bool RequiresAddingImplicitUndefined(Node declaration, Symbol symbol, Node enclosingDeclaration)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(declaration))
        {
            return false;
        }
        lock (checkerMu)
        {
            return RequiresAddingImplicitUndefinedWorker(declaration, symbol, enclosingDeclaration);
        }
    }

    // emitresolver.go:573-579
    public bool RequiresAddingImplicitUndefinedUnsafe(Node declaration, Symbol symbol, Node enclosingDeclaration)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(declaration))
        {
            return false;
        }
        // NO LOCKING - only should be called in contexts that already have a checker lock
        return RequiresAddingImplicitUndefinedWorker(declaration, symbol, enclosingDeclaration);
    }

    // emitresolver.go:581-599
    private bool RequiresAddingImplicitUndefinedWorker(Node declaration, Symbol? symbol, Node enclosingDeclaration)
    {
        // node = r.emitContext.ParseNode(node)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(declaration))
        {
            return false;
        }
        switch (declaration.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.JSDocPropertyTag:
            {
                symbol ??= checker.GetSymbolOfDeclaration(declaration);
                var t = checker.GetTypeOfSymbol(symbol!);
                checker.mappedSymbolLinks.Has(symbol!);
                // isOptionalDeclaration(declaration) == ast.HasQuestionToken(declaration) (utilities.go:276)
                return (symbol!.Flags & SymbolFlags.Property) != 0 && (symbol.Flags & SymbolFlags.Optional) != 0 &&
                    AstUtilities.HasQuestionToken(declaration) && checker.reverseMappedSymbolLinks.Has(symbol) &&
                    checker.reverseMappedSymbolLinks.Get(symbol).mappedType is not null &&
                    Checker.ContainsNonMissingUndefinedType(checker, t);
            }
            case SyntaxKind.Parameter:
            case SyntaxKind.JSDocParameterTag:
                return RequiresAddingImplicitUndefinedParameterWorker(declaration, enclosingDeclaration);
            default:
                throw new global::System.InvalidOperationException("Node cannot possibly require adding undefined");
        }
    }

    // emitresolver.go:601-603
    private bool RequiresAddingImplicitUndefinedParameterWorker(Node parameter, Node enclosingDeclaration)
    {
        return (IsRequiredInitializedParameter(parameter, enclosingDeclaration) ||
                IsOptionalUninitializedParameterProperty(parameter)) &&
               !DeclaredParameterTypeContainsUndefined(parameter);
    }

    // emitresolver.go:605-616
    private bool DeclaredParameterTypeContainsUndefined(Node parameter)
    {
        // typeNode := getNonlocalEffectiveTypeAnnotationNode(parameter); // !!! JSDoc Support
        var typeNode = parameter.Type();
        if (typeNode is null)
        {
            return false;
        }
        var t = checker.GetTypeFromTypeNode(typeNode);
        // allow error type here to avoid confusing errors that the annotation has to contain
        // undefined when it does in cases like this:
        //   export function fn(x?: Unresolved | undefined): void {}
        return checker.IsErrorType(t) || Checker.ContainsUndefinedType(checker, t);
    }

    // emitresolver.go:618-623
    private bool IsOptionalUninitializedParameterProperty(Node parameter)
    {
        return checker.strictNullChecks &&
            IsOptionalParameterWorker(parameter) &&
            ( /*isJSDocParameterTag(parameter) ||*/ parameter.Initializer() is null) && // !!! TODO: JSDoc support
            AstUtilities.HasSyntacticModifier(parameter, ModifierFlags.ParameterPropertyModifier);
    }

    // emitresolver.go:625-633
    private bool IsRequiredInitializedParameter(Node parameter, Node enclosingDeclaration)
    {
        if (!checker.strictNullChecks || IsOptionalParameterWorker(parameter) ||
            /*isJSDocParameterTag(parameter) ||*/ parameter.Initializer() is null) // !!! TODO: JSDoc Support
        {
            return false;
        }
        if (AstUtilities.HasSyntacticModifier(parameter, ModifierFlags.ParameterPropertyModifier))
        {
            return enclosingDeclaration is not null && AstUtilities.IsFunctionLikeDeclaration(enclosingDeclaration);
        }
        return true;
    }

    // emitresolver.go:635-637
    private bool IsOptionalParameterWorker(Node node)
    {
        return checker.IsOptionalParameter(node);
    }

    // emitresolver.go:639-650
    public bool IsLiteralConstDeclaration(Node node)
    {
        // node = r.emitContext.ParseNode(node)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return false;
        }
        if (Checker.IsDeclarationReadonly(node) ||
            (NodeTests.IsVariableDeclaration(node) && Checker.IsVarConst(node)))
        {
            lock (checkerMu)
            {
                return Checker.IsFreshLiteralType(checker.GetTypeOfSymbol(checker.GetSymbolOfDeclaration(node)!));
            }
        }
        return false;
    }

    // emitresolver.go:652-665
    public bool IsExpandoFunctionDeclarationUnsafe(Node node)
    {
        // node = r.emitContext.ParseNode(node)
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return false;
        }
        // this is substantially different from strada, but so is expando property checking
        var props = GetPropertiesOfContainerFunction(node);
        foreach (var p in props)
        {
            if (Checker.IsExpandoPropertyDeclaration(p.ValueDeclaration))
            {
                return true;
            }
        }
        return false;
    }

    // emitresolver.go:667-671
    public bool IsExpandoFunctionDeclaration(Node node)
    {
        lock (checkerMu)
        {
            return IsExpandoFunctionDeclarationUnsafe(node);
        }
    }

    // emitresolver.go:673-675
    private global::Ts.Printer.SymbolAccessibilityResult IsSymbolAccessibleWorker(
        Symbol? symbol, Node enclosingDeclaration, SymbolFlags meaning, bool shouldComputeAliasToMarkVisible)
    {
        return checker.IsSymbolAccessible(symbol, enclosingDeclaration, meaning, shouldComputeAliasToMarkVisible);
    }

    // emitresolver.go:677-683
    public global::Ts.Printer.SymbolAccessibilityResult IsSymbolAccessible(
        Symbol symbol, Node enclosingDeclaration, SymbolFlags meaning, bool shouldComputeAliasToMarkVisible)
    {
        // TODO: Split into locking and non-locking API methods - only current usage is the symbol
        // tracker, which is non-locking, as all tracker calls happen within a CreateX call below,
        // which already holds a lock
        return IsSymbolAccessibleWorker(symbol, enclosingDeclaration, meaning, shouldComputeAliasToMarkVisible);
    }

    // emitresolver.go:689-713
    public bool IsReferencedAliasDeclaration(Node node)
    {
        var c = checker;
        if (!c.canCollectSymbolAliasAccessibilityData || !global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return true;
        }

        lock (checkerMu)
        {
            if (AstUtilities.IsAliasSymbolDeclaration(node))
            {
                if (c.GetSymbolOfDeclaration(node) is { } symbol)
                {
                    var aliasLinks = c.aliasSymbolLinks.Get(symbol);
                    if (aliasLinks.referenced)
                    {
                        return true;
                    }
                    var target = aliasLinks.aliasTarget;
                    if (target is not null && (node.ModifierFlags() & ModifierFlags.Export) != 0 &&
                        (c.GetSymbolFlags(target) & SymbolFlags.Value) != 0 &&
                        (c.compilerOptions.ShouldPreserveConstEnums() || !IsConstEnumOrConstEnumOnlyModule(target)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    // emitresolver.go:715-725
    public bool IsValueAliasDeclaration(Node node)
    {
        var c = checker;
        if (!c.canCollectSymbolAliasAccessibilityData || !global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return true;
        }

        lock (checkerMu)
        {
            return IsValueAliasDeclarationWorker(node);
        }
    }

    // emitresolver.go:727-754
    private bool IsValueAliasDeclarationWorker(Node node)
    {
        var c = checker;

        switch (node.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
                return IsAliasResolvedToValue(c.GetSymbolOfDeclaration(node), false /*excludeTypeOnlyValues*/);
            case SyntaxKind.ImportClause:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ExportSpecifier:
            {
                var symbol = c.GetSymbolOfDeclaration(node);
                return symbol is not null && IsAliasResolvedToValue(symbol, true /*excludeTypeOnlyValues*/);
            }
            case SyntaxKind.ExportDeclaration:
            {
                var exportClause = node.AsExportDeclaration().ExportClause;
                return exportClause is not null && (NodeTests.IsNamespaceExport(exportClause) ||
                    CoreUtil.Some(exportClause.Elements(), IsValueAliasDeclarationWorker));
            }
            case SyntaxKind.ExportAssignment:
                if (node.Expression() is not null && node.Expression()!.Kind == SyntaxKind.Identifier)
                {
                    return IsAliasResolvedToValue(c.GetSymbolOfDeclaration(node), true /*excludeTypeOnlyValues*/);
                }
                return true;
            case SyntaxKind.BinaryExpression:
                if (IsCommonJSModuleExports(node) && NodeTests.IsIdentifier(node.AsBinaryExpression().Right!))
                {
                    return IsAliasResolvedToValue(c.GetSymbolOfDeclaration(node), true /*excludeTypeOnlyValues*/);
                }
                break;
        }
        return false;
    }

    // emitresolver.go:756-779
    private bool IsAliasResolvedToValue(Symbol? symbol, bool excludeTypeOnlyValues)
    {
        var c = checker;
        if (symbol is null)
        {
            return false;
        }
        if (symbol.ValueDeclaration is not null)
        {
            if (AstUtilities.GetSourceFileOfNode(symbol.ValueDeclaration) is { } container)
            {
                var fileSymbol = c.GetSymbolOfDeclaration(container.AsNode());
                // Ensures cjs export assignment is setup, since this symbol may point at, and merge
                // with, the file itself. If we don't, the merge may not have yet occurred, and the
                // flags check below will be missing flags that are added as a result of the merge.
                c.ResolveExternalModuleSymbol(fileSymbol, false /*dontResolveAlias*/);
            }
        }
        var target = c.GetExportSymbolOfValueSymbolIfExported(c.ResolveAlias(symbol).Item1);
        if (target == c.unknownSymbol)
        {
            return !excludeTypeOnlyValues || c.GetTypeOnlyAliasDeclaration(symbol) is null;
        }
        // const enums and modules that contain only const enums are not considered values from the
        // emit perspective unless 'preserveConstEnums' option is set to true
        return (c.GetSymbolFlagsEx(symbol, excludeTypeOnlyValues, true /*excludeLocalMeanings*/) & SymbolFlags.Value) != 0 &&
            (c.compilerOptions.ShouldPreserveConstEnums() || !IsConstEnumOrConstEnumOnlyModule(target!));
    }

    // emitresolver.go:781-798
    public bool IsTopLevelValueImportEqualsWithEntityName(Node node)
    {
        var c = checker;
        if (!c.canCollectSymbolAliasAccessibilityData)
        {
            return true;
        }
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node) || node.Kind != SyntaxKind.ImportEqualsDeclaration ||
            node.Parent!.Kind != SyntaxKind.SourceFile)
        {
            return false;
        }
        if (NodeTests.IsImportEqualsDeclaration(node) &&
            (AstUtilities.NodeIsMissing(node.AsImportEqualsDeclaration().ModuleReference) ||
             node.AsImportEqualsDeclaration().ModuleReference!.Kind == SyntaxKind.ExternalModuleReference))
        {
            return false;
        }

        lock (checkerMu)
        {
            return IsAliasResolvedToValue(c.GetSymbolOfDeclaration(node), false /*excludeTypeOnlyValues*/);
        }
    }

    // emitresolver.go:800-822
    public void MarkLinkedReferencesRecursively(SourceFile file)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(file.AsNode()))
        {
            return;
        }
        lock (checkerMu)
        {
            if (file is not null)
            {
                bool Visit(Node n)
                {
                    if (NodeTests.IsImportEqualsDeclaration(n) && (n.ModifierFlags() & ModifierFlags.Export) == 0)
                    {
                        return false; // These are deferred and marked in a chain when referenced
                    }
                    if (NodeTests.IsImportDeclaration(n))
                    {
                        return false; // likewise, these are ultimately what get marked by calls on other nodes - we want to skip them
                    }
                    checker.MarkLinkedReferences(n, ReferenceHint.Unspecified, null /*propSymbol*/, null /*parentType*/);
                    n.ForEachChild(Visit);
                    return false;
                }
                file.ForEachChild(Visit);
            }
        }
    }

    // emitresolver.go:824-832
    public SourceFile? GetExternalModuleFileFromDeclaration(Node declaration)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(declaration))
        {
            return null;
        }
        lock (checkerMu)
        {
            return checker.GetExternalModuleFileFromDeclaration(declaration);
        }
    }

    // emitresolver.go:834-848
    private global::Ts.Binder.IReferenceResolver GetReferenceResolver()
    {
        if (referenceResolver is null)
        {
            referenceResolver = global::Ts.Binder.ReferenceResolver.NewReferenceResolver(
                checker.compilerOptions,
                new global::Ts.Binder.ReferenceResolverHooks
                {
                    ResolveName = checker.ResolveName,
                    GetResolvedSymbol = checker.GetResolvedSymbolOrNil,
                    GetMergedSymbol = checker.GetMergedSymbol,
                    GetParentOfSymbol = sym => sym is null ? null : checker.GetParentOfSymbol(sym),
                    GetSymbolOfDeclaration = checker.GetSymbolOfDeclaration,
                    GetTypeOnlyAliasDeclaration = checker.GetTypeOnlyAliasDeclarationEx,
                    GetExportSymbolOfValueSymbolIfExported = checker.GetExportSymbolOfValueSymbolIfExported,
                    GetElementAccessExpressionName = ReferenceResolverElementAccessName,
                });
        }
        return referenceResolver;
    }

    // Adapter: Go passes checker.tryGetElementAccessExpressionName (returns (string, bool)); the
    // hook shape is Func<ElementAccessExpression, (string Name, bool Ok)>.
    private (string Name, bool Ok) ReferenceResolverElementAccessName(ElementAccessExpression expression)
    {
        var ok = checker.TryGetElementAccessExpressionName(expression, out var name);
        return (name, ok);
    }

    // emitresolver.go:850-859
    public Node? GetReferencedExportContainer(Node node, bool prefixLocals)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return null;
        }
        lock (checkerMu)
        {
            return GetReferenceResolver().GetReferencedExportContainer(node, prefixLocals);
        }
    }

    // emitresolver.go:861-865
    public void SetReferencedImportDeclaration(IdentifierNode node, Declaration @ref)
    {
        lock (checkerMu)
        {
            jsxLinks.Get(node).importRef = @ref;
        }
    }

    // emitresolver.go:867-879
    public Node? GetReferencedImportDeclaration(Node node)
    {
        lock (checkerMu)
        {
            if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
            {
                return jsxLinks.Get(node).importRef;
            }

            var symbol = checker.GetReferencedValueOrAliasSymbol(node);
            if (AstUtilities.IsNonLocalAlias(symbol, SymbolFlags.Value) &&
                checker.GetTypeOnlyAliasDeclarationEx(symbol!, SymbolFlags.Value) is null)
            {
                return checker.GetDeclarationOfAliasSymbol(symbol!);
            }
            return null;
        }
    }

    // emitresolver.go:881-890
    public Node? GetReferencedValueDeclaration(Node node)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return null;
        }
        lock (checkerMu)
        {
            return GetReferenceResolver().GetReferencedValueDeclaration(node);
        }
    }

    // emitresolver.go:892-894
    public Declaration? GetReferencedValueDeclarationUnsafe(IdentifierNode node)
    {
        return GetReferenceResolver().GetReferencedValueDeclaration(node);
    }

    // emitresolver.go:896-905
    public Node[] GetReferencedValueDeclarations(Node node)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return [];
        }
        lock (checkerMu)
        {
            return GetReferenceResolver().GetReferencedValueDeclarations(node);
        }
    }

    // emitresolver.go:907-914 — IsNameResolvable returns true if the given name resolves to any
    // symbol at location.
    public bool IsNameResolvable(Node location, string name)
    {
        lock (checkerMu)
        {
            var symbol = checker.ResolveName(location, name,
                SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace,
                null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
            return symbol is not null;
        }
    }

    // emitresolver.go:916-925
    public string GetElementAccessExpressionName(ElementAccessExpression expression)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(expression.AsNode()))
        {
            return "";
        }
        lock (checkerMu)
        {
            return GetReferenceResolver().GetElementAccessExpressionName(expression);
        }
    }

    // emitresolver.go:927-936
    public Node? GetReferencedMemberValueDeclaration(Node node)
    {
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(node))
        {
            return null;
        }
        lock (checkerMu)
        {
            return GetReferenceResolver().GetReferencedMemberValueDeclaration(node);
        }
    }

    // emitresolver.go:1143-1148
    public ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags)
    {
        // node = emitContext.ParseNode(node)
        lock (checkerMu)
        {
            return checker.GetEffectiveDeclarationFlags(node, flags);
        }
    }

    // emitresolver.go:1150-1155
    public ResolutionMode GetResolutionModeOverride(Node node)
    {
        // node = emitContext.ParseNode(node)
        lock (checkerMu)
        {
            return checker.GetResolutionModeOverride(node.AsImportAttributes(), false);
        }
    }

    // emitresolver.go:1157-1162
    public object? GetConstantValue(Node node)
    {
        // node = emitContext.ParseNode(node)
        lock (checkerMu)
        {
            return checker.GetConstantValue(node);
        }
    }

    // emitresolver.go:1256-1269
    public Symbol[] GetPropertiesOfContainerFunction(Node node)
    {
        // This is explicitly _not locked_ because it is only called via error reporters invoked via
        // node builder calls to the symbol tracker already within locked contexts.
        if (node is null)
        {
            return [];
        }
        var s = checker.GetSymbolOfDeclaration(node);
        if (s is null)
        {
            return [];
        }
        return checker.GetPropertiesOfType(checker.GetTypeOfSymbol(s));
    }

    // emitresolver.go:1280-1322 — IsThisPropertyAssignmentDeclarationRedundant reports whether a JS
    // `this.<name> = ...` expando assignment should be omitted from declaration emit because the
    // member it would synthesize is already provided by an `extends` base type. (See Go doc comment.)
    public bool IsThisPropertyAssignmentDeclarationRedundant(Node node)
    {
        if (node is null)
        {
            return false;
        }

        lock (checkerMu)
        {
            var s = checker.GetSymbolOfDeclaration(node);
            if (s is null || s.Parent is null)
            {
                return false;
            }
            var parentType = checker.GetDeclaredTypeOfSymbol(s.Parent);
            if (parentType is null)
            {
                return false;
            }
            foreach (var @base in checker.GetBaseTypes(parentType))
            {
                var baseProp = checker.GetPropertyOfType(@base, s.Name);
                if (baseProp is null)
                {
                    continue;
                }
                if ((baseProp.Flags & (SymbolFlags.Accessor | SymbolFlags.Method | SymbolFlags.Function)) != 0)
                {
                    return true;
                }
                if (checker.IsReadonlySymbol(baseProp) == checker.IsReadonlySymbol(s) &&
                    (s.Flags & SymbolFlags.Optional) == (baseProp.Flags & SymbolFlags.Optional) &&
                    checker.IsTypeIdenticalTo(checker.GetTypeOfSymbol(s), checker.GetTypeOfSymbol(baseProp)))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // ── emitresolver.go:311-332 — helper (package-level in Go) ──────────────────────────────────

    private static SymbolFlags GetMeaningOfEntityNameReference(Node entityName)
    {
        // get symbol of the first identifier of the entityName
        if (entityName.Parent!.Kind == SyntaxKind.TypeQuery ||
            (entityName.Parent.Kind == SyntaxKind.ExpressionWithTypeArguments && !Checker.IsPartOfTypeNode(entityName.Parent)) ||
            entityName.Parent.Kind == SyntaxKind.ComputedPropertyName ||
            (entityName.Parent.Kind == SyntaxKind.TypePredicate &&
             entityName.Parent.AsTypePredicateNode().ParameterName == entityName) ||
            entityName.Parent.Kind == SyntaxKind.BinaryExpression)
        {
            // Typeof value
            return SymbolFlags.Value | SymbolFlags.ExportValue;
        }
        if (entityName.Kind == SyntaxKind.QualifiedName || entityName.Kind == SyntaxKind.PropertyAccessExpression ||
            entityName.Parent.Kind == SyntaxKind.ImportEqualsDeclaration ||
            (entityName.Parent.Kind == SyntaxKind.QualifiedName && entityName.Parent.AsQualifiedName().Left == entityName) ||
            (entityName.Parent.Kind == SyntaxKind.PropertyAccessExpression && entityName.Parent.Expression() == entityName) ||
            (entityName.Parent.Kind == SyntaxKind.ElementAccessExpression && entityName.Parent.Expression() == entityName))
        {
            // Left identifier from type reference or TypeAlias
            // Entity name of the import declaration
            return SymbolFlags.Namespace;
        }
        // Type Reference or TypeAlias entity = Identifier
        return SymbolFlags.Type;
    }

    // emitresolver.go:685-687
    private static bool IsConstEnumOrConstEnumOnlyModule(Symbol s)
    {
        return Checker.IsConstEnumSymbol(s) || (s.Flags & SymbolFlags.ConstEnumOnlyModule) != 0;
    }
}
