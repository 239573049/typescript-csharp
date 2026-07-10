// Ported from tsgo internal/vfs/trackingvfs/trackingvfs.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

// Package trackingvfs provides a VFS wrapper that records every file path
// accessed during compilation. This allows watch mode to know exactly which
// files and directories the compiler depended on, including non-existent
// paths from failed module resolution.

using System;
using Ts.Collections;

namespace Ts.Vfs.TrackingVfs;

// FS wraps a vfs.FS and records every path accessed via read-like operations.
// Write operations (WriteFile, Remove, Chtimes) are not tracked since they
// represent outputs, not dependencies.
//
// Go: type FS struct — class name per the BundledWrapFs precedent (plan/01 §9.2):
// package-qualified `trackingvfs.FS` → `TrackingFs`. Constructed in Go as a composite
// literal (&FS{Inner: fs}) → required init property; SeenFiles is usable at zero value.
public sealed class TrackingFs : IFileSystem
{
    public required IFileSystem Inner { get; init; }
    public SyncSet<string> SeenFiles { get; } = new();

    public bool TryReadFile(string path, out string contents)
    {
        SeenFiles.Add(path);
        return Inner.TryReadFile(path, out contents);
    }

    public bool FileExists(string path)
    {
        SeenFiles.Add(path);
        return Inner.FileExists(path);
    }

    public bool UseCaseSensitiveFileNames() => Inner.UseCaseSensitiveFileNames();

    public void WriteFile(string path, string data)
    {
        Inner.WriteFile(path, data);
    }

    public void AppendFile(string path, string data)
    {
        Inner.AppendFile(path, data);
    }

    public void Remove(string path) => Inner.Remove(path);

    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        Inner.Chtimes(path, aTime, mTime);
    }

    public bool DirectoryExists(string path)
    {
        SeenFiles.Add(path);
        return Inner.DirectoryExists(path);
    }

    public Entries GetAccessibleEntries(string path)
    {
        SeenFiles.Add(path);
        return Inner.GetAccessibleEntries(path);
    }

    public IFileInfo? Stat(string path)
    {
        SeenFiles.Add(path);
        return Inner.Stat(path);
    }

    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        SeenFiles.Add(root);
        return Inner.WalkDir(root, (path, d, err) =>
        {
            SeenFiles.Add(path);
            return walkFn(path, d, err);
        });
    }

    public string Realpath(string path)
    {
        SeenFiles.Add(path);
        return Inner.Realpath(path);
    }
}
