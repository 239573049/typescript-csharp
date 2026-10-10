// Ported from tsgo internal/ls/rename.go (1:1).
//
// ProvideRename (:33) rides the cross-project orchestrator exactly as Go does —
// handleCrossProject → symbolAndEntriesToRename → combineRenameResponse — on top of the deep
// find-all-references engine (FindAllReferences*.cs), and reads its settings from
// LanguageService.UserPreferences().
//
// Position currency: GetRenameInfo (:47) is the LSP-currency entry (DocumentUri + lsproto
// Position, converted through l.converters); symbolAndEntriesToRename and getRangeOfEntry
// work on the reference entries' own LSP ranges. The raw UTF-16 offset currency stays behind
// LanguageService.ProvideSymbolsAndEntries.
//
// Go's (Resp, error) pairs collapse to response values: every arm here returns a nil error in
// Go. Go's (RenameInfo, bool) "handled" channel becomes RenameInfo? (null = not handled).
//
// PORT-NOTE: Go reads the client capabilities per request via lsproto.GetClientCapabilities(ctx);
// the C# carrier is the resolved capability set, so ClientSupportsWillRenameFiles /
// ClientSupportsDocumentChanges / ClientSupportsRenameResourceOperations take it as a parameter.
// The LSP server passes its own copy (Server.cs), this file passes LanguageService.ClientCapabilities().

using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerFindRefsLsExports extension methods
using Ts.Diagnostics; // Message.Localize extension method
using Ts.Core; // Tristate.IsTrue / IsTrueOrUnknown extension methods
using RequestContext = Ts.Core.RequestContext;
using WorkspaceEdit = Ts.LsProto.WorkspaceEdit;
using WorkspaceEditOrNull = Ts.LsProto.WorkspaceEditOrNull;

namespace Ts.Ls;

