// Ported from tsgo internal/execute/build/host.go and compilerHost.go.

using System;
using Ts.Collections;

namespace Ts.Execute.Build;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// host.go:17-29
internal sealed class BuildHost : global::Ts.Compiler.CompilerHost, global::Ts.TsOptions.ParseConfigHost, global::Ts.Execute.Incremental.IBuildInfoReader, global::Ts.Execute.Incremental.IHost
{
    internal Orchestrator orchestrator = null!;
    internal global::Ts.Compiler.CompilerHost host = null!;

    // Caches that last only for build cycle and then cleared out
    internal TscExtendedConfigCache extendedConfigCache = new();
    internal readonly ParseCache<global::Ts.Ast.SourceFileParseOptions, global::Ts.Ast.SourceFile> sourceFiles = new();
    internal SyncMap<PathT, TimeSpan> configTimes = new();

    // caches that stay as long as they are needed
    internal readonly ParseCache<PathT, global::Ts.TsOptions.ParsedCommandLine> resolvedReferences = new();
    internal SyncMap<PathT, DateTime> mTimes = new();

    // host.go:37-39
    public global::Ts.Vfs.IFileSystem FS() => host.FS();

    // host.go:41-43
    public string DefaultLibraryPath() => host.DefaultLibraryPath();

    // host.go:45-47
    public string GetCurrentDirectory() => host.GetCurrentDirectory();

    // host.go:49-51
    public void Trace(global::Ts.Diagnostics.Message msg, params object?[] args) =>
        throw new InvalidOperationException("build.Orchestrator.host does not support tracing, use a different host for tracing");

    // host.go:53-59
    public global::Ts.Ast.SourceFile? GetSourceFile(global::Ts.Ast.SourceFileParseOptions opts)
    {
        if (TsPathApi.IsDeclarationFileName(opts.FileName) || TsPathApi.FileExtensionIs(opts.FileName, TsPathApi.ExtensionJson))
        {
            // Cache dts and json files as they will be reused
            return sourceFiles.LoadOrStore(opts, host.GetSourceFile, false /* allowZero */);
        }
        return host.GetSourceFile(opts);
    }

    // host.go:61-77
    public global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(string fileName, PathT path)
    {
        return resolvedReferences.LoadOrStore(path, p =>
        {
            var configStart = orchestrator.opts.Sys.Now();
            // Wrap command line options in "compilerOptions" key to match tsconfig.json structure
            global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw = null;
            if (orchestrator.opts.Command.Raw is global::Ts.Collections.OrderedMap<string, object?> raw)
            {
                var wrapped = new global::Ts.Collections.OrderedMap<string, object?>();
                wrapped.Set("compilerOptions", raw);
                commandLineRaw = wrapped;
            }
            var (commandLine, _) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFilePath(
                fileName, p, orchestrator.opts.Command.CompilerOptions, commandLineRaw, this, extendedConfigCache);
            var configTime = orchestrator.opts.Sys.Now() - configStart;
            configTimes.Store(p, configTime);
            return commandLine;
        }, true /* allowZero */);
    }

    // host.go:79-84
    public global::Ts.Execute.Incremental.BuildInfo? ReadBuildInfo(global::Ts.TsOptions.ParsedCommandLine config)
    {
        var configPath = orchestrator.ToPath(config.ConfigName());
        var task = orchestrator.GetTask(configPath);
        var (buildInfo, _) = task.LoadOrStoreBuildInfo(orchestrator, orchestrator.ToPath(config.ConfigName()), config.GetBuildInfoFileName());
        return buildInfo;
    }

    // host.go:86-88
    public DateTime GetMTime(string file) => LoadOrStoreMTime(file, null, true);

    // host.go:90-92
    public void SetMTime(string file, DateTime mTime) => FS().Chtimes(file, default, mTime);

    // host.go:94-111
    internal DateTime LoadOrStoreMTime(string file, SyncMap<PathT, DateTime>? oldCache, bool store)
    {
        var path = orchestrator.ToPath(file);
        if (mTimes.TryLoad(path, out var existing))
        {
            return existing;
        }
        var found = false;
        var mTime = default(DateTime);
        if (oldCache != null)
        {
            found = oldCache.TryLoad(path, out mTime);
        }
        if (!found)
        {
            mTime = global::Ts.Execute.Incremental.HostHelpers.GetMTime(host, file);
        }
        if (store)
        {
            (mTime, _) = mTimes.LoadOrStore(path, mTime);
        }
        return mTime;
    }

    // host.go:113-116
    internal void StoreMTime(string file, DateTime mTime)
    {
        var path = orchestrator.ToPath(file);
        mTimes.Store(path, mTime);
    }

    // host.go:118-123
    internal void StoreMTimeFromOldCache(string file, SyncMap<PathT, DateTime> oldCache)
    {
        var path = orchestrator.ToPath(file);
        if (oldCache.TryLoad(path, out var mTime))
        {
            mTimes.Store(path, mTime);
        }
    }
}

// compilerHost.go:12-15
internal sealed class BuildCompilerHost : global::Ts.Compiler.CompilerHost
{
    internal BuildHost host = null!;
    internal Action<global::Ts.Diagnostics.Message, object?[]> trace = null!;

    // compilerHost.go:19-21
    public global::Ts.Vfs.IFileSystem FS() => host.FS();

    // compilerHost.go:23-25
    public string DefaultLibraryPath() => host.DefaultLibraryPath();

    // compilerHost.go:27-29
    public string GetCurrentDirectory() => host.GetCurrentDirectory();

    // compilerHost.go:31-33
    public void Trace(global::Ts.Diagnostics.Message msg, params object?[] args) => trace(msg, args);

    // compilerHost.go:35-37
    public global::Ts.Ast.SourceFile? GetSourceFile(global::Ts.Ast.SourceFileParseOptions opts) => host.GetSourceFile(opts);

    // compilerHost.go:39-41
    public global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(string fileName, PathT path) => host.GetResolvedProjectReference(fileName, path);
}
