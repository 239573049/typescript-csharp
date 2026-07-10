// Ported from tsgo internal/vfs/vfs.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
//
// Error mapping (05-conventions.md §4):
//  - ReadFile(path) (contents string, ok bool)  → bool TryReadFile(path, out contents) (§4.2 exemplar).
//  - WriteFile/AppendFile/Remove/Chtimes error  → void + thrown IOException (§4.2: these errors
//    cross layers and their text reaches diagnostics at the Go consumption point,
//    e.g. tsgo:internal/compiler/emitter.go:322).
//  - WalkDir error / WalkDirFunc error          → Exception? carried as a Go-error *value*
//    (returned, never thrown): fs.SkipDir/fs.SkipAll are control-flow sentinels compared
//    by identity, so the throw mapping would misfire. Callers nil-check exactly like Go.
//
// PORT-NOTE: Go aliases DirEntry/FileInfo/WalkDirFunc straight from stdlib io/fs
// (vfs.go:63-88). The BCL has no io/fs equivalent (FileSystemInfo is class-shaped and
// disk-bound), so per plan/06 §2.6 these become custom minimal abstractions below,
// keeping the Go interface surface 1:1.

using System;
using System.Collections.Generic;

namespace Ts.Vfs;

// FS is a file system abstraction.
public interface IFileSystem
{
    // UseCaseSensitiveFileNames returns true if the file system is case-sensitive.
    bool UseCaseSensitiveFileNames();

    // FileExists returns true if the file exists.
    bool FileExists(string path);

    // ReadFile reads the file specified by path and returns the content.
    // If the file fails to be read, ok will be false.
    bool TryReadFile(string path, out string contents);

    void WriteFile(string path, string data);

    // AppendFile appends data to the file at path, creating it if it does not exist.
    void AppendFile(string path, string data);

    // Removes `path` and all its contents. Will return the first error it encounters.
    void Remove(string path);

    // Chtimes changes the access and modification times of the named
    void Chtimes(string path, DateTime aTime, DateTime mTime);

    // DirectoryExists returns true if the path is a directory.
    bool DirectoryExists(string path);

    // GetAccessibleEntries returns the files/directories in the specified directory.
    // If any entry is a symlink, it will be followed.
    Entries GetAccessibleEntries(string path);

    IFileInfo? Stat(string path);

    // WalkDir walks the file tree rooted at root, calling walkFn for each file or directory in the tree.
    // It is has the same behavior as [fs.WalkDir], but with paths as [string].
    Exception? WalkDir(string root, WalkDirFunc walkFn);

    // Realpath returns the "real path" of the specified path,
    // following symlinks and correcting filename casing.
    string Realpath(string path);
}

// Go: ReadFile method shape reused as a first-class function type by wrapvfs.Replacements.
public delegate bool TryReadFileFunc(string path, out string contents);

public struct Entries
{
    // PORT-NOTE: Go []string zero value is nil; null preserves that here
    // (05-conventions.md §6.1 — nil vs empty is kept per-case).
    public string[]? Files { get; init; }
    public string[]? Directories { get; init; }

    // Symlinks contains the names of entries in Files or Directories that were
    // originally symbolic links (or reparse points) on disk. The names are the
    // same as those in Files/Directories (i.e., the link name, not the target).
    // nil means symlink information is not available and the entries may need
    // to be re-checked for symlinks.
    //
    // PORT-NOTE: the null/empty distinction is load-bearing — vfsmatch falls back to
    // Realpath when Symlinks is nil (tsgo:internal/vfs/vfsmatch/vfsmatch.go:635-644).
    public HashSet<string>? Symlinks { get; init; }
}

// DirEntry is [fs.DirEntry] — custom minimal abstraction (see file header).
public interface IDirEntry
{
    string Name();
    bool IsDir();
    FileMode Type();

    // Go: Info() (FileInfo, error) — every tsgo consumer nil-checks or propagates the
    // error without reading its text, so null stands in for the error case.
    IFileInfo? Info();
}

// FileInfo is [fs.FileInfo] — custom minimal abstraction (see file header).
public interface IFileInfo
{
    string Name();
    long Size();
    FileMode Mode();
    DateTime ModTime();
    bool IsDir();

    // Go: Sys() any — only the vfstest MapFS uses it (carries realpath/symlink data).
    object? Sys();
}

// Go: stdlib io/fs.FileMode — only the type bits the vfs layer consumes are ported;
// permission bits travel as raw numeric values (e.g. 0o666 in iovfs).
[Flags]
public enum FileMode : uint
{
    None = 0,
    Dir = 1u << 31,        // fs.ModeDir: d — is a directory
    Symlink = 1u << 27,    // fs.ModeSymlink: L — symbolic link
    Device = 1u << 26,     // fs.ModeDevice: D — device file
    NamedPipe = 1u << 25,  // fs.ModeNamedPipe: p — named pipe (FIFO)
    Socket = 1u << 24,     // fs.ModeSocket: S — Unix domain socket
    CharDevice = 1u << 21, // fs.ModeCharDevice: c — Unix character device
    Irregular = 1u << 19,  // fs.ModeIrregular: ? — non-regular file

    // fs.ModeType — mask for the type bits.
    TypeMask = Dir | Symlink | NamedPipe | Socket | Device | CharDevice | Irregular,
}

public static class FileModeExtensions
{
    // Go: fs.FileMode.IsDir()
    public static bool IsDir(this FileMode m) => (m & FileMode.Dir) != 0;

    // Go: fs.FileMode.IsRegular()
    public static bool IsRegular(this FileMode m) => (m & FileMode.TypeMask) == 0;

    // Go: fs.FileMode.Type()
    public static FileMode Type(this FileMode m) => m & FileMode.TypeMask;
}

// Go: errors.New sentinel values — compared by identity, carried as values, never thrown.
public sealed class SentinelError : Exception
{
    public SentinelError(string message)
        : base(message)
    {
    }
}

// Go: package-level vars of vfs.go (naming per the CoreUtil precedent for package-level members).
public static class VfsUtil
{
    // PORT-NOTE: the five fs error re-exports are dead exports in tsgo (no reference
    // outside vfs.go itself); ported for interface-surface fidelity.
    public static readonly Exception ErrInvalid = new SentinelError("invalid argument");        // fs.ErrInvalid
    public static readonly Exception ErrPermission = new SentinelError("permission denied");    // fs.ErrPermission
    public static readonly Exception ErrExist = new SentinelError("file already exists");       // fs.ErrExist
    public static readonly Exception ErrNotExist = new SentinelError("file does not exist");    // fs.ErrNotExist
    public static readonly Exception ErrClosed = new SentinelError("file already closed");      // fs.ErrClosed

    // SkipAll is [fs.SkipAll].
    public static readonly Exception SkipAll = new SentinelError("skip everything and stop the walk");

    // SkipDir is [fs.SkipDir].
    public static readonly Exception SkipDir = new SentinelError("skip this directory");
}

// WalkDirFunc is [fs.WalkDirFunc].
// d is null only when err is non-null (root stat failure). The returned Exception is a
// Go-error value: null = continue, VfsUtil.SkipDir/SkipAll = control flow, anything else
// aborts the walk and is returned by WalkDir.
public delegate Exception? WalkDirFunc(string path, IDirEntry? d, Exception? err);
