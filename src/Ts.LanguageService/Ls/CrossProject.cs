// Ported from tsgo internal/ls/crossproject.go (1:1).
//
// Go's iter.Seq[T] result streams map to lazy IEnumerable<T> (the C# iterator restarts
// on each GetEnumerator, exactly like re-ranging a Go iter.Seq — combineImplementations
// relies on that). Go's (Resp, error) transform channel becomes a plain Resp delegate:
// the Go transformers always return nil errors, and a Go-side error maps to an
// exception, which lands in the panic collector below. lsproto.Location / Range are
// classes in C#, so the combine* dedup sets are keyed by a LocationKey record struct
// instead of Go's struct-valued lsproto.Location.
//
// PORT-NOTE (foundation): LanguageService.provideSymbolsAndEntries takes the raw UTF-16
// offset in the C# foundation, so ProvideSymbolsAndEntriesForUri below converts the LSP
// uri/position pair (uri==path in the foundation currency, see the file header of
// SelectionRanges.cs). orchestrator.GetProjectsForFile returns null for Go's error arm.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections; // Set<T> / SyncMap<K,V> / SyncSet<T>

namespace Ts.Ls
{

// crossproject.go:17-21 — Project.
public interface Project
{
    global::Ts.TsPath.Path Id();
    global::Ts.Compiler.Program? GetProgram();
    bool HasFile(string fileName);
}

// crossproject.go:23-29 — projectAndTextDocumentPosition.
internal sealed class ProjectAndTextDocumentPosition
{
    public Project Project = null!;
    public LanguageService? Ls;
    public global::Ts.LsProto.DocumentUri Uri;
    public global::Ts.LsProto.Position Position = null!;
    public bool ForOriginalLocation;
}

// crossproject.go:31-35 — response[Resp].
internal sealed class CrossProjectResponse<Resp>
{
    public bool Complete;
    public Resp Result = default!;
    public bool ForOriginalLocation;
}

// crossproject.go:37-43 — CrossProjectOrchestrator.
public interface CrossProjectOrchestrator
{
    Project GetDefaultProject();
    Project[] GetAllProjectsForInitialRequest();
    LanguageService? GetLanguageServiceForProjectWithFile(CancellationToken ctx, Project project, global::Ts.LsProto.DocumentUri uri);

    // Go returns ([]Project, error); the only caller discards on error, so the C#
    // channel is a nullable array (null = error).
    Project[]? GetProjectsForFile(CancellationToken ctx, global::Ts.LsProto.DocumentUri uri);

