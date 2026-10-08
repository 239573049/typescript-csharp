// Support partial for Ls/SourceDefinition.cs (tsgo internal/ls/sourcedefinition.go).
//
// Holds the pieces the 1:1 port needs that are not yet provided by the shared
// foundation files:
//   1. lsproto LocationLink + DefinitionResponse (lsp_generated.go subset) — Protocol.cs
//      does not carry them yet.
//   2. The ls utilities/go-fallback helpers the Go file leans on that are either private
//      to other LS files or not yet ported into Ts.Ast:
//        refInfo + getReferenceAtPosition (findallreferences.go:48 / utilities.go:1305),
//        findReferenceInPosition (utilities.go:427), isModuleSpecifierLike
//        (utilities.go:47), getContextNode (findallreferences.go:271), toContextRange
//        (utilities.go:1293), getRangeOfNode (findallreferences.go:329),
//        getFileAndStartPosFromDeclaration (findallreferences.go:490),
//        ast.GetExternalModuleName (ast/utilities.go:1884),
//        ast.IsDefaultImport (ast/utilities.go:2524),
//        ast.GetImpliedNodeFormatForFile (ast/utilities.go:2533),
//        ast.IsArrayLiteralOrObjectLiteralDestructuringPattern (ast/ast.go:1403).
//   3. The definition.go helpers the source-definition checker path calls:
//        getDeclarationsFromLocation (definition.go:230),
//        getDeclarationsFromObjectLiteralElement (definition.go:304),
//        getAncestorCallLikeExpression (definition.go:334),
//        tryGetSignatureDeclaration (definition.go:345),
//        createLocationsFromLinks (definition.go:213).

using System;
using System.Collections.Generic;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerLsExports extension methods (GetResolvedSymbol)
using ResolutionMode = Ts.Core.ModuleKind;

namespace Ts.LsProto
{

// lsp_generated.go: LocationLink.
public sealed class LocationLink
{
    [System.Runtime.Serialization.DataMember(Name = "originSelectionRange")]
    [System.Text.Json.Serialization.JsonPropertyName("originSelectionRange")]
    public Range? OriginSelectionRange { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "targetUri")]
    [System.Text.Json.Serialization.JsonPropertyName("targetUri")]
    public required DocumentUri TargetUri { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "targetRange")]
    [System.Text.Json.Serialization.JsonPropertyName("targetRange")]
    public required Range TargetRange { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "targetSelectionRange")]
    [System.Text.Json.Serialization.JsonPropertyName("targetSelectionRange")]
    public required Range TargetSelectionRange { get; set; }
}

// lsp_generated.go: DefinitionResponse — the Location | Locations | DefinitionLinks union.
public sealed class DefinitionResponse
{
    public Location? Location { get; set; }
    public Location[]? Locations { get; set; }
    public LocationLink[]? DefinitionLinks { get; set; }
}

}

namespace Ts.Ls
{

// findallreferences.go:48 — refInfo.
internal sealed record RefInfo(SourceFile? File, string FileName, Ast.FileReference? Reference, bool Unverified);

// Go free functions shared by the source-definition port; hosted beside it (the Go
// originals live in ls/utilities.go, ls/findallreferences.go, ls/definition.go and ast).
internal static class SourceDefinitionDeps
{
    // utilities.go:427 — findReferenceInPosition.
    internal static Ast.FileReference? FindReferenceInPosition(Ast.FileReference[]? refs, int pos)
    {
        if (refs == null)
        {
            return null;
        }
        foreach (var @ref in refs)
        {
            if (@ref.TextRange.ContainsInclusive(pos))
            {
                return @ref;
            }
        }
        return null;
    }

    // utilities.go:47 — isModuleSpecifierLike.
    internal static bool IsModuleSpecifierLike(Node node)
    {
        if (!AstUtilities.IsStringLiteralLike(node))
        {
            return false;
        }

        if (AstUtilities.IsRequireCall(node.Parent!, false /*requireStringLiteralLikeArgument*/) || AstUtilities.IsImportCall(node.Parent!))
        {
            return node.Parent!.Arguments()![0] == node;
        }

        return node.Parent!.Kind == SyntaxKind.ExternalModuleReference ||
            node.Parent!.Kind == SyntaxKind.ImportDeclaration ||
            node.Parent!.Kind == SyntaxKind.JSImportDeclaration;
    }

