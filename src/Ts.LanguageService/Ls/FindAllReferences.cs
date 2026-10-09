// Ported from tsgo internal/ls/findallreferences.go — the LanguageService members of the
// deep find-all-references engine (free-function helpers live in FindAllReferences.Support.cs,
// getReferencedSymbolsSpecial + the string-literal families in FindAllReferences.Special.cs,
// and importTracker.go in ImportTracker.cs).
//
// Pipeline: ProvideSymbolsAndEntries (:628) → GetSymbolAndEntries (:673) →
// GetReferencedSymbolsForNode (:1161) → getReferencedSymbolsForModule(IfDeclaredBySourceFile)
// (:1627/:1293) / getReferencedSymbolsForSymbol (:1748) → refState (:1803) search state with
// checker.GetSymbolAtLocation comparison, alias/export-specifier chains and the import tracker.
//
// The raw-position entry `GetReferencesAtPosition` (C# LS currency: UTF-16 offsets) is rebuilt
// on the deep engine; its previous signature and ReferenceLocation payload are unchanged for
// the LS/Rename.cs and LSP callers. The foundation-subset helpers Rename.cs still consumes
// (GetReferencedDeclaration / GetPossibleSymbolReferenceNodes / GetRangeOfNode /
// IsValidReferencePosition) are kept verbatim.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerFindRefsLsExports / CheckerLsExports extension methods
using Ts.Collections; // Set<T>
using Ts.Compiler; // ProgramLsExports extension methods

namespace Ts.Ls;

// findallreferences.go: ReferenceEntry, reduced to the location payload the raw
// (non-LSP) foundation API returns; the LSP converters live in the deferred layer.
public sealed record ReferenceLocation(string FileName, global::Ts.Core.TextRange TextSpan);

// findallreferences.go:474-482 — position (the HasTextDocumentPosition carrier used by
// the non-local-definition family). Named LsTextDocumentPosition here to avoid clashing
// with Ts.LsProto.Position.
internal sealed class LsTextDocumentPosition
{
    public LsTextDocumentPosition(global::Ts.LsProto.DocumentUri uri, global::Ts.LsProto.Position pos)
    {
        Uri = uri;
        Pos = pos;
    }

    public global::Ts.LsProto.DocumentUri Uri { get; }
    public global::Ts.LsProto.Position Pos { get; }

    public global::Ts.LsProto.DocumentUri TextDocumentURI() => Uri;
    public global::Ts.LsProto.Position TextDocumentPosition() => Pos;
}

// findallreferences.go:484-488 — nonLocalDefinition.
internal sealed class NonLocalDefinition
{
    public NonLocalDefinition(LsTextDocumentPosition position)
    {
        Position = position;
    }

    public LsTextDocumentPosition Position { get; }

    // Go embeds `position`; its HasTextDocumentPosition methods are promoted.
    public global::Ts.LsProto.DocumentUri TextDocumentURI() => Position.TextDocumentURI();
    public global::Ts.LsProto.Position TextDocumentPosition() => Position.TextDocumentPosition();

    // Go: sync.OnceValue closures; Lazy<T> is the C# mapping.
    private Lazy<LsTextDocumentPosition?>? getSourcePosition;
    private Lazy<LsTextDocumentPosition?>? getGeneratedPosition;

    public LsTextDocumentPosition? GetSourcePosition() => getSourcePosition?.Value;
    public LsTextDocumentPosition? GetGeneratedPosition() => getGeneratedPosition?.Value;

    public NonLocalDefinition WithLazySourcePosition(Func<LsTextDocumentPosition?> factory)
    {
        getSourcePosition = new Lazy<LsTextDocumentPosition?>(factory);
        return this;
    }

    public NonLocalDefinition WithLazyGeneratedPosition(Func<LsTextDocumentPosition?> factory)
    {
        getGeneratedPosition = new Lazy<LsTextDocumentPosition?>(factory);
        return this;
    }
}

// findallreferences.go:615-620 — symbolEntryTransformOptions.
internal sealed class SymbolEntryTransformOptions
{
    // Force the result to be Location objects.
    public bool RequireLocationsResult;
    // Omit node(s) containing the original position.
    public bool DropOriginNodes;
}

// findallreferences.go:622-626 — SymbolAndEntriesData.
public sealed class SymbolAndEntriesData
{
    public static readonly SymbolAndEntriesData Empty = new(null, [], 0);

    public SymbolAndEntriesData(Node? originalNode, List<SymbolAndEntries> symbolsAndEntries, int position)
    {
        OriginalNode = originalNode;
        SymbolsAndEntries = symbolsAndEntries;
        Position = position;
    }

    public Node? OriginalNode { get; }
    public List<SymbolAndEntries> SymbolsAndEntries { get; }
    public int Position { get; }
}

public sealed partial class LanguageService
{
    // === foundation-subset members kept for Rename.cs (Go :329/:348/:1529/:1538) ===

