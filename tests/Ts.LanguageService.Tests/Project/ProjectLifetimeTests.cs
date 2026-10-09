// Ported from tsgo internal/project/projectlifetime_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one xUnit class; t.Run subtests → [Fact]s named
//    Parent_Subtest. The shared `runNewFileScenario` closure becomes a private static
//    helper taking the bool parameter (its `t *testing.T` argument disappears —
//    assertions report through xUnit directly).
//  * gotest.tools assert.Equal(t, actual, expected) → Assert.Equal(expected, actual)
//    (argument order swapped); assert.Assert(cond, msg, args...) →
//    Assert.True(cond, $"msg") with the Go message inlined.
//  * Go `err := utils.FS().WriteFile/Remove(...); assert.NilError(t, err)` → direct
//    calls: the C# IFileSystem methods throw on failure (05 §4), so a failed operation
//    fails the test exactly where Go's err assertion would.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background; tspath.Path(...) →
//    new global::Ts.TsPath.Path(...) (directory case folding happens inside Path, so the
//    lowercased "/home/projects/ts/..." spellings from the Go source carry over verbatim).

using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using FileChangeType = global::Ts.LsProto.FileChangeType;
using FileEvent = global::Ts.LsProto.FileEvent;
using LanguageKind = global::Ts.LsProto.LanguageKind;

namespace Ts.Project.Tests;