// rename.go:24-31 — RenameInfo represents the result of a rename validation check.
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
    // rename.go:33-45 — ProvideRename.
    public WorkspaceEditOrNull ProvideRename(
        RequestContext ctx,
        global::Ts.LsProto.RenameParams @params,
        CrossProjectOrchestrator? orchestrator)
    {
        return CrossProject.HandleCrossProject(
            this,
            ctx.Cancellation,
            @params,
            orchestrator,
            // Go's symbolAndEntriesToRename receives the full context (locale + cancellation);
            // the C# transformer delegate carries the token only, so the request context is
            // captured here and the pump's token is folded back into it.
            (l, c, req, data, options) => l.SymbolAndEntriesToRename(ctx with { Cancellation = c }, req, data, options),
            CrossProject.CombineRenameResponse,
            true, /*isRename*/
            false, /*implementations*/
            new SymbolEntryTransformOptions());
    }

    // rename.go:47-60 — GetRenameInfo.
    public RenameInfo GetRenameInfo(
        RequestContext ctx,
        string newName,
        global::Ts.LsProto.DocumentUri documentURI,
        global::Ts.LsProto.Position position)
    {
        var sourceFile = GetProgramAndFile(documentURI);
        var pos = ToPosition(sourceFile, position);

        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, pos);
        node = LsDeps.GetAdjustedLocation(node, true /*forRename*/, sourceFile);

        if (NodeIsEligibleForRename(node))
        {
            var renameInfo = GetRenameInfoForNode(ctx, newName, node, sourceFile, GetProgram());
            if (renameInfo != null)
            {
                return renameInfo;
            }
        }
        return GetRenameInfoError(ctx.Locale, global::Ts.Diagnostics.Diagnostics.You_cannot_rename_this_element);
    }

    // rename.go:62-101 — symbolAndEntriesToRename.
    internal WorkspaceEditOrNull SymbolAndEntriesToRename(
        RequestContext ctx,
        global::Ts.LsProto.RenameParams @params,
        SymbolAndEntriesData data,
        SymbolEntryTransformOptions options)
    {
        if (!NodeIsEligibleForRename(data.OriginalNode!))
        {
            return new WorkspaceEditOrNull();
        }

        var program = GetProgram();

        // Defense-in-depth: validate rename eligibility even if the client skipped prepareRename.
        // Use getRenameInfoForNode directly with the already-resolved node to avoid
        // re-resolving the position and polluting state baselines.
        var sourceFile = AstUtilities.GetSourceFileOfNode(data.OriginalNode!)!;
        var info = GetRenameInfoForNode(ctx, @params.NewName, data.OriginalNode!, sourceFile, program);
        if (info == null || !info.CanRename)
        {
            return new WorkspaceEditOrNull();
        }

        var entries = new List<ReferenceEntry>();
        foreach (var s in data.SymbolsAndEntries)
        {
            entries.AddRange(s.References);
        }
        var changes = new Dictionary<global::Ts.LsProto.DocumentUri, List<global::Ts.LsProto.TextEdit>>();
        var (checker, done) = program.GetTypeChecker(ctx.Cancellation);
        try
        {
            var quotePreference = LsDeps.GetQuotePreference(sourceFile, UserPreferences());
            var useAliasesForRename = UserPreferences().UseAliasesForRename.IsTrueOrUnknown();

            foreach (var entry in entries)
            {
                var uri = GetFileNameOfEntry(entry);
                if (UserPreferences().AllowRenameOfImportPath != Tristate.True && entry.Node != null && AstUtilities.IsStringLiteralLike(entry.Node) && LsDeps.TryGetImportFromModuleSpecifier(entry.Node) != null)
                {
                    continue;
                }
                var textEdit = new global::Ts.LsProto.TextEdit
                {
                    Range = GetRangeOfEntry(entry),
                    NewText = GetTextForRename(data.OriginalNode!, entry, @params.NewName, checker, quotePreference, useAliasesForRename),
                };
                if (!changes.TryGetValue(uri, out var editsForUri))
                {
                    editsForUri = [];
                    changes[uri] = editsForUri;
                }
                editsForUri.Add(textEdit);
            }

            var changesArrays = new Dictionary<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.TextEdit[]>(changes.Count);
            foreach (var (uri, edits) in changes) // map-iter: 序无关（JSON object 键序无语义；值按文件独立成组）
            {
                changesArrays[uri] = edits.ToArray();
            }
            return WorkspaceEditOrNull.FromWorkspaceEdit(new WorkspaceEdit
            {
                Changes = changesArrays,
            });
        }
        finally
        {
            done();
        }
    }

    // rename.go:103-143 — getRenameInfoForNode performs detailed validation for a rename
    // operation on a specific node. Go returns (RenameInfo, false) for "not handled"; the C#
    // mapping is a null return.
    private RenameInfo? GetRenameInfoForNode(
        RequestContext ctx,
        string newName,
        Node node,
        SourceFile sourceFile,
        global::Ts.Compiler.Program program)
    {
        var (checker, done) = program.GetTypeChecker(ctx.Cancellation);
        try
        {
            var symbol = checker.GetSymbolAtLocation(node);
            if (symbol == null)
            {
                if (AstUtilities.IsStringLiteralLike(node))
                {
                    // Allow renaming of string literal types with contextual string literal types
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

            var msg = RenameBlockedReason(sourceFile, node, symbol, checker, program);
            if (msg != null)
            {
                return GetRenameInfoError(ctx.Locale, msg);
            }

            if (AstUtilities.IsStringLiteralLike(node) && LsDeps.TryGetImportFromModuleSpecifier(node) != null)
            {
                if (UserPreferences().AllowRenameOfImportPath.IsTrue())
                {
                    return GetRenameInfoForModule(ctx, newName, node, sourceFile, symbol);
                }
                return null;
            }

            return GetRenameInfoSuccess(node, sourceFile, checker.SymbolToString(symbol));
        }
        finally
        {
            done();
        }
    }

    // rename.go:145-158 — nodeIsEligibleForRename. Owned by this file (the Go canonical home);
    // FindAllReferences.cs reaches it through the shared LanguageService type.
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

    // rename.go:160-179 — renameBlockedReason returns a non-nil diagnostic message if the
    // rename should be blocked because the symbol is a library definition, a default keyword,
    // or would cross node_modules boundaries.
    private global::Ts.Diagnostics.Message? RenameBlockedReason(
        SourceFile sourceFile,
        Node node,
        global::Ts.Ast.Symbol symbol,
        global::Ts.Checker.Checker checker,
        global::Ts.Compiler.Program program)
    {
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (IsDefinedInLibraryFile(program, declaration))
            {
                return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_elements_that_are_defined_in_the_standard_TypeScript_library;
            }
        }

        // Cannot rename `default` as in `import { default as foo } from "./someModule"`
        if (NodeTests.IsIdentifier(node) && node.Text() == "default" && symbol.Parent != null && (symbol.Parent.Flags & SymbolFlags.Module) != 0)
        {
            return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_this_element;
        }

        var msg = WouldRenameInOtherNodeModules(sourceFile, symbol, checker, UserPreferences());
        if (msg != null)
        {
            return msg;
        }

        return null;
    }

    // rename.go:181-185 — isDefinedInLibraryFile checks if a declaration is from a default
    // library file (e.g., lib.d.ts).
    private static bool IsDefinedInLibraryFile(global::Ts.Compiler.Program program, Node declaration)
    {
        var declSourceFile = AstUtilities.GetSourceFileOfNode(declaration)!; // Go dereferences unconditionally.
        return program.IsSourceFileDefaultLibrary(declSourceFile.Path()) &&
            global::Ts.TsPath.TsPath.IsDeclarationFileName(declSourceFile.FileName());
    }

    // rename.go:187-221 — wouldRenameInOtherNodeModules checks if renaming the symbol would
    // affect node_modules.
    private static global::Ts.Diagnostics.Message? WouldRenameInOtherNodeModules(
        SourceFile originalFile,
        global::Ts.Ast.Symbol symbol,
        global::Ts.Checker.Checker ch,
        UserPreferences preferences)
    {
        var sym = symbol;
        if (!preferences.UseAliasesForRename.IsTrueOrUnknown() && (sym.Flags & SymbolFlags.Alias) != 0)
        {
            var importSpecifier = global::Ts.Core.CoreUtil.Find(sym.Declarations, NodeTests.IsImportSpecifier);
            if (importSpecifier != null && importSpecifier.AsImportSpecifier().PropertyName == null)
            {
                sym = ch.GetAliasedSymbol(sym)!;
            }
        }

        var declarations = sym.Declarations;
        if ((declarations?.Length ?? 0) == 0)
        {
            return null;
        }

        var originalPackage = global::Ts.Module.Module.ParseNodeModuleFromPath(originalFile.FileName(), false /*isFolder*/);
        if (originalPackage.Length == 0)
        {
            // Original source file is not in node_modules.
            foreach (var declaration in declarations!)
            {
                if (LsDeps.IsInsideNodeModules(AstUtilities.GetSourceFileOfNode(declaration)!.FileName()))
                {
                    return global::Ts.Diagnostics.Diagnostics.You_cannot_rename_elements_that_are_defined_in_a_node_modules_folder;
                }
            }
            return null;
        }

        // Original source file is in node_modules.
        foreach (var declaration in declarations!)
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

    // rename.go:235-276 — getRenameInfoForModule handles rename validation for module
    // specifiers.
    private RenameInfo? GetRenameInfoForModule(
        RequestContext ctx,
        string newName,
        Node specifier,
        SourceFile sourceFile,
        global::Ts.Ast.Symbol moduleSymbol)
    {
        if (!global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(specifier.Text()))
        {
            return GetRenameInfoError(ctx.Locale, global::Ts.Diagnostics.Diagnostics.You_cannot_rename_a_module_via_a_global_import);
        }
        if (!ClientSupportsDocumentChanges(ClientCapabilities()) || !ClientSupportsRenameResourceOperations(ClientCapabilities()))
        {
            return GetRenameInfoError(ctx.Locale, global::Ts.Diagnostics.Diagnostics.File_rename_is_not_supported_by_the_editor);
        }

        Node? moduleSourceFile = null;
        foreach (var declaration in moduleSymbol.Declarations ?? [])
        {
            if (NodeTests.IsSourceFile(declaration))
            {
                moduleSourceFile = declaration;
                break;
            }
        }
        if (moduleSourceFile == null)
        {
            return null;
        }

        var fileName = moduleSourceFile.AsSourceFile().FileName();
        var withoutIndex = "";
        if (!specifier.Text().EndsWith("/index", System.StringComparison.Ordinal) && !specifier.Text().EndsWith("/index.js", System.StringComparison.Ordinal))
        {
            var candidate = global::Ts.TsPath.TsPath.RemoveFileExtension(fileName);
            if (candidate.EndsWith("/index", System.StringComparison.Ordinal))
            {
                withoutIndex = candidate[..^"/index".Length];
            }
        }

        var displayName = fileName;
        if (withoutIndex.Length != 0)
        {
            displayName = withoutIndex;
        }
        var newFileName = GetNewFileNameForModuleRename(displayName, specifier.Text(), newName);

        // Span should only be the last component of the path. + 1 to account for the quote character.
        var indexAfterLastSlash = specifier.Text().LastIndexOf('/') + 1;
        var start = specifier.Pos + 1 + indexAfterLastSlash;
        var length = specifier.Text().Length - indexAfterLastSlash;

        return new RenameInfo
        {
            CanRename = true,
            DisplayName = specifier.Text()[indexAfterLastSlash..],
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

    // rename.go:297-350 — getTextForRename.
    private string GetTextForRename(
        Node originalNode,
        ReferenceEntry entry,
        string newText,
        global::Ts.Checker.Checker ch,
        QuotePreference quotePreference,
        bool useAliasesForRename)
    {
        if (useAliasesForRename && entry.Kind != EntryKind.Range && (NodeTests.IsIdentifier(originalNode) || AstUtilities.IsStringLiteralLike(originalNode)))
        {
            var node = LsDeps.GetReparsedNodeForNode(entry.Node!);
            var kind = entry.Kind;
            var parent = node.Parent!; // Go dereferences unconditionally.
            var name = originalNode.Text();
            var isShorthandAssignment = NodeTests.IsShorthandPropertyAssignment(parent);
            if (isShorthandAssignment ||
                (LsDeps.IsObjectBindingElementWithoutPropertyName(parent) && parent.Name() == node && parent.AsBindingElement().DotDotDotToken == null))
            {
                if (kind == EntryKind.SearchedLocalFoundProperty)
                {
                    return name + ": " + newText;
                }
                if (kind == EntryKind.SearchedPropertyFoundLocal)
                {
                    return newText + ": " + name;
                }
                // In `const o = { x }; o.x`, symbolAtLocation at `x` in `{ x }` is the property symbol.
                // For a binding element `const { x } = o;`, symbolAtLocation at `x` is the property symbol.
                if (isShorthandAssignment)
                {
                    var grandParent = parent.Parent!;
                    if (NodeTests.IsObjectLiteralExpression(grandParent) && NodeTests.IsBinaryExpression(grandParent.Parent!) && AstUtilities.IsModuleExportsAccessExpression(grandParent.Parent!.AsBinaryExpression().Left!))
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
                global::Ts.Ast.Symbol? originalSymbol;
                if (NodeTests.IsExportSpecifier(originalNode.Parent!))
                {
                    originalSymbol = ch.GetExportSpecifierLocalTargetSymbol(originalNode.Parent!);
                }
                else
                {
                    originalSymbol = ch.GetSymbolAtLocation(originalNode);
                }
                if (originalSymbol != null)
                {
                    foreach (var declaration in originalSymbol.Declarations ?? [])
                    {
                        if (ReferenceEquals(declaration, parent))
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
                if (ReferenceEquals(originalNode, entry.Node) || ReferenceEquals(ch.GetSymbolAtLocation(originalNode), ch.GetSymbolAtLocation(entry.Node!)))
                {
                    return name + " as " + newText;
                }
                return newText + " as " + name;
            }
        }

        // If the node is a numerical indexing literal, then add quotes around the property access.
        if (entry.Kind != EntryKind.Range && NodeTests.IsNumericLiteral(entry.Node!) && AstUtilities.IsAccessExpression(entry.Node!.Parent!))
        {
            var quote = GetQuoteFromPreference(quotePreference);
            return quote + newText + quote;
        }

        return newText;
    }

    // rename.go:352-357 — getQuoteFromPreference.
    private static string GetQuoteFromPreference(QuotePreference quotePreference)
    {
        if (quotePreference == QuotePreference.Single)
        {
            return "'";
        }
        return "\"";
    }

    // rename.go:359-364 — getRenameInfoError. Go: message.Localize(locale.FromContext(ctx)).
    private static RenameInfo GetRenameInfoError(global::Ts.Locale.Locale locale, global::Ts.Diagnostics.Message message)
    {
        return new RenameInfo
        {
            CanRename = false,
            LocalizedErrorMessage = message.Localize(locale),
        };
    }

    // rename.go:366-379 — getRenameInfoSuccess.
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