    // findallreferences.go:329 getRangeOfNode
    private static global::Ts.Core.TextRange GetRangeOfNode(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile? sourceFile,
        global::Ts.Ast.Node? endNode)
    {
        sourceFile ??= global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node)!;
        var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false /*includeJsDoc*/);
        var end = (endNode ?? node).End;
        if (global::Ts.Ast.AstUtilities.IsStringLiteralLike(node) && (end - start) > 2)
        {
            if (endNode is not null)
            {
                // Go panic; InvalidOperationException is the repository's panic mapping.
                throw new InvalidOperationException("endNode is not nil for stringLiteralLike");
            }
            start += 1;
            end -= 1;
        }
        if (endNode is not null && endNode.Kind == global::Ts.Ast.SyntaxKind.CaseBlock)
        {
            end = endNode.Pos;
        }
        return new global::Ts.Core.TextRange(start, end);
    }

    // Symbol-identity channel for the raw-position API subset. Faithful Go compares
    // *ast.Symbol identities from checker.GetSymbolAtLocation after alias skipping;
    // Rename.cs (out of this session's engine rewrite scope) still rides the public emit
    // resolver: two identifier use-sites reference the same symbol exactly when they
    // resolve to the same value declaration. GetReferencedValueDeclaration already folds
    // export-value symbols onto their export symbol
    // (GetExportSymbolOfValueSymbolIfExported).
    private static global::Ts.Ast.Node? GetReferencedDeclaration(
        global::Ts.Checker.EmitResolver emitResolver,
        global::Ts.Ast.Node node)
    {
        if (IsMemberNamePosition(node))
        {
            // Member names are not lexically resolvable — resolveName would bind them
            // to an unrelated outer symbol of the same text. The member channel only
            // answers from already-resolved checker links, so unresolved member
            // references drop out (deferred with checker.GetSymbolAtLocation).
            return emitResolver.GetReferencedMemberValueDeclaration(node);
        }
        return emitResolver.GetReferencedValueDeclaration(node);
    }

    // Positions in which an identifier names a member rather than a lexical value.
    private static bool IsMemberNamePosition(global::Ts.Ast.Node node)
    {
        var parent = node.Parent;
        if (parent is null)
        {
            return false;
        }
        switch (parent.Kind)
        {
            case global::Ts.Ast.SyntaxKind.PropertyAccessExpression:
                return parent.Name() == node;
            case global::Ts.Ast.SyntaxKind.QualifiedName:
                return parent.AsQualifiedName().Right == node;
            case global::Ts.Ast.SyntaxKind.PropertyAssignment:
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
            case global::Ts.Ast.SyntaxKind.MethodSignature:
            case global::Ts.Ast.SyntaxKind.GetAccessor:
            case global::Ts.Ast.SyntaxKind.SetAccessor:
            case global::Ts.Ast.SyntaxKind.EnumMember:
                return parent.Name() == node;
            default:
                return false;
        }
    }

    // findallreferences.go:1529 getPossibleSymbolReferenceNodes
    private static List<global::Ts.Ast.Node> GetPossibleSymbolReferenceNodes(
        global::Ts.Ast.SourceFile sourceFile,
        string symbolName,
        global::Ts.Ast.Node? container)
    {
        var nodes = new List<global::Ts.Ast.Node>();
        foreach (var pos in GetPossibleSymbolReferencePositions(sourceFile, symbolName, container))
        {
            var referenceLocation = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, pos);
            if (referenceLocation.Kind != global::Ts.Ast.SyntaxKind.SourceFile)
            {
                nodes.Add(referenceLocation);
            }
        }
        return nodes;
    }

    // findallreferences.go:1538 getPossibleSymbolReferencePositions
    private static List<int> GetPossibleSymbolReferencePositions(
        global::Ts.Ast.SourceFile sourceFile,
        string symbolName,
        global::Ts.Ast.Node? container)
    {
        var positions = new List<int>();

        // TODO: Cache symbol existence for files to save text search
        // Also, need to make this work for unicode escapes.

        // Be resilient in the face of a symbol with no name or zero length name
        if (symbolName.Length == 0)
        {
            return positions;
        }

        var text = sourceFile.Text();
        var sourceLength = text.Length;
        var symbolNameLength = symbolName.Length;

        // Go: container == nil → sourceFile.AsNode(). All offsets here are UTF-16 code
        // units (the repository position currency). The absolute IndexOf overload keeps
        // the loop in one currency.
        var containerPos = container?.Pos ?? 0;
        var endPos = container?.End ?? sourceFile.End;

        var position = text.IndexOf(symbolName, containerPos, StringComparison.Ordinal);
        while (position >= 0 && position < endPos)
        {
            // We found a match.  Make sure it's not part of a larger word (i.e. the char
            // before and after it have to be a non-identifier char).
            var endPosition = position + symbolNameLength;

            if ((position == 0 || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[position - 1])) &&
                (endPosition == sourceLength || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[endPosition])))
            {
                // Found a real match.  Keep searching.
                positions.Add(position);
            }
            var startIndex = position + symbolNameLength + 1;
            if (startIndex > text.Length)
            {
                break;
            }
            position = text.IndexOf(symbolName, startIndex, StringComparison.Ordinal);
        }

        return positions;
    }

    // findallreferences.go:348 isValidReferencePosition
    internal static bool IsValidReferencePosition(global::Ts.Ast.Node node, string searchSymbolName)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.PrivateIdentifier:
                // !!!
                // if (isJSDocMemberName(node.Parent)) {
                // 	return true;
                // }
                return node.Text().Length == searchSymbolName.Length;
            case global::Ts.Ast.SyntaxKind.Identifier:
                return node.Text().Length == searchSymbolName.Length;
            case global::Ts.Ast.SyntaxKind.NoSubstitutionTemplateLiteral:
            case global::Ts.Ast.SyntaxKind.StringLiteral:
                return node.Text().Length == searchSymbolName.Length && (LsDeps.IsLiteralNameOfPropertyDeclarationOrIndexAccess(node) ||
                    FindRefsSupport.IsNameOfModuleDeclaration(node) ||
                    FindRefsSupport.IsExpressionOfExternalModuleImportEqualsDeclaration(node) ||
                    NodeTests.IsCallExpression(node.Parent!) && AstUtilities.IsBindableObjectDefinePropertyCall(node.Parent!) && node.Parent!.Arguments()![1] == node ||
                    AstUtilities.IsImportOrExportSpecifier(node.Parent!));
            case global::Ts.Ast.SyntaxKind.NumericLiteral:
                return LsDeps.IsLiteralNameOfPropertyDeclarationOrIndexAccess(node) && node.Text().Length == searchSymbolName.Length;
            case global::Ts.Ast.SyntaxKind.DefaultKeyword:
                return "default".Length == searchSymbolName.Length;
        }
        return false;
    }

    // === the raw-position entry point, rebuilt on the deep engine (:628/:673/:1161) ===

    // findallreferences.go — ProvideReferences for the raw-position C# LS currency:
    // includes declarations, matching the Go default of includeDeclaration=true requests.
    public ReferenceLocation[] GetReferencesAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var (data, ok) = ProvideSymbolsAndEntries(cancellationToken, fileName, position, false /*isRename*/, false /*implementations*/);
        if (!ok)
        {
            return [];
        }
        var locations = new List<ReferenceLocation>();
        foreach (var s in data.SymbolsAndEntries)
        {
            foreach (var reference in s.References)
            {
                ResolveEntry(reference);
                var textRange = reference.TextRange;
                if (textRange != null)
                {
                    locations.Add(new ReferenceLocation(reference.FileName, textRange.Value));
                }
                else if (reference.Node != null)
                {
                    locations.Add(new ReferenceLocation(
                        AstUtilities.GetSourceFileOfNode(reference.Node)!.FileName(),
                        GetRangeOfNode(reference.Node, null, null)));
                }
            }
        }
        return locations.ToArray();
    }

    // findallreferences.go:628-671 — provideSymbolsAndEntries.
    internal (SymbolAndEntriesData Data, bool Ok) ProvideSymbolsAndEntries(
        CancellationToken ctx,
        string fileName,
        int position,
        bool isRename,
        bool implementations)
    {
        // `findReferencedSymbols` except only computes the information needed to return reference locations
        var sourceFile = GetProgramAndFile(fileName);

        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, position);
        if (isRename)
        {
            // Adjust modifier/keyword nodes to the declaration name, matching Strada's findRenameLocations.
            node = LsDeps.GetAdjustedLocation(node, true /*forRename*/, sourceFile);
        }
        if (isRename && !NodeIsEligibleForRename(node) || implementations && NodeTests.IsSourceFile(node))
        {
            return (new SymbolAndEntriesData(node, [], position), false);
        }

        var entries = GetSymbolAndEntries(ctx, position, node, program, isRename, implementations);
        if (!implementations)
        {
            return (new SymbolAndEntriesData(node, entries, position), true);
        }

        var implementationEntries = new List<SymbolAndEntries>();
        var queue = new Queue<ReferenceEntry>();
        var seenNodes = new Set<Node>();

        void AddToQueue(List<SymbolAndEntries> symbolAndEntries)
        {
            implementationEntries.AddRange(symbolAndEntries);
            foreach (var s in symbolAndEntries)
            {
                foreach (var r in s.References)
                {
                    queue.Enqueue(r);
                }
            }
        }

        AddToQueue(entries);
        while (queue.Count != 0)
        {
            if (ctx.IsCancellationRequested)
            {
                return (SymbolAndEntriesData.Empty, false);
            }

            var entry = queue.Dequeue();
            if (entry.Node != null && !seenNodes.Has(entry.Node))
            {
                seenNodes.Add(entry.Node);
                AddToQueue(GetSymbolAndEntries(ctx, entry.Node.Pos, entry.Node, program, isRename, implementations));
            }
        }
        return (new SymbolAndEntriesData(node, implementationEntries, position), true);
    }

    // rename.go:145 — nodeIsEligibleForRename is owned by Rename.cs (the Go canonical
    // home); this partial reaches it through the shared LanguageService type.

    // findallreferences.go:673-692 — getSymbolAndEntries.
    private List<SymbolAndEntries> GetSymbolAndEntries(
        CancellationToken ctx,
        int position,
        Node node,
        global::Ts.Compiler.Program program,
        bool isRename,
        bool implementations)
    {
        var options = new RefOptions();
        if (!isRename)
        {
            options.Use = ReferenceUse.References;
            if (implementations)
            {
                options.Implementations = true;
            }
        }
        else
        {
            options.Use = ReferenceUse.Rename;
            // rename.go:689 — l.UserPreferences().UseAliasesForRename.IsTrueOrUnknown();
            // the preferences member is not on the carrier yet, so the Go default (unknown
            // → true) holds.
            options.UseAliasesForRename = true;
        }
        return GetReferencedSymbolsForNodeWorker(ctx, position, node, program, program.SourceFiles(), options);
    }

    // findallreferences.go:167-191 — getRangeOfEntry / getFileNameOfEntry / getLocationOfEntry
    // / resolveEntry.
    internal global::Ts.LsProto.Range GetRangeOfEntry(ReferenceEntry entry) => ResolveEntry(entry).LspRange!.Range;

    internal global::Ts.LsProto.DocumentUri GetFileNameOfEntry(ReferenceEntry entry) => ResolveEntry(entry).LspRange!.Uri;

    internal global::Ts.LsProto.Location GetLocationOfEntry(ReferenceEntry entry) => ResolveEntry(entry).LspRange!;

    internal ReferenceEntry ResolveEntry(ReferenceEntry entry)
    {
        if (entry.TextRange == null)
        {
            var sourceFile = AstUtilities.GetSourceFileOfNode(entry.Node!)!;
            var textRange = GetRangeOfNode(entry.Node!, sourceFile, null /*endNode*/);
            entry.TextRange = textRange;
            entry.FileName = sourceFile.FileName();
        }
        if (entry.LspRange == null)
        {
            var location = GetMappedLocation(entry.FileName, entry.TextRange.Value);
            entry.LspRange = location;
        }
        return entry;
    }

    // findallreferences.go:498-540 — getNonLocalDefinition.
    internal NonLocalDefinition? GetNonLocalDefinition(CancellationToken ctx, SymbolAndEntries entry)
    {
        if (!entry.CanUseDefinitionSymbol())
        {
            return null;
        }

        var program = GetProgram();
        for (var i = 0; i < (entry.Definition!.Symbol?.Declarations?.Length ?? 0); i++)
        {
            var d = entry.Definition.Symbol!.Declarations![i];
            var checkerAndDone = host.GetTypeCheckerForFile(ctx, AstUtilities.GetSourceFileOfNode(d)!);
            bool visible;
            try
            {
                visible = FindRefsSupport.IsDefinitionVisible(checkerAndDone.Checker.GetEmitResolver(), d);
            }
            finally
            {
                checkerAndDone.Release();
            }
            if (!visible)
            {
                continue;
            }
            var (file, startPos) = SourceDefinitionDeps.GetFileAndStartPosFromDeclaration(d);
            var fileName = file.FileName();
            var nonLocal = new NonLocalDefinition(new LsTextDocumentPosition(
                LsDeps.FileNameToDocumentURI(fileName),
                LsDeps.PositionToLineAndCharacter(file, startPos)));
            return nonLocal
                .WithLazySourcePosition(() =>
                {
                    var mapped = TryGetSourcePosition(fileName, startPos);
                    if (mapped != null)
                    {
                        return new LsTextDocumentPosition(
                            LsDeps.FileNameToDocumentURI(mapped.FileName),
                            GetScript(mapped.FileName)!.PositionToLineAndCharacter(mapped.Pos));
                    }
                    return null;
                })
                .WithLazyGeneratedPosition(() =>
                {
                    var mapped = TryGetGeneratedPosition(fileName, startPos);
                    if (mapped != null)
                    {
                        return new LsTextDocumentPosition(
                            LsDeps.FileNameToDocumentURI(mapped.FileName),
                            GetScript(mapped.FileName)!.PositionToLineAndCharacter(mapped.Pos));
                    }
                    return null;
                });
        }
        return null;
    }

    // findallreferences.go:584-613 — forEachOriginalDefinitionLocation.
    internal void ForEachOriginalDefinitionLocation(
        CancellationToken ctx,
        SymbolAndEntries entry,
        Action<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.Position> cb)
    {
        if (!entry.CanUseDefinitionSymbol())
        {
            return;
        }

        var program = GetProgram();
        foreach (var d in entry.Definition!.Symbol!.Declarations ?? [])
        {
            var (file, startPos) = SourceDefinitionDeps.GetFileAndStartPosFromDeclaration(d);
            var fileName = file.FileName();
            if (global::Ts.TsPath.TsPath.IsDeclarationFileName(fileName))
            {
                // Map to ts position
                var mapped = TryGetSourcePosition(file.FileName(), startPos);
                if (mapped != null)
                {
                    cb(
                        LsDeps.FileNameToDocumentURI(mapped.FileName),
                        GetScript(mapped.FileName)!.PositionToLineAndCharacter(mapped.Pos));
                }
            }
            else if (program.IsSourceFromProjectReferenceForLs(ToPath(fileName)))
            {
                cb(
                    LsDeps.FileNameToDocumentURI(fileName),
                    LsDeps.PositionToLineAndCharacter(file, startPos));
            }
        }
    }

    // findallreferences.go:1092-1098 — GetReferencedSymbolsForNode (public).
    // Returns all referenced symbols and their reference entries for the given node.
    // It returns all referenced symbols and their reference entries for the given node across the provided source files.
    public List<SymbolAndEntries> GetReferencedSymbolsForNode(
        CancellationToken ctx,
        int position,
        Node node,
        SourceFile[] sourceFiles)
    {
        return GetReferencedSymbolsForNodeWorker(ctx, position, node, program, sourceFiles, new RefOptions
        {
            Use = ReferenceUse.References,
        });
    }

    // findallreferences.go:1100-1157 — GetSignatureUsages: returns all usages of a
    // signature declaration as name-call pairs. For each reference to the signature's
    // name, it returns the reference node and the call expression it appears in (null if
    // the reference is not in a call position).
    public List<SignatureUsage> GetSignatureUsages(CancellationToken ctx, Node signatureDecl)
    {
        var name = signatureDecl.Name();
        if (name == null || !NodeTests.IsIdentifier(name))
        {
            return [];
        }

        var sourceFiles = program.SourceFiles();
        var entries = GetReferencedSymbolsForNode(ctx, name.Pos, name, sourceFiles);

        // Collect all declaration name nodes for the target symbol so we can
        // filter them out — the caller wants usages, not declarations.
        var declNames = new HashSet<Node>();
        foreach (var entry in entries)
        {
            if (entry.Definition?.Symbol != null)
            {
                foreach (var decl in entry.Definition.Symbol.Declarations ?? [])
                {
                    var n = decl.Name();
                    if (n != null)
                    {
                        declNames.Add(n);
                    }
                }
            }
        }

        var result = new List<SignatureUsage>();
        foreach (var entry in entries)
        {
            foreach (var @ref in entry.ReferencesList())
            {
                if (!@ref.IsNodeEntry())
                {
                    continue;
                }
                var node = @ref.NodeValue();
                if (node == null || declNames.Contains(node))
                {
                    continue;
                }

                var called = CallHierarchyAstHelpers.ClimbPastPropertyAccess(node);

                Node? callExpr = null;
                if (called.Parent != null && NodeTests.IsCallExpression(called.Parent) && called.Parent.Expression() == called)
                {
                    callExpr = called.Parent;
                }

                result.Add(new SignatureUsage(node, callExpr));
            }
        }
        return result;
    }

    // === functions for find all ref implementation ===

    // findallreferences.go:1161-1244 — getReferencedSymbolsForNode.
    internal List<SymbolAndEntries> GetReferencedSymbolsForNodeWorker(
        CancellationToken ctx,
        int position,
        Node node,
        global::Ts.Compiler.Program program,
        SourceFile[] sourceFiles,
        RefOptions options)
    {
        // !!! cancellationToken
        var sourceFilesSet = new Set<string>();
        foreach (var file in sourceFiles)
        {
            sourceFilesSet.Add(file.FileName());
        }

        if (options.Use == ReferenceUse.References || options.Use == ReferenceUse.Rename)
        {
            node = LsDeps.GetAdjustedLocation(node, options.Use == ReferenceUse.Rename, AstUtilities.GetSourceFileOfNode(node)!);
        }

        var (checker, done) = program.GetTypeCheckerForFile(ctx, AstUtilities.GetSourceFileOfNode(node)!);
        try
        {
            if (node.Kind == SyntaxKind.SourceFile)
            {
                var resolvedRef = SourceDefinitionDeps.GetReferenceAtPosition(node.AsSourceFile(), position, program);
                if (resolvedRef == null || resolvedRef.File == null)
                {
                    return [];
                }

                if (checker.GetMergedSymbol(resolvedRef.File.Symbol()) is { } moduleSymbol)
                {
                    return GetReferencedSymbolsForModule(ctx, program, moduleSymbol, false /*excludeImportTypeOfExportEquals*/, sourceFiles, sourceFilesSet);
                }

                // !!! not implemented
                // fileIncludeReasons := program.getFileIncludeReasons();
                // if (!fileIncludeReasons) {
                // 	return nil
                // }
                return
                [
                    new SymbolAndEntries(
                        new FarDefinition { Kind = DefinitionKind.TripleSlashReference, TripleSlashFileRef = new TripleSlashDefinition(resolvedRef.Reference, resolvedRef.File) },
                        GetReferencesForNonModule(resolvedRef.File, program /*fileIncludeReasons,*/)),
                ];
            }

            if (!options.Implementations)
            {
                // !!! cancellationToken
                var special = FindRefsSpecial.GetReferencedSymbolsSpecial(node, sourceFiles);
                if (special != null)
                {
                    return special;
                }
            }

            // constructors should use the class symbol, detected by name, if present
            var symbol = checker.GetSymbolAtLocation(node.Kind == SyntaxKind.Constructor && node.Parent!.Name() != null ? node.Parent!.Name()! : node);
            // Could not find a symbol e.g. unknown identifier
            if (symbol == null)
            {
                // String literal might be a property (and thus have a symbol), so do this here rather than in getReferencedSymbolsSpecial.
                if (!options.Implementations && AstUtilities.IsStringLiteralLike(node))
                {
                    if (SourceDefinitionDeps.IsModuleSpecifierLike(node))
                    {
                        // !!! not implemented
                        // fileIncludeReasons := program.GetFileIncludeReasons()
                        // if referencedFile := program.GetResolvedModuleFromModuleSpecifier(node, nil /*sourceFile*/); referencedFile != nil {
                        // return []*SymbolAndEntries{{
                        // 	definition: &Definition{Kind: definitionKindString, node: node},
                        // 	references: getReferencesForNonModule(referencedFile, program /*fileIncludeReasons,*/),
                        // }}
                        // }
                        // Fall through to string literal references. This is not very likely to return
                        // anything useful, but I guess it's better than nothing, and there's an existing
                        // test that expects this to happen (fourslash/cases/untypedModuleImport.ts).
                    }
                    return FindRefsSpecial.GetReferencesForStringLiteral(ctx, node, sourceFiles, checker);
                }
                return [];
            }

            if (symbol.Name == InternalSymbolName.ExportEquals)
            {
                if (symbol.Parent == null)
                {
                    return [];
                }
                return GetReferencedSymbolsForModule(ctx, program, symbol.Parent, false /*excludeImportTypeOfExportEquals*/, sourceFiles, sourceFilesSet);
            }

            var moduleReferences = GetReferencedSymbolsForModuleIfDeclaredBySourceFile(ctx, symbol, program, sourceFiles, checker, options, sourceFilesSet);
            if (moduleReferences != null && (symbol.Flags & SymbolFlags.Transient) == 0)
            {
                return moduleReferences;
            }

            var aliasedSymbol = GetMergedAliasedSymbolOfNamespaceExportDeclaration(node, symbol, checker);
            var moduleReferencesOfExportTarget = GetReferencedSymbolsForModuleIfDeclaredBySourceFile(ctx, aliasedSymbol, program, sourceFiles, checker, options, sourceFilesSet);

            var references = FindRefsEngine.GetReferencedSymbolsForSymbol(ctx, program, symbol, node, sourceFiles, sourceFilesSet, checker, options);
            return MergeReferences(program, moduleReferences, references, moduleReferencesOfExportTarget);
        }
        finally
        {
            done();
        }
    }

    // findallreferences.go:1293-1311 — getReferencedSymbolsForModuleIfDeclaredBySourceFile.
    internal List<SymbolAndEntries>? GetReferencedSymbolsForModuleIfDeclaredBySourceFile(
        CancellationToken ctx,
        global::Ts.Ast.Symbol? symbol,
        global::Ts.Compiler.Program program,
        SourceFile[] sourceFiles,
        global::Ts.Checker.Checker checker,
        RefOptions options,
        Set<string> sourceFilesSet)
    {
        var moduleSourceFileName = "";
        if (symbol == null || !(((symbol.Flags & SymbolFlags.Module) != 0) && symbol.Declarations!.Length != 0))
        {
            return null;
        }
        Node? moduleSourceFile = null;
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (NodeTests.IsSourceFile(decl))
            {
                moduleSourceFile = decl;
                break;
            }
        }
        if (moduleSourceFile != null)
        {
            moduleSourceFileName = moduleSourceFile.AsSourceFile().FileName();
        }
        else
        {
            return null;
        }
        var exportEquals = symbol.Exports?[InternalSymbolName.ExportEquals];
        // If exportEquals != nil, we're about to add references to `import("mod")` anyway, so don't double-count them.
        var moduleReferences = GetReferencedSymbolsForModule(ctx, program, symbol, exportEquals != null, sourceFiles, sourceFilesSet);
        if (exportEquals == null || (exportEquals.Flags & SymbolFlags.Alias) == 0 || !sourceFilesSet.Has(moduleSourceFileName))
        {
            return moduleReferences;
        }
        symbol = checker.ResolveAlias(exportEquals).Item1;
        return MergeReferences(program, moduleReferences, FindRefsEngine.GetReferencedSymbolsForSymbol(ctx, program, symbol! /*node*/, null, sourceFiles, sourceFilesSet, checker /*, cancellationToken*/, options));
    }

    // findallreferences.go:1610-1613 — getReferencesForNonModule.
    internal static List<ReferenceEntry> GetReferencesForNonModule(SourceFile referencedFile, global::Ts.Compiler.Program program)
    {
        // !!! not implemented
        return [];
    }

    // findallreferences.go:1615-1625 — getMergedAliasedSymbolOfNamespaceExportDeclaration.
    internal static global::Ts.Ast.Symbol? GetMergedAliasedSymbolOfNamespaceExportDeclaration(Node node, global::Ts.Ast.Symbol symbol, global::Ts.Checker.Checker checker)
    {
        if (node.Parent != null && node.Parent.Kind == SyntaxKind.NamespaceExportDeclaration)
        {
            var (aliasedSymbol, ok) = checker.ResolveAlias(symbol);
            if (ok)
            {
                var targetSymbol = checker.GetMergedSymbol(aliasedSymbol);
                if (!ReferenceEquals(aliasedSymbol, targetSymbol))
                {
                    return targetSymbol;
                }
            }
        }
        return null;
    }

    // findallreferences.go:1627-1727 — getReferencedSymbolsForModule.
    internal List<SymbolAndEntries> GetReferencedSymbolsForModule(
        CancellationToken ctx,
        global::Ts.Compiler.Program program,
        global::Ts.Ast.Symbol symbol,
        bool excludeImportTypeOfExportEquals,
        SourceFile[] sourceFiles,
        Set<string> sourceFilesSet)
    {
        global::Ts.Core.Debug.Assert(symbol.ValueDeclaration != null);

        var (checker, done) = program.GetTypeCheckerForFile(ctx, AstUtilities.GetSourceFileOfNode(symbol.ValueDeclaration!)!);
        try
        {
            var moduleRefs = ImportTrackerSupport.FindModuleReferences(program, sourceFiles, symbol, checker);
            var references = new List<ReferenceEntry>();
            foreach (var reference in moduleRefs)
            {
                switch (reference.Kind)
                {
                    case ModuleReferenceKind.Import:
                        var parent = reference.Literal!.Parent!;
                        if (NodeTests.IsLiteralTypeNode(parent))
                        {
                            var importType = parent.Parent!;
                            if (NodeTests.IsImportTypeNode(importType))
                            {
                                var importTypeNode = importType.AsImportTypeNode();
                                if (excludeImportTypeOfExportEquals && importTypeNode.Qualifier == null)
                                {
                                    continue;
                                }
                            }
                        }
                        // import("foo") with no qualifier will reference the `export =` of the module, which may be referenced anyway.
                        references.Add(FindRefsSupport.NewNodeEntry(reference.Literal));
                        break;
                    case ModuleReferenceKind.Implicit:
                        {
                            // For implicit references (e.g., JSX runtime imports), return the first JSX node,
                            // the first statement, or the whole file
                            Node? rangeNode = null;

                            // Skip the JSX search for tslib imports
                            if (reference.Literal!.Text() != "tslib")
                            {
                                rangeNode = FindRefsSupport.FindFirstJsxNode(reference.ReferencingFile!.AsNode());
                            }

                            if (rangeNode == null)
                            {
                                if (reference.ReferencingFile!.Statements != null && reference.ReferencingFile.Statements.Nodes.Length > 0)
                                {
                                    rangeNode = reference.ReferencingFile.Statements.Nodes[0];
                                }
                                else
                                {
                                    rangeNode = reference.ReferencingFile.AsNode();
                                }
                            }
                            references.Add(FindRefsSupport.NewNodeEntry(rangeNode));
                            break;
                        }
                    case ModuleReferenceKind.Reference:
                        references.Add(new ReferenceEntry
                        {
                            Kind = EntryKind.Range,
                            FileName = reference.ReferencingFile!.FileName(),
                            TextRange = reference.Ref!.Value.TextRange,
                        });
                        break;
                }
            }

            // Add references to the module declarations themselves
            if ((symbol.Declarations?.Length ?? 0) > 0)
            {
                foreach (var decl in symbol.Declarations!)
                {
                    switch (decl.Kind)
                    {
                        case SyntaxKind.SourceFile:
                            // Don't include the source file itself. (This may not be ideal behavior, but awkward to include an entire file as a reference.)
                            continue;
                        case SyntaxKind.ModuleDeclaration:
                            if (sourceFilesSet.Has(AstUtilities.GetSourceFileOfNode(decl)!.FileName()))
                            {
                                references.Add(FindRefsSupport.NewNodeEntry(decl.AsModuleDeclaration().Name()!));
                            }
                            break;
                        default:
                            // This may be merged with something (e.g. a class merged with a namespace).
                            continue;
                    }
                }
            }

            // Handle export equals declarations
            var exported = symbol.Exports?[InternalSymbolName.ExportEquals];
            if (exported != null && (exported.Declarations?.Length ?? 0) > 0)
            {
                foreach (var decl in exported.Declarations!)
                {
                    var sourceFile = AstUtilities.GetSourceFileOfNode(decl)!;
                    if (sourceFilesSet.Has(sourceFile.FileName()))
                    {
                        Node node;
                        // At `module.exports = ...`, reference node is `module`
                        if (NodeTests.IsBinaryExpression(decl) && NodeTests.IsPropertyAccessExpression(decl.AsBinaryExpression().Left!))
                        {
                            node = decl.AsBinaryExpression().Left!.Expression()!;
                        }
                        else if (NodeTests.IsExportAssignment(decl))
                        {
                            // Find the export keyword
                            node = global::Ts.AstNav.Tokens.FindChildOfKind(decl, SyntaxKind.ExportKeyword, sourceFile)!;
                            global::Ts.Core.Debug.Assert(node != null, "Expected to find export keyword");
                        }
                        else
                        {
                            node = AstUtilities.GetNameOfDeclaration(decl)!;
                            if (node == null)
                            {
                                node = decl;
                            }
                        }
                        references.Add(FindRefsSupport.NewNodeEntry(node!));
                    }
                }
            }

            if (references.Count > 0)
            {
                return
                [
                    new SymbolAndEntries(
                        new FarDefinition { Kind = DefinitionKind.Symbol, Symbol = symbol },
                        references),
                ];
            }
            return [];
        }
        finally
        {
            done();
        }
    }

    // findallreferences.go:1036-1090 — mergeReferences.
    internal List<SymbolAndEntries> MergeReferences(global::Ts.Compiler.Program program, params List<SymbolAndEntries>?[] referencesToMerge)
    {
        var result = new List<SymbolAndEntries>();
        static int GetSourceFileIndexOfEntry(global::Ts.Compiler.Program program, ReferenceEntry entry)
        {
            SourceFile? sourceFile;
            if (entry.Kind == EntryKind.Range)
            {
                sourceFile = program.GetSourceFile(entry.FileName);
            }
            else
            {
                sourceFile = AstUtilities.GetSourceFileOfNode(entry.Node!);
            }
            var sourceFiles = program.SourceFiles();
            for (var i = 0; i < sourceFiles.Length; i++)
            {
                if (ReferenceEquals(sourceFiles[i], sourceFile))
                {
                    return i;
                }
            }
            return -1;
        }

        foreach (var references in referencesToMerge)
        {
            if (references == null || references.Count == 0)
            {
                continue;
            }
            if (result.Count == 0)
            {
                result = references;
                continue;
            }
            foreach (var entry in references)
            {
                if (entry.Definition == null || entry.Definition.Kind != DefinitionKind.Symbol)
                {
                    result.Add(entry);
                    continue;
                }
                var symbol = entry.Definition.Symbol;
                var refIndex = result.FindIndex(@ref => @ref.Definition != null &&
                    @ref.Definition.Kind == DefinitionKind.Symbol &&
                    ReferenceEquals(@ref.Definition.Symbol, symbol));
                if (refIndex == -1)
                {
                    result.Add(entry);
                    continue;
                }

                var reference = result[refIndex];
                var sortedRefs = new List<ReferenceEntry>(reference.References);
                sortedRefs.AddRange(entry.References);
                sortedRefs.Sort((entry1, entry2) =>
                {
                    var entry1File = GetSourceFileIndexOfEntry(program, entry1);
                    var entry2File = GetSourceFileIndexOfEntry(program, entry2);
                    if (entry1File != entry2File)
                    {
                        return entry1File.CompareTo(entry2File);
                    }

                    return CompareRanges(GetRangeOfEntry(entry1), GetRangeOfEntry(entry2));
                });
                result[refIndex] = new SymbolAndEntries(reference.Definition, sortedRefs);
            }
        }
        return result;
    }

    // lsproto.CompareRanges — the generated-protocol layer has no compare helper yet, so
    // the mergeReferences ordering comparator spells it out (line, then character).
    private static int CompareRanges(global::Ts.LsProto.Range a, global::Ts.LsProto.Range b)
    {
        var lineDiff = a.Start.Line.CompareTo(b.Start.Line);
        if (lineDiff != 0)
        {
            return lineDiff;
        }
        var charDiff = a.Start.Character.CompareTo(b.Start.Character);
        if (charDiff != 0)
        {
            return charDiff;
        }
        var endLineDiff = a.End.Line.CompareTo(b.End.Line);
        if (endLineDiff != 0)
        {
            return endLineDiff;
        }
        return a.End.Character.CompareTo(b.End.Character);
    }

    // == functions for conversions ==
    // findallreferences.go:963-974 — convertSymbolAndEntriesToLocations.
    internal List<global::Ts.LsProto.Location> ConvertSymbolAndEntriesToLocations(SymbolAndEntries s, bool includeDeclarations)
    {
        var references = s.References;

        // !!! includeDeclarations
        if (!includeDeclarations && s.Definition != null)
        {
            references = references.FindAll(entry => !FindRefsSupport.IsDeclarationOfSymbol(entry.Node, s.Definition.Symbol));
        }

        return ConvertEntriesToLocations(references);
    }

    // findallreferences.go:1000-1006 — convertEntriesToLocations.
    internal List<global::Ts.LsProto.Location> ConvertEntriesToLocations(List<ReferenceEntry> entries)
    {
        var locations = new List<global::Ts.LsProto.Location>(entries.Count);
        foreach (var entry in entries)
        {
            locations.Add(GetLocationOfEntry(entry));
        }
        return locations;
    }

    // findallreferences.go:1246-1284 — getReferencesForStringLiteral (LanguageService
    // wrapper; the body lives in FindRefsSpecial with the rest of the string-literal family).
    internal List<SymbolAndEntries> GetReferencesForStringLiteral(
        CancellationToken ctx,
        Node node,
        SourceFile[] sourceFiles,
        global::Ts.Checker.Checker checker)
    {
        return FindRefsSpecial.GetReferencesForStringLiteral(ctx, node, sourceFiles, checker);
    }
}

