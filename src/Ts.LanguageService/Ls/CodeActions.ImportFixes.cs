// Ported from tsgo internal/ls/codeactions_importfixes.go (whole file): the
// ImportFixProvider — "add import" quick fixes for cannot-find-name style errors,
// UMD-global imports, type-only promotion fixes and the JSX namespace fixes.
//
// PORT-NOTE: `locale.FromContext(ctx)` → Locale.Default and `(T, error)` returns →
// nullable/throw per the LS port conventions (plan/05 §4).

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Checker; // CheckerLsExports extension methods (GetResolvedSymbol, GetJsxNamespace, ResolveName)
using Ts.Core;
using Ts.Diagnostics; // Message.Localize extension

namespace Ts.Ls;

// codeactions_importfixes.go:21-43 — importFixErrorCodes.
public static partial class CodeActions
{
    internal static readonly int[] importFixErrorCodes =
    [
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Did_you_mean_1.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Did_you_mean_the_instance_member_this_0.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Did_you_mean_the_static_member_1_0.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_namespace_0.Code,
        global::Ts.Diagnostics.Diagnostics.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead.Code,
        global::Ts.Diagnostics.Diagnostics.X_0_only_refers_to_a_type_but_is_being_used_as_a_value_here.Code,
        global::Ts.Diagnostics.Diagnostics.No_value_exists_in_scope_for_the_shorthand_property_0_Either_declare_one_or_provide_an_initializer.Code,
        global::Ts.Diagnostics.Diagnostics.X_0_cannot_be_used_as_a_value_because_it_was_imported_using_import_type.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_1_or_later.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_include_dom.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha_and_then_add_jest_or_mocha_to_the_types_field_in_your_tsconfig.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Did_you_mean_to_write_this_in_an_async_function.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery_and_then_add_jquery_to_the_types_field_in_your_tsconfig.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode_and_then_add_node_to_the_types_field_in_your_tsconfig.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_find_namespace_0_Did_you_mean_1.Code,
        global::Ts.Diagnostics.Diagnostics.Cannot_extend_an_interface_0_Did_you_mean_implements.Code,
        global::Ts.Diagnostics.Diagnostics.This_JSX_tag_requires_0_to_be_in_scope_but_it_could_not_be_found.Code,
    ];

    // codeactions_importfixes.go:45-47 — importFixID.
    internal const string importFixID = "fixMissingImport";

    // codeactions_importfixes.go:49-55 — ImportFixProvider is the CodeFixProvider for
    // import-related fixes.
    public static readonly CodeFixProvider ImportFixProvider = new()
    {
        ErrorCodes = importFixErrorCodes,
        GetCodeActions = LanguageService.GetImportCodeActions,
        FixIds = [importFixID],
        GetAllCodeActions = LanguageService.GetAllImportCodeActions,
    };
}

// codeactions_importfixes.go:57-62 — fixInfo pairs an auto-import fix with the symbol name it
// resolves and the identifier text the diagnostic pointed at.
internal sealed class FixInfo
{
    internal global::Ts.Ls.AutoImport.Fix fix = null!;
    internal string symbolName = "";
    internal string errorIdentifierText = "";
    internal bool isJsxNamespaceFix;
}

// codeactions_importfixes.go:377-380 — symbolNameInfo.
internal sealed class SymbolNameInfo
{
    internal string name = "";
    internal bool isTypeOnly; // whether the symbol currently resolves to a type-only import
}

public sealed partial class LanguageService
{
    // codeactions_importfixes.go:64-92 — getImportCodeActions.
    internal static CodeAction[]? GetImportCodeActions(CancellationToken ctx, CodeFixContext fixContext)
    {
        var info = CodeActions.GetFixInfos(ctx, fixContext, fixContext.ErrorCode, fixContext.Span.Pos);
        if (info == null || info.Count == 0)
        {
            return null;
        }

        var actions = new List<CodeAction>();
        foreach (var fixInfo in info)
        {
            var (edits, description) = fixInfo.fix.Edits(
                fixContext.SourceFile,
                fixContext.Program.Options(),
                fixContext.LS.FormatOptions(),
                fixContext.LS.Converters(),
                fixContext.LS.UserPreferences());

            actions.Add(new CodeAction
            {
                Description = description,
                Changes = edits,
                FixID = CodeActions.importFixID,
                FixAllDescription = global::Ts.Diagnostics.Diagnostics.Add_all_missing_imports.Localize(global::Ts.Locale.Locale.Default),
            });
        }
        return actions.ToArray();
    }

