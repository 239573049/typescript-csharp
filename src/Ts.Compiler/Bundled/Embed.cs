using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

using Ts.Vfs;

using FileMode = Ts.Vfs.FileMode;

namespace Ts.Bundled;

// Ported from tsgo internal/bundled/embed.go (+ bundled.go LibPath/WrapFS/IsBundled). Provides the
// `bundled:///` overlay filesystem that serves the embedded lib.*.d.ts files. In tsgo the contents
// come from //go:embed; here they come from the EmbeddedResource glob in Ts.Compiler.csproj
// (LogicalName "bundled/libs/<name>"), read on demand via GetManifestResourceStream.
public static partial class Bundled
{
    // embed.go:15
    private const string Scheme = "bundled:///";

    // bundled.go — Embedded is always true for the C# build (there is no noembed variant).
    public const bool Embedded = true;

    // embed.go:20 — libPath.
    public static string LibPath() => Scheme + "libs";

    // embed.go:17 — splitPath (strings.CutPrefix).
    private static bool SplitPath(string path, out string rest)
    {
        if (path.StartsWith(Scheme, StringComparison.Ordinal))
        {
            rest = path[Scheme.Length..];
            return true;
        }
        rest = path;
        return false;
    }

    // embed.go:24 — IsBundled.
    public static bool IsBundled(string path) => SplitPath(path, out _);

    // bundled.go:22 — WrapFS. Returns an FS which redirects embedded paths to the embedded file
    // system; other paths pass through to the wrapped FS.
    public static IFileSystem WrapFS(IFileSystem fs) => new WrappedFs(fs);

    // Go's //go:embed libs/*.d.ts, keyed by "libs/<name>". Built lazily from the assembly's
    // embedded resources. map-iter: 序无关 — keys read individually by path.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_embeddedContents = new(
        BuildEmbeddedContents,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyDictionary<string, string> BuildEmbeddedContents()
    {
        var assembly = typeof(Bundled).Assembly;
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string libName in LibNames)
        {
            using Stream? stream = assembly.GetManifestResourceStream("bundled/libs/" + libName);
            if (stream == null)
            {
                continue;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            contents["libs/" + libName] = reader.ReadToEnd();
        }
        return contents;
    }

    private static IReadOnlyDictionary<string, string> EmbeddedContents => s_embeddedContents.Value;

    // embed.go:35 — wrappedFS.
    private sealed class WrappedFs : IFileSystem
    {
        private readonly IFileSystem fs;

        public WrappedFs(IFileSystem fs) => this.fs = fs;

        // embed.go:45
        public bool UseCaseSensitiveFileNames() => fs.UseCaseSensitiveFileNames();

        // embed.go:49
        public bool FileExists(string path)
        {
            if (SplitPath(path, out string rest))
            {
                return EmbeddedContents.ContainsKey(rest);
            }
            return fs.FileExists(path);
        }

        // embed.go:57
        public bool TryReadFile(string path, out string contents)
        {
            if (SplitPath(path, out string rest))
            {
                return EmbeddedContents.TryGetValue(rest, out contents!);
            }
            return fs.TryReadFile(path, out contents);
        }

        // embed.go:66
        public bool DirectoryExists(string path)
        {
            if (SplitPath(path, out string rest))
            {
                return rest == "libs";
            }
            return fs.DirectoryExists(path);
        }

        // embed.go:73
        public Entries GetAccessibleEntries(string path)
        {
            if (SplitPath(path, out string rest))
            {
                if (rest == "")
                {
                    return new Entries { Directories = ["libs"] };
                }
                if (rest == "libs")
                {
                    return new Entries { Files = LibNames };
                }
                return default;
            }
            return fs.GetAccessibleEntries(path);
        }

        // embed.go:90
        public IFileInfo? Stat(string path)
        {
            if (SplitPath(path, out string rest))
            {
                if (rest == "" || rest == "libs")
                {
                    return new BundledFileInfo(rest, FileMode.Dir, 0);
                }
                if (EmbeddedContents.TryGetValue(rest, out string? lib))
                {
                    string libName = rest.StartsWith("libs/", StringComparison.Ordinal) ? rest["libs/".Length..] : rest;
                    return new BundledFileInfo(libName, FileMode.None, lib.Length);
                }
                return null;
            }
            return fs.Stat(path);
        }

