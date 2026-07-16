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

    // transform.go:1674-1753 — transformTopLevelDeclaration: transforms the direct child of a source
    // file into zero or more replacement statements.
    private Node? TransformTopLevelDeclaration(Node input)
    {
        if (state.LateMarkedStatements.Count > 0)
        {
            // Remove duplicates of the current statement from the deferred work queue (this was done via orderedRemoveItem in strada - why? to ensure the same backing array? microop?)
            state.LateMarkedStatements.RemoveAll(node => node == input);
        }
        if (ShouldStripInternal(input))
        {
            return null;
        }
        if (input.Kind == SyntaxKind.ImportEqualsDeclaration)
        {
            return TransformImportEqualsDeclaration(input.AsImportEqualsDeclaration());
        }
        if (input.Kind == SyntaxKind.ImportDeclaration || input.Kind == SyntaxKind.JSImportDeclaration)
        {
            var res = TransformImportDeclaration(input.AsImportDeclaration());
            if (res != null && res.Kind != SyntaxKind.ImportDeclaration)
            {
                var clone = res.Clone(Factory())!;
                clone.Kind = SyntaxKind.ImportDeclaration;
                return clone;
            }
            return res;
        }
        if (AstUtilities.IsDeclaration(input) && DeclarationUtilities.IsDeclarationAndNotVisible(EmitContext(), resolver!, input))
        {
            return null;
        }

        // !!! TODO: JSDoc support
        // if (isJSDocImportTag(input)) return;

        // Elide implementation signatures from overload sets
        if (AstUtilities.IsFunctionLike(input) && resolver!.IsImplementationOfOverload(input))
        {
            return null;
        }
        var original = EmitContext().MostOriginal(input)!;
        var id = AstUtilities.GetNodeId(original);
        var isExpandoHost = expandoHosts.ContainsKey(id);
        var hasDeferredExpandoAssignments = deferredExpandoAssignments.ContainsKey(id);
        if (isExpandoHost || hasDeferredExpandoAssignments)
        {
            return CreateFullExpandoBlock(id);
        }

        var previousEnclosingDeclaration = enclosingDeclaration;
        if (DeclarationUtilities.IsEnclosingDeclaration(input))
        {
            enclosingDeclaration = input;
        }

        var canProdiceDiagnostic = DeclarationUtilities.CanProduceDiagnostics(input);
        var oldDiag = state.GetSymbolAccessibilityDiagnostic;
        var oldName = state.ErrorNameNode;
        if (canProdiceDiagnostic)
        {
            state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(input);
        }
        var saveNeedsDeclare = needsDeclare;

        Node? result;
        switch (input.Kind)
        {
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                result = TransformTypeAliasDeclaration(input.AsTypeAliasDeclaration());
                break;
            case SyntaxKind.InterfaceDeclaration:
                result = TransformInterfaceDeclaration(input.AsInterfaceDeclaration());
                break;
            case SyntaxKind.FunctionDeclaration:
                result = TransformFunctionDeclaration(input.AsFunctionDeclaration());
                break;
            case SyntaxKind.ModuleDeclaration:
                result = TransformModuleDeclaration(input.AsModuleDeclaration());
                break;
            case SyntaxKind.ClassDeclaration:
                result = TransformClassDeclaration(input.AsClassDeclaration());
                break;
            case SyntaxKind.VariableStatement:
                result = TransformVariableStatement(input.AsVariableStatement());
                break;
            case SyntaxKind.EnumDeclaration:
                result = TransformEnumDeclaration(input.AsEnumDeclaration());
                break;
            default:
                // Anything left unhandled is an error, so this should be unreachable
                throw new InvalidOperationException($"Unhandled top-level node in declaration emit: {input.Kind}");
        }

        enclosingDeclaration = previousEnclosingDeclaration;
        state.GetSymbolAccessibilityDiagnostic = oldDiag;
        needsDeclare = saveNeedsDeclare;
        state.ErrorNameNode = oldName;
        return result;
    }

    // transform.go:2419-2440 — transformImportEqualsDeclaration.
    private Node? TransformImportEqualsDeclaration(ImportEqualsDeclaration decl)
    {
        if (!resolver!.IsDeclarationVisible(decl.AsNode()))
        {
            return null;
        }
        if (decl.ModuleReference!.Kind == SyntaxKind.ExternalModuleReference)
        {
            // Rewrite external module names if necessary
            var specifier = decl.ModuleReference.Expression();
            return Factory().AsNodeFactory().UpdateImportEqualsDeclaration(
                decl,
                decl.Modifiers(),
                decl.IsTypeOnly,
                decl.Name(),
                Factory().AsNodeFactory().UpdateExternalModuleReference(decl.ModuleReference.AsExternalModuleReference(), RewriteModuleSpecifier(decl.AsNode(), specifier)));
        }
        else
        {
            var oldDiag = state.GetSymbolAccessibilityDiagnostic;
            state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(decl.AsNode());
            CheckEntityNameVisibility(decl.ModuleReference, enclosingDeclaration!);
            state.GetSymbolAccessibilityDiagnostic = oldDiag;
            return decl.AsNode();
        }
    }

    // transform.go:2442-2546 — transformImportDeclaration.
    private Node? TransformImportDeclaration(ImportDeclaration decl)
    {
        if (decl.ImportClause == null)
        {
            // import "mod" - possibly needed for side effects? (global interface patches, module augmentations, etc)
            return Factory().AsNodeFactory().UpdateImportDeclaration(
                decl,
                decl.Modifiers(),
                decl.ImportClause,
                RewriteModuleSpecifier(decl.AsNode(), decl.ModuleSpecifier),
                TryGetResolutionModeOverride(decl.Attributes));
        }
        var phaseModifier = decl.ImportClause.AsImportClause().PhaseModifier;
        if (phaseModifier == SyntaxKind.DeferKeyword)
        {
            phaseModifier = SyntaxKind.Unknown;
        }
        // The `importClause` visibility corresponds to the default's visibility.
        Node? visibleDefaultBinding = null;
        if (decl.ImportClause != null && decl.ImportClause.Name() != null && resolver!.IsDeclarationVisible(decl.ImportClause))
        {
            visibleDefaultBinding = decl.ImportClause.Name();
        }
        if (decl.ImportClause!.AsImportClause().NamedBindings == null)
        {
            // No named bindings (either namespace or list), meaning the import is just default or should be elided
            if (visibleDefaultBinding == null)
            {
                return null;
            }
            return Factory().AsNodeFactory().UpdateImportDeclaration(
                decl,
                decl.Modifiers(),
                Factory().AsNodeFactory().UpdateImportClause(
                    decl.ImportClause.AsImportClause(),
                    phaseModifier,
                    visibleDefaultBinding,
                    /*namedBindings*/ null),
                RewriteModuleSpecifier(decl.AsNode(), decl.ModuleSpecifier),
                TryGetResolutionModeOverride(decl.Attributes));
        }
        if (decl.ImportClause.AsImportClause().NamedBindings!.Kind == SyntaxKind.NamespaceImport)
        {
            // Namespace import (optionally with visible default)
            Node? namedBindings = null;
            if (resolver!.IsDeclarationVisible(decl.ImportClause.AsImportClause().NamedBindings!))
            {
                namedBindings = decl.ImportClause.AsImportClause().NamedBindings;
            }
            if (visibleDefaultBinding == null && namedBindings == null)
            {
                return null;
            }
            return Factory().AsNodeFactory().UpdateImportDeclaration(
                decl,
                decl.Modifiers(),
                Factory().AsNodeFactory().UpdateImportClause(
                    decl.ImportClause.AsImportClause(),
                    phaseModifier,
                    visibleDefaultBinding,
                    namedBindings),
                RewriteModuleSpecifier(decl.AsNode(), decl.ModuleSpecifier),
                TryGetResolutionModeOverride(decl.Attributes));
        }
        // Named imports (optionally with visible default)
        var bindingList = CoreUtil.Filter(
            decl.ImportClause.AsImportClause().NamedBindings!.Elements(),
            b => resolver!.IsDeclarationVisible(b)) ?? [];
        if (bindingList.Length > 0 || visibleDefaultBinding != null)
        {
            Node? namedImports = null;
            if (bindingList.Length > 0)
            {
                namedImports = Factory().AsNodeFactory().UpdateNamedImports(
                    decl.ImportClause.AsImportClause().NamedBindings!.AsNamedImports(),
                    Factory().AsNodeFactory().NewNodeList(bindingList));
            }
            return Factory().AsNodeFactory().UpdateImportDeclaration(
                decl,
                decl.Modifiers(),
                Factory().AsNodeFactory().UpdateImportClause(
                    decl.ImportClause.AsImportClause(),
                    phaseModifier,
                    visibleDefaultBinding,
                    namedImports),
                RewriteModuleSpecifier(decl.AsNode(), decl.ModuleSpecifier),
                TryGetResolutionModeOverride(decl.Attributes));
        }
        // Augmentation of export depends on import
        if (resolver!.IsImportRequiredByAugmentation(decl))
        {
            if (state.IsolatedDeclarations)
            {
                state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(decl.AsNode(), global::Ts.Diagnostics.Diagnostics.Declaration_emit_for_this_file_requires_preserving_this_import_for_augmentations_This_is_not_supported_with_isolatedDeclarations));
            }
            return Factory().AsNodeFactory().UpdateImportDeclaration(
                decl,
                decl.Modifiers(),
                /*importClause*/ null,
                RewriteModuleSpecifier(decl.AsNode(), decl.ModuleSpecifier),
                TryGetResolutionModeOverride(decl.Attributes));
        }
        // Nothing visible
        return null;
    }

    // transform.go:2690-2822 — transformExpandoAssignment.
    private void TransformExpandoAssignment(BinaryExpression node)
    {
        var left = node.Left!;

        var symbol = node.Symbol;
        if (symbol == null || (symbol.Flags & SymbolFlags.Assignment) == 0)
        {
            return;
        }

        var ns = AstUtilities.GetLeftmostAccessExpression(left);
        if (ns == null || ns.Kind != SyntaxKind.Identifier)
        {
            return;
        }

        var declaration = resolver!.GetReferencedValueDeclaration(ns);
        if (declaration == null)
        {
            return;
        }

        if (ShouldStripInternal(declaration))
        {
            return;
        }

        if (NodeTests.IsVariableDeclaration(declaration) && declaration.Type() != null)
        {
            return;
        }

        if (NodeTests.IsFunctionDeclaration(declaration) && declaration.FunctionLikeData()!.FullSignature != null)
        {
            return;
        }

        if (NodeTests.IsVariableDeclaration(declaration) && !AstUtilities.IsFunctionLike(declaration.Initializer()!))
        {
            return; // We're going to add a type, no need to dupe members with a namespace
        }

        var hostSymbol = declaration.Symbol();
        if (hostSymbol == null)
        {
            return;
        }

        var name = Factory().NewIdentifier(ns.Text());
        var property = TryGetPropertyName(left);
        if (property.Length == 0 || !global::Ts.Scanner.Scanner.IsIdentifierText(property, LanguageVariant.Standard))
        {
            return;
        }

        var hostId = GetExpandoHostId(declaration);

        if (AstUtilities.IsDeclaration(declaration) && DeclarationUtilities.IsDeclarationAndNotVisible(EmitContext(), resolver, declaration))
        {
            // The host isn't visible (yet) - printing the type of a visible declaration may still
            // late-mark it as visible (e.g. an exported variable whose type prints as `typeof host`),
            // so defer the assignment to be processed if and when that happens.
            if (!deferredExpandoAssignments.TryGetValue(hostId, out var deferredList))
            {
                deferredList = new List<BinaryExpression>();
                deferredExpandoAssignments[hostId] = deferredList;
            }
            deferredList.Add(node);
            return;
        }

        if (NodeTests.IsFunctionDeclaration(declaration) && !DeclarationUtilities.ShouldEmitFunctionProperties(declaration.AsFunctionDeclaration()))
        {
            return;
        }

        TransformExpandoHost(name, declaration);

        var exportName = Factory().NewIdentifier(property);
        var localName = TryGetNameOfAssignedExpression(node.AsNode());
        if (localName == null && !resolver.IsNameResolvable(enclosingDeclaration!, property) && !AstUtilities.IsNonContextualKeyword(global::Ts.Scanner.Scanner.StringToToken(exportName.Text())))
        {
            // use exportName as localName if there won't be any conflicts or keyword issues
            localName = exportName;
        }
        if (localName == null || AstUtilities.IsNonContextualKeyword(global::Ts.Scanner.Scanner.StringToToken(localName.Text())))
        {
            // fallback to a generated name if the localName doesn't exist or is a keyword
            localName = Factory().NewGeneratedNameForNode(node.AsNode());
        }

        var (_, cleanupDiagnosticContext) = SetupDiagnosticContext(node.AsNode());
        try
        {
            if (NodeTests.IsIdentifier(node.Right!))
            {
                // alias-like, emit an `export {name}` or `export {name as alias}`
                var aliasResult = TransformBinaryExpressionToExportDeclaration(node.AsNode(), exportName);
                AddExpandoMember(hostId, aliasResult);
                return;
            }

            var preexistingExpandoHasExport = expandoMembers.TryGetValue(hostId, out var existingMembers) && CoreUtil.Some(existingMembers.ToArray(), NodeTests.IsExportDeclaration);
            ModifierList? varModifiers = null;

            if (preexistingExpandoHasExport)
            {
                varModifiers = Factory().AsNodeFactory().NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(ModifierFlags.Export, Factory().AsNodeFactory().NewModifier));
            }

            var synthesizedNamespace = Factory().AsNodeFactory().NewModuleDeclaration(null /*modifiers*/, SyntaxKind.NamespaceKeyword, name, Factory().AsNodeFactory().NewModuleBlock(Factory().AsNodeFactory().NewNodeList([])));
            synthesizedNamespace.Parent = enclosingDeclaration;
            var declarationData = synthesizedNamespace.DeclarationData()!;
            declarationData.Symbol = hostSymbol;
            var containerData = synthesizedNamespace.LocalsContainerData()!;
            containerData.Locals = new SymbolTable();
            containerData.Locals[localName.Text()] = symbol;

            var oldEnclosing = enclosingDeclaration;
            enclosingDeclaration = synthesizedNamespace;
            try
            {
                var statements = new List<Node>
                {
                    Factory().AsNodeFactory().NewVariableStatement(
                        varModifiers,
                        Factory().AsNodeFactory().NewVariableDeclarationList(
                            Factory().AsNodeFactory().NewNodeList([
                                Factory().AsNodeFactory().NewVariableDeclaration(localName, null /*exclamationToken*/, EnsureType(node.AsNode(), false), null /*initializer*/),
                            ]),
                            NodeFlags.None)),
                };

                if (localName.Text() != exportName.Text())
                {
                    var namedExports = Factory().AsNodeFactory().NewNamedExports(Factory().AsNodeFactory().NewNodeList([
                        Factory().AsNodeFactory().NewExportSpecifier(false /*isTypeOnly*/, localName, exportName),
                    ]));
                    statements.Add(Factory().AsNodeFactory().NewExportDeclaration(null /*modifiers*/, false /*isTypeOnly*/, namedExports, null /*moduleSpecifier*/, null /*attributes*/));
                }

                if (statements.Count > 1 && !preexistingExpandoHasExport)
                {
                    // Add an `export` modifier to all existing expando members so they remain exported after the `export {}` is added
                    if (expandoMembers.TryGetValue(hostId, out var decls))
                    {
                        foreach (var decl in decls)
                        {
                            var modifierFlags = ModifierFlags.Export | AstUtilities.GetCombinedModifierFlags(decl);
                            decl.SetModifiers(Factory().AsNodeFactory().NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modifierFlags, Factory().AsNodeFactory().NewModifier)));
                        }
                    }
                }
                foreach (var s in statements)
                {
                    AddExpandoMember(hostId, s);
                }
            }
            finally
            {
                enclosingDeclaration = oldEnclosing;
            }
        }
        finally
        {
            cleanupDiagnosticContext();
        }
    }

    private void AddExpandoMember(NodeId hostId, Node member)
    {
        if (!expandoMembers.TryGetValue(hostId, out var list))
        {
            list = new List<Node>();
            expandoMembers[hostId] = list;
        }
        list.Add(member);
    }

    // transform.go:2824-2829 — getExpandoHostId.
    private NodeId GetExpandoHostId(Node declaration)
    {
        var root = NodeTests.IsVariableDeclaration(declaration) ? declaration.Parent!.Parent! : declaration;
        return AstUtilities.GetNodeId(EmitContext().MostOriginal(root)!);
    }

    // transform.go:2830-2886 — transformExpandoHost.
    private void TransformExpandoHost(Node name, Node declaration)
    {
        var root = NodeTests.IsVariableDeclaration(declaration) ? declaration.Parent!.Parent! : declaration;
        var id = GetExpandoHostId(declaration);

        if (expandoHosts.ContainsKey(id))
        {
            return;
        }

        var saveNeedsDeclare = needsDeclare;
        needsDeclare = true;

        var modifierFlags = EnsureModifierFlags(root);
        var defaultExport = (modifierFlags & ModifierFlags.Export) != 0 && (modifierFlags & ModifierFlags.Default) != 0;

        needsDeclare = saveNeedsDeclare;

        if (defaultExport)
        {
            modifierFlags |= ModifierFlags.Ambient;
            modifierFlags ^= ModifierFlags.Default;
            modifierFlags ^= ModifierFlags.Export;
        }

        var (_, cleanupDiagnosticContext) = SetupDiagnosticContext(declaration);
        try
        {
            var modifiers = Factory().AsNodeFactory().NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modifierFlags, Factory().AsNodeFactory().NewModifier));
            var replacement = new List<Node>();

            if (NodeTests.IsFunctionDeclaration(declaration))
            {
                var (typeParameters, parameters, asteriskToken) = ExtractExpandoHostParams(declaration);
                replacement.Add(Factory().AsNodeFactory().UpdateFunctionDeclaration(declaration.AsFunctionDeclaration(), modifiers, asteriskToken, declaration.Name(), EnsureTypeParams(declaration, typeParameters), UpdateParameterList(declaration, parameters), EnsureType(declaration, false), null /*fullSignature*/, null /*body*/));
            }
            else if (NodeTests.IsVariableDeclaration(declaration) && AstUtilities.IsFunctionExpressionOrArrowFunction(declaration.Initializer()!))
            {
                var fn = declaration.Initializer()!;
                var (typeParameters, parameters, asteriskToken) = ExtractExpandoHostParams(fn);
                replacement.Add(Factory().AsNodeFactory().NewFunctionDeclaration(modifiers, asteriskToken, Factory().NewIdentifier(name.Text()), EnsureTypeParams(fn, typeParameters), UpdateParameterList(fn, parameters), EnsureType(fn, false), null /*fullSignature*/, null /*body*/));
            }
            else
            {
                expandoHosts[id] = TransformTopLevelDeclaration(declaration)!;
                return;
            }

            state.ReportExpandoFunctionErrors?.Invoke(declaration);

            if (defaultExport)
            {
                if (NodeTests.IsSourceFile(declaration.Parent!))
                {
                    resultHasExternalModuleIndicator = true;
                }
                resultHasScopeMarker = true;
                replacement.Add(Factory().NewExportAssignment(null /*modifiers*/, false /*isExportEquals*/, null /*typeNode*/, name));
            }

            // store host result to be added to the output when it's actually visited
            expandoHosts[id] = Factory().AsNodeFactory().NewSyntaxList(replacement.ToArray());
            if (lateStatementReplacementMap.ContainsKey(id))
            {
                lateStatementReplacementMap[id] = CreateFullExpandoBlock(id);
            }
        }
        finally
        {
            cleanupDiagnosticContext();
        }
    }

    // transform.go:2887-2934 — createFullExpandoBlock.
    private Node? CreateFullExpandoBlock(NodeId id)
    {
        // Process any expando assignments on this host that were skipped because it wasn't
        // visible when they were collected - if it's still not visible, they simply get
        // re-deferred, and are dropped if the host is never late-marked visible.
        if (deferredExpandoAssignments.TryGetValue(id, out var deferred))
        {
            deferredExpandoAssignments.Remove(id);
            foreach (var assignment in deferred)
            {
                TransformExpandoAssignment(assignment);
            }
        }
        expandoHosts.TryGetValue(id, out var n);
        if (expandoMembers.TryGetValue(id, out var addOns))
        {
            ModifierList? modifiers = null;
            Node? name = null;
            Node[] hostNodes = [];
            if (n != null && n.Kind == SyntaxKind.SyntaxList)
            {
                // find the first named syntax list element and use its' name & modifiers
                foreach (var c in n.AsSyntaxList().Children ?? [])
                {
                    if (c.Name() != null)
                    {
                        name = c.Name()!.Clone(Factory());
                        if (c.Modifiers() != null)
                        {
                            modifiers = c.Modifiers()!.Clone(Factory().AsNodeFactory());
                        }
                        break;
                    }
                }
                hostNodes = n.AsSyntaxList().Children ?? [];
            }
            else if (n != null)
            {
                name = n.Name()!.Clone(Factory());
                if (n.Modifiers() != null)
                {
                    modifiers = n.Modifiers()!.Clone(Factory().AsNodeFactory());
                }
                hostNodes = [n];
            }
            if (name != null)
            {
                var moduleDecl = Factory().AsNodeFactory().NewModuleDeclaration(
                    modifiers,
                    SyntaxKind.NamespaceKeyword,
                    name,
                    Factory().AsNodeFactory().NewModuleBlock(Factory().AsNodeFactory().NewNodeList(addOns.ToArray())));
                var members = new List<Node>(hostNodes) { moduleDecl };
                return Factory().AsNodeFactory().NewSyntaxList(members.ToArray());
            }
        }
        return n;
    }

    // transform.go:2935-2948 — extractExpandoHostParams.
    private static (TypeParameterList? typeParameters, ParameterList? parameters, Node? asteriskToken) ExtractExpandoHostParams(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.FunctionExpression:
            {
                var fn = node.AsFunctionExpression();
                return (fn.TypeParameters, fn.Parameters, fn.AsteriskToken);
            }
            case SyntaxKind.ArrowFunction:
            {
                var fn = node.AsArrowFunction();
                return (fn.TypeParameters, fn.Parameters, fn.AsteriskToken);
            }
            default:
            {
                var fn = node.AsFunctionDeclaration();
                return (fn.TypeParameters, fn.Parameters, fn.AsteriskToken);
            }
        }
    }

    // transform.go:2949-2957 — tryGetPropertyName.
    private string TryGetPropertyName(Node node)
    {
        if (NodeTests.IsElementAccessExpression(node))
        {
            return resolver!.GetElementAccessExpressionName(node.AsElementAccessExpression());
        }
        if (NodeTests.IsPropertyAccessExpression(node))
        {
            return node.Name()!.Text();
        }
        return "";
    }

    // transform.go:573-698 — visitDeclarationSubtree (not yet wired; the wiring batch switches the
    // main dispatch to it).
    private Node? VisitDeclarationSubtree(Node input)
    {
        if (ShouldStripInternal(input))
        {
            return null;
        }
        if (AstUtilities.IsDeclaration(input))
        {
            if (DeclarationUtilities.IsDeclarationAndNotVisible(EmitContext(), resolver!, input))
            {
                return null;
            }
            if (AstUtilities.HasDynamicName(input))
            {
                if (state.IsolatedDeclarations)
                {
                    // Classes and object literals usually elide properties with computed names that are not of a literal type
                    // In isolated declarations TSC needs to error on these as we don't know the type in a DTE.
                    if (!resolver!.IsDefinitelyReferenceToGlobalSymbolObject(input.Name()!.Expression()!))
                    {
                        if (NodeTests.IsClassDeclaration(input.Parent!) || NodeTests.IsObjectLiteralExpression(input.Parent!))
                        {
                            state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(input, global::Ts.Diagnostics.Diagnostics.Computed_property_names_on_class_or_object_literals_cannot_be_inferred_with_isolatedDeclarations));
                            return null;
                        }
                        else if ((NodeTests.IsInterfaceDeclaration(input.Parent!) || NodeTests.IsTypeLiteralNode(input.Parent!)) && !AstUtilities.IsEntityNameExpression(input.Name()!.Expression()!))
                        {
                            // Type declarations just need to double-check that the input computed name is an entity name expression
                            state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(input, global::Ts.Diagnostics.Diagnostics.Computed_properties_must_be_number_or_string_literals_variables_or_dotted_expressions_with_isolatedDeclarations));
                            return null;
                        }
                    }
                }
                else if (!resolver!.IsLateBound(EmitContext().ParseNode(input)!) || !AstUtilities.IsEntityNameExpression(input.Name()!.Expression()!))
                {
                    return null;
                }
            }
        }

        // Elide implementation signatures from overload sets
        if (AstUtilities.IsFunctionLike(input) && resolver!.IsImplementationOfOverload(input))
        {
            return null;
        }

        if (input.Kind == SyntaxKind.SemicolonClassElement)
        {
            return null;
        }

        if (NodeTests.IsHeritageClause(input) && (input.AsHeritageClause().Types!.Nodes.Length == 0 || (input.AsHeritageClause().Types!.Nodes.Length == 1 && AstUtilities.NodeIsMissing(input.AsHeritageClause().Types!.Nodes[0]))))
        {
            return null;
        }

        var previousEnclosingDeclaration = enclosingDeclaration;
        if (DeclarationUtilities.IsEnclosingDeclaration(input))
        {
            enclosingDeclaration = input;
        }

        var (canProdiceDiagnostic, cleanupDiagnosticContext) = SetupDiagnosticContext(input);
        try
        {
            Node? result;

            switch (input.Kind)
            {
                case SyntaxKind.MappedType:
                    result = TransformMappedTypeNode(input.AsMappedTypeNode());
                    break;
                case SyntaxKind.HeritageClause:
                    result = TransformHeritageClause(input.AsHeritageClause());
                    break;
                case SyntaxKind.MethodSignature:
                    result = TransformMethodSignatureDeclaration(input.AsMethodSignatureDeclaration());
                    break;
                case SyntaxKind.MethodDeclaration:
                    result = TransformMethodDeclaration(input.AsMethodDeclaration());
                    break;
                case SyntaxKind.ConstructSignature:
                    result = TransformConstructSignatureDeclaration(input.AsConstructSignatureDeclaration());
                    break;
                case SyntaxKind.Constructor:
                    result = TransformConstructorDeclaration(input.AsConstructorDeclaration());
                    break;
                case SyntaxKind.GetAccessor:
                    result = TransformGetAccessorDeclaration(input.AsGetAccessorDeclaration());
                    break;
                case SyntaxKind.SetAccessor:
                    result = TransformSetAccessorDeclaration(input.AsSetAccessorDeclaration());
                    break;
                case SyntaxKind.PropertyDeclaration:
                    result = TransformPropertyDeclaration(input.AsPropertyDeclaration());
                    break;
                case SyntaxKind.PropertySignature:
                    result = TransformPropertySignatureDeclaration(input.AsPropertySignatureDeclaration());
                    break;
                case SyntaxKind.CallSignature:
                    result = TransformCallSignatureDeclaration(input.AsCallSignatureDeclaration());
                    break;
                case SyntaxKind.IndexSignature:
                    result = TransformIndexSignatureDeclaration(input.AsIndexSignatureDeclaration());
                    break;
                case SyntaxKind.VariableDeclaration:
                    result = TransformVariableDeclaration(input.AsVariableDeclaration());
                    break;
                case SyntaxKind.TypeParameter:
                    result = TransformTypeParameterDeclaration(input.AsTypeParameterDeclaration());
                    break;
                case SyntaxKind.ExpressionWithTypeArguments:
                    result = TransformExpressionWithTypeArguments(input.AsExpressionWithTypeArguments());
                    break;
                case SyntaxKind.TypeReference:
                    result = TransformTypeReference(input.AsTypeReferenceNode());
                    break;
                case SyntaxKind.ConditionalType:
                    result = TransformConditionalTypeNode(input.AsConditionalTypeNode());
                    break;
                case SyntaxKind.FunctionType:
                    result = TransformFunctionTypeNode(input.AsFunctionTypeNode());
                    break;
                case SyntaxKind.ConstructorType:
                    result = TransformConstructorTypeNode(input.AsConstructorTypeNode());
                    break;
                case SyntaxKind.ImportType:
                    result = TransformImportTypeNode(input.AsImportTypeNode());
                    break;
                case SyntaxKind.TypeQuery:
                    CheckEntityNameVisibility(input.AsTypeQueryNode().ExprName!, enclosingDeclaration!);
                    result = Visitor().VisitEachChild(input);
                    break;
                case SyntaxKind.TupleType:
                    result = Visitor().VisitEachChild(input);
                    if (result != null)
                    {
                        if (global::Ts.Transformers.Transformers.IsOriginalNodeSingleLine(EmitContext(), input))
                        {
                            EmitContext().AddEmitFlags(result, global::Ts.Printer.EmitFlags.SingleLine);
                        }
                    }
                    break;
                case SyntaxKind.JSDocTypeExpression:
                    result = TransformJSDocTypeExpression(input.AsJSDocTypeExpression());
                    break;
                case SyntaxKind.JSDocTypeLiteral:
                    result = TransformJSDocTypeLiteral(input.AsJSDocTypeLiteral());
                    break;
                case SyntaxKind.JSDocPropertyTag:
                    result = TransformJSDocPropertyTag(input.AsJSDocParameterOrPropertyTag());
                    break;
                case SyntaxKind.JSDocAllType:
                    result = TransformJSDocAllType(input.AsJSDocAllType());
                    break;
                case SyntaxKind.JSDocNullableType:
                    result = TransformJSDocNullableType(input.AsJSDocNullableType());
                    break;
                case SyntaxKind.JSDocNonNullableType:
                    result = TransformJSDocNonNullableType(input.AsJSDocNonNullableType());
                    break;
                case SyntaxKind.JSDocOptionalType:
                    result = TransformJSDocOptionalType(input.AsJSDocOptionalType());
                    break;
                case SyntaxKind.JSDocVariadicType:
                    result = TransformJSDocVariadicType(input.AsJSDocVariadicType());
                    break;
                default:
                    result = Visitor().VisitEachChild(input);
                    break;
            }

            if (result != null && canProdiceDiagnostic && AstUtilities.HasDynamicName(input))
            {
                CheckName(input);
            }

            enclosingDeclaration = previousEnclosingDeclaration;
            return result;
        }
        finally
        {
            cleanupDiagnosticContext();
        }
    }

    // transform.go:703-716 — checkName.
    private void CheckName(Node node)
    {
        var oldDiag = state.GetSymbolAccessibilityDiagnostic;
        if (!suppressNewDiagnosticContexts)
        {
            state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNodeName(node);
        }
        state.ErrorNameNode = node.Name();
        Debug.Assert(AstUtilities.HasDynamicName(node)); // Should only be called with dynamic names
        var entityName = node.Name()!.Expression()!;
        CheckEntityNameVisibility(entityName, enclosingDeclaration!);
        if (!suppressNewDiagnosticContexts)
        {
            state.GetSymbolAccessibilityDiagnostic = oldDiag;
        }
        state.ErrorNameNode = null;
    }

    // transform.go:1668-1671 — checkEntityNameVisibility.
    private void CheckEntityNameVisibility(Node entityName, Node enclosingDeclarationParam)
    {
        var visibilityResult = resolver!.IsEntityNameVisible(entityName, enclosingDeclarationParam);
        tracker.HandleSymbolAccessibilityError(visibilityResult);
    }

    // transform.go:718-736 — transformMappedTypeNode: handle missing template type nodes, since the
    // printer does not.
    private Node TransformMappedTypeNode(MappedTypeNode input)
    {
        Node? typeNode;
        if (input.Type == null)
        {
            typeNode = Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        else
        {
            typeNode = Visitor().VisitNode(input.Type);
        }
        return Factory().AsNodeFactory().UpdateMappedTypeNode(
            input,
            input.ReadonlyToken,
            Visitor().VisitNode(input.TypeParameter),
            Visitor().VisitNode(input.NameType),
            input.QuestionToken,
            typeNode,
            null);
    }

    // transform.go:737-754 — transformHeritageClause.
    private Node? TransformHeritageClause(HeritageClause clause)
    {
        var retainedClauses = CoreUtil.Filter(clause.Types!.Nodes, t =>
            AstUtilities.IsEntityNameExpression(t.AsExpressionWithTypeArguments().Expression!) ||
            (clause.Token == SyntaxKind.ExtendsKeyword && t.Expression()!.Kind == SyntaxKind.NullKeyword)) ?? [];
        if (retainedClauses.Length == 0)
        {
            return null; // elide empty clause
        }
        if (retainedClauses.Length == clause.Types.Nodes.Length)
        {
            return Visitor().VisitEachChild(clause.AsNode());
        }
        return Factory().AsNodeFactory().UpdateHeritageClause(
            clause,
            clause.Token,
            Visitor().VisitNodes(Factory().AsNodeFactory().NewNodeList(retainedClauses)));
    }

    // transform.go:755-771 — transformImportTypeNode.
    private Node TransformImportTypeNode(ImportTypeNode input)
    {
        if (!AstUtilities.IsLiteralImportTypeNode(input.AsNode()))
        {
            return input.AsNode();
        }
        return Factory().AsNodeFactory().UpdateImportTypeNode(
            input,
            input.IsTypeOf,
            Factory().AsNodeFactory().UpdateLiteralTypeNode(
                input.Argument!.AsLiteralTypeNode(),
                RewriteModuleSpecifier(input.AsNode(), input.Argument.AsLiteralTypeNode().Literal)),
            input.Attributes,
            input.Qualifier,
            Visitor().VisitNodes(input.TypeArguments));
    }

    // transform.go:772-781 — transformConstructorTypeNode.
    private Node TransformConstructorTypeNode(ConstructorTypeNode input)
    {
        return Factory().AsNodeFactory().UpdateConstructorTypeNode(
            input,
            EnsureModifiers(input.AsNode()),
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            Visitor().VisitNode(input.Type));
    }

    // transform.go:782-790 — transformFunctionTypeNode.
    private Node TransformFunctionTypeNode(FunctionTypeNode input)
    {
        return Factory().AsNodeFactory().UpdateFunctionTypeNode(
            input,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            Visitor().VisitNode(input.Type));
    }

    // transform.go:791-808 — transformConditionalTypeNode.
    private Node TransformConditionalTypeNode(ConditionalTypeNode input)
    {
        var checkType = Visitor().VisitNode(input.CheckType);
        var extendsType = Visitor().VisitNode(input.ExtendsType);
        var oldEnclosingDecl = enclosingDeclaration;
        enclosingDeclaration = input.TrueType;
        var trueType = Visitor().VisitNode(input.TrueType);
        enclosingDeclaration = oldEnclosingDecl;
        var falseType = Visitor().VisitNode(input.FalseType);

        return Factory().AsNodeFactory().UpdateConditionalTypeNode(
            input,
            checkType,
            extendsType,
            trueType,
            falseType);
    }

    // transform.go:809-813 — transformTypeReference.
    private Node? TransformTypeReference(TypeReferenceNode input)
    {
        CheckEntityNameVisibility(input.TypeName!, enclosingDeclaration!);
        return Visitor().VisitEachChild(input.AsNode());
    }

    // transform.go:814-820 — transformExpressionWithTypeArguments.
    private Node? TransformExpressionWithTypeArguments(ExpressionWithTypeArguments input)
    {
        if (AstUtilities.IsEntityName(input.Expression!) || AstUtilities.IsEntityNameExpression(input.Expression!))
        {
            CheckEntityNameVisibility(input.Expression!, enclosingDeclaration!);
        }
        return Visitor().VisitEachChild(input.AsNode());
    }

    // transform.go:821-834 — transformTypeParameterDeclaration.
    private Node? TransformTypeParameterDeclaration(TypeParameterDeclaration input)
    {
        if (DeclarationUtilities.IsPrivateMethodTypeParameter(host, input) && (input.DefaultType != null || input.Constraint != null))
        {
            return Factory().AsNodeFactory().UpdateTypeParameterDeclaration(
                input,
                input.Modifiers(),
                input.Name(),
                null,
                input.Expression,
                null);
        }
        return Visitor().VisitEachChild(input.AsNode());
    }

    // transform.go:853-884 — transformCjsRequireVariableDeclaration.
    private Node? TransformCjsRequireVariableDeclaration(VariableDeclaration input)
    {
        var specifier = RewriteModuleSpecifier(input.AsNode(), input.Initializer!.AsCallExpression().Arguments!.Nodes[0]);
        if (NodeTests.IsIdentifier(input.Name()!))
        {
            // `const x = require("something")` -> `import x = require("something")`
            return Factory().AsNodeFactory().NewImportEqualsDeclaration(null, false, input.Name(), Factory().AsNodeFactory().NewExternalModuleReference(specifier));
        }
        else if (NodeTests.IsArrayBindingPattern(input.Name()!))
        {
            // TODO: Is this actually reachable? should we error on this?
            return null;
        }
        else
        { // object binding pattern
            // `const {x, y: z} = require("something")` -> `import {x, y as z} from "something"`
            var b = input.Name()!.AsBindingPattern();
            var importSpecifiers = new List<Node>();
            foreach (var elem in b.Elements!.Nodes)
            {
                if (!NodeTests.IsIdentifier(elem.Name()!))
                {
                    continue; // nested destructuring, bail
                }
                importSpecifiers.Add(Factory().AsNodeFactory().NewImportSpecifier(false, elem.PropertyName(), elem.Name()));
            }
            return Factory().AsNodeFactory().NewImportDeclaration(
                null,
                Factory().AsNodeFactory().NewImportClause(
                    SyntaxKind.Unknown,
                    null,
                    Factory().AsNodeFactory().NewNamedImports(Factory().AsNodeFactory().NewNodeList(importSpecifiers.ToArray()))),
                specifier,
                null);
        }
    }

    // transform.go:885-906 — recreateBindingPattern.
    private Node? RecreateBindingPattern(BindingPattern input)
    {
        var results = new List<Node>();
        foreach (var elem in input.Elements!.Nodes)
        {
            var result = RecreateBindingElement(elem.AsBindingElement());
            if (result == null)
            {
                continue;
            }
            if (result.Kind == SyntaxKind.SyntaxList)
            {
                results.AddRange(result.AsSyntaxList().Children ?? []);
            }
            else
            {
                results.Add(result);
            }
        }
        if (results.Count == 0)
        {
            return null;
        }
        if (results.Count == 1)
        {
            return results[0];
        }
        return Factory().AsNodeFactory().NewSyntaxList(results.ToArray());
    }

    // transform.go:907-924 — recreateBindingElement.
    private Node? RecreateBindingElement(BindingElement e)
    {
        if (e.Name() == null)
        {
            return null;
        }
        if (!DeclarationUtilities.GetBindingNameVisible(resolver!, e.AsNode()))
        {
            return null;
        }
        if (AstUtilities.IsBindingPattern(e.Name()!))
        {
            return RecreateBindingPattern(e.Name()!.AsBindingPattern());
        }
        return Factory().AsNodeFactory().NewVariableDeclaration(
            e.Name(),
            null,
            EnsureType(e.AsNode(), false),
            null); // TODO: possible strada bug - not emitting const initialized binding pattern elements?
    }

    // transform.go:1572-1576 — preserveJsDoc: copy comment range from original to updated node so
    // JSDoc comments are preserved.
    private void PreserveJsDoc(Node updated, Node original) =>
        EmitContext().AssignCommentRange(updated, original);

    // transform.go:1577-1592 — preservePartialJsDoc.
    private void PreservePartialJsDoc(Node updated, Node original)
    {
        if ((original.Flags & NodeFlags.Reparsed) == 0)
        {
            return;
        }
        var jsdoc = CoreUtil.FirstOrNil(original.EagerJSDoc(AstUtilities.GetSourceFileOfNode(original)));
        if (jsdoc == null)
        {
            return;
        }
        var description = global::Ts.Scanner.Scanner.GetTextOfJSDocComment(jsdoc.AsJSDoc().Comment);
        if (description.Length == 0)
        {
            return;
        }
        var comment = "*\n * " + description.Replace("\n", "\n * ", StringComparison.Ordinal) + "\n ";
        EmitContext().AddSyntheticLeadingComment(updated, SyntaxKind.MultiLineCommentTrivia, comment, true /*hasTrailingNewLine*/);
    }

    // transform.go:1593-1598 — removeAllComments.
    private void RemoveAllComments(Node node)
    {
        EmitContext().AddEmitFlags(node, global::Ts.Printer.EmitFlags.NoComments);
        // !!! TODO: Also remove synthetic trailing/leading comments added by transforms
        // emitNode.leadingComments = undefined;
        // emitNode.trailingComments = undefined;
    }

    // transform.go:1664-1666 — shouldPrintWithInitializer.
    private bool ShouldPrintWithInitializer(Node node) =>
        DeclarationUtilities.CanHaveLiteralInitializer(host, node) && node.Initializer() != null &&
        resolver!.IsLiteralConstDeclaration(EmitContext().MostOriginal(node)!);

    // transform.go:2392-2401 — ensureNoInitializer.
    private Node? EnsureNoInitializer(Node node)
    {
        if (ShouldPrintWithInitializer(node))
        {
            var unwrappedInitializer = DeclarationUtilities.UnwrapParenthesizedExpression(node.Initializer()!);
            if (!AstUtilities.IsPrimitiveLiteralValue(unwrappedInitializer, true))
            {
                tracker.ReportInferenceFallback(node);
            }
            return resolver!.CreateLiteralConstValue(EmitContext(), EmitContext().ParseNode(node)!, tracker);
        }
        return null;
    }

    // transform.go:2321-2354 — ensureTypeParams.
    private TypeParameterList? EnsureTypeParams(Node node, TypeParameterList? @params)
    {
        if (host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(node)!, ModifierFlags.Private) != 0)
        {
            return null;
        }
        var typeParameters = Visitor().VisitNodes(@params);
        if (typeParameters != null)
        {
            return typeParameters;
        }
        var oldErrorNameNode = state.ErrorNameNode;
        state.ErrorNameNode = node.Name();
        GetSymbolAccessibilityDiagnostic? oldDiag = null;
        if (!suppressNewDiagnosticContexts)
        {
            oldDiag = state.GetSymbolAccessibilityDiagnostic;
            if (DeclarationUtilities.CanProduceDiagnostics(node))
            {
                state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(node);
            }
        }

        if (node.FunctionLikeData() is { FullSignature: not null })
        {
            var nodes = resolver!.CreateTypeParametersOfSignatureDeclaration(EmitContext(), node, enclosingDeclaration!, DeclarationEmitNodeBuilderFlags, DeclarationEmitInternalNodeBuilderFlags, tracker);
            if (nodes != null)
            {
                typeParameters = new NodeList(nodes) { Loc = node.Loc };
            }
        }

        state.ErrorNameNode = oldErrorNameNode;
        if (!suppressNewDiagnosticContexts)
        {
            state.GetSymbolAccessibilityDiagnostic = oldDiag;
        }
        return typeParameters;
    }

    // transform.go:1015-1051 — updateAccessorParamList.
    private ParameterList UpdateAccessorParamList(Node input, bool isPrivate)
    {
        var newParams = new List<Node>();
        if (!isPrivate)
        {
            var thisParam = AstUtilities.GetThisParameter(input);
            if (thisParam != null)
            {
                newParams.Add(EnsureParameter(thisParam.AsParameterDeclaration()));
            }
        }
        if (NodeTests.IsSetAccessorDeclaration(input))
        {
            Node? valueParam = null;
            if (!isPrivate)
            {
                if (newParams.Count == 1 && input.AsSetAccessorDeclaration().Parameters!.Nodes.Length >= 2)
                {
                    valueParam = EnsureParameter(input.AsSetAccessorDeclaration().Parameters!.Nodes[1].AsParameterDeclaration());
                }
                else if (newParams.Count == 0 && input.AsSetAccessorDeclaration().Parameters!.Nodes.Length >= 1)
                {
                    valueParam = EnsureParameter(input.AsSetAccessorDeclaration().Parameters!.Nodes[0].AsParameterDeclaration());
                }
            }
            if (valueParam == null)
            {
                // When synthesizing a missing value parameter, emit `value: any` for non-private accessors to match TypeScript's declaration emit behavior.
                Node? t = null;
                if (!isPrivate)
                {
                    t = Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
                }
                valueParam = Factory().NewParameterDeclaration(
                    null,
                    null,
                    Factory().NewIdentifier("value"),
                    null,
                    t,
                    null);
            }
            newParams.Add(valueParam);
        }
        return Factory().AsNodeFactory().NewNodeList(newParams.ToArray());
    }

    // transform.go:1074-1088 — omitPrivateMethodType.
    private Node? OmitPrivateMethodType(Node input)
    {
        if (input.Symbol() != null && (input.Symbol()!.Declarations?.Length ?? 0) > 0 && input.Symbol()!.Declarations![0] != input)
        {
            return null;
        }
        var result = Factory().AsNodeFactory().NewPropertyDeclaration(
            EnsureModifiers(input),
            input.Name(),
            null,
            null,
            null);
        PreserveJsDoc(result, input);
        return result;
    }

    // transform.go:1553-1560 — rewriteModuleSpecifier.
    private Node? RewriteModuleSpecifier(Node parent, Node? input)
    {
        if (input == null)
        {
            return null;
        }
        resultHasExternalModuleIndicator = resultHasExternalModuleIndicator || (parent.Kind != SyntaxKind.ModuleDeclaration && parent.Kind != SyntaxKind.ImportType);
        return input;
    }

    // transform.go:1561-1571 — tryGetResolutionModeOverride.
    private Node? TryGetResolutionModeOverride(Node? node)
    {
        if (node == null)
        {
            return node;
        }
        var mode = host!.GetResolutionModeOverride(node);
        if (mode != ResolutionMode.None)
        {
            return node;
        }
        return null;
    }

    // transform.go:1128-1157 — visitDeclarationStatements (not yet wired).
    private Node? VisitDeclarationStatements(Node input)
    {
        if (ShouldStripInternal(input))
        {
            return null;
        }
        switch (input.Kind)
        {
            case SyntaxKind.ExportDeclaration:
                if (NodeTests.IsSourceFile(input.Parent!))
                {
                    resultHasExternalModuleIndicator = true;
                }
                resultHasScopeMarker = true;
                // Rewrite external module names if necessary
                return Factory().AsNodeFactory().UpdateExportDeclaration(
                    input.AsExportDeclaration(),
                    input.Modifiers(),
                    input.IsTypeOnly(),
                    input.AsExportDeclaration().ExportClause,
                    RewriteModuleSpecifier(input, input.ModuleSpecifier()),
                    TryGetResolutionModeOverride(input.AsExportDeclaration().Attributes));
            case SyntaxKind.ExportAssignment:
                return TransformExportAssignment(input, input, input.Expression()!, input.AsExportAssignment().IsExportEquals);
            default:
                var id = AstUtilities.GetNodeId(EmitContext().MostOriginal(input)!);
                if (!lateStatementReplacementMap.TryGetValue(id, out var existing) || existing == null)
                {
                    if (!lateStatementReplacementMap.ContainsKey(id))
                    {
                        // Don't actually transform yet; just leave as original node - will be elided/swapped by late pass
                        lateStatementReplacementMap[id] = TransformTopLevelDeclaration(input);
                    }
                }
                return input;
        }
    }

    // transform.go:1159-1178 — tryGetNameOfAssignedExpression.
    private Node? TryGetNameOfAssignedExpression(Node unwrapped)
    {
        Node? nameNode = null;
        var nameText = "";
        if (!NodeTests.IsPropertyAccessExpression(unwrapped) && unwrapped.Name() != null)
        {
            nameText = unwrapped.Name()!.Text();
        }
        else if (NodeTests.IsIdentifier(unwrapped))
        {
            nameText = unwrapped.Text();
        }
        if (nameText.Length != 0 && nameText != "default")
        {
            if (resolver!.IsNameResolvable(enclosingDeclaration!, nameText))
            {
                // create a unique name that shares the same text as its' base
                nameNode = Factory().NewUniqueNameEx(nameText, new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
            }
            else
            {
                // use the node's name as-is, since it's not otherwise in-scope
                nameNode = Factory().NewIdentifier(nameText);
            }
        }
        return nameNode;
    }

    // transform.go:1179-1193 — getNameOfExportedAssignedExpression.
    private Node GetNameOfExportedAssignedExpression(Node unwrapped, bool isExportEquals)
    {
        var nameNode = TryGetNameOfAssignedExpression(unwrapped);
        if (nameNode == null)
        {
            // fallback to a default name
            if (isExportEquals && AstUtilities.IsSourceFileJS(state.CurrentSourceFile!))
            {
                // only JS files prefer to use `_exports` for export assignments - TS has always used `_default` for both `export=` and `export default`
                nameNode = Factory().NewUniqueNameEx("_exports", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
            }
            else
            {
                nameNode = Factory().NewUniqueNameEx("_default", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
            }
        }
        cjsExportAssignmentName = nameNode;
        return nameNode;
    }

    // transform.go:1194-1263 — transformExportAssignment (full form; the single-arg approximation
    // overload is replaced by the wiring batch).
    private Node? TransformExportAssignment(Node input, Node assignment, Node expression, bool isExportEquals)
    {
        if (NodeTests.IsSourceFile(input.Parent!))
        {
            resultHasExternalModuleIndicator = true;
        }
        resultHasScopeMarker = true;
        if (NodeTests.IsIdentifier(expression) && (NodeTests.IsSourceFile(input.Parent!) || NodeTests.IsModuleBlock(input.Parent!)))
        {
            var identExportAssignment = Factory().NewExportAssignment(null, isExportEquals, null, expression);
            PreserveJsDoc(identExportAssignment, input);
            return identExportAssignment;
        }

        // Check if the expression is a class expression - emit as a class declaration + export assignment
        var unwrapped = AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.ExpressionTypePassthrough);
        var newId = GetNameOfExportedAssignedExpression(unwrapped, isExportEquals);
        if (NodeTests.IsClassExpression(unwrapped))
        {
            var mods = new List<Node>();
            if (needsDeclare)
            {
                mods.Add(Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword));
            }
            var classDecl = TransformClassExpressionToDeclaration(unwrapped, newId, Factory().AsNodeFactory().NewModifierList(mods.ToArray()));
            PreserveJsDoc(classDecl, input);
            // Reuse the same name node for the export so unique names resolve consistently
            var classExportAssignment = Factory().NewExportAssignment(null, isExportEquals, null, newId);
            RemoveAllComments(classExportAssignment);
            return Factory().AsNodeFactory().NewSyntaxList([classExportAssignment, classDecl]);
        }
        else if (AstUtilities.IsFunctionLike(unwrapped))
        {
            // Promote function or arrow function expressions to a function declaration
            var mods = new List<Node>();
            if (needsDeclare)
            {
                mods.Add(Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword));
            }
            var funcDecl = TransformFunctionLikeToDeclaration(unwrapped, newId, Factory().AsNodeFactory().NewModifierList(mods.ToArray()));
            PreserveJsDoc(funcDecl, input);
            // Reuse the same name node for the export so unique names resolve consistently
            var funcExportAssignment = Factory().NewExportAssignment(null, isExportEquals, null, newId);
            RemoveAllComments(funcExportAssignment);
            return Factory().AsNodeFactory().NewSyntaxList([funcExportAssignment, funcDecl]);
        }

        // expression is non-identifier, create _default typed variable to reference
        state.GetSymbolAccessibilityDiagnostic = _ => new SymbolAccessibilityDiagnostic
        {
            DiagnosticMessage = global::Ts.Diagnostics.Diagnostics.Default_export_of_the_module_has_or_is_using_private_name_0,
            ErrorNode = input,
        };
        cjsExportAssignmentName = newId;
        tracker.PushErrorFallbackNode(assignment);
        Node? type_ = null;
        Node? initializer = null;
        if (AstUtilities.IsPrimitiveLiteralValue(DeclarationUtilities.UnwrapParenthesizedExpression(expression), true))
        {
            initializer = resolver!.CreateLiteralConstValue(EmitContext(), EmitContext().ParseNode(assignment)!, tracker);
        }
        if (initializer == null)
        {
            type_ = EnsureType(assignment, false);
        }
        var varDecl = Factory().AsNodeFactory().NewVariableDeclaration(newId, null, type_, initializer);
        tracker.PopErrorFallbackNode();
        ModifierList modList;
        if (needsDeclare)
        {
            modList = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword)]);
        }
        else
        {
            modList = Factory().AsNodeFactory().NewModifierList([]);
        }
        var statement = Factory().AsNodeFactory().NewVariableStatement(modList, Factory().AsNodeFactory().NewVariableDeclarationList(Factory().AsNodeFactory().NewNodeList([varDecl]), NodeFlags.Const));
        var exportAssignment = Factory().NewExportAssignment(null, isExportEquals, null, newId);
        // Remove comments from the export declaration and copy them onto the synthetic _default declaration
        PreserveJsDoc(statement, input);
        return Factory().AsNodeFactory().NewSyntaxList([statement, exportAssignment]);
    }

    // transform.go:1264-1277 — transformFunctionLikeToDeclaration.
    private Node TransformFunctionLikeToDeclaration(Node unwrapped, Node funcName, ModifierList mods)
    {
        var d = unwrapped.FunctionLikeData()!;
        return Factory().AsNodeFactory().NewFunctionDeclaration(
            mods,
            null,
            funcName,
            EnsureTypeParams(unwrapped, d.TypeParameters),
            UpdateParameterList(unwrapped, d.Parameters),
            EnsureType(unwrapped, false),
            Visitor().VisitNode(d.FullSignature),
            null);
    }

    // transform.go:1278-1296 — transformBinaryExpressionToExportDeclaration.
    private Node TransformBinaryExpressionToExportDeclaration(Node input, Node name)
    {
        var propertyName = input.AsBinaryExpression().Right;

        // track alias target so referenced declarations are included in the output
        tracker.HandleSymbolAccessibilityError(resolver!.IsEntityNameVisible(propertyName!, enclosingDeclaration!));

        if (NodeTests.IsIdentifier(name) && propertyName!.Text() == name.Text())
        {
            propertyName = null;
        }

        return Factory().AsNodeFactory().NewExportDeclaration(
            null,
            false,
            Factory().AsNodeFactory().NewNamedExports(Factory().AsNodeFactory().NewNodeList([Factory().AsNodeFactory().NewExportSpecifier(false, propertyName, name)])),
            null,
            null);
    }

    // transform.go:1297-1304 — transformCommonJSExport.
    private Node? TransformCommonJSExport(Node input, Node name)
    {
        var res = TransformCommonJSExportWorker(input, name);
        if (res == null)
        {
            return res;
        }
        return WrapInCJSExportNamespace(res);
    }

    // transform.go:1305-1488 — transformCommonJSExportWorker.
    private Node? TransformCommonJSExportWorker(Node input, Node name)
    {
        var nameText = "";
        if (NodeTests.IsIdentifier(name) || NodeTests.IsStringLiteral(name))
        {
            nameText = name.Text();
        }
        if (global::Ts.Collections.SetExtensions.Has(witnessedCjsExports, nameText) && nameText.Length != 0)
        {
            return null; // Already emitted this export name
        }
        witnessedCjsExports.Add(nameText);
        resultHasExternalModuleIndicator = true;
        resultHasScopeMarker = true;
        // only transform cjs exports to shorthand at the top-level of a source file, otherwise we uniformly emit nested exports with a type annotation
        if (IsCommonJSAliasExport(input) && NodeTests.IsExpressionStatement(input.Parent!) && NodeTests.IsSourceFile(input.Parent!.Parent!))
        {
            // export { name }
            // export { source as name }
            return TransformBinaryExpressionToExportDeclaration(input, name);
        }

        // Check if the RHS is a class expression - emit as a class declaration instead of a typed variable
        if (NodeTests.IsBinaryExpression(input))
        {
            var rhs = DeclarationUtilities.UnwrapParenthesizedExpression(input.AsBinaryExpression().Right!);
            if (NodeTests.IsClassExpression(rhs))
            {
                var ce = rhs.AsClassExpression();
                var classExprName = ce.Name();
                var hasExprName = classExprName != null && classExprName!.Text().Length > 0;

                if (hasExprName)
                {
                    // Set up TrackSymbol watch to detect if the class expression's own
                    // symbol is referenced during member type serialization.
                    tracker.WatchClassSymbol(rhs.Symbol());
                    try
                    {
                        // Serialize class members using the class expression name, which
                        // triggers TrackSymbol for any self-referential member types.
                        var className2 = Factory().NewIdentifier(classExprName!.Text());
                        var classMods = new Node[] { Factory().AsNodeFactory().NewModifier(SyntaxKind.ExportKeyword) };
                        var classDecl2 = TransformClassExpressionToDeclaration(rhs, className2, Factory().AsNodeFactory().NewModifierList(classMods));
                        PreserveJsDoc(classDecl2, input);

                        // Determine if namespace isolation is needed:
                        // - The class expression name differs from the export name, OR
                        // - The class's own symbol was used in a member's serialized type
                        var namesDiffer = !NodeTests.IsIdentifier(name) || classExprName!.Text() != name.Text();
                        var needsIsolation = namesDiffer || tracker.ClassSymbolTracked;

                        if (needsIsolation)
                        {
                            var nsName = Factory().NewUniqueNameEx("_ns", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
                            var nsMods = new List<Node>();
                            if (needsDeclare)
                            {
                                nsMods.Add(Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword));
                            }
                            var nsDecl = Factory().AsNodeFactory().NewModuleDeclaration(
                                Factory().AsNodeFactory().NewModifierList(nsMods.ToArray()),
                                SyntaxKind.NamespaceKeyword,
                                nsName,
                                Factory().AsNodeFactory().NewModuleBlock(Factory().AsNodeFactory().NewNodeList([classDecl2])));

                            var aliasBase = "_exported";
                            var nameText2 = name!.Text();
                            if (NodeTests.IsIdentifier(name) && global::Ts.Scanner.Scanner.IsIdentifierText("_" + nameText2, LanguageVariant.Standard))
                            {
                                aliasBase = "_" + nameText2;
                            }
                            var importAlias = Factory().NewUniqueNameEx(aliasBase, new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
                            var qualifiedName = Factory().AsNodeFactory().NewQualifiedName(nsName, className2);
                            var importDecl = Factory().AsNodeFactory().NewImportEqualsDeclaration(null, false, importAlias, qualifiedName);

                            var exportSpecifier = Factory().AsNodeFactory().NewExportSpecifier(false, importAlias, name);
                            var exportDecl2 = Factory().AsNodeFactory().NewExportDeclaration(null, false, Factory().AsNodeFactory().NewNamedExports(Factory().AsNodeFactory().NewNodeList([exportSpecifier])), null, null);
                            RemoveAllComments(exportDecl2);

                            return Factory().AsNodeFactory().NewSyntaxList([nsDecl, importDecl, exportDecl2]);
                        }

                        // No isolation needed: names match and no self-references.
                        // Update modifiers to include declare if needed.
                        var mods2 = new List<Node> { Factory().AsNodeFactory().NewModifier(SyntaxKind.ExportKeyword) };
                        if (needsDeclare)
                        {
                            mods2.Add(Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword));
                        }
                        classDecl2 = Factory().UpdateClassDeclaration(
                            classDecl2.AsClassDeclaration(),
                            Factory().AsNodeFactory().NewModifierList(mods2.ToArray()),
                            classDecl2.AsClassDeclaration().Name(),
                            classDecl2.AsClassDeclaration().TypeParameters,
                            classDecl2.AsClassDeclaration().HeritageClauses,
                            classDecl2.AsClassDeclaration().Members);
                        return classDecl2;
                    }
                    finally
                    {
                        tracker.WatchClassSymbol(null);
                    }
                }
                var mods3 = new List<Node> { Factory().AsNodeFactory().NewModifier(SyntaxKind.ExportKeyword) };
                if (needsDeclare)
                {
                    mods3.Add(Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword));
                }
                var className3 = name;
                if (!NodeTests.IsIdentifier(className3))
                {
                    className3 = Factory().NewUniqueNameEx("_class", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
                }
                var classDecl3 = TransformClassExpressionToDeclaration(rhs, className3, Factory().AsNodeFactory().NewModifierList(mods3.ToArray()));
                PreserveJsDoc(classDecl3, input);
                if (!NodeTests.IsIdentifier(name))
                {
                    // Non-identifier name: emit class declaration + named export
                    var exportDecl3 = Factory().AsNodeFactory().NewExportDeclaration(null, false, Factory().AsNodeFactory().NewNamedExports(Factory().AsNodeFactory().NewNodeList([Factory().AsNodeFactory().NewExportSpecifier(false, className3, name)])), null, null);
                    RemoveAllComments(exportDecl3);
                    return Factory().AsNodeFactory().NewSyntaxList([classDecl3, exportDecl3]);
                }
                return classDecl3;
            }
        }

        if (NodeTests.IsIdentifier(name))
        {
            if (name.Text() == "default")
            {
                // const _default: Type; export default _default;
                var newId = Factory().NewUniqueNameEx("_default", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
                state.GetSymbolAccessibilityDiagnostic = _ => new SymbolAccessibilityDiagnostic
                {
                    DiagnosticMessage = global::Ts.Diagnostics.Diagnostics.Default_export_of_the_module_has_or_is_using_private_name_0,
                    ErrorNode = input,
                };
                tracker.PushErrorFallbackNode(input);
                var type1 = EnsureType(input, false);
                var varDecl1 = Factory().AsNodeFactory().NewVariableDeclaration(newId, null, type1, null);
                tracker.PopErrorFallbackNode();
                ModifierList modList1;
                if (needsDeclare)
                {
                    modList1 = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword)]);
                }
                else
                {
                    modList1 = Factory().AsNodeFactory().NewModifierList([]);
                }
                var statement1 = Factory().AsNodeFactory().NewVariableStatement(modList1, Factory().AsNodeFactory().NewVariableDeclarationList(Factory().AsNodeFactory().NewNodeList([varDecl1]), NodeFlags.Const));

                var assignment1 = Factory().NewExportAssignment(input.Modifiers(), false, null, newId);
                // Remove comments from the export declaration and copy them onto the synthetic _default declaration
                PreserveJsDoc(statement1, input);
                RemoveAllComments(assignment1);
                return Factory().AsNodeFactory().NewSyntaxList([statement1, assignment1]);
            }
            else if (host!.GetEmitResolver().GetReferencedValueDeclaration(name) == input || host.GetEmitResolver().GetReferencedValueDeclaration(name) == null)
            {
                // only inline to a export var if the `name` lookup points at this assignment or nothing - if it points at something else, we must use a temp name
                // export var name: Type
                tracker.PushErrorFallbackNode(input);
                var type2 = EnsureType(input, false);
                var varDecl2 = Factory().AsNodeFactory().NewVariableDeclaration(name, null, type2, null);
                tracker.PopErrorFallbackNode();
                ModifierList modList2;
                if (needsDeclare)
                {
                    modList2 = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.ExportKeyword), Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword)]);
                }
                else
                {
                    modList2 = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.ExportKeyword)]);
                }
                return Factory().AsNodeFactory().NewVariableStatement(modList2, Factory().AsNodeFactory().NewVariableDeclarationList(Factory().AsNodeFactory().NewNodeList([varDecl2]), NodeFlags.None));
            }
        }
        // const _exported: Type; export {_exported as "name"};
        var newId3 = Factory().NewUniqueNameEx("_exported", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
        state.GetSymbolAccessibilityDiagnostic = _ => new SymbolAccessibilityDiagnostic
        {
            DiagnosticMessage = global::Ts.Diagnostics.Diagnostics.Default_export_of_the_module_has_or_is_using_private_name_0,
            ErrorNode = input,
        };
        tracker.PushErrorFallbackNode(input);
        var type3 = EnsureType(input, false);
        var varDecl3 = Factory().AsNodeFactory().NewVariableDeclaration(newId3, null, type3, null);
        tracker.PopErrorFallbackNode();
        ModifierList modList3;
        if (needsDeclare)
        {
            modList3 = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword)]);
        }
        else
        {
            modList3 = Factory().AsNodeFactory().NewModifierList([]);
        }
        var statement3 = Factory().AsNodeFactory().NewVariableStatement(modList3, Factory().AsNodeFactory().NewVariableDeclarationList(Factory().AsNodeFactory().NewNodeList([varDecl3]), NodeFlags.Const));

        var assignment3 = Factory().AsNodeFactory().NewExportDeclaration(null, false, Factory().AsNodeFactory().NewNamedExports(Factory().AsNodeFactory().NewNodeList([Factory().AsNodeFactory().NewExportSpecifier(false, newId3, name)])), null, null);
        // Remove comments from the export declaration and copy them onto the synthetic _default declaration
        PreserveJsDoc(statement3, input);
        RemoveAllComments(assignment3);
        return Factory().AsNodeFactory().NewSyntaxList([statement3, assignment3]);
    }

    // transform.go:1489-1513 — wrapInCJSExportNamespace.
    private Node WrapInCJSExportNamespace(Node content)
    {
        if (cjsExportAssignmentName == null)
        {
            return content;
        }
        // Reuse the same name node so unique names resolve consistently with the class/export
        var nsName = cjsExportAssignmentName;
        Node[] members;
        if (content.Kind == SyntaxKind.SyntaxList)
        {
            members = content.AsSyntaxList().Children ?? [];
        }
        else
        {
            members = [content];
        }
        var nsMods = new List<Node>();
        if (needsDeclare)
        {
            nsMods.Add(Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword));
        }
        var (visitedMembers, _) = declareStrippingVisitor.VisitSlice(members);
        return Factory().AsNodeFactory().NewModuleDeclaration(
            Factory().AsNodeFactory().NewModifierList(nsMods.ToArray()),
            SyntaxKind.NamespaceKeyword,
            nsName,
            Factory().AsNodeFactory().NewModuleBlock(Factory().AsNodeFactory().NewNodeList(visitedMembers ?? [])));
    }

    // transform.go:1514-1525 — isCommonJSAliasExport.
    private static bool IsCommonJSAliasExport(Node node)
    {
        if (NodeTests.IsBinaryExpression(node) && NodeTests.IsIdentifier(node.AsBinaryExpression().Right!))
        {
            var symbol = node.Symbol();
            if (symbol != null && (symbol.Declarations?.Length ?? 0) == 1)
            {
                return true;
            }
        }
        return false;
    }

    // transform.go:1526-1552 — transformClassExpressionToDeclaration: converts a class expression into
    // a class declaration for use in CJS export declarations (e.g., exports.K = class K {} or
    // module.exports = class Thing {}). This delegates to the shared buildClassMembers helper to stay
    // in sync with transformClassDeclaration.
    private Node TransformClassExpressionToDeclaration(Node classExpr, Node className, ModifierList modifiers)
    {
        var previousEnclosingDeclaration = enclosingDeclaration;
        enclosingDeclaration = classExpr;
        var previousInClassExpressionDeclaration = inClassExpressionDeclaration;
        inClassExpressionDeclaration = true;
        try
        {
            Node[] extraMembers = [];
            if (AstUtilities.IsInJSFile(classExpr))
            {
                extraMembers = CollectThisPropertyAssignments(classExpr);
            }
            var members = BuildClassMembers(classExpr, extraMembers);
            var typeParameters = EnsureTypeParams(classExpr, classExpr.AsClassExpression().TypeParameters);
            var heritageClauses = Visitor().VisitNodes(classExpr.AsClassExpression().HeritageClauses);

            return Factory().AsNodeFactory().NewClassDeclaration(
                modifiers,
                className,
                typeParameters,
                heritageClauses,
                members);
        }
        finally
        {
            enclosingDeclaration = previousEnclosingDeclaration;
            inClassExpressionDeclaration = previousInClassExpressionDeclaration;
        }
    }

    // transform.go:2134-2156 — collectThisPropertyAssignments: finds `this.x = expr` assignments in
    // constructors, methods, and static blocks of JS classes and synthesizes PropertyDeclaration nodes
    // for each unique property name.
    private Node[] CollectThisPropertyAssignments(Node classNode)
    {
        var members = classNode.ClassLikeData()!.Members!;
        global::Ts.Collections.SetExtensions.Clear(seenProperties);
        // Pre-populate seen with existing direct member nodes to avoid duplicates
        foreach (var member in members.Nodes)
        {
            if (member.Name() != null)
            {
                var isStatic = AstUtilities.IsStatic(member);
                seenProperties.Add(ThisPropertyAssignmentKey.For(member.Name(), member, isStatic));
            }
        }
        thisPropertyAssignmentsCollected = new List<Node>();
        try
        {
            foreach (var n in members.Nodes)
            {
                thisPropertyVisitor.VisitEachChild(n);
            }
            return thisPropertyAssignmentsCollected.ToArray();
        }
        finally
        {
            global::Ts.Collections.SetExtensions.Clear(seenProperties);
            thisPropertyAssignmentsCollected = new List<Node>();
        }
    }

    // transform.go:2157-2177 — walkBindingPattern.
    private List<Node> WalkBindingPattern(BindingPattern pattern, Node param)
    {
        var elems = new List<Node>();
        foreach (var elem in pattern.Elements!.Nodes)
        {
            if (NodeTests.IsOmittedExpression(elem))
            {
                continue;
            }
            if (AstUtilities.IsBindingPattern(elem.Name()!))
            {
                elems.AddRange(WalkBindingPattern(elem.Name()!.AsBindingPattern(), param));
                continue;
            }
            elems.Add(Factory().AsNodeFactory().NewPropertyDeclaration(
                EnsureModifiers(param),
                elem.Name(),
                null, /*questionOrExclamationToken*/
                EnsureType(elem, false),
                null /*initializer*/));
        }
        return elems;
    }

    // transform.go:1889-1948 — buildClassMembers: builds the member list for a class-like node
    // (ClassDeclaration or ClassExpression). It handles parameter properties, private identifiers,
    // late-bound index signatures, and visited members. Extra members (e.g., this-property assignments
    // from JS files) can be passed via extraMembers.
    private NodeList BuildClassMembers(Node classNode, Node[] extraMembers)
    {
        var ctor = AstUtilities.GetFirstConstructorWithBody(classNode);
        var parameterProperties = new List<Node>();
        if (ctor != null)
        {
            var oldDiag = state.GetSymbolAccessibilityDiagnostic;
            foreach (var param in ctor.AsConstructorDeclaration().Parameters!.Nodes)
            {
                if (!AstUtilities.HasSyntacticModifier(param, ModifierFlags.ParameterPropertyModifier) || ShouldStripInternal(param))
                {
                    continue;
                }
                state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(param);
                if (param.Name()!.Kind == SyntaxKind.Identifier)
                {
                    var updated = Factory().AsNodeFactory().NewPropertyDeclaration(
                        EnsureModifiers(param),
                        param.Name(),
                        param.QuestionToken(),
                        EnsureType(param, false),
                        EnsureNoInitializer(param));
                    PreserveJsDoc(updated, param);
                    parameterProperties.Add(updated);
                }
                else
                {
                    // Pattern - this is currently an error, but we emit declarations for it somewhat correctly
                    parameterProperties.AddRange(WalkBindingPattern(param.Name()!.AsBindingPattern(), param));
                }
            }
            state.GetSymbolAccessibilityDiagnostic = oldDiag;
        }

        // When the class has at least one private identifier, create a unique constant identifier to retain the nominal typing behavior
        // Prevents other classes with the same public members from being used in place of the current class
        Node? privateIdentifier = null;
        if (CoreUtil.Some(classNode.ClassLikeData()!.Members!.Nodes, member =>
            member.Name() != null && NodeTests.IsPrivateIdentifier(member.Name()!)))
        {
            privateIdentifier = Factory().AsNodeFactory().NewPropertyDeclaration(null, Factory().NewPrivateIdentifier("#private"), null, null, null);
        }

        var lateIndexes = resolver!.CreateLateBoundIndexSignatures(
            EmitContext(),
            classNode,
            enclosingDeclaration!,
            DeclarationEmitNodeBuilderFlags,
            DeclarationEmitInternalNodeBuilderFlags,
            tracker);

        var memberNodes = new List<Node>(classNode.ClassLikeData()!.Members!.Nodes.Length);
        if (privateIdentifier != null)
        {
            memberNodes.Add(privateIdentifier);
        }
        memberNodes.AddRange(lateIndexes ?? []);
        memberNodes.AddRange(parameterProperties);
        memberNodes.AddRange(extraMembers);
        var visitResult = Visitor().VisitNodes(classNode.ClassLikeData()!.Members);
        if (visitResult is { Nodes.Length: > 0 })
        {
            memberNodes.AddRange(visitResult.Nodes);
        }
        return Factory().AsNodeFactory().NewNodeList(memberNodes.ToArray());
    }
    // transform.go:2547-2549 — transformJSDocTypeExpression.
    private Node? TransformJSDocTypeExpression(JSDocTypeExpression input) =>
        Visitor().VisitNode(input.Type);

    // transform.go:2551-2557 — transformJSDocTypeLiteral.
    private Node TransformJSDocTypeLiteral(JSDocTypeLiteral input)
    {
        var (members, _) = Visitor().VisitSlice(input.JSDocPropertyTags);
        var replacement = Factory().AsNodeFactory().NewTypeLiteralNode(Factory().AsNodeFactory().NewNodeList(members ?? []));
        EmitContext().SetOriginal(replacement, input.AsNode());
        return replacement;
    }

    // transform.go:2558-2569 — transformJSDocPropertyTag.
    private Node TransformJSDocPropertyTag(JSDocParameterOrPropertyTag input)
    {
        var replacement = Factory().AsNodeFactory().NewPropertySignatureDeclaration(
            null,
            Visitor().VisitNode(input.TagName),
            null,
            Visitor().VisitNode(input.TypeExpression),
            null);
        EmitContext().SetOriginal(replacement, input.AsNode());
        return replacement;
    }

    // transform.go:2570-2575 — transformJSDocAllType.
    private Node TransformJSDocAllType(JSDocAllType input)
    {
        var replacement = Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        EmitContext().SetOriginal(replacement, input.AsNode());
        return replacement;
    }

    // transform.go:2576-2584 — transformJSDocNullableType.
    private Node TransformJSDocNullableType(JSDocNullableType input)
    {
        var replacement = Factory().AsNodeFactory().NewUnionTypeNode(Factory().AsNodeFactory().NewNodeList([
            Visitor().VisitNode(input.Type)!,
            Factory().AsNodeFactory().NewLiteralTypeNode(Factory().AsNodeFactory().NewKeywordExpression(SyntaxKind.NullKeyword)),
        ]));
        EmitContext().SetOriginal(replacement, input.AsNode());
        return replacement;
    }

    // transform.go:2585-2588 — transformJSDocNonNullableType.
    private Node? TransformJSDocNonNullableType(JSDocNonNullableType input) =>
        Visitor().VisitNode(input.Type);

    // transform.go:2589-2594 — transformJSDocVariadicType.
    private Node TransformJSDocVariadicType(JSDocVariadicType input)
    {
        var replacement = Factory().AsNodeFactory().NewArrayTypeNode(Visitor().VisitNode(input.Type));
        EmitContext().SetOriginal(replacement, input.AsNode());
        return replacement;
    }

    // transform.go:2595-2603 — transformJSDocOptionalType.
    private Node TransformJSDocOptionalType(JSDocOptionalType input)
    {
        var replacement = Factory().AsNodeFactory().NewUnionTypeNode(Factory().AsNodeFactory().NewNodeList([
            Visitor().VisitNode(input.Type)!,
            Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.UndefinedKeyword),
        ]));
        EmitContext().SetOriginal(replacement, input.AsNode());
        return replacement;
    }

    // transform.go:2604-2623 — getNameExpressionPreferringIdentifier.
    private Node GetNameExpressionPreferringIdentifier(Node nameExpr)
    {
        if (NodeTests.IsNumericLiteral(nameExpr))
        {
            // Numeric property names are string properties in JS; convert to string literal
            nameExpr = Factory().NewStringLiteral(nameExpr.Text(), TokenFlags.None);
        }
        if (AstUtilities.IsStringLiteralLike(nameExpr) && global::Ts.Scanner.Scanner.IsIdentifierText(nameExpr.Text(), LanguageVariant.Standard))
        {
            var result = Factory().NewIdentifier(nameExpr.Text()); // prefer non-string literal names where possible
            var kwKind = global::Ts.Scanner.Scanner.IdentifierToKeywordKind(result.AsIdentifier());
            // keep keywords as strings, except `default`, which has special reformulations in the transformer
            if (kwKind == SyntaxKind.Unknown || kwKind == SyntaxKind.DefaultKeyword)
            {
                // fake this into a parse tree node so the reference resolver resolves the node via `resolveName`
                result.Parent = nameExpr.Parent;
                result.Flags &= ~NodeFlags.Synthesized;
                // intentionally leave Loc unset so the string isn't used as the text source of the identifier
                return result;
            }
        }
        return nameExpr;
    }

    // transform.go:2624-2626 — isNotDeclareModifier.
    private static bool IsNotDeclareModifier(Node mod) => mod.Kind != SyntaxKind.DeclareKeyword;

    // transform.go:276-278 — throwDiagnostic.
    private static SymbolAccessibilityDiagnostic ThrowDiagnostic(global::Ts.Printer.SymbolAccessibilityResult result) =>
        throw new InvalidOperationException("Diagnostic emitted without context");

    // ── 1:1 rebuild forward stubs (later batches replace these; unreachable until the new
    // visitSourceFile chain is wired) ──
    // transform.go:2403-2418 — visitBindingName.
    private Node? VisitBindingName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.OmittedExpression:
                return node;
            case SyntaxKind.ArrayBindingPattern:
            case SyntaxKind.ObjectBindingPattern:
                return node.VisitEachChild(bindingNameVisitor);
            case SyntaxKind.BindingElement:
                if (node.PropertyName() != null && NodeTests.IsComputedPropertyName(node.PropertyName()!) && AstUtilities.IsEntityNameExpression(node.PropertyName()!.Expression()!))
                {
                    CheckEntityNameVisibility(node.PropertyName()!.Expression()!, enclosingDeclaration!);
                }
                return Factory().AsNodeFactory().UpdateBindingElement(node.AsBindingElement(), node.AsBindingElement().DotDotDotToken, node.PropertyName(), bindingNameVisitor.VisitNode(node.Name()), null /*initializer*/);
            default:
                return node;
        }
    }
    // transform.go:2663-2689 — visitNestedExpression.
    private Node? VisitNestedExpression(Node? expression)
    {
        if (expression != null)
        {
            var (_, cleanupDiagnosticContext) = SetupDiagnosticContext(expression);
            try
            {
                switch (AstUtilities.GetAssignmentDeclarationKind(expression))
                {
                    case JSDeclarationKind.Property:
                        TransformExpandoAssignment(expression.AsBinaryExpression());
                        break;
                    case JSDeclarationKind.ExportsProperty:
                        if (state.CurrentSourceFile!.CommonJSModuleIndicator != null)
                        {
                            var result = TransformCommonJSExport(expression, GetNameExpressionPreferringIdentifier(AstUtilities.GetElementOrPropertyAccessName(expression.AsBinaryExpression().Left!)!));
                            if (result != null)
                            {
                                cjsExportMembers.Add(result);
                            }
                        }
                        break;
                    case JSDeclarationKind.ObjectDefinePropertyExports:
                        if (state.CurrentSourceFile!.CommonJSModuleIndicator != null)
                        {
                            var result = TransformCommonJSExport(expression, GetNameExpressionPreferringIdentifier(expression.Arguments()![1]));
                            if (result != null)
                            {
                                cjsExportMembers.Add(result);
                            }
                        }
                        break;
                }
                return expressionVisitor.VisitEachChild(expression); // recur through the whole tree, looking for special assignments
            }
            finally
            {
                cleanupDiagnosticContext();
            }
        }
        return null;
    }
    // transform.go:1866-1888 — stripExportModifiers.
    private Node? StripExportModifiers(Node? statement)
    {
        if (statement == null)
        {
            return null;
        }
        var parseNode = EmitContext().ParseNode(statement);
        if (NodeTests.IsImportEqualsDeclaration(statement) || (parseNode != null && host!.GetEffectiveDeclarationFlags(parseNode, ModifierFlags.Default) != 0) || !global::Ts.Checker.Checker.CanHaveModifiers(statement))
        {
            // `export import` statements should remain as-is, as imports are _not_ implicitly exported in an ambient namespace
            // Likewise, `export default` classes and the like and just be `default`, so we preserve their `export` modifiers, too
            return statement;
        }

        var oldFlags = AstUtilities.GetCombinedModifierFlags(statement);
        if ((oldFlags & ModifierFlags.Export) == 0)
        {
            return statement;
        }
        var newFlags = oldFlags & (ModifierFlags.All ^ ModifierFlags.Export);
        var modifiers = AstUtilities.CreateModifiersFromModifierFlags(newFlags, Factory().AsNodeFactory().NewModifier);
        return AstUtilities.ReplaceModifiers(Factory().AsNodeFactory(), statement, Factory().AsNodeFactory().NewModifierList(modifiers));
    }
    // transform.go:2043-2111 — visitThisPropertyAssignments.
    private Node? VisitThisPropertyAssignments(Node node)
    {
        var isStatic = false;
        var thisContainer = AstUtilities.GetThisContainer(node, false, false);
        var thisTarget = thisContainer.Parent;
        if (thisTarget == null)
        {
            return null; // thisContainer was source file, can't have expando-this
        }
        if (AstUtilities.HasStaticModifier(thisContainer) || NodeTests.IsClassStaticBlockDeclaration(thisContainer))
        {
            isStatic = true;
        }
        if (thisTarget != enclosingDeclaration)
        {
            return null; // stop searching within new `this` contexts
        }
        if (AstUtilities.GetAssignmentDeclarationKind(node) == JSDeclarationKind.ThisProperty)
        {
            while (true) // Go labeled-break emulation (`break caseBlock`)
            {
                var name = AstUtilities.GetNameOfDeclaration(node);
                var @base = resolver!.GetReferencedMemberValueDeclaration(node);
                var key = ThisPropertyAssignmentKey.For(name, node, isStatic);
                if (@base == null || global::Ts.Collections.SetExtensions.Has(seenProperties, key))
                {
                    break;
                }
                seenProperties.Add(key);

                // problem: this prop might be overriding a prop from a base type. The checker has special bails for override compat comparisons for binary expression properties,
                // but what we transform to won't - so we either need to match the base type (for example, if it's a getter/setter) or emit nothing
                // See `checkKindsOfPropertyMemberOverrides` in the checker for what we're trying to satisfy here
                if (thisTarget.ClassLikeData()!.HeritageClauses is { Nodes.Length: > 0 } && !IsClassExtendingNull(thisTarget))
                {
                    // there is a base type any assignments might be "from"
                    tracker.ReportInferenceFallback(thisTarget); // Add an isolated declarations error on this class - we can't know how to transform this prop into an assignment without referring to type information
                    if (resolver.IsThisPropertyAssignmentDeclarationRedundant(node))
                    {
                        break; // skip assignments whose member is already provided by an `extends` base type (an inherited accessor/method, or an identical inherited property)
                        // TODO: If the property has an explicit `@type` annotation, we should probably emit it (maybe with an `override` modifier) instead of skipping it
                    }
                }

                ModifierList? mods = null;
                if (isStatic)
                {
                    mods = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.StaticKeyword)]);
                }
                if (AstUtilities.HasDynamicName(node))
                {
                    if (!global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(name!))
                    {
                        break; // Member either becomes an index signature or is a reassignment
                    }
                    CheckName(node);
                    name = Factory().AsNodeFactory().NewComputedPropertyName(name); // Convert `this[foo] = expr` to `[foo]: Type`
                }
                if (AstUtilities.GetTextOfPropertyName(name!) == "constructor")
                {
                    break; // `constructor` is a builtin class member, not allowed to redeclare it
                }
                if (NodeTests.IsIdentifier(name!) && !global::Ts.Scanner.Scanner.IsIdentifierText(name!.Text(), LanguageVariant.Standard))
                {
                    name = Factory().NewStringLiteralFromNode(name!);
                }
                var prop = Factory().AsNodeFactory().NewPropertyDeclaration(
                    mods,
                    name,
                    null,
                    EnsureType(node, false),
                    null);
                if (NodeTests.IsExpressionStatement(node.Parent!))
                {
                    PreserveJsDoc(prop, node.Parent!);
                }
                thisPropertyAssignmentsCollected.Add(prop);
                break;
            }
        }
        return thisPropertyVisitor.VisitEachChild(node);
    }

    // transform.go:2112-2131 — isClassExtendingNull.
    private static bool IsClassExtendingNull(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        var heritage = node.ClassLikeData()!.HeritageClauses;
        if (heritage == null)
        {
            return false;
        }
        if (heritage.Nodes.Length > 1 || heritage.Nodes.Length == 0)
        {
            return false;
        }
        foreach (var expA in heritage.Nodes[0].AsHeritageClause().Types!.Nodes)
        {
            var expr = expA.AsExpressionWithTypeArguments().Expression;
            if (expr != null && expr.Kind == SyntaxKind.NullKeyword)
            {
                return true;
            }
        }
        return false;
    }
    // transform.go:2643-2662 — visitCJSExportAssignments.
    private Node? VisitCJSExportAssignments(Node? expression)
    {
        if (expression != null)
        {
            var (_, cleanupDiagnosticContext) = SetupDiagnosticContext(expression);
            try
            {
                if (AstUtilities.GetAssignmentDeclarationKind(expression) == JSDeclarationKind.ModuleExports)
                {
                    if (state.CurrentSourceFile!.CommonJSModuleIndicator != null)
                    {
                        var result = TransformExportAssignment(expression.Parent!, expression, expression.AsBinaryExpression().Right!, true /*isExportEquals*/);
                        if (result != null)
                        {
                            cjsExportAssignment = result;
                            resultHasScopeMarker = true;
                            resultHasExternalModuleIndicator = true;
                        }
                    }
                }
                return cjsExportAssignmentVisitor.VisitEachChild(expression); // recur through the whole tree, looking for module.exports=
            }
            finally
            {
                cleanupDiagnosticContext();
            }
        }
        return null;
    }
    // transform.go:2628-2642 — stripDeclareModifiers.
    private Node? StripDeclareModifiers(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        var mods = node.Modifiers();
        if (mods != null)
        {
            var flags = node.ModifierFlags();
            if ((flags & ModifierFlags.Ambient) != 0)
            {
                var filtered = CoreUtil.Filter(mods.Nodes, IsNotDeclareModifier) ?? [];
                node.SetModifiers(Factory().AsNodeFactory().NewModifierList(filtered));
            }
        }
        return node; // no need to recur into children, only strip at top-level
    }

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

    // transform.go:226-274 — visit: functions as both `visitDeclarationStatements` and
    // `transformRoot`, utilizing SyntaxList nodes.
    private Node? Visit(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                return VisitSourceFile(node.AsSourceFile());
            // statements we keep but do something to
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ExportAssignment:
                return VisitDeclarationStatements(node);
            // statements we elide
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
            // parts of things, things we just visit children of
            default:
                return VisitDeclarationSubtree(node);
        }
    }

    // transform.go:280-309 — visitSourceFile.
    private Node VisitSourceFile(SourceFile node)
    {
        cjsExportAssignmentName = null;
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }

        needsDeclare = true;
        needsScopeFixMarker = false;
        resultHasScopeMarker = false;
        enclosingDeclaration = node.AsNode();
        state.GetSymbolAccessibilityDiagnostic = ThrowDiagnostic;
        resultHasExternalModuleIndicator = false;
        suppressNewDiagnosticContexts = false;
        state.LateMarkedStatements.Clear();
        lateStatementReplacementMap = new Dictionary<NodeId, Node?>();
        expandoHosts = new Dictionary<NodeId, Node>();
        expandoMembers = new Dictionary<NodeId, List<Node>>();
        deferredExpandoAssignments = new Dictionary<NodeId, List<BinaryExpression>>();
        rawReferencedFiles = new List<ReferencedFilePair>();
        rawTypeReferenceDirectives = new List<FileReference>();
        rawLibReferenceDirectives = new List<FileReference>();
        global::Ts.Collections.SetExtensions.Clear(witnessedCjsExports);
        state.CurrentSourceFile = node;
        CollectFileReferences(node);
        resolver!.PrecalculateDeclarationEmitVisibility(EmitContext().MostOriginal(node.AsNode())!.AsSourceFile());
        var updated = TransformSourceFileWorker(node);
        state.CurrentSourceFile = null;
        return updated;
    }

    // transform.go:341-381 — transformSourceFile. (Named *Worker: the C# Transformer base already
    // exposes the public TransformSourceFile entry point.)
    private Node TransformSourceFileWorker(SourceFile node)
    {
        cjsExportAssignment = null;
        cjsExportAssignmentName = null;
        cjsExportMembers = new List<Node>();
        try
        {
            cjsExportAssignmentVisitor.VisitNode(node.AsNode()); // collect nested module.exports= assignments
            expressionVisitor.VisitNode(node.AsNode());          // collect expando members (requires any export assignment be located in advance)
            var statements = Visitor().VisitNodes(node.Statements)!;
            var combinedStatements = TransformAndReplaceLatePaintedStatements(statements);
            combinedStatements = AppendCjsExports(combinedStatements);
            combinedStatements.Loc = statements.Loc; // setTextRange
            if (AstUtilities.IsExternalOrCommonJSModule(node))
            {
                if (AstUtilities.IsInJSFile(node.AsNode()))
                {
                    var exportEquals = node.Symbol?.Exports?[InternalSymbolName.ExportEquals];
                    if (exportEquals != null && (exportEquals.Declarations?.Length ?? 0) > 1)
                    {
                        foreach (var decl in exportEquals.Declarations!)
                        {
                            state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(decl, global::Ts.Diagnostics.Diagnostics.Multiple_module_exports_assignments_cannot_be_serialized_for_declaration_emit));
                        }
                    }
                }
                if (!resultHasExternalModuleIndicator || (needsScopeFixMarker && !resultHasScopeMarker))
                {
                    var marker = CreateEmptyExports(Factory().AsNodeFactory());
                    var newList = new List<Node>(combinedStatements.Nodes) { marker };
                    var withMarker = Factory().AsNodeFactory().NewNodeList(newList.ToArray());
                    withMarker.Loc = combinedStatements.Loc;
                    combinedStatements = withMarker;
                }
            }
            var outputFilePath = global::Ts.TsPath.TsPath.GetDirectoryPath(global::Ts.TsPath.TsPath.NormalizeSlashes(declarationFilePath));
            var result = Factory().AsNodeFactory().UpdateSourceFile(node, combinedStatements, node.EndOfFileToken);
            result.AsSourceFile().LibReferenceDirectives = GetLibReferences();
            result.AsSourceFile().TypeReferenceDirectives = GetTypeReferences();
            result.AsSourceFile().IsDeclarationFile = true;
            result.AsSourceFile().ReferencedFiles = GetReferencedFiles(outputFilePath);
            return result;
        }
        finally
        {
            cjsExportAssignment = null;
            cjsExportAssignmentName = null;
            cjsExportMembers = new List<Node>();
        }
    }

    // transform.go:1754-1764 — transformTypeAliasDeclaration (needsDeclare is restored by
    // transformTopLevelDeclaration).
    private Node TransformTypeAliasDeclaration(TypeAliasDeclaration input)
    {
        needsDeclare = false;
        return Factory().AsNodeFactory().UpdateTypeAliasDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNode(input.Type));
    }

    // transform.go:1765-1775 — transformInterfaceDeclaration.
    private Node TransformInterfaceDeclaration(InterfaceDeclaration input)
    {
        return Factory().AsNodeFactory().UpdateInterfaceDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNodes(input.HeritageClauses),
            Visitor().VisitNodes(input.Members));
    }

    // transform.go:1776-1792 — transformFunctionDeclaration.
    private Node TransformFunctionDeclaration(FunctionDeclaration input)
    {
        if (resolver!.IsExpandoFunctionDeclaration(input.AsNode()))
        {
            state.ReportExpandoFunctionErrors?.Invoke(input.AsNode());
        }
        return Factory().AsNodeFactory().UpdateFunctionDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            input.Name(),
            EnsureTypeParams(input.AsNode(), input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null, /*fullSignature*/
            null);
    }

    // transform.go:1949-2042 — transformClassDeclaration.
    private Node TransformClassDeclaration(ClassDeclaration input)
    {
        var previousEnclosingDeclaration = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        state.ErrorNameNode = input.Name();
        tracker.PushErrorFallbackNode(input.AsNode());
        try
        {
            var modifiers = EnsureModifiers(input.AsNode());
            var typeParameters = EnsureTypeParams(input.AsNode(), input.TypeParameters);

            // Collect this.x property assignments from constructors and static blocks in JS files
            Node[] extraMembers = [];
            if (AstUtilities.IsInJSFile(input.AsNode()))
            {
                extraMembers = CollectThisPropertyAssignments(input.AsNode());
            }

            var members = BuildClassMembers(input.AsNode(), extraMembers);

            var extendsClause = DeclarationUtilities.GetEffectiveBaseTypeNode(input.AsNode());

            if (extendsClause != null && !AstUtilities.IsEntityNameExpression(extendsClause.AsExpressionWithTypeArguments().Expression!) && extendsClause.AsExpressionWithTypeArguments().Expression!.Kind != SyntaxKind.NullKeyword)
            {
                tracker.ReportInferenceFallback(extendsClause.AsExpressionWithTypeArguments().Expression!); // Add an isolated declarations error on this extends clause
                var oldId = "default";
                if (input.Name() != null && NodeTests.IsIdentifier(input.Name()!) && input.Name()!.Text().Length > 0)
                {
                    oldId = input.Name()!.Text();
                }
                var newId = Factory().NewUniqueNameEx(oldId + "_base", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
                state.GetSymbolAccessibilityDiagnostic = _ => new SymbolAccessibilityDiagnostic
                {
                    DiagnosticMessage = global::Ts.Diagnostics.Diagnostics.X_extends_clause_of_exported_class_0_has_or_is_using_private_name_1,
                    ErrorNode = extendsClause,
                    TypeName = input.Name(),
                };

                var varDecl = Factory().AsNodeFactory().NewVariableDeclaration(
                    newId,
                    null,
                    resolver!.CreateTypeOfExpression(EmitContext(), extendsClause.Expression()!, input.AsNode(), DeclarationEmitNodeBuilderFlags, DeclarationEmitInternalNodeBuilderFlags, tracker),
                    null);
                ModifierList? mods = null;
                if (needsDeclare)
                {
                    mods = Factory().AsNodeFactory().NewModifierList([Factory().AsNodeFactory().NewModifier(SyntaxKind.DeclareKeyword)]);
                }
                var statement = Factory().AsNodeFactory().NewVariableStatement(
                    mods,
                    Factory().AsNodeFactory().NewVariableDeclarationList(Factory().AsNodeFactory().NewNodeList([varDecl]), NodeFlags.Const));
                var newHeritageClause = Factory().AsNodeFactory().UpdateHeritageClause(
                    extendsClause.Parent!.AsHeritageClause(),
                    extendsClause.Parent!.AsHeritageClause().Token,
                    Factory().AsNodeFactory().NewNodeList([
                        Factory().UpdateExpressionWithTypeArguments(
                            extendsClause.AsExpressionWithTypeArguments(),
                            newId,
                            Visitor().VisitNodes(extendsClause.AsExpressionWithTypeArguments().TypeArguments)),
                    ]));
                var retainedHeritageClauses = Visitor().VisitNodes(input.HeritageClauses); // should just be `implements`
                var heritageList = new List<Node> { newHeritageClause };
                if (retainedHeritageClauses is { Nodes.Length: > 0 })
                {
                    heritageList.AddRange(retainedHeritageClauses.Nodes);
                }
                var heritageClauses = Factory().AsNodeFactory().NewNodeList(heritageList.ToArray());

                return Factory().AsNodeFactory().NewSyntaxList([
                    statement,
                    Factory().UpdateClassDeclaration(
                        input,
                        modifiers,
                        input.Name(),
                        typeParameters,
                        heritageClauses,
                        members),
                ]);
            }

            return Factory().UpdateClassDeclaration(
                input,
                modifiers,
                input.Name(),
                typeParameters,
                Visitor().VisitNodes(input.HeritageClauses),
                members);
        }
        finally
        {
            tracker.PopErrorFallbackNode();
            enclosingDeclaration = previousEnclosingDeclaration;
        }
    }

    // transform.go:1793-1865 — transformModuleDeclaration.
    // !!! TODO: module declarations are now parsed into nested module objects with export modifiers
    // It'd be good to collapse those back in the declaration output, but the AST can't represent the
    // `namespace a.b.c` shape for the printer (without using invalid identifier names).
    private Node TransformModuleDeclaration(ModuleDeclaration input)
    {
        var mods = EnsureModifiers(input.AsNode());
        var saveNeedsDeclare = needsDeclare;
        needsDeclare = false;
        var inner = input.Body;
        var keyword = input.Keyword;
        if (keyword != SyntaxKind.GlobalKeyword && (input.Name() == null || !NodeTests.IsStringLiteral(input.Name()!)))
        {
            keyword = SyntaxKind.NamespaceKeyword;
        }

        if (inner != null && inner.Kind == SyntaxKind.ModuleBlock)
        {
            var oldNeedsScopeFix = needsScopeFixMarker;
            var oldHasScopeFix = resultHasScopeMarker;
            resultHasScopeMarker = false;
            needsScopeFixMarker = false;
            var statements = Visitor().VisitNodes(inner.StatementList())!;
            var lateStatements = TransformAndReplaceLatePaintedStatements(statements);
            if ((input.Flags & NodeFlags.Ambient) != 0)
            {
                needsScopeFixMarker = false; // If it was `declare`'d everything is implicitly exported already, ignore late printed "privates"
            }
            // With the final list of statements, there are 3 possibilities:
            // 1. There's an export assignment or export declaration in the namespace - do nothing
            // 2. Everything is exported and there are no export assignments or export declarations - strip all export modifiers
            // 3. Some things are exported, some are not, and there's no marker - add an empty marker
            if (!AstUtilities.IsGlobalScopeAugmentation(input.AsNode()) && !resultHasScopeMarker && !DeclarationUtilities.HasScopeMarker(lateStatements))
            {
                if (needsScopeFixMarker)
                {
                    var withMarker = new List<Node>(lateStatements.Nodes) { CreateEmptyExports(Factory().AsNodeFactory()) };
                    lateStatements = Factory().AsNodeFactory().NewNodeList(withMarker.ToArray());
                }
                else
                {
                    lateStatements = exportStrippingVisitor.VisitNodes(lateStatements)!;
                }
            }

            var body = Factory().AsNodeFactory().UpdateModuleBlock(inner.AsModuleBlock(), lateStatements);
            needsDeclare = saveNeedsDeclare;
            needsScopeFixMarker = oldNeedsScopeFix;
            resultHasScopeMarker = oldHasScopeFix;

            return Factory().AsNodeFactory().UpdateModuleDeclaration(
                input,
                mods,
                keyword,
                input.Name(),
                body);
        }
        if (inner != null)
        {
            // trigger visit. ignore result (is deferred, so is just inner unless elided)
            Visitor().VisitNode(inner);
            // eagerly transform nested namespaces (the nesting doesn't need any elision or painting done)
            var original = EmitContext().MostOriginal(inner)!;
            var id = AstUtilities.GetNodeId(original);
            lateStatementReplacementMap.TryGetValue(id, out var body);
            lateStatementReplacementMap.Remove(id);
            return Factory().AsNodeFactory().UpdateModuleDeclaration(
                input,
                mods,
                keyword,
                input.Name(),
                body);
        }
        return Factory().AsNodeFactory().UpdateModuleDeclaration(
            input,
            mods,
            keyword,
            input.Name(),
            null);
    }

    // transform.go:2178-2232 — transformVariableStatement.
    private Node? TransformVariableStatement(VariableStatement input)
    {
        var visible = false;
        foreach (var decl in input.DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
        {
            visible = DeclarationUtilities.GetBindingNameVisible(resolver!, decl);
            if (visible)
            {
                break;
            }
        }
        if (!visible)
        {
            return null;
        }

        var inputNodes = input.DeclarationList.AsVariableDeclarationList().Declarations!.Nodes;
        Node[] extraImports = [];
        if (state.CurrentSourceFile!.CommonJSModuleIndicator != null)
        {
            var normalDeclarations = new List<Node>();
            var imports = new List<Node>();
            foreach (var n in inputNodes)
            {
                if (AstUtilities.IsVariableDeclarationInitializedToRequire(n))
                {
                    imports.Add(n);
                }
                else
                {
                    normalDeclarations.Add(n);
                }
            }
            inputNodes = normalDeclarations.ToArray();
            var (visitedImports, _) = Visitor().VisitSlice(imports.ToArray());
            extraImports = visitedImports ?? [];
        }

        var (nodes, _) = Visitor().VisitSlice(inputNodes);
        if (nodes == null || nodes.Length == 0)
        {
            if (extraImports.Length > 0)
            {
                return Factory().AsNodeFactory().NewSyntaxList(extraImports);
            }
            return null;
        }
        var nodeList = Factory().AsNodeFactory().NewNodeList(nodes);

        var modifiers = EnsureModifiers(input.AsNode());

        Node declList;
        if (global::Ts.Printer.PrinterUtilities.IsVarUsing(input.DeclarationList) || AstUtilities.IsVarAwaitUsing(input.DeclarationList))
        {
            declList = Factory().AsNodeFactory().NewVariableDeclarationList(nodeList, NodeFlags.Const);
            EmitContext().SetOriginal(declList, input.DeclarationList);
            EmitContext().SetCommentRange(declList, input.DeclarationList.Loc);
            declList.Loc = input.DeclarationList.Loc;
        }
        else
        {
            declList = Factory().AsNodeFactory().UpdateVariableDeclarationList(input.DeclarationList.AsVariableDeclarationList(), nodeList, input.DeclarationList.Flags);
        }
        var res = Factory().AsNodeFactory().UpdateVariableStatement(input, modifiers, declList);
        if (extraImports.Length > 0)
        {
            var combined = new List<Node>(extraImports) { res };
            return Factory().AsNodeFactory().NewSyntaxList(combined.ToArray());
        }
        return res;
    }

    // transform.go:835-852 — transformVariableDeclaration.
    private Node? TransformVariableDeclaration(VariableDeclaration input)
    {
        if (state.CurrentSourceFile!.CommonJSModuleIndicator != null && AstUtilities.IsVariableDeclarationInitializedToRequire(input.AsNode()))
        {
            return TransformCjsRequireVariableDeclaration(input);
        }
        if (AstUtilities.IsBindingPattern(input.Name()!))
        {
            return RecreateBindingPattern(input.Name()!.AsBindingPattern());
        }
        // Variable declaration types also suppress new diagnostic contexts, provided the contexts wouldn't be made for binding pattern types
        suppressNewDiagnosticContexts = true;
        return Factory().AsNodeFactory().UpdateVariableDeclaration(
            input,
            input.Name(),
            null,
            EnsureType(input.AsNode(), false),
            EnsureNoInitializer(input.AsNode()));
    }

    // transform.go:2233-2283 — transformEnumDeclaration.
    private Node TransformEnumDeclaration(EnumDeclaration input)
    {
        return Factory().AsNodeFactory().UpdateEnumDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Factory().AsNodeFactory().NewNodeList(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<Node>(CoreUtil.MapNonNil(input.Members!.Nodes, Node? (m) =>
            {
                if (ShouldStripInternal(m))
                {
                    return null;
                }

                // Rewrite enum values to their constants, if available
                var enumValue = resolver!.GetEnumMemberValue(m);

                if (state.IsolatedDeclarations && m.Initializer() != null && enumValue.HasExternalReferences &&
                    // This will be its own compiler error instead, so don't report.
                    !NodeTests.IsComputedPropertyName(m.Name()!))
                {
                    state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(m, global::Ts.Diagnostics.Diagnostics.Enum_member_initializers_must_be_computable_without_references_to_external_symbols_with_isolatedDeclarations));
                }

                Node? newInitializer = null;
                if (enumValue.Value is global::Ts.JsNum.Number value)
                {
                    if (value.IsInf())
                    {
                        if (value.Value > 0)
                        {
                            newInitializer = Factory().NewIdentifier("Infinity");
                        }
                        else
                        {
                            newInitializer = Factory().AsNodeFactory().NewPrefixUnaryExpression(SyntaxKind.MinusToken, Factory().NewIdentifier("Infinity"));
                        }
                    }
                    else if (value.IsNaN())
                    {
                        newInitializer = Factory().NewIdentifier("NaN");
                    }
                    else if (value.Value >= 0)
                    {
                        newInitializer = Factory().AsNodeFactory().NewNumericLiteral(value.ToString(), TokenFlags.None);
                    }
                    else
                    {
                        newInitializer = Factory().AsNodeFactory().NewPrefixUnaryExpression(
                            SyntaxKind.MinusToken,
                            Factory().AsNodeFactory().NewNumericLiteral(new global::Ts.JsNum.Number(-value.Value).ToString(), TokenFlags.None));
                    }
                }
                else if (enumValue.Value is string strValue)
                {
                    newInitializer = Factory().NewStringLiteral(strValue, TokenFlags.None);
                }
                var result = Factory().AsNodeFactory().UpdateEnumMember(m.AsEnumMember(), m.Name(), newInitializer);
                PreserveJsDoc(result, m);
                return result;
            })!))));
    }

    // transform.go:963-981 — transformPropertyDeclaration.
    private Node? TransformPropertyDeclaration(PropertyDeclaration input)
    {
        if (NodeTests.IsPrivateIdentifier(input.Name()!))
        {
            return null;
        }
        // Remove definite assignment assertion (!) from declaration files
        var postfixToken = input.PostfixToken;
        if (postfixToken != null && postfixToken.Kind == SyntaxKind.ExclamationToken)
        {
            postfixToken = null;
        }
        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            postfixToken,
            EnsureType(input.AsNode(), false),
            EnsureNoInitializer(input.AsNode()));
    }

    // transform.go:947-962 — transformPropertySignatureDeclaration.
    private Node? TransformPropertySignatureDeclaration(PropertySignatureDeclaration input)
    {
        if (NodeTests.IsPrivateIdentifier(input.Name()!))
        {
            return null;
        }
        var result = Factory().AsNodeFactory().UpdatePropertySignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            input.PostfixToken,
            EnsureType(input.AsNode(), false),
            EnsureNoInitializer(input.AsNode())); // TODO: possible strada bug (fixed here) - const property signatures never initialized
        PreservePartialJsDoc(result, input.AsNode());
        return result;
    }

    // transform.go:1107-1127 — transformMethodDeclaration.
    private Node? TransformMethodDeclaration(MethodDeclaration input)
    {
        if (host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(input.AsNode())!, ModifierFlags.Private) != 0)
        {
            return OmitPrivateMethodType(input.AsNode());
        }
        else if (NodeTests.IsPrivateIdentifier(input.Name()!))
        {
            return null;
        }
        else
        {
            return Factory().AsNodeFactory().UpdateMethodDeclaration(
                input,
                EnsureModifiers(input.AsNode()),
                null,
                input.Name(),
                input.PostfixToken,
                EnsureTypeParams(input.AsNode(), input.TypeParameters),
                UpdateParameterList(input.AsNode(), input.Parameters),
                EnsureType(input.AsNode(), false),
                null,
                null);
        }
    }

    // transform.go:1089-1106 — transformMethodSignatureDeclaration.
    private Node? TransformMethodSignatureDeclaration(MethodSignatureDeclaration input)
    {
        if (host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(input.AsNode())!, ModifierFlags.Private) != 0)
        {
            return OmitPrivateMethodType(input.AsNode());
        }
        else if (NodeTests.IsPrivateIdentifier(input.Name()!))
        {
            return null;
        }
        else
        {
            return Factory().AsNodeFactory().UpdateMethodSignatureDeclaration(
                input,
                EnsureModifiers(input.AsNode()),
                input.Name(),
                input.PostfixToken,
                EnsureTypeParams(input.AsNode(), input.TypeParameters),
                UpdateParameterList(input.AsNode(), input.Parameters),
                EnsureType(input.AsNode(), false));
        }
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

    // transform.go:999-1014 — transformGetAccesorDeclaration.
    private Node? TransformGetAccessorDeclaration(GetAccessorDeclaration input)
    {
        if (NodeTests.IsPrivateIdentifier(input.Name()!))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            null, // accessors shouldn't have type params
            UpdateAccessorParamList(input.AsNode(), host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(input.AsNode())!, ModifierFlags.Private) != 0),
            EnsureType(input.AsNode(), false),
            null,
            null);
    }

    // transform.go:982-998 — transformSetAccessorDeclaration.
    private Node? TransformSetAccessorDeclaration(SetAccessorDeclaration input)
    {
        if (NodeTests.IsPrivateIdentifier(input.Name()!))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            null, // accessors shouldn't have type params
            UpdateAccessorParamList(input.AsNode(), host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(input.AsNode())!, ModifierFlags.Private) != 0),
            null,
            null,
            null);
    }

    // transform.go:938-946 — transformCallSignatureDeclaration.
    private Node TransformCallSignatureDeclaration(CallSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateCallSignatureDeclaration(
            input,
            EnsureTypeParams(input.AsNode(), input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    // transform.go:1065-1073 — transformConstructSignatureDeclaration.
    private Node TransformConstructSignatureDeclaration(ConstructSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateConstructSignatureDeclaration(
            input,
            EnsureTypeParams(input.AsNode(), input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    // transform.go:925-937 — transformIndexSignatureDeclaration.
    private Node TransformIndexSignatureDeclaration(IndexSignatureDeclaration input)
    {
        var t = Visitor().VisitNode(input.Type);
        if (t == null)
        {
            t = Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        return Factory().AsNodeFactory().UpdateIndexSignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            UpdateParameterList(input.AsNode(), input.Parameters),
            t);
    }

    // transform.go:2355-2365 — updateParamList.
    private ParameterList UpdateParameterList(Node node, ParameterList? @params)
    {
        if (host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(node)!, ModifierFlags.Private) != 0 || @params == null || @params.Nodes.Length == 0)
        {
            return Factory().AsNodeFactory().NewNodeList([]);
        }
        var results = new Node[@params.Nodes.Length];
        for (var i = 0; i < @params.Nodes.Length; i++)
        {
            results[i] = EnsureParameter(@params.Nodes[i].AsParameterDeclaration());
        }
        return Factory().AsNodeFactory().NewNodeList(results);
    }

    // transform.go:2366-2391 — ensureParameter.
    private Node EnsureParameter(ParameterDeclaration p)
    {
        var oldDiag = state.GetSymbolAccessibilityDiagnostic;
        if (!suppressNewDiagnosticContexts)
        {
            state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(p.AsNode());
        }
        Node? questionToken = null;
        if (resolver!.IsOptionalParameter(p.AsNode()))
        {
            if (p.QuestionToken != null)
            {
                questionToken = p.QuestionToken;
            }
            else
            {
                questionToken = Factory().NewToken(SyntaxKind.QuestionToken);
            }
        }
        var result = Factory().AsNodeFactory().UpdateParameterDeclaration(
            p,
            null,
            p.DotDotDotToken,
            bindingNameVisitor.VisitNode(p.Name()),
            questionToken,
            EnsureType(p.AsNode(), true),
            EnsureNoInitializer(p.AsNode()));
        state.GetSymbolAccessibilityDiagnostic = oldDiag;
        return result;
    }

    // transform.go:1600-1663 — ensureType.
    private Node? EnsureType(Node node, bool ignorePrivate)
    {
        if (!ignorePrivate && host!.GetEffectiveDeclarationFlags(EmitContext().ParseNode(node)!, ModifierFlags.Private) != 0)
        {
            // Private nodes emit no types (except private parameter properties, whose parameter types are actually visible)
            return null;
        }

        if (ShouldPrintWithInitializer(node))
        {
            // Literal const declarations will have an initializer ensured rather than a type
            return null;
        }

        // Should be removed createTypeOfDeclaration will actually now reuse the existing annotation so there is no real need to duplicate type walking
        // Left in for now to minimize diff during syntactic type node builder refactor
        if (!NodeTests.IsExportAssignment(node) && !NodeTests.IsBindingElement(node) && node.Type() != null && (!NodeTests.IsParameterDeclaration(node) || !resolver!.RequiresAddingImplicitUndefined(node, null, enclosingDeclaration!)))
        {
            if (state.CurrentSourceFile!.IsJS())
            {
                // JS types have a heap of constructs we can't directly emit into .d.ts files; the node builder contains logic to remap those where possible, so we invoke it here
                // In strada we always built js declarations symbolically, so all js type nodes went through this postprocessing
                var jsFlags = DeclarationEmitNodeBuilderFlags;
                if (inClassExpressionDeclaration)
                {
                    jsFlags &= ~global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral;
                }
                var res = resolver!.TryJSTypeNodeToTypeNode(EmitContext(), node.Type()!, enclosingDeclaration!, jsFlags, DeclarationEmitInternalNodeBuilderFlags, tracker);
                if (res != null)
                {
                    return res;
                }
                // otherwise, fall back to full serialization
            }
            else
            {
                return Visitor().VisitNode(node.Type());
            }
        }

        var oldErrorNameNode = state.ErrorNameNode;
        state.ErrorNameNode = node.Name();
        GetSymbolAccessibilityDiagnostic? oldDiag = null;
        if (!suppressNewDiagnosticContexts)
        {
            oldDiag = state.GetSymbolAccessibilityDiagnostic;
            if (DeclarationUtilities.CanProduceDiagnostics(node))
            {
                state.GetSymbolAccessibilityDiagnostic = DeclarationDiagnostics.CreateGetSymbolAccessibilityDiagnosticForNode(node);
            }
        }
        Node? typeNode;

        var flags = DeclarationEmitNodeBuilderFlags;
        if (inClassExpressionDeclaration)
        {
            flags &= ~global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral;
        }
        if (AstUtilities.HasInferredType(node))
        {
            typeNode = resolver!.CreateTypeOfDeclaration(EmitContext(), node, enclosingDeclaration!, flags, DeclarationEmitInternalNodeBuilderFlags, tracker);
        }
        else if (AstUtilities.IsFunctionLike(node))
        {
            typeNode = resolver!.CreateReturnTypeOfSignatureDeclaration(EmitContext(), node, enclosingDeclaration!, flags, DeclarationEmitInternalNodeBuilderFlags, tracker);
        }
        else
        {
            throw new InvalidOperationException("ensureType: unexpected node kind " + node.Kind);
        }

        state.ErrorNameNode = oldErrorNameNode;
        if (!suppressNewDiagnosticContexts)
        {
            state.GetSymbolAccessibilityDiagnostic = oldDiag;
        }
        if (typeNode == null)
        {
            return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        return typeNode;
    }

    // transform.go:2284-2303 — ensureModifiers.
    private ModifierList? EnsureModifiers(Node node)
    {
        var currentFlags = AstUtilities.GetCombinedModifierFlags(EmitContext().ParseNode(node)!) & ModifierFlags.All;
        var newFlags = EnsureModifierFlags(node);
        if (currentFlags == newFlags)
        {
            // Elide decorators
            var mods = node.Modifiers();
            if (mods == null)
            {
                return mods;
            }
            if (DeclarationUtilities.CanReuseModifierNodes(mods.Nodes))
            {
                return Factory().AsNodeFactory().NewModifierList(CoreUtil.Filter(mods.Nodes, AstUtilities.IsModifier) ?? []);
            }
        }
        var result = AstUtilities.CreateModifiersFromModifierFlags(newFlags, Factory().AsNodeFactory().NewModifier);
        if (result.Length == 0)
        {
            return null;
        }
        return Factory().AsNodeFactory().NewModifierList(result);
    }

    // transform.go:2304-2320 — ensureModifierFlags.
    private ModifierFlags EnsureModifierFlags(Node node)
    {
        var mask = ModifierFlags.All ^ (ModifierFlags.Public | ModifierFlags.Async | ModifierFlags.Override); // No async and override modifiers in declaration files
        var additions = ModifierFlags.None;
        if (needsDeclare && !DeclarationUtilities.IsAlwaysType(node))
        {
            additions = ModifierFlags.Ambient;
        }
        var parentIsFile = node.Parent!.Kind == SyntaxKind.SourceFile;
        if (!parentIsFile)
        {
            mask ^= ModifierFlags.Ambient;
            additions = ModifierFlags.None;
        }
        if (AstUtilities.IsImplicitlyExportedJSDocDeclaration(node))
        {
            additions |= ModifierFlags.Export;
        }
        return DeclarationUtilities.MaskModifierFlags(node, mask, additions);
    }

    // These constants document the exact node-builder contract consumed by the deferred resolver path.
    internal static global::Ts.NodeBuilder.Flags NodeBuilderFlags => DeclarationEmitNodeBuilderFlags;
    internal static global::Ts.NodeBuilder.InternalFlags InternalNodeBuilderFlags =>
        DeclarationEmitInternalNodeBuilderFlags;
}
