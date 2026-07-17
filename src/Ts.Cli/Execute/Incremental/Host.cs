// Ported from tsgo internal/execute/incremental/host.go and incremental.go (BuildInfoReader).

using System;
using Ts.Vfs;

namespace Ts.Execute.Incremental;

// host.go:10-14
public interface IHost
{
    IFileSystem FS();
    DateTime GetMTime(string fileName);
    void SetMTime(string fileName, DateTime mTime);
}

// host.go:16-35
internal sealed class Host : IHost
{
    private readonly global::Ts.Compiler.CompilerHost host;

    internal Host(global::Ts.Compiler.CompilerHost host) => this.host = host;

    public IFileSystem FS() => host.FS();

    public DateTime GetMTime(string fileName) => HostHelpers.GetMTime(host, fileName);

    public void SetMTime(string fileName, DateTime mTime) => host.FS().Chtimes(fileName, default, mTime);
}

public static class HostHelpers
{
    // host.go:33-35
    public static IHost CreateHost(global::Ts.Compiler.CompilerHost compilerHost) => new Host(compilerHost);

    // host.go:37-45
    public static DateTime GetMTime(global::Ts.Compiler.CompilerHost host, string fileName)
    {
        var stat = host.FS().Stat(fileName);
        var mTime = default(DateTime);
        if (stat != null)
        {
            mTime = stat.ModTime();
        }
        return mTime;
    }
}

// incremental.go:9-41 — BuildInfoReader.
public interface IBuildInfoReader
{
    BuildInfo? ReadBuildInfo(global::Ts.TsOptions.ParsedCommandLine config);
}

internal sealed class BuildInfoReader : IBuildInfoReader
{
    private readonly global::Ts.Compiler.CompilerHost host;

    internal BuildInfoReader(global::Ts.Compiler.CompilerHost host) => this.host = host;

    // incremental.go:19-36
    public BuildInfo? ReadBuildInfo(global::Ts.TsOptions.ParsedCommandLine config)
    {
        var buildInfoFileName = config.GetBuildInfoFileName();
        if (buildInfoFileName.Length == 0)
        {
            return null;
        }

        // Read build info file
        if (!host.FS().TryReadFile(buildInfoFileName, out var data))
        {
            return null;
        }
        return BuildInfo.Unmarshal(data);
    }
}

public static class BuildInfoReaderHelpers
{
    // incremental.go:38-42
    public static IBuildInfoReader NewBuildInfoReader(global::Ts.Compiler.CompilerHost host) => new BuildInfoReader(host);
}
