// Ported from tsgo internal/ls/autoimport/aliasresolver.go (whole file): a minimal
// checker.Program implementation that lets a checker resolve aliases across the registry's
// lazily-parsed node_modules files; plus the RegistryCloneHost interface it consumes
// (registry.go:495-503 — the rest of registry.go is the registry batch).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using TsPathApi = Ts.TsPath.TsPath;
using ResolutionMode = Ts.Core.ModuleKind;

namespace Ts.Ls.AutoImport;

// registry.go:495-503 — RegistryCloneHost.
public interface RegistryCloneHost : global::Ts.Module.ResolutionHost
{
    (global::Ts.TsPath.Path Path, global::Ts.Compiler.Program? Program) GetDefaultProject(global::Ts.TsPath.Path path);

    global::Ts.Compiler.Program? GetProgramForProject(global::Ts.TsPath.Path projectPath);

    global::Ts.PackageJson.InfoCacheEntry? GetPackageJson(string fileName);

    SourceFile? GetSourceFile(string fileName, global::Ts.TsPath.Path path);

    void Dispose();
}

// aliasresolver.go:16-19 — pathAndFileName.
internal readonly record struct PathAndFileName(global::Ts.TsPath.Path Path, string FileName);

// aliasresolver.go:21-31 — aliasResolver.
internal sealed class AliasResolver : global::Ts.Checker.Program
{
    internal Func<string, global::Ts.TsPath.Path> toPath = null!;
    internal RegistryCloneHost host = null!;
    internal global::Ts.Module.Resolver moduleResolver = null!;

    internal SourceFile[] rootFiles = [];
    // symlinks maps from realpath to symlinked path and file name
    internal Dictionary<global::Ts.TsPath.Path, PathAndFileName>? symlinks;
    internal Action<HasFileName, string>? onFailedAmbientModuleLookup;
    internal readonly global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, global::Ts.Collections.SyncMap<global::Ts.Module.ModeAwareCacheKey, global::Ts.Module.ResolvedModule?>> resolvedModules = new();

    // aliasresolver.go:33-50 — newAliasResolver.
    internal static AliasResolver NewAliasResolver(
        SourceFile[] rootFiles,
        Dictionary<global::Ts.TsPath.Path, PathAndFileName>? symlinks,
        RegistryCloneHost host,
        global::Ts.Module.Resolver moduleResolver,
        Func<string, global::Ts.TsPath.Path> toPath,
        Action<HasFileName, string>? onFailedAmbientModuleLookup)
    {
        return new AliasResolver
        {
            toPath = toPath,
            host = host,
            moduleResolver = moduleResolver,
            rootFiles = rootFiles,
            symlinks = symlinks,
            onFailedAmbientModuleLookup = onFailedAmbientModuleLookup,
        };
    }

    // aliasresolver.go:52-55 — BindSourceFiles implements checker.Program.
    public void BindSourceFiles()
    {
        // We will bind as we parse
    }

    // aliasresolver.go:57-60 — SourceFiles implements checker.Program.
    public SourceFile[] SourceFiles() => rootFiles;

    // aliasresolver.go:62-67 — Options implements checker.Program.
    public CompilerOptions Options() => new()
    {
        NoCheck = Tristate.True,
    };

    // aliasresolver.go:69-72 — GetCurrentDirectory implements checker.Program.
    public string GetCurrentDirectory() => host.GetCurrentDirectory();

    // aliasresolver.go:74-77 — UseCaseSensitiveFileNames implements checker.Program.
    public bool UseCaseSensitiveFileNames() => host.FS().UseCaseSensitiveFileNames();

    // aliasresolver.go:79-88 — GetSourceFile implements checker.Program.
    public SourceFile? GetSourceFile(string fileName)
    {
        var file = host.GetSourceFile(fileName, toPath(fileName));
        // file may be nil due to symlink/realpath mismatch; see TestAutoImportBuilderFS
        if (file == null)
        {
            return null;
        }
        global::Ts.Binder.Binder.BindSourceFile(file);
        return file;
    }

    // aliasresolver.go:90-93 — GetDefaultResolutionModeForFile implements checker.Program.
    public ResolutionMode GetDefaultResolutionModeForFile(HasFileName file) => ModuleKind.ESNext;

    // aliasresolver.go:95-98 — GetEmitModuleFormatOfFile implements checker.Program.
    public ModuleKind GetEmitModuleFormatOfFile(HasFileName sourceFile) => ModuleKind.ESNext;

    // aliasresolver.go:100-103 — GetEmitSyntaxForUsageLocation implements checker.Program.
    public ResolutionMode GetEmitSyntaxForUsageLocation(HasFileName sourceFile, StringLiteralLikeNode? usageLocation) => ModuleKind.ESNext;

    // aliasresolver.go:105-108 — GetImpliedNodeFormatForEmit implements checker.Program.
    public ModuleKind GetImpliedNodeFormatForEmit(HasFileName sourceFile) => ModuleKind.ESNext;

