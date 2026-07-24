// Ported from tsgo internal/ls/autoimport/util.go — the registry-scoped tail
// (getResolvedPackageNames :145-177, addProjectReferenceOutputMappings :179-197,
// createCheckerPool :199-232, getPackageRealpathFuncs :249-297, resolutionHost :299-315,
// getModuleResolver :317-325). Split from Util.cs to keep the registry-independent half's
// file header accurate.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;

namespace Ts.Ls.AutoImport;

public static partial class Util
{
    // util.go:145-177 — getResolvedPackageNames.
    internal static global::Ts.Collections.Set<string> GetResolvedPackageNames(global::Ts.Compiler.Program program)
    {
        var rawNames = program.ResolvedPackageNames();
        var unresolvedPackageNames = program.UnresolvedPackageNames();

        // Normalize @types/ package names to their actual package names
        // (e.g., "@types/react" → "react"). ResolvedPackageNames can contain
        // @types names when the program resolves an import like "react" to
        // "@types/react/index.d.ts" via the PackageId.Name field.
        var resolvedPackageNames = new global::Ts.Collections.Set<string>(rawNames.Len()); // NewSetWithSizeHint
        foreach (var name in rawNames.Keys())
        {
            resolvedPackageNames.Add(global::Ts.Module.Module.GetPackageNameFromTypesPackageName(name));
        }

        foreach (var name in program.Options().Types ?? [])
        {
            if (name != "*")
            {
                resolvedPackageNames.Add(global::Ts.Module.Module.GetPackageNameFromTypesPackageName(name));
            }
        }

        if (unresolvedPackageNames.Len() > 0)
        {
            var (checker, done) = program.GetTypeChecker(default);
            try
            {
                foreach (var name in unresolvedPackageNames.Keys())
                {
                    var symbol = checker.TryFindAmbientModule(name);
                    if (symbol != null)
                    {
                        var declaringFile = AstUtilities.GetSourceFileOfModule(symbol);
                        var packageName = global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.GetPackageNameFromDirectory(declaringFile!.FileName());
                        if (packageName.Length != 0)
                        {
                            resolvedPackageNames.Add(global::Ts.Module.Module.GetPackageNameFromTypesPackageName(packageName));
                        }
                    }
                }
            }
            finally
            {
                done();
            }
        }
        return resolvedPackageNames;
    }

    // util.go:179-197 — addProjectReferenceOutputMappings: adds output .d.ts to source file
    // mappings from a program's project references to the provided map.
    // This is used during node_modules bucket building to redirect extraction
    // from output files to source files when the output is from a project reference.
    internal static void AddProjectReferenceOutputMappings(global::Ts.Compiler.Program program, Dictionary<global::Ts.TsPath.Path, string> result)
    {
        var refs = program.GetResolvedProjectReferences() ?? [];
        foreach (var reference in refs)
        {
            if (reference == null)
            {
                continue;
            }
            reference.ParseInputOutputNames();
            // map-iter: 序无关 (first-wins 语义仅在跨 program 重复键时生效, 同 program 内键唯一)
            foreach (var (outputDtsPath, mapping) in reference.OutputDtsToProjectReference())
            {
                // Only add if not already present (first program wins)
                if (!result.ContainsKey(outputDtsPath))
                {
                    result[outputDtsPath] = mapping.Source;
                }
            }
        }
    }

    // util.go:199-232 — createCheckerPool. (Go's buffered channel becomes a Monitor-guarded
    // stack: pop an idle checker, create while under the GOMAXPROCS limit, else block until
    // one is returned.)
    internal static (Func<(global::Ts.Checker.Checker Checker, Action Done)> GetChecker, Action ClosePool, Func<int> GetCreatedCount) CreateCheckerPool(global::Ts.Checker.Program program)
    {
        var maxSize = Environment.ProcessorCount;
        var pool = new System.Collections.Generic.Stack<global::Ts.Checker.Checker>();
        var created = 0;
        var mu = new object();

        (global::Ts.Checker.Checker, Action) GetChecker()
        {
            lock (mu)
            {
                while (true)
                {
                    // Try to get an existing checker
                    if (pool.Count > 0)
                    {
                        var ch = pool.Pop();
                        return (ch, () =>
                        {
                            lock (mu)
                            {
                                pool.Push(ch);
                                Monitor.Pulse(mu);
                            }
                        });
                    }
                    // Try to create a new one if under limit
                    if (created < maxSize)
                    {
                        created++;
                        var ch = global::Ts.Checker.Checker.NewChecker(program, null).Checker;
                        return (ch, () =>
                        {
                            lock (mu)
                            {
                                pool.Push(ch);
                                Monitor.Pulse(mu);
                            }
                        });
                    }
                    // At limit, wait for one to become available
                    Monitor.Wait(mu);
                }
            }
        }

        return (GetChecker, () => { /* close(pool) */ }, () => created);
    }

