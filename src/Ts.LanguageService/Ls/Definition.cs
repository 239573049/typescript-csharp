// Ported from tsgo internal/ls/definition.go (full worker).
//
// The declaration-collection helpers the Go worker leans on are already hosted by
// SourceDefinition.Support.cs (Wave 5) and are reused here, not duplicated:
//   getDeclarationsFromLocation → SourceDefinitionDeps.GetDeclarationsFromLocation,
//   getDeclarationNameForKeyword → SourceDefinitionDeps.GetDeclarationNameForKeyword,
//   getDeclarationsFromObjectLiteralElement → SourceDefinitionDeps.GetDeclarationsFromObjectLiteralElement,
//   getAncestorCallLikeExpression → SourceDefinitionDeps.GetAncestorCallLikeExpression,
//   tryGetSignatureDeclaration → SourceDefinitionDeps.TryGetSignatureDeclaration,
//   createLocationsFromLinks → SourceDefinitionDeps.CreateLocationsFromLinks,
//   getReferenceAtPosition → SourceDefinitionDeps.GetReferenceAtPosition,
//   getContextNode / toContextRange / getRangeOfNode → SourceDefinitionDeps / FindAllReferences.cs.
// The raw-position entry `GetDefinitionAtPosition` keeps its DefinitionLocation[] payload
// for the existing callers; `ProvideDefinition`/`ProvideTypeDefinition` are the Go-shaped
// LSP entries riding the same worker. Go's per-request lsproto.GetClientCapabilities(ctx)
// is the instance-held ClientCapabilities() (LanguageService.cs field note).

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerLsExports extension methods
using Ts.Collections; // Set<T>

namespace Ts.Ls;

public sealed record DefinitionLocation(string FileName, global::Ts.Core.TextRange TextSpan);

