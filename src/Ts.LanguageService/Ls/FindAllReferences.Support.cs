// Ported from tsgo internal/ls/findallreferences.go (types + free-function helpers).
//
// Carries the result-type family (ReferenceEntry / SymbolAndEntries / Definition …),
// the settings types (refOptions / referenceUse) and the ls/utilities.go helpers the
// find-all-references engine leans on that no other Ls file hosts yet. Shared helpers
// already ported elsewhere are reused, not duplicated:
//   getContextNode / toContextRange / getRangeOfNode / getFileAndStartPosFromDeclaration
//     → SourceDefinitionDeps (Wave 5), getReferenceAtPosition / findReferenceInPosition /
//     isModuleSpecifierLike → SourceDefinitionDeps,
//   isLiteralNameOfPropertyDeclarationOrIndexAccess / isObjectBindingElementWithoutPropertyName
//     → LsDeps, getMeaningFromLocation / getIntersectingMeaningFromDeclarations /
//     getContainingObjectLiteralElement / getContainerNode → LsMeaning,
//   ClimbPastPropertyAccess / IsRightSideOfPropertyAccess → CallHierarchyAstHelpers.

using System;
using System.Collections.Generic;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerFindRefsLsExports / CheckerLsExports extension methods
using Ts.Collections; // Set<T>

namespace Ts.Ls;

// findallreferences.go:29-36 — referenceUse.
internal enum ReferenceUse
{
    None = 0,
    Other = 1,
    References = 2,
    Rename = 3,
}

// findallreferences.go:38-44 — refOptions.
internal sealed class RefOptions
{
    public bool FindInStrings;
    public bool FindInComments;
    public ReferenceUse Use; // other, references, rename
    public bool Implementations;
    public bool UseAliasesForRename = true; // renamed from providePrefixAndSuffixTextForRename. default: true
}

// findallreferences.go:71-80 — DefinitionKind.
public enum DefinitionKind
{
    Symbol = 0,
    Label = 1,
    Keyword = 2,
    This = 3,
    String = 4,
    TripleSlashReference = 5,
}

// findallreferences.go:82-91 — Definition / tripleSlashDefinition.
public sealed class FarDefinition
{
    public DefinitionKind Kind;
    public global::Ts.Ast.Symbol? Symbol;
    public Node? Node;
    internal TripleSlashDefinition? TripleSlashFileRef;
}

internal sealed record TripleSlashDefinition(global::Ts.Ast.FileReference? Reference, SourceFile File);

// findallreferences.go:93-102 — entryKind.
internal enum EntryKind
{
    None = 0,
    Range = 1,
    Node = 2,
    StringLiteral = 3,
    SearchedLocalFoundProperty = 4,
    SearchedPropertyFoundLocal = 5,
}

// findallreferences.go:104-111 — ReferenceEntry.
public sealed class ReferenceEntry
{
    internal EntryKind Kind;
    public Node? Node;
    public Node? Context; // !!! ContextWithStartAndEndNode, optional
    public string FileName = "";
    public global::Ts.Core.TextRange? TextRange;
    public global::Ts.LsProto.Location? LspRange;

    // findallreferences.go:114 — Node returns the AST node for this reference entry.
    public global::Ts.Ast.Node? NodeValue() => Node;

    // findallreferences.go:119 — IsNodeEntry.
    public bool IsNodeEntry() => Node != null;
}

// findallreferences.go:55-165 — SymbolAndEntries.
public sealed class SymbolAndEntries
{
    public FarDefinition? Definition;
    public List<ReferenceEntry> References;

    public SymbolAndEntries(FarDefinition? definition, List<ReferenceEntry> references)
    {
        Definition = definition;
        References = references;
    }

    // findallreferences.go:60 — NewSymbolAndEntries.
    public static SymbolAndEntries NewSymbolAndEntries(DefinitionKind kind, Node? node, global::Ts.Ast.Symbol? symbol, List<ReferenceEntry> references)
    {
        return new SymbolAndEntries(
            new FarDefinition
            {
                Kind = kind,
                Node = node,
                Symbol = symbol,
            },
            references);
    }

    // findallreferences.go:124 — References.
    public List<ReferenceEntry> ReferencesList() => References;

    // findallreferences.go:129 — DefinitionNode.
    public Node? DefinitionNode()
    {
        if (Definition == null)
        {
            return null;
        }
        if (Definition.Node != null)
        {
            return Definition.Node;
        }
        if (Definition.Symbol != null && (Definition.Symbol.Declarations?.Length ?? 0) > 0)
        {
            return Definition.Symbol.Declarations![0];
        }
        return null;
    }

