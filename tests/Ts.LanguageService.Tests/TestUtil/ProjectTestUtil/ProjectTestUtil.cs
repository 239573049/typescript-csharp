// Ported from tsgo internal/testutil/projecttestutil/projecttestutil.go (whole file).
//
// PORT-NOTEs:
//  * Go `context.Context` → Ts.Core.RequestContext (project surfaces) / CancellationToken
//    (client surfaces) per plan/01 §4.4; WithRequestID maps to RequestContext.WithRequestId.
//  * Go `map[string]any` file maps → Dictionary<string, object?> (VfsTestUtil.FromMap<object>
//    accepts string/byte[]/MapFile values exactly like Go's vfstest.FromMap).
//  * Go `sync.Once` → Lazy<T>.
//  * The npm-install mock surfaces Go's `(bytes, error)` as `(byte[], Exception?)` (05 §4);
//    the underlying IFileSystem.WriteFile throws IOException, which is caught and returned
//    in the Exception slot exactly where Go propagated err.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using FileSystemWatcher = global::Ts.LsProto.FileSystemWatcher;

namespace Ts.TestUtil.ProjectTestUtil;

// projecttestutil.go:26-30 — the //go:generate moq directives have no C# analog; the mocks
// are hand-written in ClientMock.cs / NpmExecutorMock.cs.

// projecttestutil.go:32-34 — TestTypingsLocation.
public static class ProjectTestUtilConstants
{
    public const string TestTypingsLocation = "/home/src/Library/Caches/typescript";
}

// projecttestutil.go:36-39 — TypingsInstallerOptions.
public sealed class TypingsInstallerOptions
{
    public string[] TypesRegistry = [];

    public Dictionary<string, string> PackageToFile = [];
}

// projecttestutil.go:41-49 — SessionUtils.
public sealed class SessionUtils
{
    private readonly string currentDirectory;
    internal readonly global::Ts.Vfs.IoVfs.IFsWithSys FsFromFileMapField;
    private readonly global::Ts.Vfs.IFileSystem fs;
    private readonly ClientMock client;
    private readonly NpmExecutorMock npmExecutor;
    private readonly TypingsInstallerOptions? tiOptions;
    private readonly global::Ts.Project.Logging.LogCollector logger;

    internal SessionUtils(
        string currentDirectory,
        global::Ts.Vfs.IoVfs.IFsWithSys fsFromFileMap,
        global::Ts.Vfs.IFileSystem fs,
        ClientMock client,
        NpmExecutorMock npmExecutor,
        TypingsInstallerOptions? tiOptions,
        global::Ts.Project.Logging.LogCollector logger)
    {
        this.currentDirectory = currentDirectory;
        FsFromFileMapField = fsFromFileMap;
        this.fs = fs;
        this.client = client;
        this.npmExecutor = npmExecutor;
        this.tiOptions = tiOptions;
        this.logger = logger;
    }

    // projecttestutil.go:51-53 — FsFromFileMap.
    public global::Ts.Vfs.IoVfs.IFsWithSys FsFromFileMap()
    {
        return FsFromFileMapField;
    }

    // projecttestutil.go:55-57 — Client.
    public ClientMock Client()
    {
        return client;
    }

    // projecttestutil.go:59-61 — NpmExecutor.
    public NpmExecutorMock NpmExecutor()
    {
        return npmExecutor;
    }