public sealed class ProjectLifetimeTests
{
    // projectlifetime_test.go:15-107 — TestProjectLifetime "configured project".
    [Fact]
    public void TestProjectLifetime_ConfiguredProject()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { x } from ""./x"";",
            ["/home/projects/TS/p1/src/x.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p1/config.ts"] = @"let x = 1, y = 2;",
            ["/home/projects/TS/p2/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p2/src/index.ts"] = @"import { x } from ""./x"";",
            ["/home/projects/TS/p2/src/x.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p2/config.ts"] = @"let x = 1, y = 2;",
            ["/home/projects/TS/p3/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p3/src/index.ts"] = @"import { x } from ""./x"";",
            ["/home/projects/TS/p3/src/x.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p3/config.ts"] = @"let x = 1, y = 2;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        var snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        // Open files in two projects
        DocumentUri uri1 = "file:///home/projects/TS/p1/src/index.ts";
        DocumentUri uri2 = "file:///home/projects/TS/p2/src/index.ts";
        session.DidOpenFile(RequestContext.Background, uri1, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, uri2, 1, (string)files["/home/projects/TS/p2/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();
        snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p2/tsconfig.json")) != null);
        Assert.Single(utils.Client().WatchFilesCalls());
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p2/tsconfig.json")) != null);

        // Close p1 file and open p3 file
        session.DidCloseFile(RequestContext.Background, uri1);
        DocumentUri uri3 = "file:///home/projects/TS/p3/src/index.ts";
        session.DidOpenFile(RequestContext.Background, uri3, 1, (string)files["/home/projects/TS/p3/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();
        // Should still have two projects, but p1 replaced by p3
        snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) == null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p2/tsconfig.json")) != null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p3/tsconfig.json")) != null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) == null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p2/tsconfig.json")) != null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p3/tsconfig.json")) != null);
        Assert.Single(utils.Client().WatchFilesCalls());
        Assert.Empty(utils.Client().UnwatchFilesCalls());

        // Close p2 and p3 files, open p1 file again
        session.DidCloseFile(RequestContext.Background, uri2);
        session.DidCloseFile(RequestContext.Background, uri3);
        session.DidOpenFile(RequestContext.Background, uri1, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();
        // Should have one project (p1)
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p2/tsconfig.json")) == null);
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p3/tsconfig.json")) == null);
        Assert.Single(utils.Client().WatchFilesCalls());
        Assert.Empty(utils.Client().UnwatchFilesCalls());
    }

    // projectlifetime_test.go:109-156 — TestProjectLifetime "unrooted inferred projects".
    [Fact]
    public void TestProjectLifetime_UnrootedInferredProjects()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/src/index.ts"] = @"import { x } from ""./x"";",
            ["/home/projects/TS/p1/src/x.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p1/config.ts"] = @"let x = 1, y = 2;",
            ["/home/projects/TS/p2/src/index.ts"] = @"import { x } from ""./x"";",
            ["/home/projects/TS/p2/src/x.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p2/config.ts"] = @"let x = 1, y = 2;",
            ["/home/projects/TS/p3/src/index.ts"] = @"import { x } from ""./x"";",
            ["/home/projects/TS/p3/src/x.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p3/config.ts"] = @"let x = 1, y = 2;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        var snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        // Open files without workspace roots (empty string) - should create single inferred project
        DocumentUri uri1 = "file:///home/projects/TS/p1/src/index.ts";
        DocumentUri uri2 = "file:///home/projects/TS/p2/src/index.ts";
        session.DidOpenFile(RequestContext.Background, uri1, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, uri2, 1, (string)files["/home/projects/TS/p2/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Should have one inferred project
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);

        // Close p1 file and open p3 file
        session.DidCloseFile(RequestContext.Background, uri1);
        DocumentUri uri3 = "file:///home/projects/TS/p3/src/index.ts";
        session.DidOpenFile(RequestContext.Background, uri3, 1, (string)files["/home/projects/TS/p3/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Should still have one inferred project
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);

        // Close p2 and p3 files, open p1 file again
        session.DidCloseFile(RequestContext.Background, uri2);
        session.DidCloseFile(RequestContext.Background, uri3);
        session.DidOpenFile(RequestContext.Background, uri1, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Should still have one inferred project
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);
    }

    // projectlifetime_test.go:158-208 — TestProjectLifetime "file moves from inferred to
    // configured project".
    [Fact]
    public void TestProjectLifetime_FileMovesFromInferredToConfiguredProject()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/ts/foo.ts"] = @"export const foo = 1;",
            ["/home/projects/ts/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""include"": [""main.ts""]
			}",
            ["/home/projects/ts/p1/main.ts"] = @"import { foo } from ""../foo""; console.log(foo);",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        // Open foo.ts first - should create inferred project since no tsconfig found initially
        DocumentUri fooUri = "file:///home/projects/ts/foo.ts";
        session.DidOpenFile(RequestContext.Background, fooUri, 1, (string)files["/home/projects/ts/foo.ts"]!, LanguageKind.TypeScript.Value!);

        // Should have one inferred project
        var snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) == null);

        // Now open main.ts - should trigger discovery of tsconfig.json and move foo.ts to configured project
        DocumentUri mainUri = "file:///home/projects/ts/p1/main.ts";
        session.DidOpenFile(RequestContext.Background, mainUri, 1, (string)files["/home/projects/ts/p1/main.ts"]!, LanguageKind.TypeScript.Value!);

        // Should now have one configured project and no inferred project
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() == null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);

        // Config file should be present
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);

        // Close main.ts - configured project should remain because foo.ts is still open
        session.DidCloseFile(RequestContext.Background, mainUri);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);

        // Close foo.ts - configured project should be retained until next file open
        session.DidCloseFile(RequestContext.Background, fooUri);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
    }

    // projectlifetime_test.go:210-276 — TestProjectLifetime "file move from inferred to
    // configured via didOpen/didClose sequence".
    [Fact]
    public void TestProjectLifetime_FileMoveFromInferredToConfiguredViaDidOpenDidCloseSequence()
    {
        // Start with tsconfig.json that includes "src" but file is at root level
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/index.ts"] = @"export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Open index.ts at root level - should create inferred project since it's not under src/
        // Creates config file registry entry, but has no files
        DocumentUri indexUri = "file:///home/projects/TS/p1/index.ts";
        session.DidOpenFile(RequestContext.Background, indexUri, 1, (string)files["/home/projects/TS/p1/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Should have one inferred project only (file is not included by tsconfig)
        var snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) == null);

        // Simulate file move: create src/index.ts on disk
        utils.FS().WriteFile("/home/projects/TS/p1/src/index.ts", (string)files["/home/projects/TS/p1/index.ts"]!);
        utils.FS().Remove("/home/projects/TS/p1/index.ts");

        // Simulate file move sequence as it would happen in an editor:
        // 1. didOpen src/index.ts (new location)
        // Open comes in before file create event, so the config file is not marked as needing a file name reload,
        // so it's not turned into a configured project yet. This is probably not ideal, but it should sort itself
        // out momentarily after the file watcher events are processed. When we try the config file, we mark it
        // as "retained by src/index.ts" so the config entry doesn't get deleted before src/index.ts is closed.
        // Even though we currently think src/index.ts doesn't belong to the config, the config is in its directory
        // path, so we'll always see it as a candidate for containing src/index.ts.
        DocumentUri srcIndexUri = "file:///home/projects/TS/p1/src/index.ts";
        session.DidOpenFile(RequestContext.Background, srcIndexUri, 1, (string)files["/home/projects/TS/p1/index.ts"]!, LanguageKind.TypeScript.Value!);

        // 2. didClose index.ts (old location)
        session.DidCloseFile(RequestContext.Background, indexUri);

        // 3. didChangeWatchedFiles: create src/index.ts and delete index.ts
        // The creation event for src/index.ts now hits the config file registry, and we should notice we
        // got a creation event for a file that retained the config, triggering a filename reload.
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Uri = srcIndexUri,
                Type = FileChangeType.Created,
            },
            new FileEvent
            {
                Uri = indexUri,
                Type = FileChangeType.Deleted,
            },
        ]);

        // Should now have one configured project only (file is now under src/)
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, srcIndexUri);
        Assert.Null(err);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() == null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
    }

    // projectlifetime_test.go:278-344 — the runNewFileScenario closure shared by the two
    // "newly created file joins configured project with ./-prefixed includes" subtests.
    // Regression test for https://github.com/microsoft/typescript-go/issues/3733
    //
    // "./"-prefixed include specs combined with a dot-directory exclude ("**/.*/")
    // used to drop all wildcard directories for the config, so a newly created file
    // never triggered a root file reload. The stale nested config never claimed the
    // file, and default-project resolution assigned it to the ancestor project
    // (parsed fresh from disk, so it did contain the file) — or the inferred project
    // when no ancestor config matched — until the session was restarted. The file
    // then had the wrong compiler options (e.g. no Temporal from lib ESNext).
    // The failure was independent of the order in which didOpen and the create
    // watch event arrived, so both orderings are covered.
    private static void RunNewFileScenario(bool openBeforeCreateEvent)
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/monorepo/tsconfig.json"] = @"{
				""compilerOptions"": {
					""target"": ""ES2015"",
					""lib"": [""DOM""]
				},
				""include"": [""apps""]
			}",
            ["/home/projects/TS/monorepo/apps/web/tsconfig.json"] = @"{
				""compilerOptions"": {
					""target"": ""ESNext"",
					""lib"": [""ESNext""]
				},
				""include"": [""./app/**/*.ts""],
				""exclude"": [""**/.*/""]
			}",
            ["/home/projects/TS/monorepo/apps/web/app/existing.ts"] = @"export const existing = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Open an existing file so the nested configured project loads.
        DocumentUri existingUri = "file:///home/projects/TS/monorepo/apps/web/app/existing.ts";
        session.DidOpenFile(RequestContext.Background, existingUri, 1, (string)files["/home/projects/TS/monorepo/apps/web/app/existing.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, existingUri);
        Assert.Null(err);
        Assert.Equal(ScriptTarget.ESNext, ls!.GetProgram().Options().Target);

        // Create a new file on disk in a directory matched by the nested config's
        // wildcard include, then simulate the editor events in the given order.
        string newFileContent = @"export const newFile = 1;";
        DocumentUri newFileUri = "file:///home/projects/TS/monorepo/apps/web/app/subdir/new.ts";
        utils.FS().WriteFile("/home/projects/TS/monorepo/apps/web/app/subdir/new.ts", newFileContent);
        void OpenNewFile()
        {
            session.DidOpenFile(RequestContext.Background, newFileUri, 1, newFileContent, LanguageKind.TypeScript.Value!);
        }
        void SendCreateEvent()
        {
            session.DidChangeWatchedFiles(RequestContext.Background,
            [
                new FileEvent { Uri = newFileUri, Type = FileChangeType.Created },
            ]);
        }
        if (openBeforeCreateEvent)
        {
            OpenNewFile();
            SendCreateEvent();
        }
        else
        {
            SendCreateEvent();
            OpenNewFile();
        }

        // The new file should be served by the nested configured project, not the
        // root project (ES2015 target) or an inferred project.
        (ls, err) = session.GetLanguageService(RequestContext.Background, newFileUri);
        Assert.Null(err);
        Assert.Equal(ScriptTarget.ESNext, ls!.GetProgram().Options().Target);
        Assert.True(session.Snapshot()!.ProjectCollection.InferredProject() == null);
    }

    // projectlifetime_test.go:346-349 — "newly created file joins configured project with
    // ./-prefixed includes (didOpen before create event)".
    [Fact]
    public void TestProjectLifetime_NewlyCreatedFileJoinsConfiguredProjectWithDotPrefixedIncludes_DidOpenBeforeCreateEvent()
    {
        RunNewFileScenario(openBeforeCreateEvent: true);
    }

    // projectlifetime_test.go:351-354 — "newly created file joins configured project with
    // ./-prefixed includes (create event before didOpen)".
    [Fact]
    public void TestProjectLifetime_NewlyCreatedFileJoinsConfiguredProjectWithDotPrefixedIncludes_CreateEventBeforeDidOpen()
    {
        RunNewFileScenario(openBeforeCreateEvent: false);
    }

    // projectlifetime_test.go:356-409 — TestProjectLifetime "tsconfig move from
    // subdirectory to parent via didChangeWatchedFiles".
    [Fact]
    public void TestProjectLifetime_TsconfigMoveFromSubdirectoryToParentViaDidChangeWatchedFiles()
    {
        // Start with tsconfig.json in src/ that includes "src" - file won't be included initially
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/src/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Open src/index.ts - should create inferred project since tsconfig.json includes "src"
        // relative to its location (src/src/ which doesn't exist)
        DocumentUri indexUri = "file:///home/projects/TS/p1/src/index.ts";
        session.DidOpenFile(RequestContext.Background, indexUri, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Should have one inferred project only (file is not included by tsconfig at src/tsconfig.json)
        var snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/src/tsconfig.json")) == null);

        // Simulate tsconfig.json move: create tsconfig.json at parent level, delete from src/
        string tsconfigContent = (string)files["/home/projects/TS/p1/src/tsconfig.json"]!;
        utils.FS().WriteFile("/home/projects/TS/p1/tsconfig.json", tsconfigContent);
        utils.FS().Remove("/home/projects/TS/p1/src/tsconfig.json");

        // Simulate file move via didChangeWatchedFiles
        DocumentUri newTsconfigUri = "file:///home/projects/TS/p1/tsconfig.json";
        DocumentUri oldTsconfigUri = "file:///home/projects/TS/p1/src/tsconfig.json";
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Uri = newTsconfigUri,
                Type = FileChangeType.Created,
            },
            new FileEvent
            {
                Uri = oldTsconfigUri,
                Type = FileChangeType.Deleted,
            },
        ]);

        // Should now have one configured project only (tsconfig.json now includes src/index.ts)
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, indexUri);
        Assert.Null(err);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() == null);
        Assert.True(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json")) != null);
    }

    // projectlifetime_test.go:411-485 — TestProjectLifetime "deleted open file remains in
    // project until closed".
    [Fact]
    public void TestProjectLifetime_DeletedOpenFileRemainsInProjectUntilClosed()
    {
        // Scenario:
        // 1. Start with two files included by a tsconfig, both open
        // 2. In a single batch change, delete one of the files but leave it open, and create a new file included by the tsconfig
        // 3. Request a LS for the deleted but open file
        // 4. Project should include both the new file and the deleted open file
        // 5. Close the deleted file
        // 6. On next LS request, the project should exclude the deleted file

        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"",
            ["/home/projects/TS/p1/src/x.ts"] = @"export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        // Step 1: Open both files
        DocumentUri indexUri = "file:///home/projects/TS/p1/src/index.ts";
        DocumentUri xUri = "file:///home/projects/TS/p1/src/x.ts";
        session.DidOpenFile(RequestContext.Background, indexUri, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, xUri, 1, (string)files["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);

        // Verify initial state - both files should be in the project
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, indexUri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/index.ts") != null, "index.ts should be in project");
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") != null, "x.ts should be in project");

        // Step 2: In a single batch change:
        // - Delete x.ts from disk (but leave it open)
        // - Create a new file y.ts on disk
        utils.FS().Remove("/home/projects/TS/p1/src/x.ts");
        utils.FS().WriteFile("/home/projects/TS/p1/src/y.ts", @"export const y = 2;");

        // Send both events in a single batch
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Uri = xUri,
                Type = FileChangeType.Deleted,
            },
            new FileEvent
            {
                Uri = (DocumentUri)"file:///home/projects/TS/p1/src/y.ts",
                Type = FileChangeType.Created,
            },
        ]);

        // Step 3 & 4: Request LS for the deleted but still open file
        // Project should include: index.ts, x.ts (open overlay), y.ts (new disk file)
        (ls, err) = session.GetLanguageService(RequestContext.Background, xUri);
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/index.ts") != null, "index.ts should still be in project");
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") != null, "x.ts should still be in project (open overlay)");
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/y.ts") != null, "y.ts should be in project (new file)");

        // Step 5: Close the deleted file
        session.DidCloseFile(RequestContext.Background, xUri);

        // Step 6: On next LS request, x.ts should be excluded
        (ls, err) = session.GetLanguageService(RequestContext.Background, indexUri);
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/index.ts") != null, "index.ts should still be in project");
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") == null, "x.ts should no longer be in project (closed and deleted)");
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/y.ts") != null, "y.ts should still be in project");
    }
}