    // findallreferences.go:142 — DefinitionSymbol.
    public global::Ts.Ast.Symbol? DefinitionSymbol()
    {
        if (Definition == null)
        {
            return null;
        }
        return Definition.Symbol;
    }

    // findallreferences.go:149 — canUseDefinitionSymbol.
    public bool CanUseDefinitionSymbol()
    {
        if (Definition == null)
        {
            return false;
        }

        switch (Definition.Kind)
        {
            case DefinitionKind.Symbol or DefinitionKind.This:
                return Definition.Symbol != null;
            case DefinitionKind.TripleSlashReference:
                // !!! TODO : need to find file reference instead?
                // May need to return true to indicate this to be file search instead and might need to do for import stuff as well
                // For now
                return false;
            default:
                return false;
        }
    }
}

// findallreferences.go:1102-1105 — SignatureUsage.
public sealed record SignatureUsage(Node Name, Node? Call);

// Free-function helpers (findallreferences.go + ls/utilities.go), hosted as a static
// class so the LanguageService partials stay Go-shaped.
internal static class FindRefsSupport
{
    // findallreferences.go:193-206 — newNodeEntryWithKind / newNodeEntry.
    internal static ReferenceEntry NewNodeEntryWithKind(Node node, EntryKind kind)
    {
        var e = NewNodeEntry(node);
        e.Kind = kind;
        return e;
    }

    internal static ReferenceEntry NewNodeEntry(Node node)
    {
        // creates nodeEntry with `kind == entryKindNode`
        return new ReferenceEntry
        {
            Kind = EntryKind.Node,
            Node = node.Name() ?? node,
            Context = GetContextNodeForNodeEntry(node),
        };
    }

    // findallreferences.go:208-269 — getContextNodeForNodeEntry.
    internal static Node? GetContextNodeForNodeEntry(Node node)
    {
        if (AstUtilities.IsDeclaration(node))
        {
            return SourceDefinitionDeps.GetContextNode(node);
        }

        if (node.Parent == null)
        {
            return null;
        }

        if (!AstUtilities.IsDeclaration(node.Parent) && !NodeTests.IsExportAssignment(node.Parent))
        {
            // Special property assignment in javascript
            if (AstUtilities.IsInJSFile(node))
            {
                // !!! jsdoc: check if branch still needed
                Node? binaryExpression = null;
                if (NodeTests.IsBinaryExpression(node.Parent))
                {
                    binaryExpression = node.Parent;
                }
                else if (AstUtilities.IsAccessExpression(node.Parent) && NodeTests.IsBinaryExpression(node.Parent.Parent!) && node.Parent.Parent!.AsBinaryExpression().Left == node.Parent)
                {
                    binaryExpression = node.Parent.Parent;
                }
                if (binaryExpression != null && AstUtilities.GetAssignmentDeclarationKind(binaryExpression) != JSDeclarationKind.None)
                {
                    return SourceDefinitionDeps.GetContextNode(binaryExpression);
                }
            }

            // Jsx Tags
            switch (node.Parent.Kind)
            {
                case SyntaxKind.JsxOpeningElement or SyntaxKind.JsxClosingElement:
                    return node.Parent.Parent;
                case SyntaxKind.JsxSelfClosingElement or SyntaxKind.LabeledStatement or SyntaxKind.BreakStatement or SyntaxKind.ContinueStatement:
                    return node.Parent;
                case SyntaxKind.StringLiteral or SyntaxKind.NoSubstitutionTemplateLiteral:
                    if (LsDeps.TryGetImportFromModuleSpecifier(node) is { } validImport)
                    {
                        var declOrStatement = AstUtilities.FindAncestor(validImport, n =>
                            AstUtilities.IsDeclaration(node) || AstUtilities.IsStatement(node) || AstUtilities.IsJSDocTag(node));
                        if (declOrStatement != null && AstUtilities.IsDeclaration(declOrStatement))
                        {
                            return SourceDefinitionDeps.GetContextNode(declOrStatement);
                        }
                        return declOrStatement;
                    }
                    break;
            }

            // Handle computed property name
            var propertyName = AstUtilities.FindAncestor(node, NodeTests.IsComputedPropertyName);
            if (propertyName != null)
            {
                return SourceDefinitionDeps.GetContextNode(propertyName.Parent);
            }
            return null;
        }

        if (node.Parent.Name() == node || // node is name of declaration, use parent
            node.Parent.Kind == SyntaxKind.Constructor ||
            node.Parent.Kind == SyntaxKind.ExportAssignment ||
            // Property name of the import export specifier or binding pattern, use parent
            ((AstUtilities.IsImportOrExportSpecifier(node.Parent) || node.Parent.Kind == SyntaxKind.BindingElement) && node.Parent.PropertyName() == node) ||
            // Is default export
            (node.Kind == SyntaxKind.DefaultKeyword && AstUtilities.HasSyntacticModifier(node.Parent, ModifierFlags.ExportDefault)))
        {
            return SourceDefinitionDeps.GetContextNode(node.Parent);
        }

        return null;
    }

