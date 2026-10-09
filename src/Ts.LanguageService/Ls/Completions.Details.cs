// Ported from tsgo internal/ls/completions.go — the completion item details/resolve tier:
// ResolveCompletionItem (:4888-4905), getCompletionDocumentationFormat (:4907-4909),
// getCompletionItemDetails (:4911-4996), the detailsData model + getSymbolCompletionFromItemData
// (:4998-5083) and the createCompletionDetails family (:5085-5133); plus tryGetProgramAndFile
// (languageservice.go:56-60).

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

// completions.go:4998-5003 — detailsData (4-arm union).
internal sealed class DetailsData
{
    internal SymbolDetails? symbol;
    internal ICompletionData? request;
    internal object? literal; // literalValue
    internal bool cases;
}

// completions.go:5005-5013 — symbolDetails.
internal sealed class SymbolDetails
{
    internal Symbol symbol = null!;
    internal Node? location;
    internal SymbolOriginInfo? origin;
    internal Node? previousToken;
    internal Node? contextToken;
    internal JsxInitializerInfo jsxInitializer;
    internal bool isTypeOnlyLocation;
}

public sealed partial class LanguageService
{
    // languageservice.go:56-60 — tryGetProgramAndFile.
    internal (global::Ts.Compiler.Program Program, SourceFile? File) TryGetProgramAndFile(string fileName)
    {
        var program = GetProgram();
        var file = program.GetSourceFile(fileName);
        return (program, file);
    }

