using Ts.Collections;

// Ported from tsgo internal/project/filechange.go (whole file).
// PORT-NOTE: FileChangeSummary is a Go value struct manipulated through pointers at
// every use site (mergeFileChangeSummary, Snapshot.Clone), so it maps to a sealed
// class here; its collections.Set fields are reference types in both languages.

namespace Ts.Project;

// filechange.go:13-29 — FileChangeKind.
public enum FileChangeKind
{
    Open,
    Close,
    Change,
    Save,
    WatchCreate,
    WatchChange,
    WatchDelete,
}

public static class FileChangeKindExtensions
{
    // filechange.go:31-33 — IsWatchKind.
    public static bool IsWatchKind(this FileChangeKind k) =>
        k is FileChangeKind.WatchCreate or FileChangeKind.WatchChange or FileChangeKind.WatchDelete;
}

// filechange.go:36-44 — FileChange.
public sealed class FileChange
{
    public FileChangeKind Kind { get; init; }

    public global::Ts.LsProto.DocumentUri URI { get; init; }

    // Only set for Open/Change.
    public int Version { get; init; }

    // Only set for Open.
    public string Content { get; init; } = "";

    // Only set for Open.
    public string LanguageKind { get; init; } = "";

    // Only set for Change.
    public global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument[] Changes { get; init; } = [];
}

// filechange.go:47-66 — FileChangeSummary.
public sealed class FileChangeSummary
{
    // filechange.go:11 — excessiveChangeThreshold.
    internal const int ExcessiveChangeThreshold = 1000;

    // Only one file can be opened at a time per request.
    public global::Ts.LsProto.DocumentUri Opened { get; set; }

    // Reopened is set if a close and open occurred for the same file in a single batch of changes.
    public global::Ts.LsProto.DocumentUri Reopened { get; set; }

    public global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri> Closed { get; set; } = new();

    public global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri> Changed { get; set; } = new();

    // Only set when file watching is enabled.
    public global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri> Created { get; set; } = new();

    // Only set when file watching is enabled.
    public global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri> Deleted { get; set; } = new();

    // IncludesWatchChangeOutsideNodeModules is true if the summary includes a create, change, or delete watch
    // event of a file outside a node_modules directory.
    public bool IncludesWatchChangeOutsideNodeModules { get; set; }

    // InvalidateAll indicates that all cached file state should be discarded.
    public bool InvalidateAll { get; set; }

    // filechange.go:68-70 — IsEmpty. (PORT-NOTE: a zero-value DocumentUri has a null
    // Value string here, so emptiness is checked with IsNullOrEmpty = Go's == "".)
    public bool IsEmpty() =>
        !InvalidateAll && string.IsNullOrEmpty(Opened.Value) && string.IsNullOrEmpty(Reopened.Value) && Closed.Len() == 0 && Changed.Len() == 0 && Created.Len() == 0 && Deleted.Len() == 0;

    // filechange.go:72-74 — HasExcessiveWatchEvents.
    public bool HasExcessiveWatchEvents() =>
        InvalidateAll || Created.Len() + Deleted.Len() + Changed.Len() > ExcessiveChangeThreshold;

    // filechange.go:76-78 — HasExcessiveNonCreateWatchEvents.
    public bool HasExcessiveNonCreateWatchEvents() =>
        InvalidateAll || Deleted.Len() + Changed.Len() > ExcessiveChangeThreshold;

    // filechange.go:81-98 — mergeFileChangeSummary merges src into dst, combining their change sets.
    public static void Merge(ref FileChangeSummary dst, FileChangeSummary src)
    {
        if (src.IsEmpty())
        {
            return;
        }
        if (src.InvalidateAll)
        {
            dst.InvalidateAll = true;
        }
        foreach (var uri in src.Changed.Keys())
        {
            dst.Changed.Add(uri);
        }
        foreach (var uri in src.Created.Keys())
        {
            dst.Created.Add(uri);
        }
        foreach (var uri in src.Deleted.Keys())
        {
            dst.Deleted.Add(uri);
        }
        if (src.IncludesWatchChangeOutsideNodeModules)
        {
            dst.IncludesWatchChangeOutsideNodeModules = true;
        }
    }
}