    // codeactions_importfixes.go:94-148 — getAllImportCodeActions.
    internal static CombinedCodeActions? GetAllImportCodeActions(CancellationToken ctx, CodeFixContext fixContext)
    {
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fixContext.SourceFile.FileName()))
        {
            return null;
        }

        var allDiagnostics = fixContext.Program.GetSemanticDiagnostics(ctx, fixContext.SourceFile);

        var importDiags = new List<global::Ts.Ast.Diagnostic>();
        foreach (var diag in allDiagnostics)
        {
            if (CodeActions.ContainsErrorCode(CodeActions.importFixErrorCodes, diag.Code()))
            {
                importDiags.Add(diag);
            }
        }

        if (importDiags.Count == 0)
        {
            return null;
        }

        var (preparedView, err) = fixContext.LS.GetPreparedAutoImportView(fixContext.SourceFile);
        if (err != null)
        {
            throw err; // (View, error) → throw per plan/05 §4, matching GetPreparedAutoImportView callers
        }
        var view = preparedView;
        if (view == null)
        {
            view = fixContext.LS.GetCurrentAutoImportView(fixContext.SourceFile);
        }

        var (ch, done) = fixContext.Program.GetTypeChecker(ctx);
        try
        {
            var importAdder = global::Ts.Ls.AutoImport.AutoImportApi.NewImportAdder(
                fixContext.Program,
                ch,
                fixContext.SourceFile,
                view!, // Go passes a possibly-nil view; the adder tolerates it on this path
                fixContext.LS.FormatOptions(),
                fixContext.LS.Converters(),
                fixContext.LS.UserPreferences());

            foreach (var diag in importDiags)
            {
                CodeActions.AddImportFromDiagnostic(ctx, importAdder, diag, fixContext);
            }

            if (!importAdder.HasFixes())
            {
                return null;
            }

            return new CombinedCodeActions
            {
                Description = global::Ts.Diagnostics.Diagnostics.Add_all_missing_imports.Localize(global::Ts.Locale.Locale.Default),
                Changes = importAdder.Edits(),
            };
        }
        finally
        {
            done();
        }
    }
}

// Package-level helpers of codeactions_importfixes.go (Go package funcs → static partial
// class, mirroring the other CodeActions* port files).
public static partial class CodeActions
{
    // codeactions_importfixes.go:150-168 — addImportFromDiagnostic finds the best import fix
    // for a diagnostic and adds it to the adder.
    internal static void AddImportFromDiagnostic(CancellationToken ctx, global::Ts.Ls.AutoImport.ImportAdder importAdder, global::Ts.Ast.Diagnostic diag, CodeFixContext fixContext)
    {
        var diagFixContext = new CodeFixContext
        {
            SourceFile = fixContext.SourceFile,
            Span = new TextRange(diag.Pos(), diag.End() - diag.Pos()),
            ErrorCode = diag.Code(),
            Program = fixContext.Program,
            LS = fixContext.LS,
        };

        var infos = GetFixInfos(ctx, diagFixContext, diag.Code(), diag.Pos());
        if (infos != null && infos.Count > 0)
        {
            importAdder.AddImportFix(infos[0].fix);
        }
    }

