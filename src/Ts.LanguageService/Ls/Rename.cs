// Ported from tsgo internal/ls/rename.go.
//
// PORT-NOTE (subset shape): the Go entry points take LSP line/character positions and
// ride the full references engine (handleCrossProject → symbolAndEntriesToRename with
// per-symbol search state on checker.GetSymbolAtLocation). The C# LS foundation's
// position currency is the raw UTF-16 offset (see LanguageService.GetNodeAtPosition),
// checker.GetSymbolAtLocation is still an internal throwing stub, and the cross-project
// orchestrator belongs to the deferred project layer. This port therefore keeps the Go
// pipeline shape — eligibility → rename-info validation → reference entries → text
// edits — but the symbol channel is the public emit-resolver declaration identity used
// by FindAllReferences.cs/DocumentHighlights.cs, and the binder symbol of the resolved
// declaration stands in for the checker symbol. Deferred deep surfaces are single
// stubs marked with their Go names below. Positions are UTF-16 code units throughout.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using PrepareRenamePlaceholder = Ts.LsProto.PrepareRenamePlaceholder;
using TextEdit = Ts.LsProto.TextEdit;
using WorkspaceEdit = Ts.LsProto.WorkspaceEdit;

namespace Ts.Ls;

// rename.go:24 — RenameInfo represents the result of a rename validation check.
// It is used by the `textDocument/prepareRename` LSP handler.
public sealed class RenameInfo
{
    public bool CanRename { get; set; }
    public string LocalizedErrorMessage { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public global::Ts.LsProto.Range? TriggerSpan { get; set; }
    public string FileToRename { get; set; } = "";
    public string NewFileName { get; set; } = "";
}

public sealed partial class LanguageService
{
    // rename.go:136 — l.UserPreferences().AllowRenameOfImportPath gate. The preferences
    // carrier is deferred; the Go default (TSUnknown, i.e. not TSTrue) keeps module
    // rename disengaged. Property (not const) so the guarded arm stays compiled.
    private static bool AllowRenameOfImportPath => false;

    // rename.go:47 — GetRenameInfo.
    public RenameInfo GetRenameInfo(
        string fileName,
        int position,
        string newName = "",
        CancellationToken cancellationToken = default)
    {
        var node = GetNodeAtPosition(fileName, position, out var sourceFile);
        node = LsDeps.GetAdjustedLocation(node, true /*forRename*/, sourceFile);

        if (NodeIsEligibleForRename(node))
        {
            var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, sourceFile);
            try
            {
                var renameInfo = GetRenameInfoForNode(newName, node, sourceFile, checker.GetEmitResolver(), checker);
                if (renameInfo != null)
                {
                    return renameInfo;
                }
            }
            finally
            {
                done();
            }
        }
        return GetRenameInfoError(global::Ts.Diagnostics.Diagnostics.You_cannot_rename_this_element);
    }

    // lsp/server.go:1373 — handlePrepareRename response shaping. The Go handler turns a
    // non-renameable position into a user-facing request error; the C# subset returns
    // null (the LSP null response arm).
    public PrepareRenamePlaceholder? PrepareRename(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var info = GetRenameInfo(fileName, position, "" /*newName*/, cancellationToken);
        if (!info.CanRename)
        {
            return null;
        }
        return new PrepareRenamePlaceholder
        {
            Range = info.TriggerSpan!,
            Placeholder = info.DisplayName,
        };
    }

