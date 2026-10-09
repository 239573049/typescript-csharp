// Ported from tsgo internal/compiler/host.go.
//
// §1.5 (namespace shadowing): sibling-namespace static classes (Parser, TsPath, Core, TsOptions)
// are written global::Ts.<Pkg>.<Class>; CoreUtil is not a namespace name.

using System;
using Ts.Ast;
using Ts.Vfs;

namespace Ts.Compiler;

// host.go:14-21 — the compiler host contract. Extends module.ResolutionHost (FS + GetCurrentDirectory)
// so it can be passed straight to module.NewResolver, matching Go where the loader hands opts.Host
// (a CompilerHost) to the resolver as a module.ResolutionHost.
public interface CompilerHost : global::Ts.Module.ResolutionHost
{
    // FS() and GetCurrentDirectory() are inherited from ResolutionHost.
    string DefaultLibraryPath();

    void Trace(global::Ts.Diagnostics.Message msg, params object?[] args);

    SourceFile? GetSourceFile(SourceFileParseOptions opts);

    global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(string fileName, global::Ts.TsPath.Path path);
}

// In Go, NewCompilerHost / NewCachedFSCompilerHost are package-level public functions
// (compiler.NewCompilerHost). The C# factory methods live on the internal CompilerHostImpl (so the
// in-assembly tests reach them), which leaves them unreachable from Ts.Cli. This public facade restores
// the package-level entry point for cross-assembly callers (the tsc CLI). Named CompilerHosts (plural)
// to avoid the §1.5 namespace-shadowing trap.
public static class CompilerHosts
{
    // host.go:33-41
    public static CompilerHost NewCachedFSCompilerHost(
        string currentDirectory,
        IFileSystem fs,
        string defaultLibraryPath,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        Action<global::Ts.Diagnostics.Message, object?[]>? trace) =>
        CompilerHostImpl.NewCachedFSCompilerHost(currentDirectory, fs, defaultLibraryPath, extendedConfigCache, trace);

    // host.go:43-60
    public static CompilerHost NewCompilerHost(
        string currentDirectory,
        IFileSystem fs,
        string defaultLibraryPath,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        Action<global::Ts.Diagnostics.Message, object?[]>? trace) =>
        CompilerHostImpl.NewCompilerHost(currentDirectory, fs, defaultLibraryPath, extendedConfigCache, trace);
}

// host.go:25-31
internal sealed class CompilerHostImpl : CompilerHost
{
    private readonly string currentDirectory;
    private readonly IFileSystem fs;
    private readonly string defaultLibraryPath;
    private readonly global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache;
    private readonly Action<global::Ts.Diagnostics.Message, object?[]> trace;

    private CompilerHostImpl(
        string currentDirectory,
        IFileSystem fs,
        string defaultLibraryPath,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        Action<global::Ts.Diagnostics.Message, object?[]>? trace)
    {
        this.currentDirectory = currentDirectory;
        this.fs = fs;
        this.defaultLibraryPath = defaultLibraryPath;
        this.extendedConfigCache = extendedConfigCache;
        this.trace = trace ?? ((_, _) => { });
    }

    // host.go:33-41
    public static CompilerHost NewCachedFSCompilerHost(
        string currentDirectory,
        IFileSystem fs,
        string defaultLibraryPath,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        Action<global::Ts.Diagnostics.Message, object?[]>? trace)
    {
        return NewCompilerHost(currentDirectory, global::Ts.Vfs.CachedVfs.CachedFs.From(fs), defaultLibraryPath, extendedConfigCache, trace);
    }

    // host.go:43-60
    public static CompilerHost NewCompilerHost(
        string currentDirectory,
        IFileSystem fs,
        string defaultLibraryPath,
        global::Ts.TsOptions.ExtendedConfigCache? extendedConfigCache,
        Action<global::Ts.Diagnostics.Message, object?[]>? trace)
    {
        return new CompilerHostImpl(currentDirectory, fs, defaultLibraryPath, extendedConfigCache, trace);
    }

    // host.go:62-64
    public IFileSystem FS() => fs;

    // host.go:66-68
    public string DefaultLibraryPath() => defaultLibraryPath;

    // host.go:70-72
    public string GetCurrentDirectory() => currentDirectory;

    // host.go:74-76
    public void Trace(global::Ts.Diagnostics.Message msg, params object?[] args) => trace(msg, args);

    // host.go:78-84
    public SourceFile? GetSourceFile(SourceFileParseOptions opts)
    {
        if (!FS().TryReadFile(opts.FileName, out var text))
        {
            return null;
        }
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, global::Ts.Core.CoreUtil.GetScriptKindFromFileName(opts.FileName));
    }

    // host.go:86-89 — the loader resolves every project reference config through the host, so this
    // must parse the referenced tsconfig (host.go:87 calls
    // tsoptions.GetParsedCommandLineOfConfigFilePath with this host as the ParseConfigHost; a nil
    // commandLine simply means "unresolvable reference", which surfaces as TS6053).
    public global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(string fileName, global::Ts.TsPath.Path path)
    {
        var (commandLine, _) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFilePath(
            fileName, path, null, null, new CompilerHostParseConfigHost(this), extendedConfigCache);
        return commandLine;
    }
}

// host.go:86-89 — the compilerHost doubles as the ParseConfigHost for referenced configs (FS +
// GetCurrentDirectory are the only members ParseConfigHost needs).
internal sealed class CompilerHostParseConfigHost : global::Ts.TsOptions.ParseConfigHost
{
    private readonly CompilerHostImpl host;

    internal CompilerHostParseConfigHost(CompilerHostImpl host)
    {
        this.host = host;
    }

    public IFileSystem FS() => host.FS();

    public string GetCurrentDirectory() => host.GetCurrentDirectory();
}