    // projecttestutil.go:63-111 — SetupNpmExecutorForTypingsInstaller.
    public void SetupNpmExecutorForTypingsInstaller()
    {
        if (tiOptions == null)
        {
            return;
        }

        npmExecutor.NpmInstallFunc = (cwd, packageNames) =>
        {
            // packageNames is actually npmInstallArgs due to interface misnaming
            string[] npmInstallArgs = packageNames;
            int lenNpmInstallArgs = npmInstallArgs.Length;
            if (lenNpmInstallArgs < 3)
            {
                return ([], new InvalidOperationException($"unexpected npm install: {cwd} [{string.Join(" ", npmInstallArgs)}]"));
            }

            if (lenNpmInstallArgs == 3 && npmInstallArgs[2] == "types-registry@latest")
            {
                // Write typings file
                Exception? err = null;
                try
                {
                    fs.WriteFile(cwd + "/node_modules/types-registry/index.json", CreateTypesRegistryFileContent());
                }
                catch (Exception e)
                {
                    err = e;
                }
                return ([], err);
            }

            // Find the packages: they start at index 2 and continue until we hit a flag starting with --
            int packageEnd = lenNpmInstallArgs;
            for (int i = 2; i < lenNpmInstallArgs; i++)
            {
                if (npmInstallArgs[i].StartsWith("--", StringComparison.Ordinal))
                {
                    packageEnd = i;
                    break;
                }
            }

            for (int i = 2; i < packageEnd; i++)
            {
                // @types/packageName@TsVersionToUse
                string atTypesPackage = npmInstallArgs[i];
                // Remove version suffix
                int versionIndex = atTypesPackage.LastIndexOf('@');
                if (versionIndex > 6)
                {
                    // "@types/".length is 7, so version @ must be after
                    atTypesPackage = atTypesPackage[..versionIndex];
                }
                // Extract package name from @types/packageName
                string packageBaseName = atTypesPackage[7..]; // Remove "@types/" prefix
                if (!tiOptions.PackageToFile.TryGetValue(packageBaseName, out string? content))
                {
                    return ([], new InvalidOperationException($"content not provided for {packageBaseName}"));
                }
                try
                {
                    fs.WriteFile(cwd + "/node_modules/@types/" + packageBaseName + "/index.d.ts", content);
                }
                catch (Exception e)
                {
                    return ([], e);
                }
            }
            return ([], null);
        };
    }

    // projecttestutil.go:113-115 — ToPath.
    public global::Ts.TsPath.Path ToPath(string fileName)
    {
        return global::Ts.TsPath.TsPath.ToPath(fileName, currentDirectory, fs.UseCaseSensitiveFileNames());
    }

    // projecttestutil.go:117-119 — FS.
    public global::Ts.Vfs.IFileSystem FS()
    {
        return fs;
    }