// findallreferences.go:1748-1776 + the refState search machinery (:1778-2655), ported as a
// static engine class because the Go free function takes (program, symbol, node, …) without
// an LanguageService receiver.
internal static class FindRefsEngine
{
    private readonly record struct InheritKey(global::Ts.Ast.Symbol Symbol, global::Ts.Ast.Symbol Parent);

    // findallreferences.go:1748-1776 — getReferencedSymbolsForSymbol.
    // Core find-all-references algorithm for a normal symbol.
    internal static List<SymbolAndEntries> GetReferencedSymbolsForSymbol(
        CancellationToken ctx,
        global::Ts.Compiler.Program program,
        global::Ts.Ast.Symbol originalSymbol,
        Node? node,
        SourceFile[] sourceFiles,
        Set<string> sourceFilesSet,
        global::Ts.Checker.Checker checker,
        RefOptions options)
    {
        var symbol = FindRefsSupport.SkipPastExportOrImportSpecifierOrUnion(originalSymbol, node, checker /*useLocalSymbolForExportSpecifier*/, !FindRefsSupport.IsForRenameWithPrefixAndSuffixText(options)) ?? originalSymbol;

        // Compute the meaning from the location and the symbol it references
        var searchMeaning = SemanticMeaning.All;
        if (options.Use != ReferenceUse.Rename)
        {
            searchMeaning = LsMeaning.GetIntersectingMeaningFromDeclarations(node!, symbol, SemanticMeaning.All);
        }
        var state = new RefState(ctx, program, sourceFiles, sourceFilesSet, node, checker, searchMeaning, options);

        Node? exportSpecifier = null;
        if (FindRefsSupport.IsForRenameWithPrefixAndSuffixText(options) && symbol.Declarations!.Length != 0)
        {
            foreach (var decl in symbol.Declarations!)
            {
                if (NodeTests.IsExportSpecifier(decl))
                {
                    exportSpecifier = decl;
                    break;
                }
            }
        }
        if (exportSpecifier != null)
        {
            // When renaming at an export specifier, rename the export and not the thing being exported.
            state.GetReferencesAtExportSpecifier(exportSpecifier.Name()!, symbol, exportSpecifier.AsExportSpecifier(), state.CreateSearch(node, originalSymbol, ImpExpKind.Unknown /*comingFrom*/, "", null), true /*addReferencesHere*/, true /*alwaysGetReferences*/);
        }
        else if (node != null && node.Kind == SyntaxKind.DefaultKeyword && symbol.Name == InternalSymbolName.Default && symbol.Parent != null)
        {
            state.AddReference(node, symbol, EntryKind.Node);
            state.SearchForImportsOfExport(node, symbol, new ExportInfo(symbol.Parent, ExportKind.Default));
        }
        else
        {
            var search = state.CreateSearch(node, symbol, ImpExpKind.Unknown /*comingFrom*/, "", state.PopulateSearchSymbolSet(symbol, node, options.Use == ReferenceUse.Rename, options.UseAliasesForRename, options.Implementations));
            state.GetReferencesInContainerOrFiles(symbol, search);
        }

        return state.Result;
    }

