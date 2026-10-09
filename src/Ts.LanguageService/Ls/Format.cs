// Ported from tsgo internal/ls/format.go (full file).
//
// PORT-NOTE: Go threads the format options through a context.Context
// (format.WithFormatCodeSettings); the C# format engine takes an explicit
// Ts.Format.FormatContext (Format/Api.cs), so `ctx = format.WithFormatCodeSettings(...)`
// becomes constructing that value at the call site. lsutil.FromLSFormatOptions
// (formatcodeoptions.go:94) is hosted on the LsUtil helper below together with
// lsproto.PreferredMarkupKind (lsp.go:302) — the Lsp/ directory is generated/owned by the
// server wave, so the two protocol-adjacent helpers live in Ts.Ls. Positions are UTF-16
// code units throughout.

using System.Collections.Generic;
using Ts.Core;

namespace Ts.Ls;

// lsutil/formatcodeoptions.go:94-105 — FromLSFormatOptions merges the LSP client's
// FormattingOptions over the user's FormatCodeSettings.
public static class LsUtil
{
    public static global::Ts.Format.FormatCodeSettings FromLSFormatOptions(global::Ts.Format.FormatCodeSettings f, global::Ts.LsProto.FormattingOptions opt)
    {
        var updatedSettings = f.Clone();
        updatedSettings.TabSize = (int)opt.TabSize;
        updatedSettings.IndentSize = (int)opt.TabSize;
        updatedSettings.ConvertTabsToSpaces = TristateExtensions.BoolToTristate(opt.InsertSpaces);
        if (opt.TrimTrailingWhitespace != null)
        {
            updatedSettings.TrimTrailingWhitespace = TristateExtensions.BoolToTristate(opt.TrimTrailingWhitespace.Value);
        }
        return updatedSettings;
    }

    // lsp/lsproto/lsp.go:302-308 — PreferredMarkupKind.
    public static global::Ts.LsProto.MarkupKind PreferredMarkupKind(global::Ts.LsProto.MarkupKind[]? formats)
    {
        if (formats != null && formats.Length > 0)
        {
            return formats[0];
        }
        return global::Ts.LsProto.MarkupKind.PlainText;
    }
}

public sealed partial class LanguageService
{
    // format.go:16-25 — toLSProtoTextEdits.
    internal List<global::Ts.LsProto.TextEdit> ToLSProtoTextEdits(global::Ts.Ast.SourceFile file, TextChange[] changes)
    {
        var result = new List<global::Ts.LsProto.TextEdit>(changes.Length);
        foreach (var c in changes)
        {
            result.Add(new global::Ts.LsProto.TextEdit
            {
                NewText = c.NewText,
                Range = CreateLspRangeFromBounds(c.TextRange.Pos, c.TextRange.End, file),
            });
        }
        return result;
    }