    // projecttestutil.go:121-149 — WatchesFile reports whether any registered file watcher
    // would match the given file path. It handles both absolute glob patterns and relative
    // patterns with a base URI. On case-insensitive file systems the paths in glob patterns
    // are lowercased, so callers should pass the lowercased path.
    public bool WatchesFile(string filePath)
    {
        // Go: `if g, err := glob.Parse(p); err == nil && g.Match(path)` — the C# Glob.Parse
        // throws FormatException on invalid patterns (05 §4), so the error arm becomes a
        // catch that reports "no match".
        static bool MatchesPattern(string pattern, string path)
        {
            try
            {
                return global::Ts.Glob.Glob.Parse(pattern).Match(path);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        foreach (var call in client.WatchFilesCalls())
        {
            foreach (FileSystemWatcher watcher in call.Watchers)
            {
                if (watcher.GlobPattern.Pattern is not null)
                {
                    if (MatchesPattern(watcher.GlobPattern.Pattern, filePath))
                    {
                        return true;
                    }
                }
                else if (watcher.GlobPattern.RelativePattern is not null)
                {
                    var rp = watcher.GlobPattern.RelativePattern;
                    // PORT-NOTE: Go dereferences *rp.BaseUri.URI (a nil URI pointer would
                    // panic); the C# union stores Nullable<URI>, so the first `.Value`
                    // unwraps Nullable<URI> and the second yields the string.
                    string baseUri = rp.BaseUri.URI!.Value.Value;
                    // Convert base URI (e.g. "file:///home/projects") to a directory path
                    // with trailing separator for proper prefix matching on path boundaries.
                    // Go: lsproto.DocumentUri(baseUri).FileName() — called as a static method
                    // here because the Ls layer hosts a second internal FileName extension.
                    string baseDir = global::Ts.LsProto.DocumentUriExtensions.FileName(new global::Ts.LsProto.DocumentUri(baseUri));
                    baseDir = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(baseDir);
                    if (filePath.StartsWith(baseDir, StringComparison.Ordinal))
                    {
                        string relativePath = filePath[baseDir.Length..];
                        if (MatchesPattern(rp.Pattern, relativePath))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    // projecttestutil.go:151-153 — Logs.
    public string Logs()
    {
        return logger.ToString() ?? "";
    }

    // projecttestutil.go:155-159 — BaselineLogs. Go takes *testing.T for the test name;
    // the C# baseline surface takes an IBaselineTestContext plus the explicit name.
    public void BaselineLogs(global::Ts.TestUtil.IBaselineTestContext t, string name)
    {
        global::Ts.TestUtil.Baseline.Run(t, name + ".log", Logs(), new global::Ts.TestUtil.Baseline.Options
        {
            Subfolder = "project",
        });
    }

    private static readonly Lazy<string> typesRegistryConfigTextOnce = new(BuildTypesRegistryConfigText);

    // projecttestutil.go:166-179 — TypesRegistryConfigText.
    public static string TypesRegistryConfigText()
    {
        return typesRegistryConfigTextOnce.Value;
    }

    private static string BuildTypesRegistryConfigText()
    {
        var result = new StringBuilder();
        // map-iter: 序无关 — Go iterates this map in random order to emit a JSON object;
        // entry order is irrelevant to every consumer (it is parsed back into a map).
        foreach ((string key, string value) in TypesRegistryConfig())
        {
            if (result.Length != 0)
            {
                result.Append(',');
            }
            result.Append($"\n      \"{key}\": \"{value}\"");
        }
        return result.ToString();
    }

    private static readonly Lazy<Dictionary<string, string>> typesRegistryConfigOnce = new(BuildTypesRegistryConfig);

    // projecttestutil.go:186-201 — TypesRegistryConfig.
    public static Dictionary<string, string> TypesRegistryConfig()
    {
        return typesRegistryConfigOnce.Value;
    }

    private static Dictionary<string, string> BuildTypesRegistryConfig()
    {
        return new Dictionary<string, string>
        {
            ["latest"] = "1.3.0",
            ["ts2.0"] = "1.0.0",
            ["ts2.1"] = "1.0.0",
            ["ts2.2"] = "1.2.0",
            ["ts2.3"] = "1.3.0",
            ["ts2.4"] = "1.3.0",
            ["ts2.5"] = "1.3.0",
            ["ts2.6"] = "1.3.0",
            ["ts2.7"] = "1.3.0",
        };
    }

    // projecttestutil.go:203-218 — createTypesRegistryFileContent.
    private string CreateTypesRegistryFileContent()
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"entries\": {");
        int index = 0;
        foreach (string entry in tiOptions!.TypesRegistry)
        {
            AppendTypesRegistryConfig(builder, index, entry);
            index++;
        }
        // map-iter: 序无关 — Go iterates this map in random order; the output is a JSON
        // object whose entry order no consumer depends on.
        foreach (string key in tiOptions.PackageToFile.Keys)
        {
            if (Array.IndexOf(tiOptions.TypesRegistry, key) < 0)
            {
                AppendTypesRegistryConfig(builder, index, key);
                index++;
            }
        }
        builder.Append("\n  }\n}");
        return builder.ToString();
    }

    // projecttestutil.go:220-225 — appendTypesRegistryConfig.
    private static void AppendTypesRegistryConfig(StringBuilder builder, int index, string entry)
    {
        if (index > 0)
        {
            builder.Append(',');
        }
        builder.Append($"\n    \"{entry}\": {{{TypesRegistryConfigText()}\n    }}");
    }

    // projecttestutil.go:227-229 — Setup.
    public static (global::Ts.Project.Session Session, SessionUtils Utils) Setup(Dictionary<string, object?> files)
    {
        return SetupWithTypingsInstaller(files, new TypingsInstallerOptions());
    }

    // projecttestutil.go:231-263 — SetupWithRealFS.
    public static (global::Ts.Project.Session Session, SessionUtils Utils) SetupWithRealFS()
    {
        global::Ts.Vfs.IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(global::Ts.Vfs.OsVfs.OsVfsUtil.FS());
        var clientMock = new ClientMock();
        var npmExecutorMock = new NpmExecutorMock();
        string wd = Directory.GetCurrentDirectory();

        var sessionUtils = new SessionUtils(
            currentDirectory: wd,
            fsFromFileMap: null!,
            fs: fs,
            client: clientMock,
            npmExecutor: npmExecutorMock,
            tiOptions: null,
            logger: global::Ts.Project.Logging.LoggingApi.NewTestLogger());

        return (global::Ts.Project.Session.NewSession(new global::Ts.Project.SessionInit
        {
            BackgroundCtx = global::Ts.Core.RequestContext.Background,
            FS = fs,
            Client = clientMock,
            NpmExecutor = npmExecutorMock,
            Logger = sessionUtils.logger,
            Options = new global::Ts.Project.SessionOptions
            {
                CurrentDirectory = wd,
                DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
                PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
                WatchEnabled = true,
                LoggingEnabled = true,
                PushDiagnosticsEnabled = true,
            },
        }), sessionUtils);
    }

    // projecttestutil.go:265-267 — SetupWithOptions.
    public static (global::Ts.Project.Session Session, SessionUtils Utils) SetupWithOptions(Dictionary<string, object?> files, global::Ts.Project.SessionOptions options)
    {
        return SetupWithOptionsAndTypingsInstaller(files, options, new TypingsInstallerOptions());
    }

    // projecttestutil.go:269-271 — SetupWithTypingsInstaller.
    public static (global::Ts.Project.Session Session, SessionUtils Utils) SetupWithTypingsInstaller(Dictionary<string, object?> files, TypingsInstallerOptions tiOptions)
    {
        return SetupWithOptionsAndTypingsInstaller(files, null, tiOptions);
    }

    // projecttestutil.go:273-278 — SetupWithOptionsAndTypingsInstaller.
    public static (global::Ts.Project.Session Session, SessionUtils Utils) SetupWithOptionsAndTypingsInstaller(Dictionary<string, object?> files, global::Ts.Project.SessionOptions? options, TypingsInstallerOptions tiOptions)
    {
        (global::Ts.Project.SessionInit init, SessionUtils sessionUtils) = GetSessionInitOptions(files, options, tiOptions);
        global::Ts.Project.Session session = global::Ts.Project.Session.NewSession(init);

        return (session, sessionUtils);
    }

    // projecttestutil.go:280-282 — WithRequestID.
    public static global::Ts.Core.RequestContext WithRequestID(global::Ts.Core.RequestContext ctx)
    {
        return ctx.WithRequestId("0");
    }

    // projecttestutil.go:284-323 — GetSessionInitOptions.
    public static (global::Ts.Project.SessionInit Init, SessionUtils Utils) GetSessionInitOptions(Dictionary<string, object?> files, global::Ts.Project.SessionOptions? options, TypingsInstallerOptions tiOptions)
    {
        global::Ts.Vfs.IFileSystem fsFromFileMap = global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: false);
        global::Ts.Vfs.IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(fsFromFileMap);
        var clientMock = new ClientMock();
        var npmExecutorMock = new NpmExecutorMock();
        var sessionUtils = new SessionUtils(
            currentDirectory: "/",
            fsFromFileMap: (global::Ts.Vfs.IoVfs.IFsWithSys)fsFromFileMap,
            fs: fs,
            client: clientMock,
            npmExecutor: npmExecutorMock,
            tiOptions: tiOptions,
            logger: global::Ts.Project.Logging.LoggingApi.NewTestLogger());

        // Configure the npm executor mock to handle typings installation
        sessionUtils.SetupNpmExecutorForTypingsInstaller();

        // Use provided options or create default ones
        options ??= new global::Ts.Project.SessionOptions
        {
            CurrentDirectory = "/",
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation,
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = true,
            LoggingEnabled = true,
            PushDiagnosticsEnabled = true,
        };

        return (new global::Ts.Project.SessionInit
        {
            BackgroundCtx = global::Ts.Core.RequestContext.Background,
            Options = options,
            FS = fs,
            Client = clientMock,
            NpmExecutor = npmExecutorMock,
            Logger = sessionUtils.logger,
        }, sessionUtils);
    }
}