    // findallreferences.go:1778-1819 — refSearch + refState.
    internal sealed class RefSearch
    {
        // If coming from an export, we will not recursively search for the imported symbol (since that's where we came from).
        public ImpExpKind ComingFrom; // import, export

        public global::Ts.Ast.Symbol Symbol = null!;
        public string Text = "";
        public string EscapedText = "";

        // Only set if `options.implementations` is true. These are the symbols checked to get the implementations of a property access.
        public List<global::Ts.Ast.Symbol> Parents = [];

        public List<global::Ts.Ast.Symbol> AllSearchSymbols = [];

        // Whether a symbol is in the search set.
        // Do not compare directly to `symbol` because there may be related symbols to search for. See `populateSearchSymbolSet`.
        public Func<global::Ts.Ast.Symbol, bool> Includes = null!;
    }

    internal sealed class RefState
    {
        private readonly SourceFile[] sourceFiles;
        private readonly Set<string> sourceFilesSet;
        private readonly string specialSearchKind; // "none", "constructor", or "class"
        private readonly global::Ts.Checker.Checker checker;
        private readonly CancellationToken ctx;
        private readonly global::Ts.Compiler.Program program;
        private readonly SemanticMeaning searchMeaning;
        private readonly RefOptions options;
        internal List<SymbolAndEntries> Result = [];
        private readonly Dictionary<InheritKey, bool> inheritsFromCache = [];
        private readonly Set<Node> seenContainingTypeReferences = new(); // node seen tracker
        private readonly Set<Node> seenReExportRHS = new(); // node seen tracker
        private ImportTracker? importTracker;
        private readonly Dictionary<global::Ts.Ast.Symbol, SymbolAndEntries> symbolToReferences = [];
        private readonly Dictionary<SourceFile, Set<global::Ts.Ast.Symbol>> sourceFileToSeenSymbols = [];

