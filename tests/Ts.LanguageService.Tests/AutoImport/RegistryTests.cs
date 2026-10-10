// Ported from tsgo internal/ls/autoimport/registry_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function per xUnit class section (TestRegistryLifecycle, TestHidden…,
//    TestAutoImportEntrypointDirectorySearch, TestUpdateIndexesConcurrentMapSafety);
//    t.Run subtests become [Fact]s named <Parent>_<Subtest> (plan/05-conventions.md §16.1).
//  * Go `context.Background()` → RequestContext.Background; `t.Cleanup(session.Close)`
//    has no analog here, matching the other SessionUtils.Setup users in this assembly
//    (ProjectLifetimeTests).
//  * gotest.tools `assert.Equal(t, actual, expected)` → `Assert.Equal(expected, actual)`
//    (argument order swapped); `assert.Assert(cond, msg, args…)` → `Assert.True(cond, …)`.
//  * Go compares DirtyFile() against `tspath.Path("")`; the C# Path is a record struct
//    whose default has a null Value (Path.cs:13), so the comparison is against `default`
//    (both are "no dirty file").
//  * `collections.NewSetFromItems("pkg1")` → `Ts.Collections.Set.FromItems("pkg1")`.
//  * Like the sibling AutoImport suites, the tests and helpers are one class: Go's
//    package-level constants/helpers are private static members here.

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;
using Ts.Ls;
using Ts.Ls.AutoImport;
using Ts.TestUtil.AutoImportTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using FileChangeType = global::Ts.LsProto.FileChangeType;
using FileEvent = global::Ts.LsProto.FileEvent;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using Path = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.Ls.AutoImport.Tests;

public sealed class RegistryTests
{
    // registry_test.go:1127-1130 — lifecycleProjectRoot / monorepoProjectRoot.
    private const string lifecycleProjectRoot = "/home/src/autoimport-lifecycle";
    private const string monorepoProjectRoot = "/home/src/autoimport-monorepo";

    // registry_test.go:1167-1175 — autoImportStats.
    private static CacheStats AutoImportStats(global::Ts.Project.Session session)
    {
        global::Ts.Project.Snapshot? snapshot = session.Snapshot();
        Registry? registry = snapshot?.AutoImportRegistry();
        if (registry is null)
        {
            Assert.Fail("auto import registry not initialized");
        }
        return registry!.GetCacheStats();
    }

    // registry_test.go:1177-1183 — singleBucket.
    private static BucketStats SingleBucket(List<BucketStats> buckets)
    {
        if (buckets.Count != 1)
        {
            Assert.Fail($"expected 1 bucket, got {buckets.Count}");
        }
        return buckets[0];
    }

