// Ported from tsgo internal/transformers/declarations/transform.go.

using System;
using System.Collections.Generic;
using global::Ts.Ast;
using global::Ts.Core;
using global::Ts.Printer;

namespace Ts.Transformers.Declarations;

public sealed class ReferencedFilePair
{
    public SourceFile? File;
    public FileReference? Reference;
}

public interface IOutputPaths
{
    string DeclarationFilePath();
    string JsFilePath();
}

// Used to be passed in the TransformationContext, which is now just an EmitContext.
public interface IDeclarationEmitHost
{
    string GetCurrentDirectory();
    bool UseCaseSensitiveFileNames();
    SourceFile? GetSourceFileFromReference(SourceFile origin, FileReference reference);
    IOutputPaths GetOutputPathsFor(SourceFile file, bool forceDtsPaths);
    ResolutionMode GetResolutionModeOverride(Node node);
    ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags);
    EmitResolver GetEmitResolver();
}

// transform.go:46-62 — thisPropertyAssignmentKey.
internal readonly record struct ThisPropertyAssignmentKey(string? Name, Node? Node, bool IsStatic, bool IsPrivate)
{
    internal static ThisPropertyAssignmentKey For(Node? name, Node node, bool isStatic)
    {
        var isPrivate = name != null && NodeTests.IsPrivateIdentifier(name);
        if (name != null && !AstUtilities.IsDynamicName(name))
        {
            if (AstUtilities.TryGetTextOfPropertyName(name, out var nameText))
            {
                return new ThisPropertyAssignmentKey(nameText, null, isStatic, isPrivate);
            }
        }
        return new ThisPropertyAssignmentKey(null, node, isStatic, isPrivate);
    }
}

public sealed class DeclarationTransformer : global::Ts.Transformers.Transformer
{
    // PORT-NOTE: Breadth-first declaration emit slice. The core syntax transforms are live:
    // declaration-file conversion, body/initializer stripping, signature/type preservation and
    // primitive inference, class/interface/member filtering, and top-level declare injection.
    // The following checker-coupled transform.go branches remain explicit deferred work:
    //   * late visibility painting and alias reprocessing;
    //   * @internal comment-range stripping;
    //   * CommonJS/export-expression promotion and JS expando/this-property synthesis;
    //   * declaration/module-specifier/reference-directive path rewriting;
    //   * late-bound index signatures, computed-name checks, and complex extends expressions;
    //   * literal-const initializer preservation and the full isolatedDeclarations diagnostic matrix.
    // They are not replaced with invented checker behavior; resolver-backed calls are made only where
    // the landed EmitResolver contract can answer them faithfully.
    private static readonly global::Ts.NodeBuilder.Flags DeclarationEmitNodeBuilderFlags =
        global::Ts.NodeBuilder.Flags.MultilineObjectLiterals |
        global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral |
        global::Ts.NodeBuilder.Flags.UseTypeOfFunction |
        global::Ts.NodeBuilder.Flags.UseStructuralFallback |
        global::Ts.NodeBuilder.Flags.AllowEmptyTuple |
        global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams |
        global::Ts.NodeBuilder.Flags.NoTruncation;

    private const global::Ts.NodeBuilder.InternalFlags DeclarationEmitInternalNodeBuilderFlags =
        global::Ts.NodeBuilder.InternalFlags.AllowUnresolvedNames;

    private readonly IDeclarationEmitHost? host;
    private readonly CompilerOptions compilerOptions;
    private readonly EmitResolver? resolver;
    private readonly global::Ts.PseudoChecker.PseudoChecker pseudoChecker;
    private readonly SymbolTrackerSharedState state;
    private readonly SymbolTrackerImpl tracker;
    private readonly string declarationFilePath;
    private readonly string declarationMapPath;
    private Node? enclosingDeclaration;
    private bool needsDeclare;

    // transform.go:74-100 — remaining transformer state (populated by the 1:1 rebuild batches).
    private bool needsScopeFixMarker;
    private bool resultHasScopeMarker;
    private bool resultHasExternalModuleIndicator;
    private bool suppressNewDiagnosticContexts;
    private readonly global::Ts.Collections.Set<string> witnessedCjsExports = new();
    private Dictionary<NodeId, Node?> lateStatementReplacementMap = new();
    // store the result of transforming expando hosts so they can be inserted later if the host is actually referenced
    private Dictionary<NodeId, Node> expandoHosts = new();
    // store any found expando _members_ after transforming them so *if* the host is referenced, they can be emitted alongside it
    private Dictionary<NodeId, List<Node>> expandoMembers = new();
    // expando assignments whose host wasn't visible when collected, processed if the host is late-marked visible
    private Dictionary<NodeId, List<BinaryExpression>> deferredExpandoAssignments = new();
    private readonly global::Ts.Collections.Set<ThisPropertyAssignmentKey> seenProperties = new();
    private List<Node> thisPropertyAssignmentsCollected = new();
    private List<ReferencedFilePair> rawReferencedFiles = new();
    private List<FileReference> rawTypeReferenceDirectives = new();
    private List<FileReference> rawLibReferenceDirectives = new();
    private NodeVisitor bindingNameVisitor = null!;
    private NodeVisitor expressionVisitor = null!;
    private NodeVisitor cjsExportAssignmentVisitor = null!;
    private NodeVisitor exportStrippingVisitor = null!;
    private NodeVisitor thisPropertyVisitor = null!;
    private NodeVisitor declareStrippingVisitor = null!;

