// Ported from tsgo internal/project/extendedconfigcache_test.go (whole file).

using System;
using System.Collections.Generic;
using System.Threading;

using Ts.LsProto;
using Ts.Project.Logging;
using Ts.TsOptions;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;
using FileSystemWatcher = Ts.LsProto.FileSystemWatcher;
using Path = Ts.TsPath.Path;
using Range = Ts.LsProto.Range;
using RequestContext = Ts.Core.RequestContext;
using TelemetryEvent = Ts.LsProto.RequestFailureTelemetryEventOrPerformanceStatsTelemetryEventOrProjectInfoTelemetryEventOrNull;

namespace Ts.Project.Tests;

public sealed class ExtendedConfigCacheTests
{
    // extendedconfigcache_test.go:19-45 — noopClient.
    private sealed class NoopClient : Client
    {
        public Exception? WatchFiles(CancellationToken ctx, WatcherID id, FileSystemWatcher[] watchers) => null;

        public Exception? UnwatchFiles(CancellationToken ctx, WatcherID id) => null;

        public Exception? RefreshDiagnostics(CancellationToken ctx) => null;

        public Exception? PublishDiagnostics(CancellationToken ctx, Ts.LsProto.PublishDiagnosticsParams @params) => null;

        public Exception? RefreshInlayHints(CancellationToken ctx) => null;

        public Exception? RefreshCodeLens(CancellationToken ctx) => null;

        public void ProgressStart(Ts.Diagnostics.Message message, params object?[] args) { }

        public void ProgressFinish(Ts.Diagnostics.Message message, params object?[] args) { }

        public Exception? SendTelemetry(CancellationToken ctx, TelemetryEvent telemetry) => null;

        public bool IsActive() => true;
    }

    // extendedconfigcache_test.go:307-314 — testParseConfigHost.
    private sealed class TestParseConfigHost : ParseConfigHost
    {
        private readonly IFileSystem fs;
        private readonly string cwd;

        public TestParseConfigHost(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public IFileSystem FS() => fs;

        public string GetCurrentDirectory() => cwd;
    }

    // extendedconfigcache_test.go:57-76 — setup.
    private static Session Setup(Dictionary<string, string> files)
    {
        IFileSystem fsFromMap = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        IFileSystem fs = Ts.Bundled.Bundled.WrapFS(fsFromMap);
        return Session.NewSession(new SessionInit
        {
            BackgroundCtx = RequestContext.Background,
            Options = new SessionOptions
            {
                CurrentDirectory = "/",
                DefaultLibraryPath = Ts.Bundled.Bundled.LibPath(),
                TypingsLocation = "/home/src/Library/Caches/typescript",
                PositionEncoding = PositionEncodingKind.UTF8,
                WatchEnabled = false,
                LoggingEnabled = false,
            },
            FS = fs,
            Client = new NoopClient(),
            Logger = LoggingApi.NewTestLogger(),
            NpmExecutor = null,
        });
    }

    // extendedconfigcache_test.go:78 — untitledSeq (per-test-instance state).
    private int untitledSeq;

    // extendedconfigcache_test.go:79-83 — openUntitled.
    private void OpenUntitled(Session session)
    {
        untitledSeq++;
        var uri = new DocumentUri($"untitled:Untitled-{untitledSeq}");
        session.DidOpenFile(RequestContext.Background, uri, 1, "", LanguageKind.TypeScript.Value!);
    }

    // extendedconfigcache_test.go:85-90 — flushCloseProject is the canonical way to ensure
    // project close work is applied. Close the file, then open an unrelated file.
    private void FlushCloseProject(Session session, DocumentUri fileURI)
    {
        session.DidCloseFile(RequestContext.Background, fileURI);
        OpenUntitled(session);
    }

    // extendedconfigcache_test.go:92-98 — ownerCount.
    private static int OwnerCount(Session session, Path path)
    {
        if (!session.extendedConfigCache.entries.TryLoad(path, out OwnerCacheEntry<ExtendedConfigCacheEntry>? entry))
        {
            return 0;
        }
        return entry!.owners.Count;
    }

    // extendedconfigcache_test.go:100-105 — assertNoEntry.
    private static void AssertNoEntry(Session session, string fileName)
    {
        Path path = session.toPath(fileName);
        bool ok = session.extendedConfigCache.entries.TryLoad(path, out _);
        Assert.False(ok);
    }

    // extendedconfigcache_test.go:107-118 — expectedExtendedOwnerCounts.
    private static Dictionary<Path, int> ExpectedExtendedOwnerCounts(Session session, Snapshot snapshot)
    {
        var result = new Dictionary<Path, int>();
        // map-iter: 序无关 — per-config counting is order-independent.
        foreach ((Path _, ConfigFileEntry cfg) in snapshot.ConfigFileRegistry.configs)
        {
            if (cfg.commandLine is null || cfg.commandLine.ConfigFile is null)
            {
                continue;
            }
            foreach (string file in cfg.commandLine.ExtendedSourceFiles() ?? [])
            {
                Path path = session.toPath(file);
                result[path] = result.TryGetValue(path, out int count) ? count + 1 : 1;
            }
        }
        return result;
    }

