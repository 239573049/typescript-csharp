// Ported from tsgo internal/vfs/wrapvfs/wrapvfs.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.Vfs.WrapVfs;

// Go: type Replacements struct — optional per-method overrides; a null field delegates
// to the wrapped FS. Delegate shapes follow the IFileSystem member mapping
// (05-conventions.md §4: ReadFile → Try, write family → void + IOException,
// WalkDir → Exception? value).
public sealed class Replacements
{
    public Func<bool>? UseCaseSensitiveFileNames { get; init; }
    public Func<string, bool>? FileExists { get; init; }
    public TryReadFileFunc? ReadFile { get; init; }
    public Action<string, string>? WriteFile { get; init; }
    public Action<string, string>? AppendFile { get; init; }
    public Action<string>? Remove { get; init; }
    public Action<string, DateTime, DateTime>? Chtimes { get; init; }
    public Func<string, bool>? DirectoryExists { get; init; }
    public Func<string, Entries>? GetAccessibleEntries { get; init; }
    public Func<string, IFileInfo?>? Stat { get; init; }
    public Func<string, WalkDirFunc, Exception?>? WalkDir { get; init; }
    public Func<string, string>? Realpath { get; init; }
}

// Go: package-level func Wrap (naming per the CoreUtil precedent for package-level members).
public static class WrapVfsUtil
{
    public static IFileSystem Wrap(IFileSystem fs, Replacements replacements)
    {
        return new WrappedFs(fs, replacements);
    }
}

// Go: type wrappedFS struct (unexported).
internal sealed class WrappedFs : IFileSystem
{
    private readonly IFileSystem fs;
    private readonly Replacements replacements;

    internal WrappedFs(IFileSystem fs, Replacements replacements)
    {
        this.fs = fs;
        this.replacements = replacements;
    }

    // UseCaseSensitiveFileNames implements [vfs.FS].
    public bool UseCaseSensitiveFileNames()
    {
        if (replacements.UseCaseSensitiveFileNames != null)
        {
            return replacements.UseCaseSensitiveFileNames();
        }
        return fs.UseCaseSensitiveFileNames();
    }

    // FileExists implements [vfs.FS].
    public bool FileExists(string path)
    {
        if (replacements.FileExists != null)
        {
            return replacements.FileExists(path);
        }
        return fs.FileExists(path);
    }

    // ReadFile implements [vfs.FS].
    public bool TryReadFile(string path, out string contents)
    {
        if (replacements.ReadFile != null)
        {
            return replacements.ReadFile(path, out contents);
        }
        return fs.TryReadFile(path, out contents);
    }

    // WriteFile implements [vfs.FS].
    public void WriteFile(string path, string data)
    {
        if (replacements.WriteFile != null)
        {
            replacements.WriteFile(path, data);
            return;
        }
        fs.WriteFile(path, data);
    }

    // AppendFile implements [vfs.FS].
    public void AppendFile(string path, string data)
    {
        if (replacements.AppendFile != null)
        {
            replacements.AppendFile(path, data);
            return;
        }
        fs.AppendFile(path, data);
    }

    // Remove implements [vfs.FS].
    public void Remove(string path)
    {
        if (replacements.Remove != null)
        {
            replacements.Remove(path);
            return;
        }
        fs.Remove(path);
    }

    // Chtimes implements [vfs.FS].
    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        if (replacements.Chtimes != null)
        {
            replacements.Chtimes(path, aTime, mTime);
            return;
        }
        fs.Chtimes(path, aTime, mTime);
    }

    // DirectoryExists implements [vfs.FS].
    public bool DirectoryExists(string path)
    {
        if (replacements.DirectoryExists != null)
        {
            return replacements.DirectoryExists(path);
        }
        return fs.DirectoryExists(path);
    }

    // GetAccessibleEntries implements [vfs.FS].
    public Entries GetAccessibleEntries(string path)
    {
        if (replacements.GetAccessibleEntries != null)
        {
            return replacements.GetAccessibleEntries(path);
        }
        return fs.GetAccessibleEntries(path);
    }

    // Stat implements [vfs.FS].
    public IFileInfo? Stat(string path)
    {
        if (replacements.Stat != null)
        {
            return replacements.Stat(path);
        }
        return fs.Stat(path);
    }

    // WalkDir implements [vfs.FS].
    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        if (replacements.WalkDir != null)
        {
            return replacements.WalkDir(root, walkFn);
        }
        return fs.WalkDir(root, walkFn);
    }

    // Realpath implements [vfs.FS].
    public string Realpath(string path)
    {
        if (replacements.Realpath != null)
        {
            return replacements.Realpath(path);
        }
        return fs.Realpath(path);
    }
}
