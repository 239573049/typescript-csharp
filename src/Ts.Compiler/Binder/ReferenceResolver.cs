// Ported from tsgo internal/binder/referenceresolver.go.
using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Diagnostics;

namespace Ts.Binder;

public interface IReferenceResolver
{
    Node? GetReferencedExportContainer(Node node, bool prefixLocals);
    Node? GetReferencedImportDeclaration(Node node);
    Node? GetReferencedValueDeclaration(Node node);
    Node[] GetReferencedValueDeclarations(Node node);
    string GetElementAccessExpressionName(ElementAccessExpression expression);
    Node? GetReferencedMemberValueDeclaration(Node node);
}

public sealed class ReferenceResolverHooks
{
    public Func<Node?, string, SymbolFlags, Message?, bool, bool, Symbol?>? ResolveName;
    public Func<Node, Symbol?>? GetResolvedSymbol;
    public Func<Symbol?, Symbol?>? GetMergedSymbol;
    public Func<Symbol?, Symbol?>? GetParentOfSymbol;
    public Func<Node, Symbol?>? GetSymbolOfDeclaration;
    public Func<Symbol, SymbolFlags, Node?>? GetTypeOnlyAliasDeclaration;
    public Func<Symbol?, Symbol?>? GetExportSymbolOfValueSymbolIfExported;
    public Func<ElementAccessExpression, (string Name, bool Ok)>? GetElementAccessExpressionName;
}

public sealed class ReferenceResolver : IReferenceResolver
{
    private NameResolver? resolver;
    private readonly CompilerOptions? options;
    private ReferenceResolverHooks hooks;

    public ReferenceResolver(CompilerOptions? options, ReferenceResolverHooks hooks)
    {
        this.options = options;
        this.hooks = hooks;
    }

    public static IReferenceResolver NewReferenceResolver(CompilerOptions? options, ReferenceResolverHooks hooks)
    {
        return new ReferenceResolver(options, hooks);
    }

    private Symbol? GetResolvedSymbol(Node? node)
    {
        if (node != null)
        {
            if (hooks.GetResolvedSymbol != null)
            {
                return hooks.GetResolvedSymbol(node);
            }
        }
        return null;
    }

    private Symbol? GetMergedSymbol(Symbol? symbol)
    {
        if (symbol != null)
        {
            if (hooks.GetMergedSymbol != null)
            {
                return hooks.GetMergedSymbol(symbol);
            }
            return symbol;
        }
        return null;
    }

    private Symbol? GetParentOfSymbol(Symbol? symbol)
    {
        if (symbol != null)
        {
            if (hooks.GetParentOfSymbol != null)
            {
                return hooks.GetParentOfSymbol(symbol);
            }
            return symbol.Parent;
        }
        return null;
    }

    private Symbol? GetSymbolOfDeclaration(Node? declaration)
    {
        if (declaration != null)
        {
            if (hooks.GetSymbolOfDeclaration != null)
            {
                return hooks.GetSymbolOfDeclaration(declaration);
            }
            return declaration.Symbol();
        }
        return null;
    }