    // rename.go:33 ProvideRename / :62 symbolAndEntriesToRename, fused per the header
    // note: the reference entries come from the FindAllReferences foundation subset
    // (identifier use-sites whose emit-resolver declaration matches the queried node's)
    // instead of the deep per-symbol search state.
    public WorkspaceEdit? ProvideRename(
        string fileName,
        int position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var originalNode = GetNodeAtPosition(fileName, position, out var sourceFile);
        originalNode = LsDeps.GetAdjustedLocation(originalNode, true /*forRename*/, sourceFile);

        // rename.go:63 — eligibility gate.
        if (!NodeIsEligibleForRename(originalNode))
        {
            return null;
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, sourceFile);
        try
        {
            var emitResolver = checker.GetEmitResolver();

            // rename.go:69 — defense-in-depth: validate rename eligibility even if the
            // client skipped prepareRename.
            var info = GetRenameInfoForNode(newName, originalNode, sourceFile, emitResolver, checker);
            if (info == null || !info.CanRename)
            {
                return null;
            }

            if (originalNode.Kind != SyntaxKind.Identifier && originalNode.Kind != SyntaxKind.PrivateIdentifier)
            {
                // Go dispatches the special reference families (labels, this, string
                // literals) through getReferencedSymbolsForNode. Deferred with the
                // FindAllReferences deep path.
                return null;
            }
            var targetDeclaration = GetReferencedDeclaration(emitResolver, originalNode);
            if (targetDeclaration is null)
            {
                return null;
            }

            var quotePreference = LsDeps.GetQuotePreference(sourceFile);
            // rename.go:83 — UserPreferences().UseAliasesForRename.IsTrueOrUnknown();
            // the preferences carrier is deferred, so the Go default (unknown) holds.
            const bool useAliasesForRename = true;

            var searchName = originalNode.Text();
            var changes = new Dictionary<global::Ts.LsProto.DocumentUri, List<TextEdit>>();
            // Go iterates the request's sourceFiles slice in program order; positions
            // within a file ascend, so the edit order is deterministic.
            foreach (var file in program.SourceFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var reference in GetPossibleSymbolReferenceNodes(file, searchName, null))
                {
                    if (!IsValidReferencePosition(reference, searchName))
                    {
                        continue;
                    }
                    if (!ReferenceEquals(GetReferencedDeclaration(emitResolver, reference), targetDeclaration))
                    {
                        continue;
                    }
                    // rename.go:87 — skip module-specifier string entries unless
                    // AllowRenameOfImportPath (deferred preference, default false).
                    if (!AllowRenameOfImportPath &&
                        AstUtilities.IsStringLiteralLike(reference) &&
                        LsDeps.TryGetImportFromModuleSpecifier(reference) != null)
                    {
                        continue;
                    }
                    var uri = LsDeps.FileNameToDocumentURI(file.FileName());
                    var textEdit = new TextEdit
                    {
                        Range = LsDeps.ToLspRange(file, GetRangeOfNode(reference, file, null)),
                        NewText = GetTextForRename(originalNode, reference, newName, emitResolver, quotePreference, useAliasesForRename),
                    };
                    if (!changes.TryGetValue(uri, out var edits))
                    {
                        edits = new List<TextEdit>();
                        changes[uri] = edits;
                    }
                    edits.Add(textEdit);
                }
            }

            var changesArrays = new Dictionary<global::Ts.LsProto.DocumentUri, TextEdit[]>(changes.Count);
            foreach (var (uri, edits) in changes) // map-iter: 序无关（JSON object 键序无语义；值按文件独立成组）
            {
                changesArrays[uri] = edits.ToArray();
            }
            return new WorkspaceEdit
            {
                Changes = changesArrays,
            };
        }
        finally
        {
            done();
        }
    }

    // rename.go:104 — getRenameInfoForNode performs detailed validation for a rename
    // operation on a specific node. Go returns (RenameInfo, false) for "not handled";
    // the C# mapping is a null return. PORT-NOTE: the Go symbol channel is
    // checker.GetSymbolAtLocation; it is still an internal throwing stub, so identifiers
    // resolve through the emit-resolver declaration channel (see FindAllReferences.cs)
    // and the binder symbol of the resolved declaration stands in for the checker symbol.
    private RenameInfo? GetRenameInfoForNode(
        string newName,
        Node node,
        SourceFile sourceFile,
        global::Ts.Checker.EmitResolver emitResolver,
        global::Ts.Checker.Checker checker)
    {
        global::Ts.Ast.Symbol? symbol = null;
        if (node.Kind is SyntaxKind.Identifier or SyntaxKind.PrivateIdentifier)
        {
            symbol = GetReferencedDeclaration(emitResolver, node)?.Symbol();
        }
        if (symbol == null)
        {
            if (AstUtilities.IsStringLiteralLike(node))
            {
                // Allow renaming of string literal types with contextual string literal types.
                var typ = FindRefsSpecial.GetContextualTypeFromParentOrAncestorTypeNode(node, checker);
                if (typ != null && (typ.IsStringLiteral() ||
                    (typ.IsUnion() && global::Ts.Core.CoreUtil.Every(typ.Types(), t => t.IsStringLiteral()))))
                {
                    return GetRenameInfoSuccess(node, sourceFile, node.Text());
                }
            }
            else if (LsDeps.IsLabelName(node))
            {
                var name = node.Text();
                return GetRenameInfoSuccess(node, sourceFile, name);
            }
            return null;
        }

        // Only allow a symbol to be renamed if it actually has at least one declaration.
        if ((symbol.Declarations?.Length ?? 0) == 0)
        {
            return null;
        }

        var msg = RenameBlockedReason(sourceFile, node, symbol);
        if (msg != null)
        {
            return GetRenameInfoError(msg);
        }

        if (AstUtilities.IsStringLiteralLike(node) && LsDeps.TryGetImportFromModuleSpecifier(node) != null)
        {
            if (AllowRenameOfImportPath)
            {
                return GetRenameInfoForModule(newName, node, sourceFile, symbol);
            }
            return null;
        }

        // Go: ch.SymbolToString(symbol); the checker printer surface is internal to
        // Ts.Compiler, and for every symbol reachable on this subset path the binder
        // name is the printed name.
        return GetRenameInfoSuccess(node, sourceFile, symbol.Name);
    }

    // rename.go:145 — nodeIsEligibleForRename.
    private static bool NodeIsEligibleForRename(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.ThisKeyword:
                return true;
            case SyntaxKind.NumericLiteral:
                return LsDeps.IsLiteralNameOfPropertyDeclarationOrIndexAccess(node);
            default:
                return false;
        }
    }

    // rename.go:162 — renameBlockedReason returns a non-nil diagnostic message if the
    // rename should be blocked because the symbol is a library definition, a default
    // keyword, or would cross node_modules boundaries.
    private global::Ts.Diagnostics.Message? RenameBlockedReason(
        SourceFile sourceFile,
        Node node,
        global::Ts.Ast.Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (IsDefinedInLibraryFile(declaration))
            {
                return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_elements_that_are_defined_in_the_standard_TypeScript_library;
            }
        }

        // Cannot rename `default` as in `import { default as foo } from "./someModule"`
        if (NodeTests.IsIdentifier(node) &&
            node.Text() == "default" &&
            symbol.Parent != null &&
            (symbol.Parent.Flags & SymbolFlags.Module) != 0)
        {
            return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_this_element;
        }

        return WouldRenameInOtherNodeModules(sourceFile, symbol);
    }

    // rename.go:182 — isDefinedInLibraryFile checks if a declaration is from a default
    // library file (e.g., lib.d.ts).
    private bool IsDefinedInLibraryFile(Node declaration)
    {
        var declSourceFile = AstUtilities.GetSourceFileOfNode(declaration)!; // Go dereferences unconditionally.
        return program.IsSourceFileDefaultLibrary(declSourceFile.Path()) &&
            global::Ts.TsPath.TsPath.IsDeclarationFileName(declSourceFile.FileName());
    }

    // rename.go:188 — wouldRenameInOtherNodeModules checks if renaming the symbol would
    // affect node_modules. PORT-NOTE: the preferences carrier is deferred, so
    // UseAliasesForRename.IsTrueOrUnknown() holds and the Go alias-unwrapping branch
    // (ch.GetAliasedSymbol behind !IsTrueOrUnknown) drops out.
    private static global::Ts.Diagnostics.Message? WouldRenameInOtherNodeModules(
        SourceFile originalFile,
        global::Ts.Ast.Symbol symbol)
    {
        var declarations = symbol.Declarations;
        if (declarations == null || declarations.Length == 0)
        {
            return null;
        }

        var originalPackage = global::Ts.Module.Module.ParseNodeModuleFromPath(originalFile.FileName(), false /*isFolder*/);
        if (originalPackage.Length == 0)
        {
            // Original source file is not in node_modules.
            foreach (var declaration in declarations)
            {
                if (LsDeps.IsInsideNodeModules(AstUtilities.GetSourceFileOfNode(declaration)!.FileName()))
                {
                    return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_elements_that_are_defined_in_a_node_modules_folder;
                }
            }
            return null;
        }

        // Original source file is in node_modules.
        foreach (var declaration in declarations)
        {
            var declPackage = global::Ts.Module.Module.ParseNodeModuleFromPath(
                AstUtilities.GetSourceFileOfNode(declaration)!.FileName(),
                false /*isFolder*/);
            if (declPackage.Length != 0 && declPackage != originalPackage)
            {
                return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_elements_that_are_defined_in_another_node_modules_folder;
            }
        }
        return null;
    }

    // rename.go:223-233 — client-capability gates. Go reads lsproto.GetClientCapabilities(ctx);
    // the C# carrier is the resolved capability set (the server passes its own copy, the
    // language service passes LanguageService.ClientCapabilities()).
    public static bool ClientSupportsWillRenameFiles(global::Ts.LsProto.ResolvedClientCapabilities caps)
    {
        return caps.Workspace.FileOperations.WillRename;
    }

    public static bool ClientSupportsDocumentChanges(global::Ts.LsProto.ResolvedClientCapabilities caps)
    {
        return caps.Workspace.WorkspaceEdit.DocumentChanges;
    }

    public static bool ClientSupportsRenameResourceOperations(global::Ts.LsProto.ResolvedClientCapabilities caps)
    {
        return System.Array.IndexOf(caps.Workspace.WorkspaceEdit.ResourceOperations ?? [], global::Ts.LsProto.ResourceOperationKind.Rename) >= 0;
    }

    // rename.go:236-276 — getRenameInfoForModule handles rename validation for module
    // specifiers.
    private RenameInfo? GetRenameInfoForModule(
        string newName,
        Node specifier,
        SourceFile sourceFile,
        global::Ts.Ast.Symbol moduleSymbol)
    {
        if (!global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(specifier.Text()))
        {
            return GetRenameInfoError(global::Ts.Diagnostics.Diagnostics.You_cannot_rename_a_module_via_a_global_import);
        }
        if (!ClientSupportsDocumentChanges(ClientCapabilities()) || !ClientSupportsRenameResourceOperations(ClientCapabilities()))
        {
            return GetRenameInfoError(global::Ts.Diagnostics.Diagnostics.File_rename_is_not_supported_by_the_editor);
        }

        Node? moduleSourceFile = null;
        foreach (var decl in moduleSymbol.Declarations ?? [])
        {
            if (NodeTests.IsSourceFile(decl))
            {
                moduleSourceFile = decl;
                break;
            }
        }
        if (moduleSourceFile == null)
        {
            return null;
        }

        var fileName = moduleSourceFile.AsSourceFile().FileName();
        var withoutIndex = "";
        var specifierText = specifier.Text();
        if (!specifierText.EndsWith("/index", StringComparison.Ordinal) && !specifierText.EndsWith("/index.js", StringComparison.Ordinal))
        {
            var candidate = global::Ts.TsPath.TsPath.RemoveFileExtension(fileName);
            if (candidate.EndsWith("/index", StringComparison.Ordinal))
            {
                withoutIndex = candidate[..^"/index".Length];
            }
        }

        var displayName = fileName;
        if (withoutIndex.Length != 0)
        {
            displayName = withoutIndex;
        }
        var newFileName = GetNewFileNameForModuleRename(displayName, specifierText, newName);

        // Span should only be the last component of the path. + 1 to account for the quote character.
        var indexAfterLastSlash = specifierText.LastIndexOf('/') + 1;
        var start = specifier.Pos + 1 + indexAfterLastSlash;
        var length = specifierText.Length - indexAfterLastSlash;

        return new RenameInfo
        {
            CanRename = true,
            DisplayName = specifierText[indexAfterLastSlash..],
            TriggerSpan = LsDeps.ToLspRange(sourceFile, new global::Ts.Core.TextRange(start, start + length)),
            FileToRename = displayName,
            NewFileName = newFileName,
        };
    }

    // rename.go:278-295 — getNewFileNameForModuleRename. Adjust the new name based on the
    // old path that an import specifier resolves to. For example, if specifier "a.js"
    // resolves to file a.ts, renaming "a.js" -> "b.js" should mean file rename a.ts -> b.ts.
    private string GetNewFileNameForModuleRename(string oldPath, string specifierText, string newName)
    {
        var newPath = global::Ts.TsPath.TsPath.CombinePaths(global::Ts.TsPath.TsPath.GetDirectoryPath(oldPath), newName);
        var ignoreCase = !UseCaseSensitiveFileNames();
        string oldExt;
        if (global::Ts.TsPath.TsPath.IsDeclarationFileName(oldPath))
        {
            oldExt = global::Ts.TsPath.TsPath.GetDeclarationFileExtension(oldPath);
        }
        else
        {
            oldExt = global::Ts.TsPath.TsPath.GetAnyExtensionFromPath(oldPath, null /*extensions*/, ignoreCase);
        }
        if (!global::Ts.TsPath.TsPath.HasExtension(newPath))
        {
            newPath = newPath + oldExt;
        }
        else if (global::Ts.TsPath.TsPath.GetAnyExtensionFromPath(newPath, null /*extensions*/, ignoreCase) == global::Ts.TsPath.TsPath.GetAnyExtensionFromPath(specifierText, null /*extensions*/, ignoreCase))
        {
            newPath = global::Ts.TsPath.TsPath.ChangeAnyExtension(newPath, oldExt, null /*extensions*/, ignoreCase);
        }
        return newPath;
    }

    // rename.go:297 — getTextForRename. PORT-NOTE: the subset reference engine produces
    // plain node entries, so the Go arms keyed on entryKindSearchedLocalFoundProperty /
    // entryKindSearchedPropertyFoundLocal (deep per-symbol search state) and the
    // entryKindRange guards collapse; the checker symbol comparisons
    // (GetSymbolAtLocation / GetExportSpecifierLocalTargetSymbol) ride the
    // emit-resolver declaration channel.
    private static string GetTextForRename(
        Node originalNode,
        Node entryNode,
        string newText,
        global::Ts.Checker.EmitResolver emitResolver,
        QuotePreference quotePreference,
        bool useAliasesForRename)
    {
        if (useAliasesForRename && (NodeTests.IsIdentifier(originalNode) || AstUtilities.IsStringLiteralLike(originalNode)))
        {
            var node = LsDeps.GetReparsedNodeForNode(entryNode);
            var parent = node.Parent!; // Go dereferences unconditionally.
            var name = originalNode.Text();
            var isShorthandAssignment = NodeTests.IsShorthandPropertyAssignment(parent);
            if (isShorthandAssignment ||
                (LsDeps.IsObjectBindingElementWithoutPropertyName(parent) &&
                    parent.Name() == node &&
                    parent.AsBindingElement().DotDotDotToken == null))
            {
                // In `const o = { x }; o.x`, symbolAtLocation at `x` in `{ x }` is the property symbol.
                // For a binding element `const { x } = o;`, symbolAtLocation at `x` is the property symbol.
                if (isShorthandAssignment)
                {
                    var grandParent = parent.Parent!;
                    if (NodeTests.IsObjectLiteralExpression(grandParent) &&
                        NodeTests.IsBinaryExpression(grandParent.Parent!) &&
                        AstUtilities.IsModuleExportsAccessExpression(grandParent.Parent!.AsBinaryExpression().Left!))
                    {
                        return name + ": " + newText;
                    }
                    return newText + ": " + name;
                }
                return name + ": " + newText;
            }
            if (NodeTests.IsImportSpecifier(parent) && parent.PropertyName() == null)
            {
                // If the original symbol was using this alias, just rename the alias.
                var originalSymbol = GetReferencedDeclaration(emitResolver, originalNode)?.Symbol();
                if (originalSymbol?.Declarations != null)
                {
                    foreach (var declaration in originalSymbol.Declarations)
                    {
                        if (declaration == parent)
                        {
                            return name + " as " + newText;
                        }
                    }
                }
                return newText;
            }
            if (NodeTests.IsExportSpecifier(parent) && parent.PropertyName() == null)
            {
                // If the symbol for the node is same as declared node symbol use prefix text
                if (originalNode == entryNode ||
                    ReferenceEquals(
                        GetReferencedDeclaration(emitResolver, originalNode),
                        GetReferencedDeclaration(emitResolver, entryNode)))
                {
                    return name + " as " + newText;
                }
                return newText + " as " + name;
            }
        }

        // If the node is a numerical indexing literal, then add quotes around the property access.
        if (NodeTests.IsNumericLiteral(entryNode) && AstUtilities.IsAccessExpression(entryNode.Parent!))
        {
            var quote = GetQuoteFromPreference(quotePreference);
            return quote + newText + quote;
        }

        return newText;
    }

    // rename.go:352 — getQuoteFromPreference.
    private static string GetQuoteFromPreference(QuotePreference quotePreference)
    {
        if (quotePreference == QuotePreference.Single)
        {
            return "'";
        }
        return "\"";
    }

    // rename.go:359 — getRenameInfoError. locale.FromContext(ctx) collapses to the
    // default locale until the request-context locale carrier lands.
    private static RenameInfo GetRenameInfoError(global::Ts.Diagnostics.Message message)
    {
        return new RenameInfo
        {
            CanRename = false,
            LocalizedErrorMessage = global::Ts.Diagnostics.Diagnostics.Localize(
                global::Ts.Locale.Locale.Default, message, message.Key),
        };
    }

    // rename.go:366 — getRenameInfoSuccess.
    private static RenameInfo GetRenameInfoSuccess(Node node, SourceFile sourceFile, string displayName)
    {
        var start = global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false /*includeJSDoc*/);
        var end = node.End;
        if (AstUtilities.IsStringLiteralLike(node))
        {
            // Exclude the quotes
            start++;
            end--;
        }
        return new RenameInfo
        {
            CanRename = true,
            DisplayName = displayName,
            TriggerSpan = LsDeps.ToLspRange(sourceFile, new global::Ts.Core.TextRange(start, end)),
        };
    }
}