    // ls/utilities.go:190 — isNameOfModuleDeclaration.
    internal static bool IsNameOfModuleDeclaration(Node node)
    {
        if (node.Parent!.Kind != SyntaxKind.ModuleDeclaration)
        {
            return false;
        }
        return node.Parent.Name() == node;
    }

    // ls/utilities.go:197 — isExpressionOfExternalModuleImportEqualsDeclaration. The guard
    // is ast.IsExternalModuleImportEqualsDeclaration (ast/utilities.go:1307: kind check
    // before the cast), NOT ls.isExternalModuleImportEquals (importTracker.go:662, which
    // relies on its callers to kind-check). FIX: the port previously called
    // ImportTrackerSupport.IsExternalModuleImportEquals here, which hard-casts and throws
    // InvalidCastException on non-ImportEqualsDeclaration parents (hit by any find-
    // references over a StringLiteral whose Parent.Parent is another node kind).
    internal static bool IsExpressionOfExternalModuleImportEqualsDeclaration(Node node)
    {
        return node.Parent!.Kind == global::Ts.Ast.SyntaxKind.ImportEqualsDeclaration &&
            node.Parent!.AsImportEqualsDeclaration().ModuleReference!.Kind == global::Ts.Ast.SyntaxKind.ExternalModuleReference &&
            node.Parent!.AsImportEqualsDeclaration().ModuleReference!.Expression() == node;
    }

