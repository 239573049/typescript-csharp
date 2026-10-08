// Ported from tsgo internal/ls/callhierarchy.go.
//
// PORT-NOTE: the Go entry points take an LSP document URI + line/character position and
// a CrossProjectOrchestrator. The C# LS foundation's position currency is the raw UTF-16
// offset (see LanguageService.GetNodeAtPosition), and the project layer (crossproject.go)
// is not ported, so:
//   * ProvidePrepareCallHierarchy takes fileName + raw position;
//   * ProvideCallHierarchyIncomingCalls/OutgoingCalls take the LSP CallHierarchyItem and
//     convert item.SelectionRange.Start through LsDeps.LineAndCharacterToPosition;
//   * handleCrossProject collapses to its single-project branch (orchestrator == nil):
//     provideSymbolsAndEntries → symbolAndEntriesToIncomingCalls, with the deep
//     getReferencedSymbolsForNode channel replaced by the FindAllReferences foundation
//     scan (emit-resolver identity comparison) — see SymbolAndEntriesToIncomingCalls;
//   * the lsproto response wrappers (CallHierarchyItemsOrNull et al.) become plain
//     arrays, matching the foundation entry conventions (Folding.cs, Definition.cs).
// l.getScript + l.converters.ToLSPRange dissolve into LsDeps.ToLspRange (the source-map
// detour in source_map.go is deferred with that layer). ctx.Err() checks map to
// CancellationToken.IsCancellationRequested. Positions are UTF-16 code units.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using CallHierarchyIncomingCall = Ts.LsProto.CallHierarchyIncomingCall;
using CallHierarchyItem = Ts.LsProto.CallHierarchyItem;
using CallHierarchyOutgoingCall = Ts.LsProto.CallHierarchyOutgoingCall;
using LspRange = Ts.LsProto.Range;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // Go: type CallHierarchyDeclaration = *ast.Node — a documentation alias; C# carries
    // the *ast.Node directly (and `[]*ast.Node` for the multi-declaration shape, boxed
    // in object by findImplementationOrAllInitialDeclarations).

    // callhierarchy.go:24 — isNamedExpression. Indicates whether a node is named function or class expression.
    private static bool IsNamedExpression(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        if (!NodeTests.IsFunctionExpression(node) && !NodeTests.IsClassExpression(node))
        {
            return false;
        }
        var name = node.Name();
        return name != null && NodeTests.IsIdentifier(name);
    }

    // callhierarchy.go:35 — isVariableLike.
    private static bool IsVariableLike(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        return NodeTests.IsPropertyDeclaration(node) || NodeTests.IsVariableDeclaration(node);
    }

    // callhierarchy.go:43 — isAssignedExpression. Indicates whether a node is a function, arrow, or class expression assigned to a constant variable or class property.
    private static bool IsAssignedExpression(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        if (!(NodeTests.IsFunctionExpression(node) || NodeTests.IsArrowFunction(node) || NodeTests.IsClassExpression(node)))
        {
            return false;
        }
        if (node.Name() != null)
        {
            return false;
        }
        var parent = node.Parent;
        if (!IsVariableLike(parent))
        {
            return false;
        }

        if (parent!.Initializer() != node)
        {
            return false;
        }

        var name = parent!.Name();
        if (!NodeTests.IsIdentifier(name!))
        {
            return false;
        }

        return (AstUtilities.GetCombinedNodeFlags(parent) & NodeFlags.Const) != 0 || NodeTests.IsPropertyDeclaration(parent);
    }

    // callhierarchy.go:73 — isPossibleCallHierarchyDeclaration. Indicates whether a node could possibly be a call hierarchy declaration.
    //
    // See `resolveCallHierarchyDeclaration` for the specific rules.
    private static bool IsPossibleCallHierarchyDeclaration(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        return NodeTests.IsSourceFile(node) ||
            NodeTests.IsModuleDeclaration(node) ||
            NodeTests.IsFunctionDeclaration(node) ||
            NodeTests.IsFunctionExpression(node) ||
            NodeTests.IsClassDeclaration(node) ||
            NodeTests.IsClassExpression(node) ||
            NodeTests.IsClassStaticBlockDeclaration(node) ||
            NodeTests.IsMethodDeclaration(node) ||
            NodeTests.IsMethodSignatureDeclaration(node) ||
            NodeTests.IsGetAccessorDeclaration(node) ||
            NodeTests.IsSetAccessorDeclaration(node);
    }

    // callhierarchy.go:93 — isValidCallHierarchyDeclaration. Indicates whether a node is a valid a call hierarchy declaration.
    //
    // See `resolveCallHierarchyDeclaration` for the specific rules.
    private static bool IsValidCallHierarchyDeclaration(Node? node)
    {
        if (node == null)
        {
            return false;
        }

        if (NodeTests.IsSourceFile(node))
        {
            return true;
        }

        if (NodeTests.IsModuleDeclaration(node))
        {
            return NodeTests.IsIdentifier(node.Name()!);
        }

        return NodeTests.IsFunctionDeclaration(node) ||
            NodeTests.IsClassDeclaration(node) ||
            NodeTests.IsClassStaticBlockDeclaration(node) ||
            NodeTests.IsMethodDeclaration(node) ||
            NodeTests.IsMethodSignatureDeclaration(node) ||
            NodeTests.IsGetAccessorDeclaration(node) ||
            NodeTests.IsSetAccessorDeclaration(node) ||
            IsNamedExpression(node) ||
            IsAssignedExpression(node);
    }

    // callhierarchy.go:118 — getCallHierarchyDeclarationReferenceNode. Gets the node that can be used as a reference to a call hierarchy declaration.
    private static Node? GetCallHierarchyDeclarationReferenceNode(Node? node)
    {
        if (node == null)
        {
            return null;
        }

        if (NodeTests.IsSourceFile(node))
        {
            return node;
        }

        if (node.Name() is { } name)
        {
            return name;
        }

        if (IsAssignedExpression(node))
        {
            return node.Parent!.Name();
        }

        if (node.Modifiers() is { } modifiers)
        {
            foreach (var mod in modifiers.Nodes)
            {
                if (mod.Kind == SyntaxKind.DefaultKeyword)
                {
                    return mod;
                }
            }
        }

        return null;
    }

    // callhierarchy.go:147 — getSymbolOfCallHierarchyDeclaration. Gets the symbol for a call hierarchy declaration.
    private static global::Ts.Ast.Symbol? GetSymbolOfCallHierarchyDeclaration(global::Ts.Checker.Checker c, Node? node)
    {
        if (NodeTests.IsClassStaticBlockDeclaration(node!))
        {
            return null;
        }
        var location = GetCallHierarchyDeclarationReferenceNode(node);
        if (location == null)
        {
            return null;
        }
        return c.GetSymbolAtLocation(location);
    }

    // callhierarchy.go:159 — getCallHierarchyItemName. Gets the text and range for the name of a call hierarchy declaration.
    private static (string Text, int Pos, int End) GetCallHierarchyItemName(global::Ts.Compiler.Program program, Node node)
    {
        if (NodeTests.IsSourceFile(node))
        {
            var sourceFile = node.AsSourceFile()!;
            return (sourceFile.FileName(), 0, 0);
        }

        if ((NodeTests.IsFunctionDeclaration(node) || NodeTests.IsClassDeclaration(node)) && node.Name() == null)
        {
            if (node.Modifiers() is { } declModifiers)
            {
                foreach (var mod in declModifiers.Nodes)
                {
                    if (mod.Kind == SyntaxKind.DefaultKeyword)
                    {
                        var declSourceFile = AstUtilities.GetSourceFileOfNode(node)!;
                        var start = global::Ts.Scanner.Scanner.SkipTrivia(declSourceFile.Text(), mod.Pos);
                        return ("default", start, mod.End);
                    }
                }
            }
        }

        if (NodeTests.IsClassStaticBlockDeclaration(node))
        {
            var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
            var pos = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), MoveRangePastModifiers(node).Pos);
            var end = pos + 6; // "static".length
            var (c, done) = program.GetTypeChecker(default);
            try
            {
                var symbol = c.GetSymbolAtLocation(node.Parent!);
                var prefix = "";
                if (symbol != null)
                {
                    prefix = c.SymbolToString(symbol) + " ";
                }
                return (prefix + "static {}", pos, end);
            }
            finally
            {
                done();
            }
        }

        Node? declName;
        if (IsAssignedExpression(node))
        {
            declName = node.Parent!.Name();
        }
        else
        {
            declName = AstUtilities.GetNameOfDeclaration(node);
        }

        if (declName == null || !AstUtilities.NodeIsPresent(declName))
        {
            var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
            switch (node.Kind)
            {
                case SyntaxKind.FunctionDeclaration or SyntaxKind.FunctionExpression:
                    {
                        var kwPos = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), MoveRangePastModifiers(node).Pos);
                        return ("(anonymous)", kwPos, kwPos + 8); // "function".length
                    }
                case SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression:
                    {
                        var kwPos = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), MoveRangePastModifiers(node).Pos);
                        return ("(anonymous)", kwPos, kwPos + 5); // "class".length
                    }
            }
            global::Ts.Core.Debug.Assert(declName != null, "Expected call hierarchy item to have a name");
        }

        var text = GetTextOfCallHierarchyName(program, node, declName!, node);

        var nameSourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        var namePos = global::Ts.Scanner.Scanner.SkipTrivia(nameSourceFile.Text(), declName!.Pos);

        return (text, namePos, declName.End);
    }

    // callhierarchy.go:219 — getTextOfCallHierarchyName.
    private static string GetTextOfCallHierarchyName(global::Ts.Compiler.Program program, Node sourceNode, Node name, Node printNode)
    {
        if (NodeTests.IsIdentifier(name) || AstUtilities.IsStringOrNumericLiteralLike(name))
        {
            return name.Text();
        }
        if (NodeTests.IsComputedPropertyName(name))
        {
            var expr = name.Expression();
            if (AstUtilities.IsStringOrNumericLiteralLike(expr!))
            {
                return expr!.Text();
            }
        }

        var (c, done) = program.GetTypeCheckerForFile(default, AstUtilities.GetSourceFileOfNode(sourceNode)!);
        try
        {
            var symbol = c.GetSymbolAtLocation(name);
            if (symbol != null)
            {
                var text = c.SymbolToString(symbol);
                if (text != "")
                {
                    return text;
                }
            }
        }
        finally
        {
            done();
        }

        var sourceFile = AstUtilities.GetSourceFileOfNode(sourceNode)!;
        // printer.GetSingleLineStringWriter is internal to Ts.Printer; the same behavior
        // is re-hosted as SingleLineTextWriter (see CallHierarchy.Support.cs).
        var writer = new SingleLineTextWriter();
        var p = global::Ts.Printer.Printer.NewPrinter(new global::Ts.Printer.PrinterOptions { RemoveComments = true }, new global::Ts.Printer.PrintHandlers(), null);
        p.Write(printNode, sourceFile, writer, null);
        return writer.String();
    }

    // callhierarchy.go:248 — getCallHierarchyItemContainerName.
    private static string GetCallHierarchyItemContainerName(global::Ts.Compiler.Program program, Node node)
    {
        if (IsAssignedExpression(node))
        {
            var parent = node.Parent!;
            if (NodeTests.IsPropertyDeclaration(parent) && AstUtilities.IsClassLike(parent.Parent!))
            {
                if (NodeTests.IsClassExpression(parent.Parent!))
                {
                    if (AstUtilities.GetAssignedName(parent.Parent!) is { } assignedName)
                    {
                        return GetTextOfCallHierarchyName(program, node, assignedName, assignedName);
                    }
                }
                else
                {
                    if (parent.Parent!.Name() is { } name)
                    {
                        return GetTextOfCallHierarchyName(program, node, name, name);
                    }
                }
            }
            if (parent.Parent!.Parent != null && parent.Parent!.Parent!.Parent != null && NodeTests.IsModuleBlock(parent.Parent!.Parent!.Parent!))
            {
                var modParent = parent.Parent!.Parent!.Parent!.Parent;
                if (NodeTests.IsModuleDeclaration(modParent!))
                {
                    if (modParent!.Name() is { } name && NodeTests.IsIdentifier(name))
                    {
                        return name.Text();
                    }
                }
            }
            return "";
        }

        switch (node.Kind)
        {
            case SyntaxKind.GetAccessor or SyntaxKind.SetAccessor or SyntaxKind.MethodDeclaration:
                {
                    if (node.Parent!.Kind == SyntaxKind.ObjectLiteralExpression)
                    {
                        if (AstUtilities.GetAssignedName(node.Parent!) is { } assignedName)
                        {
                            return GetTextOfCallHierarchyName(program, node, assignedName, assignedName);
                        }
                    }
                    if (AstUtilities.GetNameOfDeclaration(node.Parent!) is { } name)
                    {
                        return GetTextOfCallHierarchyName(program, node, name, name);
                    }
                    break;
                }
            case SyntaxKind.FunctionDeclaration or SyntaxKind.ClassDeclaration or SyntaxKind.ModuleDeclaration:
                {
                    if (NodeTests.IsModuleBlock(node.Parent!))
                    {
                        if (NodeTests.IsModuleDeclaration(node.Parent!.Parent!))
                        {
                            if (node.Parent!.Parent!.Name() is { } name && NodeTests.IsIdentifier(name))
                            {
                                return name.Text();
                            }
                        }
                    }
                    break;
                }
        }

        return "";
    }

    // callhierarchy.go:296 — moveRangePastModifiers.
    private static global::Ts.Core.TextRange MoveRangePastModifiers(Node node)
    {
        if (node.Modifiers() is { } modifiers && modifiers.Nodes.Length > 0)
        {
            var lastMod = modifiers.Nodes[modifiers.Nodes.Length - 1];
            return new global::Ts.Core.TextRange(lastMod.End, node.End);
        }
        return new global::Ts.Core.TextRange(node.Pos, node.End);
    }

    // callhierarchy.go:305 — findImplementation. Finds the implementation of a function-like declaration, if one exists.
    private static Node? FindImplementation(global::Ts.Checker.Checker c, Node? node)
    {
        if (node == null)
        {
            return null;
        }

        if (!AstUtilities.IsFunctionLikeDeclaration(node))
        {
            return node;
        }

        if (node.Body() != null)
        {
            return node;
        }

        if (NodeTests.IsConstructorDeclaration(node))
        {
            return AstUtilities.GetFirstConstructorWithBody(node.Parent!);
        }

        if (NodeTests.IsFunctionDeclaration(node) || NodeTests.IsMethodDeclaration(node))
        {
            var symbol = GetSymbolOfCallHierarchyDeclaration(c, node);
            if (symbol != null && symbol.ValueDeclaration != null)
            {
                if (AstUtilities.IsFunctionLikeDeclaration(symbol.ValueDeclaration) && symbol.ValueDeclaration.Body() != null)
                {
                    return symbol.ValueDeclaration;
                }
            }
            return null;
        }

        return node;
    }

    // callhierarchy.go:335 — findAllInitialDeclarations.
    private static List<Node>? FindAllInitialDeclarations(global::Ts.Checker.Checker c, Node? node)
    {
        if (NodeTests.IsClassStaticBlockDeclaration(node!))
        {
            return null;
        }

        var symbol = GetSymbolOfCallHierarchyDeclaration(c, node);
        if (symbol == null || symbol.Declarations == null)
        {
            return null;
        }

        var declarations = symbol.Declarations;

        // Sort declaration indices by (file name, position) so the merged stream below is
        // order-independent (Go sorts `indices` the same way).
        var indices = new int[declarations.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }
        var keys = new (string File, int Pos)[declarations.Length];
        for (var i = 0; i < declarations.Length; i++)
        {
            var decl = declarations[i];
            keys[i] = (AstUtilities.GetSourceFileOfNode(decl)!.FileName(), decl.Pos);
        }

        Array.Sort(indices, (a, b) =>
        {
            if (keys[a].File != keys[b].File)
            {
                return string.CompareOrdinal(keys[a].File, keys[b].File);
            }
            return keys[a].Pos.CompareTo(keys[b].Pos);
        });

        List<Node>? result = null;
        Node? lastDecl = null;

        foreach (var i in indices)
        {
            var decl = declarations[i];
            if (IsValidCallHierarchyDeclaration(decl))
            {
                if (lastDecl == null || lastDecl.Parent != decl.Parent || lastDecl.End != decl.Pos)
                {
                    (result ??= new List<Node>()).Add(decl);
                }
                lastDecl = decl;
            }
        }

        return result;
    }

    // callhierarchy.go:386 — findImplementationOrAllInitialDeclarations. Find the implementation or the first declaration for a call hierarchy declaration.
    // Returns *ast.Node (boxed Node) or []*ast.Node (Node[]).
    private static object? FindImplementationOrAllInitialDeclarations(global::Ts.Checker.Checker c, Node? node)
    {
        if (NodeTests.IsClassStaticBlockDeclaration(node!))
        {
            return node;
        }

        if (AstUtilities.IsFunctionLikeDeclaration(node!))
        {
            var impl = FindImplementation(c, node);
            if (impl != null)
            {
                return impl;
            }
            var decls = FindAllInitialDeclarations(c, node);
            if (decls != null)
            {
                return decls.ToArray();
            }
            return node;
        }

        var declarations = FindAllInitialDeclarations(c, node);
        if (declarations != null)
        {
            return declarations.ToArray();
        }
        return node;
    }

    // callhierarchy.go:408 — resolveCallHierarchyDeclaration. Resolves the call hierarchy declaration for a node.
    // A call hierarchy item must refer to either a SourceFile, Module Declaration, Class Static Block, or something intrinsically callable that has a name:
    // - Class Declarations
    // - Class Expressions (with a name)
    // - Function Declarations
    // - Function Expressions (with a name or assigned to a const variable)
    // - Arrow Functions (assigned to a const variable)
    // - Constructors
    // - Class `static {}` initializer blocks
    // - Methods
    // - Accessors
    //
    // If a call is contained in a non-named callable Node (function expression, arrow function, etc.), then
    // its containing `CallHierarchyItem` is a containing function or SourceFile that matches the above list.
    private static object? ResolveCallHierarchyDeclaration(global::Ts.Compiler.Program program, Node? location)
    {
        var (c, done) = program.GetTypeChecker(default);
        try
        {
            var followingSymbol = false;

            while (location != null)
            {
                if (IsValidCallHierarchyDeclaration(location))
                {
                    return FindImplementationOrAllInitialDeclarations(c, location);
                }

                if (IsPossibleCallHierarchyDeclaration(location))
                {
                    var ancestor = AstUtilities.FindAncestor(location, IsValidCallHierarchyDeclaration);
                    if (ancestor != null)
                    {
                        return FindImplementationOrAllInitialDeclarations(c, ancestor);
                    }
                }

                if (AstUtilities.IsDeclarationName(location))
                {
                    if (IsValidCallHierarchyDeclaration(location.Parent))
                    {
                        return FindImplementationOrAllInitialDeclarations(c, location.Parent);
                    }
                    if (IsPossibleCallHierarchyDeclaration(location.Parent))
                    {
                        var ancestor = AstUtilities.FindAncestor(location.Parent!, IsValidCallHierarchyDeclaration);
                        if (ancestor != null)
                        {
                            return FindImplementationOrAllInitialDeclarations(c, ancestor);
                        }
                    }
                    if (IsVariableLike(location.Parent))
                    {
                        var initializer = location.Parent!.Initializer();
                        if (initializer != null && IsAssignedExpression(initializer))
                        {
                            return initializer;
                        }
                    }
                    return null;
                }

                if (NodeTests.IsConstructorDeclaration(location))
                {
                    if (IsValidCallHierarchyDeclaration(location.Parent))
                    {
                        return location.Parent;
                    }
                    return null;
                }

                if (location.Kind == SyntaxKind.StaticKeyword && NodeTests.IsClassStaticBlockDeclaration(location.Parent!))
                {
                    location = location.Parent;
                    continue;
                }

                // #39453
                if (NodeTests.IsVariableDeclaration(location))
                {
                    if (location.Initializer() is { } initializer && IsAssignedExpression(initializer))
                    {
                        return initializer;
                    }
                }

                if (!followingSymbol)
                {
                    var symbol = c.GetSymbolAtLocation(location);
                    if (symbol != null)
                    {
                        if ((symbol.Flags & SymbolFlags.Alias) != 0)
                        {
                            symbol = c.GetAliasedSymbol(symbol)!;
                        }
                        if (symbol.ValueDeclaration != null)
                        {
                            followingSymbol = true;
                            location = symbol.ValueDeclaration;
                            continue;
                        }
                    }
                }

                return null;
            }

            return null;
        }
        finally
        {
            done();
        }
    }

    // callhierarchy.go:499 — createCallHierarchyItem. Creates a `CallHierarchyItem` for a call hierarchy declaration.
    private CallHierarchyItem CreateCallHierarchyItem(global::Ts.Compiler.Program program, Node node)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        var (nameText, namePos, nameEnd) = GetCallHierarchyItemName(program, node);
        var containerName = GetCallHierarchyItemContainerName(program, node);

        var kind = GetSymbolKindFromNode(node);

        var fullStart = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), node.Pos, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = true });
        var span = LsDeps.ToLspRange(sourceFile, new global::Ts.Core.TextRange(fullStart, node.End));
        var selectionSpan = LsDeps.ToLspRange(sourceFile, new global::Ts.Core.TextRange(namePos, nameEnd));

        var item = new CallHierarchyItem
        {
            Name = nameText,
            Kind = kind,
            Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(sourceFile.FileName()),
            Range = span,
            SelectionRange = selectionSpan,
        };

        if (containerName != "")
        {
            item.Detail = containerName;
        }

        return item;
    }

    // callhierarchy.go:526 — callSite.
    private sealed record CallSite(Node Declaration, global::Ts.Core.TextRange TextRange, SourceFile SourceFile);

    // callhierarchy.go:532 — convertEntryToCallSite. The Go input is a findallreferences
    // ReferenceEntry restricted to kind == entryKindNode; the foundation reference scan
    // (see FindIncomingCallReferences) only produces node entries, so the kind check
    // dissolves.
    private static CallSite? ConvertEntryToCallSite(Node entryNode)
    {
        var node = entryNode;
        if (!CallHierarchyAstHelpers.IsCallOrNewExpressionTarget(node, true /*includeElementAccess*/, true /*skipPastOuterExpressions*/) &&
            !CallHierarchyAstHelpers.IsTaggedTemplateTag(node, true, true) &&
            !CallHierarchyAstHelpers.IsDecoratorTarget(node, true, true) &&
            !CallHierarchyAstHelpers.IsJsxOpeningLikeElementTagName(node, true, true) &&
            !CallHierarchyAstHelpers.IsRightSideOfPropertyAccess(node) &&
            !CallHierarchyAstHelpers.IsArgumentExpressionOfElementAccess(node))
        {
            return null;
        }

        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        var ancestor = AstUtilities.FindAncestor(node, IsValidCallHierarchyDeclaration);
        ancestor ??= sourceFile.AsNode();

        var start = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), node.Pos);
        return new CallSite(
            ancestor!,
            new global::Ts.Core.TextRange(start, node.End),
            sourceFile);
    }

    // callhierarchy.go:561 — getCallSiteGroupKey.
    private static global::Ts.Ast.NodeId GetCallSiteGroupKey(CallSite site)
    {
        return AstUtilities.GetNodeId(site.Declaration);
    }

    // callhierarchy.go:565 — convertCallSiteGroupToIncomingCall.
    private CallHierarchyIncomingCall ConvertCallSiteGroupToIncomingCall(global::Ts.Compiler.Program program, List<CallSite> entries)
    {
        var fromRanges = new LspRange[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            fromRanges[i] = LsDeps.ToLspRange(entry.SourceFile, entry.TextRange);
        }

        Array.Sort(fromRanges, global::Ts.LsProto.Util.CompareRanges);

        return new CallHierarchyIncomingCall
        {
            From = CreateCallHierarchyItem(program, entries[0].Declaration),
            FromRanges = fromRanges,
        };
    }

    // callhierarchy.go:580 — incomingEntry. Go's sync.Once fields become Lazy<T>.
    private sealed class IncomingCallEntry
    {
        public IncomingCallEntry(LanguageService ls, Node node)
        {
            this.ls = ls;
            this.node = node;
            lazySourceFile = new(() => AstUtilities.GetSourceFileOfNode(this.node)!);
            lazyDocumentUri = new(() => global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(GetSourceFile().FileName()));
            lazyPosition = new(() =>
            {
                var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(this.node, GetSourceFile(), false /*includeJsDoc*/);
                return LsDeps.PositionToLineAndCharacter(GetSourceFile(), start);
            });
        }

        private readonly LanguageService ls;
        private readonly Node node;

        private readonly Lazy<SourceFile> lazySourceFile;
        private readonly Lazy<global::Ts.LsProto.DocumentUri> lazyDocumentUri;
        private readonly Lazy<global::Ts.LsProto.Position> lazyPosition;

        // Go: getSourceFile.
        public SourceFile GetSourceFile() => lazySourceFile.Value;

        // Go: TextDocumentURI (lsproto.HasTextDocumentPosition).
        public global::Ts.LsProto.DocumentUri TextDocumentURI() => lazyDocumentUri.Value;

        // Go: TextDocumentPosition (lsproto.HasTextDocumentPosition).
        public global::Ts.LsProto.Position TextDocumentPosition() => lazyPosition.Value;
    }

    // callhierarchy.go:619 — getIncomingCalls. Gets the call sites that call into the provided call hierarchy declaration.
    // PORT-NOTE: Go dispatches through handleCrossProject(..., combineIncomingCalls, ...); the
    // C# foundation has no project layer, so the single-project branch runs inline: gather the
    // references for the declaration's reference node, then symbolAndEntriesToIncomingCalls.
    private CallHierarchyIncomingCall[] GetIncomingCalls(CancellationToken cancellationToken, global::Ts.Compiler.Program program, Node declaration)
    {
        // Source files and modules have no incoming calls.
        if (NodeTests.IsSourceFile(declaration) || NodeTests.IsModuleDeclaration(declaration) || NodeTests.IsClassStaticBlockDeclaration(declaration))
        {
            return [];
        }

        var location = GetCallHierarchyDeclarationReferenceNode(declaration);
        if (location == null)
        {
            return [];
        }

        var incomingEntry = new IncomingCallEntry(this, location);

        var result = SymbolAndEntriesToIncomingCalls(
            cancellationToken,
            incomingEntry,
            FindIncomingCallReferences(incomingEntry.TextDocumentURI().Value, LsDeps.LineAndCharacterToPosition(incomingEntry.GetSourceFile(), incomingEntry.TextDocumentPosition().Line, incomingEntry.TextDocumentPosition().Character), cancellationToken));

        if (result != null)
        {
            Array.Sort(result, (a, b) =>
            {
                var uriComp = string.CompareOrdinal(a.From.Uri.Value, b.From.Uri.Value);
                if (uriComp != 0)
                {
                    return uriComp;
                }
                if (a.FromRanges.Length == 0 || b.FromRanges.Length == 0)
                {
                    return 0;
                }
                return global::Ts.LsProto.Util.CompareRanges(a.FromRanges[0], b.FromRanges[0]);
            });
        }
        return result ?? [];
    }

    // findallreferences.go:628-692 — provideSymbolsAndEntries + getSymbolAndEntries, reduced
    // to the node-reference channel the foundation can answer (see FindAllReferences.cs):
    // identifier/private-identifier use-sites whose emit-resolver declaration matches the
    // queried node's. The deep families (labels, this/super, string/numeric literals,
    // import/export alias chains) are deferred with getReferencedSymbolsForNode.
    private Node[]? FindIncomingCallReferences(string fileName, int position, CancellationToken cancellationToken)
    {
        var file = GetProgramAndFile(fileName);
        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);
        if (node.Kind == SyntaxKind.SourceFile)
        {
            return null;
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var emitResolver = checker.GetEmitResolver();
            var targetDeclaration = GetReferencedDeclaration(emitResolver, node);
            if (targetDeclaration is null)
            {
                return null;
            }

            var searchName = node.Text();
            var references = new List<Node>();
            foreach (var sourceFile in program.SourceFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var reference in GetPossibleSymbolReferenceNodes(sourceFile, searchName, null))
                {
                    if (!IsValidReferencePosition(reference, searchName))
                    {
                        continue;
                    }
                    if (!ReferenceEquals(GetReferencedDeclaration(emitResolver, reference), targetDeclaration))
                    {
                        continue;
                    }
                    references.Add(reference);
                }
            }
            return references.ToArray();
        }
        finally
        {
            done();
        }
    }

    // callhierarchy.go:660 — symbolAndEntriesToIncomingCalls. The Go input flattens
    // data.SymbolsAndEntries[*].references (kind == entryKindNode); the foundation scan
    // already returns exactly those node entries, flattened.
    private CallHierarchyIncomingCall[]? SymbolAndEntriesToIncomingCalls(
        CancellationToken cancellationToken,
        IncomingCallEntry incomingEntry,
        Node[]? refEntries)
    {
        var program = GetProgram();

        var callSites = new List<CallSite>();
        if (refEntries != null)
        {
            foreach (var entry in refEntries)
            {
                if (ConvertEntryToCallSite(entry) is { } site)
                {
                    callSites.Add(site);
                }
            }
        }

        if (callSites.Count == 0)
        {
            return null;
        }

        // map-iter: 已排序 — grouped results are sorted by (From.Uri, FromRanges[0]) in
        // GetIncomingCalls before returning, so dictionary order does not leak.
        var grouped = new Dictionary<global::Ts.Ast.NodeId, List<CallSite>>();
        foreach (var site in callSites)
        {
            var key = GetCallSiteGroupKey(site);
            if (!grouped.TryGetValue(key, out var group))
            {
                group = new List<CallSite>();
                grouped[key] = group;
            }
            group.Add(site);
        }

        var result = new List<CallHierarchyIncomingCall>();
        foreach (var sites in grouped.Values)
        {
            result.Add(ConvertCallSiteGroupToIncomingCall(program, sites));
        }
        return result.ToArray();
    }

    // callhierarchy.go:691 — callSiteCollector.
    private sealed class CallSiteCollector
    {
        public CallSiteCollector(global::Ts.Compiler.Program program)
        {
            this.program = program;
        }

        private readonly global::Ts.Compiler.Program program;
        public readonly List<CallSite> CallSites = new();

        // callhierarchy.go:696 — recordCallSite.
        public void RecordCallSite(Node node)
        {
            Node? target = null;

            if (NodeTests.IsTaggedTemplateExpression(node))
            {
                target = node.AsTaggedTemplateExpression().Tag;
            }
            else if (NodeTests.IsJsxOpeningElement(node))
            {
                target = node.TagName();
            }
            else if (NodeTests.IsJsxSelfClosingElement(node))
            {
                target = node.TagName();
            }
            else if (NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsElementAccessExpression(node))
            {
                target = node;
            }
            else if (NodeTests.IsClassStaticBlockDeclaration(node))
            {
                target = node;
            }
            else if (NodeTests.IsCallExpression(node))
            {
                target = node.Expression();
            }
            else if (NodeTests.IsNewExpression(node))
            {
                target = node.Expression();
            }
            else if (NodeTests.IsDecorator(node))
            {
                target = node.Expression();
            }

            if (target == null)
            {
                return;
            }

            var declaration = ResolveCallHierarchyDeclaration(program, target);
            if (declaration == null)
            {
                return;
            }

            var sourceFile = AstUtilities.GetSourceFileOfNode(target)!;
            var start = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), target.Pos);
            var textRange = new global::Ts.Core.TextRange(start, target.End);

            switch (declaration)
            {
                case Node decl:
                    CallSites.Add(new CallSite(decl, textRange, sourceFile));
                    break;
                case Node[] decls:
                    foreach (var d in decls)
                    {
                        CallSites.Add(new CallSite(d, textRange, sourceFile));
                    }
                    break;
            }
        }

        // callhierarchy.go:749 — collect.
        public void Collect(Node? node)
        {
            if (node == null)
            {
                return;
            }

            // do not descend into ambient nodes.
            if ((node.Flags & NodeFlags.Ambient) != 0)
            {
                return;
            }

            // do not descend into other call site declarations, other than class member names
            if (IsValidCallHierarchyDeclaration(node))
            {
                if (AstUtilities.IsClassLike(node))
                {
                    foreach (var member in node.Members()!)
                    {
                        if (member.Name() != null && NodeTests.IsComputedPropertyName(member.Name()!))
                        {
                            Collect(member.Name()!.Expression());
                        }
                    }
                }
                return;
            }

            switch (node.Kind)
            {
                case SyntaxKind.Identifier or SyntaxKind.ImportEqualsDeclaration or SyntaxKind.ImportDeclaration or SyntaxKind.ExportDeclaration or SyntaxKind.InterfaceDeclaration or SyntaxKind.TypeAliasDeclaration:
                    // do not descend into nodes that cannot contain callable nodes
                    return;
                case SyntaxKind.ClassStaticBlockDeclaration:
                    RecordCallSite(node);
                    return;
                case SyntaxKind.TypeAssertionExpression or SyntaxKind.AsExpression:
                    // do not descend into the type side of an assertion
                    Collect(node.Expression());
                    return;
                case SyntaxKind.VariableDeclaration or SyntaxKind.Parameter:
                    // do not descend into the type of a variable or parameter declaration
                    Collect(node.Name());
                    Collect(node.Initializer());
                    return;
                case SyntaxKind.CallExpression:
                    // do not descend into the type arguments of a call expression
                    RecordCallSite(node);
                    Collect(node.Expression());
                    foreach (var arg in node.Arguments()!)
                    {
                        Collect(arg);
                    }
                    return;
                case SyntaxKind.NewExpression:
                    // do not descend into the type arguments of a new expression
                    RecordCallSite(node);
                    Collect(node.Expression());
                    foreach (var arg in node.Arguments()!)
                    {
                        Collect(arg);
                    }
                    return;
                case SyntaxKind.TaggedTemplateExpression:
                    // do not descend into the type arguments of a tagged template expression
                    RecordCallSite(node);
                    var taggedTemplate = node.AsTaggedTemplateExpression();
                    Collect(taggedTemplate.Tag);
                    Collect(taggedTemplate.Template);
                    return;
                case SyntaxKind.JsxOpeningElement or SyntaxKind.JsxSelfClosingElement:
                    // do not descend into the type arguments of a JsxOpeningLikeElement
                    RecordCallSite(node);
                    Collect(node.TagName());
                    Collect(node.Attributes());
                    return;
                case SyntaxKind.Decorator:
                    RecordCallSite(node);
                    Collect(node.Expression());
                    return;
                case SyntaxKind.PropertyAccessExpression or SyntaxKind.ElementAccessExpression:
                    RecordCallSite(node);
                    node.ForEachChild(child =>
                    {
                        Collect(child);
                        return false;
                    });
                    return;
                case SyntaxKind.SatisfiesExpression:
                    // do not descend into the type side of an assertion
                    Collect(node.Expression());
                    return;
            }

            if (AstUtilities.IsPartOfTypeNode(node))
            {
                // do not descend into types
                return;
            }

            node.ForEachChild(child =>
            {
                Collect(child);
                return false;
            });
        }
    }

    // callhierarchy.go:849 — collectCallSites.
    private static List<CallSite> CollectCallSites(global::Ts.Compiler.Program program, global::Ts.Checker.Checker c, Node node)
    {
        var collector = new CallSiteCollector(program);

        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                foreach (var stmt in node.Statements()!)
                {
                    collector.Collect(stmt);
                }
                break;

            case SyntaxKind.ModuleDeclaration:
                {
                    var body = node.Body();
                    if (!AstUtilities.HasSyntacticModifier(node, ModifierFlags.Ambient) && body != null && NodeTests.IsModuleBlock(body))
                    {
                        foreach (var stmt in body.Statements()!)
                        {
                            collector.Collect(stmt);
                        }
                    }
                    break;
                }

            case SyntaxKind.FunctionDeclaration or SyntaxKind.FunctionExpression or SyntaxKind.ArrowFunction or SyntaxKind.MethodDeclaration or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor:
                {
                    var impl = FindImplementation(c, node);
                    if (impl != null)
                    {
                        foreach (var param in impl.Parameters())
                        {
                            collector.Collect(param);
                        }
                        collector.Collect(impl.Body());
                    }
                    break;
                }

            case SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression:
                {
                    if (node.Modifiers() is { } modifiers)
                    {
                        foreach (var mod in modifiers.Nodes)
                        {
                            collector.Collect(mod);
                        }
                    }

                    var heritage = AstUtilities.GetClassExtendsHeritageElement(node);
                    if (heritage != null)
                    {
                        collector.Collect(heritage.Expression());
                    }

                    foreach (var member in node.Members()!)
                    {
                        if (LsDeps.CanHaveModifiers(member) && member.Modifiers() != null)
                        {
                            foreach (var mod in member.Modifiers()!.Nodes)
                            {
                                collector.Collect(mod);
                            }
                        }

                        if (NodeTests.IsPropertyDeclaration(member))
                        {
                            collector.Collect(member.Initializer());
                        }
                        else if (NodeTests.IsConstructorDeclaration(member))
                        {
                            if (member.Body() is { } body)
                            {
                                foreach (var param in member.Parameters())
                                {
                                    collector.Collect(param);
                                }
                                collector.Collect(body);
                            }
                        }
                        else if (NodeTests.IsClassStaticBlockDeclaration(member))
                        {
                            collector.Collect(member);
                        }
                    }
                    break;
                }

            case SyntaxKind.ClassStaticBlockDeclaration:
                {
                    var staticBlock = node.AsClassStaticBlockDeclaration();
                    collector.Collect(staticBlock!.Body);
                    break;
                }

            default:
                global::Ts.Core.Debug.AssertNever(node);
                break;
        }

        return collector.CallSites;
    }

    // callhierarchy.go:922 — convertCallSiteGroupToOutgoingCall.
    private CallHierarchyOutgoingCall ConvertCallSiteGroupToOutgoingCall(global::Ts.Compiler.Program program, List<CallSite> entries)
    {
        var fromRanges = new LspRange[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            fromRanges[i] = LsDeps.ToLspRange(entry.SourceFile, entry.TextRange);
        }

        Array.Sort(fromRanges, global::Ts.LsProto.Util.CompareRanges);

        return new CallHierarchyOutgoingCall
        {
            To = CreateCallHierarchyItem(program, entries[0].Declaration),
            FromRanges = fromRanges,
        };
    }

    // callhierarchy.go:938 — getOutgoingCalls. Gets the call sites that call out of the provided call hierarchy declaration.
    private CallHierarchyOutgoingCall[]? GetOutgoingCalls(global::Ts.Compiler.Program program, Node declaration)
    {
        if ((declaration.Flags & NodeFlags.Ambient) != 0 || NodeTests.IsMethodSignatureDeclaration(declaration))
        {
            return null;
        }

        var (c, done) = program.GetTypeChecker(default);
        try
        {
            var callSites = CollectCallSites(program, c, declaration);

            if (callSites.Count == 0)
            {
                return null;
            }

            // map-iter: 已排序 — grouped results are sorted by (To.Uri, FromRanges[0]) below,
            // so dictionary order does not leak.
            var grouped = new Dictionary<global::Ts.Ast.NodeId, List<CallSite>>();
            foreach (var site in callSites)
            {
                var key = GetCallSiteGroupKey(site);
                if (!grouped.TryGetValue(key, out var group))
                {
                    group = new List<CallSite>();
                    grouped[key] = group;
                }
                group.Add(site);
            }

            var result = new List<CallHierarchyOutgoingCall>();
            foreach (var sites in grouped.Values)
            {
                result.Add(ConvertCallSiteGroupToOutgoingCall(program, sites));
            }

            var calls = result.ToArray();
            Array.Sort(calls, (a, b) =>
            {
                var uriComp = string.CompareOrdinal(a.To.Uri.Value, b.To.Uri.Value);
                if (uriComp != 0)
                {
                    return uriComp;
                }
                if (a.FromRanges.Length == 0 || b.FromRanges.Length == 0)
                {
                    return 0;
                }
                return global::Ts.LsProto.Util.CompareRanges(a.FromRanges[0], b.FromRanges[0]);
            });

            return calls;
        }
        finally
        {
            done();
        }
    }

    // callhierarchy.go:976 — ProvidePrepareCallHierarchy.
    public CallHierarchyItem[] ProvidePrepareCallHierarchy(
        string documentURI,
        int position,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(documentURI);
        var program = GetProgram();
        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);

        if (node.Kind == SyntaxKind.SourceFile)
        {
            return [];
        }

        var declaration = ResolveCallHierarchyDeclaration(program, node);
        if (declaration == null)
        {
            return [];
        }

        List<CallHierarchyItem>? items = null;
        switch (declaration)
        {
            case Node decl:
                items = [CreateCallHierarchyItem(program, decl)];
                break;
            case Node[] decls:
                items = new List<CallHierarchyItem>(decls.Length);
                foreach (var d in decls)
                {
                    items.Add(CreateCallHierarchyItem(program, d));
                }
                break;
        }

        if (items == null)
        {
            return [];
        }
        return items.ToArray();
    }

    // callhierarchy.go:1010 — ProvideCallHierarchyIncomingCalls. PORT-NOTE: Go threads a
    // CrossProjectOrchestrator; collapsed to the single-project path (see GetIncomingCalls).
    public CallHierarchyIncomingCall[] ProvideCallHierarchyIncomingCalls(
        CallHierarchyItem item,
        CancellationToken cancellationToken = default)
    {
        var program = GetProgram();
        var fileName = item.Uri.FileName();
        var file = program.GetSourceFile(fileName);
        if (file == null)
        {
            return [];
        }

        var pos = LsDeps.LineAndCharacterToPosition(file, item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
        Node? node;
        if (pos == 0)
        {
            node = file.AsNode();
        }
        else
        {
            node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, pos);
        }

        if (node == null)
        {
            return [];
        }

        var declaration = ResolveCallHierarchyDeclaration(program, node);
        if (declaration == null)
        {
            return [];
        }

        Node? decl;
        switch (declaration)
        {
            case Node d:
                decl = d;
                break;
            case Node[] d:
                decl = d.Length > 0 ? d[0] : null;
                break;
            default:
                decl = null;
                break;
        }

        if (decl == null)
        {
            return [];
        }

        return GetIncomingCalls(cancellationToken, program, decl);
    }

    // callhierarchy.go:1056 — ProvideCallHierarchyOutgoingCalls.
    public CallHierarchyOutgoingCall[] ProvideCallHierarchyOutgoingCalls(
        CallHierarchyItem item,
        CancellationToken cancellationToken = default)
    {
        var program = GetProgram();
        var fileName = item.Uri.FileName();
        var file = program.GetSourceFile(fileName);
        if (file == null)
        {
            return [];
        }

        var pos = LsDeps.LineAndCharacterToPosition(file, item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
        Node? node;
        if (pos == 0)
        {
            node = file.AsNode();
        }
        else
        {
            node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, pos);
        }

        if (node == null)
        {
            return [];
        }

        var declaration = ResolveCallHierarchyDeclaration(program, node);
        if (declaration == null)
        {
            return [];
        }

        Node? decl;
        switch (declaration)
        {
            case Node d:
                decl = d;
                break;
            case Node[] d:
                decl = d.Length > 0 ? d[0] : null;
                break;
            default:
                decl = null;
                break;
        }

        if (decl == null)
        {
            return [];
        }

        var calls = GetOutgoingCalls(program, decl);
        return calls ?? [];
    }
}
