// Ported from tsgo internal/project/overlayfs.go (whole file). The FileHandle /
// fileBase / diskFile / Overlay value types it shares with snapshot.go live in
// Snapshot.cs, mirroring the Go file split.

using System;
using System.Collections.Generic;
using static global::Ts.LsProto.DocumentUriExtensions;

namespace Ts.Project;

// overlayfs.go:96-103 — overlayFS.
public sealed class OverlayFS
{
    private readonly Func<string, global::Ts.TsPath.Path> toPath;
    internal readonly global::Ts.Vfs.IFileSystem fs;
    private readonly string positionEncoding;

    private readonly object mu = new();
    private Dictionary<global::Ts.TsPath.Path, Overlay> overlays;

    // overlayfs.go:105-112 — newOverlayFS. (Go composite literal → constructor; the
    // fields keep their readonly shape.)
    private OverlayFS(
        global::Ts.Vfs.IFileSystem fs,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        string positionEncoding,
        Func<string, global::Ts.TsPath.Path> toPath)
    {
        this.fs = fs;
        this.positionEncoding = positionEncoding;
        this.overlays = overlays;
        this.toPath = toPath;
    }

    public static OverlayFS NewOverlayFS(
        global::Ts.Vfs.IFileSystem fs,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        string positionEncoding,
        Func<string, global::Ts.TsPath.Path> toPath)
    {
        return new OverlayFS(fs, overlays, positionEncoding, toPath);
    }

    // overlayfs.go:114-119 — Overlays.
    public Dictionary<global::Ts.TsPath.Path, Overlay> Overlays()
    {
        lock (mu)
        {
            return overlays;
        }
    }

    // overlayfs.go:121-133 — getFile.
    public FileHandle? GetFile(string fileName)
    {
        Dictionary<global::Ts.TsPath.Path, Overlay> overlaysSnapshot;
        lock (mu)
        {
            overlaysSnapshot = overlays;
        }

        global::Ts.TsPath.Path path = toPath(fileName);
        if (overlaysSnapshot.TryGetValue(path, out Overlay? overlay))
        {
            return overlay;
        }

        if (!fs.TryReadFile(fileName, out string content))
        {
            return null;
        }
        return DiskFile.NewDiskFile(fileName, content);
    }

    // Reduced collection of changes that occurred on a single file (processChanges local type).
    private sealed class FileEvents
    {
        internal FileChange? openChange;
        internal FileChange? closeChange;
        internal bool watchChanged;
        internal List<FileChange> changes = [];
        internal bool saved;
        internal bool created;
        internal bool deleted;
    }