        internal RefState(
            CancellationToken ctx,
            global::Ts.Compiler.Program program,
            SourceFile[] sourceFiles,
            Set<string> sourceFilesSet,
            Node? node,
            global::Ts.Checker.Checker checker,
            SemanticMeaning searchMeaning,
            RefOptions options)
        {
            this.sourceFiles = sourceFiles;
            this.sourceFilesSet = sourceFilesSet;
            this.specialSearchKind = FindRefsSupport.GetSpecialSearchKind(node);
            this.checker = checker;
            this.ctx = ctx;
            this.program = program;
            this.searchMeaning = searchMeaning;
            this.options = options;
        }

        // findallreferences.go:1837-1839 — includesSourceFile.
        internal bool IncludesSourceFile(SourceFile sourceFile) => sourceFilesSet.Has(sourceFile.FileName());

        // findallreferences.go:1841-1846 — getImportSearches.
        internal ImportsResult GetImportSearches(global::Ts.Ast.Symbol exportSymbol, ExportInfo exportInfo)
        {
            importTracker ??= ImportTrackerSupport.CreateImportTracker(ctx, program, sourceFiles, sourceFilesSet, checker);
            return importTracker(exportSymbol, exportInfo, options.Use == ReferenceUse.Rename);
        }

        // findallreferences.go:1848-1879 — createSearch.
        // @param allSearchSymbols set of additional symbols for use by `includes`
        internal RefSearch CreateSearch(Node? location, global::Ts.Ast.Symbol symbol, ImpExpKind comingFrom, string text, List<global::Ts.Ast.Symbol>? allSearchSymbols)
        {
            // Note: if this is an external module symbol, the name doesn't include quotes.
            // Note: getLocalSymbolForExportDefault handles `export default class C {}`, but not `export default C` or `export { C as default }`.
            // The other two forms seem to be handled downstream (e.g. in `skipPastExportOrImportSpecifier`), so special-casing the first form
            // here appears to be intentional).
            if (text.Length == 0)
            {
                var s = global::Ts.Binder.NameResolver.GetLocalSymbolForExportDefault(symbol);
                if (s == null)
                {
                    s = FindRefsSupport.GetNonModuleSymbolOfMergedModuleSymbol(symbol);
                    if (s == null)
                    {
                        s = symbol;
                    }
                }
                text = StringUtil.StringUtil.StripQuotes(AstUtilities.SymbolName(s));
            }
            if (allSearchSymbols == null || allSearchSymbols.Count == 0)
            {
                allSearchSymbols = [symbol];
            }
            var search = new RefSearch
            {
                Symbol = symbol,
                ComingFrom = comingFrom,
                Text = text,
                EscapedText = text,
                AllSearchSymbols = allSearchSymbols,
                Includes = sym => allSearchSymbols.Contains(sym),
            };
            if (options.Implementations && location != null)
            {
                search.Parents = FindRefsSupport.GetParentSymbolsOfPropertyAccess(location, symbol, checker);
            }
            return search;
        }

        // findallreferences.go:1881-1891 — referenceAdder.
        private Action<Node, EntryKind> ReferenceAdder(global::Ts.Ast.Symbol searchSymbol)
        {
            if (!symbolToReferences.TryGetValue(searchSymbol, out var symbolAndEntries) || symbolAndEntries == null)
            {
                symbolAndEntries = SymbolAndEntries.NewSymbolAndEntries(DefinitionKind.Symbol, null, searchSymbol, []);
                symbolToReferences[searchSymbol] = symbolAndEntries;
                Result.Add(symbolAndEntries);
            }
            return (node, kind) =>
            {
                symbolAndEntries.References.Add(FindRefsSupport.NewNodeEntryWithKind(node, kind));
            };
        }

        // findallreferences.go:1893-1905 — addReference.
        internal void AddReference(Node referenceLocation, global::Ts.Ast.Symbol symbol, EntryKind kind)
        {
            // if rename symbol from default export anonymous function, for example `export default function() {}`, we do not need to add reference
            if (options.Use == ReferenceUse.Rename && referenceLocation.Kind == SyntaxKind.DefaultKeyword)
            {
                return;
            }

            var addRef = ReferenceAdder(symbol);
            if (options.Implementations)
            {
                AddImplementationReferences(referenceLocation, n => addRef(n, kind));
            }
            else
            {
                addRef(referenceLocation, kind);
            }
        }

        // findallreferences.go:2000-2058 — addImplementationReferences.
        internal void AddImplementationReferences(Node refNode, Action<Node> addRef)
        {
            // Check if we found a function/propertyAssignment/method with an implementation or initializer
            if (AstUtilities.IsDeclarationName(refNode) && FindRefsSupport.IsImplementation(refNode.Parent!))
            {
                addRef(refNode);
                return;
            }

            if (refNode.Kind != SyntaxKind.Identifier)
            {
                return;
            }

            if (refNode.Parent!.Kind == SyntaxKind.ShorthandPropertyAssignment)
            {
                // Go ahead and dereference the shorthand assignment by going to its definition
                FindRefsSupport.GetReferenceEntriesForShorthandPropertyAssignment(refNode, checker, addRef);
            }

            // Check if the node is within an extends or implements clause

            if (FindRefsSupport.GetContainingNodeIfInHeritageClause(refNode) is { } containingNode)
            {
                addRef(containingNode);
                return;
            }

            // If we got a type reference, try and see if the reference applies to any expressions that can implement an interface
            // Find the first node whose parent isn't a type node -- i.e., the highest type node.
            var typeNode = AstUtilities.FindAncestor(refNode, a =>
                !NodeTests.IsQualifiedName(a.Parent!) && !AstUtilities.IsTypeNode(a.Parent!) && !AstUtilities.IsTypeElement(a.Parent!));

            if (typeNode == null || typeNode.Parent!.Type() == null)
            {
                return;
            }

            var typeHavingNode = typeNode.Parent!;
            if (typeHavingNode.Type() == typeNode && seenContainingTypeReferences.AddIfAbsent(typeHavingNode))
            {
                void AddIfImplementation(Node e)
                {
                    if (FindRefsSupport.IsImplementationExpression(e))
                    {
                        addRef(e);
                    }
                }
                if (AstUtilities.HasInitializer(typeHavingNode))
                {
                    AddIfImplementation(typeHavingNode.Initializer()!);
                }
                else if (AstUtilities.IsFunctionLike(typeHavingNode) && typeHavingNode.Body() != null)
                {
                    var body = typeHavingNode.Body()!;
                    if (body.Kind == SyntaxKind.Block)
                    {
                        AstUtilities.ForEachReturnStatement(body, returnStatement =>
                        {
                            var expr = returnStatement.Expression();
                            if (expr != null)
                            {
                                AddIfImplementation(expr);
                            }
                            return false;
                        });
                    }
                    else
                    {
                        AddIfImplementation(body);
                    }
                }
                else if (AstUtilities.IsAssertionExpression(typeHavingNode) || NodeTests.IsSatisfiesExpression(typeHavingNode))
                {
                    AddIfImplementation(typeHavingNode.Expression()!);
                }
            }
        }