    // Go returns iter.Seq[Project].
    IEnumerable<Project> GetProjectsLoadingProjectTree(CancellationToken ctx, Set<global::Ts.TsPath.Path> requestedProjectTrees);
}

// crossproject.go:50 — symbolAndEntriesToResp transformer signature.
internal delegate Resp SymbolAndEntriesToResp<in Req, Resp>(
    LanguageService ls,
    CancellationToken ctx,
    Req request,
    SymbolAndEntriesData data,
    SymbolEntryTransformOptions options)
    where Req : global::Ts.LsProto.IHasTextDocumentPosition;

public sealed partial class LanguageService
{
    // crossproject.go:61/:105 — l.provideSymbolsAndEntries in the LSP uri/position
    // currency. The C# foundation engine takes the raw UTF-16 offset, so the conversion
    // rides GetProgramAndFile + LsDeps.LineAndCharacterToPosition; the uri is the
    // foundation's file key (see the SelectionRanges.cs header for the currency note).
    internal (SymbolAndEntriesData Data, bool Ok) ProvideSymbolsAndEntriesForUri(
        CancellationToken ctx,
        global::Ts.LsProto.DocumentUri uri,
        global::Ts.LsProto.Position position,
        bool isRename,
        bool implementations)
    {
        var file = GetProgramAndFile(uri);
        var pos = LsDeps.LineAndCharacterToPosition(file, position.Line, position.Character);
        return ProvideSymbolsAndEntries(ctx, file.FileName(), pos, isRename, implementations);
    }
}

internal static class CrossProject
{
    // crossproject.go:45-281 — handleCrossProject.
    internal static Resp HandleCrossProject<Req, Resp>(
        LanguageService defaultLs,
        CancellationToken ctx,
        Req request,
        CrossProjectOrchestrator? orchestrator,
        SymbolAndEntriesToResp<Req, Resp> symbolAndEntriesToResp,
        Func<IEnumerable<Resp>, Resp> combineResults,
        bool isRename,
        bool implementations,
        SymbolEntryTransformOptions options)
        where Req : global::Ts.LsProto.IHasTextDocumentPosition
    {
        // Single project
        if (orchestrator == null)
        {
            var (singleData, _) = defaultLs.ProvideSymbolsAndEntriesForUri(ctx, request.TextDocumentURI(), request.TextDocumentPosition(), isRename, implementations);
            return symbolAndEntriesToResp(defaultLs, ctx, request, singleData, options);
        }

        var defaultProject = orchestrator.GetDefaultProject();
        var allProjects = orchestrator.GetAllProjectsForInitialRequest();
        var results = new SyncMap<global::Ts.TsPath.Path, CrossProjectResponse<Resp>>();
        NonLocalDefinition? defaultDefinition = null;
        bool CanSearchProject(Project project)
        {
            return !results.TryLoad(project.Id(), out _);
        }
        var wg = global::Ts.Core.WorkGroup.Create(false);
        var panicsOccured = new List<string>();
        var panicMu = new object();

        void EnqueueItem(ProjectAndTextDocumentPosition item)
        {
            var response = new CrossProjectResponse<Resp>();
            if (results.LoadOrStore(item.Project.Id(), response).Loaded)
            {
                return;
            }
            wg.Queue(() =>
            {
                if (ctx.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    // Process the item
                    var ls = item.Ls;
                    if (ls == null)
                    {
                        // Get it now
                        ls = orchestrator.GetLanguageServiceForProjectWithFile(ctx, item.Project, item.Uri);
                        if (ls == null)
                        {
                            return;
                        }
                    }
                    var (data, ok) = ls.ProvideSymbolsAndEntriesForUri(ctx, item.Uri, item.Position, isRename, implementations);
                    if (ctx.IsCancellationRequested)
                    {
                        return;
                    }
                    if (ok)
                    {
                        foreach (var entry in data.SymbolsAndEntries)
                        {
                            // Find the default definition that can be in another project
                            // Later we will use this load ancestor tree that references this location and expand search
                            if (ReferenceEquals(item.Project, defaultProject) && defaultDefinition == null)
                            {
                                defaultDefinition = ls.GetNonLocalDefinition(ctx, entry);
                            }
                            ls.ForEachOriginalDefinitionLocation(ctx, entry, (uri, position) =>
                            {
                                // Get default configured project for this file
                                var defProjects = orchestrator.GetProjectsForFile(ctx, uri);
                                if (defProjects == null)
                                {
                                    return;
                                }
                                foreach (var defProject in defProjects)
                                {
                                    // Optimization: don't enqueue if will be discarded
                                    if (CanSearchProject(defProject))
                                    {
                                        EnqueueItem(new ProjectAndTextDocumentPosition
                                        {
                                            Project = defProject,
                                            Uri = uri,
                                            Position = position,
                                            ForOriginalLocation = true,
                                        });
                                    }
                                }
                            });
                        }
                    }

                    // Go: (result, errSearch) — the transform error arm maps to the panic
                    // collector (see the file header).
                    var result = symbolAndEntriesToResp(ls, ctx, request, data, options);
                    response.Complete = true;
                    response.Result = result;
                    response.ForOriginalLocation = item.ForOriginalLocation;
                }
                catch (Exception ex)
                {
                    var stack = Environment.StackTrace;
                    lock (panicMu)
                    {
                        panicsOccured.Add($"panic handling request: {ex.Message}\n{stack}");
                    }
                }
            });
        }

        // Initial set of projects and locations in the queue, starting with default project
        EnqueueItem(new ProjectAndTextDocumentPosition
        {
            Project = defaultProject,
            Ls = defaultLs,
            Uri = request.TextDocumentURI(),
            Position = request.TextDocumentPosition(),
        });
        foreach (var project in allProjects)
        {
            if (!ReferenceEquals(project, defaultProject))
            {
                EnqueueItem(new ProjectAndTextDocumentPosition
                {
                    Project = project,
                    // TODO!! symlinks need to change the URI
                    Uri = request.TextDocumentURI(),
                    Position = request.TextDocumentPosition(),
                });
            }
        }

        IEnumerable<Resp> GetResultsIterator()
        {
            var seenProjects = new SyncSet<global::Ts.TsPath.Path>();
            if (results.TryLoad(defaultProject.Id(), out var defaultResponse) && defaultResponse!.Complete)
            {
                yield return defaultResponse.Result;
            }
            seenProjects.Add(defaultProject.Id());
            foreach (var project in allProjects)
            {
                if (seenProjects.AddIfAbsent(project.Id()))
                {
                    if (results.TryLoad(project.Id(), out var response) && response!.Complete)
                    {
                        yield return response.Result;
                    }
                }
            }
            // Prefer the searches from locations for default definition.
            // SyncMap.Range takes a callback, so each priority pass materializes before
            // yielding; the seen-set short-circuit order is preserved verbatim.
            var nonOriginal = new List<Resp>();
            results.Range((key, response) =>
            {
                if (!response.ForOriginalLocation && seenProjects.AddIfAbsent(key) && response.Complete)
                {
                    nonOriginal.Add(response.Result);
                }
                return true;
            });
            foreach (var result in nonOriginal)
            {
                yield return result;
            }
            // Then the searches from original locations
            var original = new List<Resp>();
            results.Range((key, response) =>
            {
                if (response.ForOriginalLocation && seenProjects.AddIfAbsent(key) && response.Complete)
                {
                    original.Add(response.Result);
                }
                return true;
            });
            foreach (var result in original)
            {
                yield return result;
            }
        }

        // Outer loop - to complete work if more is added after completing existing queue
        while (true)
        {
            // Process existing known projects first
            wg.RunAndWait();
            // No need to use mu here since we are not in parallel at this point
            if (panicsOccured.Count != 0)
            {
                throw new InvalidOperationException($"Panics occurred during cross-project handling: {string.Join("\n", panicsOccured)}");
            }
            if (ctx.IsCancellationRequested)
            {
                return default!;
            }

            wg = global::Ts.Core.WorkGroup.Create(false);
            var hasMoreWork = false;
            if (defaultDefinition != null)
            {
                var requestedProjectTrees = new Set<global::Ts.TsPath.Path>();
                results.Range((key, response) =>
                {
                    if (response.Complete)
                    {
                        requestedProjectTrees.Add(key);
                    }
                    return true;
                });

                // Load more projects based on default definition found
                foreach (var loadedProject in orchestrator.GetProjectsLoadingProjectTree(ctx, requestedProjectTrees))
                {
                    if (ctx.IsCancellationRequested)
                    {
                        return default!;
                    }

                    // Can loop forever without this (enqueue here, dequeue above, repeat)
                    if (!CanSearchProject(loadedProject) || loadedProject.GetProgram() == null)
                    {
                        continue;
                    }

                    // Enqueue the project and location for further processing
                    if (loadedProject.HasFile(defaultDefinition.TextDocumentURI().FileName()))
                    {
                        EnqueueItem(new ProjectAndTextDocumentPosition
                        {
                            Project = loadedProject,
                            Uri = defaultDefinition.TextDocumentURI(),
                            Position = defaultDefinition.TextDocumentPosition(),
                        });
                        hasMoreWork = true;
                    }
                    else if (defaultDefinition.GetSourcePosition() is { } sourcePos && loadedProject.HasFile(sourcePos.TextDocumentURI().FileName()))
                    {
                        EnqueueItem(new ProjectAndTextDocumentPosition
                        {
                            Project = loadedProject,
                            Uri = sourcePos.TextDocumentURI(),
                            Position = sourcePos.TextDocumentPosition(),
                        });
                        hasMoreWork = true;
                    }
                    else if (defaultDefinition.GetGeneratedPosition() is { } generatedPos && loadedProject.HasFile(generatedPos.TextDocumentURI().FileName()))
                    {
                        EnqueueItem(new ProjectAndTextDocumentPosition
                        {
                            Project = loadedProject,
                            Uri = generatedPos.TextDocumentURI(),
                            Position = generatedPos.TextDocumentPosition(),
                        });
                        hasMoreWork = true;
                    }
                }
            }
            if (!hasMoreWork)
            {
                break;
            }
        }

        if (results.Size() > 1)
        {
            return combineResults(GetResultsIterator());
        }
        else
        {
            // Single result, return that directly
            foreach (var value in GetResultsIterator())
            {
                return value;
            }
        }
        return default!;
    }