    // extendedconfigcache_test.go:120-127 — assertExtendedOwnerCountsMatchRegistry.
    private static void AssertExtendedOwnerCountsMatchRegistry(Session session, Snapshot snapshot)
    {
        Dictionary<Path, int> expected = ExpectedExtendedOwnerCounts(session, snapshot);
        // map-iter: 序无关 — each path's owner count is checked independently.
        foreach ((Path path, int want) in expected)
        {
            int got = OwnerCount(session, path);
            Assert.Equal(want, got);
        }
    }

    // TestExtendedConfigCacheOwnership tests the invariant that each ExtendedSourceFile
    // of a config in the ConfigFileRegistry is owned exactly once per snapshot that
    // references it, and released exactly once when that snapshot is removed.
    // (extendedconfigcache_test.go:47-55)

    // extendedconfigcache_test.go:129-175 — "multi-extends shared ancestor counted once".
    [Fact]
    public void TestExtendedConfigCacheOwnership_MultiExtendsSharedAncestorCountedOnce()
    {
        // One config extends *two* configs; both extend a shared root.
        // Expected behavior: ExtendedSourceFiles() is deduped, so the shared root should only
        // be ref'd once for this config.
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = """
                {
                    "extends": ["./tsconfig.base1.json", "./tsconfig.base2.json"]
                }
                """,
            ["/project/tsconfig.base1.json"] = """
                {
                    "extends": "./tsconfig.root.json",
                    "compilerOptions": {"strict": true}
                }
                """,
            ["/project/tsconfig.base2.json"] = """
                {
                    "extends": "./tsconfig.root.json",
                    "compilerOptions": {"noImplicitAny": true}
                }
                """,
            ["/project/tsconfig.root.json"] = """
                {
                    "compilerOptions": {"target": "ES2020"}
                }
                """,
            ["/project/src/main.ts"] = "export const x = 1;",
        };

        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/src/main.ts"), 1, files["/project/src/main.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;

        ParsedCommandLine? config = snapshot.ConfigFileRegistry.GetConfig(new Path("/project/tsconfig.json"));
        Assert.NotNull(config);
        // Shared root should only appear once in the flattened list.
        int rootCount = 0;
        foreach (string f in config!.ExtendedSourceFiles() ?? [])
        {
            if (f == "/project/tsconfig.root.json")
            {
                rootCount++;
            }
        }
        Assert.Equal(1, rootCount);

        // And the cache owner counts should match the registry's deduped list.
        AssertExtendedOwnerCountsMatchRegistry(session, snapshot);

        FlushCloseProject(session, new DocumentUri("file:///project/src/main.ts"));
        AssertNoEntry(session, "/project/tsconfig.base1.json");
        AssertNoEntry(session, "/project/tsconfig.base2.json");
        AssertNoEntry(session, "/project/tsconfig.root.json");
    }

    // extendedconfigcache_test.go:177-208 — "ExtendedSourceFiles can contain same path
    // twice (case-insensitive)".
    [Fact]
    public void TestExtendedConfigCacheOwnership_ExtendedSourceFilesCanContainSamePathTwiceCaseInsensitive()
    {
        // This test is descriptive, not prescriptive. This seems bad and unintentional,
        // but is here to show that while the problem exists in the underlying config parsing
        // API, it doesn't disrupt cache ownership.
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = """
                {
                    "extends": ["./Shared.json", "./shared.json"]
                }
                """,
            ["/project/shared.json"] = """
                {
                    "compilerOptions": {"strict": true}
                }
                """,
        };

        // This test intentionally bypasses the project system's ExtendedConfigCache so we can
        // observe how ExtendedSourceFiles behaves when the same underlying file is referenced
        // with different casing on a case-insensitive FS.
        IFileSystem fsFromMap = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        IFileSystem fs = Ts.Bundled.Bundled.WrapFS(fsFromMap);

        // Minimal ParseConfigHost implementation.
        var h = new TestParseConfigHost(fs, "/");
        var (cmd, diags) = TsConfigParsing.GetParsedCommandLineOfConfigFile("/project/tsconfig.json", null, null, h, null /*extendedConfigCache*/);
        Assert.True(diags is null || diags.Length == 0);
        Assert.NotNull(cmd);

