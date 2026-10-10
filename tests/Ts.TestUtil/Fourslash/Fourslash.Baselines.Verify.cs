// Ported from tsgo internal/fourslash/fourslash.go — the BASELINE verify families:
//   find-all-references  (:2399-2505) → GetReferencesAtPosition / ProvideVSReferences
//   code lens            (:2507-2560) → ProvideCodeLenses / ResolveCodeLens
//   go-to family         (:2566-2694) → ProvideDefinition / ProvideTypeDefinition /
//                                       ProvideSourceDefinition / ProvideImplementations
//   workspace symbol     (:2696-2719, :5496-5560) → ProvideWorkspaceSymbols
//   outlining spans      (:2721-2801) → ProvideFoldingRange
//   hover baselines      (:2803-2973) → ProvideHover
//   signature help       (:2975-3083) → ProvideSignatureHelp
//   selection ranges     (:3085-3221) → ProvideSelectionRanges
//   call hierarchy       (:3223-3540) → ProvidePrepareCallHierarchy / Incoming / Outgoing
//   document highlights  (:3542-3653) → ProvideDocumentHighlights / MultiDocumentHighlights
//   rename baseline      (:4549-4655, :5010-5021) → ProvideRename
//   inlay hints          (:5051-5110) → ProvideInlayHint
//   linked editing       (:5112-5189) → ProvideLinkedEditingRange
//   non-suggestion diags (:5273-5457) → ProvideDiagnostics + ErrorBaseline
//   document symbols     (:5562-5634) → ProvideDocumentSymbols
//   closing tags         (:4079-4120) → ProvideOnAutoInsert
//
// JSON PHASE NOTE: Go's baseline writers append a JSON dump of the raw response
// (core.StringifyJson) next to several text sections. core.StringifyJson is deferred in the C#
// port (src/Ts.Compiler/Core/Core.cs:673-675), so those sections are not emitted here; the
// affected baselines are listed in Scripts/csharpFailingTests.txt under plan/11 D-021.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ts.Core;
using Ts.Ls;
using Xunit;
using LsProto = global::Ts.LsProto;
// The non-suggestion-diagnostics family instantiates Go's generic GetErrorBaseline[T] at
// ast.Diagnostic (fourslash.go:5291); the sln has one Diagnostic type (plan/05 §3.1).
using Diagnostic = Ts.Ast.Diagnostic;

namespace Ts.TestUtil.Fourslash;

public sealed partial class FourslashTest
{
    // Go: fourslash.go:4546 — `type MarkerOrRangeOrName = any`; the C# corpus spells the three
    // arms as string / Marker / RangeMarker. A string is resolved through the session's marker
    // table (Go: f.testData.MarkerPositions[name], fourslash.go:4564-4569).
    private IFourslashMarkerOrRange ToMarkerOrRange(object markerOrNameOrRange) => markerOrNameOrRange switch
    {
        string name => MarkerByName(name),
        Marker marker => marker,
        RangeMarker range => range,
        _ => throw new InvalidOperationException(
            "Invalid marker or range type: " + markerOrNameOrRange.GetType().Name
            + ". Expected string, Marker, or RangeMarker."),
    };

    // fourslash.go:3656-3670 — lookupMarkersOrGetRanges.
    private List<IFourslashMarkerOrRange> lookupMarkersOrGetRanges(params string[] markers)
    {
        if (markers.Length == 0)
        {
            return [.. Ranges().Cast<IFourslashMarkerOrRange>()];
        }
        var result = new List<IFourslashMarkerOrRange>(markers.Length);
        foreach (string markerName in markers)
        {
            result.Add(MarkerByName(markerName));
        }
        return result;
    }

    // fourslash.go:3556-3572 — the MarkerOrRangeOrName widening of VerifyBaselineRename /
    // VerifyBaselineDocumentHighlights. Go additionally spreads range/marker slices into the
    // variadic (`ToAny(x)...`); C# has no variadic spread, so a slice element unfolds
    // recursively (a string is never unfolded).
    private List<IFourslashMarkerOrRange> ToMarkerOrRanges(IEnumerable<object?> markerOrNameOrRanges)
    {
        var result = new List<IFourslashMarkerOrRange>();
        AddMarkerOrRanges(result, markerOrNameOrRanges);
        return result;
    }