    // utilities.go:1305 — getReferenceAtPosition.
    // PORT-NOTE: the /// <reference types="..."/> and /// <reference lib="..."/> arms call
    // program.GetResolvedTypeReferenceDirectiveFromTypeReferenceDirective /
    // program.GetLibFileFromReference, which the C# Program does not expose yet; those two
    // arms are deferred with the program API (a directive found at the position resolves to
    // null until then).
    internal static RefInfo? GetReferenceAtPosition(SourceFile sourceFile, int position, global::Ts.Compiler.Program program)
    {
        if (FindReferenceInPosition(sourceFile.ReferencedFiles, position) is { } referencePath)
        {
            if (program.GetSourceFileFromReference(sourceFile, referencePath) is { } file)
            {
                return new RefInfo(file, file.FileName(), referencePath, false);
            }
            return null;
        }

        if (FindReferenceInPosition(sourceFile.TypeReferenceDirectives, position) != null)
        {
            // Deferred: program.GetResolvedTypeReferenceDirectiveFromTypeReferenceDirective.
            return null;
        }

        if (FindReferenceInPosition(sourceFile.LibReferenceDirectives, position) != null)
        {
            // Deferred: program.GetLibFileFromReference.
            return null;
        }

        if (sourceFile.Imports() is not { Length: > 0 } && sourceFile.ModuleAugmentations is not { Length: > 0 })
        {
            return null;
        }

        var node = global::Ts.AstNav.Tokens.GetTouchingToken(sourceFile, position);
        if (!IsModuleSpecifierLike(node) || !global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(node.Text()))
        {
            return null;
        }

        if (program.GetResolvedModuleFromModuleSpecifier(sourceFile, node) is { } resolution)
        {
            var verifiedFileName = resolution.ResolvedFileName;
            var fileName = resolution.ResolvedFileName;
            if (fileName == "")
            {
                fileName = global::Ts.TsPath.TsPath.ResolvePath(global::Ts.TsPath.TsPath.GetDirectoryPath(sourceFile.FileName()), node.Text());
            }
            return new RefInfo(
                program.GetSourceFile(fileName),
                fileName,
                null,
                verifiedFileName != "");
        }

        return null;
    }

