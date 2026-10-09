// Ported from tsgo internal/compiler/projectreferencedtsfakinghost.go (whole file).
//
// PORT-NOTEs:
//  * Go's projectReferenceDtsFakingVfs.fileExistsIfProjectReferenceDts /
//    directoryExistsIfProjectReferenceDeclDir return core.Tristate (Unknown/True/False).
//    Ts.Vfs.IFileSystem's FileExists/DirectoryExists are a fixed `bool`, so those two
//    helpers keep the TSUnknown sentinel internally as `bool?` (null == TSUnknown) and
//    the two entry points fuse Go's `if result != TSUnknown { return result == TSTrue }`
//    arm, which leaves no other place in the (bool-returning) interface.
//  * Go vfs.FS methods that panic("should not be called by resolver") map to
//    NotSupportedException with the same message.
//  * cachedvfs.From → Ts.Vfs.CachedVfs.CachedFs.From.
//  * `symlinks.KnownSymlinks{}` (the Go zero value) → new KnownSymlinks("", false): the
//    zero-value cwd is only read by SetFile, which is given an already-absolute realpath.
//  * Go `strings.CutPrefix(s, prefix)` → StartsWith + slice.
//  * `result.IsTrue()` (Tristate) → `result == true` on bool?.

using System;
using System.Collections.Generic;

using Ts.Collections;

using Path = global::Ts.TsPath.Path;

namespace Ts.Compiler;

// projectreferencedtsfakinghost.go:16-21
internal sealed class ProjectReferenceDtsFakingHost : global::Ts.Module.ResolutionHost
{
    private CompilerHost host = null!;
    private global::Ts.Vfs.CachedVfs.CachedFs fs = null!;

    // projectreferencedtsfakinghost.go:23-34
    internal static ProjectReferenceDtsFakingHost NewProjectReferenceDtsFakingHost(FileLoader loader)
    {
        // Create a new host that will fake the dts files
        var host = new ProjectReferenceDtsFakingHost
        {
            host = loader.Opts.Host,
            fs = global::Ts.Vfs.CachedVfs.CachedFs.From(new ProjectReferenceDtsFakingVfs
            {
                projectReferenceFileMapper = loader.ProjectReferenceFileMapper,
                dtsDirectories = loader.DtsDirectories,
            }),
        };
        return host;
    }

    private ProjectReferenceDtsFakingHost()
    {
    }

    // FS implements module.ResolutionHost.
    public global::Ts.Vfs.IFileSystem FS()
    {
        return fs;
    }

    // GetCurrentDirectory implements module.ResolutionHost.
    public string GetCurrentDirectory()
    {
        return host.GetCurrentDirectory();
    }
}

// projectreferencedtsfakinghost.go:46-50
internal sealed class ProjectReferenceDtsFakingVfs : global::Ts.Vfs.IFileSystem
{
    internal ProjectReferenceFileMapper projectReferenceFileMapper = null!;
    internal Set<Path> dtsDirectories = null!;
    internal readonly global::Ts.Symlinks.KnownSymlinks knownSymlinks = new("", false);

    // UseCaseSensitiveFileNames implements vfs.FS.
    public bool UseCaseSensitiveFileNames()
    {
        return projectReferenceFileMapper.Opts.Host.FS().UseCaseSensitiveFileNames();
    }

    // FileExists implements vfs.FS.
    public bool FileExists(string path)
    {
        if (projectReferenceFileMapper.Opts.Host.FS().FileExists(path))
        {
            return true;
        }
        if (!global::Ts.TsPath.TsPath.IsDeclarationFileName(path))
        {
            return false;
        }
        // Project references go to source file instead of .d.ts file
        bool? result = FileExistsIfProjectReferenceDts(path); // Go: isFile /*isFile*/, true entry arm
        if (result is not null)
        {
            return result == true;
        }
        return FileOrDirectoryExistsUsingSource(path /*isFile*/, isFile: true);
    }

    // ReadFile implements vfs.FS.
    public bool TryReadFile(string path, out string contents)
    {
        // Dont need to override as we cannot mimick read file
        return projectReferenceFileMapper.Opts.Host.FS().TryReadFile(path, out contents);
    }

    // WriteFile implements vfs.FS.
    public void WriteFile(string path, string data)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // AppendFile implements vfs.FS.
    public void AppendFile(string path, string data)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // Remove implements vfs.FS.
    public void Remove(string path)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // Chtimes implements vfs.FS.
    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // DirectoryExists implements vfs.FS.
    public bool DirectoryExists(string path)
    {
        if (projectReferenceFileMapper.Opts.Host.FS().DirectoryExists(path))
        {
            HandleDirectoryCouldBeSymlink(path);
            return true;
        }
        bool? result = DirectoryExistsIfProjectReferenceDeclDir(path);
        if (result is not null)
        {
            return result == true;
        }
        return FileOrDirectoryExistsUsingSource(path /*isFile*/, isFile: false);
    }

    // GetAccessibleEntries implements vfs.FS.
    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // Stat implements vfs.FS.
    public global::Ts.Vfs.IFileInfo? Stat(string path)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // WalkDir implements vfs.FS.
    public Exception? WalkDir(string root, global::Ts.Vfs.WalkDirFunc walkFn)
    {
        throw new NotSupportedException("should not be called by resolver");
    }

    // Realpath implements vfs.FS.
    public string Realpath(string path)
    {
        if (knownSymlinks.Files().TryLoad(ToPath(path), out string? result))
        {
            return result!;
        }
        return projectReferenceFileMapper.Opts.Host.FS().Realpath(path);
    }