    // registry_test.go:24-896 — TestRegistryLifecycle.
    // registry_test.go:26-52 — "builds project and node_modules buckets".
    [Fact]
    public void TestRegistryLifecycle_BuildsProjectAndNodeModulesBuckets()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 1);
        global::Ts.Project.Session session = fixture.Session();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);
        session.DidOpenFile(RequestContext.Background, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);

        CacheStats stats = AutoImportStats(session);
        BucketStats projectBucket = SingleBucket(stats.ProjectBuckets);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(projectBucket.State.Dirty());
        Assert.Equal(0, projectBucket.FileCount);
        Assert.True(nodeModulesBucket.State.Dirty());
        Assert.Equal(0, nodeModulesBucket.FileCount);

        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(RequestContext.Background, mainFile.URI());
        Assert.Null(err);

        stats = AutoImportStats(session);
        projectBucket = SingleBucket(stats.ProjectBuckets);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(projectBucket.State.Dirty());
        Assert.True(projectBucket.ExportCount > 0);
        Assert.False(nodeModulesBucket.State.Dirty());
        Assert.True(nodeModulesBucket.ExportCount > 0);
    }

    // registry_test.go:54-100 — "bucket does not rebuild on same-file change".
    [Fact]
    public void TestRegistryLifecycle_BucketDoesNotRebuildOnSameFileChange()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 2);
        global::Ts.Project.Session session = fixture.Session();
        global::Ts.TestUtil.ProjectTestUtil.SessionUtils utils = fixture.Utils();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);
        ProjectFileHandle secondaryFile = project.File(1);
        session.DidOpenFile(RequestContext.Background, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, secondaryFile.URI(), 1, secondaryFile.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(RequestContext.Background, mainFile.URI());
        Assert.Null(err);

        string updatedContent = mainFile.Content() + "// change\n";
        session.DidChangeFile(RequestContext.Background, mainFile.URI(), 2,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = updatedContent },
            },
        ]);

        (_, err) = session.GetLanguageService(RequestContext.Background, mainFile.URI());
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats projectBucket = SingleBucket(stats.ProjectBuckets);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(projectBucket.State.Dirty());
        Assert.Equal(utils.ToPath(mainFile.FileName()), projectBucket.State.DirtyFile());
        Assert.False(nodeModulesBucket.State.Dirty());
        Assert.Equal(default, nodeModulesBucket.State.DirtyFile());

        // Bucket should not recompute when requesting same file changed
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(RequestContext.Background, mainFile.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        projectBucket = SingleBucket(stats.ProjectBuckets);
        Assert.True(projectBucket.State.Dirty());
        Assert.Equal(utils.ToPath(mainFile.FileName()), projectBucket.State.DirtyFile());

        // Bucket should recompute when other file has changed
        session.DidChangeFile(RequestContext.Background, secondaryFile.URI(), 1,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = "// new content" },
            },
        ]);
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(RequestContext.Background, mainFile.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        projectBucket = SingleBucket(stats.ProjectBuckets);
        Assert.False(projectBucket.State.Dirty());
    }

    // registry_test.go:102-144 — "bucket updates on same-file change when new files added
    // to the program".
    [Fact]
    public void TestRegistryLifecycle_BucketUpdatesOnSameFileChangeWhenNewFilesAddedToTheProgram()
    {
        const string projectRoot = "/home/src/explicit-files-project";
        var files = new Dictionary<string, object?>
        {
            [projectRoot + "/tsconfig.json"] = """
            {
            				"compilerOptions": {
            					"module": "esnext",
            					"target": "esnext",
            					"strict": true
            				},
            				"files": ["index.ts"]
            			}
            """,
            [projectRoot + "/index.ts"] = "",
            [projectRoot + "/utils.ts"] = "export const foo = 1;\nexport const bar = 2;",
        };
        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(files);

        RequestContext ctx = RequestContext.Background;
        DocumentUri indexURI = (DocumentUri)("file://" + projectRoot + "/index.ts");

        // Open the index.ts file
        session.DidOpenFile(ctx, indexURI, 1, "", LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);
        CacheStats stats = AutoImportStats(session);
        BucketStats projectBucket = SingleBucket(stats.ProjectBuckets);
        Assert.Equal(1, projectBucket.FileCount);

        // Edit index.ts to import foo from utils.ts
        const string newContent = "import { foo } from \"./utils\";";
        session.DidChangeFile(ctx, indexURI, 2,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = newContent },
            },
        ]);

        // Bucket should be rebuilt because new files were added
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);
        stats = AutoImportStats(session);
        projectBucket = SingleBucket(stats.ProjectBuckets);
        Assert.Equal(2, projectBucket.FileCount);
    }

    // registry_test.go:146-186 — "package.json dependency changes invalidate node_modules
    // buckets".
    [Fact]
    public void TestRegistryLifecycle_PackageJsonDependencyChangesInvalidateNodeModulesBuckets()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 1);
        global::Ts.Project.Session session = fixture.Session();
        global::Ts.TestUtil.ProjectTestUtil.SessionUtils sessionUtils = fixture.Utils();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);
        NodeModulesPackageHandle nodePackage = project.NodeModules()[0];
        FileHandle packageJSON = project.PackageJSONFile();
        RequestContext ctx = RequestContext.Background;

        session.DidOpenFile(ctx, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);
        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(nodeModulesBucket.State.Dirty());

        global::Ts.Vfs.IFileSystem fs = sessionUtils.FS();
        void UpdatePackageJson(string contentArg)
        {
            fs.WriteFile(packageJSON.FileName(), contentArg);
            session.DidChangeWatchedFiles(ctx,
            [
                new FileEvent
                {
                    Type = FileChangeType.Changed,
                    Uri = packageJSON.URI(),
                },
            ]);
        }

        string sameDepsContent = $"{{\n  \"name\": \"local-project-stable\",\n  \"dependencies\": {{\n    \"{nodePackage.Name}\": \"*\"\n  }}\n}}\n";
        UpdatePackageJson(sameDepsContent);
        (_, err) = session.GetLanguageService(ctx, mainFile.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(nodeModulesBucket.State.Dirty());

        string differentDepsContent = $"{{\n  \"name\": \"local-project-stable\",\n  \"dependencies\": {{\n    \"{nodePackage.Name}\": \"*\",\n    \"newpkg\": \"*\"\n  }}\n}}\n";
        UpdatePackageJson(differentDepsContent);
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        Assert.True(SingleBucket(stats.NodeModulesBuckets).DependencyNames!.Has("newpkg"));
    }

    // registry_test.go:188-229 — "node_modules buckets get deleted when no open files can
    // reference them".
    [Fact]
    public void TestRegistryLifecycle_NodeModulesBucketsGetDeletedWhenNoOpenFilesCanReferenceThem()
    {
        MonorepoFixture fixture = AutoImportFixtures.SetupMonorepoLifecycleSession(new MonorepoSetupConfig
        {
            Root = monorepoProjectRoot,
            MonorepoPackageTemplate = new MonorepoPackageTemplate
            {
                Name = "monorepo",
                NodeModuleNames = ["pkg-root"],
            },
            Packages =
            [
                new MonorepoPackageConfig { FileCount = 1, MonorepoPackageTemplate = new MonorepoPackageTemplate { Name = "package-a", NodeModuleNames = ["pkg-a"] } },
                new MonorepoPackageConfig { FileCount = 1, MonorepoPackageTemplate = new MonorepoPackageTemplate { Name = "package-b", NodeModuleNames = ["pkg-b"] } },
            ],
        });
        global::Ts.Project.Session session = fixture.Session();
        MonorepoHandle monorepo = fixture.Monorepo();
        ProjectHandle pkgA = monorepo.Package(0);
        ProjectHandle pkgB = monorepo.Package(1);
        ProjectFileHandle fileA = pkgA.File(0);
        ProjectFileHandle fileB = pkgB.File(0);
        RequestContext ctx = RequestContext.Background;

        // Open file in package-a, should create buckets for root and package-a node_modules
        session.DidOpenFile(ctx, fileA.URI(), 1, fileA.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, fileA.URI());
        Assert.Null(err);

        // Open file in package-b, should also create buckets for package-b
        session.DidOpenFile(ctx, fileB.URI(), 1, fileB.Content(), LanguageKind.TypeScript.Value!);
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, fileB.URI());
        Assert.Null(err);
        CacheStats stats = AutoImportStats(session);
        Assert.Equal(3, stats.NodeModulesBuckets.Count);
        Assert.Equal(2, stats.ProjectBuckets.Count);

        // Close file in package-a, package-a's node_modules bucket and project bucket should be removed
        session.DidCloseFile(ctx, fileA.URI());
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, fileB.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        Assert.Equal(2, stats.NodeModulesBuckets.Count);
        Assert.Single(stats.ProjectBuckets);
    }

    // registry_test.go:231-278 — "deleting node_modules leaves the registry prepared for
    // importing".
    [Fact]
    public void TestRegistryLifecycle_DeletingNodeModulesLeavesTheRegistryPreparedForImporting()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 1);
        global::Ts.Project.Session session = fixture.Session();
        global::Ts.TestUtil.ProjectTestUtil.SessionUtils sessionUtils = fixture.Utils();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);
        RequestContext ctx = RequestContext.Background;

        UserPreferences preferences = UserPreferences.NewDefaultUserPreferences();
        preferences.IncludeCompletionsForModuleExports = Tristate.True;
        preferences.IncludeCompletionsForImportStatements = Tristate.True;

        // Build auto-imports once so both buckets are clean and prepared.
        session.DidOpenFile(ctx, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);

        global::Ts.Project.Snapshot? snapshot = session.Snapshot();
        global::Ts.Project.Project? defaultProject = snapshot!.GetDefaultProject(mainFile.URI());
        Assert.True(defaultProject != null);
        Path projectPath = defaultProject!.ConfigFilePath();
        Assert.True(snapshot.AutoImportRegistry()!.IsPreparedForImportingFile(mainFile.FileName(), projectPath, preferences));
        Assert.Single(AutoImportStats(session).NodeModulesBuckets);

        // Simulate the user deleting node_modules: remove the directory from disk
        // and notify the session of the deletion, which marks the node_modules
        // bucket dirty.
        string nodeModulesDir = TsPathApi.CombinePaths(project.Root(), "node_modules");
        sessionUtils.FS().Remove(nodeModulesDir);
        session.DidChangeWatchedFiles(ctx,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(nodeModulesDir),
            },
        ]);

        // Re-preparing auto-imports must succeed and leave the registry prepared.
        // Before the fix, the node_modules bucket's rebuild bailed out with
        // vfs.ErrNotExist, leaving the stale dirty bucket in place so the registry
        // reported "needs rebuild" forever (which crashed the request handler).
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);

        snapshot = session.Snapshot();
        Assert.True(
            snapshot!.AutoImportRegistry()!.IsPreparedForImportingFile(mainFile.FileName(), projectPath, preferences),
            "registry should be prepared after node_modules is deleted");
        // The node_modules bucket should be removed entirely, not left behind as an
        // empty bucket.
        Assert.Empty(AutoImportStats(session).NodeModulesBuckets);
    }

    // registry_test.go:280-321 — "deleting node_modules alongside a package.json change
    // removes the bucket".
    [Fact]
    public void TestRegistryLifecycle_DeletingNodeModulesAlongsidePackageJsonChangeRemovesTheBucket()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 1);
        global::Ts.Project.Session session = fixture.Session();
        global::Ts.TestUtil.ProjectTestUtil.SessionUtils sessionUtils = fixture.Utils();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);
        FileHandle packageJSON = project.PackageJSONFile();
        RequestContext ctx = RequestContext.Background;

        UserPreferences preferences = UserPreferences.NewDefaultUserPreferences();
        preferences.IncludeCompletionsForModuleExports = Tristate.True;
        preferences.IncludeCompletionsForImportStatements = Tristate.True;

        session.DidOpenFile(ctx, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);

        global::Ts.Project.Snapshot? snapshot = session.Snapshot();
        global::Ts.Project.Project? defaultProject = snapshot!.GetDefaultProject(mainFile.URI());
        Assert.True(defaultProject != null);
        Path projectPath = defaultProject!.ConfigFilePath();
        Assert.Single(AutoImportStats(session).NodeModulesBuckets);

        // In a single changeset, edit package.json AND delete node_modules. The
        // package.json change must not prevent the now-missing node_modules bucket
        // from being removed.
        sessionUtils.FS().WriteFile(packageJSON.FileName(), "{\"name\": \"app\", \"dependencies\": {}}");
        string nodeModulesDir = TsPathApi.CombinePaths(project.Root(), "node_modules");
        sessionUtils.FS().Remove(nodeModulesDir);
        session.DidChangeWatchedFiles(ctx,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = packageJSON.URI(),
            },
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(nodeModulesDir),
            },
        ]);

        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);

        snapshot = session.Snapshot();
        Assert.True(snapshot!.AutoImportRegistry()!.IsPreparedForImportingFile(mainFile.FileName(), projectPath, preferences));
        Assert.Empty(AutoImportStats(session).NodeModulesBuckets);
    }

    // registry_test.go:323-350 — "deleting a package directory inside node_modules
    // invalidates the bucket".
    [Fact]
    public void TestRegistryLifecycle_DeletingAPackageDirectoryInsideNodeModulesInvalidatesTheBucket()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 1);
        global::Ts.Project.Session session = fixture.Session();
        global::Ts.TestUtil.ProjectTestUtil.SessionUtils sessionUtils = fixture.Utils();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);
        NodeModulesPackageHandle nodePackage = project.NodeModules()[0];
        RequestContext ctx = RequestContext.Background;

        session.DidOpenFile(ctx, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);
        Assert.True(SingleBucket(AutoImportStats(session).NodeModulesBuckets).ExportCount > 0);

        // Delete just the package directory, leaving node_modules itself in place. The
        // package's files are read transiently by the registry, so they are never tracked
        // in diskFiles/diskDirectories; only the directory deletion event is reported, and
        // it must survive snapshotfs filtering to invalidate the bucket.
        sessionUtils.FS().Remove(nodePackage.Directory);
        session.DidChangeWatchedFiles(ctx,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(nodePackage.Directory),
            },
        ]);

        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);
        Assert.Equal(0, SingleBucket(AutoImportStats(session).NodeModulesBuckets).ExportCount);
    }

    // registry_test.go:352-414 — "node_modules bucket dependency selection changes with
    // open files".
    [Fact]
    public void TestRegistryLifecycle_NodeModulesBucketDependencySelectionChangesWithOpenFiles()
    {
        const string monorepoRoot = "/home/src/monorepo";
        string packageADir = TsPathApi.CombinePaths(monorepoRoot, "packages", "a");
        string monorepoIndex = TsPathApi.CombinePaths(monorepoRoot, "index.js");
        string packageAIndex = TsPathApi.CombinePaths(packageADir, "index.js");

        MonorepoFixture fixture = AutoImportFixtures.SetupMonorepoLifecycleSession(new MonorepoSetupConfig
        {
            Root = monorepoRoot,
            MonorepoPackageTemplate = new MonorepoPackageTemplate
            {
                Name = "monorepo",
                NodeModuleNames = ["pkg1", "pkg2", "pkg3"],
                DependencyNames = ["pkg1"],
            },
            Packages =
            [
                new MonorepoPackageConfig
                {
                    FileCount = 0,
                    MonorepoPackageTemplate = new MonorepoPackageTemplate
                    {
                        Name = "a",
                        DependencyNames = ["pkg1", "pkg2"],
                    },
                },
            ],
            ExtraFiles =
            [
                new TextFileSpec { Path = monorepoIndex, Content = "export const monorepoIndex = 1;\n" },
                new TextFileSpec { Path = packageAIndex, Content = "export const pkgA = 2;\n" },
            ],
        });
        global::Ts.Project.Session session = fixture.Session();
        FileHandle monorepoHandle = fixture.ExtraFile(monorepoIndex);
        FileHandle packageAHandle = fixture.ExtraFile(packageAIndex);

        RequestContext ctx = RequestContext.Background;

        // Open monorepo root file: expect dependencies restricted to pkg1
        session.DidOpenFile(ctx, monorepoHandle.URI(), 1, monorepoHandle.Content(), LanguageKind.JavaScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, monorepoHandle.URI());
        Assert.Null(err);
        CacheStats stats = AutoImportStats(session);
        Assert.True(SingleBucket(stats.NodeModulesBuckets).DependencyNames!.Equals(global::Ts.Collections.Set.FromItems("pkg1")));

        // Open package-a file: pkg2 should be added to existing bucket
        session.DidOpenFile(ctx, packageAHandle.URI(), 1, packageAHandle.Content(), LanguageKind.JavaScript.Value!);
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, packageAHandle.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        Assert.True(SingleBucket(stats.NodeModulesBuckets).DependencyNames!.Equals(global::Ts.Collections.Set.FromItems("pkg1", "pkg2")));

        // Close package-a file; only monorepo bucket should remain
        session.DidCloseFile(ctx, packageAHandle.URI());
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, monorepoHandle.URI());
        Assert.Null(err);
        stats = AutoImportStats(session);
        Assert.True(SingleBucket(stats.NodeModulesBuckets).DependencyNames!.Equals(global::Ts.Collections.Set.FromItems("pkg1")));

        // Close monorepo file; no node_modules buckets should remain
        session.DidCloseFile(ctx, monorepoHandle.URI());
        session.DidOpenFile(ctx, (DocumentUri)"untitled:Untitled-1", 0, "", LanguageKind.TypeScript.Value!);
        (_, err) = session.GetLanguageService(ctx, (DocumentUri)"untitled:Untitled-1");
        Assert.Null(err);
        stats = AutoImportStats(session);
        Assert.Empty(stats.NodeModulesBuckets);
    }

    // registry_test.go:416-493 — "node_modules bucket includes resolved packages from all
    // projects".
    [Fact]
    public void TestRegistryLifecycle_NodeModulesBucketIncludesResolvedPackagesFromAllProjects()
    {
        // This test verifies that when multiple projects share a node_modules directory,
        // the node_modules bucket includes resolved package names from ALL projects,
        // not just the currently requested file's project.
        //
        // Scenario:
        // - Two separate projects (project-a and project-b) share a root node_modules
        // - pkg-listed is in both package.json dependencies
        // - pkg-unlisted is NOT in any package.json, but project-a imports it directly
        // - When requesting completions for project-b, pkg-unlisted should still be in
        //   the node_modules bucket because project-a's resolved packages are included.
        const string monorepoRoot = "/home/src/cross-project-deps";
        string packageADir = TsPathApi.CombinePaths(monorepoRoot, "packages", "a");
        string packageBDir = TsPathApi.CombinePaths(monorepoRoot, "packages", "b");
        string packageAIndex = TsPathApi.CombinePaths(packageADir, "index.ts");
        string packageBIndex = TsPathApi.CombinePaths(packageBDir, "index.ts");

        MonorepoFixture fixture = AutoImportFixtures.SetupMonorepoLifecycleSession(new MonorepoSetupConfig
        {
            Root = monorepoRoot,
            MonorepoPackageTemplate = new MonorepoPackageTemplate
            {
                Name = "monorepo",
                // Both pkg-listed and pkg-unlisted exist in node_modules
                NodeModuleNames = ["pkg-listed", "pkg-unlisted"],
                // But only pkg-listed is in the root package.json dependencies
                DependencyNames = ["pkg-listed"],
            },
            Packages =
            [
                new MonorepoPackageConfig
                {
                    FileCount = 0,
                    MonorepoPackageTemplate = new MonorepoPackageTemplate
                    {
                        Name = "a",
                        // package-a only lists pkg-listed in its package.json
                        DependencyNames = ["pkg-listed"],
                    },
                },
                new MonorepoPackageConfig
                {
                    FileCount = 0,
                    MonorepoPackageTemplate = new MonorepoPackageTemplate
                    {
                        Name = "b",
                        // package-b also only lists pkg-listed in its package.json
                        DependencyNames = ["pkg-listed"],
                    },
                },
            ],
            ExtraFiles =
            [
                // project-a directly imports pkg-unlisted (not in package.json)
                new TextFileSpec { Path = packageAIndex, Content = "import { pkg_unlisted_value } from \"pkg-unlisted\";\nexport const a = pkg_unlisted_value;\n" },
                // project-b does not import pkg-unlisted
                new TextFileSpec { Path = packageBIndex, Content = "export const b = 1;\n" },
            ],
        });
        global::Ts.Project.Session session = fixture.Session();
        FileHandle packageAHandle = fixture.ExtraFile(packageAIndex);
        FileHandle packageBHandle = fixture.ExtraFile(packageBIndex);

        RequestContext ctx = RequestContext.Background;

        // Open file in project-a (which imports pkg-unlisted)
        session.DidOpenFile(ctx, packageAHandle.URI(), 1, packageAHandle.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, packageAHandle.URI());
        Assert.Null(err);

        // Open file in project-b (which does not import pkg-unlisted)
        session.DidOpenFile(ctx, packageBHandle.URI(), 1, packageBHandle.Content(), LanguageKind.TypeScript.Value!);
        // Request auto-imports for project-b
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, packageBHandle.URI());
        Assert.Null(err);

        // Verify that the node_modules bucket includes pkg-unlisted
        // even though we requested auto-imports for project-b which doesn't list it.
        // This is because project-a imports it directly, and the bucket should include
        // resolved packages from all projects that share the node_modules directory.
        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(nodeModulesBucket.DependencyNames!.Has("pkg-listed"), "pkg-listed should be in dependencies");
        Assert.True(nodeModulesBucket.DependencyNames.Has("pkg-unlisted"), "pkg-unlisted should be in dependencies because project-a imports it");
    }

    // registry_test.go:495-624 — "symlinked monorepo invalidates on source file change".
    [Fact]
    public void TestRegistryLifecycle_SymlinkedMonorepoInvalidatesOnSourceFileChange()
    {
        // This test verifies that when a source file in a symlinked project reference
        // is modified, the node_modules bucket is properly invalidated.
        //
        // Scenario:
        // 1. project-a imports from project-b (symlinked into node_modules)
        // 2. project-b has projectBFunction export
        // 3. Open project-b's source file and delete the export
        // 4. Verify the node_modules bucket is invalidated and rebuilt
        //
        // We also include a regular (non-symlinked) package "other-pkg" to observe
        // whether a change to project-b triggers a full rebuild of the entire bucket
        // or a more granular update.
        const string monorepoRoot = "/home/src/symlinked-monorepo-invalidation";
        string projectADir = TsPathApi.CombinePaths(monorepoRoot, "packages", "project-a");
        string projectBDir = TsPathApi.CombinePaths(monorepoRoot, "packages", "project-b");
        string projectAIndex = TsPathApi.CombinePaths(projectADir, "src", "index.ts");
        string projectBSrcIndex = TsPathApi.CombinePaths(projectBDir, "src", "index.ts");
        string projectBDistIndex = TsPathApi.CombinePaths(projectBDir, "dist", "index.d.ts");
        string otherPkgDir = TsPathApi.CombinePaths(projectADir, "node_modules", "other-pkg");

        var files = new Dictionary<string, object?>
        {
            // project-b: the library package
            [TsPathApi.CombinePaths(projectBDir, "tsconfig.json")] = """
            {
            				"compilerOptions": {
            					"composite": true,
            					"outDir": "./dist",
            					"rootDir": "./src",
            					"declaration": true,
            					"module": "esnext",
            					"strict": true
            				},
            				"include": ["src"]
            			}
            """,
            [TsPathApi.CombinePaths(projectBDir, "package.json")] = """
            {
            				"name": "project-b",
            				"version": "1.0.0",
            				"main": "dist/index.js",
            				"types": "dist/index.d.ts"
            			}
            """,
            [projectBSrcIndex] = "export function projectBFunction(): string { return \"hello\"; }\nexport const projectBValue: number = 42;",
            [projectBDistIndex] = "export declare function projectBFunction(): string;\nexport declare const projectBValue: number;",
            // other-pkg: a regular (non-symlinked) package
            [TsPathApi.CombinePaths(otherPkgDir, "package.json")] = """
            {
            				"name": "other-pkg",
            				"version": "1.0.0",
            				"main": "index.js",
            				"types": "index.d.ts"
            			}
            """,
            [TsPathApi.CombinePaths(otherPkgDir, "index.d.ts")] = "export declare function otherFunction(): void;\nexport declare const otherValue: string;",
            // project-a: the consumer package
            [TsPathApi.CombinePaths(projectADir, "tsconfig.json")] = """
            {
            				"compilerOptions": {
            					"module": "esnext",
            					"strict": true,
            					"outDir": "./dist",
            					"rootDir": "./src"
            				},
            				"include": ["src"],
            				"references": [{ "path": "../project-b" }]
            			}
            """,
            [TsPathApi.CombinePaths(projectADir, "package.json")] = """
            {
            				"name": "project-a",
            				"dependencies": { "project-b": "*", "other-pkg": "*" }
            			}
            """,
            [projectAIndex] = "console.log(\"hello\");\n",
            // Symlink: project-b is accessible via node_modules
            [TsPathApi.CombinePaths(projectADir, "node_modules", "project-b")] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink(projectBDir),
        };

        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(files);
        RequestContext ctx = RequestContext.Background;

        // Open project-a's index file and get initial auto-imports
        DocumentUri projectAURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(projectAIndex);
        string projectAContent = (string)files[projectAIndex]!;
        session.DidOpenFile(ctx, projectAURI, 1, projectAContent, LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, projectAURI);
        Assert.Null(err);

        // Verify initial state: bucket is clean with files
        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        int initialFileCount = nodeModulesBucket.FileCount;
        Assert.False(nodeModulesBucket.State.Dirty());
        Assert.True(initialFileCount > 0);

        // Open project-b's source file
        DocumentUri projectBURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(projectBSrcIndex);
        string projectBContent = (string)files[projectBSrcIndex]!;
        session.DidOpenFile(ctx, projectBURI, 1, projectBContent, LanguageKind.TypeScript.Value!);

        // Modify the file (delete one export)
        const string newProjectBContent = "export const projectBValue: number = 42;";
        session.DidChangeFile(ctx, projectBURI, 2,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = newProjectBContent },
            },
        ]);

        // Check that the node_modules bucket is now dirty
        (_, err) = session.GetLanguageService(ctx, projectAURI);
        Assert.Null(err);
        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(nodeModulesBucket.State.Dirty());

        // Verify that only project-b is marked for update, not other-pkg.
        // This tests that we correctly track which packages need granular updates.
        global::Ts.Collections.Set<string>? dirtyPackages = nodeModulesBucket.State.DirtyPackages();
        Assert.True(dirtyPackages != null, "dirty packages should be tracked");
        Assert.True(dirtyPackages!.Has("project-b"), "project-b should be in dirty packages");
        Assert.True(!dirtyPackages.Has("other-pkg"), "other-pkg should NOT be in dirty packages");
        Assert.Equal(1, dirtyPackages.Len());

        // Rebuild by requesting auto-imports again.
        // NOTE: Currently the entire bucket is rebuilt, not just the dirty packages.
        // The dirtyPackages tracking is in place for future granular update implementation.
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, projectAURI);
        Assert.Null(err);

        // Verify bucket is clean again after rebuild
        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(nodeModulesBucket.State.Dirty());
    }

    // registry_test.go:626-781 — "pnpm-style symlinks only grant granular updates to
    // workspace packages".
    [Fact]
    public void TestRegistryLifecycle_PnpmStyleSymlinksOnlyGrantGranularUpdatesToWorkspacePackages()
    {
        // In pnpm, every package in node_modules is symlinked — registry packages
        // are symlinked into node_modules/.pnpm/<pkg>@<version>/node_modules/<pkg>.
        // Only local workspace packages (whose realpaths are outside node_modules
        // and within the workspace root) should be eligible for granular updates.
        // Registry packages should trigger full bucket rebuilds.
        const string monorepoRoot = "/home/src/pnpm-monorepo";
        string projectADir = TsPathApi.CombinePaths(monorepoRoot, "packages", "project-a");
        string projectBDir = TsPathApi.CombinePaths(monorepoRoot, "packages", "project-b");
        string projectAIndex = TsPathApi.CombinePaths(projectADir, "src", "index.ts");
        string projectBSrcIndex = TsPathApi.CombinePaths(projectBDir, "src", "index.ts");
        string projectBDistIndex = TsPathApi.CombinePaths(projectBDir, "dist", "index.d.ts");

        // Simulated pnpm virtual store for a registry package (inside project-a's node_modules).
        // In real pnpm, this would be node_modules/.pnpm/other-pkg@1.0.0/node_modules/other-pkg,
        // but we use a simplified path to avoid creating nested node_modules buckets in tests.
        string pnpmStoreDir = TsPathApi.CombinePaths(projectADir, "node_modules", ".pnpm-store", "other-pkg@1.0.0");
        string otherPkgIndex = TsPathApi.CombinePaths(pnpmStoreDir, "index.d.ts");

        var files = new Dictionary<string, object?>
        {
            // project-b: a local workspace package
            [TsPathApi.CombinePaths(projectBDir, "tsconfig.json")] = """
            {
            				"compilerOptions": {
            					"composite": true,
            					"outDir": "./dist",
            					"rootDir": "./src",
            					"declaration": true,
            					"module": "esnext",
            					"strict": true
            				},
            				"include": ["src"]
            			}
            """,
            [TsPathApi.CombinePaths(projectBDir, "package.json")] = """
            {
            				"name": "project-b",
            				"version": "1.0.0",
            				"main": "dist/index.js",
            				"types": "dist/index.d.ts"
            			}
            """,
            [projectBSrcIndex] = "export function projectBFunction(): string { return \"hello\"; }\nexport const projectBValue: number = 42;",
            [projectBDistIndex] = "export declare function projectBFunction(): string;\nexport declare const projectBValue: number;",
            // other-pkg: a registry package in pnpm's virtual store
            [TsPathApi.CombinePaths(pnpmStoreDir, "package.json")] = """
            {
            				"name": "other-pkg",
            				"version": "1.0.0",
            				"main": "index.js",
            				"types": "index.d.ts"
            			}
            """,
            [otherPkgIndex] = "export declare function otherFunction(): void;\nexport declare const otherValue: string;",
            // project-a: the consumer package
            [TsPathApi.CombinePaths(projectADir, "tsconfig.json")] = """
            {
            				"compilerOptions": {
            					"module": "esnext",
            					"strict": true,
            					"outDir": "./dist",
            					"rootDir": "./src"
            				},
            				"include": ["src"],
            				"references": [{ "path": "../project-b" }]
            			}
            """,
            [TsPathApi.CombinePaths(projectADir, "package.json")] = """
            {
            				"name": "project-a",
            				"dependencies": { "project-b": "*", "other-pkg": "*" }
            			}
            """,
            [projectAIndex] = "console.log(\"hello\");\n",
            // Symlink: local workspace package (realpath outside node_modules)
            [TsPathApi.CombinePaths(projectADir, "node_modules", "project-b")] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink(projectBDir),
            // Symlink: pnpm-style registry package (realpath inside node_modules/.pnpm)
            [TsPathApi.CombinePaths(projectADir, "node_modules", "other-pkg")] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink(pnpmStoreDir),
        };

        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.SetupWithOptions(files, new global::Ts.Project.SessionOptions
        {
            CurrentDirectory = monorepoRoot,
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = true,
            LoggingEnabled = true,
            PushDiagnosticsEnabled = true,
        });
        RequestContext ctx = RequestContext.Background;

        // Open project-a's index file and build auto-imports
        DocumentUri projectAURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(projectAIndex);
        string projectAContent = (string)files[projectAIndex]!;
        session.DidOpenFile(ctx, projectAURI, 1, projectAContent, LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, projectAURI);
        Assert.Null(err);

        // Verify initial state: bucket is clean
        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(nodeModulesBucket.State.Dirty());

        // Modify project-b's source file (local workspace package)
        DocumentUri projectBURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(projectBSrcIndex);
        string projectBContent = (string)files[projectBSrcIndex]!;
        session.DidOpenFile(ctx, projectBURI, 1, projectBContent, LanguageKind.TypeScript.Value!);
        session.DidChangeFile(ctx, projectBURI, 2,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = "export const projectBValue: number = 42;" },
            },
        ]);

        // project-b should get a granular update (tracked in dirtyPackages)
        (_, err) = session.GetLanguageService(ctx, projectAURI);
        Assert.Null(err);
        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(nodeModulesBucket.State.Dirty());
        global::Ts.Collections.Set<string>? dirtyPackages = nodeModulesBucket.State.DirtyPackages();
        Assert.True(dirtyPackages != null, "dirty packages should be tracked for workspace package");
        Assert.True(dirtyPackages!.Has("project-b"), "project-b should be in dirty packages");
        Assert.Equal(1, dirtyPackages.Len());

        // Rebuild to clear dirty state
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, projectAURI);
        Assert.Null(err);
        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(nodeModulesBucket.State.Dirty());

        // Now modify other-pkg (pnpm registry package, realpath inside node_modules/.pnpm)
        DocumentUri otherPkgURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(otherPkgIndex);
        string otherPkgContent = (string)files[otherPkgIndex]!;
        session.DidOpenFile(ctx, otherPkgURI, 1, otherPkgContent, LanguageKind.TypeScript.Value!);
        session.DidChangeFile(ctx, otherPkgURI, 2,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = "export declare function otherFunction(): void;" },
            },
        ]);

        // other-pkg should trigger a full rebuild (multipleFilesDirty), not a granular update.
        // Read stats from the request-time snapshot (held by ref via WithLanguageServiceAndSnapshot)
        // rather than session.Snapshot(), so a background auto-import warm task — which would
        // walk otherPkgURI's ancestor chain through project-a's node_modules and rebuild that
        // bucket — cannot race with the assertion by adopting a freshly-cleaned snapshot
        // before we read it.
        (_, err) = session.WithLanguageServiceAndSnapshot(ctx, projectAURI, (_, snapshot) =>
        {
            stats = snapshot.AutoImportRegistry()!.GetCacheStats();
            return (null, null);
        });
        Assert.Null(err);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(nodeModulesBucket.State.Dirty());
        dirtyPackages = nodeModulesBucket.State.DirtyPackages();
        // A full rebuild means dirtyPackages is nil (multipleFilesDirty takes precedence)
        // or dirtyPackages doesn't contain "other-pkg" as a granular entry
        if (dirtyPackages != null)
        {
            Assert.True(!dirtyPackages.Has("other-pkg"), "other-pkg should NOT be in dirty packages (should trigger full rebuild)");
        }
    }

    // registry_test.go:783-835 — "changed fileExcludePatterns triggers bucket rebuild".
    [Fact]
    public void TestRegistryLifecycle_ChangedFileExcludePatternsTriggersBucketRebuild()
    {
        Fixture fixture = AutoImportFixtures.SetupLifecycleSession(lifecycleProjectRoot, 1);
        global::Ts.Project.Session session = fixture.Session();
        ProjectHandle project = fixture.SingleProject();
        ProjectFileHandle mainFile = project.File(0);

        RequestContext ctx = RequestContext.Background;

        // Open file and build auto-imports initially
        session.DidOpenFile(ctx, mainFile.URI(), 1, mainFile.Content(), LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);

        // Verify buckets are clean after initial build
        CacheStats stats = AutoImportStats(session);
        BucketStats projectBucket = SingleBucket(stats.ProjectBuckets);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.False(projectBucket.State.Dirty());
        Assert.False(nodeModulesBucket.State.Dirty());

        // IsPreparedForImportingFile should return true with no exclude patterns
        global::Ts.Project.Snapshot? snapshot = session.Snapshot();
        global::Ts.Project.Project? defaultProject = snapshot!.GetDefaultProject(mainFile.URI());
        Assert.True(defaultProject != null);
        Path projectPath = defaultProject!.ConfigFilePath();
        UserPreferences preferences = UserPreferences.NewDefaultUserPreferences();
        preferences.IncludeCompletionsForModuleExports = Tristate.True;
        preferences.IncludeCompletionsForImportStatements = Tristate.True;
        bool isPrepared = snapshot.AutoImportRegistry()!.IsPreparedForImportingFile(mainFile.FileName(), projectPath, preferences);
        Assert.True(isPrepared);

        // Change the file exclude patterns preference
        UserPreferences newPreferences = UserPreferences.NewDefaultUserPreferences();
        newPreferences.IncludeCompletionsForModuleExports = Tristate.True;
        newPreferences.IncludeCompletionsForImportStatements = Tristate.True;
        newPreferences.AutoImportFileExcludePatterns = ["**/node_modules/**/*.d.ts"];
        session.Configure(newPreferences);

        // IsPreparedForImportingFile should return false since exclude patterns changed
        global::Ts.Project.Snapshot? snapshot2 = session.Snapshot();
        bool isPrepared2 = snapshot2!.AutoImportRegistry()!.IsPreparedForImportingFile(mainFile.FileName(), projectPath, newPreferences);
        Assert.True(!isPrepared2);

        // After GetCurrentLanguageServiceWithAutoImports, buckets should be rebuilt
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, mainFile.URI());
        Assert.Null(err);

        // IsPreparedForImportingFile should return true now that buckets are rebuilt
        global::Ts.Project.Snapshot? snapshot3 = session.Snapshot();
        bool isPrepared3 = snapshot3!.AutoImportRegistry()!.IsPreparedForImportingFile(mainFile.FileName(), projectPath, newPreferences);
        Assert.True(isPrepared3, "IsPreparedForImportingFile should return true after bucket rebuild with new fileExcludePatterns");
    }

    // registry_test.go:837-895 — "dedupes packages that resolve to same realpath across
    // ancestor node_modules buckets".
    [Fact]
    public void TestRegistryLifecycle_DedupesPackagesThatResolveToSameRealpathAcrossAncestorNodeModulesBuckets()
    {
        const string repoRoot = "/home/src/autoimport-realpath-dedupe";
        string appDir = TsPathApi.CombinePaths(repoRoot, "apps", "web");
        string sharedPkgDir = TsPathApi.CombinePaths(repoRoot, "node_modules", "shared");
        string appIndex = TsPathApi.CombinePaths(appDir, "src", "index.ts");

        var files = new Dictionary<string, object?>
        {
            [TsPathApi.CombinePaths(repoRoot, "package.json")] = """
            {
            				"name": "repo-root",
            				"private": true,
            				"dependencies": { "shared": "*" }
            			}
            """,
            [TsPathApi.CombinePaths(repoRoot, "tsconfig.json")] = """
            {
            				"compilerOptions": {
            					"module": "esnext",
            					"target": "esnext",
            					"strict": true
            				},
            				"include": ["apps/**/*"]
            			}
            """,
            [TsPathApi.CombinePaths(appDir, "package.json")] = """
            {
            				"name": "web",
            				"private": true,
            				"dependencies": { "shared": "*" }
            			}
            """,
            [TsPathApi.CombinePaths(appDir, "tsconfig.json")] = """
            {
            				"compilerOptions": {
            					"module": "esnext",
            					"target": "esnext",
            					"strict": true
            				},
            				"include": ["src"]
            			}
            """,
            [appIndex] = "export const app = 1;\n",
            [TsPathApi.CombinePaths(sharedPkgDir, "package.json")] = """
            {
            				"name": "shared",
            				"version": "1.0.0",
            				"types": "index.d.ts"
            			}
            """,
            [TsPathApi.CombinePaths(sharedPkgDir, "index.d.ts")] = "export declare const sharedValue: 1;\n",
            [TsPathApi.CombinePaths(appDir, "node_modules", "shared")] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink(sharedPkgDir),
        };

        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(files);

        RequestContext ctx = RequestContext.Background;
        DocumentUri appURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(appIndex);
        session.DidOpenFile(ctx, appURI, 1, (string)files[appIndex]!, LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, appURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        Assert.Equal(2, stats.NodeModulesBuckets.Count);
        Assert.Equal(1, stats.UniquePackageCount);
    }

    // registry_test.go:898-981 — TestHiddenDirectoriesInNodeModules.
    // registry_test.go:900-980 — "deep import through subdirectory package.json in hidden
    // store".
    [Fact]
    public void TestHiddenDirectoriesInNodeModules_DeepImportThroughSubdirectoryPackageJsonInHiddenStore()
    {
        // Simulates a realistic scenario where:
        // 1. A package is symlinked from node_modules into a hidden store directory
        // 2. The user does a deep import like `import { debug } from "some-pkg/debug"`
        // 3. The package has NO "exports" field, so resolution uses the nested
        //    package.json at some-pkg/debug/package.json
        // 4. That nested package.json has no "name" or "version" (just {"main":"..."}),
        //    which is completely normal for subdirectory package.json files
        // 5. getPackageId uses the nested package.json (not the root), fails to get
        //    a name/version, so PackageId is empty
        // 6. collectPackageNames falls through to GetPackageNameFromDirectory, which
        //    extracts ".yarn-store" from the realpath after /node_modules/
        // See https://github.com/microsoft/typescript-go/issues/2780
        const string projectRoot = "/home/src/fuse-project";
        string storeDir = projectRoot + "/node_modules/.yarn-store";
        string pkgStoreDir = storeDir + "/some-pkg-npm-1.0.0-abc123/package";

        var files = new Dictionary<string, object?>
        {
            [projectRoot + "/tsconfig.json"] = """
            {
            				"compilerOptions": {
            					"module": "commonjs",
            					"target": "es2020",
            					"strict": true
            				}
            			}
            """,
            [projectRoot + "/package.json"] = """
            {
            				"name": "test-project",
            				"dependencies": {
            					"some-pkg": "*",
            					"real-package": "*"
            				}
            			}
            """,
            // Deep import: "some-pkg/debug" — resolves through the subdirectory package.json
            [projectRoot + "/index.ts"] = "import { debug } from \"some-pkg/debug\";",

            // Real package that should be indexed normally
            [projectRoot + "/node_modules/real-package/package.json"] = "{\"name\":\"real-package\",\"version\":\"1.0.0\",\"types\":\"index.d.ts\"}",
            [projectRoot + "/node_modules/real-package/index.d.ts"] = "export declare const realExport: number;\n",

            // Symlink: node_modules/some-pkg -> .yarn-store/.../package/
            [projectRoot + "/node_modules/some-pkg"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink(pkgStoreDir),

            // Root package.json with name+version but NO "exports" field.
            // This is key: without exports, the resolver resolves deep imports
            // through the subdirectory package.json, not the root.
            [pkgStoreDir + "/package.json"] = "{\"name\":\"some-pkg\",\"version\":\"1.0.0\",\"types\":\"index.d.ts\"}",
            [pkgStoreDir + "/index.d.ts"] = "export declare const something: number;\n",
            // Subdirectory package.json for the deep import — no name or version,
            // just a main field. This is normal for packages that expose subpaths
            // without using the "exports" field.
            [pkgStoreDir + "/debug/package.json"] = "{\"main\":\"./debug.js\",\"types\":\"./debug.d.ts\"}",
            [pkgStoreDir + "/debug/debug.d.ts"] = "export declare function debug(msg: string): void;\n",
            [pkgStoreDir + "/debug/debug.js"] = "exports.debug = function(msg) { console.log(msg); };\n",

            // Other content in the hidden store that should never be crawled
            [storeDir + "/other-pkg-npm-2.0.0-def456/package/package.json"] = "{\"name\":\"other-pkg\",\"version\":\"1.0.0\",\"types\":\"index.d.ts\"}",
            [storeDir + "/other-pkg-npm-2.0.0-def456/package/index.d.ts"] = "export declare const other: string;\n",
        };

        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(files);

        RequestContext ctx = RequestContext.Background;
        DocumentUri indexURI = (DocumentUri)("file://" + projectRoot + "/index.ts");
        session.DidOpenFile(ctx, indexURI, 1, (string)files[projectRoot + "/index.ts"]!, LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);

        // .yarn-store must not appear as a dependency name.
        // If it does, extractPackages will try to process the entire hidden
        // directory (ReadDirectory **/*), which is the CPU/memory blowup.
        Assert.True(nodeModulesBucket.DependencyNames != null, "DependencyNames should not be nil");
        foreach (string name in nodeModulesBucket.DependencyNames!.Keys())
        {
            Assert.True(name[0] != '.', $"hidden directory {name} should not appear as a dependency name");
        }
    }

    // registry_test.go:983-1125 — TestAutoImportEntrypointDirectorySearch.
    // registry_test.go:1012-1028 — "default limits to main entrypoint".
    [Fact]
    public void TestAutoImportEntrypointDirectorySearch_DefaultLimitsToMainEntrypoint()
    {
        (global::Ts.Project.Session session, Dictionary<string, object?> files, DocumentUri indexURI) = SetupEntrypointSearchSession();
        RequestContext ctx = RequestContext.Background;

        session.DidOpenFile(ctx, indexURI, 1, (string)files[entrypointSearchIndex]!, LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        // Without the preference, only the main entrypoint (index.d.ts) should be found
        Assert.Equal(1, nodeModulesBucket.FileCount);
    }

    // registry_test.go:1030-1050 — "autoImportEntrypointDirectorySearch enables all files".
    [Fact]
    public void TestAutoImportEntrypointDirectorySearch_AutoImportEntrypointDirectorySearchEnablesAllFiles()
    {
        (global::Ts.Project.Session session, Dictionary<string, object?> files, DocumentUri indexURI) = SetupEntrypointSearchSession();

        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.AutoImportEntrypointDirectorySearch = Tristate.True;
        session.Configure(prefs);

        RequestContext ctx = RequestContext.Background;
        session.DidOpenFile(ctx, indexURI, 1, (string)files[entrypointSearchIndex]!, LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        // With the preference, all 4 .d.ts files should be found via directory search
        Assert.True(nodeModulesBucket.FileCount >= 4, $"expected at least 4 files from directory search, got {nodeModulesBucket.FileCount}");
    }

    // registry_test.go:1052-1091 — "changing preference triggers rebuild".
    [Fact]
    public void TestAutoImportEntrypointDirectorySearch_ChangingPreferenceTriggersRebuild()
    {
        (global::Ts.Project.Session session, Dictionary<string, object?> files, DocumentUri indexURI) = SetupEntrypointSearchSession();

        RequestContext ctx = RequestContext.Background;
        session.DidOpenFile(ctx, indexURI, 1, (string)files[entrypointSearchIndex]!, LanguageKind.TypeScript.Value!);

        // Build auto-imports with default preferences (directory search disabled)
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.Equal(1, nodeModulesBucket.FileCount);

        // Now enable directory search
        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.AutoImportEntrypointDirectorySearch = Tristate.True;
        session.Configure(prefs);

        // Registry should report not prepared (preference changed)
        global::Ts.Project.Snapshot? snapshot = session.Snapshot();
        global::Ts.Project.Project? defaultProject = snapshot!.GetDefaultProject(indexURI);
        Assert.True(defaultProject != null);
        Path projectPath = defaultProject!.ConfigFilePath();
        bool isPrepared = snapshot.AutoImportRegistry()!.IsPreparedForImportingFile(
            entrypointSearchIndex, projectPath, prefs);
        Assert.True(!isPrepared, "registry should not be prepared after preference change");

        // Rebuild
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(nodeModulesBucket.FileCount >= 4, $"expected at least 4 files after rebuild with directory search enabled, got {nodeModulesBucket.FileCount}");
    }

    // registry_test.go:1093-1124 — "deep import from program update enables recursive
    // search for that package".
    [Fact]
    public void TestAutoImportEntrypointDirectorySearch_DeepImportFromProgramUpdateEnablesRecursiveSearchForThatPackage()
    {
        (global::Ts.Project.Session session, Dictionary<string, object?> files, DocumentUri indexURI) = SetupEntrypointSearchSession();

        RequestContext ctx = RequestContext.Background;
        session.DidOpenFile(ctx, indexURI, 1, (string)files[entrypointSearchIndex]!, LanguageKind.TypeScript.Value!);

        // Initial build with top-level import only ("my-pkg", not a deep import)
        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.Equal(1, nodeModulesBucket.FileCount);

        // Now update the program to add a deep import from the same package
        const string newContent = "import { main } from \"my-pkg\";\nimport { deep } from \"my-pkg/nested/deep\";\n";
        session.DidChangeFile(ctx, indexURI, 2,
        [
            new global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new global::Ts.LsProto.TextDocumentContentChangeWholeDocument { Text = newContent },
            },
        ]);

        // After the program update, auto-imports should detect the deep import and
        // enable recursive directory search for my-pkg, finding all .d.ts files.
        (_, err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        stats = AutoImportStats(session);
        nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.True(nodeModulesBucket.FileCount >= 4, $"expected at least 4 files after deep import triggers recursive search, got {nodeModulesBucket.FileCount}");
    }

    private const string entrypointSearchProjectRoot = "/home/src/entrypoint-search";
    private const string entrypointSearchIndex = entrypointSearchProjectRoot + "/index.ts";

    // registry_test.go:986-1010 — the `files` map shared by all
    // TestAutoImportEntrypointDirectorySearch subtests.
    private static (global::Ts.Project.Session Session, Dictionary<string, object?> Files, DocumentUri IndexUri) SetupEntrypointSearchSession()
    {
        const string projectRoot = entrypointSearchProjectRoot;
        string nodeModulesDir = projectRoot + "/node_modules";
        string pkgDir = nodeModulesDir + "/my-pkg";

        var files = new Dictionary<string, object?>
        {
            [projectRoot + "/tsconfig.json"] = """
            {
            				"compilerOptions": {
            					"module": "commonjs",
            					"target": "es2020"
            				}
            			}
            """,
            [projectRoot + "/package.json"] = """
            {
            				"name": "test-project",
            				"dependencies": { "my-pkg": "*" }
            			}
            """,
            [projectRoot + "/index.ts"] = "import { main } from \"my-pkg\";",
            // Package with NO "exports" field and multiple files.
            // Without the preference, only the main entrypoint is found.
            // With the preference, directory search finds all .d.ts files.
            [pkgDir + "/package.json"] = "{\"name\":\"my-pkg\",\"version\":\"1.0.0\",\"types\":\"index.d.ts\"}",
            [pkgDir + "/index.d.ts"] = "export declare const main: number;\n",
            [pkgDir + "/extra.d.ts"] = "export declare const extra: string;\n",
            [pkgDir + "/nested/deep.d.ts"] = "export declare const deep: boolean;\n",
            [pkgDir + "/nested/deeper.d.ts"] = "export declare const deeper: boolean;\n",
        };

        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(files);
        DocumentUri indexURI = (DocumentUri)("file://" + projectRoot + "/index.ts");
        return (session, files, indexURI);
    }

    // registry_test.go:1132-1165 — TestUpdateIndexesConcurrentMapSafety.
    [Fact]
    public void TestUpdateIndexesConcurrentMapSafety()
    {
        const string projectRoot = "/home/src/autoimport-fallback-race";
        const int packageCount = 40;

        var files = new Dictionary<string, object?>
        {
            [projectRoot + "/tsconfig.json"] = """
            {
            				"compilerOptions": { "module": "esnext", "target": "esnext", "strict": true }
            			}
            """,
            [projectRoot + "/index.ts"] = "export {};\n",
        };
        for (int i = 0; i < packageCount; i++)
        {
            string pkgDir = $"{projectRoot}/node_modules/pkg{i}";
            files[pkgDir + "/package.json"] = $"{{\"name\":\"pkg{i}\",\"version\":\"1.0.0\",\"main\":\"index.js\"}}";
            files[pkgDir + "/index.js"] = "module.exports = {};\n";
            string typesDir = $"{projectRoot}/node_modules/@types/pkg{i}";
            files[typesDir + "/package.json"] = $"{{\"name\":\"@types/pkg{i}\",\"version\":\"1.0.0\",\"types\":\"index.d.ts\"}}";
            files[typesDir + "/index.d.ts"] = $"export declare const foo{i}: number;\n";
        }

        (global::Ts.Project.Session session, _) = global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(files);

        RequestContext ctx = RequestContext.Background;
        DocumentUri indexURI = (DocumentUri)("file://" + projectRoot + "/index.ts");
        session.DidOpenFile(ctx, indexURI, 1, "export {};\n", LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, indexURI);
        Assert.Null(err);

        CacheStats stats = AutoImportStats(session);
        BucketStats nodeModulesBucket = SingleBucket(stats.NodeModulesBuckets);
        Assert.Equal(packageCount, nodeModulesBucket.ExportCount);
    }
}
