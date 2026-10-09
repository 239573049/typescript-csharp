// Ported from tsgo internal/ls/findallreferences.go — the LSP-shaped entries that ride
// the cross-project orchestrator (ProvideReferences :694, provideImplementationsEx :929)
// plus their symbolAndEntries→response transformers (:722/:943) and the location-link
// converter (:1008).
//
// HOSTING-NOTE: these members' Go canonical home is findallreferences.go, whose C# file
// (FindAllReferences.cs) is mid-rewrite by the deep-engine wave; the entries are hosted
// in this separate partial to avoid colliding with it. The Go (Resp, error) pairs
// collapse to the response values: the transformers always return nil errors in Go.
// lsproto.GetClientCapabilities(ctx) reads the capabilities stored on the language
// service (LanguageService.ClientCapabilities).

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

    // findallreferences.go:708-720 — ProvideVSReferences.
    internal global::Ts.LsProto.VSReferenceItemsOrNull ProvideVSReferences(
        CancellationToken ctx,
        global::Ts.LsProto.ReferenceParams parameters,
        CrossProjectOrchestrator? orchestrator)
    {
        return CrossProject.HandleCrossProject(
            this,
            ctx,
            parameters,
            orchestrator,
            static (l, c, req, data, options) => l.SymbolAndEntriesToVSReferences(c, req, data, options),
            CrossProject.CombineVSReferences,
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

    // findallreferences.go:730-790 — symbolAndEntriesToVSReferences.
    internal global::Ts.LsProto.VSReferenceItemsOrNull SymbolAndEntriesToVSReferences(
        CancellationToken ctx,
        global::Ts.LsProto.ReferenceParams parameters,
        SymbolAndEntriesData data,
        SymbolEntryTransformOptions options)
    {
        var caps = ClientCapabilities();
        bool vsCapability = caps.VSSupportsVisualStudioExtensions;
        var items = new List<global::Ts.LsProto.VSReferenceItem>();
        int id = 0;
        string projectName = projectPath.Value;

        foreach (var s in data.SymbolsAndEntries)
        {
            if (s.Definition == null)
            {
                continue;
            }

            // Convert definition to info
            var defInfo = DefinitionToReferencedSymbolDefinitionInfo(ctx, s.Definition, data.OriginalNode, vsCapability);
            if (defInfo == null)
            {
                continue;
            }

            // Create the definition item
            int definitionId = id;
            var defItem = new global::Ts.LsProto.VSReferenceItem
            {
                VSId = definitionId,
                VSLocation = defInfo.Value.Location,
                VSDefinitionText = defInfo.Value.DisplayText,
                VSKind = [global::Ts.LsProto.VSReferenceKind.Unknown],
                VSProjectName = projectName,
                VSContainingType = "",
            };
            items.Add(defItem);
            id++;

            // Create reference items grouped under the definition
            foreach (var reference in s.References)
            {
                // Skip the declaration itself (already represented by the definition item)
                if (s.Definition.Symbol != null && FindRefsSupport.IsDeclarationOfSymbol(reference.Node, s.Definition.Symbol))
                {
                    continue;
                }

                var refLocation = GetLocationOfEntry(reference);

                // Determine read/write kind
                var kind = global::Ts.LsProto.VSReferenceKind.Read;
                if (reference.Kind != EntryKind.Range && reference.Node != null && LsDeps.IsWriteAccessForReference(reference.Node))
                {
                    kind = global::Ts.LsProto.VSReferenceKind.Write;
                }

                var refItem = new global::Ts.LsProto.VSReferenceItem
                {
                    VSId = id,
                    VSDefinitionId = definitionId,
                    VSLocation = refLocation,
                    VSKind = [kind],
                    VSProjectName = projectName,
                };
                items.Add(refItem);
                id++;
            }
        }

        return new global::Ts.LsProto.VSReferenceItemsOrNull { VSReferenceItems = items.ToArray() };
    }

    // findallreferences.go:792-797 — referencedSymbolDefinitionInfo holds the computed info for a definition
    internal readonly record struct ReferencedSymbolDefinitionInfo(
        Node Node,
        global::Ts.LsProto.Location Location,
        global::Ts.LsProto.VSClassifiedTextElement? DisplayText);

    private static global::Ts.LsProto.VSClassifiedTextElement SingleRun(string text, global::Ts.LsProto.ClassificationTypeName classification) => new()
    {
        Runs = [new global::Ts.LsProto.VSClassifiedTextRun { Text = text, ClassificationTypeName = classification.Value ?? "" }],
    };

    // findallreferences.go:799-905 — definitionToReferencedSymbolDefinitionInfo converts a Definition to display info
    private ReferencedSymbolDefinitionInfo? DefinitionToReferencedSymbolDefinitionInfo(CancellationToken ctx, FarDefinition def, Node? originalNode, bool vsCapability)
    {
        switch (def.Kind)
        {
            case DefinitionKind.Symbol:
            {
                var symbol = def.Symbol;
                if (symbol == null)
                {
                    return null;
                }
                // Get display parts
                var element = GetDefinitionKindAndDisplayParts(ctx, symbol, originalNode!, vsCapability);

                // Get the definition node
                Node node;
                if (symbol.Declarations is { Length: > 0 })
                {
                    var decl = symbol.Declarations[0];
                    node = decl.Name() ?? decl;
                }
                else
                {
                    node = originalNode!;
                }

                var loc = GetLocationOfEntry(new ReferenceEntry { Kind = EntryKind.Node, Node = node });
                return new ReferencedSymbolDefinitionInfo(node, loc, element);
            }

            case DefinitionKind.Label:
            {
                var node = def.Node;
                if (node == null)
                {
                    return null;
                }
                var loc = GetLocationOfEntry(new ReferenceEntry { Kind = EntryKind.Node, Node = node });
                return new ReferencedSymbolDefinitionInfo(node, loc, SingleRun(node.Text(), global::Ts.LsProto.ClassificationTypeName.Text));
            }

            case DefinitionKind.Keyword:
            {
                var node = def.Node;
                if (node == null)
                {
                    return null;
                }
                var name = global::Ts.Scanner.Scanner.TokenToString(node.Kind);
                var loc = GetLocationOfEntry(new ReferenceEntry { Kind = EntryKind.Node, Node = node });
                return new ReferencedSymbolDefinitionInfo(node, loc, SingleRun(name, global::Ts.LsProto.ClassificationTypeName.Keyword));
            }

            case DefinitionKind.This:
            {
                var node = def.Node;
                if (node == null)
                {
                    return null;
                }
                var symbol = def.Symbol;
                if (symbol == null)
                {
                    return null;
                }
                var element = GetDefinitionKindAndDisplayParts(ctx, symbol, node, vsCapability);
                var loc = GetLocationOfEntry(new ReferenceEntry { Kind = EntryKind.Node, Node = node });
                return new ReferencedSymbolDefinitionInfo(node, loc, element);
            }

            case DefinitionKind.String:
            {
                var node = def.Node;
                if (node == null)
                {
                    return null;
                }
                var loc = GetLocationOfEntry(new ReferenceEntry { Kind = EntryKind.Node, Node = node });
                return new ReferencedSymbolDefinitionInfo(node, loc, SingleRun(node.Text(), global::Ts.LsProto.ClassificationTypeName.String));
            }

            case DefinitionKind.TripleSlashReference:
            {
                if (def.TripleSlashFileRef == null || def.TripleSlashFileRef.File == null)
                {
                    return null;
                }
                Node node = def.TripleSlashFileRef.File;
                var loc = GetLocationOfEntry(new ReferenceEntry { Kind = EntryKind.Node, Node = node });
                return new ReferencedSymbolDefinitionInfo(node, loc, SingleRun("\"" + def.TripleSlashFileRef.Reference!.Value.FileName + "\"", global::Ts.LsProto.ClassificationTypeName.String));
            }

            default:
                return null;
        }
    }

    // findallreferences.go:907-925 — getDefinitionKindAndDisplayParts returns the classified display text for a symbol definition.
    private global::Ts.LsProto.VSClassifiedTextElement GetDefinitionKindAndDisplayParts(CancellationToken ctx, global::Ts.Ast.Symbol symbol, Node originalNode, bool vsCapability)
    {
        var program = GetProgram();
        var (c, done) = program.GetTypeChecker(ctx);
        try
        {
            var meaning = LsMeaning.GetIntersectingMeaningFromDeclarations(originalNode, symbol, SemanticMeaning.All);

            var info = GetQuickInfoAndDeclarationAtLocation(c, symbol, originalNode, null, vsCapability, meaning);

            if (vsCapability)
            {
                return new global::Ts.LsProto.VSClassifiedTextElement
                {
                    Runs = System.Array.ConvertAll(info.DisplayParts.GetRuns(), run => new global::Ts.LsProto.VSClassifiedTextRun { Text = run.Text, ClassificationTypeName = run.ClassificationTypeName }),
                };
            }
            // Fallback: single unclassified run with the full text
            var text = info.DisplayParts.String();
            return SingleRun(text, global::Ts.LsProto.ClassificationTypeName.Text);
        }
        finally
        {
            done();
        }
    }

    // findallreferences.go:925-927 — ProvideImplementations.
    internal global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull ProvideImplementations(
        CancellationToken ctx,
        global::Ts.LsProto.ImplementationParams parameters,
        CrossProjectOrchestrator? orchestrator)
    {
        return ProvideImplementationsEx(ctx, parameters, new SymbolEntryTransformOptions(), orchestrator);
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

        if (!options.RequireLocationsResult && ClientCapabilities().TextDocument.Implementation.LinkSupport)
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