        string[] extended = cmd!.ExtendedSourceFiles()!;
        Assert.Equal(2, extended.Length);
        Assert.Equal("/project/Shared.json", extended[0]);
        Assert.Equal("/project/shared.json", extended[1]);
    }

    // extendedconfigcache_test.go:210-232 — "project system dedupes case-only extends
    // via cache".
    [Fact]
    public void TestExtendedConfigCacheOwnership_ProjectSystemDedupesCaseOnlyExtendsViaCache()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = """
                {
                    "extends": ["./Shared.json", "./shared.json"]
                }
                """,
            ["/project/shared.json"] = """
                {
                    "compilerOptions": {"strict": true}
                }
                """,
            ["/project/src/main.ts"] = "export const x = 1;",
        };

        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/src/main.ts"), 1, files["/project/src/main.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;

        ParsedCommandLine? config = snapshot.ConfigFileRegistry.GetConfig(new Path("/project/tsconfig.json"));
        Assert.NotNull(config);
        string[] extended = config!.ExtendedSourceFiles()!;
        Assert.Single(extended);
        Assert.Equal(session.toPath(extended[0]), session.toPath("/project/shared.json"));
    }

    // extendedconfigcache_test.go:234-304 — "transitive extended config ownership with
    // new project".
    [Fact]
    public void TestExtendedConfigCacheOwnership_TransitiveExtendedConfigOwnershipWithNewProject()
    {
        // Scenario: transitive extends chain where a new project reuses a cached
        // extended config without reparsing it, which should still acquire the transitive deps.
        //
        // projectA/tsconfig.json extends shared/tsconfig.base.json extends shared/tsconfig.common.json
        // projectB/tsconfig.json extends shared/tsconfig.base.json extends shared/tsconfig.common.json
        //
        // When projectB is opened AFTER projectA, tsconfig.base.json is retrieved from cache
        // (not reparsed), so tsconfig.common.json still needs projectB's snapshot ownership.
        var files = new Dictionary<string, string>
        {
            ["/user/username/projects/shared/tsconfig.common.json"] = """
                {
                    "compilerOptions": { "strict": true }
                }
                """,
            ["/user/username/projects/shared/tsconfig.base.json"] = """
                {
                    "extends": "./tsconfig.common.json",
                    "compilerOptions": { "target": "ES2020" }
                }
                """,
            ["/user/username/projects/projectA/tsconfig.json"] = """
                {
                    "extends": "../shared/tsconfig.base.json"
                }
                """,
            ["/user/username/projects/projectA/src/main.ts"] = "const a = 1;",
            ["/user/username/projects/projectB/tsconfig.json"] = """
                {
                    "extends": "../shared/tsconfig.base.json"
                }
                """,
            ["/user/username/projects/projectB/src/main.ts"] = "const b = 2;",
            ["/user/username/projects/other/src/main.ts"] = "const other = 3;",
        };

        Session session = Setup(files);

        // Step 1: Open file in projectA - this parses the full extends chain
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/projectA/src/main.ts"), 1, files["/user/username/projects/projectA/src/main.ts"], LanguageKind.TypeScript.Value!);

        // Verify extended configs are in cache with correct owner counts
        bool baseOk = session.extendedConfigCache.entries.TryLoad(new Path("/user/username/projects/shared/tsconfig.base.json"), out OwnerCacheEntry<ExtendedConfigCacheEntry>? baseEntry);
        bool commonOk = session.extendedConfigCache.entries.TryLoad(new Path("/user/username/projects/shared/tsconfig.common.json"), out OwnerCacheEntry<ExtendedConfigCacheEntry>? commonEntry);
        Assert.True(baseOk, "tsconfig.base.json should be in cache");
        Assert.True(commonOk, "tsconfig.common.json should be in cache");
        Assert.Single(baseEntry!.owners);
        Assert.Single(commonEntry!.owners);

        // Step 2: Open file in projectB - this should acquire tsconfig.base.json from cache
        // (not reparse it), and should also acquire tsconfig.common.json.
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/projectB/src/main.ts"), 1, files["/user/username/projects/projectB/src/main.ts"], LanguageKind.TypeScript.Value!);

        // Step 3: Close projectA file and open an unrelated file to force projectA cleanup
        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/projectA/src/main.ts"));
        // Opening another file triggers cleanup of closed projects
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/other/src/main.ts"), 1, files["/user/username/projects/other/src/main.ts"], LanguageKind.TypeScript.Value!);

        // Close the other file too so only projectB remains
        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/other/src/main.ts"));

        // Step 4: Trigger another snapshot clone for projectB
        session.DidChangeFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/projectB/src/main.ts"), 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromPartial(new TextDocumentContentChangePartial
            {
                Range = new Range
                {
                    Start = new Position { Line = 0, Character = 0 },
                    End = new Position { Line = 0, Character = 12 },
                },
                Text = "const b = 3;",
            }),
        ]);
        // This call triggered the panic
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///user/username/projects/projectB/src/main.ts"));
        Assert.Null(err);
    }
}
