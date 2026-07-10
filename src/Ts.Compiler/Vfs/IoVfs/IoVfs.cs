// Ported from tsgo internal/vfs/iovfs/iofs.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
//
// Go layers this adapter over stdlib fs.FS; the C# counterpart is IFileProvider
// (Ts.Vfs.FileProvider.cs — see its header for the adjudication). Error mapping per
// 05-conventions.md §4.2: write-family errors are thrown IOExceptions; RealpathFS's
// (string, error) is consumed in-place → Try pattern.

using System;
using System.IO;
using Ts.Vfs.Internal;
using FileMode = Ts.Vfs.FileMode;

namespace Ts.Vfs.IoVfs;

// Go: RealpathFS — fs.FS with Realpath(path) (string, error).
public interface IRealpathFileProvider : IFileProvider
{
    bool TryRealpath(string path, out string realpath);
}

// Go: WritableFS — fs.FS with write support. perm carries Go's fs.FileMode permission
// bits as raw values (0o666/0o777 from From below). Failures throw IOException.
public interface IWritableFileProvider : IFileProvider
{
    void WriteFile(string path, string data, FileMode perm);
    void AppendFile(string path, string data, FileMode perm);
    void MkdirAll(string path, FileMode perm);

    // Removes `path` and all its contents. Will return the first error it encounters.
    void Remove(string path);

    void Chtimes(string path, DateTime aTime, DateTime mTime);
}

// Go: FsWithSys — vfs.FS plus access to the underlying fs.FS (test-infrastructure
// escape hatch; tsgo uses it to downcast to *vfstest.MapFS).
public interface IFsWithSys : IFileSystem
{
    IFileProvider FSys();
}

// Go: realpath closure shape func(path string) (string, error), error consumed in-place.
internal delegate bool TryRealpathFunc(string path, out string realpath);

// Go: package-level func From (naming per the CoreUtil precedent for package-level members).
public static class IoVfsUtil
{
    // From creates a new FS from an [fs.FS].
    //
    // For paths like `c:/foo/bar`, fsys will be used as though it's rooted at `/` and the path is `/c:/foo/bar`.
    //
    // If the provided [fs.FS] implements [RealpathFS], it will be used to implement the Realpath method.
    // If the provided [fs.FS] implements [WritableFS], it will be used to implement the WriteFile method.
    //
    // From does not actually handle case-insensitivity; ensure the passed in [fs.FS]
    // respects case-insensitive file names if needed. Consider using [vfstest.FromMap] for testing.
    public static IFsWithSys From(IFileProvider fsys, bool useCaseSensitiveFileNames)
    {
        TryRealpathFunc realpath;
        if (fsys is IRealpathFileProvider realpathFsys)
        {
            realpath = (string path, out string result) =>
            {
                // Go: strings.CutPrefix(path, "/")
                bool hadSlash = path.StartsWith('/');
                string rest = hadSlash ? path[1..] : path;
                if (!realpathFsys.TryRealpath(rest, out string rp))
                {
                    result = "";
                    return false;
                }
                result = hadSlash ? "/" + rp : rp;
                return true;
            };
        }
        else
        {
            realpath = (string path, out string result) =>
            {
                result = path;
                return true;
            };
        }

        Action<string, string> writeFile;
        Action<string, string> appendFile;
        Action<string> mkdirAll;
        Action<string> remove;
        Action<string, DateTime, DateTime> chtimes;
        if (fsys is IWritableFileProvider writableFsys)
        {
            writeFile = (path, content) => writableFsys.WriteFile(CutLeadingSlash(path), content, (FileMode)0b_110_110_110 /* 0o666 */);
            appendFile = (path, content) => writableFsys.AppendFile(CutLeadingSlash(path), content, (FileMode)0b_110_110_110 /* 0o666 */);
            mkdirAll = path => writableFsys.MkdirAll(CutLeadingSlash(path), (FileMode)0b_111_111_111 /* 0o777 */);
            remove = path => writableFsys.Remove(CutLeadingSlash(path));
            chtimes = (path, aTime, mTime) => writableFsys.Chtimes(CutLeadingSlash(path), aTime, mTime);
        }
        else
        {
            writeFile = (_, _) => throw new InvalidOperationException("writeFile not supported");
            appendFile = (_, _) => throw new InvalidOperationException("appendFile not supported");
            mkdirAll = _ => throw new InvalidOperationException("mkdirAll not supported");
            remove = _ => throw new InvalidOperationException("remove not supported");
            chtimes = (_, _, _) => throw new InvalidOperationException("chtimes not supported");
        }

        return new IoFs(
            common: new Common
            {
                RootFor = root =>
                {
                    if (root == "/")
                    {
                        return fsys;
                    }

                    string p = TsPathShim.RemoveTrailingDirectorySeparator(root);
                    var sub = FileProvider.Sub(fsys, p);
                    if (sub == null)
                    {
                        if (TsPathShim.IsUrl(root))
                        {
                            return null;
                        }
                        // Go: panic(fmt.Sprintf("vfs: failed to create sub file system for %q: %v", p, err));
                        // fs.Sub's only error here is the PathError "sub <dir>: invalid name".
                        throw new InvalidOperationException($"vfs: failed to create sub file system for \"{p}\": sub {p}: invalid name");
                    }
                    return sub;
                },
            },
            useCaseSensitiveFileNames: useCaseSensitiveFileNames,
            realpath: realpath,
            writeFile: writeFile,
            appendFile: appendFile,
            mkdirAll: mkdirAll,
            remove: remove,
            chtimes: chtimes,
            fsys: fsys);
    }