    private Symbol? GetReferencedValueSymbol(Node reference, bool startInDeclarationContainer)
    {
        var resolvedSymbol = GetResolvedSymbol(reference);
        if (resolvedSymbol != null)
        {
            return resolvedSymbol;
        }

        var location = reference;
        if (startInDeclarationContainer && reference.Parent != null && AstUtilities.IsDeclaration(reference.Parent) && reference.Parent.Name() == reference)
        {
            location = AstUtilities.GetDeclarationContainer(reference.Parent);
        }

        if (hooks.ResolveName != null)
        {
            return hooks.ResolveName(location, reference.Text(), SymbolFlags.ExportValue | SymbolFlags.Value | SymbolFlags.Alias, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
        }

        if (resolver == null)
        {
            resolver = new NameResolver
            {
                CompilerOptions = options!,
            };
        }

        return resolver.Resolve(location, reference.Text(), SymbolFlags.ExportValue | SymbolFlags.Value | SymbolFlags.Alias, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
    }

    private bool IsTypeOnlyAliasDeclaration(Symbol? symbol)
    {
        if (symbol != null)
        {
            if (hooks.GetTypeOnlyAliasDeclaration != null)
            {
                return hooks.GetTypeOnlyAliasDeclaration(symbol, SymbolFlags.Value) != null;
            }

            var node = GetDeclarationOfAliasSymbol(symbol);
            while (node != null)
            {
                switch (node.Kind)
                {
                    case SyntaxKind.ImportEqualsDeclaration:
                    case SyntaxKind.ExportDeclaration:
                        return node.IsTypeOnly();
                    case SyntaxKind.ImportClause:
                    case SyntaxKind.ImportSpecifier:
                    case SyntaxKind.ExportSpecifier:
                        if (node.IsTypeOnly())
                        {
                            return true;
                        }
                        node = node.Parent;
                        continue;
                    case SyntaxKind.NamedImports:
                    case SyntaxKind.NamedExports:
                        node = node.Parent;
                        continue;
                }
                break;
            }
        }
        return false;
    }

    private Node? GetDeclarationOfAliasSymbol(Symbol symbol)
    {
        return CoreUtil.FindLast(symbol.Declarations, AstUtilities.IsAliasSymbolDeclaration);
    }

    private Symbol? GetExportSymbolOfValueSymbolIfExported(Symbol? symbol)
    {
        if (symbol != null)
        {
            if (hooks.GetExportSymbolOfValueSymbolIfExported != null)
            {
                return hooks.GetExportSymbolOfValueSymbolIfExported(symbol);
            }
            if ((symbol.Flags & SymbolFlags.ExportValue) != 0 && symbol.ExportSymbol != null)
            {
                symbol = symbol.ExportSymbol;
            }
            return GetMergedSymbol(symbol);
        }
        return null;
    }

    public Node? GetReferencedExportContainer(Node node, bool prefixLocals) /*SourceFile|ModuleDeclaration|EnumDeclaration*/
    {
        // When resolving the export for the name of a module or enum
        // declaration, we need to start resolution at the declaration's container.
        // Otherwise, we could incorrectly resolve the export as the
        // declaration if it contains an exported member with the same name.
        var startInDeclarationContainer = node.Parent != null && (node.Parent.Kind == SyntaxKind.ModuleDeclaration || node.Parent.Kind == SyntaxKind.EnumDeclaration) && node == node.Parent.Name();
        var symbol = GetReferencedValueSymbol(node, startInDeclarationContainer);
        if (symbol != null)
        {
            if ((symbol.Flags & SymbolFlags.ExportValue) != 0)
            {
                // If we reference an exported entity within the same module declaration, then whether
                // we prefix depends on the kind of entity. SymbolFlags.ExportHasLocal encompasses all the
                // kinds that we do NOT prefix.
                var exportSymbol = GetMergedSymbol(symbol.ExportSymbol);
                if (!prefixLocals && (exportSymbol!.Flags & SymbolFlags.ExportHasLocal) != 0 && (exportSymbol.Flags & SymbolFlags.Variable) == 0)
                {
                    return null;
                }
                symbol = exportSymbol;
            }
            var parentSymbol = GetParentOfSymbol(symbol);
            if (parentSymbol != null)
            {
                if ((parentSymbol.Flags & SymbolFlags.ValueModule) != 0 && parentSymbol.ValueDeclaration != null && parentSymbol.ValueDeclaration.Kind == SyntaxKind.SourceFile)
                {
                    var symbolFile = parentSymbol.ValueDeclaration.AsSourceFile();
                    var referenceFile = AstUtilities.GetSourceFileOfNode(node);
                    // If `node` accesses an export and that export isn't in the same file, then symbol is a namespace export, so return nil.
                    var symbolIsUmdExport = symbolFile != referenceFile;
                    if (symbolIsUmdExport)
                    {
                        return null;
                    }
                    return symbolFile.AsNode();
                }
                Func<Node, bool> isMatchingContainer = n =>
                    (n.Kind == SyntaxKind.ModuleDeclaration || n.Kind == SyntaxKind.EnumDeclaration) && GetSymbolOfDeclaration(n) == parentSymbol;
                return AstUtilities.FindAncestor(node.Parent, isMatchingContainer);
            }
        }

        return null;
    }

    public Node? GetReferencedImportDeclaration(Node node)
    {
        var symbol = GetReferencedValueSymbol(node, false /*startInDeclarationContainer*/);
        if (symbol != null)
        {
            // We should only get the declaration of an alias if there isn't a local value
            // declaration for the symbol
            if (AstUtilities.IsNonLocalAlias(symbol, SymbolFlags.Value /*excludes*/) && !IsTypeOnlyAliasDeclaration(symbol))
            {
                return GetDeclarationOfAliasSymbol(symbol);
            }
        }

        return null;
    }

    public Node? GetReferencedValueDeclaration(Node node)
    {
        var symbol = GetReferencedValueSymbol(node, false /*startInDeclarationContainer*/);
        if (symbol != null)
        {
            return GetExportSymbolOfValueSymbolIfExported(symbol)!.ValueDeclaration;
        }
        return null;
    }

    public Node[] GetReferencedValueDeclarations(Node node)
    {
        // PORT-NOTE: Go returns a nil slice when empty; C# returns an empty array (len 0, same semantics).
        List<Node> declarations = new();
        var symbol = GetReferencedValueSymbol(node, false /*startInDeclarationContainer*/);
        if (symbol != null)
        {
            symbol = GetExportSymbolOfValueSymbolIfExported(symbol);
            foreach (var declaration in symbol!.Declarations ?? [])
            {
                switch (declaration.Kind)
                {
                    case SyntaxKind.VariableDeclaration:
                    case SyntaxKind.Parameter:
                    case SyntaxKind.BindingElement:
                    case SyntaxKind.PropertyDeclaration:
                    case SyntaxKind.PropertyAssignment:
                    case SyntaxKind.ShorthandPropertyAssignment:
                    case SyntaxKind.EnumMember:
                    case SyntaxKind.ObjectLiteralExpression:
                    case SyntaxKind.FunctionDeclaration:
                    case SyntaxKind.FunctionExpression:
                    case SyntaxKind.ArrowFunction:
                    case SyntaxKind.ClassDeclaration:
                    case SyntaxKind.ClassExpression:
                    case SyntaxKind.EnumDeclaration:
                    case SyntaxKind.MethodDeclaration:
                    case SyntaxKind.GetAccessor:
                    case SyntaxKind.SetAccessor:
                    case SyntaxKind.ModuleDeclaration:
                        declarations.Add(declaration);
                        break;
                }
            }
        }
        return declarations.ToArray();
    }

    public string GetElementAccessExpressionName(ElementAccessExpression expression)
    {
        if (expression != null)
        {
            if (hooks.GetElementAccessExpressionName != null)
            {
                var (name, ok) = hooks.GetElementAccessExpressionName(expression);
                if (ok)
                {
                    return name;
                }
            }
        }
        return "";
    }

    public Node? GetReferencedMemberValueDeclaration(Node node)
    {
        // member references are `this.something` or `this[something]`, so should always simply have a resolved symbol
        var s = GetResolvedSymbol(node);
        if (s == null && node.Symbol() != null)
        {
            // might be a declaration instead of a ref, get the merged declaration symbol
            s = GetMergedSymbol(node.Symbol());
        }
        if (s == null)
        {
            return null;
        }
        return GetExportSymbolOfValueSymbolIfExported(s)!.ValueDeclaration;
    }
}
