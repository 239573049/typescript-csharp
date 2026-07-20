// Ported from tsgo internal/testutil/fsbaselineutil/differ.go — the FS-snapshot differ that
// renders "//// [path] *new*/*modified*/..." sections into tsc/tsbuild baselines.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Ts.Collections;
using Ts.Vfs;
using FileMode = global::Ts.Vfs.FileMode;
using Ts.Vfs.IoVfs;
using Ts.Vfs.VfsTest;

namespace Ts.TestUtil.FsBaselineUtil;

// differ.go:18-23
public sealed class DiffEntry
{
    public string Content = "";
    public DateTime MTime;
    public bool IsWritten;
    public string SymlinkTarget = "";
}

// differ.go:25-28
public sealed class FsSnapshot
{
    public Dictionary<string, DiffEntry> Snap = new(StringComparer.Ordinal);
    public SyncSet<string> DefaultLibs = new();
}

// differ.go:30-36
public sealed class FSDiffer
{
    public IFsWithSys FS = null!;
    public Func<SyncSet<string>?>? DefaultLibs;
    public SyncSet<string> WrittenFiles = null!;

    private FsSnapshot? serializedDiff;

    // differ.go:38-40
    public MapFS MapFs() => (MapFS)FS.FSys();

    // differ.go:42-44
    public FsSnapshot? SerializedDiff() => serializedDiff;

    // differ.go:46-95
    public void BaselineFSwithDiff(TextWriter baseline)
    {
        // todo: baselines the entire fs, possibly doesn't correctly diff all cases of emitted files, since emit isn't fully implemented and doesn't always emit the same way as strada
        var snap = new Dictionary<string, DiffEntry>(StringComparer.Ordinal);

        var diffs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, file) in MapFs().Entries())
        {
            if ((file.Mode & FileMode.Symlink) != 0)
            {
                if (!MapFs().GetTargetOfSymlink(path, out var target))
                {
                    throw new InvalidOperationException("Failed to resolve symlink target: " + path);
                }
                var newSymlinkEntry = new DiffEntry { SymlinkTarget = target };
                snap[path] = newSymlinkEntry;
                AddFsEntryDiff(diffs, newSymlinkEntry, path);
                continue;
            }
            else if (file.Mode.IsRegular())
            {
                var content = SanitizeInternalSymbolName(Encoding.UTF8.GetString(file.Data ?? []));
                var newEntry = new DiffEntry { Content = content, MTime = file.ModTime, IsWritten = WrittenFiles.Has(path) };
                snap[path] = newEntry;
                AddFsEntryDiff(diffs, newEntry, path);
            }
        }
        if (serializedDiff != null)
        {
            foreach (var path in serializedDiff.Snap.Keys) // map-iter: 序无关 (diffs sorted before rendering)
            {
                if (MapFs().GetFileInfo(path) == null)
                {
                    // report deleted
                    AddFsEntryDiff(diffs, null, path);
                }
            }
        }
        var defaultLibs = new SyncSet<string>();
        if (DefaultLibs?.Invoke() is { } libs)
        {
            libs.Range(libPath =>
            {
                defaultLibs.Add(libPath);
                return true;
            });
        }
        serializedDiff = new FsSnapshot
        {
            Snap = snap,
            DefaultLibs = defaultLibs,
        };
        var diffKeys = new List<string>(diffs.Keys);
        diffKeys.Sort(StringComparer.Ordinal);
        foreach (var path in diffKeys)
        {
            baseline.Write("//// [" + path + "] ");
            baseline.Write(diffs[path]);
            baseline.Write('\n');
        }
        baseline.Write('\n');
        WrittenFiles.Clear(); // Reset written files after baseline
    }

    private static readonly Regex internalSymbolRegex = new(@"�@[^@]+@[0-9]+", RegexOptions.CultureInvariant);

    // differ.go:99-109 — Replaces internal symbol names of shape �@symbolName@123 with
    // �@symbolName@<symbolId> to avoid baselining differences in symbol ids, which can
    // change between runs.
    public static string SanitizeInternalSymbolName(string s)
    {
        if (!s.Contains("�@", StringComparison.Ordinal))
        {
            return s;
        }
        return internalSymbolRegex.Replace(s, match =>
        {
            var idStart = match.Value.LastIndexOf('@');
            return match.Value[..idStart] + "@<symbolId>";
        });
    }

    // differ.go:111-139
    private void AddFsEntryDiff(Dictionary<string, string> diffs, DiffEntry? newDirContent, string path)
    {
        DiffEntry? oldDirContent = null;
        SyncSet<string>? defaultLibs = null;
        if (serializedDiff != null)
        {
            oldDirContent = serializedDiff.Snap.GetValueOrDefault(path);
            defaultLibs = serializedDiff.DefaultLibs;
        }
        // todo handle more cases of fs changes
        if (oldDirContent == null)
        {
            if (DefaultLibs?.Invoke() is not { } libs || !libs.Has(path))
            {
                if (newDirContent!.SymlinkTarget.Length > 0)
                {
                    diffs[path] = "-> " + newDirContent.SymlinkTarget + " *new*";
                }
                else
                {
                    diffs[path] = "*new* \n" + newDirContent.Content;
                }
            }
        }
        else if (newDirContent == null)
        {
            diffs[path] = "*deleted*";
        }
        else if (newDirContent.Content != oldDirContent.Content)
        {
            diffs[path] = "*modified* \n" + newDirContent.Content;
        }
        else if (newDirContent.IsWritten)
        {
            diffs[path] = "*rewrite with same content*";
        }
        else if (newDirContent.MTime != oldDirContent.MTime)
        {
            diffs[path] = "*mTime changed*";
        }
        else if (defaultLibs != null && defaultLibs.Has(path) && DefaultLibs?.Invoke() is { } currentLibs && !currentLibs.Has(path))
        {
            // Lib file that was read
            diffs[path] = "*Lib*\n" + newDirContent.Content;
        }
    }

    // differ.go:141-145 — FileChange represents a filesystem change detected between snapshots.
    public readonly record struct FileChange(string Path, bool Deleted);

    // differ.go:147-177
    public FileChange[] ChangedPaths()
    {
        if (serializedDiff == null)
        {
            return [];
        }

        var changes = new List<FileChange>();
        var oldSnap = serializedDiff;

        // Check current files against previous snapshot.
        foreach (var (path, file) in MapFs().Entries())
        {
            if ((file.Mode & FileMode.Symlink) != 0 || !file.Mode.IsRegular())
            {
                continue;
            }
            if (!oldSnap.Snap.TryGetValue(path, out var old))
            {
                // New file.
                changes.Add(new FileChange(path, false));
            }
            else if (Encoding.UTF8.GetString(file.Data ?? []) != old.Content || file.ModTime != old.MTime)
            {
                // Modified or touched file.
                changes.Add(new FileChange(path, false));
            }
        }

        // Check for deleted files.
        foreach (var path in oldSnap.Snap.Keys) // map-iter: 序无关 (change-set consumers sort)
        {
            if (MapFs().GetFileInfo(path) == null)
            {
                changes.Add(new FileChange(path, true));
            }
        }

        return changes.ToArray();
    }
}