    private void AddMarkerOrRanges(List<IFourslashMarkerOrRange> result, System.Collections.IEnumerable enumerable)
    {
        foreach (object? item in enumerable)
        {
            switch (item)
            {
                case null:
                    break;
                case string name:
                    result.Add(MarkerByName(name));
                    break;
                case IFourslashMarkerOrRange markerOrRange:
                    result.Add(markerOrRange);
                    break;
                case System.Collections.IEnumerable slice:
                    AddMarkerOrRanges(result, slice);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Invalid marker or range type: " + item.GetType().Name
                        + ". Expected string, Marker, or RangeMarker.");
            }
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2399-2425 — VerifyBaselineFindAllReferences.
    // ---------------------------------------------------------------------

    public void VerifyBaselineFindAllReferences(params string[] markers)
    {
        foreach (IFourslashMarkerOrRange markerOrRange in lookupMarkersOrGetRanges(markers))
        {
            GoToMarkerOrRange(markerOrRange);

            // fourslash.go:2411-2422 — the TextDocumentReferences request is answered from the
            // session's current program (D-012: no JSON-RPC round trip); the raw entry returns
            // the reference span + file, which is exactly the lsproto.Location the Go baseline
            // receives.
            ReferenceLocation[] references = LS().GetReferencesAtPosition(
                activeFilename,
                caretOffset(),
                cancellationToken);
            var locations = new List<LsProto.Location>(references.Length);
            foreach (ReferenceLocation reference in references)
            {
                locations.Add(new LsProto.Location
                {
                    Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(reference.FileName),
                    Range = converters.ToLSPRange(getScriptInfo(reference.FileName)!, reference.TextSpan),
                });
            }

            addResultToBaseline(
                FourslashBaselineCommand.FindAllReferences,
                GetBaselineForLocationsWithFileContents(
                    locations,
                    new BaselineFourslashLocationsOptions
                    {
                        Marker = markerOrRange as Marker,
                        MarkerName = "/*FIND ALL REFS*/",
                    }));
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2566-2694 — the go-to family.
    // ---------------------------------------------------------------------

    public void VerifyBaselineGoToDefinition(bool includeOriginalSelectionRange, params string[] markers) =>
        VerifyBaselineDefinitions(
            FourslashBaselineCommand.GoToDefinition,
            "/*GOTO DEF*/",
            static (f, position) => f.LS().ProvideDefinition(
                f.activeFilename,
                position,
                f.cancellationToken) ?? new LsProto.LocationOrLocationsOrDefinitionLinksOrNull(),
            includeOriginalSelectionRange,
            markers);

    public void VerifyBaselineGoToTypeDefinition(params string[] markers) =>
        VerifyBaselineDefinitions(
            FourslashBaselineCommand.GoToTypeDefinition,
            "/*GOTO TYPE*/",
            static (f, position) => f.LS().ProvideTypeDefinition(
                f.activeFilename,
                position,
                f.cancellationToken) ?? new LsProto.LocationOrLocationsOrDefinitionLinksOrNull(),
            includeOriginalSelectionRange: false,
            markers);

    public void VerifyBaselineGoToSourceDefinition(params string[] markers) =>
        VerifyBaselineDefinitions(
            FourslashBaselineCommand.GoToSourceDefinition,
            "/*GOTO SOURCE DEF*/",
            static (f, position) => f.LS().ProvideSourceDefinition(
                f.activeFilename,
                f.converters.PositionToLineAndCharacter(f.getScriptInfo(f.activeFilename)!, position),
                clientSupportsLink: false,
                f.cancellationToken),
            includeOriginalSelectionRange: false,
            markers);

    public void VerifyBaselineGoToImplementation(params string[] markerNames) =>
        VerifyBaselineDefinitions(
            FourslashBaselineCommand.GoToImplementation,
            "/*GOTO IMPL*/",
            static (f, position) => f.LS().ProvideImplementations(
                f.cancellationToken,
                new LsProto.ImplementationParams
                {
                    TextDocument = new LsProto.TextDocumentIdentifier
                    {
                        Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(f.activeFilename),
                    },
                    Position = f.converters.PositionToLineAndCharacter(f.getScriptInfo(f.activeFilename)!, position),
                },
                orchestrator: null),
            includeOriginalSelectionRange: false,
            markerNames);

    // fourslash.go:2590-2644 — verifyBaselineDefinitions.
    private void VerifyBaselineDefinitions(
        FourslashBaselineCommand definitionCommand,
        string definitionMarker,
        Func<FourslashTest, int, LsProto.LocationOrLocationsOrDefinitionLinksOrNull> getDefinitions,
        bool includeOriginalSelectionRange,
        params string[] markers)
    {
        foreach (IFourslashMarkerOrRange markerOrRange in lookupMarkersOrGetRanges(markers))
        {
            GoToMarkerOrRange(markerOrRange);

            LsProto.LocationOrLocationsOrDefinitionLinksOrNull result =
                getDefinitions(this, caretOffset());

            var resultAsSpans = new List<DocumentSpan>();
            DocumentSpan? additionalSpan = null;
            if (result.Locations != null)
            {
                foreach (LsProto.Location location in result.Locations)
                {
                    resultAsSpans.Add(LocationToSpan(location));
                }
            }
            else if (result.Location != null)
            {
                resultAsSpans.Add(LocationToSpan(result.Location));
            }
            else if (result.DefinitionLinks != null)
            {
                LsProto.Range? originRange = null;
                foreach (LsProto.LocationLink link in result.DefinitionLinks)
                {
                    if (originRange != null
                        && (link.OriginSelectionRange == null || !RangesEqual(originRange, link.OriginSelectionRange)))
                    {
                        throw new InvalidOperationException("multiple different origin ranges in definition links");
                    }
                    originRange = link.OriginSelectionRange;
                    LsProto.Range? contextSpan = null;
                    if (!RangesEqual(link.TargetRange, link.TargetSelectionRange) && !isStradaServer)
                    {
                        contextSpan = link.TargetRange;
                    }
                    resultAsSpans.Add(new DocumentSpan(link.TargetUri, link.TargetSelectionRange, contextSpan));
                }
                if (originRange != null && includeOriginalSelectionRange)
                {
                    additionalSpan = new DocumentSpan(
                        global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                        originRange);
                }
            }

            addResultToBaseline(
                definitionCommand,
                GetBaselineForSpansWithFileContents(
                    resultAsSpans,
                    new BaselineFourslashLocationsOptions
                    {
                        Marker = markerOrRange as Marker,
                        MarkerName = definitionMarker,
                        AdditionalSpan = additionalSpan,
                        PreserveResultOrder = definitionCommand == FourslashBaselineCommand.GoToSourceDefinition,
                    }));
        }
    }

    // baselineutil.go — locationToSpan.
    private static DocumentSpan LocationToSpan(LsProto.Location location) =>
        new(location.Uri, location.Range);

    // ---------------------------------------------------------------------
    // fourslash.go:2696-2719 — VerifyBaselineWorkspaceSymbol.
    // ---------------------------------------------------------------------

    public void VerifyBaselineWorkspaceSymbol(string query)
    {
        LsProto.SymbolInformation[] symbols = WorkspaceSymbols(query);

        var locationToText = new Dictionary<DocumentSpan, string>(ReferenceEqualityComparer.Instance);
        var groupedRanges = new List<KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>>();
        var order = new List<LsProto.DocumentUri>();
        var byUri = new Dictionary<string, List<DocumentSpan>>(StringComparer.Ordinal);
        foreach (LsProto.SymbolInformation symbol in symbols)
        {
            DocumentSpan span = LocationToSpan(symbol.Location);
            string key = symbol.Location.Uri.Value;
            if (!byUri.TryGetValue(key, out List<DocumentSpan>? bucket))
            {
                bucket = [];
                byUri[key] = bucket;
                order.Add(symbol.Location.Uri);
            }
            bucket.Add(span);
            locationToText[span] = SymbolInformationToData(symbol);
        }
        foreach (LsProto.DocumentUri uri in order)
        {
            groupedRanges.Add(new KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>(uri, byUri[uri.Value]));
        }

        addResultToBaseline(
            FourslashBaselineCommand.WorkspaceSymbol,
            GetBaselineForGroupedSpansWithFileContents(
                groupedRanges,
                new BaselineFourslashLocationsOptions
                {
                    GetLocationData = span => locationToText.TryGetValue(span, out string? text) ? text : "",
                }));
    }

    // baselineutil.go:1180-1182 — symbolInformationToData.
    private static string SymbolInformationToData(LsProto.SymbolInformation symbol) =>
        "{| name: " + symbol.Name + ", kind: " + symbol.Kind + " |}";

    // ---------------------------------------------------------------------
    // fourslash.go:5496-5522 — VerifyWorkspaceSymbol (`verify.navigateTo`).
    // ---------------------------------------------------------------------

    public void VerifyWorkspaceSymbol(VerifyWorkspaceSymbolCase[] cases)
    {
        UserPreferences originalPreferences = userPreferences;
        foreach (VerifyWorkspaceSymbolCase testCase in cases)
        {
            UserPreferences preferences = testCase.Preferences ?? UserPreferences.NewDefaultUserPreferences();
            Configure(preferences);
            LsProto.SymbolInformation[] actual = WorkspaceSymbols(testCase.Pattern);

            // The corpus spells the expected set as `Exact` (VerifyWorkspaceSymbolCase.Exact);
            // Go's `Includes` arm has no corpus caller yet.
            if (testCase.Exact == null)
            {
                throw new InvalidOperationException("Test case must have 'Exact' field set");
            }
            VerifyExactSymbols(actual, testCase.Exact, "Workspace symbols mismatch with pattern '" + testCase.Pattern + "'");
        }
        Configure(originalPreferences);
    }

    // symbols.go:524-596 — ProvideWorkspaceSymbols, queried from the active project's program.
    private LsProto.SymbolInformation[] WorkspaceSymbols(string query)
    {
        LsProto.SymbolInformationsOrWorkspaceSymbolsOrNull result =
            global::Ts.Ls.LanguageService.ProvideWorkspaceSymbols(
                new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
                [LS().GetProgram()],
                LS().Converters(),
                userPreferences,
                query);
        return result.SymbolInformations ?? [];
    }

    // fourslash.go:5524-5536 — verifyExactSymbols.
    private static void VerifyExactSymbols(LsProto.SymbolInformation[] actual, LsProto.SymbolInformation[] expected, string prefix)
    {
        if (actual.Length != expected.Length)
        {
            throw new InvalidOperationException(
                prefix + ": Expected " + expected.Length + " symbols, but got " + actual.Length);
        }
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(
                SymbolsEqual(actual[i], expected[i]),
                prefix + ": symbol " + i + " mismatch");
        }
    }

    // The field-wise comparison Go gets from cmp.Diff on lsproto.SymbolInformation.
    private static bool SymbolsEqual(LsProto.SymbolInformation a, LsProto.SymbolInformation b) =>
        a.Name == b.Name
        && a.Kind == b.Kind
        && a.ContainerName == b.ContainerName
        && a.Location.Uri.Value == b.Location.Uri.Value
        && RangesEqual(a.Location.Range, b.Location.Range);

    // ---------------------------------------------------------------------
    // fourslash.go:2721-2801 — VerifyOutliningSpans / VerifyFoldingRangeLines.
    // ---------------------------------------------------------------------

    public void VerifyOutliningSpans(params LsProto.FoldingRangeKind[] foldingRangeKind)
    {
        LsProto.FoldingRange[] actualRanges = LS().ProvideFoldingRange(
            activeFilename,
            lineFoldingOnly: false,
            supportsCollapsedText: false,
            cancellationToken);
        if (foldingRangeKind.Length > 0)
        {
            LsProto.FoldingRangeKind targetKind = foldingRangeKind[0];
            actualRanges = [.. actualRanges.Where(range => range.Kind is { } kind && kind.Value == targetKind.Value)];
        }

        List<RangeMarker> ranges = Ranges();
        if (actualRanges.Length != ranges.Count)
        {
            throw new InvalidOperationException(
                "verifyOutliningSpans failed - expected total spans to be " + ranges.Count
                + ", but was " + actualRanges.Length);
        }

        ranges.Sort((a, b) => ComparePositions(a.LSPos(), b.LSPos()));

        for (int i = 0; i < ranges.Count; i++)
        {
            LsProto.FoldingRange actualRange = actualRanges[i];
            RangeMarker expectedRange = ranges[i];
            var startPos = new LsProto.Position
            {
                Line = actualRange.StartLine,
                Character = actualRange.StartCharacter ?? 0,
            };
            var endPos = new LsProto.Position
            {
                Line = actualRange.EndLine,
                Character = actualRange.EndCharacter ?? 0,
            };

            if (ComparePositions(startPos, expectedRange.LSRange.Start) != 0
                || ComparePositions(endPos, expectedRange.LSRange.End) != 0)
            {
                throw new InvalidOperationException(
                    "verifyOutliningSpans failed - span " + (i + 1) + " has invalid positions:\n"
                    + "  actual: start (" + actualRange.StartLine + "," + (actualRange.StartCharacter ?? 0)
                    + "), end (" + actualRange.EndLine + "," + (actualRange.EndCharacter ?? 0) + ")\n"
                    + "  expected: start (" + expectedRange.LSRange.Start.Line + "," + expectedRange.LSRange.Start.Character
                    + "), end (" + expectedRange.LSRange.End.Line + "," + expectedRange.LSRange.End.Character + ")");
            }
        }
    }

    // fourslash.go:2770-2774 — FoldingRangeLineExpected.
    public readonly record struct FoldingRangeLineExpected(uint StartLine, uint EndLine);

    // fourslash.go:2776-2801 — VerifyFoldingRangeLines.
    public void VerifyFoldingRangeLines(FoldingRangeLineExpected[] expected)
    {
        LsProto.FoldingRange[] actualRanges = LS().ProvideFoldingRange(
            activeFilename,
            lineFoldingOnly: false,
            supportsCollapsedText: false,
            cancellationToken);
        if (actualRanges.Length != expected.Length)
        {
            throw new InvalidOperationException(
                "verifyFoldingRangeLines failed - expected " + expected.Length + " ranges, got " + actualRanges.Length);
        }
        for (int i = 0; i < expected.Length; i++)
        {
            LsProto.FoldingRange got = actualRanges[i];
            if (got.StartLine != expected[i].StartLine || got.EndLine != expected[i].EndLine)
            {
                throw new InvalidOperationException(
                    "verifyFoldingRangeLines failed - range " + i + ": expected (startLine=" + expected[i].StartLine
                    + ", endLine=" + expected[i].EndLine + "), got (startLine=" + got.StartLine + ", endLine=" + got.EndLine + ")");
            }
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2803-2973 — VerifyBaselineHover / VerifyBaselineHoverWithVerbosity.
    // ---------------------------------------------------------------------

    public void VerifyBaselineHover()
    {
        var markersAndItems = new List<MarkerAndItem<LsProto.Hover>>();
        foreach (Marker marker in Markers())
        {
            if (marker.Name == null)
            {
                continue;
            }
            LsProto.HoverOrNull? result = LS().ProvideHover(
                new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
                new LsProto.HoverParams
                {
                    TextDocument = new LsProto.TextDocumentIdentifier
                    {
                        Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(marker.FileName),
                    },
                    Position = marker.LSPosition,
                });
            markersAndItems.Add(new MarkerAndItem<LsProto.Hover>(marker, result?.Hover));
        }

        addResultToBaseline(
            FourslashBaselineCommand.QuickInfo,
            AnnotateContentWithTooltips(
                markersAndItems,
                "quickinfo",
                static hover => hover?.Range,
                static (hover, _) => HoverTooltipLines(hover)));

        // !!! JSON section (core.StringifyJson(markersAndItems)) — see the file header.
    }

    // fourslash.go:2827-2850 — getTooltipLines for a hover.
    private static string[] HoverTooltipLines(LsProto.Hover? item)
    {
        if (item == null)
        {
            return [];
        }
        var result = new List<string>();
        if (item.Contents.MarkupContent != null)
        {
            result.AddRange(SplitLines(item.Contents.MarkupContent.Value));
        }
        if (item.Contents.String != null)
        {
            result.AddRange(SplitLines(item.Contents.String));
        }
        if (item.Contents.MarkedStringWithLanguage != null)
        {
            AppendLinesForMarkedStringWithLanguage(result, item.Contents.MarkedStringWithLanguage);
        }
        if (item.Contents.MarkedStrings != null)
        {
            foreach (LsProto.StringOrMarkedStringWithLanguage ms in item.Contents.MarkedStrings)
            {
                if (ms.MarkedStringWithLanguage != null)
                {
                    AppendLinesForMarkedStringWithLanguage(result, ms.MarkedStringWithLanguage);
                }
                else if (ms.String != null)
                {
                    result.Add(ms.String);
                }
            }
        }
        return [.. result];
    }

    // fourslash.go:2860-2865 — appendLinesForMarkedStringWithLanguage.
    private static void AppendLinesForMarkedStringWithLanguage(List<string> result, LsProto.MarkedStringWithLanguage ms)
    {
        result.Add("```" + ms.Language);
        result.Add(ms.Value);
        result.Add("```");
    }

    // fourslash.go:2867-2870 — hoverWithVerbosity.
    private sealed class HoverWithVerbosity
    {
        public LsProto.Hover? Hover;
        public int VerbosityLevel;
    }

    // fourslash.go:2886-2973 — VerifyBaselineHoverWithVerbosity.
    public void VerifyBaselineHoverWithVerbosity(Dictionary<string, int[]> verbosityLevels)
    {
        var markersAndItems = new List<MarkerAndItem<HoverWithVerbosity>>();
        foreach (Marker marker in Markers())
        {
            if (marker.Name == null)
            {
                continue;
            }
            int[] levels = verbosityLevels.TryGetValue(marker.Name, out int[]? configuredLevels)
                ? configuredLevels
                : [0];
            for (int i = 0; i < levels.Length; i++)
            {
                int level = levels[i];
                LsProto.HoverOrNull? result = LS().ProvideHover(
                    new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
                    new LsProto.HoverParams
                    {
                        TextDocument = new LsProto.TextDocumentIdentifier
                        {
                            Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(marker.FileName),
                        },
                        Position = marker.LSPosition,
                        VerbosityLevel = level > 0 ? level : null,
                    });
                var item = new HoverWithVerbosity { Hover = result?.Hover, VerbosityLevel = level };
                // If the previous level said it can't expand further, verify the content is identical.
                if (i > 0 && level > levels[i - 1])
                {
                    HoverWithVerbosity? prevItem = markersAndItems[^1].Item;
                    if (prevItem?.Hover != null && !prevItem.Hover.CanIncreaseVerbosity)
                    {
                        string prevContent = HoverContentString(prevItem.Hover);
                        string curContent = HoverContentString(item.Hover);
                        if (prevContent != curContent)
                        {
                            Assert.Fail(
                                "At marker \"" + marker.Name + "\": verbosity level " + level
                                + " response differs from level " + levels[i - 1] + ", but level " + levels[i - 1]
                                + " had canIncreaseVerbosity=false.");
                        }
                    }
                }
                markersAndItems.Add(new MarkerAndItem<HoverWithVerbosity>(marker, item));
            }
        }

        addResultToBaseline(
            FourslashBaselineCommand.QuickInfo,
            AnnotateContentWithTooltips(
                markersAndItems,
                "quickinfo",
                static item => item?.Hover?.Range,
                static (item, _) => HoverVerbosityTooltipLines(item)));

        // !!! JSON section (core.StringifyJson(markersAndItems)) — see the file header.
    }

    // fourslash.go:2873-2884 — hoverContentString.
    private static string HoverContentString(LsProto.Hover? hover)
    {
        if (hover == null)
        {
            return "";
        }
        if (hover.Contents.MarkupContent != null)
        {
            return hover.Contents.MarkupContent.Value;
        }
        return hover.Contents.String ?? "";
    }

    // fourslash.go:2937-2965 — getTooltipLines for a hoverWithVerbosity.
    private static string[] HoverVerbosityTooltipLines(HoverWithVerbosity? item)
    {
        if (item?.Hover == null)
        {
            return [];
        }
        var result = new List<string>(HoverTooltipLines(item.Hover));
        result.Add("(verbosity level: " + item.VerbosityLevel + ")");
        return [.. result];
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2975-3083 — VerifyBaselineSignatureHelp.
    // ---------------------------------------------------------------------

    public void VerifyBaselineSignatureHelp()
    {
        var markersAndItems = new List<MarkerAndItem<LsProto.SignatureHelp>>();
        foreach (Marker marker in Markers())
        {
            if (marker.Name == null)
            {
                continue;
            }
            LsProto.SignatureHelpOrNull? result = LS().ProvideSignatureHelp(
                new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
                global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(marker.FileName),
                marker.LSPosition,
                context: null);
            markersAndItems.Add(new MarkerAndItem<LsProto.SignatureHelp>(marker, result?.SignatureHelp));
        }

        addResultToBaseline(
            FourslashBaselineCommand.SignatureHelp,
            AnnotateContentWithTooltips(
                markersAndItems,
                "signaturehelp",
                // SignatureHelp has no range like hover does.
                static _ => null,
                static (item, _) => SignatureHelpTooltipLines(item)));

        // !!! JSON section (core.StringifyJson(markersAndItems)) — see the file header.
    }

    // fourslash.go:2997-3075 — getTooltipLines for a signature help.
    private static string[] SignatureHelpTooltipLines(LsProto.SignatureHelp? item)
    {
        if (item == null || item.Signatures.Length == 0)
        {
            return ["No signature help available"];
        }

        int activeSignature = 0;
        if (item.ActiveSignature is { } active && (int)active < item.Signatures.Length)
        {
            activeSignature = (int)active;
        }
        LsProto.SignatureInformation sig = item.Signatures[activeSignature];

        string signatureLine = sig.Label;
        string activeParamLine = "";

        // Per-signature activeParameter takes precedence over the top-level one per LSP spec.
        LsProto.UintegerOrNull? activeParamPtr = sig.ActiveParameter ?? item.ActiveParameter;

        if (activeParamPtr?.Uinteger is { } activeParamIndex
            && (int)activeParamIndex < (sig.Parameters?.Length ?? 0))
        {
            LsProto.ParameterInformation activeParam = sig.Parameters![(int)activeParamIndex];
            string activeParamLabel = activeParam.Label.String
                ?? (activeParam.Label.Tuple is { } tuple
                    ? signatureLine[(int)tuple.Item1..(int)tuple.Item2]
                    : throw new InvalidOperationException("Unsupported param label kind."));
            signatureLine = ReplaceFirst(signatureLine, activeParamLabel, "**" + activeParamLabel + "**");

            if (activeParam.Documentation != null)
            {
                activeParamLine = activeParam.Documentation.MarkupContent?.Value
                    ?? activeParam.Documentation.String
                    ?? "";
                activeParamLine = "- `" + activeParamLabel + "`: " + activeParamLine;
            }
        }

        var result = new List<string>(16) { signatureLine };
        if (activeParamLine.Length != 0)
        {
            result.Add(activeParamLine);
        }
        if (sig.Documentation != null)
        {
            if (sig.Documentation.MarkupContent != null)
            {
                result.AddRange(SplitLines(sig.Documentation.MarkupContent.Value));
            }
            else if (sig.Documentation.String != null)
            {
                result.AddRange(SplitLines(sig.Documentation.String));
            }
            else
            {
                throw new InvalidOperationException("Unsupported documentation format.");
            }
        }
        return [.. result];
    }

    // ---------------------------------------------------------------------
    // fourslash.go:3085-3221 — VerifyBaselineSelectionRanges.
    // ---------------------------------------------------------------------

    public void VerifyBaselineSelectionRanges()
    {
        var result = new StringBuilder();
        const string newLine = "\n";

        List<Marker> markers = Markers();
        for (int i = 0; i < markers.Count; i++)
        {
            Marker marker = markers[i];
            if (i > 0)
            {
                result.Append(newLine);
                result.Append(new string('=', 80));
                result.Append(newLine);
                result.Append(newLine);
            }

            FourslashScriptInfo script = getScriptInfo(marker.FileName)!;
            string fileContent = script.Content;

            int markerPos = marker.Position;
            result.Append(fileContent[..markerPos]).Append("/**/").Append(fileContent[markerPos..]).Append(newLine);

            // Go sends the LSP position; the raw entry takes the UTF-16 offset (marker.Position).
            LsProto.SelectionRange[] rangesAtMarker = LS().ProvideSelectionRanges(marker.FileName, [marker.Position]);

            if (rangesAtMarker.Length == 0)
            {
                result.Append("No selection ranges available\n");
                continue;
            }

            LsProto.SelectionRange? selectionRange = rangesAtMarker[0];
            result.Append(newLine);

            while (selectionRange != null)
            {
                int start = converters.LineAndCharacterToPosition(script, selectionRange.Range.Start);
                int end = converters.LineAndCharacterToPosition(script, selectionRange.Range.End);

                var masked = new char[fileContent.Length];
                for (int c = 0; c < fileContent.Length; c++)
                {
                    char ch = fileContent[c];
                    if (c >= start && c < end)
                    {
                        masked[c] = ch == ' ' ? '\u2022' : ch;
                    }
                    else
                    {
                        masked[c] = ch is '\n' or '\r' ? ch : ' ';
                    }
                }

                string maskedStr = new string(masked);
                maskedStr = maskedStr.Replace("\n", "\u21b2\n", StringComparison.Ordinal);
                maskedStr = maskedStr.Replace("\r", "\u21b2\r", StringComparison.Ordinal);

                string[] lines = SplitLines(maskedStr);
                List<string> nonBlankLines = [];
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length != 0 && trimmed != "\u21b2")
                    {
                        nonBlankLines.Add(line);
                    }
                }
                maskedStr = string.Join("\n", nonBlankLines);

                int leadingWidth = -1;
                int trailingWidth = -1;
                for (int c = 0; c < maskedStr.Length; c++)
                {
                    if (IsRealCharacter(maskedStr[c]))
                    {
                        leadingWidth = c;
                        break;
                    }
                }
                for (int c = maskedStr.Length - 1; c >= 0; c--)
                {
                    if (IsRealCharacter(maskedStr[c]))
                    {
                        trailingWidth = c;
                        break;
                    }
                }

                if (leadingWidth != -1 && trailingWidth != -1 && leadingWidth <= trailingWidth)
                {
                    string prefix = maskedStr[..leadingWidth];
                    string middle = maskedStr[leadingWidth..(trailingWidth + 1)];
                    string suffix = maskedStr[(trailingWidth + 1)..];
                    middle = middle.Replace("\u2022", " ", StringComparison.Ordinal);
                    middle = middle.Replace("\u21b2", "", StringComparison.Ordinal);
                    maskedStr = prefix + middle + suffix;
                }

                if (maskedStr.Contains('\n', StringComparison.Ordinal))
                {
                    result.Append(newLine);
                }
                result.Append(maskedStr);
                if (!maskedStr.EndsWith('\n'))
                {
                    result.Append(newLine);
                }

                selectionRange = selectionRange.Parent;
            }
        }

        string text = result.ToString();
        addResultToBaseline(
            FourslashBaselineCommand.SmartSelection,
            text.EndsWith('\n') ? text[..^1] : text);
    }

    // fourslash.go:3175-3177 — isRealCharacter.
    private static bool IsRealCharacter(char ch) =>
        ch != '\u2022' && ch != '\u21b2' && !global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(ch);

    // ---------------------------------------------------------------------
    // fourslash.go:3223-3540 — VerifyBaselineCallHierarchy.
    // ---------------------------------------------------------------------

    public void VerifyBaselineCallHierarchy()
    {
        string fileName = activeFilename;
        LsProto.Position position = currentCaretPosition;

        LsProto.CallHierarchyItem[] prepareResult = LS().ProvidePrepareCallHierarchy(
            fileName,
            caretOffset(),
            cancellationToken);
        if (prepareResult.Length == 0)
        {
            addResultToBaseline(FourslashBaselineCommand.CallHierarchy, "No call hierarchy items available");
            return;
        }

        var result = new StringBuilder();
        foreach (LsProto.CallHierarchyItem item in prepareResult)
        {
            var seen = new HashSet<CallHierarchyItemKey>();
            string itemFileName = item.Uri.FileName();
            FourslashScriptInfo? script = getOrLoadScriptInfo(itemFileName);
            FormatCallHierarchyItem(result, script!, item, CallHierarchyItemDirection.Root, seen, "");
        }

        string text = result.ToString();
        addResultToBaseline(
            FourslashBaselineCommand.CallHierarchy,
            text.EndsWith('\n') ? text[..^1] : text);
    }

    // fourslash.go:3252-3258 — callHierarchyItemDirection.
    private enum CallHierarchyItemDirection
    {
        Root = 0,
        Incoming = 1,
        Outgoing = 2,
    }

    // fourslash.go:3260-3264 — callHierarchyItemKey.
    private readonly record struct CallHierarchyItemKey(
        LsProto.DocumentUri Uri,
        LsProto.Range Range,
        CallHierarchyItemDirection Direction);

    // fourslash.go:3270-3413 — formatCallHierarchyItem.
    private void FormatCallHierarchyItem(
        StringBuilder result,
        FourslashScriptInfo file,
        LsProto.CallHierarchyItem item,
        CallHierarchyItemDirection direction,
        HashSet<CallHierarchyItemKey> seen,
        string prefix)
    {
        var key = new CallHierarchyItemKey(item.Uri, item.Range, direction);
        bool alreadySeen = !seen.Add(key);

        bool incomingSkip = false;
        bool incomingSeen = false;
        LsProto.CallHierarchyIncomingCall[] incomingValues = [];
        bool outgoingSkip = false;
        bool outgoingSeen = false;
        LsProto.CallHierarchyOutgoingCall[] outgoingValues = [];

        if (direction == CallHierarchyItemDirection.Outgoing)
        {
            incomingSkip = true;
        }
        else if (alreadySeen)
        {
            incomingSeen = true;
        }
        else
        {
            incomingValues = LS().ProvideCallHierarchyIncomingCalls(
                item,
                cancellationToken);
        }

        if (direction == CallHierarchyItemDirection.Incoming)
        {
            outgoingSkip = true;
        }
        else if (alreadySeen)
        {
            outgoingSeen = true;
        }
        else
        {
            outgoingValues = LS().ProvideCallHierarchyOutgoingCalls(item, cancellationToken);
        }

        string trailingPrefix = prefix;
        result.Append(prefix).Append("\u256d name: ").Append(item.Name).Append('\n');
        result.Append(prefix).Append("\u251c kind: ").Append(SymbolKindToLowercase(item.Kind)).Append('\n');
        if (item.Detail != null && item.Detail.Length != 0)
        {
            result.Append(prefix).Append("\u251c containerName: ").Append(item.Detail).Append('\n');
        }
        result.Append(prefix).Append("\u251c file: ").Append(item.Uri.FileName()).Append('\n');
        result.Append(prefix).Append("\u251c span:\n");
        FormatCallHierarchyItemSpan(result, file, item.Range, prefix + "\u2502 ", prefix + "\u2502 ");
        result.Append(prefix).Append("\u251c selectionSpan:\n");
        FormatCallHierarchyItemSpan(result, file, item.SelectionRange, prefix + "\u2502 ", prefix + "\u2502 ");

        // Handle incoming calls.
        if (incomingSeen)
        {
            if (outgoingSkip)
            {
                result.Append(trailingPrefix).Append("\u2570 incoming: ...\n");
            }
            else
            {
                result.Append(prefix).Append("\u251c incoming: ...\n");
            }
        }
        else if (!incomingSkip)
        {
            if (incomingValues.Length == 0)
            {
                if (outgoingSkip)
                {
                    result.Append(trailingPrefix).Append("\u2570 incoming: none\n");
                }
                else
                {
                    result.Append(prefix).Append("\u251c incoming: none\n");
                }
            }
            else
            {
                result.Append(prefix).Append("\u251c incoming:\n");
                for (int i = 0; i < incomingValues.Length; i++)
                {
                    LsProto.CallHierarchyIncomingCall incomingCall = incomingValues[i];
                    FourslashScriptInfo? fromFile = getOrLoadScriptInfo(incomingCall.From.Uri.FileName());
                    result.Append(prefix).Append("\u2502 \u256d from:\n");
                    FormatCallHierarchyItem(result, fromFile!, incomingCall.From, CallHierarchyItemDirection.Incoming, seen, prefix + "\u2502 \u2502 ");
                    result.Append(prefix).Append("\u2502 \u251c fromSpans:\n");

                    string fromSpansTrailingPrefix = trailingPrefix + "\u2570 \u2570 ";
                    if (i < incomingValues.Length - 1)
                    {
                        fromSpansTrailingPrefix = prefix + "\u2502 \u2570 ";
                    }
                    else if (!outgoingSkip && (!outgoingSeen || outgoingValues.Length > 0))
                    {
                        fromSpansTrailingPrefix = prefix + "\u2502 \u2570 ";
                    }
                    FormatCallHierarchyItemSpans(result, fromFile!, incomingCall.FromRanges, prefix + "\u2502 \u2502 ", fromSpansTrailingPrefix);
                }
            }
        }

        // Handle outgoing calls.
        if (outgoingSeen)
        {
            result.Append(trailingPrefix).Append("\u2570 outgoing: ...\n");
        }
        else if (!outgoingSkip)
        {
            if (outgoingValues.Length == 0)
            {
                result.Append(trailingPrefix).Append("\u2570 outgoing: none\n");
            }
            else
            {
                result.Append(prefix).Append("\u251c outgoing:\n");
                for (int i = 0; i < outgoingValues.Length; i++)
                {
                    LsProto.CallHierarchyOutgoingCall outgoingCall = outgoingValues[i];
                    FourslashScriptInfo? toFile = getOrLoadScriptInfo(outgoingCall.To.Uri.FileName());
                    result.Append(prefix).Append("\u2502 \u256d to:\n");
                    FormatCallHierarchyItem(result, toFile!, outgoingCall.To, CallHierarchyItemDirection.Outgoing, seen, prefix + "\u2502 \u2502 ");
                    result.Append(prefix).Append("\u2502 \u251c fromSpans:\n");

                    string fromSpansTrailingPrefix = trailingPrefix + "\u2570 \u2570 ";
                    if (i < outgoingValues.Length - 1)
                    {
                        fromSpansTrailingPrefix = prefix + "\u2502 \u2570 ";
                    }
                    FormatCallHierarchyItemSpans(result, file, outgoingCall.FromRanges, prefix + "\u2502 \u2502 ", fromSpansTrailingPrefix);
                }
            }
        }
    }

    // fourslash.go:3266-3268 — symbolKindToLowercase: strings.ToLower over the enum's name.
    // The LSP symbol-kind names are ASCII, so the per-char ASCII fold is the documented
    // ASCII-only path (string.ToLowerInvariant is banned, RS0030).
    private static string SymbolKindToLowercase(LsProto.SymbolKind kind)
    {
        string name = kind.ToString();
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            builder.Append(ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch);
        }
        return builder.ToString();
    }

    // fourslash.go:3415-3513 — formatCallHierarchyItemSpan.
    private void FormatCallHierarchyItemSpan(
        StringBuilder result,
        FourslashScriptInfo file,
        LsProto.Range span,
        string prefix,
        string closingPrefix)
    {
        LsProto.Position startLc = span.Start;
        LsProto.Position endLc = span.End;

        List<int> lineStarts = ComputeLineStarts(file.Content);

        int contextStart = converters.LineAndCharacterToPosition(file, span.Start);
        int contextEnd = converters.LineAndCharacterToPosition(file, span.End);

        while (contextStart > 0 && file.Content[contextStart - 1] is not ('\n' or '\r'))
        {
            contextStart--;
        }
        while (contextEnd < file.Content.Length && file.Content[contextEnd] is not ('\n' or '\r'))
        {
            contextEnd++;
        }

        int contextStartLine = (int)startLc.Line;
        int contextEndLine = (int)endLc.Line;
        int lineNumWidth = (contextEndLine + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 2;

        result.Append(prefix)
            .Append("\u256d ")
            .Append(file.FileName)
            .Append(':').Append(startLc.Line + 1).Append(':').Append(startLc.Character + 1)
            .Append('-').Append(endLc.Line + 1).Append(':').Append(endLc.Character + 1)
            .Append('\n');

        for (int lineNum = contextStartLine; lineNum <= contextEndLine; lineNum++)
        {
            int lineStart = lineStarts[lineNum];
            int lineEnd = lineNum + 1 < lineStarts.Count ? lineStarts[lineNum + 1] : file.Content.Length;
            string lineContent = file.Content[lineStart..lineEnd].TrimEnd('\r', '\n');

            string lineNumStr = (lineNum + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            string paddedLineNum = new string(' ', lineNumWidth - lineNumStr.Length - 1) + lineNumStr;
            if (lineContent.Length == 0)
            {
                result.Append(prefix).Append("\u2502 ").Append(paddedLineNum).Append('\n');
            }
            else
            {
                result.Append(prefix).Append("\u2502 ").Append(paddedLineNum).Append(' ').Append(lineContent).Append('\n');
            }

            if (lineNum >= (int)startLc.Line && lineNum <= (int)endLc.Line)
            {
                int selStart = 0;
                int selEnd = lineContent.Length;
                if (lineNum == (int)startLc.Line)
                {
                    selStart = (int)startLc.Character;
                }
                if (lineNum == (int)endLc.Line)
                {
                    selEnd = (int)endLc.Character;
                }

                bool isEmpty = startLc.Line == endLc.Line && startLc.Character == endLc.Character;
                if (isEmpty)
                {
                    result.Append(prefix)
                        .Append("\u2502 ")
                        .Append(new string(' ', lineNumWidth + selStart))
                        .Append("<\n");
                }
                else
                {
                    int selLength = Math.Max(selEnd - selStart, 1);
                    if (lineNum < (int)endLc.Line && selEnd > lineContent.Length)
                    {
                        selEnd = lineContent.Length;
                        selLength = selEnd - selStart;
                    }
                    result.Append(prefix)
                        .Append("\u2502 ")
                        .Append(new string(' ', lineNumWidth + selStart))
                        .Append(new string('^', selLength))
                        .Append('\n');
                }
            }
        }

        result.Append(closingPrefix).Append("\u2570\n");
    }

    // fourslash.go:3515-3523 — computeLineStarts.
    private static List<int> ComputeLineStarts(string content)
    {
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }
        return lineStarts;
    }

    // fourslash.go:3525-3540 — formatCallHierarchyItemSpans.
    private void FormatCallHierarchyItemSpans(
        StringBuilder result,
        FourslashScriptInfo file,
        LsProto.Range[] spans,
        string prefix,
        string trailingPrefix)
    {
        for (int i = 0; i < spans.Length; i++)
        {
            string closingPrefix = i == spans.Length - 1 ? trailingPrefix : prefix;
            FormatCallHierarchyItemSpan(result, file, spans[i], prefix, closingPrefix);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:3542-3653 — VerifyBaselineDocumentHighlights(WithOptions).
    // ---------------------------------------------------------------------

    // Go: VerifyBaselineDocumentHighlights / VerifyBaselineDocumentHighlightsWithOptions
    // (fourslash.go:3542-3575). Go's variadic `markerOrRangeOrNames ...MarkerOrRangeOrName`
    // widens string / *Marker / *RangeMarker, and the corpus also spreads `test.ranges()` /
    // `test.markers()` into it (Go's `ToAny(...)...`). The C# variadic carries the same three
    // element types plus a spread (an array or list of them); the `filesToSearch` array is the
    // one `string[]` is never a marker, so it is recognized by type wherever it appears.
    public void VerifyBaselineDocumentHighlights(UserPreferences? preferences, params object?[] markerOrRangeOrNames) =>
        VerifyBaselineDocumentHighlightsWithOptions([preferences, null, .. markerOrRangeOrNames]);

    public void VerifyBaselineDocumentHighlightsWithOptions(
        UserPreferences? preferences,
        string[] filesToSearch,
        params object?[] markerOrRangeOrNames) =>
        VerifyBaselineDocumentHighlightsWithOptions([preferences, filesToSearch, .. markerOrRangeOrNames]);

    private void VerifyBaselineDocumentHighlightsWithOptions(object?[] arguments)
    {
        UserPreferences? preferences = null;
        string[]? filesToSearch = null;
        var elements = new List<object?>();
        foreach (object? argument in arguments)
        {
            switch (argument)
            {
                case UserPreferences preferencesValue:
                    preferences = preferencesValue;
                    break;
                case null:
                    break;
                case string[] files:
                    // The `{ filesToSearch: [...] }` options object (fourslash.go:3553).
                    filesToSearch = files;
                    break;
                case object?[] spread:
                    // A spread argument (Go's `ToAny(x)...`) contributes its elements.
                    elements.AddRange(spread);
                    break;
                default:
                    elements.Add(argument);
                    break;
            }
        }

        var markerOrRanges = new List<IFourslashMarkerOrRange>(elements.Count);
        foreach (object? element in elements)
        {
            markerOrRanges.Add(ToMarkerOrRange(element!));
        }
        VerifyBaselineDocumentHighlightsWorker(preferences, filesToSearch, markerOrRanges);
    }

    // fourslash.go:3577-3653 — verifyBaselineDocumentHighlights.
    private void VerifyBaselineDocumentHighlightsWorker(
        UserPreferences? preferences,
        string[]? filesToSearch,
        List<IFourslashMarkerOrRange> markerOrRanges)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            foreach (IFourslashMarkerOrRange markerOrRange in markerOrRanges)
            {
                goToMarker(markerOrRange);

                var spans = new List<LsProto.Location>();
                string header = "";

                if (filesToSearch is { Length: > 0 })
                {
                    var searchUris = new LsProto.DocumentUri[filesToSearch.Length];
                    for (int i = 0; i < filesToSearch.Length; i++)
                    {
                        searchUris[i] = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(filesToSearch[i]);
                    }

                    LsProto.MultiDocumentHighlightsOrNull result = LS().ProvideMultiDocumentHighlights(
                        new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
                        global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                        currentCaretPosition,
                        searchUris);
                    foreach (LsProto.MultiDocumentHighlight multiHighlight in result.MultiDocumentHighlights ?? [])
                    {
                        foreach (LsProto.DocumentHighlight highlight in multiHighlight.Highlights)
                        {
                            spans.Add(new LsProto.Location { Uri = multiHighlight.Uri, Range = highlight.Range });
                        }
                    }

                    var sb = new StringBuilder();
                    sb.Append("// filesToSearch:\n");
                    foreach (string file in filesToSearch)
                    {
                        sb.Append("//   ").Append(file).Append('\n');
                    }
                    sb.Append('\n');
                    header = sb.ToString();
                }
                else
                {
                    LsProto.DocumentHighlight[] highlights = LS().ProvideDocumentHighlights(
                        activeFilename,
                        caretOffset(),
                        cancellationToken);
                    foreach (LsProto.DocumentHighlight highlight in highlights)
                    {
                        spans.Add(new LsProto.Location
                        {
                            Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                            Range = highlight.Range,
                        });
                    }
                }

                addResultToBaseline(
                    FourslashBaselineCommand.DocumentHighlights,
                    header + GetBaselineForLocationsWithFileContents(
                        spans,
                        new BaselineFourslashLocationsOptions
                        {
                            Marker = markerOrRange as Marker,
                            MarkerName = "/*HIGHLIGHTS*/",
                        }));
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:4549-4655 — VerifyBaselineRename.
    // ---------------------------------------------------------------------

    public void VerifyBaselineRename(UserPreferences? preferences, params object?[] markerOrNameOrRanges) =>
        VerifyBaselineRenameWorker(preferences, ToMarkerOrRanges(markerOrNameOrRanges));

    public void VerifyBaselineRenameAtRangesWithText(UserPreferences? preferences, params string[] texts)
    {
        var markerOrRanges = new List<IFourslashMarkerOrRange>();
        foreach (string text in texts)
        {
            if (GetRangesByText().TryGetValue(text, out List<RangeMarker>? bucket))
            {
                markerOrRanges.AddRange(bucket);
            }
        }
        VerifyBaselineRenameWorker(preferences, markerOrRanges);
    }

    // fourslash.go:4575-4655 — verifyBaselineRename.
    private void VerifyBaselineRenameWorker(UserPreferences? preferences, List<IFourslashMarkerOrRange> markerOrRanges)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            foreach (IFourslashMarkerOrRange markerOrRange in markerOrRanges)
            {
                GoToMarkerOrRange(markerOrRange);

                LsProto.WorkspaceEdit? result = LS().ProvideRename(
                    RenameContext(),
                    new LsProto.RenameParams
                    {
                        TextDocument = new LsProto.TextDocumentIdentifier { Uri = DocumentUriFor(activeFilename) },
                        Position = currentCaretPosition,
                        NewName = "?",
                    },
                    orchestrator: null).WorkspaceEdit;

                Dictionary<LsProto.DocumentUri, LsProto.TextEdit[]> changes =
                    result?.Changes ?? new Dictionary<LsProto.DocumentUri, LsProto.TextEdit[]>();

                var spanToText = new Dictionary<DocumentSpan, string>(ReferenceEqualityComparer.Instance);
                var groupedRanges = new List<KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>>();
                var order = new List<LsProto.DocumentUri>();
                var byUri = new Dictionary<string, List<DocumentSpan>>(StringComparer.Ordinal);
                // map-iter: 序无关 — the spans are grouped by uri and the text is attached to each
                // span; the order inside a file is the edit order Go keeps per uri.
                foreach (KeyValuePair<LsProto.DocumentUri, LsProto.TextEdit[]> entry in changes)
                {
                    foreach (LsProto.TextEdit edit in entry.Value)
                    {
                        var span = new DocumentSpan(entry.Key, edit.Range);
                        if (!byUri.TryGetValue(entry.Key.Value, out List<DocumentSpan>? bucket))
                        {
                            bucket = [];
                            byUri[entry.Key.Value] = bucket;
                            order.Add(entry.Key);
                        }
                        bucket.Add(span);
                        spanToText[span] = edit.NewText;
                    }
                }
                foreach (LsProto.DocumentUri uri in order)
                {
                    groupedRanges.Add(new KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>(uri, byUri[uri.Value]));
                }

                var renameOptions = new StringBuilder();
                if (preferences != null)
                {
                    if (!preferences.UseAliasesForRename.IsUnknown())
                    {
                        renameOptions.Append("// @useAliasesForRename: ")
                            .Append(preferences.UseAliasesForRename.IsTrue() ? "true" : "false")
                            .Append('\n');
                    }
                    if (preferences.QuotePreference != "")
                    {
                        renameOptions.Append("// @quotePreference: ")
                            .Append(preferences.QuotePreference)
                            .Append('\n');
                    }
                }

                string baselineFileContent = GetBaselineForGroupedSpansWithFileContents(
                    groupedRanges,
                    new BaselineFourslashLocationsOptions
                    {
                        Marker = markerOrRange as Marker,
                        MarkerName = "/*RENAME*/",
                        EndMarker = "RENAME|]",
                        StartMarkerPrefix = span =>
                        {
                            string text = spanToText.TryGetValue(span, out string? t) ? t : "";
                            string prefix = SplitOnQuestion(text).Prefix;
                            return prefix.Length != 0 ? "/*START PREFIX*/" + prefix : null;
                        },
                        EndMarkerSuffix = span =>
                        {
                            string text = spanToText.TryGetValue(span, out string? t) ? t : "";
                            string suffix = SplitOnQuestion(text).Suffix;
                            return suffix.Length != 0 ? suffix + "/*END SUFFIX*/" : null;
                        },
                    });

                string baselineResult = renameOptions.Length > 0
                    ? renameOptions.ToString() + "\n" + baselineFileContent
                    : baselineFileContent;

                addResultToBaseline(FourslashBaselineCommand.Rename, baselineResult);
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // Go: strings.Split(text, "?") — [prefix, suffix]; SplitN semantics for extra delimiters.
    private static (string Prefix, string Suffix) SplitOnQuestion(string text)
    {
        int index = text.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? (text, "") : (text[..index], text[(index + 1)..]);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:5051-5110 — VerifyBaselineInlayHints.
    // ---------------------------------------------------------------------

    public void VerifyBaselineInlayHints(LsProto.Range? span, UserPreferences? testPreferences)
    {
        string fileName = activeFilename;
        FourslashScriptInfo script = getScriptInfo(fileName)!;
        LsProto.Range lspRange = span ?? converters.ToLSPRange(script, new TextRange(0, script.Content.Length));

        UserPreferences preferences = testPreferences ?? UserPreferences.NewDefaultUserPreferences();
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            LsProto.InlayHint[] hints = LS().ProvideInlayHint(
                fileName,
                lspRange,
                preferences.InlayHints,
                cancellationToken);

            string[] fileLines = SplitLines(script.Content);
            var annotations = new List<string>();
            if (hints.Length != 0)
            {
                Array.Sort(hints, (a, b) => ComparePositions(a.Position, b.Position));
                foreach (LsProto.InlayHint hint in hints)
                {
                    if (hint.Label.InlayHintLabelParts != null)
                    {
                        foreach (LsProto.InlayHintLabelPart part in hint.Label.InlayHintLabelParts)
                        {
                            // Avoid diffs caused by lib file updates.
                            if (part.Location != null && IsLibFile(part.Location.Uri.FileName()))
                            {
                                part.Location.Range.Start = new LsProto.Position { Line = 0, Character = 0 };
                                part.Location.Range.End = new LsProto.Position { Line = 0, Character = 0 };
                            }
                        }
                    }
                    string underline = new string(' ', (int)hint.Position.Character) + "^";
                    string annotation = fileLines[(int)hint.Position.Line] + "\n" + underline;
                    // !!! JSON section (core.StringifyJson(hint)) — see the file header.
                    annotations.Add(annotation);
                }
            }

            if (annotations.Count == 0)
            {
                annotations.Add("=== No inlay hints ===");
            }

            addResultToBaseline(FourslashBaselineCommand.InlayHints, string.Join("\n\n", annotations));
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:5459-5465 — isLibFile.
    // ---------------------------------------------------------------------

    private static bool IsLibFile(string fileName)
    {
        string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(fileName);
        return baseName.StartsWith("lib.", StringComparison.Ordinal)
            && baseName.EndsWith(".d.ts", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:5112-5189 — VerifyBaselineLinkedEditing.
    // ---------------------------------------------------------------------

    public void VerifyBaselineLinkedEditing()
    {
        var baselineBuilder = new StringBuilder();
        int offset = 0;

        foreach (FourslashFile file in testData.Files)
        {
            baselineBuilder.Append("// === Linked Editing ===\n");
            baselineBuilder.Append("=== ").Append(file.VirtualFileName).Append(" ===\n");
            var results = new List<LsProto.LinkedEditingRanges>();
            var found = new HashSet<string>(StringComparer.Ordinal);
            FourslashScriptInfo script = getScriptInfo(file.VirtualFileName)!;

            for (int i = 0; i < script.Content.Length; i++)
            {
                // Go sends the LSP request at PositionToLineAndCharacter(i); the LS converts it
                // back to offset i, so the raw-position overload is the same request.
                LsProto.LinkedEditingRanges? result = LS().ProvideLinkedEditingRange(file.VirtualFileName, i);
                if (result is { Ranges.Length: > 0 } && found.Add(RangeKey(result.Ranges[0])))
                {
                    results.Add(result);
                }
            }

            if (results.Count == 0)
            {
                baselineBuilder.Append(script.Content).Append("\n\n--No linked edits found--\n\n\n");
                continue;
            }

            results.Sort((a, b) => ComparePositions(a.Ranges[0].Start, b.Ranges[0].Start));
            var baselineDetails = new List<(LsProto.Position Pos, string Marker)>();
            var foundEditInfoBuilder = new StringBuilder();
            foreach (LsProto.LinkedEditingRanges edit in results)
            {
                baselineDetails.Add((edit.Ranges[0].Start, "[|/*" + offset + "*/"));
                baselineDetails.Add((edit.Ranges[0].End, "|]"));
                baselineDetails.Add((edit.Ranges[1].Start, "[|/*" + offset + "*/"));
                baselineDetails.Add((edit.Ranges[1].End, "|]"));

                foundEditInfoBuilder.Append("\n\n=== ").Append(offset).Append(" ===\n");
                // !!! JSON section (core.StringifyJson(edit)) — see the file header.
                offset++;
            }

            baselineDetails.Sort(static (a, b) => ComparePositionsStatic(a.Pos, b.Pos));

            int lastPosition = 0;
            foreach ((LsProto.Position pos, string marker) in baselineDetails)
            {
                int currentPosition = converters.LineAndCharacterToPosition(script, pos);
                baselineBuilder.Append(script.Content[lastPosition..currentPosition]).Append(marker);
                lastPosition = currentPosition;
            }
            baselineBuilder.Append(script.Content[lastPosition..]);
            baselineBuilder.Append(foundEditInfoBuilder).Append("\n\n\n");
        }

        writeToBaseline(FourslashBaselineCommand.LinkedEditing, baselineBuilder.ToString());
    }

    private static string RangeKey(LsProto.Range range) =>
        range.Start.Line + ":" + range.Start.Character + "-" + range.End.Line + ":" + range.End.Character;

    private static int ComparePositionsStatic(LsProto.Position a, LsProto.Position b)
    {
        if (a.Line != b.Line)
        {
            return a.Line < b.Line ? -1 : 1;
        }
        if (a.Character != b.Character)
        {
            return a.Character < b.Character ? -1 : 1;
        }
        return 0;
    }

    // fourslash.go:5273-5457 — VerifyBaselineNonSuggestionDiagnostics.
    // PORT-NOTE: Go's generic GetErrorBaseline[T] (error_baseline.go:56) is instantiated here at
    // Ts.Ast.Diagnostic — the C# GetErrorBaseline takes the sln's single Diagnostic sidecar
    // (Ts.TestUtil/TsBaseline/ErrorBaseline.cs:52, plan/05 §3.1 collapse).
    // ---------------------------------------------------------------------

    public void VerifyBaselineNonSuggestionDiagnostics()
    {
        var files = new List<BaselineTestFile>();
        var diagnostics = new List<Diagnostic>();
        // map-iter: 序无关 — the file list is sorted by unit name before the baseline is built.
        foreach (KeyValuePair<string, FourslashScriptInfo> entry in scriptInfos)
        {
            string fileName = entry.Key;
            if (global::Ts.TsPath.TsPath.HasJSONFileExtension(fileName))
            {
                continue;
            }
            FourslashScriptInfo scriptInfo = entry.Value;
            files.Add(new BaselineTestFile(fileName, scriptInfo.Content));
            foreach (LsProto.Diagnostic diagnostic in getDiagnostics(fileName).Where(static d => !isSuggestionDiagnostic(d)))
            {
                diagnostics.Add(ToDiagnostic(scriptInfo, diagnostic));
            }
        }
        files.Sort(static (a, b) => string.CompareOrdinal(a.UnitName, b.UnitName));
        string result = global::Ts.TestUtil.ErrorBaseline.GetErrorBaseline(files, diagnostics);
        addResultToBaseline(FourslashBaselineCommand.NonSuggestionDiagnostics, result);
    }

    // fourslash.go:5296-5369 — fourslashDiagnostic / fourslashDiagnosticFile. Go's
    // diagnosticwriter.Diagnostic/FileLike interfaces collapse onto their single C# realization
    // (Ts.Ast.Diagnostic + SourceFile; plan/05 §3.1), so toDiagnostic builds an ast.Diagnostic
    // whose message is the already-localized LSP string and whose file carries the ECMA line map
    // the writer needs for the squiggle rows.
    private Diagnostic ToDiagnostic(FourslashScriptInfo scriptInfo, LsProto.Diagnostic lspDiagnostic)
    {
        var category = lspDiagnostic.Severity switch
        {
            LsProto.DiagnosticSeverity.Warning => global::Ts.Diagnostics.Category.Warning,
            LsProto.DiagnosticSeverity.Information => global::Ts.Diagnostics.Category.Message,
            LsProto.DiagnosticSeverity.Hint => global::Ts.Diagnostics.Category.Suggestion,
            _ => global::Ts.Diagnostics.Category.Error,
        };
        int code = lspDiagnostic.Code?.Integer ?? 0;

        var related = new List<Diagnostic>();
        if (lspDiagnostic.RelatedInformation != null)
        {
            foreach (LsProto.DiagnosticRelatedInformation info in lspDiagnostic.RelatedInformation)
            {
                FourslashScriptInfo? relatedScriptInfo = getScriptInfo(DocumentUriToFileName(info.Location.Uri));
                if (relatedScriptInfo == null)
                {
                    continue;
                }
                related.Add(ToFourslashDiagnostic(
                    relatedScriptInfo,
                    converters.FromLSPRange(relatedScriptInfo, info.Location.Range),
                    code,
                    category,
                    info.Message));
            }
        }

        return ToFourslashDiagnostic(
            scriptInfo,
            converters.FromLSPRange(scriptInfo, lspDiagnostic.Range),
            code,
            category,
            DiagnosticMessageAsString(lspDiagnostic.Message),
            related);
    }

    // lsproto/util.go — StringOrMarkupContent.AsString.
    private static string DiagnosticMessageAsString(LsProto.StringOrMarkupContent message) =>
        message.String ?? message.MarkupContent?.Value ?? "";

    // fourslash.go:5296-5369 — one fourslashDiagnostic: the file's ECMA line map comes from
    // SourceFileLike.GetECMALineStarts (the writer's FileLike.ECMALineMap analogue).
    private static Diagnostic ToFourslashDiagnostic(
        FourslashScriptInfo scriptInfo,
        TextRange loc,
        int code,
        global::Ts.Diagnostics.Category category,
        string messageText,
        List<Diagnostic>? relatedDiagnostics = null)
    {
        var sourceFile = global::Ts.Parser.Parser.ParseSourceFile(
            new global::Ts.Ast.SourceFileParseOptions
            {
                FileName = scriptInfo.FileName,
                Path = new global::Ts.TsPath.Path(scriptInfo.FileName),
            },
            scriptInfo.Content,
            global::Ts.Core.CoreUtil.GetScriptKindFromFileName(scriptInfo.FileName));
        // The message text is already the LSP-localized string; an empty key and no args make
        // Diagnostic.Localize a passthrough (the Go fourslashDiagnostic.Localize body).
        var message = new global::Ts.Diagnostics.Message(code, category, key: "", text: messageText);
        Diagnostic diagnostic = Diagnostic.NewDiagnostic(sourceFile, loc, message);
        if (relatedDiagnostics is { Count: > 0 })
        {
            diagnostic.SetRelatedInfo([.. relatedDiagnostics]);
        }
        return diagnostic;
    }

    // ---------------------------------------------------------------------
    // fourslash.go:5562-5634 — VerifyBaselineDocumentSymbol.
    // ---------------------------------------------------------------------

    public void VerifyBaselineDocumentSymbol()
    {
        LsProto.SymbolInformationsOrDocumentSymbolsOrNull result = LS().ProvideDocumentSymbols(
            activeFilename,
            hierarchicalDocumentSymbolSupport: true,
            cancellationToken);
        LsProto.DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename);

        var symbolBySpan = new Dictionary<DocumentSpanKey, LsProto.DocumentSymbol>();
        // fourslash.go:5573 — `if result.DocumentSymbols != nil { for _, symbol := range *result.DocumentSymbols }`.
        List<LsProto.DocumentSymbol> documentSymbols = result.DocumentSymbols ?? [];
        foreach (LsProto.DocumentSymbol symbol in documentSymbols)
        {
            CollectDocumentSymbolSpans(uri, symbol, symbolBySpan);
        }

        var spans = new List<DocumentSpan>(symbolBySpan.Count);
        foreach (KeyValuePair<DocumentSpanKey, LsProto.DocumentSymbol> entry in symbolBySpan)
        {
            spans.Add(new DocumentSpan(entry.Key.Uri, entry.Key.TextSpan, entry.Key.ContextSpan));
        }

        addResultToBaseline(
            FourslashBaselineCommand.DocumentSymbols,
            GetBaselineForSpansWithFileContents(
                spans,
                new BaselineFourslashLocationsOptions
                {
                    GetLocationData = span =>
                    {
                        LsProto.DocumentSymbol symbol = symbolBySpan[new DocumentSpanKey(span.Uri, span.TextSpan, span.ContextSpan!)];
                        return "{| name: " + symbol.Name + ", kind: " + symbol.Kind + " |}";
                    },
                }));

        var detailsBuilder = new StringBuilder();
        WriteDocumentSymbolDetails(documentSymbols, 0, detailsBuilder);
        writeToBaseline(FourslashBaselineCommand.DocumentSymbols, "\n\n// === Details ===\n" + detailsBuilder);
    }

    // fourslash.go:5598-5605 — writeDocumentSymbolDetails.
    private static void WriteDocumentSymbolDetails(List<LsProto.DocumentSymbol> symbols, int indent, StringBuilder builder)
    {
        foreach (LsProto.DocumentSymbol symbol in symbols)
        {
            builder.Append(new string(' ', indent * 2))
                .Append('(').Append(symbol.Kind).Append(") ").Append(symbol.Name).Append('\n');
            if (symbol.Children != null)
            {
                WriteDocumentSymbolDetails(symbol.Children, indent + 1, builder);
            }
        }
    }

    // fourslash.go:5607-5626 — collectDocumentSymbolSpans.
    private static void CollectDocumentSymbolSpans(
        LsProto.DocumentUri uri,
        LsProto.DocumentSymbol symbol,
        Dictionary<DocumentSpanKey, LsProto.DocumentSymbol> symbolBySpan)
    {
        var key = new DocumentSpanKey(uri, symbol.SelectionRange, symbol.Range);
        symbolBySpan.TryAdd(key, symbol);
        if (symbol.Children != null)
        {
            foreach (LsProto.DocumentSymbol child in symbol.Children)
            {
                CollectDocumentSymbolSpans(uri, child, symbolBySpan);
            }
        }
    }

    // fourslash.go:5628-5634 — documentSpanKey (value-comparable, so the symbol spans dedupe the
    // way Go's value key does rather than by pointer identity).
    private readonly record struct DocumentSpanKey(LsProto.DocumentUri Uri, LsProto.Range TextSpan, LsProto.Range ContextSpan)
    {
        public bool Equals(DocumentSpanKey other) =>
            RangesEqual(TextSpan, other.TextSpan)
            && RangesEqual(ContextSpan, other.ContextSpan)
            && Uri.Value == other.Uri.Value;

        public override int GetHashCode() => HashCode.Combine(Uri.Value, TextSpan.Start.Line, TextSpan.Start.Character, TextSpan.End.Line, TextSpan.End.Character, ContextSpan.Start.Line, ContextSpan.Start.Character);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:4079-4120 — VerifyBaselineClosingTags.
    // ---------------------------------------------------------------------

    public void VerifyBaselineClosingTags()
    {
        var markersAndItems = new List<MarkerAndItem<LsProto.VSOnAutoInsertResponseItem>>();
        foreach (Marker marker in Markers())
        {
            if (marker.Name == null)
            {
                continue;
            }
            LsProto.VSOnAutoInsertResponseItemOrNull result = LS().ProvideOnAutoInsert(
                cancellationToken,
                new LsProto.VSOnAutoInsertParams
                {
                    VSTextDocument = new LsProto.TextDocumentIdentifier
                    {
                        Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(marker.FileName),
                    },
                    VSPosition = marker.LSPosition,
                    VSCh = ">",
                });
            markersAndItems.Add(new MarkerAndItem<LsProto.VSOnAutoInsertResponseItem>(marker, result.VSOnAutoInsertResponseItem));
        }

        addResultToBaseline(
            FourslashBaselineCommand.ClosingTag,
            AnnotateContentWithTooltips(
                markersAndItems,
                "closing tag",
                // Returning null renders the caret marker at the marker position (the text edit's
                // range is zero-width, which would render as an empty underline).
                static _ => null,
                // Go's getTooltipLines takes (item, prev); the closing-tag tooltip ignores prev.
                static (item, _) =>
                {
                    if (item?.VSTextEdit == null)
                    {
                        return ["No closing tag"];
                    }
                    string format = item.VSTextEditFormat == LsProto.InsertTextFormat.Snippet ? "snippet" : "plaintext";
                    return [format + ": \"" + item.VSTextEdit.NewText + "\""];
                }));
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2507-2560 — VerifyBaselineCodeLens.
    // ---------------------------------------------------------------------

    public void VerifyBaselineCodeLens(UserPreferences? preferences)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            bool foundAtLeastOneCodeLens = false;
            foreach (string openFile in openFiles.OrderBy(static f => f, StringComparer.Ordinal))
            {
                LsProto.CodeLensesOrNull unresolved = LS().ProvideCodeLenses(
                    cancellationToken,
                    global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(openFile));
                if (unresolved.CodeLenses is not { Length: > 0 } codeLenses)
                {
                    continue;
                }
                foundAtLeastOneCodeLens = true;

                foreach (LsProto.CodeLens unresolvedCodeLens in codeLenses)
                {
                    LsProto.CodeLens resolvedCodeLens = LS().ResolveCodeLens(
                        cancellationToken,
                        unresolvedCodeLens,
                        // fourslash.go:317 — showCodeLensLocationsCommandName.
                        showLocationsCommandName: "typescript.showCodeLensLocations",
                        orchestrator: null);
                    Assert.True(resolvedCodeLens.Command != null, "Expected resolved code lens to have a command.");

                    var locations = new List<LsProto.Location>();
                    // !!! commandArgs: Go round-trips the resolve response's arguments (see the
                    // file header on the deferred JSON channel).

                    // fourslash.go:2538-2542 — the marker is a synthesized RangeMarker over the
                    // resolved lens range (Go fills both LSRange and the harness Range).
                    var marker = new RangeMarker(
                        openFile,
                        converters.FromLSPRange(getScriptInfo(openFile)!, resolvedCodeLens.Range),
                        marker: null)
                    {
                        LSRange = resolvedCodeLens.Range,
                    };
                    addResultToBaseline(
                        FourslashBaselineCommand.CodeLenses,
                        GetBaselineForLocationsWithFileContents(
                            locations,
                            new BaselineFourslashLocationsOptions
                            {
                                Marker = marker,
                                MarkerName = "/*CODELENS: " + resolvedCodeLens.Command.Title + "*/",
                            }));
                }
            }

            if (!foundAtLeastOneCodeLens)
            {
                throw new InvalidOperationException("Expected at least one code lens in any open file, but got none.");
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2427-2505 — VerifyBaselineVSFindAllReferences.
    // ---------------------------------------------------------------------

    public void VerifyBaselineVSFindAllReferences(params string[] markers)
    {
        foreach (IFourslashMarkerOrRange markerOrRange in lookupMarkersOrGetRanges(markers))
        {
            GoToMarkerOrRange(markerOrRange);

            LsProto.VSReferenceItemsOrNull result = LS().ProvideVSReferences(
                cancellationToken,
                new LsProto.ReferenceParams
                {
                    TextDocument = new LsProto.TextDocumentIdentifier
                    {
                        Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                    },
                    Position = currentCaretPosition,
                    Context = new LsProto.ReferenceContext { IncludeDeclaration = true },
                },
                orchestrator: null);

            var locations = new List<LsProto.Location>();
            foreach (LsProto.VSReferenceItem item in result.VSReferenceItems ?? [])
            {
                locations.Add(item.VSLocation);
            }

            // !!! JSON section (core.StringifyJson(result)) — see the file header.
            addResultToBaseline(
                FourslashBaselineCommand.VsFindAllReferences,
                GetBaselineForLocationsWithFileContents(
                    locations,
                    new BaselineFourslashLocationsOptions
                    {
                        Marker = markerOrRange as Marker,
                        MarkerName = "/*FIND ALL REFS*/",
                    }));
        }
    }

    // ---------------------------------------------------------------------
    // Helpers shared with the partials above.
    // ---------------------------------------------------------------------

    // Go: lsproto.ComparePositions — see the private ComparePositions in FourslashBaselineText.cs.

    // Go: regexp `\r?\n` Split(text, -1) — see SplitLinesRegex in FourslashBaselineText.cs.
    private static string[] SplitLines(string text) => SplitLinesStatic(text);

    private static string[] SplitLinesStatic(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(text[start..end]);
                start = i + 1;
            }
        }
        lines.Add(text[start..]);
        return [.. lines];
    }

    // Go: strings.Replace(s, old, new, 1).
    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }
}

#endif