    // completions.go:4888-4905 — ResolveCompletionItem.
    public global::Ts.LsProto.CompletionItem ResolveCompletionItem(
        global::Ts.LsProto.CompletionItem item,
        global::Ts.LsProto.CompletionItemData? data,
        CancellationToken cancellationToken = default)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data), "completion item data is nil");
        }

        var (program, file) = TryGetProgramAndFile(data.FileName);
        if (file == null)
        {
            throw new InvalidOperationException($"file not found: {data.FileName}");
        }

        var (checker, done) = program.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            return GetCompletionItemDetails(checker, data.Position, file, item, data);
        }
        finally
        {
            done();
        }
    }

    // completions.go:4888-4905 — ResolveCompletionItem (LSP-level entry). Go's (T, error)
    // pair maps to exceptions: nil data and a missing file both surface as
    // InvalidOperationException (the repository's Go-error mapping for request invariants).
    public global::Ts.LsProto.CompletionItem ResolveCompletionItem(
        global::Ts.Core.RequestContext ctx,
        global::Ts.LsProto.CompletionItem item,
        global::Ts.LsProto.CompletionItemData? data)
    {
        if (data == null)
        {
            throw new InvalidOperationException("completion item data is nil");
        }

        var (program, file) = TryGetProgramAndFile(data.FileName);
        if (file == null)
        {
            throw new InvalidOperationException($"file not found: {data.FileName}");
        }

        var (checker, done) = program.GetTypeCheckerForFile(ctx.Cancellation, file);
        try
        {
            return GetCompletionItemDetails(checker, data.Position, file, item, data);
        }
        finally
        {
            done();
        }
    }

    // completions.go:4907-4909 — getCompletionDocumentationFormat. Resolves the stored
    // client capabilities through PreferredMarkupKind; the default-constructed capabilities
    // (no DocumentationFormat entries) resolve to plaintext, matching the previous
    // constant-shaped stub this replaces.
    internal string GetCompletionDocumentationFormat() =>
        LsUtil.PreferredMarkupKind(ClientCapabilities().TextDocument.Completion.CompletionItem.DocumentationFormat).Value ?? "plaintext";

    // completions.go:4911-4996 — getCompletionItemDetails.
    internal global::Ts.LsProto.CompletionItem GetCompletionItemDetails(
        TsChecker checker,
        int position,
        SourceFile file,
        global::Ts.LsProto.CompletionItem item,
        global::Ts.LsProto.CompletionItemData data)
    {
        var docFormat = GetCompletionDocumentationFormat();
        var (contextToken, previousToken) = GetRelevantTokens(position, file);
        if (LsDeps.IsInString(file, position, previousToken))
        {
            return GetStringLiteralCompletionDetails(
                checker,
                item,
                data.Name ?? "",
                file,
                position,
                contextToken,
                docFormat);
        }

        if (data.AutoImport != null)
        {
            // completions.go:4935-4940.
            var (edits, description) = new global::Ts.Ls.AutoImport.Fix { AutoImportFix = data.AutoImport }
                .Edits(file, program.Options(), FormatOptions(), Converters(), UserPreferences());
            item.AdditionalTextEdits = edits;
            item.Detail = description;
            return item;
        }

        // Compute all the completion symbols again.
        var symbolCompletion = GetSymbolCompletionFromItemData(
            checker,
            file,
            position,
            data);
        var preferences = UserPreferences();

        if (symbolCompletion.request != null)
        {
            var request = symbolCompletion.request;
            switch (request)
            {
                case CompletionDataJSDocTagName:
                    return CreateSimpleDetails(item, data.Name ?? "", docFormat);
                case CompletionDataJSDocTag:
                    return CreateSimpleDetails(item, data.Name ?? "", docFormat);
                case CompletionDataJSDocParameterName:
                    return CreateSimpleDetails(item, data.Name ?? "", docFormat);
                case CompletionDataKeyword keyword:
                    if (CoreUtil.Some(keyword.KeywordCompletions, c => c.Item!.Label == data.Name))
                    {
                        return CreateSimpleDetails(item, data.Name ?? "", docFormat);
                    }
                    return item;
                default:
                    throw new InvalidOperationException($"Unexpected completion data type: {request.GetType().Name}");
            }
        }
        else if (symbolCompletion.symbol != null)
        {
            var symbolDetails = symbolCompletion.symbol;
            return CreateCompletionDetailsForSymbol(
                item,
                symbolDetails.symbol,
                checker,
                symbolDetails.location!,
                position,
                docFormat);
        }
        else if (symbolCompletion.literal != null)
        {
            var literal = symbolCompletion.literal;
            return CreateSimpleDetails(item, CompletionNameForLiteral(file, preferences, literal), docFormat);
        }
        else if (symbolCompletion.cases)
        {
            return item;
        }
        else
        {
            // Didn't find a symbol with this name.  See if we can find a keyword instead.
            if (CoreUtil.Some(AllKeywordCompletions.Value, c => c.Label == data.Name))
            {
                return CreateSimpleDetails(item, data.Name ?? "", docFormat);
            }
            return item;
        }
    }

    // completions.go:5015-5083 — getSymbolCompletionFromItemData.
    internal DetailsData GetSymbolCompletionFromItemData(
        TsChecker ch,
        SourceFile file,
        int position,
        global::Ts.LsProto.CompletionItemData itemData)
    {
        if (itemData.Source == CompletionSource.SwitchCases)
        {
            return new DetailsData
            {
                cases = true,
            };
        }

        var completionData = GetCompletionData(ch, file, position, UserPreferences(), true /*forItemResolve*/);

        if (completionData == null)
        {
            return new DetailsData();
        }

        if (completionData is not CompletionDataData data)
        {
            return new DetailsData
            {
                request = completionData,
            };
        }

        var preferences = UserPreferences();
        object? literal = null;
        foreach (var lit in data.literals ?? [])
        {
            if (lit != null && CompletionNameForLiteral(file, preferences, lit) == itemData.Name)
            {
                literal = lit;
                break;
            }
        }
        if (literal != null)
        {
            return new DetailsData
            {
                literal = literal,
            };
        }

        // Find the symbol with the matching entry name.
        // We don't need to perform character checks here because we're only comparing the
        // name against 'entryName' (which is known to be good), not building a new
        // completion entry.
        var symbols = data.symbols ?? [];
        for (var index = 0; index < symbols.Length; index++)
        {
            var symbol = symbols[index];
            SymbolOriginInfo? origin = null;
            data.symbolToOriginInfoMap?.TryGetValue(index, out origin);
            var (displayName, _) = GetCompletionEntryDisplayNameForSymbol(symbol, origin, data.completionKind, data.isJsxIdentifierExpected);
            if (displayName == itemData.Name &&
                (itemData.Source == CompletionSource.ClassMemberSnippet && (symbol.Flags & SymbolFlags.ClassMember) != 0 ||
                    itemData.Source == CompletionSource.ObjectLiteralMethodSnippet && (symbol.Flags & (SymbolFlags.Property | SymbolFlags.Method)) != 0 ||
                    GetSourceFromOrigin(origin) == itemData.Source ||
                    itemData.Source == CompletionSource.ObjectLiteralMemberWithComma))
            {
                return new DetailsData
                {
                    symbol = new SymbolDetails
                    {
                        symbol = symbol,
                        location = data.location,
                        origin = origin,
                        previousToken = data.previousToken,
                        contextToken = data.contextToken,
                        jsxInitializer = data.jsxInitializer,
                        isTypeOnlyLocation = data.isTypeOnlyLocation,
                    },
                };
            }
        }
        return new DetailsData();
    }

    // completions.go:5085-5093 — createSimpleDetails.
    internal static global::Ts.LsProto.CompletionItem CreateSimpleDetails(
        global::Ts.LsProto.CompletionItem item,
        string name,
        string docFormat)
    {
        return CreateCompletionDetails(item, name, "" /*documentation*/, docFormat);
    }

    // completions.go:5095-5113 — createCompletionDetails.
    internal static global::Ts.LsProto.CompletionItem CreateCompletionDetails(
        global::Ts.LsProto.CompletionItem item,
        string detail,
        string documentation,
        string docFormat)
    {
        // !!! fill in additionalTextEdits from code actions
        if (item.Detail == null && detail.Length != 0)
        {
            item.Detail = detail;
        }
        if (documentation.Length != 0)
        {
            item.Documentation = global::Ts.LsProto.StringOrMarkupContent.FromMarkupContent(new global::Ts.LsProto.MarkupContent
            {
                Kind = docFormat,
                Value = documentation,
            });
        }
        return item;
    }

    // completions.go:5123-5133 — createCompletionDetailsForSymbol.
    internal global::Ts.LsProto.CompletionItem CreateCompletionDetailsForSymbol(
        global::Ts.LsProto.CompletionItem item,
        Symbol symbol,
        TsChecker checker,
        Node location,
        int position,
        string docFormat)
    {
        var (quickInfo, documentation) = GetQuickInfoAndDocumentationForSymbol(checker, symbol, location, docFormat == "markdown", null);
        return CreateCompletionDetails(item, quickInfo, documentation, docFormat);
    }

    // string_completions.go:2017-2041 — getStringLiteralCompletionDetails.
    internal global::Ts.LsProto.CompletionItem GetStringLiteralCompletionDetails(
        TsChecker checker,
        global::Ts.LsProto.CompletionItem item,
        string name,
        SourceFile file,
        int position,
        Node? contextToken,
        string docFormat)
    {
        if (contextToken == null || !AstUtilities.IsStringLiteralLike(contextToken))
        {
            return item;
        }
        var completions = GetStringLiteralCompletionEntries(
            file,
            contextToken,
            position,
            checker);
        if (completions == null)
        {
            return item;
        }
        return StringLiteralCompletionDetails(item, name, contextToken, position, completions, file, checker, docFormat);
    }

    // string_completions.go:2043-2074 — stringLiteralCompletionDetails.
    internal global::Ts.LsProto.CompletionItem StringLiteralCompletionDetails(
        global::Ts.LsProto.CompletionItem item,
        string name,
        Node location,
        int position,
        StringLiteralCompletionsResult completion,
        SourceFile file,
        TsChecker checker,
        string docFormat)
    {
        if (completion.fromPaths != null)
        {
            // Path completions have eagerly-resolved details so the client can show an accurate icon
            // for items of file kind based on the file extension provided in the item detail.
            return item;
        }
        else if (completion.fromProperties != null)
        {
            var properties = completion.fromProperties;
            foreach (var symbol in properties.symbols)
            {
                if (symbol.Name == name)
                {
                    return CreateCompletionDetailsForSymbol(item, symbol, checker, location, position, docFormat);
                }
            }
        }
        else if (completion.fromTypes != null)
        {
            var types = completion.fromTypes;
            foreach (var t in types.types)
            {
                if ((string)t.AsLiteralType().Value()! == name)
                {
                    return CreateCompletionDetails(item, name, "" /*documentation*/, docFormat);
                }
            }
        }
        return item;
    }
}