    // codeactions_importfixes.go:170-239 — getFixInfos.
    internal static List<FixInfo>? GetFixInfos(CancellationToken ctx, CodeFixContext fixContext, int errorCode, int pos)
    {
        // Can't compute import fixes for dynamic/untitled files since they don't have real file paths
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fixContext.SourceFile.FileName()))
        {
            return null;
        }

        var symbolToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(fixContext.SourceFile, pos);

        global::Ts.Ls.AutoImport.View? view = null;
        List<FixInfo>? info = null;

        if (errorCode == global::Ts.Diagnostics.Diagnostics.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead.Code)
        {
            view = fixContext.LS.GetCurrentAutoImportView(fixContext.SourceFile);
            info = GetFixesInfoForUMDImport(ctx, fixContext, symbolToken, view);
        }
        else if (!NodeTests.IsIdentifier(symbolToken!))
        {
            return null;
        }
        else if (errorCode == global::Ts.Diagnostics.Diagnostics.X_0_cannot_be_used_as_a_value_because_it_was_imported_using_import_type.Code)
        {
            var (ch, done) = fixContext.Program.GetTypeChecker(ctx);
            try
            {
                var compilerOptions = fixContext.Program.Options();
                var symbolNames = GetSymbolNamesToImport(fixContext.SourceFile, ch, symbolToken!, compilerOptions);

                var allTypeOnlyFixes = new List<FixInfo>();
                foreach (var sn in symbolNames)
                {
                    if (!sn.isTypeOnly)
                    {
                        continue;
                    }
                    var fix = GetTypeOnlyPromotionFix(ctx, fixContext.SourceFile, symbolToken!, sn.name, fixContext.Program);
                    if (fix != null)
                    {
                        allTypeOnlyFixes.Add(new FixInfo { fix = fix, symbolName = sn.name, errorIdentifierText = symbolToken!.Text() });
                    }
                }

                // For JSX opening tags, there can be separate type-only errors for both the tag name
                // identifier and the JSX namespace identifier. When both produce valid fixes, we
                // disambiguate using the diagnostic message, which quotes the symbol name in single
                // quotes (e.g., "'React' cannot be used as a value..."). If filtering yields nothing
                // (e.g., due to localization), fall back to returning all candidates.
                var diagnosticMessage = "";
                if (fixContext.Diagnostic != null)
                {
                    diagnosticMessage = fixContext.Diagnostic.Message.String ?? "";
                }
                if (allTypeOnlyFixes.Count > 1 && diagnosticMessage != "")
                {
                    foreach (var fi in allTypeOnlyFixes)
                    {
                        if (diagnosticMessage.Contains("'" + fi.symbolName + "'", StringComparison.Ordinal))
                        {
                            info ??= [];
                            info.Add(fi);
                        }
                    }
                }
                if (info == null || info.Count == 0)
                {
                    info = allTypeOnlyFixes;
                }
                return info;
            }
            finally
            {
                done();
            }
        }
        else
        {
            var (preparedView, err) = fixContext.LS.GetPreparedAutoImportView(fixContext.SourceFile);
            if (err != null)
            {
                throw err; // (View, error) → throw per plan/05 §4, matching GetPreparedAutoImportView callers
            }
            view = preparedView;
            if (view != null)
            {
                info = GetFixesInfoForNonUMDImport(ctx, fixContext, symbolToken!, view);
            }
        }

        // Sort fixes by preference
        if (view == null)
        {
            view = fixContext.LS.GetCurrentAutoImportView(fixContext.SourceFile);
        }
        return SortFixInfo(info, fixContext, view);
    }

    // codeactions_importfixes.go:241-266 — getFixesInfoForUMDImport.
    internal static List<FixInfo>? GetFixesInfoForUMDImport(CancellationToken ctx, CodeFixContext fixContext, Node? token, global::Ts.Ls.AutoImport.View? view)
    {
        var (ch, done) = fixContext.Program.GetTypeChecker(ctx);
        try
        {
            var umdSymbol = GetUmdSymbol(token!, ch);
            if (umdSymbol == null)
            {
                return null;
            }

            var export = global::Ts.Ls.AutoImport.AutoImportApi.SymbolToExport(umdSymbol, ch);
            var isValidTypeOnlyUseSite = AstUtilities.IsValidTypeOnlyAliasUseSite(token!);

            var result = new List<FixInfo>();
            foreach (var fix in view!.GetFixes(export!, false, isValidTypeOnlyUseSite, null) ?? [])
            {
                var errorIdentifierText = "";
                if (NodeTests.IsIdentifier(token!))
                {
                    errorIdentifierText = token!.Text();
                }
                result.Add(new FixInfo
                {
                    fix = fix,
                    symbolName = umdSymbol.Name,
                    errorIdentifierText = errorIdentifierText,
                });
            }
            return result;
        }
        finally
        {
            done();
        }
    }

    // codeactions_importfixes.go:268-295 — getUmdSymbol.
    internal static global::Ts.Ast.Symbol? GetUmdSymbol(Node token, global::Ts.Checker.Checker ch)
    {
        // try the identifier to see if it is the umd symbol
        global::Ts.Ast.Symbol? umdSymbol = null;
        if (NodeTests.IsIdentifier(token))
        {
            umdSymbol = ch.GetResolvedSymbol(token);
        }
        if (IsUMDExportSymbol(umdSymbol))
        {
            return umdSymbol;
        }

        // The error wasn't for the symbolAtLocation, it was for the JSX tag itself, which needs access to e.g. `React`.
        var parent = token.Parent;
        if ((AstUtilities.IsJsxOpeningLikeElement(parent!) && parent!.TagName() == token) ||
            NodeTests.IsJsxOpeningFragment(parent!))
        {
            Node? location;
            if (AstUtilities.IsJsxOpeningLikeElement(parent!))
            {
                location = token;
            }
            else
            {
                location = parent;
            }
            var jsxNamespace = ch.GetJsxNamespace(parent!);
            var parentSymbol = ch.ResolveName(jsxNamespace, location, SymbolFlags.Value, false /* excludeGlobals */);
            if (IsUMDExportSymbol(parentSymbol))
            {
                return parentSymbol;
            }
        }
        return null;
    }

    // codeactions_importfixes.go:297-301 — isUMDExportSymbol.
    internal static bool IsUMDExportSymbol(global::Ts.Ast.Symbol? symbol)
    {
        return symbol != null && (symbol.Declarations?.Length ?? 0) > 0 &&
            symbol.Declarations![0] != null &&
            NodeTests.IsNamespaceExportDeclaration(symbol.Declarations[0]);
    }

    // codeactions_importfixes.go:303-351 — getFixesInfoForNonUMDImport.
    internal static List<FixInfo> GetFixesInfoForNonUMDImport(CancellationToken ctx, CodeFixContext fixContext, Node symbolToken, global::Ts.Ls.AutoImport.View view)
    {
        var (ch, done) = fixContext.Program.GetTypeChecker(ctx);
        try
        {
            var compilerOptions = fixContext.Program.Options();

            var isValidTypeOnlyUseSite = AstUtilities.IsValidTypeOnlyAliasUseSite(symbolToken);
            var symbolNames = GetSymbolNamesToImport(fixContext.SourceFile, ch, symbolToken, compilerOptions);
            var allInfo = new List<FixInfo>();

            // Compute usage position for JSDoc import type fixes
            var usagePosition = LsDeps.PositionToLineAndCharacter(fixContext.SourceFile, global::Ts.Scanner.Scanner.GetTokenPosOfNode(symbolToken, fixContext.SourceFile, false));

            foreach (var sn in symbolNames)
            {
                // Type-only imports are handled by the promotion code path, not the auto-import path.
                if (sn.isTypeOnly)
                {
                    continue;
                }

                var symbolName = sn.name;
                // "default" is a keyword and not a legal identifier for the import
                if (symbolName == "default")
                {
                    continue;
                }

                var isJSXTagName = symbolName == symbolToken.Text() && AstUtilities.IsJsxTagName(symbolToken);
                var queryKind = global::Ts.Ls.AutoImport.QueryKind.ExactMatch;
                if (isJSXTagName)
                {
                    queryKind = global::Ts.Ls.AutoImport.QueryKind.CaseInsensitiveMatch;
                }

                var exports = view.Search(symbolName, queryKind);
                foreach (var export in exports)
                {
                    if (isJSXTagName && !(export.Name() == symbolName || export.IsRenameable()))
                    {
                        continue;
                    }

                    var fixes = view.GetFixes(export, isJSXTagName, isValidTypeOnlyUseSite, usagePosition);
                    foreach (var fix in fixes ?? [])
                    {
                        allInfo.Add(new FixInfo
                        {
                            fix = fix,
                            symbolName = symbolName,
                            isJsxNamespaceFix = symbolName != symbolToken.Text(),
                        });
                    }
                }
            }

            return allInfo;
        }
        finally
        {
            done();
        }
    }

    // codeactions_importfixes.go:353-375 — getTypeOnlyPromotionFix.
    internal static global::Ts.Ls.AutoImport.Fix? GetTypeOnlyPromotionFix(CancellationToken ctx, SourceFile sourceFile, Node symbolToken, string symbolName, global::Ts.Compiler.Program program)
    {
        var (ch, done) = program.GetTypeChecker(ctx);
        try
        {
            // Get the symbol at the token location
            var symbol = ch.ResolveName(symbolName, symbolToken, SymbolFlags.Value, true /* excludeGlobals */);
            if (symbol == null)
            {
                return null;
            }

            // Get the type-only alias declaration
            var typeOnlyAliasDeclaration = ch.GetTypeOnlyAliasDeclaration(symbol);
            if (typeOnlyAliasDeclaration == null || AstUtilities.GetSourceFileOfNode(typeOnlyAliasDeclaration) != sourceFile)
            {
                return null;
            }

            return new global::Ts.Ls.AutoImport.Fix
            {
                AutoImportFix = new global::Ts.LsProto.AutoImportFix
                {
                    Kind = global::Ts.LsProto.AutoImportFixKind.PromoteTypeOnly,
                },
                TypeOnlyAliasDeclaration = typeOnlyAliasDeclaration,
            };
        }
        finally
        {
            done();
        }
    }

    // codeactions_importfixes.go:382-411 — getSymbolNamesToImport.
    internal static List<SymbolNameInfo> GetSymbolNamesToImport(SourceFile sourceFile, global::Ts.Checker.Checker ch, Node symbolToken, global::Ts.Core.CompilerOptions compilerOptions)
    {
        var parent = symbolToken.Parent;
        if ((AstUtilities.IsJsxOpeningLikeElement(parent!) || NodeTests.IsJsxClosingElement(parent!)) &&
            parent!.TagName() == symbolToken &&
            JsxModeNeedsExplicitImport(compilerOptions.Jsx))
        {
            var jsxNamespace = ch.GetJsxNamespace(sourceFile.AsNode());
            if (NeedsJsxNamespaceFix(jsxNamespace, symbolToken, ch))
            {
                var result = new List<SymbolNameInfo>();
                if (!global::Ts.Scanner.Scanner.IsIntrinsicJsxName(symbolToken.Text()))
                {
                    var compSymbol = ch.ResolveName(symbolToken.Text(), symbolToken, SymbolFlags.Value, false /* excludeGlobals */);
                    if (compSymbol == null)
                    {
                        result.Add(new SymbolNameInfo { name = symbolToken.Text() });
                    }
                    else if (ch.GetTypeOnlyAliasDeclaration(compSymbol) != null)
                    {
                        result.Add(new SymbolNameInfo { name = symbolToken.Text(), isTypeOnly = true });
                    }
                }
                var nsIsTypeOnly = false;
                if (ch.ResolveName(jsxNamespace, symbolToken, SymbolFlags.Value, true /* excludeGlobals */) is { } nsSymbol)
                {
                    nsIsTypeOnly = ch.GetTypeOnlyAliasDeclaration(nsSymbol) != null;
                }
                result.Add(new SymbolNameInfo { name = jsxNamespace, isTypeOnly = nsIsTypeOnly });
                return result;
            }
        }
        var tokenIsTypeOnly = false;
        if (ch.ResolveName(symbolToken.Text(), symbolToken, SymbolFlags.Value, true /* excludeGlobals */) is { } sym)
        {
            tokenIsTypeOnly = ch.GetTypeOnlyAliasDeclaration(sym) != null;
        }
        return [new SymbolNameInfo { name = symbolToken.Text(), isTypeOnly = tokenIsTypeOnly }];
    }

    // codeactions_importfixes.go:413-425 — needsJsxNamespaceFix.
    internal static bool NeedsJsxNamespaceFix(string jsxNamespace, Node symbolToken, global::Ts.Checker.Checker ch)
    {
        if (global::Ts.Scanner.Scanner.IsIntrinsicJsxName(symbolToken.Text()))
        {
            return true;
        }
        var namespaceSymbol = ch.ResolveName(jsxNamespace, symbolToken, SymbolFlags.Value, true /* excludeGlobals */);
        if (namespaceSymbol == null)
        {
            return true;
        }
        if (namespaceSymbol.Declarations != null && Array.Exists(namespaceSymbol.Declarations, global::Ts.Checker.Checker.IsTypeOnlyImportOrExportDeclaration))
        {
            return (namespaceSymbol.Flags & SymbolFlags.Value) == 0;
        }
        return false;
    }

    // codeactions_importfixes.go:427-429 — jsxModeNeedsExplicitImport.
    internal static bool JsxModeNeedsExplicitImport(global::Ts.Core.JsxEmit jsx)
    {
        return jsx == global::Ts.Core.JsxEmit.React || jsx == global::Ts.Core.JsxEmit.ReactNative;
    }

    // codeactions_importfixes.go:431-452 — sortFixInfo.
    internal static List<FixInfo>? SortFixInfo(List<FixInfo>? fixes, CodeFixContext fixContext, global::Ts.Ls.AutoImport.View? view)
    {
        if (fixes == null || fixes.Count == 0)
        {
            return fixes;
        }

        // Create a copy to avoid modifying the original
        var sorted = new List<FixInfo>(fixes);

        // Sort by:
        // 1. JSX namespace fixes last
        // 2. Fix comparison using view.CompareFixes
        sorted.Sort((a, b) =>
        {
            // JSX namespace fixes should come last
            var cmp = CoreUtil.CompareBooleans(a.isJsxNamespaceFix, b.isJsxNamespaceFix);
            if (cmp != 0)
            {
                return cmp;
            }
            return view!.CompareFixesForSorting(a.fix, b.fix);
        });

        return sorted;
    }
}