    // lsproto.Location and Range are classes in C#, so Go's struct-valued dedup key
    // becomes this record struct (uri + full range).
    internal readonly record struct LocationKey(
        string Uri,
        int StartLine,
        int StartCharacter,
        int EndLine,
        int EndCharacter);

    internal static LocationKey KeyOf(global::Ts.LsProto.Location location) =>
        new(
            location.Uri.Value,
            (int)location.Range.Start.Line,
            (int)location.Range.Start.Character,
            (int)location.Range.End.Line,
            (int)location.Range.End.Character);

    // crossproject.go:283-294 — combineLocationArray[T].
    internal static List<T> CombineLocationArray<T>(List<T> combined, T[]? locations, Set<LocationKey> seen)
        where T : global::Ts.LsProto.IHasLocation
    {
        if (locations == null)
        {
            return combined;
        }
        foreach (var loc in locations)
        {
            if (seen.AddIfAbsent(KeyOf(loc.GetLocation())))
            {
                combined.Add(loc);
            }
        }
        return combined;
    }

    // crossproject.go:296-305 — combineResponseLocations[T].
    internal static global::Ts.LsProto.Location[] CombineResponseLocations<T>(IEnumerable<T> results)
        where T : global::Ts.LsProto.IHasLocations
    {
        var combined = new List<global::Ts.LsProto.Location>();
        var seenLocations = new Set<LocationKey>();
        foreach (var resp in results)
        {
            var locations = resp.GetLocations();
            if (locations != null)
            {
                combined = CombineLocationArray(combined, locations, seenLocations);
            }
        }
        return combined.ToArray();
    }