    // projectreferencedtsfakinghost.go:130-132
    private Path ToPath(string path)
    {
        return global::Ts.TsPath.TsPath.ToPath(path, projectReferenceFileMapper.Opts.Host.GetCurrentDirectory(), UseCaseSensitiveFileNames());
    }

    // projectreferencedtsfakinghost.go:134-163
    private void HandleDirectoryCouldBeSymlink(string directory)
    {
        if (global::Ts.TsPath.TsPath.ContainsIgnoredPath(directory))
        {
            return;
        }

        // Because we already watch node_modules, handle symlinks in there
        if (!directory.Contains("/node_modules/", StringComparison.Ordinal))
        {
            return;
        }

        Path directoryPath = ToPath(directory).EnsureTrailingDirectorySeparator();
        if (knownSymlinks.Directories().TryLoad(directoryPath, out _))
        {
            return;
        }

        string realDirectory = Realpath(directory);
        Path realPath;
        if (realDirectory == directory)
        {
            // not symlinked
            return;
        }
        realPath = ToPath(realDirectory).EnsureTrailingDirectorySeparator();
        if (realPath == directoryPath)
        {
            // not symlinked
            return;
        }
        knownSymlinks.SetDirectory(directory, directoryPath, new global::Ts.Symlinks.KnownDirectoryLink
        {
            Real = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(realDirectory),
            RealPath = realPath,
        });
    }

    // projectreferencedtsfakinghost.go:165-214
    private bool FileOrDirectoryExistsUsingSource(string fileOrDirectory, bool isFile)
    {
        // Check current directory or file
        bool? result = isFile ? FileExistsIfProjectReferenceDts(fileOrDirectory) : DirectoryExistsIfProjectReferenceDeclDir(fileOrDirectory);
        if (result is not null)
        {
            return result == true;
        }

        Path fileOrDirectoryPath = ToPath(fileOrDirectory);
        if (!fileOrDirectoryPath.ToString().Contains("/node_modules/", StringComparison.Ordinal))
        {
            return false;
        }
        // Check if the directory or file is a symlinked package
        string packageRoot = global::Ts.Module.Module.ParseNodeModuleFromPath(fileOrDirectory, true /*isFolder*/);
        if (packageRoot != "")
        {
            HandleDirectoryCouldBeSymlink(packageRoot);
        }
        SyncMap<Path, global::Ts.Symlinks.KnownDirectoryLink?> knownDirectoryLinks = knownSymlinks.Directories();
        if (knownDirectoryLinks.Size() == 0)
        {
            return false;
        }
        if (isFile)
        {
            if (knownSymlinks.Files().TryLoad(fileOrDirectoryPath, out _))
            {
                return true;
            }
        }

        // If it contains node_modules check if its one of the symlinked path we know of
        bool exists = false;
        // map-iter: 序无关 —— 命中任一已知目录链接即停，与枚举顺序无关
        knownDirectoryLinks.Range((directoryPath, knownDirectoryLink) =>
        {
            string fileOrDirectoryPathString = fileOrDirectoryPath.ToString();
            string directoryPathString = directoryPath.ToString();
            if (!fileOrDirectoryPathString.StartsWith(directoryPathString, StringComparison.Ordinal))
            {
                return true;
            }
            string relative = fileOrDirectoryPathString[directoryPathString.Length..];
            // Go calls the local `fileOrDirectoryExistsUsingSource` function value here (the
            // Tristate helper selected by isFile), NOT this method.
            bool? realResult = isFile
                ? FileExistsIfProjectReferenceDts(knownDirectoryLink!.RealPath.ToString() + relative)
                : DirectoryExistsIfProjectReferenceDeclDir(knownDirectoryLink!.RealPath.ToString() + relative);
            exists = realResult == true;
            if (exists)
            {
                if (isFile)
                {
                    // Store the real path for the file
                    string absolutePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileOrDirectory, projectReferenceFileMapper.Opts.Host.GetCurrentDirectory());
                    knownSymlinks.SetFile(
                        absolutePath,
                        fileOrDirectoryPath,
                        knownDirectoryLink.Real + absolutePath[directoryPathString.Length..]);
                }
                return false;
            }
            return true;
        });
        return exists;
    }

    // projectreferencedtsfakinghost.go:216-222 — returns core.Tristate (null == TSUnknown).
    private bool? FileExistsIfProjectReferenceDts(string file)
    {
        global::Ts.TsOptions.SourceOutputAndProjectReference? source = projectReferenceFileMapper.GetProjectReferenceFromOutputDts(ToPath(file));
        if (source is not null)
        {
            return projectReferenceFileMapper.Opts.Host.FS().FileExists(source.Source);
        }
        return null;
    }

    // projectreferencedtsfakinghost.go:224-237 — returns core.Tristate (null == TSUnknown).
    private bool? DirectoryExistsIfProjectReferenceDeclDir(string dir)
    {
        Path dirPath = ToPath(dir);
        string dirPathWithTrailingDirectorySeparator = dirPath + "/";
        // map-iter: 序无关 —— 命中任一 declaration 目录即返回 true
        foreach (Path declDirPath in dtsDirectories.Keys())
        {
            string declDirPathString = declDirPath.ToString();
            if (dirPath.ToString() == declDirPathString ||
                // Any parent directory of declaration dir
                declDirPathString.StartsWith(dirPathWithTrailingDirectorySeparator, StringComparison.Ordinal) ||
                // Any directory inside declaration dir
                dirPath.ToString().StartsWith(declDirPathString + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return null;
    }
}