    // format.go:27-43 — ProvideFormatDocument.
    public global::Ts.LsProto.TextEditsOrNull? ProvideFormatDocument(
        global::Ts.Core.RequestContext ctx,
        global::Ts.LsProto.DocumentUri documentURI,
        global::Ts.LsProto.FormattingOptions options)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        if (UserPreferences().EnableFormatting.IsFalse())
        {
            return new global::Ts.LsProto.TextEditsOrNull();
        }
        var file = GetProgramAndFile(documentURI);
        var formatOpts = LsUtil.FromLSFormatOptions(FormatOptions(), options);
        var edits = ToLSProtoTextEdits(file, GetFormattingEditsForDocument(
            ctx,
            file,
            formatOpts));
        return new global::Ts.LsProto.TextEditsOrNull { TextEdits = edits.ToArray() };
    }

    // format.go:45-63 — ProvideFormatDocumentRange.
    public global::Ts.LsProto.TextEditsOrNull? ProvideFormatDocumentRange(
        global::Ts.Core.RequestContext ctx,
        global::Ts.LsProto.DocumentUri documentURI,
        global::Ts.LsProto.FormattingOptions options,
        global::Ts.LsProto.Range r)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        if (UserPreferences().EnableFormatting.IsFalse())
        {
            return new global::Ts.LsProto.TextEditsOrNull();
        }
        var file = GetProgramAndFile(documentURI);
        var formatOpts = LsUtil.FromLSFormatOptions(FormatOptions(), options);
        var edits = ToLSProtoTextEdits(file, GetFormattingEditsForRange(
            ctx,
            file,
            formatOpts,
            Converters().FromLSPRange(global::Ts.Ls.Change.SourceFileScriptExtensions.AsScript(file), r)));
        return new global::Ts.LsProto.TextEditsOrNull { TextEdits = edits.ToArray() };
    }

    // format.go:65-85 — ProvideFormatDocumentOnType.
    public global::Ts.LsProto.TextEditsOrNull? ProvideFormatDocumentOnType(
        global::Ts.Core.RequestContext ctx,
        global::Ts.LsProto.DocumentUri documentURI,
        global::Ts.LsProto.FormattingOptions options,
        global::Ts.LsProto.Position position,
        string character)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        if (UserPreferences().EnableFormatting.IsFalse())
        {
            return new global::Ts.LsProto.TextEditsOrNull();
        }
        var file = GetProgramAndFile(documentURI);
        var formatOpts = LsUtil.FromLSFormatOptions(FormatOptions(), options);
        var edits = ToLSProtoTextEdits(file, GetFormattingEditsAfterKeystroke(
            ctx,
            file,
            formatOpts,
            (int)Converters().LineAndCharacterToPosition(global::Ts.Ls.Change.SourceFileScriptExtensions.AsScript(file), position),
            character));
        return new global::Ts.LsProto.TextEditsOrNull { TextEdits = edits.ToArray() };
    }

    // format.go:87-95 — getFormattingEditsForRange.
    private TextChange[] GetFormattingEditsForRange(
        global::Ts.Core.RequestContext ctx,
        global::Ts.Ast.SourceFile file,
        global::Ts.Format.FormatCodeSettings options,
        TextRange r)
    {
        var formatCtx = global::Ts.Format.FormatContext.WithFormatCodeSettings(options, options.NewLineCharacter);
        return global::Ts.Format.Api.FormatSelection(formatCtx, file, r.Pos, r.End);
    }

    // format.go:97-104 — getFormattingEditsForDocument.
    private TextChange[] GetFormattingEditsForDocument(
        global::Ts.Core.RequestContext ctx,
        global::Ts.Ast.SourceFile file,
        global::Ts.Format.FormatCodeSettings options)
    {
        var formatCtx = global::Ts.Format.FormatContext.WithFormatCodeSettings(options, options.NewLineCharacter);
        return global::Ts.Format.Api.FormatDocument(formatCtx, file);
    }

    // format.go:106-131 — getFormattingEditsAfterKeystroke.
    private TextChange[] GetFormattingEditsAfterKeystroke(
        global::Ts.Core.RequestContext ctx,
        global::Ts.Ast.SourceFile file,
        global::Ts.Format.FormatCodeSettings options,
        int position,
        string key)
    {
        var formatCtx = global::Ts.Format.FormatContext.WithFormatCodeSettings(options, options.NewLineCharacter);

        var tokenAtPosition = global::Ts.AstNav.Tokens.GetTokenAtPosition(file, position);
        if (LsDeps.IsInComment(file, position, tokenAtPosition) == null)
        {
            switch (key)
            {
                case "{":
                    return global::Ts.Format.Api.FormatOnOpeningCurly(formatCtx, file, position);
                case "}":
                    return global::Ts.Format.Api.FormatOnClosingCurly(formatCtx, file, position);
                case ";":
                    return global::Ts.Format.Api.FormatOnSemicolon(formatCtx, file, position);
                case "\n":
                    return global::Ts.Format.Api.FormatOnEnter(formatCtx, file, position);
                default:
                    return [];
            }
        }
        return [];
    }
}