    // crossproject.go:307-309 — combineReferences.
    internal static global::Ts.LsProto.LocationsOrNull CombineReferences(IEnumerable<global::Ts.LsProto.LocationsOrNull> results)
    {
        return new global::Ts.LsProto.LocationsOrNull { Locations = CombineResponseLocations(results) };
    }

    // crossproject.go:311-337 — combineVSReferences.
    internal static global::Ts.LsProto.VSReferenceItemsOrNull CombineVSReferences(IEnumerable<global::Ts.LsProto.VSReferenceItemsOrNull> results)
    {
        var combined = new List<global::Ts.LsProto.VSReferenceItem>();
        // Re-number IDs across projects to maintain unique IDs and correct definition references
        var nextId = 0;
        foreach (var resp in results)
        {
            if (resp.VSReferenceItems == null)
            {
                continue;
            }
            // Map old IDs to new IDs for this batch
            var idMap = new Dictionary<int, int>();
            foreach (var item in resp.VSReferenceItems)
            {
                var oldId = item.VSId;
                var newId = nextId;
                idMap[oldId] = newId;
                nextId++;

                var newItem = CloneVSReferenceItem(item);
                newItem.VSId = newId;
                if (item.VSDefinitionId != null)
                {
                    var newDefId = idMap[item.VSDefinitionId.Value];
                    newItem.VSDefinitionId = newDefId;
                }
                combined.Add(newItem);
            }
        }
        return new global::Ts.LsProto.VSReferenceItemsOrNull { VSReferenceItems = combined.ToArray() };
    }

    // Go: `newItem := *item` (struct copy through the pointer); the C# type is a class,
    // so the copy is spelled out. Reference-typed members are shared, exactly like Go's
    // shallow struct copy.
    private static global::Ts.LsProto.VSReferenceItem CloneVSReferenceItem(global::Ts.LsProto.VSReferenceItem item) =>
        new()
        {
            VSId = item.VSId,
            VSDefinitionId = item.VSDefinitionId,
            VSKind = item.VSKind,
            VSLocation = item.VSLocation,
            VSDefinitionText = item.VSDefinitionText,
            VSProjectName = item.VSProjectName,
            VSContainingType = item.VSContainingType,
        };

    // crossproject.go:339-350 — combineImplementations.
    // NOTE: the Go function re-ranges `results` mid-iteration inside the Locations arm;
    // the C# iterator restarts on GetEnumerator exactly like a Go iter.Seq re-run.
    internal static global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull CombineImplementations(IEnumerable<global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull> results)
    {
        var combined = new List<global::Ts.LsProto.LocationLink>();
        var seenLocations = new Set<LocationKey>();
        foreach (var resp in results)
        {
            if (resp.DefinitionLinks != null)
            {
                combined = CombineLocationArray(combined, resp.DefinitionLinks, seenLocations);
            }
            else if (resp.Locations != null)
            {
                return new global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { Locations = CombineResponseLocations(results) };
            }
        }
        return new global::Ts.LsProto.LocationOrLocationsOrDefinitionLinksOrNull { DefinitionLinks = combined.ToArray() };
    }