    // Go: strings.CutPrefix(path, "/") where only the trimmed value is consumed.
    private static string CutLeadingSlash(string path) => path.StartsWith('/') ? path[1..] : path;
}

// Go: type ioFS struct (unexported) — internal implementation behind IoVfsUtil.From.
internal sealed class IoFs : IFsWithSys
{
    private readonly Common common;

    private readonly bool useCaseSensitiveFileNames;
    private readonly TryRealpathFunc realpath;
    private readonly Action<string, string> writeFile;
    private readonly Action<string, string> appendFile;
    private readonly Action<string> mkdirAll;
    private readonly Action<string> remove;
    private readonly Action<string, DateTime, DateTime> chtimes;
    private readonly IFileProvider fsys;

    internal IoFs(
        Common common,
        bool useCaseSensitiveFileNames,
        TryRealpathFunc realpath,
        Action<string, string> writeFile,
        Action<string, string> appendFile,
        Action<string> mkdirAll,
        Action<string> remove,
        Action<string, DateTime, DateTime> chtimes,
        IFileProvider fsys)
    {
        this.common = common;
        this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
        this.realpath = realpath;
        this.writeFile = writeFile;
        this.appendFile = appendFile;
        this.mkdirAll = mkdirAll;
        this.remove = remove;
        this.chtimes = chtimes;
        this.fsys = fsys;
    }

    public bool UseCaseSensitiveFileNames()
    {
        return useCaseSensitiveFileNames;
    }

    public bool DirectoryExists(string path)
    {
        return common.DirectoryExists(path);
    }

    public bool FileExists(string path)
    {
        return common.FileExists(path);
    }

    public Entries GetAccessibleEntries(string path)
    {
        return common.GetAccessibleEntries(path);
    }

    public IFileInfo? Stat(string path)
    {
        _ = Common.RootLength(path); // Assert path is rooted
        return common.Stat(path);
    }

    public bool TryReadFile(string path, out string contents)
    {
        return common.TryReadFile(path, out contents);
    }

    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        return common.WalkDir(root, walkFn);
    }

    public void Remove(string path)
    {
        _ = Common.RootLength(path); // Assert path is rooted
        remove(path);
    }

    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        _ = Common.RootLength(path); // Assert path is rooted
        chtimes(path, aTime, mTime);
    }

    public string Realpath(string path)
    {
        var (root, rest) = Common.SplitPath(path);
        // splitPath normalizes the path into parts (e.g. "c:/foo/bar" -> "c:/", "foo/bar")
        // Put them back together to call realpath.
        if (!realpath(root + rest, out string result))
        {
            return path;
        }
        return result;
    }

    private void WriteFileEnsuringDir(string path, string content, Action<string, string> write)
    {
        _ = Common.RootLength(path); // Assert path is rooted
        try
        {
            write(path, content);
            return;
        }
        catch (IOException)
        {
            // Go: any write error falls through to mkdirAll + retry. IOException is the
            // Go-error equivalence class for the write family (05-conventions.md §4.2).
        }
        mkdirAll(TsPathShim.GetDirectoryPath(TsPathShim.NormalizePath(path)));
        write(path, content);
    }

    public void WriteFile(string path, string content)
    {
        WriteFileEnsuringDir(path, content, writeFile);
    }

    public void AppendFile(string path, string content)
    {
        WriteFileEnsuringDir(path, content, appendFile);
    }

    public IFileProvider FSys()
    {
        return fsys;
    }
}