        // embed.go:105
        public Exception? WalkDir(string root, WalkDirFunc walkFn)
        {
            if (SplitPath(root, out string rest))
            {
                Exception? err = WalkDirImpl(rest, walkFn);
                if (err != null)
                {
                    if (ReferenceEquals(err, VfsUtil.SkipAll))
                    {
                        return null;
                    }
                    return err;
                }
                return null;
            }
            return fs.WalkDir(root, walkFn);
        }

        // embed.go:118 — walkDir.
        private static Exception? WalkDirImpl(string rest, WalkDirFunc walkFn)
        {
            IReadOnlyList<BundledFileInfo> entries;
            switch (rest)
            {
                case "":
                    entries = s_rootEntries;
                    break;
                case "libs":
                    entries = LibsEntries;
                    break;
                default:
                    return null;
            }

            foreach (BundledFileInfo entry in entries)
            {
                string name = rest + "/" + entry.Name();

                Exception? err = walkFn(Scheme + name, entry, null);
                if (err != null)
                {
                    if (ReferenceEquals(err, VfsUtil.SkipAll))
                    {
                        return VfsUtil.SkipAll;
                    }
                    if (ReferenceEquals(err, VfsUtil.SkipDir))
                    {
                        continue;
                    }
                    return err;
                }
                if (entry.IsDir())
                {
                    err = WalkDirImpl(name.StartsWith("/", StringComparison.Ordinal) ? name[1..] : name, walkFn);
                    if (err != null)
                    {
                        return err;
                    }
                }
            }

            return null;
        }

        // embed.go:148
        public string Realpath(string path)
        {
            if (SplitPath(path, out _))
            {
                return path;
            }
            return fs.Realpath(path);
        }

        // embed.go:156
        public void WriteFile(string path, string data)
        {
            if (SplitPath(path, out _))
            {
                throw new InvalidOperationException("cannot write to embedded file system");
            }
            fs.WriteFile(path, data);
        }

        // embed.go:163
        public void AppendFile(string path, string data)
        {
            if (SplitPath(path, out _))
            {
                throw new InvalidOperationException("cannot write to embedded file system");
            }
            fs.AppendFile(path, data);
        }

        // embed.go:170
        public void Remove(string path)
        {
            if (SplitPath(path, out _))
            {
                throw new InvalidOperationException("cannot remove from embedded file system");
            }
            fs.Remove(path);
        }

        // embed.go:177
        public void Chtimes(string path, DateTime aTime, DateTime mTime)
        {
            if (SplitPath(path, out _))
            {
                throw new InvalidOperationException("cannot change times on embedded file system");
            }
            fs.Chtimes(path, aTime, mTime);
        }
    }

    // embed.go:110 — rootEntries.
    private static readonly BundledFileInfo[] s_rootEntries = [new BundledFileInfo("libs", FileMode.Dir, 0)];

    // embed.go — libsEntries (one dir entry per bundled lib).
    private static readonly Lazy<IReadOnlyList<BundledFileInfo>> s_libsEntries = new(
        BuildLibsEntries,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyList<BundledFileInfo> BuildLibsEntries()
    {
        var list = new List<BundledFileInfo>(LibNames.Length);
        foreach (string libName in LibNames)
        {
            list.Add(new BundledFileInfo(libName, FileMode.None, 0));
        }
        return list;
    }

    private static IReadOnlyList<BundledFileInfo> LibsEntries => s_libsEntries.Value;

    // embed.go:185 — fileInfo (implements both fs.FileInfo and fs.DirEntry).
    private sealed class BundledFileInfo : IFileInfo, IDirEntry
    {
        private readonly string name;
        private readonly FileMode mode;
        private readonly long size;

        public BundledFileInfo(string name, FileMode mode, long size)
        {
            this.name = name;
            this.mode = mode;
            this.size = size;
        }

        public bool IsDir() => mode.IsDir();

        public DateTime ModTime() => default;

        public FileMode Mode() => mode;

        public string Name() => name;

        public long Size() => size;

        public object? Sys() => null;

        public IFileInfo? Info() => this;

        public FileMode Type() => mode.Type();
    }
}