    // ls/utilities.go:239 — isThis.
    internal static bool IsThis(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ThisKeyword:
                // case ast.KindThisType: TODO: GH#9267
                return true;
            case SyntaxKind.Identifier:
                // 'this' as a parameter
                return node.Text() == "this" && node.Parent!.Kind == SyntaxKind.Parameter;
            default:
                return false;
        }
    }

    // ls/utilities.go:319-344 — typeKeywords + isTypeKeyword.
    private static readonly HashSet<SyntaxKind> TypeKeywords =
    [
        SyntaxKind.AnyKeyword,
        SyntaxKind.BooleanKeyword,
        SyntaxKind.NeverKeyword,
        SyntaxKind.NumberKeyword,
        SyntaxKind.ObjectKeyword,
        SyntaxKind.StringKeyword,
        SyntaxKind.SymbolKeyword,
        SyntaxKind.TypeOfKeyword,
        SyntaxKind.TrueKeyword,
        SyntaxKind.VoidKeyword,
        SyntaxKind.UndefinedKeyword,
        SyntaxKind.UniqueKeyword,
        SyntaxKind.UnknownKeyword,
    ];

    internal static bool IsTypeKeyword(SyntaxKind kind) => TypeKeywords.Contains(kind);

    // ls/utilities.go:383 — isStaticSymbol.
    internal static bool IsStaticSymbol(global::Ts.Ast.Symbol symbol)
    {
        if (symbol.ValueDeclaration == null)
        {
            return false;
        }
        var modifierFlags = symbol.ValueDeclaration.ModifierFlags();
        return (modifierFlags & ModifierFlags.Static) != 0;
    }

    // ls/utilities.go:391 — isImplementation.
    internal static bool IsImplementation(Node node)
    {
        if ((node.Flags & NodeFlags.Ambient) != 0)
        {
            return !(node.Kind == SyntaxKind.InterfaceDeclaration || node.Kind == SyntaxKind.TypeAliasDeclaration);
        }
        if (AstUtilities.IsVariableLike(node))
        {
            return AstUtilities.HasInitializer(node);
        }
        if (AstUtilities.IsFunctionLikeDeclaration(node))
        {
            return node.Body() != null;
        }
        return AstUtilities.IsClassLike(node) || AstUtilities.IsModuleOrEnumDeclaration(node);
    }

    // ls/utilities.go:404 — isImplementationExpression.
    internal static bool IsImplementationExpression(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return IsImplementationExpression(node.Expression()!);
            case SyntaxKind.ArrowFunction or SyntaxKind.FunctionExpression or SyntaxKind.ObjectLiteralExpression or SyntaxKind.ClassExpression or SyntaxKind.ArrayLiteralExpression:
                return true;
            default:
                return false;
        }
    }

    // ls/utilities.go:415 — isReadonlyTypeOperator.
    internal static bool IsReadonlyTypeOperator(Node node)
    {
        return node.Kind == SyntaxKind.ReadonlyKeyword && node.Parent!.Kind == SyntaxKind.TypeOperator && node.Parent!.AsTypeOperatorNode().Operator == SyntaxKind.ReadonlyKeyword;
    }

    // ls/utilities.go:431 — getContainingNodeIfInHeritageClause.
    internal static Node? GetContainingNodeIfInHeritageClause(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        if (node.Kind == SyntaxKind.Identifier || node.Kind == SyntaxKind.PropertyAccessExpression)
        {
            return GetContainingNodeIfInHeritageClause(node.Parent);
        }
        if (node.Kind == SyntaxKind.ExpressionWithTypeArguments && (AstUtilities.IsClassLike(node.Parent!.Parent!) || node.Parent!.Parent!.Kind == SyntaxKind.InterfaceDeclaration))
        {
            return node.Parent!.Parent;
        }
        return null;
    }

    // ls/utilities.go:1004 — getTargetLabel.
    internal static Node? GetTargetLabel(Node? referenceNode, string labelName)
    {
        // todo: rewrite as `ast.FindAncestor`
        while (referenceNode != null)
        {
            if (referenceNode.Kind == SyntaxKind.LabeledStatement && referenceNode.Label()!.Text() == labelName)
            {
                return referenceNode.Label();
            }
            referenceNode = referenceNode.Parent;
        }
        return null;
    }

    // ls/utilities.go:61 — getNonModuleSymbolOfMergedModuleSymbol.
    internal static global::Ts.Ast.Symbol? GetNonModuleSymbolOfMergedModuleSymbol(global::Ts.Ast.Symbol symbol)
    {
        if ((symbol.Declarations?.Length ?? 0) == 0 || (symbol.Flags & (SymbolFlags.Module | SymbolFlags.Transient)) == 0)
        {
            return null;
        }

        Node? decl = null;
        foreach (var d in symbol.Declarations!)
        {
            if (!NodeTests.IsSourceFile(d) && !NodeTests.IsModuleDeclaration(d))
            {
                decl = d;
                break;
            }
        }
        return decl?.Symbol();
    }

    // ls/utilities.go:74 — getLocalSymbolForExportSpecifier.
    internal static global::Ts.Ast.Symbol GetLocalSymbolForExportSpecifier(Node referenceLocation, global::Ts.Ast.Symbol referenceSymbol, Node exportSpecifier, global::Ts.Checker.Checker ch)
    {
        if (IsExportSpecifierAlias(referenceLocation, exportSpecifier))
        {
            if (ch.GetExportSpecifierLocalTargetSymbol(exportSpecifier) is { } symbol)
            {
                return symbol;
            }
        }
        return referenceSymbol;
    }

    // ls/utilities.go:84 — isExportSpecifierAlias.
    internal static bool IsExportSpecifierAlias(Node referenceLocation, Node exportSpecifier)
    {
        global::Ts.Core.Debug.Assert(exportSpecifier.AsExportSpecifier().PropertyName == referenceLocation || exportSpecifier.Name() == referenceLocation, "referenceLocation is not export specifier name or property name");
        var propertyName = exportSpecifier.AsExportSpecifier().PropertyName;
        if (propertyName != null)
        {
            // Given `export { foo as bar } [from "someModule"]`: It's an alias at `foo`, but at `bar` it's a new symbol.
            return propertyName == referenceLocation;
        }
        else
        {
            // `export { foo } from "foo"` is a re-export.
            // `export { foo };` is not a re-export, it creates an alias for the local variable `foo`.
            return exportSpecifier.Parent!.Parent!.ModuleSpecifier() == null;
        }
    }

    // ls/utilities.go:915 — getAllSuperTypeNodes.
    internal static List<Node> GetAllSuperTypeNodes(Node node)
    {
        if (NodeTests.IsInterfaceDeclaration(node))
        {
            var elements = AstUtilities.GetHeritageElements(node, SyntaxKind.ExtendsKeyword);
            return elements != null ? new List<Node>(elements) : [];
        }
        if (AstUtilities.IsClassLike(node))
        {
            var result = new List<Node>();
            if (AstUtilities.GetClassExtendsHeritageElement(node) is { } extendsElement)
            {
                result.Add(extendsElement); // core.SingleElementSlice
            }
            result.AddRange(AstUtilities.GetImplementsTypeNodes(node));
            return result;
        }
        return [];
    }

    // ls/utilities.go:934 — getParentSymbolsOfPropertyAccess.
    internal static List<global::Ts.Ast.Symbol> GetParentSymbolsOfPropertyAccess(Node location, global::Ts.Ast.Symbol symbol, global::Ts.Checker.Checker ch)
    {
        if (!CallHierarchyAstHelpers.IsRightSideOfPropertyAccess(location))
        {
            return [];
        }
        var lhsType = ch.GetTypeAtLocation(location.Parent!.Expression()!);
        if (lhsType == null)
        {
            return [];
        }
        List<global::Ts.Checker.Type> possibleSymbols;
        if ((lhsType.Flags & TypeFlags.UnionOrIntersection) != 0)
        {
            possibleSymbols = new List<global::Ts.Checker.Type>(lhsType.Types());
        }
        else if (lhsType.Symbol != symbol.Parent)
        {
            possibleSymbols = [lhsType];
        }
        else
        {
            possibleSymbols = [];
        }
        var result = new List<global::Ts.Ast.Symbol>();
        foreach (var t in possibleSymbols)
        {
            if (t.Symbol != null && (t.Symbol!.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
            {
                result.Add(t.Symbol!);
            }
        }
        return result;
    }

    // ls/utilities.go:956 — getPropertySymbolsFromBaseTypes.
    internal static global::Ts.Ast.Symbol? GetPropertySymbolsFromBaseTypes(global::Ts.Ast.Symbol symbol, string propertyName, global::Ts.Checker.Checker checker, Func<global::Ts.Ast.Symbol, global::Ts.Ast.Symbol?> cb)
    {
        var seen = new Set<global::Ts.Ast.Symbol>();

        global::Ts.Ast.Symbol? Recur(global::Ts.Ast.Symbol symbol)
        {
            // Use `addToSeen` to ensure we don't infinitely recurse in this situation:
            //      interface C extends C {
            //          /*findRef*/propName: string;
            //      }
            if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) == 0 || !seen.AddIfAbsent(symbol))
            {
                return null;
            }
            foreach (var declaration in symbol.Declarations ?? [])
            {
                foreach (var typeReference in GetAllSuperTypeNodes(declaration))
                {
                    var propertyType = checker.GetTypeAtLocation(typeReference);
                    if (propertyType != null && propertyType.Symbol != null)
                    {
                        // Visit the typeReference as well to see if it directly or indirectly uses that property
                        if (checker.GetPropertyOfType(propertyType, propertyName) is { } propertySymbol)
                        {
                            foreach (var rootSymbol in checker.GetRootSymbols(propertySymbol))
                            {
                                if (cb(rootSymbol) is { } result)
                                {
                                    return result;
                                }
                            }
                        }
                        if (Recur(propertyType.Symbol!) is { } recurseResult)
                        {
                            return recurseResult;
                        }
                    }
                }
            }
            return null;
        }

        return Recur(symbol);
    }

    // ls/utilities.go:989 — getPropertySymbolFromBindingElement.
    internal static global::Ts.Ast.Symbol? GetPropertySymbolFromBindingElement(global::Ts.Checker.Checker checker, Node bindingElement)
    {
        var typeOfPattern = checker.GetTypeAtLocation(bindingElement.Parent!);
        if (typeOfPattern != null)
        {
            return checker.GetPropertyOfType(typeOfPattern, bindingElement.Name()!.Text());
        }
        return null;
    }

    // ls/utilities.go:996 — getPropertySymbolOfObjectBindingPatternWithoutPropertyName.
    internal static global::Ts.Ast.Symbol? GetPropertySymbolOfObjectBindingPatternWithoutPropertyName(global::Ts.Ast.Symbol symbol, global::Ts.Checker.Checker checker)
    {
        var bindingElement = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.BindingElement);
        if (bindingElement != null && LsDeps.IsObjectBindingElementWithoutPropertyName(bindingElement))
        {
            return GetPropertySymbolFromBindingElement(checker, bindingElement);
        }
        return null;
    }

    // ls/utilities.go:1420 — isSourceFileWithGlobalExports.
    internal static bool IsSourceFileWithGlobalExports(Node? node)
    {
        return node != null && NodeTests.IsSourceFile(node) && node.AsSourceFile().GlobalExports != null;
    }

    // findallreferences.go:372-374 — isForRenameWithPrefixAndSuffixText.
    internal static bool IsForRenameWithPrefixAndSuffixText(RefOptions options)
    {
        return options.Use == ReferenceUse.Rename && options.UseAliasesForRename;
    }

    // findallreferences.go:376-399 — skipPastExportOrImportSpecifierOrUnion.
    internal static global::Ts.Ast.Symbol? SkipPastExportOrImportSpecifierOrUnion(global::Ts.Ast.Symbol symbol, Node? node, global::Ts.Checker.Checker checker, bool useLocalSymbolForExportSpecifier)
    {
        if (node == null)
        {
            return null;
        }
        var parent = node.Parent!;
        if (parent.Kind == SyntaxKind.ExportSpecifier && useLocalSymbolForExportSpecifier)
        {
            return GetLocalSymbolForExportSpecifier(node, symbol, parent, checker);
        }
        // If the symbol is declared as part of a declaration like `{ type: "a" } | { type: "b" }`, use the property on the union type to get more references.
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (decl.Parent == null)
            {
                // Ignore UMD module and global merge and CJS module end exports symbols
                if ((symbol.Flags & (SymbolFlags.Transient | SymbolFlags.ModuleExports)) != 0)
                {
                    continue;
                }
                // Assertions for GH#21814. We should be handling SourceFile symbols in `getReferencedSymbolsForModule` instead of getting here.
                // Go panic; InvalidOperationException is the repository's panic mapping.
                throw new InvalidOperationException("Unexpected symbol at " + node.Kind + ": " + symbol.Name);
            }
            if (decl.Parent!.Kind == SyntaxKind.TypeLiteral && decl.Parent!.Parent!.Kind == SyntaxKind.UnionType)
            {
                if (checker.GetPropertyOfType(checker.GetTypeFromTypeNode(decl.Parent!.Parent!), symbol.Name) is { } property)
                {
                    return property; // core.FirstNonNil
                }
            }
        }
        return null;
    }

    // findallreferences.go:401-470 — getSymbolScope.
    internal static Node? GetSymbolScope(global::Ts.Ast.Symbol symbol)
    {
        // If this is the symbol of a named function expression or named class expression,
        // then named references are limited to its own scope.
        var valueDeclaration = symbol.ValueDeclaration;
        if (valueDeclaration != null && (valueDeclaration.Kind == SyntaxKind.FunctionExpression || valueDeclaration.Kind == SyntaxKind.ClassExpression))
        {
            return valueDeclaration;
        }

        if ((symbol.Declarations?.Length ?? 0) == 0)
        {
            return null;
        }

        var declarations = symbol.Declarations!;
        // If this is private property or method, the scope is the containing class
        if ((symbol.Flags & (SymbolFlags.Property | SymbolFlags.Method)) != 0)
        {
            Node? privateDeclaration = null;
            foreach (var d in declarations)
            {
                if (AstUtilities.HasSyntacticModifier(d, ModifierFlags.Private) || AstUtilities.IsPrivateIdentifierClassElementDeclaration(d))
                {
                    privateDeclaration = d;
                    break;
                }
            }
            if (privateDeclaration != null)
            {
                return AstUtilities.FindAncestorKind(privateDeclaration, SyntaxKind.ClassDeclaration);
            }
            // Else this is a public property and could be accessed from anywhere.
            return null;
        }

        // If symbol is of object binding pattern element without property name we would want to
        // look for property too and that could be anywhere
        foreach (var d in declarations)
        {
            if (LsDeps.IsObjectBindingElementWithoutPropertyName(d))
            {
                return null;
            }
        }

        /*
            If the symbol has a parent, it's globally visible unless:
            - It's a private property (handled above).
            - It's a type parameter.
            - The parent is an external module: then we should only search in the module (and recurse on the export later).
            - But if the parent has `export as namespace`, the symbol is globally visible through that namespace.
        */
        var exposedByParent = symbol.Parent != null && (symbol.Flags & SymbolFlags.TypeParameter) == 0;
        if (exposedByParent && !(global::Ts.Checker.Checker.IsExternalModuleSymbol(symbol.Parent!) && !IsSourceFileWithGlobalExports(symbol.Parent!.ValueDeclaration)))
        {
            return null;
        }

        Node? scope = null;
        foreach (var declaration in declarations)
        {
            var container = LsMeaning.GetContainerNode(declaration);
            if (scope != null && scope != container)
            {
                // Different declarations have different containers, bail out
                return null;
            }

            if (container == null || (container.Kind == SyntaxKind.SourceFile && !AstUtilities.IsExternalOrCommonJSModule(container.AsSourceFile())))
            {
                // This is a global variable and not an external module, any declaration defined
                // within this scope is visible outside the file
                return null;
            }

            scope = container;
        }

        // If symbol.parent, this means we are in an export of an external module. (Otherwise we would have returned `undefined` above.)
        // For an export of a module, we may be in a declaration file, and it may be accessed elsewhere. E.g.:
        //     declare module "a" { export type T = number; }
        //     declare module "b" { import { T } from "a"; export const x: T; }
        // So we must search the whole source file. (Because we will mark the source file as seen, we we won't return to it when searching for imports.)
        if (exposedByParent)
        {
            return AstUtilities.GetSourceFileOfNode(scope!)!.AsNode();
        }
        return scope; // TODO: GH#18217
    }

    // findallreferences.go:545-582 — isDefinitionVisible.
    // This is special handling to determine if we should load up more projects and find location in other projects
    // By default arrows (and such other ast kinds) are not visible as declaration emitter doesnt need them
    // But we want to handle them specially so that they are visible if their parent is visible
    internal static bool IsDefinitionVisible(global::Ts.Checker.EmitResolver emitResolver, Node declaration)
    {
        if (emitResolver.IsDeclarationVisible(declaration))
        {
            return true;
        }
        if (declaration.Parent == null)
        {
            return false;
        }

        // Variable initializers are visible if variable is visible
        if (AstUtilities.HasInitializer(declaration.Parent) && declaration.Parent.Initializer() == declaration)
        {
            return IsDefinitionVisible(emitResolver, declaration.Parent);
        }

        // Handle some exceptions here like arrow function, members of class and object literal expression which are technically not visible but we want the definition to be determined by its parent
        switch (declaration.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.MethodDeclaration:
                // Private/protected properties/methods are not visible
                if (AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Private) || NodeTests.IsPrivateIdentifier(declaration.Name()!))
                {
                    return false;
                }
                // Public properties/methods are visible if its parents are visible, so:
                // falls through
                goto case SyntaxKind.Constructor;
            case SyntaxKind.Constructor:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.FunctionExpression:
                return IsDefinitionVisible(emitResolver, declaration.Parent);
            default:
                return false;
        }
    }

    // findallreferences.go:1584-1608 — findFirstJsxNode: recursively searches for the first
    // JSX element, self-closing element, or fragment.
    internal static Node? FindFirstJsxNode(Node root)
    {
        Node? Visit(Node node)
        {
            // Check if this is a JSX node we're looking for
            switch (node.Kind)
            {
                case SyntaxKind.JsxElement or SyntaxKind.JsxSelfClosingElement or SyntaxKind.JsxFragment:
                    return node;
            }

            // Skip subtree if it doesn't contain JSX
            if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsJsx) == 0)
            {
                return null;
            }

            // Traverse children to find JSX node
            Node? result = null;
            node.ForEachChild(child =>
            {
                result = Visit(child);
                return result != null; // Stop if found
            });
            return result;
        }

        return Visit(root);
    }

    // findallreferences.go:1730-1746 — getSpecialSearchKind.
    internal static string GetSpecialSearchKind(Node? node)
    {
        if (node == null)
        {
            return "none";
        }
        switch (node.Kind)
        {
            case SyntaxKind.Constructor or SyntaxKind.ConstructorKeyword:
                return "constructor";
            case SyntaxKind.Identifier:
                if (AstUtilities.IsClassLike(node.Parent!))
                {
                    global::Ts.Core.Debug.Assert(node.Parent!.Name() == node);
                    return "class";
                }
                goto default;
            default:
                return "none";
        }
    }

    // findallreferences.go:1907-1920 — getReferenceEntriesForShorthandPropertyAssignment.
    internal static void GetReferenceEntriesForShorthandPropertyAssignment(Node node, global::Ts.Checker.Checker checker, Action<Node> addReference)
    {
        var refSymbol = checker.GetSymbolAtLocation(node);
        if (refSymbol == null || refSymbol.ValueDeclaration == null)
        {
            return;
        }
        var shorthandSymbol = checker.GetShorthandAssignmentValueSymbol(refSymbol.ValueDeclaration);
        if (shorthandSymbol != null && (shorthandSymbol.Declarations?.Length ?? 0) > 0)
        {
            foreach (var declaration in shorthandSymbol.Declarations!)
            {
                if ((LsMeaning.GetMeaningFromDeclaration(declaration) & SemanticMeaning.Value) != 0)
                {
                    addReference(declaration);
                }
            }
        }
    }

    // findallreferences.go:1922-1924 — isMethodOrAccessor.
    internal static bool IsMethodOrAccessor(Node node)
    {
        return node.Kind == SyntaxKind.MethodDeclaration || node.Kind == SyntaxKind.GetAccessor || node.Kind == SyntaxKind.SetAccessor;
    }

    // findallreferences.go:1926-1928 — tryGetClassByExtendingIdentifier.
    internal static Node? TryGetClassByExtendingIdentifier(Node node)
    {
        return AstUtilities.TryGetClassExtendingExpressionWithTypeArguments(CallHierarchyAstHelpers.ClimbPastPropertyAccess(node).Parent!);
    }

    // findallreferences.go:1930-1935 — getClassConstructorSymbol.
    internal static global::Ts.Ast.Symbol? GetClassConstructorSymbol(global::Ts.Ast.Symbol classSymbol)
    {
        if (classSymbol.Members == null)
        {
            return null;
        }
        return classSymbol.Members.GetValueOrDefault(global::Ts.Ast.InternalSymbolName.Constructor);
    }

    // findallreferences.go:1937-1939 — hasOwnConstructor.
    internal static bool HasOwnConstructor(Node classDeclaration)
    {
        return GetClassConstructorSymbol(classDeclaration.Symbol()!) != null;
    }

    // findallreferences.go:1941-1968 — findOwnConstructorReferences.
    internal static void FindOwnConstructorReferences(global::Ts.Ast.Symbol classSymbol, SourceFile sourceFile, Action<Node> addNode)
    {
        var constructorSymbol = GetClassConstructorSymbol(classSymbol);
        if (constructorSymbol != null && (constructorSymbol.Declarations?.Length ?? 0) > 0)
        {
            foreach (var decl in constructorSymbol.Declarations!)
            {
                if (decl.Kind == SyntaxKind.Constructor)
                {
                    if (global::Ts.AstNav.Tokens.FindChildOfKind(decl, SyntaxKind.ConstructorKeyword, sourceFile) is { } ctrKeyword)
                    {
                        addNode(ctrKeyword);
                    }
                }
            }
        }

        if (classSymbol.Exports != null)
        {
            foreach (var member in classSymbol.Exports.Values)
            {
                var decl = member.ValueDeclaration;
                if (decl != null && decl.Kind == SyntaxKind.MethodDeclaration)
                {
                    var body = decl.Body();
                    if (body != null)
                    {
                        ForEachDescendantOfKind(body, SyntaxKind.ThisKeyword, thisKeyword =>
                        {
                            if (IsNewOrCallExpressionTarget(thisKeyword, isNew: true))
                            {
                                addNode(thisKeyword);
                            }
                        });
                    }
                }
            }
        }
    }

    // findallreferences.go:1970-1988 — findSuperConstructorAccesses.
    internal static void FindSuperConstructorAccesses(Node classDeclaration, Action<Node> addNode)
    {
        var constructorSymbol = GetClassConstructorSymbol(classDeclaration.Symbol()!);
        if (constructorSymbol == null || (constructorSymbol.Declarations?.Length ?? 0) == 0)
        {
            return;
        }

        foreach (var decl in constructorSymbol.Declarations!)
        {
            if (decl.Kind == SyntaxKind.Constructor)
            {
                var body = decl.Body();
                if (body != null)
                {
                    ForEachDescendantOfKind(body, SyntaxKind.SuperKeyword, node =>
                    {
                        if (IsNewOrCallExpressionTarget(node, isNew: false))
                        {
                            addNode(node);
                        }
                    });
                }
            }
        }
    }

    // findallreferences.go:1990-1998 — forEachDescendantOfKind.
    internal static void ForEachDescendantOfKind(Node node, SyntaxKind kind, Action<Node> action)
    {
        node.ForEachChild(child =>
        {
            if (child.Kind == kind)
            {
                action(child);
            }
            ForEachDescendantOfKind(child, kind, action);
            return false;
        });
    }

    // ast/utilities.go:3628/3632 — IsCallExpressionTarget / IsNewExpressionTarget with the
    // (includeElementAccess=false, skipPastOuterExpressions=false) argument shape every
    // findallreferences call site uses; isCalleeWorker reduced accordingly (no ast port yet).
    internal static bool IsNewOrCallExpressionTarget(Node node, bool isNew)
    {
        var target = CallHierarchyAstHelpers.ClimbPastPropertyAccess(node);
        return target != null && target.Parent != null &&
            (isNew ? NodeTests.IsNewExpression(target.Parent) : NodeTests.IsCallExpression(target.Parent)) &&
            (isNew ? target.Parent.AsNewExpression().Expression : target.Parent.AsCallExpression().Expression) == target;
    }

    // findallreferences.go:976-998 — isDeclarationOfSymbol.
    internal static bool IsDeclarationOfSymbol(Node? node, global::Ts.Ast.Symbol? target)
    {
        if (node == null || target == null)
        {
            return false;
        }

        Node? source;
        if (LsDeps.GetDeclarationFromName(node) is { } decl)
        {
            source = decl;
        }
        else if (node.Kind == SyntaxKind.DefaultKeyword)
        {
            source = node.Parent;
        }
        else if (AstUtilities.IsLiteralComputedPropertyDeclarationName(node))
        {
            source = node.Parent!.Parent;
        }
        else if (node.Kind == SyntaxKind.ConstructorKeyword && NodeTests.IsConstructorDeclaration(node.Parent!))
        {
            source = node.Parent!.Parent;
        }
        else
        {
            source = null;
        }

        // !!!
        // const commonjsSource = source && isBinaryExpression(source) ? source.left as unknown as Declaration : undefined;

        if (source == null)
        {
            return false;
        }
        foreach (var d in target.Declarations ?? [])
        {
            if (d == source)
            {
                return true;
            }
        }
        return false;
    }
}