    private Node? cjsExportAssignment;
    private List<Node> cjsExportMembers = new();
    private Node? cjsExportAssignmentName; // tracks the name node used for `export =` in CJS module.exports assignments
    private bool inClassExpressionDeclaration; // true when serializing members of a class expression kept as a class declaration

    private DeclarationTransformer(
        IDeclarationEmitHost? host,
        EmitContext? context,
        CompilerOptions? compilerOptions,
        EmitResolver? resolver,
        string declarationFilePath = "",
        string declarationMapPath = "")
    {
        this.host = host;
        this.compilerOptions = compilerOptions ?? new CompilerOptions();
        this.resolver = resolver ?? host?.GetEmitResolver();
        this.declarationFilePath = declarationFilePath;
        this.declarationMapPath = declarationMapPath;
        pseudoChecker = global::Ts.PseudoChecker.PseudoChecker.NewPseudoChecker(
            this.compilerOptions.StrictNullChecks == Tristate.True,
            this.compilerOptions.ExactOptionalPropertyTypes == Tristate.True);
        state = new SymbolTrackerSharedState
        {
            IsolatedDeclarations = this.compilerOptions.IsolatedDeclarations == Tristate.True,
            StripInternal = this.compilerOptions.StripInternal == Tristate.True,
            Resolver = this.resolver,
        };
        tracker = SymbolTracker.NewSymbolTracker(host, this.resolver, state);
        // transform.go:117-130 — isolatedDeclarations expando-function error reporting callback.
        state.ReportExpandoFunctionErrors = node =>
        {
            if (!state.IsolatedDeclarations)
            {
                return;
            }
            var props = this.resolver!.GetPropertiesOfContainerFunction(node);
            foreach (var p in props)
            {
                if (global::Ts.Checker.Checker.IsExpandoPropertyDeclaration(p.ValueDeclaration))
                {
                    var errorTarget = p.ValueDeclaration!;
                    if (NodeTests.IsBinaryExpression(errorTarget))
                    {
                        errorTarget = errorTarget.AsBinaryExpression().Left!;
                    }
                    state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(errorTarget, global::Ts.Diagnostics.Diagnostics.Assigning_properties_to_functions_without_declaring_them_is_not_supported_with_isolatedDeclarations_Add_an_explicit_declaration_for_the_properties_assigned_to_this_function));
                }
            }
        };
        // REBUILD: explicit Go zero values for state the later batches write (CS0649-clean).
        needsScopeFixMarker = false;
        resultHasScopeMarker = false;
        resultHasExternalModuleIndicator = false;
        suppressNewDiagnosticContexts = false;
        cjsExportAssignment = null;
        cjsExportAssignmentName = null;
        inClassExpressionDeclaration = false;
        NewTransformer(Visit, context);
        bindingNameVisitor = EmitContext().NewNodeVisitor(VisitBindingName);
        expressionVisitor = EmitContext().NewNodeVisitor(VisitNestedExpression);
        exportStrippingVisitor = EmitContext().NewNodeVisitor(StripExportModifiers);
        thisPropertyVisitor = EmitContext().NewNodeVisitor(VisitThisPropertyAssignments);
        cjsExportAssignmentVisitor = EmitContext().NewNodeVisitor(VisitCJSExportAssignments);
        declareStrippingVisitor = EmitContext().NewNodeVisitor(StripDeclareModifiers);
    }

    // transform.go:146-148 — shouldStripInternal.
    private bool ShouldStripInternal(Node? node) =>
        state.StripInternal && node != null && IsInternalDeclaration(node, state.CurrentSourceFile!);

    // transform.go:150-201 — isInternalDeclaration.
    private bool IsInternalDeclaration(Node? node, SourceFile sourceFile)
    {
        if (node == null)
        {
            return false;
        }
        var parseTreeNode = EmitContext().MostOriginal(node)!;
        if (!global::Ts.Printer.PrinterUtilities.IsParseTreeNode(parseTreeNode))
        {
            return false;
        }
        if (parseTreeNode.Kind == SyntaxKind.Parameter)
        {
            var params_ = parseTreeNode.Parent!.Parameters()!;
            var paramIdx = Array.FindIndex(params_, p => p == parseTreeNode);
            Node? previousSibling = null;
            if (paramIdx > 0)
            {
                previousSibling = params_[paramIdx - 1];
            }

            var text = sourceFile.Text();
            var commentRanges = new List<CommentRange>();

            if (previousSibling != null)
            {
                // to handle
                // ... parameters, /** @internal */
                // public param: string
                var trailingPos = global::Ts.Scanner.Scanner.SkipTriviaEx(text, previousSibling.End + 1, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = true });
                foreach (var comment in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(Factory().AsNodeFactory(), text, trailingPos))
                {
                    commentRanges.Add(comment);
                }
                foreach (var comment in global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(Factory().AsNodeFactory(), text, node.Pos))
                {
                    commentRanges.Add(comment);
                }
            }
            else
            {
                var trailingPos = global::Ts.Scanner.Scanner.SkipTriviaEx(text, node.Pos, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = true });
                foreach (var comment in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(Factory().AsNodeFactory(), text, trailingPos))
                {
                    commentRanges.Add(comment);
                }
            }

            if (commentRanges.Count > 0)
            {
                return HasInternalAnnotation(commentRanges[^1], sourceFile);
            }
            return false;
        }

        foreach (var commentRange in GetLeadingCommentRangesOfNode(parseTreeNode, sourceFile))
        {
            if (HasInternalAnnotation(commentRange, sourceFile))
            {
                return true;
            }
        }
        return false;
    }

    // transform.go:203-208 — getLeadingCommentRangesOfNode.
    private IEnumerable<CommentRange> GetLeadingCommentRangesOfNode(Node? node, SourceFile sourceFile)
    {
        if (node == null || node.Kind == SyntaxKind.JsxText)
        {
            return [];
        }
        return global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(Factory().AsNodeFactory(), sourceFile.Text(), node.Pos);
    }

    // transform.go:210-213 — hasInternalAnnotation.
    private static bool HasInternalAnnotation(CommentRange commentRange, SourceFile sourceFile)
    {
        var comment = sourceFile.Text()[commentRange.Pos..commentRange.End];
        return comment.Contains("@internal", StringComparison.Ordinal);
    }

    // REBUILD-ANCHOR — references the state fields the 1:1 rebuild batches will use so the batch-1
    // slice builds warning-clean (CS0169 under TreatWarningsAsErrors). Removed when the rebuild wires
    // the new visitSourceFile chain.
    private bool RebuildFieldAnchor() =>
        !resultHasScopeMarker && !resultHasExternalModuleIndicator && !suppressNewDiagnosticContexts &&
        !needsScopeFixMarker && !inClassExpressionDeclaration && cjsExportAssignment == null &&
        cjsExportAssignmentName == null && cjsExportMembers.Count == 0 &&
        lateStatementReplacementMap.Count == 0 && expandoHosts.Count == 0 && expandoMembers.Count == 0 &&
        deferredExpandoAssignments.Count == 0 && thisPropertyAssignmentsCollected.Count == 0 &&
        rawReferencedFiles.Count == 0 && rawTypeReferenceDirectives.Count == 0 &&
        rawLibReferenceDirectives.Count == 0 && global::Ts.Collections.SetExtensions.Len(witnessedCjsExports) == 0 &&
        global::Ts.Collections.SetExtensions.Len(seenProperties) == 0 && bindingNameVisitor != null &&
        expressionVisitor != null && cjsExportAssignmentVisitor != null && exportStrippingVisitor != null &&
        thisPropertyVisitor != null && declareStrippingVisitor != null && declarationMapPath != null &&
        !ShouldStripInternal(null) && ThrowDiagnostic(default!) == null;

    // transform.go:311-316 — collectFileReferences.
    private void CollectFileReferences(SourceFile sourceFile)
    {
        foreach (var @ref in sourceFile.ReferencedFiles ?? [])
        {
            rawReferencedFiles.Add(new ReferencedFilePair { File = sourceFile, Reference = @ref });
        }
        rawTypeReferenceDirectives.AddRange(sourceFile.TypeReferenceDirectives ?? []);
        rawLibReferenceDirectives.AddRange(sourceFile.LibReferenceDirectives ?? []);
    }

    // transform.go:317-322 — nodeOrSyntaxListChildren.
    private static Node[] NodeOrSyntaxListChildren(Node node)
    {
        if (NodeTests.IsSyntaxList(node))
        {
            return node.AsSyntaxList().Children ?? [];
        }
        return [node];
    }

    // transform.go:323-325 — flattenSyntaxLists.
    private static Node[] FlattenSyntaxLists(Node[] nodes) =>
        CoreUtil.FlatMap(nodes, NodeOrSyntaxListChildren) ?? [];

    // transform.go:327-340 — appendCjsExports.
    private NodeList AppendCjsExports(NodeList combinedStatements)
    {
        var result = new List<Node>();
        if (cjsExportAssignment != null)
        {
            result.Add(cjsExportAssignment);
        }
        result.AddRange(cjsExportMembers);
        result.AddRange(combinedStatements.Nodes);
        var statementNodes = FlattenSyntaxLists(result.ToArray());
        if (statementNodes.Length != combinedStatements.Nodes.Length)
        {
            combinedStatements = Factory().AsNodeFactory().NewNodeList(statementNodes);
        }
        return combinedStatements;
    }

    // transform.go:382-384 — createEmptyExports.
    private static Node CreateEmptyExports(global::Ts.Ast.NodeFactory factory) =>
        factory.NewExportDeclaration(null /*modifiers*/, false /*isTypeOnly*/, factory.NewNamedExports(factory.NewNodeList([])), null, null);

    // transform.go:386-462 — transformAndReplaceLatePaintedStatements.
    // This is a `while` loop because `handleSymbolAccessibilityError` can see additional import aliases
    // marked as visible during error handling which must now be included in the output and themselves
    // checked for errors. For example:
    // ```
    // module A {
    //   export module Q {}
    //   import B = Q;
    //   import C = B;
    //   export import D = C;
    // }
    // ```
    // In such a scenario, only Q and D are initially visible, but we don't consider imports as private
    // names - instead we say they if they are referenced they must be recorded. So while checking D's
    // visibility we mark C as visible, then we must check C which in turn marks B, completing the chain
    // of dependent imports and allowing a valid declaration file output. Today, this dependent alias
    // marking only happens for internal import aliases.
    private NodeList TransformAndReplaceLatePaintedStatements(NodeList statements)
    {
        while (true)
        {
            if (state.LateMarkedStatements.Count == 0)
            {
                break;
            }

            var next = state.LateMarkedStatements[0];
            state.LateMarkedStatements.RemoveAt(0);

            var saveNeedsDeclare = needsDeclare;
            needsDeclare = next.Parent != null && NodeTests.IsSourceFile(next.Parent);

            var result = TransformTopLevelDeclaration(next);

            needsDeclare = saveNeedsDeclare;
            var original = EmitContext().MostOriginal(next)!;
            var id = AstUtilities.GetNodeId(original);
            lateStatementReplacementMap[id] = result;
        }

        // And lastly, we need to get the final form of all those indetermine import declarations from
        // before and add them to the output list (and remove them from the set to examine for outter
        // declarations)
        var results = new List<Node>(statements.Nodes.Length);
        foreach (var statement in statements.Nodes)
        {
            if (!global::Ts.Checker.Checker.IsLateVisibilityPaintedStatement(statement))
            {
                results.Add(statement);
                continue;
            }
            var original = EmitContext().MostOriginal(statement)!;
            var id = AstUtilities.GetNodeId(original);
            if (!lateStatementReplacementMap.TryGetValue(id, out var replacement))
            {
                results.Add(statement);
                continue; // not replaced
            }
            if (replacement == null)
            {
                continue; // deleted
            }
            if (replacement.Kind == SyntaxKind.SyntaxList)
            {
                if (!needsScopeFixMarker || !resultHasExternalModuleIndicator)
                {
                    foreach (var elem in replacement.AsSyntaxList().Children ?? [])
                    {
                        if (DeclarationUtilities.NeedsScopeMarker(elem))
                        {
                            needsScopeFixMarker = true;
                        }
                        if (NodeTests.IsSourceFile(statement.Parent!) && AstUtilities.IsExternalModuleIndicator(elem))
                        {
                            resultHasExternalModuleIndicator = true;
                        }
                    }
                }
                results.AddRange(replacement.AsSyntaxList().Children ?? []);
            }
            else
            {
                if (DeclarationUtilities.NeedsScopeMarker(replacement))
                {
                    needsScopeFixMarker = true;
                }
                if (NodeTests.IsSourceFile(statement.Parent!) && AstUtilities.IsExternalModuleIndicator(replacement))
                {
                    resultHasExternalModuleIndicator = true;
                }
                results.Add(replacement);
            }
        }

        return Factory().AsNodeFactory().NewNodeList(results.ToArray());
    }

    // transform.go:464-518 — getReferencedFiles: handle path rewrites for triple slash ref comments.
    private FileReference[] GetReferencedFiles(string outputFilePath)
    {
        var results = new List<FileReference>();
        foreach (var pair in rawReferencedFiles)
        {
            var sourceFile = pair.File!;
            var @ref = pair.Reference!.Value;

            if (!@ref.Preserve)
            {
                continue;
            }

            var file = host!.GetSourceFileFromReference(sourceFile, @ref)!;
            if (file == null)
            {
                continue;
            }

            string declFileName;
            if (file.IsDeclarationFile)
            {
                declFileName = file.FileName();
            }
            else
            {
                var paths = host.GetOutputPathsFor(file, true);
                // Try to use output path for referenced file, or output js path if that doesn't exist, or the input path if all else fails
                declFileName = paths.DeclarationFilePath();
                if (declFileName.Length == 0)
                {
                    declFileName = paths.JsFilePath();
                }
                if (declFileName.Length == 0)
                {
                    declFileName = file.FileName();
                }
            }
            // Should only be missing if the source file is missing a fileName (at which point we can't name a reference to it anyway)
            // TODO: Shouldn't this be a crash or assert instead of a silent continue?
            if (declFileName.Length == 0)
            {
                continue;
            }

            var fileName = global::Ts.TsPath.TsPath.GetRelativePathToDirectoryOrUrl(
                outputFilePath,
                declFileName,
                false, // TODO: Probably unsafe to assume this isn't a URL, but that's what strada does
                new global::Ts.TsPath.ComparePathsOptions
                {
                    CurrentDirectory = host.GetCurrentDirectory(),
                    UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                });

            results.Add(new FileReference
            {
                TextRange = new TextRange(-1, -1),
                FileName = fileName,
                ResolutionMode = @ref.ResolutionMode,
                Preserve = @ref.Preserve,
            });
        }
        return results.ToArray();
    }

    // transform.go:519-533 — getLibReferences: clone retained references.
    private FileReference[] GetLibReferences()
    {
        var result = new List<FileReference>();
        foreach (var @ref in rawLibReferenceDirectives)
        {
            if (!@ref.Preserve)
            {
                continue;
            }
            result.Add(new FileReference
            {
                TextRange = new TextRange(-1, -1),
                FileName = @ref.FileName,
                ResolutionMode = @ref.ResolutionMode,
                Preserve = @ref.Preserve,
            });
        }
        return result.ToArray();
    }

    // transform.go:535-549 — getTypeReferences: clone retained references.
    private FileReference[] GetTypeReferences()
    {
        var result = new List<FileReference>();
        foreach (var @ref in rawTypeReferenceDirectives)
        {
            if (!@ref.Preserve)
            {
                continue;
            }
            result.Add(new FileReference
            {
                TextRange = new TextRange(-1, -1),
                FileName = @ref.FileName,
                ResolutionMode = @ref.ResolutionMode,
                Preserve = @ref.Preserve,
            });
        }
        return result.ToArray();
    }

    // transform.go:551-571 — setupDiagnosticContext. Returns (canProduceDiagnostic, restore).
    private (bool canProduceDiagnostic, Action restore) SetupDiagnosticContext(Node input)
    {
        var canProdiceDiagnostic = DeclarationUtilities.CanProduceDiagnostics(input);
        var oldWithinObjectLiteralType = suppressNewDiagnosticContexts;
        var shouldEnterSuppressNewDiagnosticsContextContext = (input.Kind == SyntaxKind.TypeLiteral || input.Kind == SyntaxKind.MappedType) && !(input.Parent!.Kind == SyntaxKind.TypeAliasDeclaration || input.Parent.Kind == SyntaxKind.JSTypeAliasDeclaration);

        var oldDiag = state.GetSymbolAccessibilityDiagnostic;
        if (canProdiceDiagnostic && !suppressNewDiagnosticContexts)
        {
            state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(input);
        }
        var oldName = state.ErrorNameNode;

        if (shouldEnterSuppressNewDiagnosticsContextContext)
        {
            suppressNewDiagnosticContexts = true;
        }

        return (canProdiceDiagnostic, () =>
        {
            state.GetSymbolAccessibilityDiagnostic = oldDiag;
            state.ErrorNameNode = oldName;
            suppressNewDiagnosticContexts = oldWithinObjectLiteralType;
        });
    }

    // ── rebuild forward stub: replaced when visitDeclarationStatements batch lands ──
    private Node? TransformTopLevelDeclaration(Node input) => throw new NotImplementedException("declarations rebuild");

    // transform.go:276-278 — throwDiagnostic.
    private static SymbolAccessibilityDiagnostic ThrowDiagnostic(global::Ts.Printer.SymbolAccessibilityResult result) =>
        throw new InvalidOperationException("Diagnostic emitted without context");

    // ── 1:1 rebuild forward stubs (later batches replace these; unreachable until the new
    // visitSourceFile chain is wired) ──
    private Node? VisitBindingName(Node node) => throw new NotImplementedException("declarations rebuild");
    private Node? VisitNestedExpression(Node node) => throw new NotImplementedException("declarations rebuild");
    private Node? StripExportModifiers(Node node) => throw new NotImplementedException("declarations rebuild");
    private Node? VisitThisPropertyAssignments(Node node) => throw new NotImplementedException("declarations rebuild");
    private Node? VisitCJSExportAssignments(Node node) => throw new NotImplementedException("declarations rebuild");
    private Node? StripDeclareModifiers(Node node) => throw new NotImplementedException("declarations rebuild");

    // TODO: Convert to TransformerFactory signature to allow more automatic composition with other transforms.
    public static DeclarationTransformer NewDeclarationTransformer(
        IDeclarationEmitHost host,
        EmitContext? context,
        CompilerOptions? compilerOptions,
        string declarationFilePath,
        string declarationMapPath)
    {
        return new DeclarationTransformer(host, context, compilerOptions, host.GetEmitResolver(), declarationFilePath, declarationMapPath);
    }

    // Local composition adapter used while the compiler's declaration host wiring is still landing.
    public static DeclarationTransformer NewDeclarationTransformer(global::Ts.Transformers.TransformOptions options) =>
        new(null, options.Context, options.CompilerOptions, options.EmitResolver);

    public Diagnostic[] GetDiagnostics() => state.Diagnostics.ToArray();

    private Node? Visit(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                return VisitSourceFile(node.AsSourceFile());

            case SyntaxKind.FunctionDeclaration:
                return TransformFunctionDeclaration(node.AsFunctionDeclaration());
            case SyntaxKind.ClassDeclaration:
                return TransformClassDeclaration(node.AsClassDeclaration());
            case SyntaxKind.InterfaceDeclaration:
                return TransformInterfaceDeclaration(node.AsInterfaceDeclaration());
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return TransformTypeAliasDeclaration(node.AsTypeAliasDeclaration());
            case SyntaxKind.VariableStatement:
                return TransformVariableStatement(node.AsVariableStatement());
            case SyntaxKind.EnumDeclaration:
                return TransformEnumDeclaration(node.AsEnumDeclaration());
            case SyntaxKind.ModuleDeclaration:
                return TransformModuleDeclaration(node.AsModuleDeclaration());

            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportDeclaration:
                return TransformModuleIndicator(node);
            case SyntaxKind.ExportAssignment:
                return TransformExportAssignment(node.AsExportAssignment());

            case SyntaxKind.VariableDeclaration:
                return TransformVariableDeclaration(node.AsVariableDeclaration());
            case SyntaxKind.Parameter:
                return EnsureParameter(node.AsParameterDeclaration());
            case SyntaxKind.PropertyDeclaration:
                return TransformPropertyDeclaration(node.AsPropertyDeclaration());
            case SyntaxKind.PropertySignature:
                return TransformPropertySignatureDeclaration(node.AsPropertySignatureDeclaration());
            case SyntaxKind.MethodDeclaration:
                return TransformMethodDeclaration(node.AsMethodDeclaration());
            case SyntaxKind.MethodSignature:
                return TransformMethodSignatureDeclaration(node.AsMethodSignatureDeclaration());
            case SyntaxKind.Constructor:
                return TransformConstructorDeclaration(node.AsConstructorDeclaration());
            case SyntaxKind.GetAccessor:
                return TransformGetAccessorDeclaration(node.AsGetAccessorDeclaration());
            case SyntaxKind.SetAccessor:
                return TransformSetAccessorDeclaration(node.AsSetAccessorDeclaration());
            case SyntaxKind.CallSignature:
                return TransformCallSignatureDeclaration(node.AsCallSignatureDeclaration());
            case SyntaxKind.ConstructSignature:
                return TransformConstructSignatureDeclaration(node.AsConstructSignatureDeclaration());
            case SyntaxKind.IndexSignature:
                return TransformIndexSignatureDeclaration(node.AsIndexSignatureDeclaration());
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.SemicolonClassElement:
                return null;

            case SyntaxKind.BreakStatement:
            case SyntaxKind.ContinueStatement:
            case SyntaxKind.DebuggerStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.EmptyStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.IfStatement:
            case SyntaxKind.LabeledStatement:
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.TryStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.NotEmittedStatement:
            case SyntaxKind.Block:
            case SyntaxKind.MissingDeclaration:
            case SyntaxKind.ExpressionStatement:
                return null;

            default:
                return Visitor().VisitEachChild(node);
        }
    }

    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }

        needsDeclare = true;
        enclosingDeclaration = node.AsNode();
        state.CurrentSourceFile = node;
        resolver?.PrecalculateDeclarationEmitVisibility(EmitContext().MostOriginal(node.AsNode())!.AsSourceFile());

        var statements = Visitor().VisitNodes(node.Statements) ?? Factory().NewNodeList([]);
        var result = Factory().AsNodeFactory().UpdateSourceFile(node, statements, node.EndOfFileToken);
        result.AsSourceFile().IsDeclarationFile = true;

        state.CurrentSourceFile = null;
        enclosingDeclaration = null;
        return result;
    }

    private Node? TransformModuleIndicator(Node node)
    {
        if (resolver is not null &&
            node.Kind is SyntaxKind.ImportEqualsDeclaration or SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration &&
            !resolver.IsDeclarationVisible(node))
        {
            return null;
        }
        return Visitor().VisitEachChild(node);
    }

    private Node? TransformExportAssignment(ExportAssignment input)
    {
        var expression = input.Expression;
        if (expression?.Kind == SyntaxKind.Identifier)
        {
            return Factory().AsNodeFactory().UpdateExportAssignment(
                input,
                input.Modifiers(),
                input.IsExportEquals,
                null,
                expression);
        }

        // PORT-NOTE: transform.go promotes non-identifier export assignments to a synthetic typed
        // declaration. That path requires checker-backed CreateTypeOfExpression and generated-name
        // visibility. Keep it explicitly deferred instead of emitting an invalid .d.ts expression.
        if (state.IsolatedDeclarations && expression is not null)
        {
            tracker.ReportInferenceFallback(expression);
        }
        return null;
    }

    private Node TransformTypeAliasDeclaration(TypeAliasDeclaration input)
    {
        var oldNeedsDeclare = needsDeclare;
        needsDeclare = false;
        var result = Factory().AsNodeFactory().UpdateTypeAliasDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNode(input.Type));
        needsDeclare = oldNeedsDeclare;
        return result;
    }

    private Node TransformInterfaceDeclaration(InterfaceDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        var result = Factory().AsNodeFactory().UpdateInterfaceDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNodes(input.HeritageClauses),
            Visitor().VisitNodes(input.Members));
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformFunctionDeclaration(FunctionDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        var result = Factory().AsNodeFactory().UpdateFunctionDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null,
            null);
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformClassDeclaration(ClassDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        tracker.PushErrorFallbackNode(input.AsNode());

        var members = Visitor().VisitNodes(input.Members) ?? Factory().NewNodeList([]);
        var result = Factory().AsNodeFactory().UpdateClassDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNodes(input.HeritageClauses),
            members);

        tracker.PopErrorFallbackNode();
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformModuleDeclaration(ModuleDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        var oldNeedsDeclare = needsDeclare;
        enclosingDeclaration = input.AsNode();
        needsDeclare = false;
        var result = Factory().AsNodeFactory().UpdateModuleDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Keyword,
            input.Name(),
            Visitor().VisitNode(input.Body));
        needsDeclare = oldNeedsDeclare;
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformVariableStatement(VariableStatement input)
    {
        var declarations = input.DeclarationList?.AsVariableDeclarationList().Declarations;
        var visitedDeclarations = Visitor().VisitNodes(declarations) ?? Factory().NewNodeList([]);
        var declarationList = Factory().AsNodeFactory().UpdateVariableDeclarationList(
            input.DeclarationList!.AsVariableDeclarationList(),
            visitedDeclarations,
            input.DeclarationList.Flags);
        return Factory().AsNodeFactory().UpdateVariableStatement(
            input,
            EnsureModifiers(input.AsNode()),
            declarationList);
    }

    private Node TransformVariableDeclaration(VariableDeclaration input) =>
        Factory().AsNodeFactory().UpdateVariableDeclaration(
            input,
            input.Name(),
            null,
            EnsureType(input.AsNode(), false),
            null);

    private Node TransformEnumDeclaration(EnumDeclaration input) =>
        Factory().AsNodeFactory().UpdateEnumDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.Members));

    private Node? TransformPropertyDeclaration(PropertyDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        var postfixToken = input.PostfixToken?.Kind == SyntaxKind.ExclamationToken
            ? null
            : input.PostfixToken;
        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            postfixToken,
            EnsureType(input.AsNode(), false),
            null);
    }

    private Node? TransformPropertySignatureDeclaration(PropertySignatureDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdatePropertySignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            input.PostfixToken,
            EnsureType(input.AsNode(), false),
            null);
    }

    private Node? TransformMethodDeclaration(MethodDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        if (GetEffectiveDeclarationFlags(input.AsNode(), ModifierFlags.Private) != 0)
        {
            return Factory().AsNodeFactory().NewPropertyDeclaration(
                EnsureModifiers(input.AsNode()), input.Name(), null, null, null);
        }
        return Factory().AsNodeFactory().UpdateMethodDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            input.Name(),
            input.PostfixToken,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null,
            null);
    }

    private Node? TransformMethodSignatureDeclaration(MethodSignatureDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        if (GetEffectiveDeclarationFlags(input.AsNode(), ModifierFlags.Private) != 0)
        {
            return Factory().AsNodeFactory().NewPropertyDeclaration(
                EnsureModifiers(input.AsNode()), input.Name(), null, null, null);
        }
        return Factory().AsNodeFactory().UpdateMethodSignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            input.PostfixToken,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));
    }

    private Node TransformConstructorDeclaration(ConstructorDeclaration input) =>
        Factory().AsNodeFactory().UpdateConstructorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            UpdateParameterList(input.AsNode(), input.Parameters),
            null,
            null,
            null);

    private Node? TransformGetAccessorDeclaration(GetAccessorDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            null,
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null,
            null);
    }

    private Node? TransformSetAccessorDeclaration(SetAccessorDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            null,
            UpdateParameterList(input.AsNode(), input.Parameters),
            null,
            null,
            null);
    }

    private Node TransformCallSignatureDeclaration(CallSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateCallSignatureDeclaration(
            input,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    private Node TransformConstructSignatureDeclaration(ConstructSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateConstructSignatureDeclaration(
            input,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    private Node TransformIndexSignatureDeclaration(IndexSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateIndexSignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    private ParameterList UpdateParameterList(Node owner, ParameterList? parameters)
    {
        if (GetEffectiveDeclarationFlags(owner, ModifierFlags.Private) != 0 || parameters is null)
        {
            return Factory().NewNodeList([]);
        }
        var results = new List<Node>(parameters.Nodes.Length);
        foreach (var parameter in parameters.Nodes)
        {
            results.Add(EnsureParameter(parameter.AsParameterDeclaration()));
        }
        return Factory().NewNodeList(results.ToArray());
    }

    private Node EnsureParameter(ParameterDeclaration input)
    {
        var questionToken = input.QuestionToken;
        if (resolver?.IsOptionalParameter(input.AsNode()) == true && questionToken is null)
        {
            questionToken = Factory().NewToken(SyntaxKind.QuestionToken);
        }
        return Factory().AsNodeFactory().UpdateParameterDeclaration(
            input,
            null,
            input.DotDotDotToken,
            input.Name(),
            questionToken,
            EnsureType(input.AsNode(), true),
            null);
    }

    private Node EnsureType(Node node, bool ignorePrivate)
    {
        if (!ignorePrivate && GetEffectiveDeclarationFlags(node, ModifierFlags.Private) != 0)
        {
            return null!;
        }
        if (node.Type() is { } annotation)
        {
            return Visitor().VisitNode(annotation)!;
        }

        global::Ts.PseudoChecker.PseudoType pseudoType;
        if (global::Ts.Ast.AstUtilities.IsFunctionLike(node))
        {
            pseudoType = pseudoChecker.GetReturnTypeOfSignature(node);
        }
        else
        {
            pseudoType = pseudoChecker.GetTypeOfDeclaration(node);
        }
        return PseudoTypeToTypeNode(pseudoType);
    }

    private Node PseudoTypeToTypeNode(global::Ts.PseudoChecker.PseudoType type)
    {
        switch (type.Kind)
        {
            case global::Ts.PseudoChecker.PseudoTypeKind.Direct:
                return Visitor().VisitNode(type.AsPseudoTypeDirect().TypeNode)!;
            case global::Ts.PseudoChecker.PseudoTypeKind.MaybeConstLocation:
                return PseudoTypeToTypeNode(type.AsPseudoTypeMaybeConstLocation().RegularType);
            case global::Ts.PseudoChecker.PseudoTypeKind.Union:
            {
                var types = type.AsPseudoTypeUnion().Types;
                var nodes = new Node[types.Length];
                for (var i = 0; i < types.Length; i++)
                {
                    nodes[i] = PseudoTypeToTypeNode(types[i]);
                }
                return Factory().AsNodeFactory().NewUnionTypeNode(Factory().NewNodeList(nodes));
            }
            case global::Ts.PseudoChecker.PseudoTypeKind.String:
            case global::Ts.PseudoChecker.PseudoTypeKind.StringLiteral:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.StringKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Number:
            case global::Ts.PseudoChecker.PseudoTypeKind.NumericLiteral:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.NumberKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.BigInt:
            case global::Ts.PseudoChecker.PseudoTypeKind.BigIntLiteral:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.BigIntKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Boolean:
            case global::Ts.PseudoChecker.PseudoTypeKind.False:
            case global::Ts.PseudoChecker.PseudoTypeKind.True:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.BooleanKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Undefined:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.UndefinedKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Null:
                return Factory().AsNodeFactory().NewLiteralTypeNode(
                    Factory().NewKeywordExpression(SyntaxKind.NullKeyword));
            case global::Ts.PseudoChecker.PseudoTypeKind.Any:
            case global::Ts.PseudoChecker.PseudoTypeKind.Inferred:
            case global::Ts.PseudoChecker.PseudoTypeKind.NoResult:
            case global::Ts.PseudoChecker.PseudoTypeKind.SingleCallSignature:
            case global::Ts.PseudoChecker.PseudoTypeKind.Tuple:
            case global::Ts.PseudoChecker.PseudoTypeKind.ObjectLiteral:
            default:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
    }

    private ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags) =>
        host?.GetEffectiveDeclarationFlags(EmitContext().ParseNode(node) ?? node, flags) ??
        (global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(node) & flags);

    private ModifierList? EnsureModifiers(Node node)
    {
        var flags = global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(node) & ModifierFlags.All;
        flags &= ~(ModifierFlags.Public | ModifierFlags.Async | ModifierFlags.Override | ModifierFlags.Decorator);
        if (node.Parent?.Kind == SyntaxKind.SourceFile && needsDeclare &&
            !DeclarationUtilities.IsAlwaysType(node) &&
            node.Kind != SyntaxKind.TypeAliasDeclaration &&
            node.Kind != SyntaxKind.JSTypeAliasDeclaration &&
            (flags & ModifierFlags.Default) == 0)
        {
            flags |= ModifierFlags.Ambient;
        }
        else if (node.Parent?.Kind != SyntaxKind.SourceFile)
        {
            flags &= ~ModifierFlags.Ambient;
        }

        var modifiers = new List<Node>();
        AddModifierIf(flags, ModifierFlags.Export, SyntaxKind.ExportKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Default, SyntaxKind.DefaultKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Ambient, SyntaxKind.DeclareKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Private, SyntaxKind.PrivateKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Protected, SyntaxKind.ProtectedKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Static, SyntaxKind.StaticKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Readonly, SyntaxKind.ReadonlyKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Abstract, SyntaxKind.AbstractKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Accessor, SyntaxKind.AccessorKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.In, SyntaxKind.InKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Out, SyntaxKind.OutKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Const, SyntaxKind.ConstKeyword, modifiers);
        return modifiers.Count == 0 ? null : Factory().AsNodeFactory().NewModifierList(modifiers.ToArray());
    }

    private void AddModifierIf(
        ModifierFlags flags,
        ModifierFlags flag,
        SyntaxKind kind,
        List<Node> modifiers)
    {
        if ((flags & flag) != 0)
        {
            modifiers.Add(Factory().AsNodeFactory().NewModifier(kind));
        }
    }

    // These constants document the exact node-builder contract consumed by the deferred resolver path.
    internal static global::Ts.NodeBuilder.Flags NodeBuilderFlags => DeclarationEmitNodeBuilderFlags;
    internal static global::Ts.NodeBuilder.InternalFlags InternalNodeBuilderFlags =>
        DeclarationEmitInternalNodeBuilderFlags;
}
