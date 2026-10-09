// Ported from tsgo internal/project/autoimport.go (whole file).

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project;

// autoimport.go:15-18 — autoImportBuilderFS.
internal sealed class AutoImportBuilderFS : FileSource
{
    internal readonly SnapshotFSBuilder snapshotFSBuilder;
    internal readonly global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, FileHandle?> untrackedFiles = new();

    internal AutoImportBuilderFS(SnapshotFSBuilder snapshotFSBuilder)
    {
        this.snapshotFSBuilder = snapshotFSBuilder;
    }

    // autoimport.go:22-25 — FS implements FileSource.
    public global::Ts.Vfs.IFileSystem FS() => snapshotFSBuilder.fs;

    // autoimport.go:27-31 — GetFile implements FileSource.
    public FileHandle? GetFile(string fileName)
    {
        global::Ts.TsPath.Path path = snapshotFSBuilder.toPath(fileName);
        return GetFileByPath(fileName, path);
    }

    // autoimport.go:33-56 — GetFileByPath implements FileSource.
    // We want to avoid long-term caching of files referenced only by auto-imports, so we
    // override GetFileByPath to avoid collecting more files into the snapshotFSBuilder's
    // diskFiles. (Note the reason we can't just use the finalized SnapshotFS is that changed
    // files not read during other parts of the snapshot clone will be marked as dirty, but
    // not yet refreshed from disk.)
    public FileHandle? GetFileByPath(string fileName, global::Ts.TsPath.Path path)
    {
        if (snapshotFSBuilder.overlays.TryGetValue(path, out Overlay? overlay))
        {
            return overlay;
        }
        (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? diskFile, bool ok) = snapshotFSBuilder.diskFiles.Load(path);
        if (ok)
        {
            return snapshotFSBuilder.ReloadEntryIfNeeded(diskFile!);
        }
        if (untrackedFiles.TryLoad(path, out FileHandle? cachedFh))
        {
            return cachedFh;
        }
        FileHandle? fh = null;
        if (snapshotFSBuilder.fs.TryReadFile(fileName, out string content))
        {
            fh = DiskFile.NewDiskFile(fileName, content);
        }
        (FileHandle? actualFh, bool _) = untrackedFiles.LoadOrStore(path, fh);
        return actualFh;
    }

    // autoimport.go:58-60 — GetAccessibleEntries implements FileSource.
    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        return snapshotFSBuilder.GetAccessibleEntries(path);
    }

    // autoimport.go:62-65 — FileExists implements FileSource.
    public bool FileExists(string fileName, global::Ts.TsPath.Path path)
    {
        return snapshotFSBuilder.FileExists(fileName, path);
    }
}

// autoimport.go:67-75 — autoImportRegistryCloneHost.
internal sealed class AutoImportRegistryCloneHost : global::Ts.Ls.AutoImport.RegistryCloneHost
{
    internal readonly ProjectCollection projectCollection;
    internal readonly RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache;
    internal readonly SourceFS fs;
    internal readonly string currentDirectory;

    private readonly Lock filesMu = new();
    private readonly List<ParseCacheKey> files = [];

    // autoimport.go:79-92 — newAutoImportRegistryCloneHost.
    internal static AutoImportRegistryCloneHost NewAutoImportRegistryCloneHost(
        ProjectCollection projectCollection,
        RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache,
        SnapshotFSBuilder snapshotFSBuilder,
        string currentDirectory,
        Func<string, global::Ts.TsPath.Path> toPath)
    {
        return new AutoImportRegistryCloneHost(projectCollection, parseCache, SourceFS.NewSourceFS(false, new AutoImportBuilderFS(snapshotFSBuilder), toPath), currentDirectory);
    }

    private AutoImportRegistryCloneHost(ProjectCollection projectCollection, RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache, SourceFS fs, string currentDirectory)
    {
        this.projectCollection = projectCollection;
        this.parseCache = parseCache;
        this.fs = fs;
        this.currentDirectory = currentDirectory;
    }

    // autoimport.go:94-97 — FS implements autoimport.RegistryCloneHost.
    public global::Ts.Vfs.IFileSystem FS() => fs;

    // autoimport.go:99-102 — GetCurrentDirectory implements autoimport.RegistryCloneHost.
    public string GetCurrentDirectory() => currentDirectory;

    // autoimport.go:104-111 — GetDefaultProject implements autoimport.RegistryCloneHost.
    public (global::Ts.TsPath.Path Path, global::Ts.Compiler.Program? Program) GetDefaultProject(global::Ts.TsPath.Path path)
    {
        Project? project = projectCollection.GetDefaultProject(path);
        if (project is null)
        {
            return (default, null);
        }
        return (project.configFilePath, project.GetProgram());
    }

    // autoimport.go:113-142 — GetPackageJson implements autoimport.RegistryCloneHost.
    public global::Ts.PackageJson.InfoCacheEntry GetPackageJson(string fileName)
    {
        // !!! ref-counted shared cache
        FileHandle? fh = fs.GetFile(fileName);
        string packageDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName);
        if (fh is null)
        {
            return new global::Ts.PackageJson.InfoCacheEntry
            {
                DirectoryExists = fs.DirectoryExists(packageDirectory),
                PackageDirectory = packageDirectory,
            };
        }
        (global::Ts.PackageJson.Fields fields, bool ok) = global::Ts.PackageJson.PackageJsonParser.Parse(System.Text.Encoding.UTF8.GetBytes(fh.Content()));
        if (!ok)
        {
            return new global::Ts.PackageJson.InfoCacheEntry
            {
                DirectoryExists = true,
                PackageDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName),
                Contents = new global::Ts.PackageJson.PackageJson
                {
                    Parseable = false,
                },
            };
        }
        return new global::Ts.PackageJson.InfoCacheEntry
        {
            DirectoryExists = true,
            PackageDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName),
            Contents = new global::Ts.PackageJson.PackageJson
            {
                Fields = fields,
                Parseable = true,
            },
        };
    }

    // autoimport.go:144-151 — GetProgramForProject implements autoimport.RegistryCloneHost.
    public global::Ts.Compiler.Program? GetProgramForProject(global::Ts.TsPath.Path projectPath)
    {
        Project? project = projectCollection.GetProjectByPath(projectPath);
        if (project is null)
        {
            return null;
        }
        return project.GetProgram();
    }

    // autoimport.go:153-171 — GetSourceFile implements autoimport.RegistryCloneHost.
    public global::Ts.Ast.SourceFile? GetSourceFile(string fileName, global::Ts.TsPath.Path path)
    {
        FileHandle? fh = fs.GetFile(fileName);
        if (fh is null)
        {
            return null;
        }
        var opts = new global::Ts.Ast.SourceFileParseOptions
        {
            FileName = fileName,
            Path = path,
        };
        ParseCacheKey key = ParseCache.NewParseCacheKey(opts, fh.Hash(), fh.Kind());
        global::Ts.Ast.SourceFile result = parseCache.Acquire(key, fh);

        lock (filesMu)
        {
            files.Add(key);
        }

        return result;
    }

    // autoimport.go:173-180 — Dispose implements autoimport.RegistryCloneHost.
    public void Dispose()
    {
        lock (filesMu)
        {
            foreach (ParseCacheKey key in files)
            {
                parseCache.Deref(key);
            }
        }
    }
}
