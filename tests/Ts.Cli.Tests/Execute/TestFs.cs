// Ported from tsgo internal/execute/tsctests/fs.go — the test FS wrapper that swaps the real
// compiler version with harnessutil.FakeTSVersion in .tsbuildinfo files (both directions) and
// writes the readable sidecar baseline next to every .tsbuildinfo write.

using System;
using Ts.Collections;
using Ts.TestUtil.FsBaselineUtil;
using Ts.Vfs;

namespace Ts.Cli.Tests.Execute;

using TsPathApi = global::Ts.TsPath.TsPath;

internal static class Harness
{
    // harnessutil.go:41
    internal const string FakeTSVersion = "FakeTSVersion";
}

// fs.go:16-20
internal sealed class TestFs : IFileSystem
{
    internal readonly IFileSystem FS;
    internal SyncSet<string>? defaultLibs = null; // assigned by TestSys.ensureLibPathExists (sys.go)
    internal readonly SyncSet<string> writtenFiles = new();

    internal TestFs(IFileSystem fs) => FS = fs;

    // fs.go:22-26
    private void RemoveIgnoreLibPath(string path)
    {
        if (defaultLibs != null && defaultLibs.Has(path))
        {
            defaultLibs.Delete(path);
        }
    }

    // fs.go:28-33 — ReadFile reads the file specified by path and returns the content.
    // If the file fails to be read, ok will be false.
    public bool TryReadFile(string path, out string contents)
    {
        RemoveIgnoreLibPath(path);
        return TryReadFileHandlingBuildInfo(path, out contents);
    }

    // fs.go:35-51
    private bool TryReadFileHandlingBuildInfo(string path, out string contents)
    {
        var ok = FS.TryReadFile(path, out contents);
        if (ok && TsPathApi.FileExtensionIs(path, TsPathApi.ExtensionTsBuildInfo))
        {
            // read buildinfo and modify version
            var buildInfo = global::Ts.Execute.Incremental.BuildInfo.Unmarshal(contents);
            if (buildInfo != null && buildInfo.Version == Harness.FakeTSVersion)
            {
                buildInfo.Version = global::Ts.Core.TsVersion.Version();
                contents = buildInfo.Marshal();
            }
        }
        return ok;
    }

    // fs.go:53-57
    public void WriteFile(string path, string data)
    {
        RemoveIgnoreLibPath(path);
        writtenFiles.Add(path);
        WriteFileHandlingBuildInfo(path, data);
    }

    // fs.go:59-84
    private void WriteFileHandlingBuildInfo(string path, string data)
    {
        if (TsPathApi.FileExtensionIs(path, TsPathApi.ExtensionTsBuildInfo))
        {
            var buildInfo = global::Ts.Execute.Incremental.BuildInfo.Unmarshal(data);
            if (buildInfo != null)
            {
                if (buildInfo.Version == global::Ts.Core.TsVersion.Version())
                {
                    // Change it to harnessutil.FakeTSVersion
                    buildInfo.Version = Harness.FakeTSVersion;
                    data = buildInfo.Marshal();
                }
                // Write readable build info version
                WriteFile(
                    path + ".readable.baseline.txt",
                    ReadableBuildInfo.ToReadableBuildInfo(buildInfo, FSDiffer.SanitizeInternalSymbolName(data)));
            }
            else
            {
                throw new InvalidOperationException("testFs.WriteFile: failed to unmarshal build info: - use underlying FS's write method if this is intended use for testcase");
            }
        }
        FS.WriteFile(path, data);
    }

    // fs.go:86-90 — Removes `path` and all its contents. Will return the first error it encounters.
    public void Remove(string path)
    {
        RemoveIgnoreLibPath(path);
        FS.Remove(path);
    }

    // Delegated members (Go struct embedding of vfs.FS).
    public bool UseCaseSensitiveFileNames() => FS.UseCaseSensitiveFileNames();

    public bool FileExists(string path) => FS.FileExists(path);

    public void AppendFile(string path, string data) => FS.AppendFile(path, data);

    public void Chtimes(string path, DateTime aTime, DateTime mTime) => FS.Chtimes(path, aTime, mTime);

    public bool DirectoryExists(string path) => FS.DirectoryExists(path);

    public Entries GetAccessibleEntries(string path) => FS.GetAccessibleEntries(path);

    public IFileInfo? Stat(string path) => FS.Stat(path);

    public Exception? WalkDir(string root, WalkDirFunc walkFn) => FS.WalkDir(root, walkFn);

    public string Realpath(string path) => FS.Realpath(path);
}
