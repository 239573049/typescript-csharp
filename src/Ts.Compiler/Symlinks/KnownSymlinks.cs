// Ported from tsgo internal/symlinks/knownsymlinks.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Module;
using Ts.TsPath;

namespace Ts.Symlinks;

public sealed class KnownDirectoryLink
{
    // Matches the casing returned by `realpath`. Used to compute the `realpath` of children.
    // Always has trailing directory separator
    public string Real = "";

    // toPath(real). Stored to avoid repeated recomputation.
    // Always has trailing directory separator
    public Path RealPath;
}

public sealed class KnownSymlinks
{
    private readonly SyncMap<Path, KnownDirectoryLink?> directories = new();
    private readonly SyncMap<Path, SyncSet<string>> directoriesByRealpath = new();
    private readonly SyncMap<Path, string> files = new();
    private readonly SyncMap<Path, SyncSet<string>> filesByRealpath = new();
    // internal (not private): Go's package-private fields, read by the same-package
    // ported tests via InternalsVisibleTo (05-conventions.md §1.1/§16).
    internal readonly string cwd;
    internal readonly bool useCaseSensitiveFileNames;

    public KnownSymlinks(string currentDirectory, bool useCaseSensitiveFileNames)
    {
        this.cwd = currentDirectory;
        this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
    }

    public bool HasDirectory(Path symlinkPath)
    {
        return directories.TryLoad(symlinkPath.EnsureTrailingDirectorySeparator(), out _);
    }

    // Gets a map from symlink to realpath. Keys have trailing directory separators.
    public SyncMap<Path, KnownDirectoryLink?> Directories()
    {
        return directories;
    }

    public SyncMap<Path, SyncSet<string>> DirectoriesByRealpath()
    {
        return directoriesByRealpath;
    }

    // Gets a map from symlink to realpath
    public SyncMap<Path, string> Files()
    {
        return files;
    }

    // Gets a map from realpath to symlinks
    public SyncMap<Path, SyncSet<string>> FilesByRealpath()
    {
        return filesByRealpath;
    }

    public void SetDirectory(string symlink, Path symlinkPath, KnownDirectoryLink? realDirectory)
    {
        if (realDirectory != null)
        {
            if (!directories.TryLoad(symlinkPath, out _))
            {
                var (set, _) = directoriesByRealpath.LoadOrStore(realDirectory.RealPath, new SyncSet<string>());
                set.Add(symlink);
            }
        }
        directories.Store(symlinkPath, realDirectory);
    }

    public void SetFile(string symlink, Path symlinkPath, string realpath)
    {
        if (!files.TryLoad(symlinkPath, out _))
        {
            Path realpathPath = global::Ts.TsPath.TsPath.ToPath(realpath, cwd, useCaseSensitiveFileNames);
            var (set, _) = filesByRealpath.LoadOrStore(realpathPath, new SyncSet<string>());
            set.Add(symlink);
        }
        files.Store(symlinkPath, realpath);
    }

    public void SetSymlinksFromResolutions(
        Action<Action<ResolvedModule, string, ResolutionMode, Path>, SourceFile?> forEachResolvedModule,
        Action<Action<ResolvedTypeReferenceDirective, string, ResolutionMode, Path>, SourceFile?> forEachResolvedTypeReferenceDirective)
    {
        forEachResolvedModule(
            (resolution, moduleName, mode, filePath) => ProcessResolution(resolution.OriginalPath, resolution.ResolvedFileName),
            null);
        forEachResolvedTypeReferenceDirective(
            (resolution, moduleName, mode, filePath) => ProcessResolution(resolution.OriginalPath, resolution.ResolvedFileName),
            null);
    }

    public void ProcessResolution(string originalPath, string resolvedFileName)
    {
        if (originalPath == "" || resolvedFileName == "")
        {
            return;
        }
        SetFile(originalPath, global::Ts.TsPath.TsPath.ToPath(originalPath, cwd, useCaseSensitiveFileNames), resolvedFileName);
        var (commonResolved, commonOriginal) = GuessDirectorySymlink(resolvedFileName, originalPath, cwd);
        if (commonResolved != "" && commonOriginal != "")
        {
            Path symlinkPath = global::Ts.TsPath.TsPath.ToPath(commonOriginal, cwd, useCaseSensitiveFileNames);
            if (!global::Ts.TsPath.TsPath.ContainsIgnoredPath(symlinkPath.ToString()))
            {
                SetDirectory(
                    commonOriginal,
                    symlinkPath.EnsureTrailingDirectorySeparator(),
                    new KnownDirectoryLink
                    {
                        Real = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(commonResolved),
                        RealPath = global::Ts.TsPath.TsPath.ToPath(commonResolved, cwd, useCaseSensitiveFileNames).EnsureTrailingDirectorySeparator(),
                    });
            }
        }
    }

    internal (string, string) GuessDirectorySymlink(string a, string b, string cwd)
    {
        List<string> aParts = global::Ts.TsPath.TsPath.GetPathComponents(global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(a, cwd), "");
        List<string> bParts = global::Ts.TsPath.TsPath.GetPathComponents(global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(b, cwd), "");
        bool isDirectory = false;
        while (aParts.Count >= 2 && bParts.Count >= 2 &&
            !IsNodeModulesOrScopedPackageDirectory(aParts[aParts.Count - 2]) &&
            !IsNodeModulesOrScopedPackageDirectory(bParts[bParts.Count - 2]) &&
            global::Ts.TsPath.TsPath.GetCanonicalFileName(aParts[aParts.Count - 1], useCaseSensitiveFileNames) == global::Ts.TsPath.TsPath.GetCanonicalFileName(bParts[bParts.Count - 1], useCaseSensitiveFileNames))
        {
            aParts = aParts.GetRange(0, aParts.Count - 1);
            bParts = bParts.GetRange(0, bParts.Count - 1);
            isDirectory = true;
        }
        if (isDirectory)
        {
            return (global::Ts.TsPath.TsPath.GetPathFromPathComponents(aParts), global::Ts.TsPath.TsPath.GetPathFromPathComponents(bParts));
        }
        return ("", "");
    }

    internal bool IsNodeModulesOrScopedPackageDirectory(string s)
    {
        return s != "" && (global::Ts.TsPath.TsPath.GetCanonicalFileName(s, useCaseSensitiveFileNames) == "node_modules" || s.StartsWith('@'));
    }
}