    // ast/utilities.go:2524 — IsDefaultImport.
    internal static bool IsDefaultImport(Node node /*ImportDeclaration | ImportEqualsDeclaration | ExportDeclaration*/)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration:
                var importClause = node.ImportClause();
                return importClause != null && importClause.Name() != null;
        }
        return false;
    }

    // ast/ast.go:1403 — IsArrayLiteralOrObjectLiteralDestructuringPattern.
    internal static bool IsArrayLiteralOrObjectLiteralDestructuringPattern(Node node)
    {
        if (!(NodeTests.IsArrayLiteralExpression(node) || NodeTests.IsObjectLiteralExpression(node)))
        {
            return false;
        }
        var parent = node.Parent;
        // [a,b,c] from:
        // [a, b, c] = someExpression;
        if (NodeTests.IsBinaryExpression(parent!) && parent!.AsBinaryExpression().Left == node && parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken)
        {
            return true;
        }
        // [a, b, c] from:
        // for([a, b, c] of expression)
        if (NodeTests.IsForOfStatement(parent!) && parent!.Initializer() == node)
        {
            return true;
        }
        // {x, a: {a, b, c} } = someExpression
        if (NodeTests.IsPropertyAssignment(parent!))
        {
            return IsArrayLiteralOrObjectLiteralDestructuringPattern(parent!.Parent!);
        }
        // [a, b, c] of
        // [x, [a, b, c] ] = someExpression
        return IsArrayLiteralOrObjectLiteralDestructuringPattern(parent!);
    }

    // ast/utilities.go:2533 — GetImpliedNodeFormatForFile.
    internal static ResolutionMode GetImpliedNodeFormatForFile(string path, string packageJsonType)
    {
        var impliedNodeFormat = global::Ts.Core.Core.ResolutionModeNone;
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(path, [global::Ts.TsPath.TsPath.ExtensionDmts, global::Ts.TsPath.TsPath.ExtensionMts, global::Ts.TsPath.TsPath.ExtensionMjs]))
        {
            impliedNodeFormat = global::Ts.Core.Core.ResolutionModeESM;
        }
        else if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(path, [global::Ts.TsPath.TsPath.ExtensionDcts, global::Ts.TsPath.TsPath.ExtensionCts, global::Ts.TsPath.TsPath.ExtensionCjs]))
        {
            impliedNodeFormat = global::Ts.Core.Core.ResolutionModeCommonJS;
        }
        else if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(path, [global::Ts.TsPath.TsPath.ExtensionDts, global::Ts.TsPath.TsPath.ExtensionTs, global::Ts.TsPath.TsPath.ExtensionTsx, global::Ts.TsPath.TsPath.ExtensionJs, global::Ts.TsPath.TsPath.ExtensionJsx]))
        {
            impliedNodeFormat = packageJsonType == "module" ? global::Ts.Core.Core.ResolutionModeESM : global::Ts.Core.Core.ResolutionModeCommonJS;
        }

        return impliedNodeFormat;
    }

    // ast/utilities.go:1884-1898 — GetExternalModuleName (the Node-returning ast form;
    // OrganizeImports.cs carries a private string-returning variant).
    internal static Node? GetExternalModuleName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration or SyntaxKind.ExportDeclaration:
                return node.ModuleSpecifier();
            case SyntaxKind.ImportEqualsDeclaration:
                if (node.AsImportEqualsDeclaration()!.ModuleReference!.Kind == SyntaxKind.ExternalModuleReference)
                {
                    return node.AsImportEqualsDeclaration()!.ModuleReference!.Expression();
                }
                return null;
            case SyntaxKind.ImportType:
                return GetImportTypeNodeLiteral(node);
            case SyntaxKind.CallExpression:
                {
                    var args = node.Arguments();
                    if (args != null && args.Length > 0)
                    {
                        return args[0];
                    }
                    return null;
                }
            default:
                return null;
        }
    }

    // ast/utilities.go — getImportTypeNodeLiteral (backing GetExternalModuleName's
    // ImportType arm).
    private static Node? GetImportTypeNodeLiteral(Node node)
    {
        if (node.AsImportTypeNode()!.Argument!.Kind == SyntaxKind.StringLiteral)
        {
            return node.AsImportTypeNode().Argument;
        }
        return null;
    }

    // findallreferences.go:271 — getContextNode.
    internal static Node? GetContextNode(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        switch (node.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                if (!NodeTests.IsVariableDeclarationList(node.Parent!) || node.Parent!.AsVariableDeclarationList().Declarations!.Nodes.Length != 1)
                {
                    return node;
                }
                else if (NodeTests.IsVariableStatement(node.Parent!.Parent!))
                {
                    return node.Parent!.Parent!;
                }
                else if (AstUtilities.IsForInOrOfStatement(node.Parent!.Parent!))
                {
                    return GetContextNode(node.Parent!.Parent!);
                }
                return node.Parent;

            case SyntaxKind.BindingElement:
                return GetContextNode(node.Parent!.Parent!);

            case SyntaxKind.ImportSpecifier:
                return node.Parent!.Parent!.Parent;

            case SyntaxKind.ExportSpecifier or SyntaxKind.NamespaceImport:
                return node.Parent!.Parent;

            case SyntaxKind.ImportClause or SyntaxKind.NamespaceExport:
                return node.Parent;

            case SyntaxKind.BinaryExpression:
                return node.Parent!.Kind == SyntaxKind.ExpressionStatement ? node.Parent : node;

            case SyntaxKind.ForOfStatement or SyntaxKind.ForInStatement:
                // !!! not implemented
                return null;

            case SyntaxKind.PropertyAssignment or SyntaxKind.ShorthandPropertyAssignment:
                if (IsArrayLiteralOrObjectLiteralDestructuringPattern(node.Parent!))
                {
                    return GetContextNode(AstUtilities.FindAncestor(node.Parent!, n =>
                        n.Kind == SyntaxKind.BinaryExpression || AstUtilities.IsForInOrOfStatement(n)));
                }
                return node;
            case SyntaxKind.SwitchStatement:
                // !!! not implemented
                return null;
            default:
                return node;
        }
    }

    // utilities.go:1293 — toContextRange.
    internal static global::Ts.Core.TextRange? ToContextRange(global::Ts.Core.TextRange textRange, SourceFile contextFile, Node? context)
    {
        if (context == null)
        {
            return textRange;
        }
        // !!! isContextWithStartAndEndNode
        var contextRange = GetRangeOfNode(context, contextFile, null /*endNode*/);
        if (contextRange.Pos != textRange.Pos || contextRange.End != textRange.End)
        {
            return contextRange;
        }
        return null;
    }

    // findallreferences.go:329 — getRangeOfNode (a LanguageService-free copy is kept here;
    // FindAllReferences.cs hosts a private instance duplicate).
    internal static global::Ts.Core.TextRange GetRangeOfNode(Node node, SourceFile? sourceFile, Node? endNode)
    {
        sourceFile ??= AstUtilities.GetSourceFileOfNode(node)!;
        var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false /*includeJsDoc*/);
        var end = (endNode ?? node).End;
        if (AstUtilities.IsStringLiteralLike(node) && (end - start) > 2)
        {
            if (endNode is not null)
            {
                // Go panic; InvalidOperationException is the repository's panic mapping.
                throw new InvalidOperationException("endNode is not nil for stringLiteralLike");
            }
            start += 1;
            end -= 1;
        }
        if (endNode is not null && endNode.Kind == SyntaxKind.CaseBlock)
        {
            end = endNode.Pos;
        }
        return new global::Ts.Core.TextRange(start, end);
    }

    // findallreferences.go:490 — getFileAndStartPosFromDeclaration.
    internal static (SourceFile File, int StartPos) GetFileAndStartPosFromDeclaration(Node declaration)
    {
        var file = AstUtilities.GetSourceFileOfNode(declaration)!;
        var name = AstUtilities.GetNameOfDeclaration(declaration) ?? declaration;
        var textRange = GetRangeOfNode(name, file, null /*endNode*/);

        return (file, textRange.Pos);
    }

    // definition.go:230 — getDeclarationsFromLocation.
    internal static List<Node> GetDeclarationsFromLocation(global::Ts.Checker.Checker c, Node node)
    {
        if (NodeTests.IsIdentifier(node) && NodeTests.IsShorthandPropertyAssignment(node.Parent!))
        {
            // Because name in short-hand property assignment has two different meanings: property name and property value,
            // using go-to-definition at such position should go to the variable declaration of the property value rather than
            // go to the declaration of the property name (in this case stay at the same position). However, if go-to-definition
            // is performed at the location of property access, we would like to go to definition of the property in the short-hand
            // assignment. This case and others are handled by the following code.
            // and the contextual type's property declarations
            var shorthandSymbol = c.GetResolvedSymbol(node);
            var declarations = shorthandSymbol?.Declarations is { } decls ? new List<Node>(decls) : new List<Node>();
            var contextualDeclarations = GetDeclarationsFromObjectLiteralElement(c, node);
            declarations.AddRange(contextualDeclarations);
            return declarations;
        }

        if (AstUtilities.IsPropertyName(node) && NodeTests.IsBindingElement(node.Parent!) && NodeTests.IsObjectBindingPattern(node.Parent!.Parent!))
        {
            // If the node is the name of a BindingElement within an ObjectBindingPattern instead of just returning the
            // declaration of the symbol (which is itself), we should try to get to the original type of the
            // ObjectBindingPattern and return the property declaration for the referenced property.
            // For example:
            //      import('./foo').then(({ bar }) => undefined); => should navigate to the declaration in file "./foo"
            //
            //      function bar<T>(onfulfilled: (value: T) => void) { }
            //      interface Test { prop1: number }
            //      bar<Test>(({ prop1 }) => undefined);  => should navigate to prop1 in Test
            var bindingEl = node.Parent!.AsBindingElement();
            if (bindingEl.DotDotDotToken == null && node == (bindingEl.PropertyName ?? node.Parent!.Name()))
            {
                if (AstUtilities.TryGetTextOfPropertyName(node, out var name))
                {
                    var t = c.GetTypeAtLocation(node.Parent!.Parent!);
                    var types = new List<global::Ts.Checker.Type> { t };
                    if (t.IsUnion())
                    {
                        types = new List<global::Ts.Checker.Type>(t.Types());
                    }
                    var result = new List<Node>();
                    foreach (var unionType in types)
                    {
                        if (c.GetPropertyOfType(unionType, name) is { } prop)
                        {
                            result.AddRange(prop.Declarations ?? []);
                        }
                    }
                    return result;
                }
            }
        }

        node = GetDeclarationNameForKeyword(node);
        if (c.GetSymbolAtLocation(node) is { } symbol)
        {
            if ((symbol.Flags & SymbolFlags.Class) != 0 && (symbol.Flags & (SymbolFlags.Function | SymbolFlags.Variable)) == 0 && node.Kind == SyntaxKind.ConstructorKeyword)
            {
                if (symbol.Members!.TryGetValue(global::Ts.Ast.InternalSymbolName.Constructor, out var constructor) && constructor != null)
                {
                    symbol = constructor;
                }
            }
            if ((symbol.Flags & SymbolFlags.Alias) != 0)
            {
                var (resolved, _) = c.ResolveAlias(symbol);
                if (resolved != null)
                {
                    symbol = resolved;
                }
            }
            var objectLiteralElementDeclarations = GetDeclarationsFromObjectLiteralElement(c, node);
            if (objectLiteralElementDeclarations.Count > 0)
            {
                return objectLiteralElementDeclarations;
            }
            if (symbol.Declarations is { Length: > 0 } symbolDeclarations)
            {
                return new List<Node>(symbolDeclarations);
            }
        }
        // Deferred: c.GetIndexSignaturesAtLocation(node) is not on the public checker
        // surface yet; the index-signature fallback arm lands with it.
        return [];
    }

    // definition.go:137 — getDeclarationNameForKeyword.
    internal static Node GetDeclarationNameForKeyword(Node node)
    {
        if (node.Kind >= SyntaxKind.FirstKeyword && node.Kind <= SyntaxKind.LastKeyword)
        {
            if (NodeTests.IsVariableDeclarationList(node.Parent!))
            {
                var declarations = node.Parent!.AsVariableDeclarationList().Declarations!.Nodes;
                if (declarations.Length > 0 && declarations[0].Name() != null)
                {
                    return declarations[0].Name()!;
                }
            }
            else if (node.Parent!.DeclarationData() != null && node.Parent!.Name() != null && node.Pos < node.Parent!.Name()!.Pos)
            {
                return node.Parent!.Name()!;
            }
        }
        return node;
    }

    // definition.go:304 — getDeclarationsFromObjectLiteralElement. Returns declarations from the contextual type
    // of an object literal element, if available.
    internal static List<Node> GetDeclarationsFromObjectLiteralElement(global::Ts.Checker.Checker c, Node node)
    {
        var element = LsMeaning.GetContainingObjectLiteralElement(node);
        if (element == null)
        {
            return [];
        }

        var contextualType = c.GetContextualType(element.Parent!, global::Ts.Checker.ContextFlags.None);
        if (contextualType == null)
        {
            return [];
        }

        var properties = c.GetPropertySymbolsFromContextualType(element, contextualType, false /*unionSymbolOk*/);
        bool SomeMatches(Symbol p) =>
            p.ValueDeclaration != null && NodeTests.IsObjectLiteralExpression(p.ValueDeclaration.Parent!) && AstUtilities.IsObjectLiteralElement(p.ValueDeclaration) && p.ValueDeclaration.Name() == node;
        var hasMatch = false;
        foreach (var p in properties)
        {
            if (SomeMatches(p))
            {
                hasMatch = true;
                break;
            }
        }
        if (hasMatch)
        {
            if (c.GetContextualType(element.Parent!, global::Ts.Checker.ContextFlags.IgnoreNodeInferences) is { } withoutNodeInferencesType)
            {
                var withoutNodeInferencesProperties = c.GetPropertySymbolsFromContextualType(element, withoutNodeInferencesType, false /*unionSymbolOk*/);
                if (withoutNodeInferencesProperties.Length > 0)
                {
                    properties = withoutNodeInferencesProperties;
                }
            }
        }

        var result = new List<Node>();
        foreach (var prop in properties)
        {
            result.AddRange(prop.Declarations ?? []);
        }
        return result;
    }

    // definition.go:334 — getAncestorCallLikeExpression. Returns a CallLikeExpression where `node` is the target being invoked.
    // (ast.IsRightSideOfPropertyAccess rides the CallHierarchyAstHelpers re-host of
    // ast/utilities.go:3585; the ast port has no public member for it yet.)
    internal static Node? GetAncestorCallLikeExpression(Node node)
    {
        var target = AstUtilities.FindAncestor(node, n => !CallHierarchyAstHelpers.IsRightSideOfPropertyAccess(n));
        var callLike = target!.Parent;
        if (callLike != null && AstUtilities.IsCallLikeExpression(callLike) && AstUtilities.GetInvokedExpression(callLike) == target)
        {
            return callLike;
        }
        return null;
    }

    // definition.go:345 — tryGetSignatureDeclaration.
    internal static Node? TryGetSignatureDeclaration(global::Ts.Checker.Checker typeChecker, Node node)
    {
        global::Ts.Checker.Signature? signature = null;
        var callLike = GetAncestorCallLikeExpression(node);
        if (callLike != null)
        {
            signature = typeChecker.GetResolvedSignature(callLike);
        }
        // Don't go to a function type, go to the value having that type.
        if (signature != null && signature.Declaration() != null)
        {
            var declaration = signature.Declaration()!;
            if (AstUtilities.IsFunctionLike(declaration) && !NodeTests.IsFunctionTypeNode(declaration))
            {
                return declaration;
            }
        }
        return null;
    }

    // definition.go:213 — createLocationsFromLinks.
    internal static global::Ts.LsProto.Location[] CreateLocationsFromLinks(global::Ts.LsProto.LocationLink[] links)
    {
        var locations = new global::Ts.LsProto.Location[links.Length];
        for (var i = 0; i < links.Length; i++)
        {
            var link = links[i];
            locations[i] = new global::Ts.LsProto.Location
            {
                Uri = link.TargetUri,
                Range = link.TargetSelectionRange,
            };
        }
        return locations;
    }
}
}
