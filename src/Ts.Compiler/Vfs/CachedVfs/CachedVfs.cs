// Ported from tsgo internal/vfs/cachedvfs/cachedvfs.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Threading;
using Ts.Collections;

namespace Ts.Vfs.CachedVfs;

// Go: type FS struct — caching decorator over vfs.FS. Class name per the BundledWrapFs
// precedent (plan/01 §9.2): package-qualified Go `cachedvfs.FS` → `CachedFs`.
public sealed class CachedFs : IFileSystem
{
    private readonly IFileSystem fs;
    private int enabled; // Go: atomic.Bool → int + Volatile/Interlocked (05-conventions.md §14)

    private readonly SyncMap<string, bool> directoryExistsCache = new();
    private readonly SyncMap<string, bool> fileExistsCache = new();
    private readonly SyncMap<string, Entries> getAccessibleEntriesCache = new();
    private readonly SyncMap<string, string> realpathCache = new();
    private readonly SyncMap<string, IFileInfo?> statCache = new();

    private CachedFs(IFileSystem fs)
    {
        this.fs = fs;
    }

    private bool Enabled => Volatile.Read(ref enabled) != 0;

    public static CachedFs From(IFileSystem fs)
    {
        var fsys = new CachedFs(fs);
        Volatile.Write(ref fsys.enabled, 1);
        return fsys;
    }

    public void DisableAndClearCache()
    {
        // Go: enabled.CompareAndSwap(true, false)
        if (Interlocked.CompareExchange(ref enabled, 0, 1) == 1)
        {
            ClearCache();
        }
    }

    public void Enable()
    {
        Volatile.Write(ref enabled, 1);
    }

    public void ClearCache()
    {
        directoryExistsCache.Clear();
        fileExistsCache.Clear();
        getAccessibleEntriesCache.Clear();
        realpathCache.Clear();
        statCache.Clear();
    }

    public bool DirectoryExists(string path)
    {
        if (Enabled)
        {
            if (directoryExistsCache.TryLoad(path, out bool cached))
            {
                return cached;
            }
        }

        bool ret = fs.DirectoryExists(path);

        if (Enabled)
        {
            directoryExistsCache.Store(path, ret);
        }

        return ret;
    }

    public bool FileExists(string path)
    {
        if (Enabled)
        {
            if (fileExistsCache.TryLoad(path, out bool cached))
            {
                return cached;
            }
        }

        bool ret = fs.FileExists(path);

        if (Enabled)
        {
            fileExistsCache.Store(path, ret);
        }

        return ret;
    }

    public Entries GetAccessibleEntries(string path)
    {
        if (Enabled)
        {
            if (getAccessibleEntriesCache.TryLoad(path, out var cached))
            {
                return cached;
            }
        }

        var ret = fs.GetAccessibleEntries(path);

        if (Enabled)
        {
            getAccessibleEntriesCache.Store(path, ret);
        }

        return ret;
    }

    public bool TryReadFile(string path, out string contents)
    {
        return fs.TryReadFile(path, out contents);
    }

    public string Realpath(string path)
    {
        if (Enabled)
        {
            if (realpathCache.TryLoad(path, out var cached))
            {
                return cached;
            }
        }

        string ret = fs.Realpath(path);

        if (Enabled)
        {
            realpathCache.Store(path, ret);
        }

        return ret;
    }

    public void Remove(string path)
    {
        fs.Remove(path);
    }

    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        fs.Chtimes(path, aTime, mTime);
    }

    public IFileInfo? Stat(string path)
    {
        if (Enabled)
        {
            if (statCache.TryLoad(path, out var cached))
            {
                return cached;
            }
        }

        var ret = fs.Stat(path);

        if (Enabled)
        {
            statCache.Store(path, ret);
        }

        return ret;
    }

    public bool UseCaseSensitiveFileNames()
    {
        return fs.UseCaseSensitiveFileNames();
    }

    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        return fs.WalkDir(root, walkFn);
    }

    public void WriteFile(string path, string data)
    {
        fs.WriteFile(path, data);
    }

    public void AppendFile(string path, string data)
    {
        fs.AppendFile(path, data);
    }
}
