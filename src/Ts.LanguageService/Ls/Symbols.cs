// Ported from tsgo internal/ls/symbols.go (document-symbol outline).
//
// PORT-NOTE: the Go entry point reads the client capability from the request context
// (lsproto.GetClientCapabilities(ctx).TextDocument.DocumentSymbol.
// HierarchicalDocumentSymbolSupport). The C# LS foundation has no per-request context
// carrier yet, so the capability bit is an explicit parameter; ctx.Err() checks map to
// CancellationToken.IsCancellationRequested. Positions are UTF-16 code units.
//
// PORT-NOTE (deferred tail, symbols.go:516-636): ProvideWorkspaceSymbols and its
// private support set (DeclarationInfo, shouldExcludeFile, isInsideNodeModules,
// getMatchScore, compareDeclarationInfos) are not ported in this wave. They need
// ast.SourceFile.GetDeclarationMap (unported, see Ast/SourceFile.cs PORT-NOTE),
// lsutil.UserPreferences, lsconv.Converters.ToLSPLocation, ls getContainerNode, and a
// multi-Program host — none of which exist on the C# surface yet.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using DocumentSymbol = Ts.LsProto.DocumentSymbol;
using LspLocation = Ts.LsProto.Location;
using LspRange = Ts.LsProto.Range;
using SymbolInformation = Ts.LsProto.SymbolInformation;
using SymbolKind = Ts.LsProto.SymbolKind;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // symbols.go:24 — ProvideDocumentSymbols.
    public global::Ts.LsProto.SymbolInformationsOrDocumentSymbols ProvideDocumentSymbols(
        string fileName,
        bool hierarchicalDocumentSymbolSupport = true,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(fileName);
        if (hierarchicalDocumentSymbolSupport)
        {
            var symbols = GetDocumentSymbolsForChildren(cancellationToken, file, file);
            return global::Ts.LsProto.SymbolInformationsOrDocumentSymbols.FromDocumentSymbols(symbols);
        }
        // Client doesn't support hierarchical document symbols, return flat SymbolInformation array
        var symbolInfos = GetDocumentSymbolInformations(cancellationToken, file, fileName);
        return global::Ts.LsProto.SymbolInformationsOrDocumentSymbols.FromSymbolInformations(symbolInfos);
    }

    // symbols.go:40 — getDocumentSymbolInformations converts hierarchical DocumentSymbols
    // to a flat SymbolInformation array.
    private SymbolInformation[] GetDocumentSymbolInformations(CancellationToken cancellationToken, SourceFile file, string fileName)
    {
        // First get hierarchical symbols
        var docSymbols = GetDocumentSymbolsForChildren(cancellationToken, file, file);

        // Flatten the hierarchy
        var result = new List<SymbolInformation>();
        void Flatten(List<DocumentSymbol> symbols, string? containerName)
        {
            foreach (var symbol in symbols)
            {
                var info = new SymbolInformation
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind,
                    Location = new LspLocation
                    {
                        Uri = new global::Ts.LsProto.DocumentUri(fileName),
                        Range = symbol.Range,
                    },
                    ContainerName = containerName,
                    Tags = symbol.Tags,
                    Deprecated = symbol.Deprecated,
                };
                result.Add(info);

                // Recursively flatten children with this symbol as container
                if (symbol.Children != null && symbol.Children.Count > 0)
                {
                    Flatten(symbol.Children, symbol.Name);
                }
            }
        }
        Flatten(docSymbols, null);
        return result.ToArray();
    }

    // symbols.go:72 — getDocumentSymbolsForChildren.
    private List<DocumentSymbol> GetDocumentSymbolsForChildren(CancellationToken cancellationToken, Node node, SourceFile file)
    {
        List<DocumentSymbol>? symbols = null;
        var expandoTargets = new HashSet<string>(StringComparer.Ordinal);

        void AddSymbolForNode(Node addNode, Node? name, List<DocumentSymbol>? children)
        {
            if ((addNode.Flags & NodeFlags.Reparsed) == 0)
            {
                var symbol = NewDocumentSymbol(addNode, name, children);
                if (symbol != null)
                {
                    (symbols ??= []).Add(symbol);
                }
            }
        }

        List<DocumentSymbol>? GetSymbolsForChildren(Node? childrenNode)
        {
            List<DocumentSymbol>? result = null;
            if (childrenNode != null)
            {
                var saveExpandoTargets = expandoTargets;
                expandoTargets = new HashSet<string>(StringComparer.Ordinal);
                var saveSymbols = symbols;
                symbols = null;
                childrenNode.ForEachChild(Visit);
                result = symbols;
                symbols = saveSymbols;
                expandoTargets = saveExpandoTargets;
            }
            return result;
        }

        Action StartNode(Node? startNode, Node? name)
        {
            if (startNode == null)
            {
                return () => { };
            }
            var saveExpandoTargets = expandoTargets;
            expandoTargets = new HashSet<string>(StringComparer.Ordinal);
            var saveSymbols = symbols;
            symbols = null;
            return () =>
            {
                var result = symbols;
                symbols = saveSymbols;
                expandoTargets = saveExpandoTargets;
                AddSymbolForNode(startNode, name, result);
            };
        }

        List<DocumentSymbol>? GetSymbolsForNode(Node? symbolsNode)
        {
            List<DocumentSymbol>? result = null;
            if (symbolsNode != null)
            {
                var saveSymbols = symbols;
                symbols = null;
                Visit(symbolsNode);
                result = symbols;
                symbols = saveSymbols;
            }
            return result;
        }

        bool Visit(Node visitedNode)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
            if ((visitedNode.Flags & NodeFlags.Reparsed) == 0)
            {
                var jsdocs = visitedNode.JSDoc(file);
                if (jsdocs != null && jsdocs.Length > 0)
                {
                    foreach (var jsdoc in jsdocs)
                    {
                        var tagList = jsdoc.AsJSDoc().Tags;
                        if (tagList != null)
                        {
                            foreach (var tag in tagList.Nodes)
                            {
                                if (NodeTests.IsJSDocTypedefTag(tag) || NodeTests.IsJSDocCallbackTag(tag))
                                {
                                    AddSymbolForNode(tag, null /*name*/, null /*children*/);
                                }
                            }
                        }
                    }
                }
            }
            switch (visitedNode.Kind)
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.EnumDeclaration:
                    if (AstUtilities.IsClassLike(visitedNode) && LsDeps.GetDeclarationName(visitedNode) != "")
                    {
                        expandoTargets.Add(LsDeps.GetDeclarationName(visitedNode));
                    }
                    AddSymbolForNode(visitedNode, null /*name*/, GetSymbolsForChildren(visitedNode));
                    break;
                case SyntaxKind.ModuleDeclaration:
                    AddSymbolForNode(visitedNode, null /*name*/, GetSymbolsForChildren(GetInteriorModule(visitedNode)));
                    break;
                case SyntaxKind.Constructor:
                    AddSymbolForNode(visitedNode, null /*name*/, GetSymbolsForChildren(visitedNode.Body()));
                    foreach (var param in visitedNode.Parameters())
                    {
                        if (AstUtilities.IsParameterPropertyDeclaration(param, visitedNode))
                        {
                            AddSymbolForNode(param, null /*name*/, null /*children*/);
                        }
                    }
                    break;
                case SyntaxKind.FunctionDeclaration:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.ArrowFunction:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                    var declName = LsDeps.GetDeclarationName(visitedNode);
                    if (declName != "")
                    {
                        expandoTargets.Add(declName);
                    }
                    AddSymbolForNode(visitedNode, null /*name*/, GetSymbolsForChildren(visitedNode.Body()));
                    break;
                case SyntaxKind.VariableDeclaration:
                case SyntaxKind.BindingElement:
                case SyntaxKind.PropertyAssignment:
                case SyntaxKind.PropertyDeclaration:
                    var nodeName = visitedNode.Name();
                    if (nodeName != null)
                    {
                        if (AstUtilities.IsBindingPattern(nodeName))
                        {
                            Visit(nodeName);
                        }
                        else
                        {
                            AddSymbolForNode(visitedNode, null /*name*/, GetSymbolsForChildren(visitedNode.Initializer()));
                        }
                    }
                    break;
                case SyntaxKind.SpreadAssignment:
                    AddSymbolForNode(visitedNode, visitedNode.Expression(), null /*children*/);
                    break;
                case SyntaxKind.MethodSignature:
                case SyntaxKind.PropertySignature:
                case SyntaxKind.CallSignature:
                case SyntaxKind.ConstructSignature:
                case SyntaxKind.IndexSignature:
                case SyntaxKind.EnumMember:
                case SyntaxKind.ShorthandPropertyAssignment:
                case SyntaxKind.TypeAliasDeclaration:
                case SyntaxKind.ImportEqualsDeclaration:
                case SyntaxKind.ExportSpecifier:
                    AddSymbolForNode(visitedNode, null /*name*/, null /*children*/);
                    break;
                case SyntaxKind.ImportClause:
                    // Handle default import case e.g.:
                    //    import d from "mod";
                    if (visitedNode.Name() != null)
                    {
                        AddSymbolForNode(visitedNode.Name()!, visitedNode.Name(), null /*children*/);
                    }
                    // Handle named bindings in imports e.g.:
                    //    import * as NS from "mod";
                    //    import {a, b as B} from "mod";
                    var namedBindings = visitedNode.AsImportClause().NamedBindings;
                    if (namedBindings != null)
                    {
                        if (namedBindings.Kind == SyntaxKind.NamespaceImport)
                        {
                            AddSymbolForNode(namedBindings, null /*name*/, null /*children*/);
                        }
                        else
                        {
                            foreach (var element in namedBindings.Elements() ?? [])
                            {
                                AddSymbolForNode(element, null /*name*/, null /*children*/);
                            }
                        }
                    }
                    break;
                case SyntaxKind.BinaryExpression:
                case SyntaxKind.CallExpression:
                    var assignmentKind = AstUtilities.GetAssignmentDeclarationKind(visitedNode);
                    switch (assignmentKind)
                    {
                        // `module.exports = ...`` should be reparsed into a JSExportAssignment,
                        // and `exports.a = ...`` into a CommonJSExport.
                        case JSDeclarationKind.None:
                        case JSDeclarationKind.ThisProperty:
                        case JSDeclarationKind.ModuleExports:
                        case JSDeclarationKind.ExportsProperty:
                        case JSDeclarationKind.ObjectDefinePropertyExports:
                            visitedNode.ForEachChild(Visit);
                            break;
                        case JSDeclarationKind.Property:
                        case JSDeclarationKind.ObjectDefinePropertyValue:
                            Node target;
                            Node? targetFunction;
                            Node definition;
                            Node propertyName;
                            // `A.b = ... ` or `A.prototype.b = ...`
                            if (NodeTests.IsBinaryExpression(visitedNode))
                            {
                                var binaryExpr = visitedNode.AsBinaryExpression();
                                target = binaryExpr.Left!;
                                targetFunction = target.Expression();
                                definition = binaryExpr.Right!;
                                // `A.b` or `A.prototype.b`
                                if (NodeTests.IsPropertyAccessExpression(target))
                                {
                                    propertyName = target.AsPropertyAccessExpression().Name()!;
                                }
                                else
                                {
                                    // `A["b"]` or `A.prototype["b"]`
                                    propertyName = target.AsElementAccessExpression().ArgumentExpression!;
                                }
                            }
                            else
                            {
                                // `Object.defineProperty(A, "b", {...})`
                                var args = visitedNode.Arguments()!;
                                targetFunction = args[0];
                                target = args[1];
                                propertyName = target;
                                definition = args[2];
                            }
                            if (IsPrototypeExpando(targetFunction!))
                            {
                                targetFunction = targetFunction!.Expression();
                                // If we see a prototype assignment, start tracking the target as an expando target.
                                if (NodeTests.IsIdentifier(targetFunction!))
                                {
                                    expandoTargets.Add(targetFunction!.Text());
                                }
                            }
                            if (NodeTests.IsIdentifier(targetFunction!) &&
                                expandoTargets.Contains(targetFunction!.Text()))
                            {
                                var endNode = StartNode(visitedNode, targetFunction);
                                AddSymbolForNode(target, propertyName, GetSymbolsForNode(definition));
                                endNode();
                            }
                            else
                            {
                                visitedNode.ForEachChild(Visit);
                            }
                            break;
                    }
                    break;
                case SyntaxKind.ExportAssignment:
                    if (visitedNode.AsExportAssignment().IsExportEquals)
                    {
                        AddSymbolForNode(visitedNode, null /*name*/, GetSymbolsForNode(visitedNode.Expression()));
                    }
                    else
                    {
                        visitedNode.ForEachChild(Visit);
                    }
                    break;
                default:
                    visitedNode.ForEachChild(Visit);
                    break;
            }
            return false;
        }

        node.ForEachChild(Visit);
        return MergeExpandos(symbols ?? []);
    }

    // symbols.go:260 — isPrototypeExpando. Target is `f.prototype`.
    private static bool IsPrototypeExpando(Node target)
    {
        if (AstUtilities.IsAccessExpression(target))
        {
            var accessName = AstUtilities.GetElementOrPropertyAccessName(target);
            return accessName != null && string.Equals(accessName.Text(), "prototype", StringComparison.Ordinal);
        }
        return false;
    }

    private const int MaxLength = 150; // symbols.go:268 — maxLength.

    // symbols.go:270 — newDocumentSymbol.
    private DocumentSymbol? NewDocumentSymbol(Node node, Node? name, List<DocumentSymbol>? children)
    {
        var result = new DocumentSymbol();
        var file = AstUtilities.GetSourceFileOfNode(node)!; // Go unconditional dereference.
        var nodeStartPos = global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), node.Pos);
        if (name == null)
        {
            name = AstUtilities.GetNameOfDeclaration(node);
        }
        string text;
        int nameStartPos, nameEndPos;
        if (NodeTests.IsModuleDeclaration(node) && !AstUtilities.IsAmbientModule(node))
        {
            text = GetModuleName(node);
            nameStartPos = global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), name!.Pos); // Go unconditional dereference.
            nameEndPos = GetInteriorModule(node).Name()!.End;
        }
        else if (AstUtilities.IsAnyExportAssignment(node) && node.AsExportAssignment().IsExportEquals)
        {
            text = "export=";
            if (!AstUtilities.NodeIsMissing(name))
            {
                nameStartPos = global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), name!.Pos);
                nameEndPos = name.End;
            }
            else
            {
                nameStartPos = nodeStartPos;
                nameEndPos = node.End;
            }
        }
        else if (name != null)
        {
            text = GetTextOfName(name);
            nameStartPos = Math.Max(global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), name.Pos), nodeStartPos);
            nameEndPos = Math.Max(name.End, nodeStartPos);
        }
        else
        {
            text = GetUnnamedNodeLabel(node);
            nameStartPos = nodeStartPos;
            nameEndPos = nodeStartPos;
        }
        if (text == "")
        {
            return null;
        }
        var truncatedText = global::Ts.StringUtil.StringUtil.TruncateByRunes(text, MaxLength);
        if (truncatedText.Length < text.Length)
        {
            text = truncatedText + "...";
        }
        result.Name = text;
        result.Kind = GetSymbolKindFromNode(node);
        result.Range = new LspRange
        {
            Start = CreateLspPosition(nodeStartPos, file),
            End = CreateLspPosition(node.End, file),
        };
        result.SelectionRange = new LspRange
        {
            Start = CreateLspPosition(nameStartPos, file),
            End = CreateLspPosition(nameEndPos, file),
        };
        children ??= [];
        result.Children = children;
        return result;
    }

    // symbols.go:327 — mergeExpandos. Merges expando symbols into their target symbols,
    // and namespaces of same name. (Go modifies the input slice in place by nil-ing
    // merged entries; the merged flags array is the reference-type equivalent.)
    private static List<DocumentSymbol> MergeExpandos(List<DocumentSymbol> symbols)
    {
        var mergedSymbols = new List<DocumentSymbol>(symbols.Count);
        // Collect symbols that can be an expando target.
        var nameToExpandoTargetIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        // Collect namespaces.
        var nameToNamespaceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            if (IsAnonymousName(symbol.Name))
            {
                continue;
            }
            if (symbol.Kind is SymbolKind.Class or SymbolKind.Function or SymbolKind.Variable)
            {
                if (!nameToExpandoTargetIndex.TryGetValue(symbol.Name, out var indices))
                {
                    indices = [];
                    nameToExpandoTargetIndex[symbol.Name] = indices;
                }
                indices.Add(i);
            }
            if (symbol.Kind == SymbolKind.Namespace)
            {
                if (!nameToNamespaceIndex.ContainsKey(symbol.Name))
                {
                    nameToNamespaceIndex[symbol.Name] = i;
                }
            }
        }
        var merged = new bool[symbols.Count];
        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            if (symbol.Children != null)
            {
                symbol.Children = MergeExpandos(symbol.Children);
            }

            // Anonymous symbols never merge.
            if (IsAnonymousName(symbol.Name))
            {
                continue;
            }

            // Merge expandos.
            if (symbol.Kind == SymbolKind.Property)
            {
                if (nameToExpandoTargetIndex.TryGetValue(symbol.Name, out var symbolsWithSameName))
                {
                    for (var j = symbolsWithSameName.Count - 1; j >= 0; j--)
                    {
                        var targetIndex = symbolsWithSameName[j];
                        var targetSymbol = symbols[targetIndex];
                        MergeChildren(targetSymbol, symbol);
                        // Mark this symbol as merged.
                        merged[i] = true;
                    }
                }
            }
            // Merge namespaces.
            if (symbol.Kind == SymbolKind.Namespace)
            {
                if (nameToNamespaceIndex.TryGetValue(symbol.Name, out var targetIndex) && targetIndex != i)
                {
                    var targetSymbol = symbols[targetIndex];
                    MergeChildren(targetSymbol, symbol);
                    // Mark this symbol as merged.
                    merged[i] = true;
                }
            }
        }
        for (var i = 0; i < symbols.Count; i++)
        {
            if (!merged[i])
            {
                mergedSymbols.Add(symbols[i]);
            }
        }
        return mergedSymbols;
    }

    // symbols.go:386 — mergeChildren.
    private static void MergeChildren(DocumentSymbol target, DocumentSymbol source)
    {
        if (source.Children != null)
        {
            if (target.Children == null)
            {
                target.Children = source.Children;
            }
            else
            {
                var combined = new List<DocumentSymbol>(target.Children.Count + source.Children.Count);
                combined.AddRange(target.Children);
                combined.AddRange(source.Children);
                target.Children = MergeExpandos(combined);
                target.Children.Sort((a, b) => global::Ts.LsProto.Util.CompareRanges(a.Range, b.Range));
            }
        }
    }

    // symbols.go:400 — isAnonymousName. See `getUnnamedNodeLabel`.
    private static bool IsAnonymousName(string name)
    {
        return name is "<function>" or "<class>" or "export=" or "default" or "constructor" or "()" or "new()" or "[]" ||
            name.EndsWith(") callback", StringComparison.Ordinal);
    }

    // symbols.go:405 — getTextOfName.
    private static string GetTextOfName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.NumericLiteral:
                return node.Text();
            case SyntaxKind.StringLiteral:
                return "\"" + LsDeps.EscapeString(node.Text(), '"') + "\"";
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return "`" + LsDeps.EscapeString(node.Text(), '`') + "`";
            case SyntaxKind.ComputedPropertyName:
                if (AstUtilities.IsStringOrNumericLiteralLike(node.Expression()!))
                {
                    return GetTextOfName(node.Expression()!);
                }
                break;
        }
        return global::Ts.Scanner.Scanner.GetTextOfNode(node);
    }

    // symbols.go:421 — getUnnamedNodeLabel.
    private static string GetUnnamedNodeLabel(Node node)
    {
        var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent);
        if (parent != null && NodeTests.IsExportAssignment(parent))
        {
            if (parent.AsExportAssignment().IsExportEquals)
            {
                return "export=";
            }
            return "default";
        }
        switch (node.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                if ((node.ModifierFlags() & ModifierFlags.Default) != 0)
                {
                    return "default";
                }
                if (NodeTests.IsCallExpression(node.Parent!))
                {
                    var name = GetCallExpressionName(node.Parent!.Expression()!);
                    if (name != "")
                    {
                        name = CleanCallbackText(name);
                        if (name.Length > MaxLength)
                        {
                            return name + " callback";
                        }
                        var args = CleanCallbackText(GetCallExpressionLiteralArgs(node.Parent!));
                        return name + "(" + args + ") callback";
                    }
                }
                return "<function>";
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                if ((node.ModifierFlags() & ModifierFlags.Default) != 0)
                {
                    return "default";
                }
                return "<class>";
            case SyntaxKind.Constructor:
                return "constructor";
            case SyntaxKind.CallSignature:
                return "()";
            case SyntaxKind.ConstructSignature:
                return "new()";
            case SyntaxKind.IndexSignature:
                return "[]";
        }
        return "";
    }

    // symbols.go:462 — getCallExpressionName.
    private static string GetCallExpressionName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
                return node.Text();
            case SyntaxKind.PropertyAccessExpression:
                var left = GetCallExpressionName(node.Expression()!);
                var right = GetCallExpressionName(node.Name()!);
                if (left != "")
                {
                    return left + "." + right;
                }
                return right;
        }
        return "";
    }

    // symbols.go:477 — getCallExpressionLiteralArgs.
    private static string GetCallExpressionLiteralArgs(Node callExpr)
    {
        var parts = new List<string>();
        foreach (var arg in callExpr.Arguments() ?? [])
        {
            if (AstUtilities.IsStringLiteralLike(arg) || NodeTests.IsTemplateExpression(arg))
            {
                parts.Add(global::Ts.Scanner.Scanner.GetTextOfNode(arg));
            }
        }
        return string.Join(", ", parts);
    }

    // symbols.go:487 — cleanCallbackText.
    private static string CleanCallbackText(string text)
    {
        var truncated = global::Ts.StringUtil.StringUtil.TruncateByRunes(text, MaxLength);
        if (truncated.Length < text.Length)
        {
            text = truncated + "...";
        }
        // strings.Map dropping line-break runes (all line breaks are BMP code units).
        var b = new StringBuilder(text.Length);
        foreach (var r in text)
        {
            if (!global::Ts.StringUtil.StringUtil.IsLineBreak(r))
            {
                b.Append(r);
            }
        }
        return b.ToString();
    }

    // symbols.go:500 — getInteriorModule.
    private static Node GetInteriorModule(Node node)
    {
        while (node.Body() != null && NodeTests.IsModuleDeclaration(node.Body()!))
        {
            node = node.Body()!;
        }
        return node;
    }

    // symbols.go:507 — getModuleName.
    private static string GetModuleName(Node node)
    {
        var result = node.Name()!.Text();
        while (node.Body() != null && NodeTests.IsModuleDeclaration(node.Body()!))
        {
            node = node.Body()!;
            result = result + "." + node.Name()!.Text();
        }
        return result;
    }

    // symbols.go:640 — getSymbolKindFromNode converts an AST node to an LSP SymbolKind.
    // Combines getNodeKind with VS Code's fromProtocolScriptElementKind.
    private static SymbolKind GetSymbolKindFromNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                if (AstUtilities.IsExternalModule(node.AsSourceFile()))
                {
                    return SymbolKind.Module;
                }
                return SymbolKind.File;
            case SyntaxKind.ModuleDeclaration:
                return SymbolKind.Namespace;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return SymbolKind.Class;
            case SyntaxKind.InterfaceDeclaration:
                return SymbolKind.Interface;
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSDocTypedefTag:
            case SyntaxKind.JSDocCallbackTag:
                return SymbolKind.Class;
            case SyntaxKind.EnumDeclaration:
                return SymbolKind.Enum;
            case SyntaxKind.VariableDeclaration:
                return SymbolKind.Variable;
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
                return SymbolKind.Function;
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return SymbolKind.Property;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                return SymbolKind.Method;
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.SpreadAssignment:
            case SyntaxKind.IndexSignature:
                return SymbolKind.Property;
            case SyntaxKind.CallSignature:
                return SymbolKind.Method;
            case SyntaxKind.ConstructSignature:
                return SymbolKind.Constructor;
            case SyntaxKind.Constructor:
            case SyntaxKind.ClassStaticBlockDeclaration:
                return SymbolKind.Constructor;
            case SyntaxKind.TypeParameter:
                return SymbolKind.TypeParameter;
            case SyntaxKind.EnumMember:
                return SymbolKind.EnumMember;
            case SyntaxKind.Parameter:
                if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.ParameterPropertyModifier))
                {
                    return SymbolKind.Property;
                }
                return SymbolKind.Variable;
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.CallExpression:
                var kind = AstUtilities.GetAssignmentDeclarationKind(node);
                switch (kind)
                {
                    case JSDeclarationKind.ThisProperty:
                    case JSDeclarationKind.Property:
                    case JSDeclarationKind.ObjectDefinePropertyValue:
                        return SymbolKind.Property;
                }
                break;
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
                // String literals used as property names (e.g., in Object.defineProperty)
                return SymbolKind.Property;
        }
        return SymbolKind.Variable;
    }
}