        // findallreferences.go:2060-2073 — getReferencesInContainerOrFiles.
        internal void GetReferencesInContainerOrFiles(global::Ts.Ast.Symbol symbol, RefSearch search)
        {
            // Try to get the smallest valid scope that we can limit our search to;
            // otherwise we'll need to search globally (i.e. include each file).
            if (FindRefsSupport.GetSymbolScope(symbol) is { } scope)
            {
                var addReferencesHere = scope.Kind != SyntaxKind.SourceFile || Array.IndexOf(sourceFiles, scope.AsSourceFile()) >= 0;
                GetReferencesInContainer(scope, AstUtilities.GetSourceFileOfNode(scope)!, search, addReferencesHere);
            }
            else
            {
                // Global search
                foreach (var sourceFile in sourceFiles)
                {
                    // state.cancellationToken.throwIfCancellationRequested();
                    SearchForName(sourceFile, search);
                }
            }
        }

        // findallreferences.go:2075-2078 — getReferencesInSourceFile.
        internal void GetReferencesInSourceFile(SourceFile sourceFile, RefSearch search, bool addReferencesHere)
        {
            // state.cancellationToken.throwIfCancellationRequested();
            GetReferencesInContainer(sourceFile.AsNode(), sourceFile, search, addReferencesHere);
        }

        // findallreferences.go:2080-2091 — getReferencesInContainer.
        private void GetReferencesInContainer(Node container, SourceFile sourceFile, RefSearch search, bool addReferencesHere)
        {
            // Search within node "container" for references for a search value, where the search value is defined as a
            //     tuple of (searchSymbol, searchText, searchLocation, and searchMeaning).
            // searchLocation: a node where the search value
            if (!MarkSearchedSymbols(sourceFile, search.AllSearchSymbols))
            {
                return;
            }

            foreach (var position in FindRefsSpecial.GetPossibleSymbolReferencePositionsStatic(sourceFile, search.Text, container))
            {
                GetReferencesAtLocation(sourceFile, position, search, addReferencesHere);
            }
        }

        // findallreferences.go:2093-2106 — markSearchedSymbols.
        private bool MarkSearchedSymbols(SourceFile sourceFile, List<global::Ts.Ast.Symbol> symbols)
        {
            if (!sourceFileToSeenSymbols.TryGetValue(sourceFile, out var seenSymbols) || seenSymbols == null)
            {
                seenSymbols = new();
                sourceFileToSeenSymbols[sourceFile] = seenSymbols;
            }
            var anyNewSymbols = false;
            foreach (var sym in symbols)
            {
                if (seenSymbols.AddIfAbsent(sym))
                {
                    anyNewSymbols = true;
                }
            }
            return anyNewSymbols;
        }

        // findallreferences.go:2108-2177 — getReferencesAtLocation.
        private void GetReferencesAtLocation(SourceFile sourceFile, int position, RefSearch search, bool addReferencesHere)
        {
            var referenceLocation = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, position);

            if (!LanguageService.IsValidReferencePosition(referenceLocation, search.Text))
            {
                // This wasn't the start of a token.  Check to see if it might be a
                // match in a comment or string if that's what the caller is asking
                // for.

                // !!! not implemented
                // if (!state.options.implementations && (state.options.findInStrings && isInString(sourceFile, position) || state.options.findInComments && isInNonReferenceComment(sourceFile, position))) {
                // 	// In the case where we're looking inside comments/strings, we don't have
                // 	// an actual definition.  So just use 'undefined' here.  Features like
                // 	// 'Rename' won't care (as they ignore the definitions), and features like
                // 	// 'FindReferences' will just filter out these results.
                // 	state.addStringOrCommentReference(sourceFile.FileName, createTextSpan(position, search.text.length));
                // }

                return;
            }

            if ((LsMeaning.GetMeaningFromLocation(referenceLocation) & searchMeaning) == 0)
            {
                return;
            }

            var referenceSymbol = checker.GetSymbolAtLocation(referenceLocation);
            if (referenceSymbol == null)
            {
                return;
            }

            var parent = referenceLocation.Parent!;
            if (parent.Kind == SyntaxKind.ImportSpecifier && parent.PropertyName() == referenceLocation)
            {
                // This is added through `singleReferences` in ImportsResult. If we happen to see it again, don't add it again.
                return;
            }

            if (parent.Kind == SyntaxKind.ExportSpecifier)
            {
                GetReferencesAtExportSpecifier(referenceLocation, referenceSymbol, parent.AsExportSpecifier(), search, addReferencesHere, false /*alwaysGetReferences*/);
                return;
            }

            var (relatedSymbol, relatedSymbolKind) = GetRelatedSymbol(search, referenceSymbol, referenceLocation);
            if (relatedSymbol == null)
            {
                GetReferenceForShorthandProperty(referenceSymbol, search);
                return;
            }

            switch (specialSearchKind)
            {
                case "none":
                    if (addReferencesHere)
                    {
                        AddReference(referenceLocation, relatedSymbol, relatedSymbolKind);
                    }
                    break;
                case "constructor":
                    AddConstructorReferences(referenceLocation, relatedSymbol, search, addReferencesHere);
                    break;
                case "class":
                    AddClassStaticThisReferences(referenceLocation, relatedSymbol, search, addReferencesHere);
                    break;
            }

            // Use the parent symbol if the location is commonjs require syntax on javascript files only.
            if (AstUtilities.IsInJSFile(referenceLocation) && referenceLocation.Parent!.Kind == SyntaxKind.BindingElement &&
                AstUtilities.IsVariableDeclarationInitializedToBareOrAccessedRequire(referenceLocation.Parent!.Parent!.Parent!))
            {
                referenceSymbol = referenceLocation.Parent!.Symbol();
                // The parent will not have a symbol if it's an ObjectBindingPattern (when destructuring is used).  In
                // this case, just skip it, since the bound identifiers are not an alias of the import.
                if (referenceSymbol == null)
                {
                    return;
                }
            }

            GetImportOrExportReferences(referenceLocation, referenceSymbol, search);
        }

        // findallreferences.go:2179-2203 — addConstructorReferences.
        private void AddConstructorReferences(Node referenceLocation, global::Ts.Ast.Symbol symbol, RefSearch search, bool addReferencesHere)
        {
            if (FindRefsSupport.IsNewOrCallExpressionTarget(referenceLocation, true) && addReferencesHere)
            {
                AddReference(referenceLocation, symbol, EntryKind.Node);
            }

            Action<Node, EntryKind> Pusher() => ReferenceAdder(search.Symbol);

            if (AstUtilities.IsClassLike(referenceLocation.Parent!))
            {
                // This is the class declaration containing the constructor.
                var sourceFile = AstUtilities.GetSourceFileOfNode(referenceLocation)!;
                FindRefsSupport.FindOwnConstructorReferences(search.Symbol, sourceFile, n => Pusher()(n, EntryKind.Node));
            }
            else
            {
                // If this class appears in `extends C`, then the extending class' "super" calls are references.
                if (FindRefsSupport.TryGetClassByExtendingIdentifier(referenceLocation) is { } classExtending)
                {
                    FindRefsSupport.FindSuperConstructorAccesses(classExtending, n => Pusher()(n, EntryKind.Node));
                    FindInheritedConstructorReferences(classExtending);
                }
            }
        }

        // findallreferences.go:2205-2240 — addClassStaticThisReferences.
        private void AddClassStaticThisReferences(Node referenceLocation, global::Ts.Ast.Symbol symbol, RefSearch search, bool addReferencesHere)
        {
            if (addReferencesHere)
            {
                AddReference(referenceLocation, symbol, EntryKind.Node);
            }

            var classLike = referenceLocation.Parent!;
            if (options.Use == ReferenceUse.Rename || !AstUtilities.IsClassLike(classLike))
            {
                return;
            }

            var addRef = ReferenceAdder(search.Symbol);
            var members = classLike.Members();
            if (members == null)
            {
                return;
            }
            foreach (var member in members)
            {
                if (!(FindRefsSupport.IsMethodOrAccessor(member) && AstUtilities.HasStaticModifier(member)))
                {
                    continue;
                }
                var body = member.Body();
                if (body != null)
                {
                    void Cb(Node node)
                    {
                        if (node.Kind == SyntaxKind.ThisKeyword)
                        {
                            addRef(node, EntryKind.Node);
                        }
                        else if (!AstUtilities.IsFunctionLike(node) && !AstUtilities.IsClassLike(node))
                        {
                            node.ForEachChild(child =>
                            {
                                Cb(child);
                                return false;
                            });
                        }
                    }
                    Cb(body);
                }
            }
        }

        // findallreferences.go:2242-2249 — findInheritedConstructorReferences.
        private void FindInheritedConstructorReferences(Node classDeclaration)
        {
            if (FindRefsSupport.HasOwnConstructor(classDeclaration))
            {
                return;
            }
            var classSymbol = classDeclaration.Symbol()!;
            var search = CreateSearch(null, classSymbol, ImpExpKind.Unknown, "", null);
            GetReferencesInContainerOrFiles(classSymbol, search);
        }

        // findallreferences.go:2251-2263 — getImportOrExportReferences.
        private void GetImportOrExportReferences(Node referenceLocation, global::Ts.Ast.Symbol referenceSymbol, RefSearch search)
        {
            var importOrExport = ImportTrackerSupport.GetImportOrExportSymbol(referenceLocation, referenceSymbol, checker, search.ComingFrom == ImpExpKind.Export);
            if (importOrExport == null)
            {
                return;
            }
            if (importOrExport.Kind == ImpExpKind.Import)
            {
                if (!FindRefsSupport.IsForRenameWithPrefixAndSuffixText(options))
                {
                    SearchForImportedSymbol(importOrExport.Symbol!);
                }
            }
            else
            {
                SearchForImportsOfExport(referenceLocation, importOrExport.Symbol!, importOrExport.ExportInfo!);
            }
        }

        // findallreferences.go:2265-2267 — markSeenReExportRHS.
        private bool MarkSeenReExportRHS(Node node) => seenReExportRHS.AddIfAbsent(node);