    // util.go:249-297 — getPackageRealpathFuncs: returns functions to transform between symlink
    // and realpath for files within a package.
    // It calls FS.Realpath once per package directory and uses prefix substitution for files
    // within that directory, avoiding expensive realpath syscalls for each file. For files
    // outside the package (e.g. re-exported dependencies reached through node_modules
    // symlinks), it resolves the file's directory realpath once, finds the symlink boundary
    // (the package root where the symlink lives), and caches that prefix mapping.
    // All subsequent files under the same symlinked package directory use prefix substitution
    // with no syscalls.
    internal static (Func<string, string> ToRealpath, Func<string, string> ToSymlink) GetPackageRealpathFuncs(global::Ts.Vfs.IFileSystem fs, string packageDir)
    {
        var realPackageDir = fs.Realpath(packageDir);
        var isSymlinked = realPackageDir != packageDir;
        // Cache of package-directory-level symlink→realpath prefix mappings for
        // external packages encountered via re-exports. Keyed by the node_modules
        // package directory (e.g. "/app/node_modules/dep"), so all files under
        // that package reuse a single realpath lookup.
        var dirCache = new Dictionary<string, string>(StringComparer.Ordinal);
        Func<string, string> toRealpath = fileName =>
        {
            // Fast path: files within the package use prefix substitution.
            if (isSymlinked)
            {
                if (fileName.StartsWith(packageDir, StringComparison.Ordinal))
                {
                    return realPackageDir + fileName[packageDir.Length..];
                }
            }
            // Files outside the package (e.g. re-exports into symlinked deps):
            // find the node_modules package directory, resolve it once, and cache.
            var pkgDir = global::Ts.Module.Module.ParseNodeModuleFromPath(fileName, false /*isFolder*/);
            if (pkgDir.Length == 0)
            {
                return fileName;
            }
            if (dirCache.TryGetValue(pkgDir, out var cachedRealDir))
            {
                if (cachedRealDir == pkgDir)
                {
                    return fileName;
                }
                return cachedRealDir + fileName[pkgDir.Length..];
            }
            var realDir = fs.Realpath(pkgDir);
            dirCache[pkgDir] = realDir;
            if (realDir == pkgDir)
            {
                return fileName;
            }
            return realDir + fileName[pkgDir.Length..];
        };
        if (!isSymlinked)
        {
            return (toRealpath, static s => s); // core.Identity
        }
        // toSymlink only handles files within the package directory (reversing the
        // packageDir→realPackageDir substitution). It does not handle arbitrary external
        // paths; callers should only use it for files known to be within the package.
        Func<string, string> toSymlink = fileName =>
        {
            if (fileName.StartsWith(realPackageDir, StringComparison.Ordinal))
            {
                return packageDir + fileName[realPackageDir.Length..];
            }
            return fileName;
        };
        return (toRealpath, toSymlink);
    }
}

// util.go:299-315 — resolutionHost.
internal sealed class ResolutionHostImpl : global::Ts.Module.ResolutionHost
{
    internal global::Ts.Vfs.IFileSystem fs = null!;
    internal string currentDirectory = "";

    public string GetCurrentDirectory()
    {
        return currentDirectory;
    }

    public global::Ts.Vfs.IFileSystem FS()
    {
        return fs;
    }
}

public static partial class Util
{
    // util.go:317-325 — getModuleResolver.
    internal static global::Ts.Module.Resolver GetModuleResolver(RegistryCloneHost host, Func<string, string> realpath, global::Ts.Module.ResolverOptions opts)
    {
        var rh = new ResolutionHostImpl
        {
            fs = global::Ts.Vfs.WrapVfs.WrapVfsUtil.Wrap(host.FS(), new global::Ts.Vfs.WrapVfs.Replacements { Realpath = realpath }),
            currentDirectory = host.GetCurrentDirectory(),
        };
        return new global::Ts.Module.Resolver(rh, global::Ts.Core.Core.EmptyCompilerOptions, "", "", opts);
    }
}
