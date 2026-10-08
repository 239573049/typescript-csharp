// Ported from tsgo internal/ls/sourcedefinition.go.
//
// PORT-NOTE (structural deviations only — every algorithmic branch is a 1:1 translation):
//   1. Go reads lsproto.GetClientCapabilities(ctx).TextDocument.Definition.LinkSupport from
//      the request context; the C# foundation has no per-request context carrier, so
//      clientSupportsLink is threaded as an explicit entry parameter (default false = the
//      Go zero capability: plain Locations).
//   2. l.getMappedLocation / l.tryGetSourcePosition are the source-map detour
//      (sourcemap wave); they are deferred and the identity mapping (file name → document
//      URI, raw range → LSP range) stands in — marked at each arm.
//   3. provideDefinitionWorker (definition.go) has no LSP-shaped counterpart in the C#
//      foundation yet (Ls/Definition.cs is the reduced pre-LSP port), so the fallback arms
//      call GetDefinitionAtPosition and map its DefinitionLocation[] into the response
//      shape. createDefinitionLocations is likewise hosted in this file until the full
//      definition.go port lands (it is an instance member there; Go free
//      createLocationsFromLinks rides SourceDefinitionDeps).
//   4. resolveTripleSlashReference's /// <reference types/lib> arms defer with the program
//      API (see SourceDefinitionDeps.GetReferenceAtPosition).
// ctx.Err() checks map to CancellationToken.IsCancellationRequested; positions are UTF-16
// code units; Go nil slices map to empty lists and Go returns of nil map to null where the
// caller distinguishes them.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Collections; // Set<T>.Has extension method
using Ts.Module; // ResolvedModule.IsResolved extension method
using Ts.PackageJson; // InfoCacheEntry.Exists / GetContents extension methods
using ResolutionMode = Ts.Core.ModuleKind;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // sourcedefinition.go:24 — ProvideSourceDefinition.
    public global::Ts.LsProto.DefinitionResponse ProvideSourceDefinition(
        string documentURI,
        global::Ts.LsProto.Position position,
        bool clientSupportsLink = false,
        CancellationToken cancellationToken = default)
    {
        var program = GetProgram();
        var file = GetProgramAndFile(documentURI);
        var pos = LsDeps.LineAndCharacterToPosition(file, position.Line, position.Character);
        var resolver = NewSourceDefResolver(program, file.FileName());
        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, pos);

        if (node.Kind == SyntaxKind.SourceFile)
        {
            // Triple-slash directives are comments, not AST nodes, so
            // GetTouchingPropertyName returns the SourceFile node.
            var (tripleSlashDeclarations, @ref) = resolver.ResolveTripleSlashReference(file, pos, program);
            if (tripleSlashDeclarations.Count != 0)
            {
                // Go dereferences ref directly (non-nil whenever declarations were found);
                // FileReference is a value type in C#, so the dereference is spelled
                // GetValueOrDefault() with the invariant asserted (Debug.Assert does not
                // participate in nullable flow analysis, so .Value would trip CS8629).
                global::Ts.Core.Debug.Assert(@ref.HasValue, "expected a triple-slash reference");
                var refSelectionRange = CreateLspRangeFromBounds(@ref.GetValueOrDefault().Pos, @ref.GetValueOrDefault().End, file);
                return CreateDefinitionLocations(refSelectionRange, clientSupportsLink, tripleSlashDeclarations, null /*reference*/);
            }
            return new global::Ts.LsProto.DefinitionResponse();
        }

        var originSelectionRange = CreateLspRangeFromNode(node, file);

        // If the cursor is directly on a module specifier string, resolve to the
        // implementation file's entry point.
        var containingModuleSpecifier = FindContainingModuleSpecifier(node);
        if (node == containingModuleSpecifier)
        {
            var specifierMode = program.GetModeForUsageLocation(file, containingModuleSpecifier);
            if (resolver.ResolveImplementation(containingModuleSpecifier!.Text(), specifierMode) is { } entryImplementationFile && entryImplementationFile != "")
            {
                if (resolver.GetOrParseSourceFile(entryImplementationFile) is { } entrySourceFile)
                {
                    return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, GetSourceDefinitionEntryDeclarations(entrySourceFile), null);
                }
            }
            return ProvideDefinitionWorker(documentURI, position, cancellationToken);
        }

        // Phase 1: Syntactic fast path — when the cursor is inside an
        // import/require/export, forward-resolve the module specifier to an
        // implementation file and search it directly. This avoids acquiring
        // the type checker entirely when the fast path succeeds.
        var resolvedImplFile = "";
        if (containingModuleSpecifier != null)
        {
            var specifierMode = program.GetModeForUsageLocation(file, containingModuleSpecifier);
            resolvedImplFile = resolver.ResolveImplementation(containingModuleSpecifier.Text(), specifierMode);
        }

        if (resolvedImplFile != "")
        {
            var names = GetCandidateSourceDeclarationNames(node, null);
            var moduleResults = resolver.SearchImplementationFile(node, resolvedImplFile, names);
            if (moduleResults != null && moduleResults.Count != 0)
            {
                if (!AstUtilities.IsPartOfTypeNode(node) && !global::Ts.Checker.Checker.IsPartOfTypeOnlyImportOrExportDeclaration(node) || HasConcreteSourceDeclarations(moduleResults))
                {
                    return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, UniqueDeclarationNodes(moduleResults), null);
                }
            }
        }

        // Phase 2: Type checker path — acquire the checker for the original file
        // and use its declarations and module specifier to map to source
        // implementations. This is the only point where the checker is used;
        // after this, only the NoDts module resolver and file parsing are needed.
        var (checkerDeclarations, moduleSpecifier) = GetSourceDefCheckerInfo(program, file, node, cancellationToken);

        // Phase 3: Map checker results to source definitions.
        var declarations = resolver.ResolveFromCheckerInfo(node, resolvedImplFile, checkerDeclarations, moduleSpecifier);
        if (declarations.Count == 0)
        {
            // If we resolved an implementation file from an import/export but
            // couldn't find specific declarations, fall back to the file entry
            // point rather than the standard definition provider — unless the
            // checker found declarations that are all type-only (e.g. interfaces),
            // in which case the .d.ts definition is more appropriate.
            if (containingModuleSpecifier != null && resolvedImplFile != "" && !HasConcreteSourceDeclarations(checkerDeclarations))
            {
                if (resolver.GetOrParseSourceFile(resolvedImplFile) is { } fallbackSourceFile)
                {
                    return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, GetSourceDefinitionEntryDeclarations(fallbackSourceFile), null);
                }
            }
            return ProvideDefinitionWorker(documentURI, position, cancellationToken);
        }
        return CreateDefinitionLocations(originSelectionRange, clientSupportsLink, declarations, null /*reference*/);
    }

    // definition.go:155 — createDefinitionLocations. Hosted here: the C# Definition.cs
    // foundation subset predates the LSP-shaped definition pipeline (see the file
    // PORT-NOTE); this source-definition consumer owns the LSP-shaped builder.
    private global::Ts.LsProto.DefinitionResponse CreateDefinitionLocations(
        global::Ts.LsProto.Range originSelectionRange,
        bool clientSupportsLink,
        List<Node> declarations,
        RefInfo? reference)
    {
        var locations = new List<global::Ts.LsProto.LocationLink>();
        var locationRanges = new HashSet<(string FileName, global::Ts.Core.TextRange Range)>();

        if (reference != null)
        {
            var emptyTargetRange = new global::Ts.LsProto.Range
            {
                Start = new global::Ts.LsProto.Position
                {
                    Line = 0,
                    Character = 0,
                },
                End = new global::Ts.LsProto.Position
                {
                    Line = 0,
                    Character = 0,
                },
            };
            locations.Add(new global::Ts.LsProto.LocationLink
            {
                OriginSelectionRange = originSelectionRange,
                TargetUri = LsDeps.FileNameToDocumentURI(reference.FileName),
                TargetRange = emptyTargetRange,
                TargetSelectionRange = emptyTargetRange,
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
            if (locationRanges.Add((fileName, nameRange)))
            {
                var contextNode = SourceDefinitionDeps.GetContextNode(decl) ?? decl;
                var contextRange = SourceDefinitionDeps.ToContextRange(nameRange, file, contextNode) ?? nameRange;
                // PORT-NOTE(getMappedLocation): the source-map detour is deferred with the
                // sourcemap wave; the identity mapping stands in.
                var targetUri = LsDeps.FileNameToDocumentURI(fileName);
                var targetSelectionLoc = LsDeps.ToLspRange(file, nameRange);
                var targetLoc = LsDeps.ToLspRange(file, contextRange);
                locations.Add(new global::Ts.LsProto.LocationLink
                {
                    OriginSelectionRange = originSelectionRange,
                    TargetSelectionRange = targetSelectionLoc,
                    TargetUri = targetUri,
                    TargetRange = targetLoc,
                });
            }
        }

        if (clientSupportsLink)
        {
            return new global::Ts.LsProto.DefinitionResponse { DefinitionLinks = locations.ToArray() };
        }
        return new global::Ts.LsProto.DefinitionResponse { Locations = SourceDefinitionDeps.CreateLocationsFromLinks(locations.ToArray()) };
    }

    // definition.go — provideDefinitionWorker. PORT-NOTE: the Go worker resolves the
    // symbol declarations through the checker and rebuilds the response; the C#
    // foundation's go-to-definition entry is the reduced GetDefinitionAtPosition
    // (Definition.cs), so the fallback maps its DefinitionLocation[] result into the
    // response shape until the full definition.go port lands.
    private global::Ts.LsProto.DefinitionResponse ProvideDefinitionWorker(string documentURI, global::Ts.LsProto.Position position, CancellationToken cancellationToken)
    {
        var file = GetProgramAndFile(documentURI);
        var pos = LsDeps.LineAndCharacterToPosition(file, position.Line, position.Character);
        var definitions = GetDefinitionAtPosition(documentURI, pos, cancellationToken);
        if (definitions.Length == 0)
        {
            return new global::Ts.LsProto.DefinitionResponse();
        }
        var locations = new global::Ts.LsProto.Location[definitions.Length];
        for (var i = 0; i < definitions.Length; i++)
        {
            var targetFile = program.GetSourceFile(definitions[i].FileName) ?? file;
            locations[i] = new global::Ts.LsProto.Location
            {
                Uri = LsDeps.FileNameToDocumentURI(definitions[i].FileName),
                Range = LsDeps.ToLspRange(targetFile, definitions[i].TextSpan),
            };
        }
        return new global::Ts.LsProto.DefinitionResponse { Locations = locations };
    }

    // sourcedefinition.go:121 — newSourceDefResolver.
    private SourceDefResolver NewSourceDefResolver(global::Ts.Compiler.Program program, string resolveFrom)
    {
        var options = program.Options();
        var noDtsOptions = options.Clone();
        noDtsOptions.NoDtsResolution = global::Ts.Core.Tristate.True;
        return new SourceDefResolver(
            this,
            program.Host().FS(),
            options,
            program.GetSourceFile,
            resolveFrom,
            new global::Ts.Module.Resolver(program.Host(), noDtsOptions, program.GetGlobalTypingsCacheLocation(), ""));
    }

    // sourcedefinition.go:177 — getSourceDefCheckerInfo. Acquires the type checker for the
    // given file and returns the definition declarations for node along with the module
    // specifier of the import that brought the symbol into scope (empty if not applicable).
    private static (List<Node> Declarations, string ModuleSpecifier) GetSourceDefCheckerInfo(
        global::Ts.Compiler.Program program,
        SourceFile file,
        Node node,
        CancellationToken cancellationToken)
    {
        var (c, done) = program.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var declarations = SourceDefinitionDeps.GetDeclarationsFromLocation(c, node);
            var isPropertyName = node.Parent != null && AstUtilities.IsAccessExpression(node.Parent!) && node.Parent!.Name() == node;
            if (declarations.Count == 0 && isPropertyName)
            {
                if (node.Parent!.Expression() is { } left)
                {
                    if (c.GetPropertyOfType(c.GetTypeAtLocation(left), node.Text()) is { } prop)
                    {
                        declarations = prop.Declarations is { } propDeclarations ? new List<Node>(propDeclarations) : [];
                    }
                }
            }
            if (SourceDefinitionDeps.TryGetSignatureDeclaration(c, node) is { } calledDeclaration)
            {
                var nonFunctionDeclarations = new List<Node>();
                foreach (var declaration in declarations)
                {
                    if (!AstUtilities.IsFunctionLike(declaration))
                    {
                        nonFunctionDeclarations.Add(declaration);
                    }
                }
                nonFunctionDeclarations.Add(calledDeclaration);
                declarations = nonFunctionDeclarations;
            }

            // Extract module specifier from the import that brought this symbol into
            // scope. For property access (obj.prop), walk up the access chain to the
            // root expression's symbol.
            var moduleSpecifier = "";
            var resolveNode = node;
            if (isPropertyName)
            {
                var expr = node.Parent!.Expression();
                while (expr != null && AstUtilities.IsAccessExpression(expr))
                {
                    expr = expr.Expression();
                }
                if (expr != null)
                {
                    resolveNode = expr;
                }
            }
            if (c.GetSymbolAtLocation(resolveNode) is { } sym)
            {
                if (sym.Declarations != null)
                {
                    foreach (var d in sym.Declarations)
                    {
                        if (!NodeTests.IsImportSpecifier(d) && !NodeTests.IsImportClause(d) && !NodeTests.IsNamespaceImport(d) && !NodeTests.IsImportEqualsDeclaration(d))
                        {
                            continue;
                        }
                        if (global::Ts.Checker.Checker.TryGetModuleSpecifierFromDeclaration(d) is { } spec)
                        {
                            moduleSpecifier = spec.Text();
                            break;
                        }
                    }
                }
            }

            return (declarations, moduleSpecifier);
        }
        finally
        {
            done();
        }
    }

    // sourcedefinition.go:291 — isDefaultImportName.
    private static bool IsDefaultImportName(Node? node)
    {
        if (node == null || node.Parent == null || !NodeTests.IsImportClause(node.Parent) || node.Parent.Name() != node || node.Parent.Parent == null)
        {
            return false;
        }
        return SourceDefinitionDeps.IsDefaultImport(node.Parent.Parent);
    }

    // sourcedefinition.go:298 — getSourceDefinitionEntryNode.
    private static Node GetSourceDefinitionEntryNode(SourceFile sourceFile)
    {
        if (sourceFile.AsNode().Statements()!.Length != 0)
        {
            return sourceFile.AsNode().Statements()![0];
        }
        return sourceFile.AsNode();
    }

    // sourcedefinition.go:305 — getSourceDefinitionEntryDeclarations.
    private static List<Node> GetSourceDefinitionEntryDeclarations(SourceFile sourceFile)
    {
        return [GetSourceDefinitionEntryNode(sourceFile)];
    }

    // sourcedefinition.go:447 — findContainingModuleSpecifier.
    private static Node? FindContainingModuleSpecifier(Node node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (AstUtilities.IsAnyImportOrReExport(current) || AstUtilities.IsRequireCall(current, true /*requireStringLiteralLikeArgument*/) || AstUtilities.IsImportCall(current))
            {
                if (SourceDefinitionDeps.GetExternalModuleName(current) is { } moduleSpecifier && AstUtilities.IsStringLiteralLike(moduleSpecifier))
                {
                    return moduleSpecifier;
                }
            }
        }
        return null;
    }

    // sourcedefinition.go:506 — getCandidateSourceDeclarationNames.
    private static List<string> GetCandidateSourceDeclarationNames(Node? originalNode, Node? declaration)
    {
        var names = new List<string>();
        if (declaration != null)
        {
            if (AstUtilities.GetNameOfDeclaration(declaration) is { } name)
            {
                if (AstUtilities.TryGetTextOfPropertyName(name, out var text) && text != "")
                {
                    names.Add(text);
                }
            }
            if (declaration.Kind == SyntaxKind.ExportAssignment)
            {
                names.Add("default");
            }
            if ((NodeTests.IsFunctionDeclaration(declaration) || NodeTests.IsClassDeclaration(declaration)) && (declaration.ModifierFlags() & ModifierFlags.ExportDefault) == ModifierFlags.ExportDefault)
            {
                names.Add("default");
            }
            if (NodeTests.IsImportSpecifier(declaration) || NodeTests.IsExportSpecifier(declaration))
            {
                if (declaration.PropertyName() is { } propName)
                {
                    names.Add(propName.Text());
                }
            }
        }
        if (originalNode != null)
        {
            if (NodeTests.IsIdentifier(originalNode) || NodeTests.IsPrivateIdentifier(originalNode))
            {
                names.Add(originalNode.Text());
            }
            if (IsDefaultImportName(originalNode))
            {
                names.Add("default");
            }
            if (originalNode.Parent != null)
            {
                if (NodeTests.IsImportSpecifier(originalNode.Parent) || NodeTests.IsExportSpecifier(originalNode.Parent))
                {
                    if (originalNode.Parent.PropertyName() is { } propName)
                    {
                        names.Add(propName.Text());
                    }
                }
            }
        }
        return names;
    }

    // sourcedefinition.go:544 — findDeclarationNodesByName.
    private static List<Node> FindDeclarationNodesByName(SourceFile sourceFile, List<string> names)
    {
        var filteredNames = Ts.Core.CoreUtil.Filter(names.ToArray(), name => name != "");
        var deduplicatedNames = Ts.Core.CoreUtil.Deduplicate(filteredNames);
        names = deduplicatedNames != null ? new List<string>(deduplicatedNames) : [];
        if (names.Count == 0)
        {
            return [];
        }

        var wanted = new global::Ts.Collections.Set<string>();
        var wantDefault = false;
        foreach (var name in names)
        {
            if (name == "default")
            {
                wantDefault = true;
                continue;
            }
            wanted.Add(name);
        }

        var candidates = new List<(Node node, int depth)>();
        var minDepth = int.MaxValue;

        bool Visit(Node? node)
        {
            var matched = false;
            if (AstUtilities.GetNameOfDeclaration(node) is { } name)
            {
                if (AstUtilities.TryGetTextOfPropertyName(name, out var text) && text != "")
                {
                    if (wanted.Has(text))
                    {
                        matched = true;
                    }
                }
            }
            if (wantDefault && node!.Kind == SyntaxKind.ExportAssignment)
            {
                matched = true;
            }
            if (wantDefault && (NodeTests.IsFunctionDeclaration(node!) || NodeTests.IsClassDeclaration(node!)) && (node!.ModifierFlags() & ModifierFlags.ExportDefault) == ModifierFlags.ExportDefault)
            {
                matched = true;
            }
            if (matched)
            {
                var depth = GetContainerDepth(node!);
                candidates.Add((node!, depth));
                if (depth < minDepth)
                {
                    minDepth = depth;
                }
            }
            return node!.ForEachChild(Visit);
        }
        sourceFile.AsNode().ForEachChild(Visit);

        // Only keep declarations at the shallowest depth, like getTopMostDeclarationNamesInFile.
        var declarations = new List<Node>();
        foreach (var candidate in candidates)
        {
            if (candidate.depth == minDepth)
            {
                declarations.Add(candidate.node);
            }
        }
        return UniqueDeclarationNodes(declarations);
    }

    // sourcedefinition.go:606 — getContainerDepth. Counts the number of container nodes above a declaration,
    // matching the behavior of getDepth in getTopMostDeclarationNamesInFile.
    private static int GetContainerDepth(Node node)
    {
        var depth = 0;
        var current = node;
        while (current != null)
        {
            current = LsMeaning.GetContainerNode(current);
            depth++;
        }
        return depth;
    }

    // sourcedefinition.go:616 — filterPreferredSourceDeclarations.
    private static List<Node> FilterPreferredSourceDeclarations(Node? originalNode, List<Node> declarations)
    {
        if (declarations.Count <= 1 || originalNode == null)
        {
            return declarations;
        }
        if (GetPropertyLikeSourceDeclarations(originalNode, declarations) is { } preferred && preferred.Count != 0)
        {
            return preferred;
        }
        var concrete = new List<Node>();
        foreach (var declaration in declarations)
        {
            if (IsConcreteSourceDeclaration(declaration))
            {
                concrete.Add(declaration);
            }
        }
        if (concrete.Count != 0)
        {
            return concrete;
        }
        return declarations;
    }

    // sourcedefinition.go:629 — getPropertyLikeSourceDeclarations.
    private static List<Node>? GetPropertyLikeSourceDeclarations(Node originalNode, List<Node> declarations)
    {
        if (originalNode.Parent == null || !AstUtilities.IsAccessExpression(originalNode.Parent) || originalNode.Parent.Name() != originalNode)
        {
            return null;
        }
        var preferred = new List<Node>();
        foreach (var node in declarations)
        {
            switch (node.Kind)
            {
                case SyntaxKind.PropertyAssignment or SyntaxKind.ShorthandPropertyAssignment or SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature or SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor or SyntaxKind.EnumMember:
                    preferred.Add(node);
                    break;
            }
        }
        return preferred;
    }

    // sourcedefinition.go:651 — hasConcreteSourceDeclarations.
    private static bool HasConcreteSourceDeclarations(List<Node> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (IsConcreteSourceDeclaration(declaration))
            {
                return true;
            }
        }
        return false;
    }

    // sourcedefinition.go:655 — isConcreteSourceDeclaration.
    private static bool IsConcreteSourceDeclaration(Node node)
    {
        if (!AstUtilities.IsDeclaration(node) || node.Kind == SyntaxKind.ExportAssignment)
        {
            return false;
        }
        if ((NodeTests.IsBinaryExpression(node) || NodeTests.IsCallExpression(node)) && AstUtilities.GetAssignmentDeclarationKind(node) != JSDeclarationKind.None)
        {
            return false;
        }
        switch (node.Kind)
        {
            case SyntaxKind.Parameter or SyntaxKind.TypeParameter or SyntaxKind.BindingElement or SyntaxKind.ImportClause or SyntaxKind.ImportSpecifier or SyntaxKind.NamespaceImport or SyntaxKind.ExportSpecifier or SyntaxKind.PropertyAccessExpression or SyntaxKind.ElementAccessExpression:
                return false;
            default:
                return true;
        }
    }

    // sourcedefinition.go:678 — uniqueDeclarationNodes.
    private static List<Node> UniqueDeclarationNodes(List<Node> nodes)
    {
        var seen = new HashSet<(string FileName, global::Ts.Core.TextRange Loc)>();
        var result = new List<Node>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node == null)
            {
                continue;
            }
            var fileName = AstUtilities.GetSourceFileOfNode(node)!.FileName();
            var key = (fileName, node.Loc);
            if (!seen.Add(key))
            {
                continue;
            }
            result.Add(node);
        }
        return result;
    }

    // sourcedefinition.go:699 — findClosestDeclarationNode.
    private static Node FindClosestDeclarationNode(SourceFile sourceFile, int pos)
    {
        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, pos);
        for (var current = node; current != null; current = current.Parent)
        {
            if (AstUtilities.IsDeclaration(current) || current.Kind == SyntaxKind.ExportAssignment)
            {
                return current;
            }
        }
        return GetSourceDefinitionEntryNode(sourceFile);
    }

    // languageservice.go — ReadFile. PORT-NOTE: the Go l.ReadFile wraps the host FS with
    // the ok-flag pair; the C# foundation Host interface exposes FileExists only, so the
    // text read goes through the program's own FS.
    private string? ReadFile(string fileName)
    {
        return program.Host().FS().TryReadFile(fileName, out var text) ? text : null;
    }

    // languageservice.go — toPath.
    private global::Ts.TsPath.Path ToPath(string fileName)
    {
        return global::Ts.TsPath.TsPath.ToPath(fileName, program.GetCurrentDirectory(), program.UseCaseSensitiveFileNames());
    }

    // sourcedefinition.go:111 — sourceDefResolver. Resolves source definitions by mapping
    // .d.ts declarations to their implementation files (.js/.ts). It uses the NoDts module
    // resolver and file parsing for resolution, but never acquires the type checker or
    // the original program; all checker-dependent work is done before results
    // are passed in.
    private sealed class SourceDefResolver(
        LanguageService ls,
        global::Ts.Vfs.IFileSystem fs,
        global::Ts.Core.CompilerOptions options,
        Func<string, SourceFile?> getSourceFile,
        string resolveFrom,
        global::Ts.Module.Resolver resolver)
    {
        public readonly LanguageService ls = ls;
        public readonly global::Ts.Vfs.IFileSystem fs = fs;
        public readonly global::Ts.Core.CompilerOptions options = options;
        public readonly Func<string, SourceFile?> getSourceFile = getSourceFile;
        public readonly string resolveFrom = resolveFrom;
        public readonly global::Ts.Module.Resolver resolver = resolver;
        public readonly Dictionary<string, SourceFile?> parsedFiles = [];

        // sourcedefinition.go:141 — resolveFromCheckerInfo. Maps type-checker declarations to source
        // implementations. It uses only the NoDts module resolver and file parsing;
        // the type checker and original request file are not needed.
        public List<Node> ResolveFromCheckerInfo(Node node, string resolvedImplFile, List<Node> checkerDeclarations, string moduleSpecifier)
        {
            // If we don't yet have a forward-resolved implementation file, try to
            // recover a module specifier from the checker (e.g. from the import that
            // brought the symbol into scope, or from the root of an access expression).
            if (resolvedImplFile == "" && moduleSpecifier != "")
            {
                resolvedImplFile = ResolveImplementation(moduleSpecifier, InferImpliedNodeFormat(resolveFrom));
            }

            // For property access where the checker found no declarations (e.g.
            // mapped types), search the implementation file for the property name.
            if (checkerDeclarations.Count == 0 && resolvedImplFile != "")
            {
                var names = GetCandidateSourceDeclarationNames(node, null);
                if (SearchImplementationFile(node, resolvedImplFile, names) is { } results)
                {
                    return UniqueDeclarationNodes(results);
                }
            }

            var declarations = new List<Node>();
            foreach (var declaration in checkerDeclarations)
            {
                declarations.AddRange(MapDeclarationToSource(node, declaration, resolvedImplFile));
            }
            declarations = UniqueDeclarationNodes(declarations);
            if (HasConcreteSourceDeclarations(declarations))
            {
                return declarations;
            }
            return [];
        }

        // sourcedefinition.go:233 — resolveTripleSlashReference. Handles /// <reference path/types="..."/> directives.
        // For path references to .js files, it returns the entry declarations directly.
        // For path references to .d.ts files or type references, it uses the NoDts
        // resolver to find the corresponding implementation file.
        public (List<Node> Declarations, Ast.FileReference? Reference) ResolveTripleSlashReference(SourceFile file, int pos, global::Ts.Compiler.Program program)
        {
            var @ref = SourceDefinitionDeps.GetReferenceAtPosition(file, pos, program);
            if (@ref == null || @ref.File == null)
            {
                return ([], null);
            }

            // If the referenced file is already an implementation file, return it directly.
            if (!@ref.File.IsDeclarationFile)
            {
                return (GetSourceDefinitionEntryDeclarations(@ref.File), @ref.Reference);
            }

            // The referenced file is a .d.ts. Try to find the implementation file
            // using the NoDts module resolver via findImplementationFileFromDtsFileName.
            var dtsFileName = @ref.File.FileName();
            var preferredMode = InferImpliedNodeFormat(dtsFileName);
            var implementationFile = FindImplementationFileFromDtsFileName(dtsFileName, preferredMode);
            if (implementationFile == "")
            {
                return ([], null);
            }

            var sourceFile = GetOrParseSourceFile(implementationFile);
            if (sourceFile == null)
            {
                return ([], null);
            }
            return (GetSourceDefinitionEntryDeclarations(sourceFile), @ref.Reference);
        }

        // sourcedefinition.go:263 — searchImplementationFile. Searches an implementation file for declarations
        // matching the given names. Returns null when no declarations matched; callers
        // fall through to the checker path or to the standard definition provider.
        public List<Node>? SearchImplementationFile(Node originalNode, string implementationFile, List<string> names)
        {
            if (implementationFile == "")
            {
                return null;
            }
            var sourceFile = GetOrParseSourceFile(implementationFile);
            if (sourceFile == null)
            {
                return null;
            }
            if (IsDefaultImportName(originalNode))
            {
                // For default imports, only search for "default" declarations to avoid
                // matching unrelated declarations with the same identifier name.
                var defaultDeclarations = FindDeclarationsInFile(implementationFile, ["default"], new global::Ts.Collections.Set<string>());
                if (defaultDeclarations.Count != 0)
                {
                    return FilterPreferredSourceDeclarations(originalNode, defaultDeclarations);
                }
                return GetSourceDefinitionEntryDeclarations(sourceFile);
            }
            var declarations = FindDeclarationsInFile(implementationFile, names, new global::Ts.Collections.Set<string>());
            if (declarations.Count != 0)
            {
                return FilterPreferredSourceDeclarations(originalNode, declarations);
            }
            return null;
        }

        // sourcedefinition.go:309 — mapDeclarationToSource.
        public List<Node> MapDeclarationToSource(Node originalNode, Node declaration, string resolvedImplFile)
        {
            var (file, startPos) = SourceDefinitionDeps.GetFileAndStartPosFromDeclaration(declaration);
            var fileName = file.FileName();

            // PORT-NOTE(tryGetSourcePosition): the source-map detour
            // (r.ls.tryGetSourcePosition(fileName, startPos) → findClosestDeclarationNode
            // on the mapped source file) is deferred with the sourcemap wave; the identity
            // position mapping stands in and the arm below keeps its Go shape.

            if (!global::Ts.TsPath.TsPath.IsDeclarationFileName(fileName))
            {
                return [declaration];
            }

            var implementationFile = resolvedImplFile;
            if (implementationFile == "")
            {
                // Reverse-resolve .d.ts path to implementation file. This path is only
                // reached for declarations with no associated module specifier (e.g.
                // globals, ambient declarations, or when forward resolution failed).
                var dtsFileName = AstUtilities.GetSourceFileOfNode(declaration)!.FileName();
                var preferredMode = InferImpliedNodeFormat(dtsFileName);
                implementationFile = FindImplementationFileFromDtsFileName(dtsFileName, preferredMode);
            }

            return SearchImplementationFile(originalNode, implementationFile, GetCandidateSourceDeclarationNames(originalNode, declaration)) ?? [];
        }

        // sourcedefinition.go:340 — findImplementationFileFromDtsFileName.
        public string FindImplementationFileFromDtsFileName(string dtsFileName, ResolutionMode preferredMode)
        {
            var jsExt = global::Ts.Module.Module.TryGetJSExtensionForFile(dtsFileName, options);
            if (jsExt != "")
            {
                var candidate = global::Ts.TsPath.TsPath.ChangeExtension(dtsFileName, jsExt);
                if (fs.FileExists(candidate))
                {
                    return candidate;
                }
            }

            var parts = global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.GetNodeModulePathParts(dtsFileName);
            if (parts == null)
            {
                return "";
            }

            // Ensure the file only contains one /node_modules/ segment. If there's more
            // than one, the package name extraction may be incorrect, so bail out.
            if (dtsFileName.LastIndexOf("/node_modules/", StringComparison.Ordinal) != parts.TopLevelNodeModulesIndex)
            {
                return "";
            }

            var packageNamePathPart = dtsFileName[(parts.TopLevelPackageNameIndex + 1)..parts.PackageRootIndex];
            var packageName = global::Ts.Module.Module.GetPackageNameFromTypesPackageName(global::Ts.Module.Module.UnmangleScopedPackageName(packageNamePathPart));
            if (packageName == "")
            {
                return "";
            }

            var pathToFileInPackage = dtsFileName[(parts.PackageRootIndex + 1)..];

            // Try resolving as a package subpath first (e.g. "pkg/dist/utils"), then
            // fall back to the bare package name (e.g. "pkg"). This covers both main
            // entrypoints and deep imports without needing to inspect package.json
            // entrypoints.
            if (pathToFileInPackage != "")
            {
                var specifier = packageName + "/" + global::Ts.TsPath.TsPath.RemoveFileExtension(pathToFileInPackage);
                if (ResolveImplementation(specifier, preferredMode) is { } subpathImplementationFile && subpathImplementationFile != "")
                {
                    return subpathImplementationFile;
                }
            }
            return ResolveImplementation(packageName, preferredMode);
        }

        // sourcedefinition.go:383 — resolveImplementation.
        public string ResolveImplementation(string moduleName, ResolutionMode preferredMode)
        {
            return ResolveImplementationFrom(moduleName, resolveFrom, preferredMode);
        }

        // sourcedefinition.go:390 — resolveImplementationFrom.
        public string ResolveImplementationFrom(string moduleName, string resolveFromFile, ResolutionMode preferredMode)
        {
            var modes = new List<ResolutionMode> { preferredMode };
            if (preferredMode != ResolutionMode.ESNext)
            {
                modes.Add(ResolutionMode.ESNext);
            }
            if (preferredMode != ResolutionMode.CommonJS)
            {
                modes.Add(ResolutionMode.CommonJS);
            }

            foreach (var mode in modes)
            {
                var (resolved, _) = resolver.ResolveModuleName(moduleName, resolveFromFile, mode, null /*redirectedReference*/);
                // Go: resolved != nil && resolved.IsResolved() — the C# ResolveModuleName
                // returns a non-nullable ResolvedModule struct; its zero value reports
                // !IsResolved(), so the nil arm dissolves into the predicate.
                if (resolved.IsResolved() && !global::Ts.TsPath.TsPath.IsDeclarationFileName(resolved.ResolvedFileName))
                {
                    return resolved.ResolvedFileName;
                }
            }
            return "";
        }

        // sourcedefinition.go:412 — getOrParseSourceFile.
        public SourceFile? GetOrParseSourceFile(string fileName)
        {
            if (getSourceFile(fileName) is { } sourceFile)
            {
                return sourceFile;
            }
            if (parsedFiles.TryGetValue(fileName, out var parsedFile))
            {
                return parsedFile;
            }
            SourceFile? parsed = null;
            if (ls.ReadFile(fileName) is { } text)
            {
                parsed = global::Ts.Parser.Parser.ParseSourceFile(
                    new Ts.Ast.SourceFileParseOptions { FileName = fileName, Path = ls.ToPath(fileName) },
                    text,
                    global::Ts.Core.CoreUtil.GetScriptKindFromFileName(fileName));
                global::Ts.Binder.Binder.BindSourceFile(parsed);
            }
            parsedFiles[fileName] = parsed;
            return parsed;
        }

        // sourcedefinition.go:437 — inferImpliedNodeFormat. Determines the module format for a source file that may not be
        // in the program, using the file extension and nearest package.json "type" field.
        public ResolutionMode InferImpliedNodeFormat(string fileName)
        {
            var packageJsonType = "";
            var scope = resolver.GetPackageScopeForPath(global::Ts.TsPath.TsPath.GetDirectoryPath(fileName));
            if (scope.Exists())
            {
                if (scope.GetContents()!.Type.GetValue(out var value))
                {
                    packageJsonType = value!;
                }
            }
            return SourceDefinitionDeps.GetImpliedNodeFormatForFile(fileName, packageJsonType);
        }

        // sourcedefinition.go:458 — findDeclarationsInFile.
        public List<Node> FindDeclarationsInFile(string fileName, List<string> names, global::Ts.Collections.Set<string> seen)
        {
            if (fileName == "" || names.Count == 0)
            {
                return [];
            }
            if (!seen.AddIfAbsent(fileName))
            {
                return [];
            }

            var sourceFile = GetOrParseSourceFile(fileName);
            if (sourceFile == null)
            {
                return [];
            }

            var declarations = FindDeclarationNodesByName(sourceFile, names);
            if (declarations.Count != 0 && HasConcreteSourceDeclarations(declarations))
            {
                return declarations;
            }

            var forwarded = new List<Node>();
            foreach (var forwardedFile in GetForwardedImplementationFiles(sourceFile))
            {
                forwarded.AddRange(FindDeclarationsInFile(forwardedFile, names, seen));
            }
            if (forwarded.Count != 0)
            {
                if (HasConcreteSourceDeclarations(forwarded))
                {
                    return UniqueDeclarationNodes(forwarded);
                }
                var combined = new List<Node>(declarations);
                combined.AddRange(forwarded);
                return UniqueDeclarationNodes(combined);
            }
            return declarations;
        }

        // sourcedefinition.go:493 — getForwardedImplementationFiles.
        public List<string> GetForwardedImplementationFiles(SourceFile sourceFile)
        {
            var preferredMode = InferImpliedNodeFormat(sourceFile.FileName());

            var files = new List<string>();
            foreach (var imp in sourceFile.Imports()!)
            {
                var moduleName = imp.Text();
                if (ResolveImplementationFrom(moduleName, sourceFile.FileName(), preferredMode) is { } implementationFile && implementationFile != "")
                {
                    files.Add(implementationFile);
                }
            }
            return Ts.Core.CoreUtil.Deduplicate(files.ToArray()) is { } deduplicated ? new List<string>(deduplicated) : [];
        }
    }
}