    // overlayfs.go:135-341 — processChanges.
    public (FileChangeSummary Result, Dictionary<global::Ts.TsPath.Path, Overlay> Overlays) ProcessChanges(
        IReadOnlyList<FileChange> changes)
    {
        lock (mu)
        {
            var result = new FileChangeSummary();
            Dictionary<global::Ts.TsPath.Path, Overlay> newOverlays = new(overlays);

            var fileEventMap = new Dictionary<global::Ts.LsProto.DocumentUri, FileEvents>();

            foreach (FileChange change in changes)
            {
                global::Ts.LsProto.DocumentUri uri = change.URI;
                if (!fileEventMap.TryGetValue(uri, out FileEvents? events))
                {
                    events = new FileEvents();
                    fileEventMap[uri] = events;
                }
                else if (events.openChange is not null)
                {
                    throw new InvalidOperationException("should see no changes after open");
                }

                if (!result.IncludesWatchChangeOutsideNodeModules && change.Kind.IsWatchKind() && !uri.Value.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    result.IncludesWatchChangeOutsideNodeModules = true;
                }

                switch (change.Kind)
                {
                    case FileChangeKind.Open:
                        if (events.closeChange is not null)
                        {
                            events.closeChange = null;
                        }
                        events.openChange = change;
                        events.watchChanged = false;
                        events.changes = [];
                        events.saved = false;
                        events.created = false;
                        events.deleted = false;
                        break;
                    case FileChangeKind.Close:
                        events.closeChange = change;
                        events.changes = [];
                        events.saved = false;
                        events.watchChanged = false;
                        break;
                    case FileChangeKind.Change:
                        if (events.closeChange is not null)
                        {
                            throw new InvalidOperationException("should see no changes after close");
                        }
                        events.changes.Add(change);
                        events.saved = false;
                        events.watchChanged = false;
                        break;
                    case FileChangeKind.Save:
                        events.saved = true;
                        break;
                    case FileChangeKind.WatchCreate:
                        if (events.deleted)
                        {
                            // Delete followed by create becomes a change.
                            events.deleted = false;
                            events.watchChanged = true;
                        }
                        else
                        {
                            events.created = true;
                        }
                        break;
                    case FileChangeKind.WatchChange:
                        if (!events.created)
                        {
                            events.watchChanged = true;
                            events.saved = false;
                        }
                        break;
                    case FileChangeKind.WatchDelete:
                        events.watchChanged = false;
                        events.saved = false;
                        // Delete after create cancels out.
                        if (events.created)
                        {
                            events.created = false;
                        }
                        else
                        {
                            events.deleted = true;
                        }
                        break;
                }
            }

            // Process deduplicated events per file.
            // map-iter: 序无关 — each uri's events are independent; the summary accumulates
            // into sets and per-path overlay assignments do not interact.
            foreach ((global::Ts.LsProto.DocumentUri uri, FileEvents events) in fileEventMap)
            {
                global::Ts.TsPath.Path path = uri.Path(fs.UseCaseSensitiveFileNames());
                Overlay? o = newOverlays.TryGetValue(path, out Overlay? existing) ? existing : null;

                if (events.openChange is not null)
                {
                    if (!string.IsNullOrEmpty(result.Opened.Value) || !string.IsNullOrEmpty(result.Reopened.Value))
                    {
                        throw new InvalidOperationException("can only process one file open event at a time");
                    }
                    if (o is not null && o.Content() != events.openChange.Content)
                    {
                        result.Changed.Add(uri);
                    }
                    else if (o is null)
                    {
                        result.Opened = uri;
                    }
                    else
                    {
                        result.Reopened = uri;
                    }
                    global::Ts.Core.ScriptKind scriptKind = global::Ts.Ls.LsConv.Converters.LanguageKindToScriptKind(events.openChange.LanguageKind);
                    if (scriptKind == global::Ts.Core.ScriptKind.Unknown)
                    {
                        scriptKind = global::Ts.Core.CoreUtil.GetScriptKindFromFileName(uri.FileName());
                    }
                    newOverlays[path] = Overlay.NewOverlay(
                        uri.FileName(),
                        events.openChange.Content,
                        events.openChange.Version,
                        scriptKind);
                    continue;
                }

                if (events.closeChange is not null)
                {
                    if (o is null)
                    {
                        throw new InvalidOperationException("overlay not found for closed file: " + uri.Value);
                    }
                    result.Closed.Add(uri);
                    newOverlays.Remove(path);
                    o = null;
                }

                if (events.watchChanged)
                {
                    if (o is null)
                    {
                        result.Changed.Add(uri);
                    }
                    else if (o is not null && !events.saved)
                    {
                        (bool matchesDiskText, bool _) = o.ComputeMatchesDiskText(fs);
                        if (matchesDiskText != o.MatchesDiskText())
                        {
                            o = Overlay.NewOverlay(o.FileName(), o.Content(), o.Version(), o.Kind());
                            o.SetMatchesDiskText(matchesDiskText);
                            newOverlays[path] = o;
                        }
                    }
                }

                if (events.changes.Count > 0)
                {
                    result.Changed.Add(uri);
                    if (o is null)
                    {
                        throw new InvalidOperationException("overlay not found for changed file: " + uri.Value);
                    }
                    foreach (FileChange change in events.changes)
                    {
                        var converters = new global::Ts.Ls.LsConv.Converters(positionEncoding, _ => o.LSPLineMap()!);
                        foreach (global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument textChange in change.Changes)
                        {
                            if (textChange.Partial is { } partialChange)
                            {
                                // converters.go:54-58 — FromLSPTextChange (hosted inline; the
                                // C# LsConv foundation lacks that exact wrapper).
                                var textChangeValue = new global::Ts.Core.TextChange
                                {
                                    TextRange = converters.FromLSPRange(o, partialChange.Range!),
                                    NewText = partialChange.Text,
                                };
                                string newContent = textChangeValue.ApplyTo(o.Content());
                                o = Overlay.NewOverlay(o.FileName(), newContent, change.Version, o.Kind());
                            }
                            else if (textChange.WholeDocument is { } wholeChange)
                            {
                                o = Overlay.NewOverlay(o.FileName(), wholeChange.Text, change.Version, o.Kind());
                            }
                        }
                        if (change.Changes.Length > 0)
                        {
                            o.SetVersion(change.Version);
                            o.Rehash();
                            o.SetMatchesDiskText(false);
                            newOverlays[path] = o;
                        }
                    }
                }

                if (events.saved)
                {
                    if (o is not null)
                    {
                        o = Overlay.NewOverlay(o.FileName(), o.Content(), o.Version(), o.Kind());
                        o.SetMatchesDiskText(true);
                        newOverlays[path] = o;
                    }
                    else if (!events.watchChanged)
                    {
                        // File was saved but never opened via didOpen; treat as a disk change.
                        result.Changed.Add(uri);
                    }
                }

                if (events.created && o is null)
                {
                    result.Created.Add(uri);
                }

                if (events.deleted && o is null)
                {
                    result.Deleted.Add(uri);
                }
            }

            overlays = newOverlays;
            return (result, newOverlays);
        }
    }
}
