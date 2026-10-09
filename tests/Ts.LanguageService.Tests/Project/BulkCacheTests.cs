// Ported from tsgo internal/project/bulkcache_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one xUnit class; t.Run subtests → [Fact]s named
//    Parent_Subtest (Parent_Mid_Inner for the doubly nested cases).
//  * gotest.tools assert.Equal(t, actual, expected, msg) → Assert.Equal(expected, actual)
//    (argument order swapped); xUnit's Equal/Same/Null have no message overload, so the
//    Go message strings are kept as comments on the assertion where they can't be passed.
//  * Go `err := utils.FS().WriteFile(...); assert.NilError(t, err)` → a direct WriteFile
//    call: the C# IFileSystem.WriteFile throws on failure (05 §4), so a failed write
//    fails the test exactly where Go's err assertion would.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background.

using System;
using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using FileChangeType = global::Ts.LsProto.FileChangeType;
using FileEvent = global::Ts.LsProto.FileEvent;
using LanguageKind = global::Ts.LsProto.LanguageKind;

namespace Ts.Project.Tests;

public sealed class BulkCacheTests
{
    // bulkcache_test.go:24-40 — baseFiles.
    private static Dictionary<string, object?> BaseFiles()
    {
        return new Dictionary<string, object?>
        {
            ["/project/tsconfig.json"] = @"{
				""compilerOptions"": {
					""strict"": true,
					""target"": ""es2015"",
					""types"": [""node""]
				},
				""include"": [""src/**/*""]
			}",
            ["/project/src/index.ts"] = @"import { helper } from ""./helper""; console.log(helper);",
            ["/project/src/helper.ts"] = @"export const helper = ""test"";",
            ["/project/src/utils/lib.ts"] = @"export function util() { return ""util""; }",

            ["/project/node_modules/@types/node/index.d.ts"] = @"import ""./fs""; import ""./console"";",
            ["/project/node_modules/@types/node/fs.d.ts"] = @"",
            ["/project/node_modules/@types/node/console.d.ts"] = @"",
        };
    }

    // bulkcache_test.go:44-94 — the `test` closure of the "large number of node_modules
    // changes invalidates only node_modules cache" subtest.
    private static void NodeModulesScenario(Dictionary<string, object?> baseFiles, FileEvent[] fileEvents, bool expectNodeModulesInvalidation)
    {
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(baseFiles);

        // Open a file to create the project
        session.DidOpenFile(RequestContext.Background, "file:///project/src/index.ts", 1, (string)baseFiles["/project/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Get initial snapshot and verify config
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/index.ts");
        Assert.Null(err);
        Assert.Equal(ScriptTarget.ES2015, ls!.GetProgram().Options().Target);

        var snapshotBefore = session.Snapshot()!;
        var configBefore = snapshotBefore.ConfigFileRegistry;

        // Update tsconfig.json on disk to test that configs don't get reloaded
        utils.FS().WriteFile("/project/tsconfig.json", @"{
				""compilerOptions"": {
					""strict"": true,
					""target"": ""esnext"",
					""types"": [""node""]
				},
				""include"": [""src/**/*""]
			}");
        // Update fs.d.ts in node_modules
        utils.FS().WriteFile("/project/node_modules/@types/node/fs.d.ts", "new text");

        // Process the excessive node_modules changes
        session.DidChangeWatchedFiles(RequestContext.Background, fileEvents);

        // Get language service again to trigger snapshot update
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/index.ts");
        Assert.Null(err);

        var snapshotAfter = session.Snapshot()!;
        var configAfter = snapshotAfter.ConfigFileRegistry;

        // Config should NOT have been reloaded (target should remain ES2015, not esnext)
        // Go message: "Config should not have been reloaded for node_modules-only changes"
        Assert.Equal(ScriptTarget.ES2015, ls!.GetProgram().Options().Target);

        // Config registry should be the same instance (no configs reloaded). Go's
        // assert.Equal deep-compares; the implementation reuses the instance, which the
        // Go message states ("same instance"), so identity is the intent.
        // Go message: "Config registry should not have changed for node_modules-only changes"
        Assert.Same(configBefore, configAfter);

        string fsDtsText = snapshotAfter.GetFile("/project/node_modules/@types/node/fs.d.ts")!.Content();
        if (expectNodeModulesInvalidation)
        {
            Assert.Equal("new text", fsDtsText);
        }
        else
        {
            Assert.Equal("", fsDtsText);
        }
    }

    // bulkcache_test.go:121-157 — the `test` closure of the "large number of changes
    // outside node_modules" subtest.
    private static void OutsideNodeModulesScenario(Dictionary<string, object?> baseFiles, FileEvent[] fileEvents, bool expectConfigReload)
    {
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(baseFiles);

        // Open a file to create the project
        session.DidOpenFile(RequestContext.Background, "file:///project/src/index.ts", 1, (string)baseFiles["/project/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Get initial state
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/index.ts");
        Assert.Null(err);
        Assert.Equal(ScriptTarget.ES2015, ls!.GetProgram().Options().Target);

        // Update tsconfig.json on disk
        utils.FS().WriteFile("/project/tsconfig.json", @"{
				""compilerOptions"": {
					""strict"": true,
					""target"": ""esnext"",
					""types"": [""node""]
				},
				""include"": [""src/**/*""]
			}");
        // Add root file
        utils.FS().WriteFile("/project/src/rootFile.ts", @"console.log(""root file"")");

        session.DidChangeWatchedFiles(RequestContext.Background, fileEvents);
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/index.ts");
        Assert.Null(err);

        if (expectConfigReload)
        {
            // Go message: "Config should have been reloaded for changes outside node_modules"
            Assert.Equal(ScriptTarget.ESNext, ls!.GetProgram().Options().Target);
            // Go message: "New root file should be present"
            Assert.True(ls.GetProgram().GetSourceFile("/project/src/rootFile.ts") != null);
        }
        else
        {
            // Go message: "Config should not have been reloaded for changes outside node_modules"
            Assert.Equal(ScriptTarget.ES2015, ls!.GetProgram().Options().Target);
            // Go message: "New root file should not be present"
            Assert.True(ls.GetProgram().GetSourceFile("/project/src/rootFile.ts") == null);
        }
    }

    // bulkcache_test.go:223-264 — the `test` closure of the "config file names cache" subtest.
    private static void ConfigFileNamesCacheScenario(FileEvent[] fileEvents, bool expectConfigDiscovery, string testName)
    {
        var files = new Dictionary<string, object?>
        {
            ["/project/src/index.ts"] = @"console.log(""test"");", // No tsconfig initially
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Open file without tsconfig - should create inferred project
        session.DidOpenFile(RequestContext.Background, "file:///project/src/index.ts", 1, (string)files["/project/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.True(snapshot.ProjectCollection.InferredProject() != null, "Should have inferred project");
        Assert.Equal(Kind.Inferred, snapshot.GetDefaultProject("file:///project/src/index.ts")!.Kind);

        // Create a tsconfig that would affect this file (simulating a missed creation event)
        utils.FS().WriteFile("/project/tsconfig.json", @"{
			""compilerOptions"": {
				""strict"": true
			},
			""include"": [""src/**/*""]
		}");

        // Process the changes
        session.DidChangeWatchedFiles(RequestContext.Background, fileEvents);

        // Get language service to trigger config discovery
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/index.ts");
        Assert.Null(err);

        snapshot = session.Snapshot()!;
        var newProject = snapshot.GetDefaultProject("file:///project/src/index.ts")!;

        // Check expected behavior
        if (expectConfigDiscovery)
        {
            // Should now use configured project instead of inferred
            // Go messages: "Should now use configured project after cache invalidation",
            // "Should use the newly discovered tsconfig"
            Assert.Equal(Kind.Configured, newProject.Kind);
            Assert.Equal("/project/tsconfig.json", newProject.Name());
        }
        else
        {
            // Should still use inferred project (config file names cache not cleared)
            // Go message: "Should still use inferred project after node_modules-only changes"
            Assert.True(ReferenceEquals(newProject, snapshot.ProjectCollection.InferredProject()), testName);
        }
    }

    // bulkcache_test.go:42 — "large number of node_modules changes invalidates only
    // node_modules cache" › "with file existing in cache".
    [Fact]
    public void TestBulkCacheInvalidation_LargeNumberOfNodeModulesChangesInvalidatesOnlyNodeModulesCache_WithFileExistingInCache()
    {
        var fileEvents = GenerateFileEvents(1001, "file:///project/node_modules/generated/file%d.js", FileChangeType.Created);
        // Include two files in the program to trigger a full program creation.
        // Exclude fs.d.ts to show that its content still gets invalidated.
        fileEvents =
        [
            .. fileEvents,
            new FileEvent
            {
                Uri = "file:///project/node_modules/@types/node/index.d.ts",
                Type = FileChangeType.Changed,
            },
            new FileEvent
            {
                Uri = "file:///project/node_modules/@types/node/console.d.ts",
                Type = FileChangeType.Changed,
            },
        ];

        NodeModulesScenario(BaseFiles(), fileEvents, expectNodeModulesInvalidation: true);
    }

    // bulkcache_test.go:112 — "large number of node_modules changes invalidates only
    // node_modules cache" › "without file existing in cache".
    [Fact]
    public void TestBulkCacheInvalidation_LargeNumberOfNodeModulesChangesInvalidatesOnlyNodeModulesCache_WithoutFileExistingInCache()
    {
        var fileEvents = GenerateFileEvents(1001, "file:///project/node_modules/generated/file%d.js", FileChangeType.Created);
        NodeModulesScenario(BaseFiles(), fileEvents, expectNodeModulesInvalidation: false);
    }

    // bulkcache_test.go:119 — "large number of changes outside node_modules" ›
    // "with event matching include glob".
    [Fact]
    public void TestBulkCacheInvalidation_LargeNumberOfChangesOutsideNodeModules_WithEventMatchingIncludeGlob()
    {
        var fileEvents = GenerateFileEvents(1001, "file:///project/generated/file%d.ts", FileChangeType.Created);
        fileEvents =
        [
            .. fileEvents,
            new FileEvent
            {
                Uri = "file:///project/src/rootFile.ts",
                Type = FileChangeType.Created,
            },
        ];
        OutsideNodeModulesScenario(BaseFiles(), fileEvents, expectConfigReload: true);
    }

    // bulkcache_test.go:169 — "large number of changes outside node_modules" ›
    // "without event matching include glob".
    [Fact]
    public void TestBulkCacheInvalidation_LargeNumberOfChangesOutsideNodeModules_WithoutEventMatchingIncludeGlob()
    {
        var fileEvents = GenerateFileEvents(1001, "file:///project/generated/file%d.ts", FileChangeType.Created);
        OutsideNodeModulesScenario(BaseFiles(), fileEvents, expectConfigReload: false);
    }

    // bulkcache_test.go:176-219 — "large number of changes outside node_modules causes
    // project reevaluation".
    [Fact]
    public void TestBulkCacheInvalidation_LargeNumberOfChangesOutsideNodeModulesCausesProjectReevaluation()
    {
        var baseFiles = BaseFiles();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(baseFiles);

        // Open a file that will initially use the root tsconfig
        session.DidOpenFile(RequestContext.Background, "file:///project/src/utils/lib.ts", 1, (string)baseFiles["/project/src/utils/lib.ts"]!, LanguageKind.TypeScript.Value!);

        // Initially, the file should use the root project (strict mode)
        var snapshot = session.Snapshot()!;
        var initialProject = snapshot.GetDefaultProject("file:///project/src/utils/lib.ts")!;
        // Go message: "Should initially use root tsconfig"
        Assert.Equal("/project/tsconfig.json", initialProject.Name());

        // Get language service to verify initial strict mode
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/utils/lib.ts");
        Assert.Null(err);
        // Go message: "Should initially use strict mode from root config"
        Assert.Equal(Tristate.True, ls!.GetProgram().Options().Strict);

        // Now create the nested tsconfig (this would normally be detected, but we'll simulate a missed event)
        utils.FS().WriteFile("/project/src/utils/tsconfig.json", @"{
				""compilerOptions"": {
					""strict"": false,
					""target"": ""esnext""
				}
			}");

        // Create excessive changes to trigger bulk invalidation
        FileEvent[] fileEvents = GenerateFileEvents(1001, "file:///project/src/generated/file%d.ts", FileChangeType.Created);

        // Process the excessive changes - this should trigger project reevaluation
        session.DidChangeWatchedFiles(RequestContext.Background, fileEvents);

        // Get language service - this should now find the nested config and switch projects
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/utils/lib.ts");
        Assert.Null(err);

        snapshot = session.Snapshot()!;
        var newProject = snapshot.GetDefaultProject("file:///project/src/utils/lib.ts")!;

        // The file should now use the nested tsconfig
        // Go messages: "Should now use nested tsconfig after bulk invalidation",
        // "Should now use non-strict mode from nested config",
        // "Should use esnext target from nested config"
        Assert.Equal("/project/src/utils/tsconfig.json", newProject.Name());
        Assert.Equal(Tristate.False, ls!.GetProgram().Options().Strict);
        Assert.Equal(ScriptTarget.ESNext, ls.GetProgram().Options().Target);
    }

    // bulkcache_test.go:221 — "config file names cache" › "excessive changes only in
    // node_modules does not affect config file names cache".
    [Fact]
    public void TestBulkCacheInvalidation_ConfigFileNamesCache_ExcessiveChangesOnlyInNodeModulesDoesNotAffectConfigFileNamesCache()
    {
        var fileEvents = GenerateFileEvents(1001, "file:///project/node_modules/generated/file%d.js", FileChangeType.Created);
        ConfigFileNamesCacheScenario(fileEvents, expectConfigDiscovery: false, testName: "node_modules changes should not clear config cache");
    }

    // bulkcache_test.go:272 — "config file names cache" › "excessive changes outside
    // node_modules clears config file names cache".
    [Fact]
    public void TestBulkCacheInvalidation_ConfigFileNamesCache_ExcessiveChangesOutsideNodeModulesClearsConfigFileNamesCache()
    {
        var fileEvents = GenerateFileEvents(1001, "file:///project/src/generated/file%d.ts", FileChangeType.Created);
        // Presence of any tsconfig.json file event triggers rediscovery for config for all open files
        fileEvents =
        [
            .. fileEvents,
            new FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///project/src/generated/tsconfig.json"),
                Type = FileChangeType.Created,
            },
        ];
        ConfigFileNamesCacheScenario(fileEvents, expectConfigDiscovery: true, testName: "non-node_modules changes should clear config cache");
    }

    // bulkcache_test.go:284-320 — "excessive changes in dist folder do not invalidate".
    // Simulate external build tool changing files in dist/ (not included by any project)
    [Fact]
    public void TestBulkCacheInvalidation_ExcessiveChangesInDistFolderDoNotInvalidate()
    {
        var files = new Dictionary<string, object?>
        {
            ["/project/src/index.ts"] = @"console.log(""test"");", // No tsconfig initially
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Open file without tsconfig - should create inferred project
        session.DidOpenFile(RequestContext.Background, "file:///project/src/index.ts", 1, (string)files["/project/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.Equal(Kind.Inferred, snapshot.GetDefaultProject("file:///project/src/index.ts")!.Kind);

        // Create a tsconfig that would affect this file (simulating a missed creation event)
        // This should NOT be discovered after dist-folder changes
        utils.FS().WriteFile("/project/tsconfig.json", @"{
				""compilerOptions"": {
					""strict"": true
				},
				""include"": [""src/**/*""]
			}");

        // Create excessive changes in dist folder only
        FileEvent[] fileEvents = GenerateFileEvents(1001, "file:///project/dist/generated/file%d.js", FileChangeType.Created);
        session.DidChangeWatchedFiles(RequestContext.Background, fileEvents);

        // File should still use inferred project (config file names cache NOT cleared for dist changes)
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///project/src/index.ts");
        Assert.Null(err);

        snapshot = session.Snapshot()!;
        var newProject = snapshot.GetDefaultProject("file:///project/src/index.ts")!;
        // Go message: "dist-folder changes should not cause config discovery"
        Assert.Equal(Kind.Inferred, newProject.Kind);
        // This assertion will fail until we implement logic to ignore dist folder changes
    }

    // bulkcache_test.go:322-378 — "bulk invalidation with retained config whose command
    // line is nil". Regression test for
    // https://github.com/microsoft/typescript-go/issues/4545
    //
    // A config file entry can be retained (here, by an open file whose default
    // project search fanned out to a referenced config) while its commandLine is
    // nil, because the referenced config file does not exist. A bulk cache
    // invalidation triggered by an excessive number of watch events ranges over
    // all config entries and used to dereference entry.commandLine.ConfigFile
    // without a nil check, crashing.
    //
    // The nil-check-avoiding short circuit `!ok || text != entry.commandLine...`
    // only protects the case where the file cannot be read, so the config file
    // must exist on disk (readable) while the entry's commandLine is still nil to
    // reach the crash.
    [Fact]
    public void TestBulkCacheInvalidation_BulkInvalidationWithRetainedConfigWhoseCommandLineIsNil()
    {
        string appConfig = @"{
				""compilerOptions"": { ""composite"": true, ""target"": ""esnext"" },
				""include"": [""**/*""]
			}";
        var files = new Dictionary<string, object?>
        {
            // Solution config references ./app, but app/tsconfig.json does not exist.
            ["/project/tsconfig.json"] = @"{
					""compilerOptions"": { ""composite"": true },
					""files"": [],
					""references"": [{ ""path"": ""./app"" }]
				}",
            ["/project/app/main.ts"] = @"export const main = 1;",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Open a file in the (non-existent) referenced project. The default project
        // search fans out to app/tsconfig.json, creating a retained config entry
        // with commandLine == nil and pendingReload == None.
        session.DidOpenFile(RequestContext.Background, "file:///project/app/main.ts", 1, (string)files["/project/app/main.ts"]!, LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///project/app/main.ts");
        Assert.Null(err);

        // Create the referenced config on disk WITHOUT notifying, so the
        // nil-commandLine entry is not reloaded (pendingReload stays None) but the
        // file becomes readable.
        utils.FS().WriteFile("/project/app/tsconfig.json", appConfig);

        // Trigger a bulk cache invalidation with an excessive number of watch events.
        // The creation of a config file drives the excessive-change path into
        // invalidateCache, which ranges over all config entries -- including the one
        // whose commandLine is nil -- and used to crash.
        FileEvent[] fileEvents = GenerateFileEvents(1001, "file:///project/app/generated/file%d.ts", FileChangeType.Created);
        fileEvents =
        [
            .. fileEvents,
            new FileEvent
            {
                Uri = "file:///project/newdir/tsconfig.json",
                Type = FileChangeType.Created,
            },
        ];
        session.DidChangeWatchedFiles(RequestContext.Background, fileEvents);
        (_, err) = session.GetLanguageService(RequestContext.Background, "file:///project/app/main.ts");
        Assert.Null(err);
    }

    // bulkcache_test.go:382-391 — generateFileEvents. Helper function to generate
    // excessive file change events.
    private static FileEvent[] GenerateFileEvents(int count, string pathTemplate, FileChangeType changeType)
    {
        var events = new List<FileEvent>();
        for (int i = 0; i < count; i++)
        {
            events.Add(new FileEvent
            {
                // Go: fmt.Sprintf(pathTemplate, i) with a %d verb; string.Format uses {0}.
                Uri = string.Format(pathTemplate.Replace("%d", "{0}"), i),
                Type = changeType,
            });
        }
        return events.ToArray();
    }
}