        // findallreferences.go:2269-2339 — getReferencesAtExportSpecifier.
        internal void GetReferencesAtExportSpecifier(
            Node referenceLocation,
            global::Ts.Ast.Symbol referenceSymbol,
            Node exportSpecifier,
            RefSearch search,
            bool addReferencesHere,
            bool alwaysGetReferences)
        {
            global::Ts.Core.Debug.Assert(!alwaysGetReferences || options.UseAliasesForRename, "If alwaysGetReferences is true, then prefix/suffix text must be enabled");

            var exportDeclaration = exportSpecifier.Parent!.Parent!.AsExportDeclaration();
            var propertyName = exportSpecifier.AsExportSpecifier().PropertyName;
            var name = exportSpecifier.Name()!;
            var localSymbol = FindRefsSupport.GetLocalSymbolForExportSpecifier(referenceLocation, referenceSymbol, exportSpecifier, checker);

            if (!alwaysGetReferences && !search.Includes(localSymbol))
            {
                return;
            }

            void AddRef()
            {
                if (addReferencesHere)
                {
                    AddReference(referenceLocation, localSymbol, EntryKind.Node);
                }
            }

            if (propertyName == null)
            {
                // Don't rename at `export { default } from "m";`. (but do continue to search for imports of the re-export)
                if (!(options.Use == ReferenceUse.Rename && AstUtilities.ModuleExportNameIsDefault(name)))
                {
                    AddRef();
                }
            }
            else if (referenceLocation == propertyName)
            {
                // For `export { foo as bar } from "baz"`, "`foo`" will be added from the singleReferences for import searches of the original export.
                // For `export { foo as bar };`, where `foo` is a local, so add it now.
                if (exportDeclaration.ModuleSpecifier == null)
                {
                    AddRef();
                }

                if (addReferencesHere && options.Use != ReferenceUse.Rename && MarkSeenReExportRHS(name))
                {
                    var exportSymbol = exportSpecifier.Symbol();
                    global::Ts.Core.Debug.Assert(exportSymbol != null, "exportSpecifier.Symbol() should not be nil");
                    AddReference(name, exportSymbol, EntryKind.Node);
                }
            }
            else
            {
                if (MarkSeenReExportRHS(referenceLocation))
                {
                    AddRef();
                }
            }

            // For `export { foo as bar }`, rename `foo`, but not `bar`.
            if (!FindRefsSupport.IsForRenameWithPrefixAndSuffixText(options) || alwaysGetReferences)
            {
                var isDefaultExport = AstUtilities.ModuleExportNameIsDefault(referenceLocation) || AstUtilities.ModuleExportNameIsDefault(exportSpecifier.Name()!);
                var exportKind = ExportKind.Named;
                if (isDefaultExport)
                {
                    exportKind = ExportKind.Default;
                }
                var exportSymbol = exportSpecifier.Symbol();
                global::Ts.Core.Debug.Assert(exportSymbol != null, "exportSpecifier.Symbol() should not be nil");
                var exportInfo = ImportTrackerSupport.GetExportInfo(exportSymbol, exportKind, checker);
                if (exportInfo != null)
                {
                    SearchForImportsOfExport(referenceLocation, exportSymbol, exportInfo);
                }
            }

            // At `export { x } from "foo"`, also search for the imported symbol `"foo".x`.
            if (search.ComingFrom != ImpExpKind.Export && exportDeclaration.ModuleSpecifier != null && propertyName == null && !FindRefsSupport.IsForRenameWithPrefixAndSuffixText(options))
            {
                var imported = checker.GetExportSpecifierLocalTargetSymbol(exportSpecifier);
                if (imported != null)
                {
                    SearchForImportedSymbol(imported);
                }
            }
        }

        // findallreferences.go:2341-2348 — searchForImportedSymbol.
        // Go to the symbol we imported from and find references for it.
        private void SearchForImportedSymbol(global::Ts.Ast.Symbol symbol)
        {
            foreach (var declaration in symbol.Declarations ?? [])
            {
                var exportingFile = AstUtilities.GetSourceFileOfNode(declaration)!;
                // Need to search in the file even if it's not in the search-file set, because it might export the symbol.
                GetReferencesInSourceFile(exportingFile, CreateSearch(declaration, symbol, ImpExpKind.Import, "", null), IncludesSourceFile(exportingFile));
            }
        }

        // findallreferences.go:2350-2386 — searchForImportsOfExport.
        // Search for all imports of a given exported symbol using `State.getImportSearches`. */
        internal void SearchForImportsOfExport(Node exportLocation, global::Ts.Ast.Symbol exportSymbol, ExportInfo exportInfo)
        {
            var r = GetImportSearches(exportSymbol, exportInfo);

            // For `import { foo as bar }` just add the reference to `foo`, and don't otherwise search in the file.
            if (r.ImportSearches.Count != 0 || r.SingleReferences.Count != 0)
            {
                if (r.SingleReferences.Count != 0)
                {
                    var addRef = ReferenceAdder(exportSymbol);
                    foreach (var singleRef in r.SingleReferences)
                    {
                        if (ShouldAddSingleReference(singleRef))
                        {
                            addRef(singleRef, EntryKind.Node);
                        }
                    }
                }

                // For each import, find all references to that import in its source file.
                foreach (var i in r.ImportSearches)
                {
                    GetReferencesInSourceFile(AstUtilities.GetSourceFileOfNode(i.ImportLocation)!, CreateSearch(i.ImportLocation, i.ImportSymbol!, ImpExpKind.Export, "", null), true /*addReferencesHere*/);
                }
            }

            if (r.IndirectUsers.Count != 0)
            {
                RefSearch? indirectSearch = null;
                switch (exportInfo.ExportKind)
                {
                    case ExportKind.Named:
                        indirectSearch = CreateSearch(exportLocation, exportSymbol, ImpExpKind.Export, "", null);
                        break;
                    case ExportKind.Default:
                        // Search for a property access to '.default'. This can't be renamed.
                        if (options.Use != ReferenceUse.Rename)
                        {
                            indirectSearch = CreateSearch(exportLocation, exportSymbol, ImpExpKind.Export, "default", null);
                        }
                        break;
                }
                if (indirectSearch != null)
                {
                    foreach (var indirectUser in r.IndirectUsers)
                    {
                        SearchForName(indirectUser, indirectSearch);
                    }
                }
            }
        }

        // findallreferences.go:2388-2401 — shouldAddSingleReference.
        private bool ShouldAddSingleReference(Node singleRef)
        {
            if (!HasMatchingMeaning(singleRef))
            {
                return false;
            }
            if (options.Use != ReferenceUse.Rename)
            {
                return true;
            }
            // Don't rename an import type `import("./module-name")` when renaming `name` in `export = name;`
            if (!NodeTests.IsIdentifier(singleRef) && !AstUtilities.IsImportOrExportSpecifier(singleRef.Parent!))
            {
                return false;
            }
            // At `default` in `import { default as x }` or `export { default as x }`, do add a reference, but do not rename.
            return !(AstUtilities.IsImportOrExportSpecifier(singleRef.Parent!) && AstUtilities.ModuleExportNameIsDefault(singleRef));
        }

        // findallreferences.go:2403-2405 — hasMatchingMeaning.
        private bool HasMatchingMeaning(Node referenceLocation) => (LsMeaning.GetMeaningFromLocation(referenceLocation) & searchMeaning) != 0;

        // findallreferences.go:2407-2422 — getReferenceForShorthandProperty.
        private void GetReferenceForShorthandProperty(global::Ts.Ast.Symbol referenceSymbol, RefSearch search)
        {
            if ((referenceSymbol.Flags & SymbolFlags.Transient) != 0 || referenceSymbol.ValueDeclaration == null)
            {
                return;
            }
            var shorthandValueSymbol = checker.GetShorthandAssignmentValueSymbol(referenceSymbol.ValueDeclaration);
            var name = AstUtilities.GetNameOfDeclaration(referenceSymbol.ValueDeclaration);

            // Because in short-hand property assignment, an identifier which stored as name of the short-hand property assignment
            // has two meanings: property name and property value. Therefore when we do findAllReference at the position where
            // an identifier is declared, the language service should return the position of the variable declaration as well as
            // the position in short-hand property assignment excluding property accessing. However, if we do findAllReference at the
            // position of property accessing, the referenceEntry of such position will be handled in the first case.
            if (name != null && search.Includes(shorthandValueSymbol!))
            {
                AddReference(name, shorthandValueSymbol!, EntryKind.Node);
            }
        }

        // === search ===
        // findallreferences.go:2425-2448 — populateSearchSymbolSet.
        internal List<global::Ts.Ast.Symbol> PopulateSearchSymbolSet(global::Ts.Ast.Symbol symbol, Node? location, bool isForRename, bool providePrefixAndSuffixText, bool implementations)
        {
            if (location == null)
            {
                return [symbol];
            }
            var result = new List<global::Ts.Ast.Symbol>();
            ForEachRelatedSymbol(
                symbol,
                location,
                isForRename,
                !(isForRename && providePrefixAndSuffixText),
                (sym, root, @base) =>
                {
                    // static method/property and instance method/property might have the same name. Only include static or only include instance.
                    if (@base != null)
                    {
                        if (FindRefsSupport.IsStaticSymbol(symbol) != FindRefsSupport.IsStaticSymbol(@base))
                        {
                            @base = null;
                        }
                    }
                    result.Add(@base ?? root ?? sym);
                    return null;
                }, // when try to find implementation, implementations is true, and not allowed to find base class
                /*allowBaseTypes*/ _ => !implementations);
            return result;
        }

        // findallreferences.go:2450-2480 — getRelatedSymbol.
        private (global::Ts.Ast.Symbol? Symbol, EntryKind Kind) GetRelatedSymbol(RefSearch search, global::Ts.Ast.Symbol referenceSymbol, Node referenceLocation)
        {
            return ForEachRelatedSymbol(
                referenceSymbol,
                referenceLocation,
                false, /*isForRenamePopulateSearchSymbolSet*/
                options.Use != ReferenceUse.Rename || options.UseAliasesForRename, /*onlyIncludeBindingElementAtReferenceLocation*/
                (sym, rootSymbol, baseSymbol) =>
                {
                    // check whether the symbol used to search itself is just the searched one.
                    if (baseSymbol != null)
                    {
                        // static method/property and instance method/property might have the same name. Only check static or only check instance.
                        if (FindRefsSupport.IsStaticSymbol(referenceSymbol) != FindRefsSupport.IsStaticSymbol(baseSymbol))
                        {
                            baseSymbol = null;
                        }
                    }
                    var searchSym = baseSymbol ?? rootSymbol ?? sym;
                    if (searchSym != null && search.Includes(searchSym))
                    {
                        if (rootSymbol != null && (sym.CheckFlags & CheckFlags.Synthetic) == 0)
                        {
                            return rootSymbol;
                        }
                        return sym;
                    }
                    // For a base type, use the symbol for the derived type. For a synthetic (e.g. union) property, use the union symbol.
                    return null;
                },
                rootSymbol =>
                {
                    var blocked = search.Parents.Count != 0;
                    if (blocked)
                    {
                        var any = false;
                        foreach (var parent in search.Parents)
                        {
                            if (ExplicitlyInheritsFrom(rootSymbol.Parent!, parent))
                            {
                                any = true;
                                break;
                            }
                        }
                        blocked = !any;
                    }
                    return !blocked;
                });
        }