public sealed partial class LanguageService
{
    // definition.go:17-26 — ProvideDefinition.
    public Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull? ProvideDefinition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        // definition.go:22 — PreferGoToSourceDefinition routes to ProvideSourceDefinition;
        // the preference member is not on the carrier yet (Go default false).
        var node = GetNodeAtPosition(fileName, position, out var file);
        return ProvideDefinitionWorker(cancellationToken, node, file, position);
    }

    // definition.go:28-98 — provideDefinitionWorker.
    private Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull? ProvideDefinitionWorker(
        CancellationToken ctx,
        Node node,
        SourceFile file,
        int pos)
    {
        // definition.go:33-34
        bool clientSupportsLink = ClientCapabilities().TextDocument.Definition.LinkSupport;

        var reference = SourceDefinitionDeps.GetReferenceAtPosition(file, pos, program);

        if (node.Kind == SyntaxKind.SourceFile)
        {
            return new Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull();
        }

        var originSelectionRange = LsDeps.ToLspRange(file, GetRangeOfNode(node, file, null));
        if (reference != null && reference.File != null)
        {
            return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, [], reference);
        }

        var (c, done) = host.GetTypeCheckerForFile(ctx, file);
        try
        {
            // definition.go:65-70 — the case/default-keyword arm maps to the switch
            // statement's keyword range; the LSP shape returns the single mapped location.
            if (node.Kind == SyntaxKind.CaseKeyword || node.Kind == SyntaxKind.DefaultKeyword && NodeTests.IsDefaultClause(node.Parent!))
            {
                var stmt = AstUtilities.FindAncestor(node.Parent!, NodeTests.IsSwitchStatement);
                if (stmt != null)
                {
                    var stmtFile = AstUtilities.GetSourceFileOfNode(stmt)!;
                    return CreateLocationFromFileAndRange(stmtFile, global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(stmtFile, stmt.Pos));
                }
            }

            var declarations = CollectDefinitionDeclarations(node, file, c);
            return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, declarations, reference);
        }
        finally
        {
            done();
        }
    }

    // definition.go:50-97 — the checker-driven declaration collection, shared by the raw
    // and LSP entries (the case/default-keyword arm lives in the LSP worker above; the
    // raw payload surfaces the switch statement itself).
    private List<Node> CollectDefinitionDeclarations(Node node, SourceFile file, global::Ts.Checker.Checker c)
    {
        if (node.Kind == SyntaxKind.OverrideKeyword)
        {
            if (GetSymbolForOverriddenMember(c, node) is { } sym)
            {
                return [.. sym.Declarations ?? []];
            }
        }

        if (LsDeps.IsJumpStatementTarget(node))
        {
            if (FindRefsSupport.GetTargetLabel(node.Parent, node.Text()) is { } label)
            {
                return [label];
            }
        }

        if (node.Kind == SyntaxKind.CaseKeyword || node.Kind == SyntaxKind.DefaultKeyword && NodeTests.IsDefaultClause(node.Parent!))
        {
            var stmt = AstUtilities.FindAncestor(node.Parent!, NodeTests.IsSwitchStatement);
            if (stmt != null)
            {
                return [stmt];
            }
        }

        if (node.Kind == SyntaxKind.ReturnKeyword || node.Kind == SyntaxKind.YieldKeyword || node.Kind == SyntaxKind.AwaitKeyword)
        {
            var fn = AstUtilities.FindAncestor(node, AstUtilities.IsFunctionLikeDeclaration);
            if (fn != null)
            {
                return [fn];
            }
        }

        var declarations = SourceDefinitionDeps.GetDeclarationsFromLocation(c, node);
        var calledDeclaration = SourceDefinitionDeps.TryGetSignatureDeclaration(c, node);
        if (calledDeclaration != null && !(AstUtilities.IsJsxOpeningLikeElement(node.Parent!) && IsJsxConstructorLike(calledDeclaration)))
        {
            var symbol = c.GetSymbolAtLocation(SourceDefinitionDeps.GetDeclarationNameForKeyword(node));
            if (symbol != null && System.Linq.Enumerable.Any(c.GetRootSymbols(symbol), rootSymbol => SymbolMatchesSignature(rootSymbol, calledDeclaration)))
            {
                if (!NodeTests.IsConstructorDeclaration(calledDeclaration!))
                {
                    declarations = [];
                }
                else
                {
                    declarations = declarations.FindAll(node =>
                        node != calledDeclaration && (NodeTests.IsClassDeclaration(node) || NodeTests.IsClassExpression(node)));
                }
            }
            else
            {
                declarations = declarations.FindAll(node => node != calledDeclaration);
            }
            declarations.Add(calledDeclaration!);
        }
        return declarations;
    }

    // Raw-position entry, rebuilt on the full worker (payload unchanged for callers).
    public DefinitionLocation[] GetDefinitionAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var node = GetNodeAtPosition(fileName, position, out var file);
        if (node.Kind == SyntaxKind.SourceFile)
        {
            return [];
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var declarations = CollectDefinitionDeclarations(node, file, checker);
            return CreateDefinitionLocations(declarations.ToArray());
        }
        finally
        {
            done();
        }
    }

    // definition.go:100-135 — ProvideTypeDefinition.
    public Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull? ProvideTypeDefinition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        // definition.go:105-106
        bool clientSupportsLink = ClientCapabilities().TextDocument.TypeDefinition.LinkSupport;

        var file = GetProgramAndFile(fileName);
        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);
        if (node.Kind == SyntaxKind.SourceFile)
        {
            return new Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull();
        }
        var originSelectionRange = LsDeps.ToLspRange(file, GetRangeOfNode(node, file, null));

        var (c, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            node = SourceDefinitionDeps.GetDeclarationNameForKeyword(node);

            if (c.GetSymbolAtLocation(node) is { } symbol)
            {
                var symbolType = GetTypeOfSymbolAtLocation(c, symbol, node);
                var declarations = GetDeclarationsFromType(symbolType);
                // definition.go:123-125 — the `GetFirstTypeArgumentFromKnownType` arm is
                // deferred: the checker surface is not landed yet, so the prepended
                // type-argument declarations drop out.
                if (declarations.Count != 0)
                {
                    return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, declarations, null /*reference*/);
                }
                if ((symbol.Flags & SymbolFlags.Value) == 0 && (symbol.Flags & SymbolFlags.Type) != 0)
                {
                    return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, [.. symbol.Declarations ?? []], null /*reference*/);
                }
            }
        }
        finally
        {
            done();
        }

        return new Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull();
    }

    // definition.go:137-148 — getDeclarationNameForKeyword → SourceDefinitionDeps
    // (GetDeclarationNameForKeyword); re-exported here for the raw worker's call sites.

    // definition.go:155-211 — createDefinitionLocations.
    private Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull CreateDefinitionLocations(
        Ts.LsProto.Range originSelectionRange,
        bool clientSupportsLink,
        List<Node> declarations,
        RefInfo? reference)
    {
        var locations = new List<Ts.LsProto.LocationLink>();
        var locationRanges = new Set<FileRange>();

        if (reference != null)
        {
            var targetRange = new Ts.LsProto.Range
            {
                Start = new Ts.LsProto.Position { Line = 0, Character = 0 },
                End = new Ts.LsProto.Position { Line = 0, Character = 0 },
            };
            locations.Add(new Ts.LsProto.LocationLink
            {
                OriginSelectionRange = originSelectionRange,
                TargetUri = LsDeps.FileNameToDocumentURI(reference.FileName),
                TargetRange = targetRange,
                TargetSelectionRange = targetRange,
            });
        }

        foreach (var decl in declarations)
        {
            var file = AstUtilities.GetSourceFileOfNode(decl)!;
            var fileName = file.FileName();
            var name = AstUtilities.GetNameOfDeclaration(decl) ?? decl;
            global::Ts.Core.TextRange nameRange;
            if (name.Kind == SyntaxKind.EmptyStatement)
            {
                nameRange = new global::Ts.Core.TextRange(name.Pos, name.Pos);
            }
            else
            {
                nameRange = CreateRangeFromNode(name, file);
            }
            if (locationRanges.AddIfAbsent(new FileRange(fileName, nameRange)))
            {
                var contextNode = SourceDefinitionDeps.GetContextNode(decl) ?? decl;
                var contextRange = SourceDefinitionDeps.ToContextRange(nameRange, file, contextNode) ?? nameRange;
                var targetSelectionLoc = GetMappedLocation(fileName, nameRange);
                var targetLoc = GetMappedLocation(fileName, contextRange);
                locations.Add(new Ts.LsProto.LocationLink
                {
                    OriginSelectionRange = originSelectionRange,
                    TargetSelectionRange = targetSelectionLoc.Range,
                    TargetUri = targetLoc.Uri,
                    TargetRange = targetLoc.Range,
                });
            }
        }

        if (clientSupportsLink)
        {
            return new Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { DefinitionLinks = locations.ToArray() };
        }
        return new Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { Locations = SourceDefinitionDeps.CreateLocationsFromLinks(locations.ToArray()) };
    }

    // definition.go:223-228 — createLocationFromFileAndRange.
    private Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull CreateLocationFromFileAndRange(SourceFile file, global::Ts.Core.TextRange textRange)
    {
        var mappedLocation = GetMappedLocation(file.FileName(), textRange);
        return new Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { Location = mappedLocation };
    }

    private static DefinitionLocation[] CreateDefinitionLocations(global::Ts.Ast.Node[] declarations)
    {
        var locations = new List<DefinitionLocation>(declarations.Length);
        var seen = new HashSet<FileRange>();

        foreach (var declaration in declarations)
        {
            // Go unconditionally dereferences the declaration's source file.
            var file = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(declaration)!;
            var name = global::Ts.Ast.AstUtilities.GetNameOfDeclaration(declaration) ?? declaration;
            global::Ts.Core.TextRange textSpan;
            if (name.Kind == global::Ts.Ast.SyntaxKind.EmptyStatement)
            {
                textSpan = new global::Ts.Core.TextRange(name.Pos, name.Pos);
            }
            else
            {
                textSpan = CreateRangeFromNode(name, file);
            }

            var fileRange = new FileRange(file.FileName(), textSpan);
            if (seen.Add(fileRange))
            {
                locations.Add(new DefinitionLocation(fileRange.FileName, fileRange.TextSpan));
            }
        }

        return locations.ToArray();
    }

    // utilities.go:createRangeFromNode
    private static global::Ts.Core.TextRange CreateRangeFromNode(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile file)
    {
        return new global::Ts.Core.TextRange(
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, file, false /*includeJSDoc*/),
            node.End);
    }

    private readonly record struct FileRange(string FileName, global::Ts.Core.TextRange TextSpan);

    // definition.go:362-372 — isJsxConstructorLike.
    private static bool IsJsxConstructorLike(Node node)
    {
        return NodeTests.IsConstructorDeclaration(node) ||
            NodeTests.IsConstructorTypeNode(node) ||
            NodeTests.IsCallSignatureDeclaration(node) ||
            NodeTests.IsConstructSignatureDeclaration(node);
    }

    // definition.go:374-385 — symbolMatchesSignature.
    private static bool SymbolMatchesSignature(global::Ts.Ast.Symbol? symbol, Node? calledDeclaration)
    {
        if (symbol == null || calledDeclaration == null)
        {
            return false;
        }
        var calledSymbol = calledDeclaration.Symbol();
        if (ReferenceEquals(symbol, calledSymbol) || calledSymbol != null && ReferenceEquals(symbol, calledSymbol.Parent))
        {
            return true;
        }
        var parent = calledDeclaration.Parent;
        return parent != null && (AstUtilities.IsAssignmentExpression(parent, false /*excludeCompoundAssignment*/) ||
            !AstUtilities.IsCallLikeExpression(parent) && AstUtilities.CanHaveSymbol(parent) && ReferenceEquals(symbol, parent.Symbol()));
    }

    // definition.go:387-415 — getSymbolForOverriddenMember.
    private static global::Ts.Ast.Symbol? GetSymbolForOverriddenMember(global::Ts.Checker.Checker typeChecker, Node node)
    {
        var classElement = AstUtilities.FindAncestor(node, AstUtilities.IsClassElement);
        if (classElement == null || classElement.Name() == null)
        {
            return null;
        }
        var baseDeclaration = AstUtilities.FindAncestor(classElement, AstUtilities.IsClassLike);
        if (baseDeclaration == null)
        {
            return null;
        }
        var baseTypeNode = AstUtilities.GetClassExtendsHeritageElement(baseDeclaration);
        if (baseTypeNode == null)
        {
            return null;
        }
        var expression = AstUtilities.SkipParentheses(baseTypeNode.Expression()!);
        global::Ts.Ast.Symbol? @base;
        if (NodeTests.IsClassExpression(expression))
        {
            @base = expression.Symbol();
        }
        else
        {
            @base = typeChecker.GetSymbolAtLocation(expression);
        }
        if (@base == null)
        {
            return null;
        }
        var name = AstUtilities.GetTextOfPropertyName(classElement.Name()!);
        if (AstUtilities.HasStaticModifier(classElement))
        {
            return typeChecker.GetPropertyOfType(typeChecker.GetTypeOfSymbol(@base), name);
        }
        return typeChecker.GetPropertyOfType(typeChecker.GetDeclaredTypeOfSymbol(@base), name);
    }

    // definition.go:417-428 — getTypeOfSymbolAtLocation.
    private static global::Ts.Checker.Type GetTypeOfSymbolAtLocation(global::Ts.Checker.Checker c, global::Ts.Ast.Symbol symbol, Node node)
    {
        var t = c.GetTypeOfSymbolAtLocation(symbol, node);
        // If the type is just a function's inferred type, go-to-type should go to the return type instead since
        // go-to-definition takes you to the function anyway.
        if (ReferenceEquals(t.Symbol, symbol) || t.Symbol != null && symbol.ValueDeclaration != null && NodeTests.IsVariableDeclaration(symbol.ValueDeclaration) && symbol.ValueDeclaration.Initializer() == t.Symbol!.ValueDeclaration)
        {
            var sigs = c.GetCallSignatures(t);
            if (sigs.Length == 1)
            {
                return c.GetReturnTypeOfSignature(sigs[0]);
            }
        }
        return t;
    }

    // definition.go:430-440 — getDeclarationsFromType.
    private static List<Node> GetDeclarationsFromType(global::Ts.Checker.Type t)
    {
        var result = new List<Node>();
        foreach (var distributed in t.Distributed())
        {
            if (distributed.Symbol != null)
            {
                foreach (var decl in distributed.Symbol!.Declarations ?? [])
                {
                    if (!result.Contains(decl))
                    {
                        result.Add(decl);
                    }
                }
            }
        }
        return result;
    }
}