    // aliasresolver.go:110-113 — GetModeForUsageLocation implements checker.Program.
    public ResolutionMode GetModeForUsageLocation(HasFileName file, StringLiteralLikeNode? moduleSpecifier) => ModuleKind.ESNext;

    // aliasresolver.go:115-127 — GetResolvedModule implements checker.Program.
    public global::Ts.Module.ResolvedModule? GetResolvedModule(HasFileName currentSourceFile, string moduleReference, ResolutionMode mode)
    {
        var (cache, _) = resolvedModules.LoadOrStore(currentSourceFile.Path(), new global::Ts.Collections.SyncMap<global::Ts.Module.ModeAwareCacheKey, global::Ts.Module.ResolvedModule?>());
        if (cache.TryLoad(new global::Ts.Module.ModeAwareCacheKey(moduleReference, mode), out var cached))
        {
            return cached;
        }
        var (resolved, _) = moduleResolver.ResolveModuleName(moduleReference, currentSourceFile.FileName(), mode, null);
        (resolved, _) = cache.LoadOrStore(new global::Ts.Module.ModeAwareCacheKey(moduleReference, mode), resolved);
        if (!global::Ts.Module.ResolvedModuleExtensions.IsResolved(resolved) && !TsPathApi.PathIsRelative(moduleReference))
        {
            onFailedAmbientModuleLookup?.Invoke(currentSourceFile, moduleReference);
        }
        return resolved;
    }

    // aliasresolver.go:129-132 — GetSourceFileForResolvedModule implements checker.Program.
    public SourceFile? GetSourceFileForResolvedModule(string fileName) => GetSourceFile(fileName);

    // aliasresolver.go:134-138 — GetResolvedModules implements checker.Program.
    // only used when producing diagnostics, which hopefully the checker won't do
    public Dictionary<global::Ts.TsPath.Path, global::Ts.Module.ModeAwareCache<global::Ts.Module.ResolvedModule>> GetResolvedModules() => null!;

    // ---

    // aliasresolver.go:142-145 — GetSymlinkCache implements checker.Program.
    public global::Ts.Symlinks.KnownSymlinks GetSymlinkCache() => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:147-150 — GetSourceFileMetaData implements checker.Program.
    public SourceFileMetaData GetSourceFileMetaData(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:152-155 — CommonSourceDirectory implements checker.Program.
    public string CommonSourceDirectory() => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:157-160 — FileExists implements checker.Program.
    public bool FileExists(string fileName) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:162-165 — GetGlobalTypingsCacheLocation implements checker.Program.
    public string GetGlobalTypingsCacheLocation() => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:167-170 — GetImportHelpersImportSpecifier implements checker.Program.
    public Node? GetImportHelpersImportSpecifier(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:172-175 — GetJSXRuntimeImportSpecifier implements checker.Program.
    public (string ModuleReference, Node? Specifier) GetJSXRuntimeImportSpecifier(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:177-180 — GetNearestAncestorDirectoryWithPackageJson implements checker.Program.
    public string GetNearestAncestorDirectoryWithPackageJson(string dirname) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:182-185 — GetPackageJsonInfo implements checker.Program.
    public global::Ts.PackageJson.InfoCacheEntry? GetPackageJsonInfo(string pkgJsonPath) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:187-190 — GetProjectReferenceFromOutputDts implements checker.Program.
    public global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromOutputDts(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:192-195 — GetProjectReferenceFromSource implements checker.Program.
    public global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:197-200 — GetRedirectForResolution implements checker.Program.
    public global::Ts.TsOptions.ParsedCommandLine? GetRedirectForResolution(HasFileName file) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:202-205 — GetRedirectTargets implements checker.Program.
    public string[] GetRedirectTargets(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:207-210 — GetResolvedModuleFromModuleSpecifier implements checker.Program.
    public global::Ts.Module.ResolvedModule? GetResolvedModuleFromModuleSpecifier(HasFileName file, StringLiteralLikeNode? moduleSpecifier) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:212-215 — GetSourceOfProjectReferenceIfOutputIncluded implements checker.Program.
    public string GetSourceOfProjectReferenceIfOutputIncluded(HasFileName file) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:217-220 — IsSourceFileDefaultLibrary implements checker.Program.
    public bool IsSourceFileDefaultLibrary(global::Ts.TsPath.Path path) => false;

    // aliasresolver.go:222-225 — IsSourceFromProjectReference implements checker.Program.
    public bool IsSourceFromProjectReference(global::Ts.TsPath.Path path) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:227-230 — SourceFileMayBeEmitted implements checker.Program.
    public bool SourceFileMayBeEmitted(SourceFile sourceFile, bool forceDtsEmit) => throw new NotImplementedException("unimplemented");

    // aliasresolver.go:232-234 — GetPackagesMap.
    public Dictionary<string, bool> GetPackagesMap() => null!;
}