        // findallreferences.go:2482-2616 — forEachRelatedSymbol.
        private (global::Ts.Ast.Symbol? Symbol, EntryKind Kind) ForEachRelatedSymbol(
            global::Ts.Ast.Symbol symbol,
            Node? location,
            bool isForRenamePopulateSearchSymbolSet,
            bool onlyIncludeBindingElementAtReferenceLocation,
            Func<global::Ts.Ast.Symbol, global::Ts.Ast.Symbol?, global::Ts.Ast.Symbol?, global::Ts.Ast.Symbol?> cbSymbol,
            Func<global::Ts.Ast.Symbol, bool> allowBaseTypes)
        {
            global::Ts.Ast.Symbol? FromRoot(global::Ts.Ast.Symbol sym)
            {
                // If this is a union property:
                //   - In populateSearchSymbolsSet we will add all the symbols from all its source symbols in all unioned types.
                //   - In findRelatedSymbol, we will just use the union symbol if any source symbol is included in the search.
                // If the symbol is an instantiation from a another symbol (e.g. widened symbol):
                //   - In populateSearchSymbolsSet, add the root the list
                //   - In findRelatedSymbol, return the source symbol if that is in the search. (Do not return the instantiation symbol.)
                foreach (var rootSymbol in checker.GetRootSymbols(sym))
                {
                    var result = cbSymbol(sym, rootSymbol, null /*baseSymbol*/);
                    if (result != null)
                    {
                        return result;
                    }
                    // Add symbol of properties/methods of the same name in base classes and implemented interfaces definitions
                    if (rootSymbol.Parent != null && (rootSymbol.Parent.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0 && allowBaseTypes(rootSymbol))
                    {
                        var baseResult = FindRefsSupport.GetPropertySymbolsFromBaseTypes(rootSymbol.Parent, rootSymbol.Name, checker, @base => cbSymbol(sym, rootSymbol, @base));
                        if (baseResult != null)
                        {
                            return baseResult;
                        }
                    }
                }
                return null;
            }

            if (LsMeaning.GetContainingObjectLiteralElement(location!) is { } containingObjectLiteralElement)
            {
                /* Because in short-hand property assignment, location has two meaning : property name and as value of the property
                 * When we do findAllReference at the position of the short-hand property assignment, we would want to have references to position of
                 * property name and variable declaration of the identifier.
                 * Like in below example, when querying for all references for an identifier 'name', of the property assignment, the language service
                 * should show both 'name' in 'obj' and 'name' in variable declaration
                 *      const name = "Foo";
                 *      const obj = { name };
                 * In order to do that, we will populate the search set with the value symbol of the identifier as a value of the property assignment
                 * so that when matching with potential reference symbol, both symbols from property declaration and variable declaration
                 * will be included correctly.
                 */
                var shorthandValueSymbol = checker.GetShorthandAssignmentValueSymbol(location!.Parent);
                // gets the local symbol
                if (shorthandValueSymbol != null && isForRenamePopulateSearchSymbolSet)
                {
                    // When renaming 'x' in `const o = { x }`, just rename the local variable, not the property.
                    return (cbSymbol(shorthandValueSymbol, null /*rootSymbol*/, null /*baseSymbol*/), EntryKind.SearchedLocalFoundProperty);
                }
                // If the location is in a context sensitive location (i.e. in an object literal) try
                // to get a contextual type for it, and add the property symbol from the contextual
                // type to the search set
                if (checker.GetContextualType(containingObjectLiteralElement.Parent!, ContextFlags.None) is { } contextualType)
                {
                    var symbols = checker.GetPropertySymbolsFromContextualType(containingObjectLiteralElement, contextualType, true /*unionSymbolOk*/);
                    foreach (var sym in symbols)
                    {
                        var res = FromRoot(sym);
                        if (res != null)
                        {
                            return (res, EntryKind.SearchedPropertyFoundLocal);
                        }
                    }
                }
                // If the location is name of property symbol from object literal destructuring pattern
                // Search the property symbol
                //      for ( { property: p2 } of elems) { }
                if (checker.GetPropertySymbolOfDestructuringAssignment(location) is { } propertySymbol)
                {
                    var res = cbSymbol(propertySymbol, null /*rootSymbol*/, null /*baseSymbol*/);
                    if (res != null)
                    {
                        return (res, EntryKind.SearchedPropertyFoundLocal);
                    }
                }
                if (shorthandValueSymbol != null)
                {
                    var res = cbSymbol(shorthandValueSymbol, null /*rootSymbol*/, null /*baseSymbol*/);
                    if (res != null)
                    {
                        return (res, EntryKind.SearchedLocalFoundProperty);
                    }
                }
            }

            if (LanguageService.GetMergedAliasedSymbolOfNamespaceExportDeclaration(location!, symbol, checker) is { } aliasedSymbol)
            {
                // In case of UMD module and global merging, search for global as well
                var res = cbSymbol(aliasedSymbol, null /*rootSymbol*/, null /*baseSymbol*/);
                if (res != null)
                {
                    return (res, EntryKind.Node);
                }
            }

            if (FromRoot(symbol) is { } rootRes)
            {
                return (rootRes, EntryKind.Node);
            }

            if (symbol.ValueDeclaration != null && AstUtilities.IsParameterPropertyDeclaration(symbol.ValueDeclaration, symbol.ValueDeclaration.Parent!))
            {
                var (paramProp1, paramProp2) = checker.GetSymbolsOfParameterPropertyDeclaration(symbol.ValueDeclaration, symbol.Name);
                global::Ts.Core.Debug.Assert(
                    (paramProp1.Flags & SymbolFlags.FunctionScopedVariable) != 0 && (paramProp2.Flags & SymbolFlags.ClassMember) != 0,
                    "GetSymbolsOfParameterPropertyDeclaration must return (parameter, member) pair");
                return FromRoot((symbol.Flags & SymbolFlags.FunctionScopedVariable) != 0 ? paramProp2 : paramProp1) is { } paramPropRes
                    ? (paramPropRes, EntryKind.Node)
                    : (null, EntryKind.None);
            }

            if (AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.ExportSpecifier) is { } exportSpecifier && (!isForRenamePopulateSearchSymbolSet || exportSpecifier.PropertyName() == null))
            {
                if (checker.GetExportSpecifierLocalTargetSymbol(exportSpecifier) is { } localSymbol)
                {
                    var res = cbSymbol(localSymbol, null /*rootSymbol*/, null /*baseSymbol*/);
                    if (res != null)
                    {
                        return (res, EntryKind.Node);
                    }
                }
            }

            // symbolAtLocation for a binding element is the local symbol. See if the search symbol is the property.
            // Don't do this when populating search set for a rename when prefix and suffix text will be provided -- just rename the local.
            if (!isForRenamePopulateSearchSymbolSet)
            {
                global::Ts.Ast.Symbol? bindingElementPropertySymbol;
                if (onlyIncludeBindingElementAtReferenceLocation)
                {
                    if (!LsDeps.IsObjectBindingElementWithoutPropertyName(location!.Parent!))
                    {
                        return (null, EntryKind.None);
                    }
                    bindingElementPropertySymbol = FindRefsSupport.GetPropertySymbolFromBindingElement(checker, location.Parent!);
                }
                else
                {
                    bindingElementPropertySymbol = FindRefsSupport.GetPropertySymbolOfObjectBindingPatternWithoutPropertyName(symbol, checker);
                }
                if (bindingElementPropertySymbol == null)
                {
                    return (null, EntryKind.None);
                }
                return (FromRoot(bindingElementPropertySymbol), EntryKind.SearchedPropertyFoundLocal);
            }

            global::Ts.Core.Debug.Assert(isForRenamePopulateSearchSymbolSet);

            // due to the above assert and the arguments at the uses of this function,
            // (onlyIncludeBindingElementAtReferenceLocation <=> !providePrefixAndSuffixTextForRename) holds
            var includeOriginalSymbolOfBindingElement = onlyIncludeBindingElementAtReferenceLocation;

            if (includeOriginalSymbolOfBindingElement)
            {
                if (FindRefsSupport.GetPropertySymbolOfObjectBindingPatternWithoutPropertyName(symbol, checker) is { } bindingElementPropertySymbol2)
                {
                    return (FromRoot(bindingElementPropertySymbol2), EntryKind.SearchedPropertyFoundLocal);
                }
            }
            return (null, EntryKind.None);
        }

        // findallreferences.go:2618-2623 — searchForName.
        // Search for all occurrences of an identifier in a source file (and filter out the ones that match).
        private void SearchForName(SourceFile sourceFile, RefSearch search)
        {
            if (sourceFile.GetNameTable().ContainsKey(search.EscapedText))
            {
                GetReferencesInSourceFile(sourceFile, search, true /*addReferencesHere*/);
            }
        }

        // findallreferences.go:2625-2654 — explicitlyInheritsFrom.
        private bool ExplicitlyInheritsFrom(global::Ts.Ast.Symbol symbol, global::Ts.Ast.Symbol parent)
        {
            if (ReferenceEquals(symbol, parent))
            {
                return true;
            }

            // Check cache first
            var key = new InheritKey(symbol, parent);
            if (inheritsFromCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // Set to false initially to prevent infinite recursion
            inheritsFromCache[key] = false;

            if (symbol.Declarations == null)
            {
                return false;
            }

            var inherits = false;
            foreach (var declaration in symbol.Declarations)
            {
                var superTypeNodes = FindRefsSupport.GetAllSuperTypeNodes(declaration);
                foreach (var typeReference in superTypeNodes)
                {
                    var typ = checker.GetTypeAtLocation(typeReference);
                    if (typ != null && typ.Symbol != null && ExplicitlyInheritsFrom(typ.Symbol!, parent))
                    {
                        inherits = true;
                        break;
                    }
                }
                if (inherits)
                {
                    break;
                }
            }

            // Update cache with the actual result
            inheritsFromCache[key] = inherits;
            return inherits;
        }
    }
}