    // crossproject.go:352-406 — combineRenameResponse.
    internal static global::Ts.LsProto.WorkspaceEditOrNull CombineRenameResponse(IEnumerable<global::Ts.LsProto.WorkspaceEditOrNull> results)
    {
        var combined = new Dictionary<global::Ts.LsProto.DocumentUri, List<global::Ts.LsProto.TextEdit>>();
        var seenChanges = new Dictionary<global::Ts.LsProto.DocumentUri, Set<RangeKey>>();
        var documentChanges = new List<global::Ts.LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile>();
        var seenRenames = new Set<(global::Ts.LsProto.DocumentUri OldUri, global::Ts.LsProto.DocumentUri NewUri)>();

        foreach (var resp in results)
        {
            if (resp.WorkspaceEdit != null && resp.WorkspaceEdit.DocumentChanges != null)
            {
                foreach (var change in resp.WorkspaceEdit.DocumentChanges)
                {
                    if (change.RenameFile != null)
                    {
                        var key = (change.RenameFile.OldUri, change.RenameFile.NewUri);
                        if (seenRenames.AddIfAbsent(key))
                        {
                            documentChanges.Add(change);
                        }
                    }
                    else
                    {
                        documentChanges.Add(change);
                    }
                }
            }
            if (resp.WorkspaceEdit != null && resp.WorkspaceEdit.Changes != null)
            {
                // map-iter: 序无关 —— 每个 doc 的合并结果互不依赖，最终按 doc 键输出
                foreach (var (doc, changes) in resp.WorkspaceEdit.Changes)
                {
                    if (!seenChanges.TryGetValue(doc, out var seenSet) || seenSet == null)
                    {
                        seenSet = new Set<RangeKey>();
                        seenChanges[doc] = seenSet;
                    }
                    if (!combined.TryGetValue(doc, out var changesForDoc) || changesForDoc == null)
                    {
                        changesForDoc = [];
                    }
                    foreach (var change in changes)
                    {
                        if (!seenSet.Has(RangeKeyOf(change.Range)))
                        {
                            seenSet.Add(RangeKeyOf(change.Range));
                            changesForDoc.Add(change);
                        }
                    }
                    combined[doc] = changesForDoc;
                }
            }
        }
        if (documentChanges.Count > 0 || combined.Count > 0)
        {
            var workspaceEdit = new global::Ts.LsProto.WorkspaceEdit();
            if (documentChanges.Count > 0)
            {
                workspaceEdit.DocumentChanges = documentChanges.ToArray();
            }
            if (combined.Count > 0)
            {
                var changesMap = new Dictionary<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.TextEdit[]>();
                foreach (var (doc, edits) in combined)
                {
                    changesMap[doc] = edits.ToArray();
                }
                workspaceEdit.Changes = changesMap;
            }
            return new global::Ts.LsProto.WorkspaceEditOrNull { WorkspaceEdit = workspaceEdit };
        }
        return new global::Ts.LsProto.WorkspaceEditOrNull();
    }

    internal readonly record struct RangeKey(
        int StartLine,
        int StartCharacter,
        int EndLine,
        int EndCharacter);

    internal static RangeKey RangeKeyOf(global::Ts.LsProto.Range range) =>
        new(
            (int)range.Start.Line,
            (int)range.Start.Character,
            (int)range.End.Line,
            (int)range.End.Character);

    // crossproject.go:408-421 — combineIncomingCalls.
    internal static global::Ts.LsProto.CallHierarchyIncomingCallsOrNull CombineIncomingCalls(IEnumerable<global::Ts.LsProto.CallHierarchyIncomingCallsOrNull> results)
    {
        var combined = new List<global::Ts.LsProto.CallHierarchyIncomingCall>();
        var seenCalls = new Set<LocationKey>();
        foreach (var resp in results)
        {
            if (resp.CallHierarchyIncomingCalls != null)
            {
                foreach (var call in resp.CallHierarchyIncomingCalls)
                {
                    if (seenCalls.AddIfAbsent(KeyOf(call.From.GetLocation())))
                    {
                        combined.Add(call);
                    }
                }
            }
        }
        return new global::Ts.LsProto.CallHierarchyIncomingCallsOrNull { CallHierarchyIncomingCalls = combined.ToArray() };
    }
}
} // namespace Ts.Ls
