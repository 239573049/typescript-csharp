// Ported from tsgo internal/ls/findallreferences.go — the LSP-shaped entries that ride
// the cross-project orchestrator (ProvideReferences :694, provideImplementationsEx :929)
// plus their symbolAndEntries→response transformers (:722/:943) and the location-link
// converter (:1008).
//
// HOSTING-NOTE: these members' Go canonical home is findallreferences.go, whose C# file
// (FindAllReferences.cs) is mid-rewrite by the deep-engine wave; the entries are hosted
// in this separate partial to avoid colliding with it. The Go (Resp, error) pairs
// collapse to the response values: the transformers always return nil errors in Go, and
// the lsproto.GetClientCapabilities(ctx) carrier is not wired yet, so the capability
// reads take the Go zero value (see the Definition.cs clientSupportsLink precedent) —
// only the Locations arm of symbolAndEntriesToImplementations is reachable until the
// capabilities wave lands.

using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Collections; // Set<T>

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // findallreferences.go:694-704 — ProvideReferences.
    internal global::Ts.LsProto.LocationsOrNull ProvideReferences(
        CancellationToken ctx,
        global::Ts.LsProto.ReferenceParams parameters,
        CrossProjectOrchestrator? orchestrator)
    {
        return CrossProject.HandleCrossProject(
            this,
            ctx,
            parameters,
            orchestrator,
            static (l, c, req, data, options) => l.SymbolAndEntriesToReferences(c, req, data, options),
            CrossProject.CombineReferences,
            false, /*isRename*/
            false, /*implementations*/
            new SymbolEntryTransformOptions());
    }

    // findallreferences.go:722-728 — symbolAndEntriesToReferences.
    internal global::Ts.LsProto.LocationsOrNull SymbolAndEntriesToReferences(
        CancellationToken ctx,
        global::Ts.LsProto.ReferenceParams parameters,
        SymbolAndEntriesData data,
        SymbolEntryTransformOptions options)
    {
        // `findReferencedSymbols` except only computes the information needed to return reference locations
        var locations = new List<global::Ts.LsProto.Location>();
        foreach (var s in data.SymbolsAndEntries)
        {
            locations.AddRange(ConvertSymbolAndEntriesToLocations(s, parameters.Context.IncludeDeclaration));
        }
        return new global::Ts.LsProto.LocationsOrNull { Locations = locations.ToArray() };
    }

    // findallreferences.go:929-941 — provideImplementationsEx.
    internal global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull ProvideImplementationsEx(
        CancellationToken ctx,
        global::Ts.LsProto.ImplementationParams parameters,
        SymbolEntryTransformOptions options,
        CrossProjectOrchestrator? orchestrator)
    {
        return CrossProject.HandleCrossProject(
            this,
            ctx,
            parameters,
            orchestrator,
            static (l, c, req, data, options) => l.SymbolAndEntriesToImplementations(c, req, data, options),
            CrossProject.CombineImplementations,
            false, /*isRename*/
            true, /*implementations*/
            options);
    }

    // findallreferences.go:943-961 — symbolAndEntriesToImplementations.
    internal global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull SymbolAndEntriesToImplementations(
        CancellationToken ctx,
        global::Ts.LsProto.ImplementationParams parameters,
        SymbolAndEntriesData data,
        SymbolEntryTransformOptions options)
    {
        var seenNodes = new Set<Node>();
        var entries = new List<ReferenceEntry>();
        foreach (var entry in data.SymbolsAndEntries)
        {
            foreach (var reference in entry.References)
            {
                if (seenNodes.AddIfAbsent(reference.Node!) && (!options.DropOriginNodes || !new global::Ts.Core.TextRange(reference.Node!.Pos, reference.Node.End).ContainsInclusive(data.Position)))
                {
                    entries.Add(reference);
                }
            }
        }

        // Go: lsproto.GetClientCapabilities(ctx).TextDocument.Implementation.LinkSupport —
        // zero value while the capabilities carrier is unwired (see the file header).
        const bool implementationLinkSupport = false;
        if (!options.RequireLocationsResult && implementationLinkSupport)
        {
            var links = ConvertEntriesToLocationLinks(entries);
            return new global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { DefinitionLinks = links.ToArray() };
        }
        var locations = ConvertEntriesToLocations(entries);
        return new global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { Locations = locations.ToArray() };
    }

    // findallreferences.go:1008-1035 — convertEntriesToLocationLinks.
    internal List<global::Ts.LsProto.LocationLink> ConvertEntriesToLocationLinks(List<ReferenceEntry> entries)
    {
        var links = new List<global::Ts.LsProto.LocationLink>(entries.Count);
        foreach (var entry in entries)
        {
            // Get the selection range (the actual reference)
            var loc = GetLocationOfEntry(entry);
            var targetSelectionRange = loc.Range;
            var targetRange = targetSelectionRange;

            // For entries with nodes, compute ranges directly from the node
            if (entry.Node != null)
            {
                // Get the context range (broader scope including declaration context)
                var contextTextRange = SourceDefinitionDeps.ToContextRange(entry.TextRange!.Value, program.GetSourceFile(entry.FileName)!, entry.Context);
                if (contextTextRange != null)
                {
                    var contextLocation = GetMappedLocation(entry.FileName, contextTextRange.Value);
                    targetRange = contextLocation.Range;
                }
            }

            links.Add(new global::Ts.LsProto.LocationLink
            {
                TargetUri = LsDeps.FileNameToDocumentURI(entry.FileName),
                TargetRange = targetRange,
                TargetSelectionRange = targetSelectionRange,
            });
        }
        return links;
    }
}
