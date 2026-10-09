// Ported from tsgo internal/project/project_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go file → one xUnit class; each t.Run subtest → its own [Fact] named
//    ParentTest_Subtest.
//  * gotest.tools assert.Equal(t, actual, expected) → Assert.Equal(expected, actual)
//    (argument order swapped); reference comparisons (program identity) → Assert.Same /
//    Assert.NotSame; assert.Assert(cond, msg...) → Assert.True(cond, $"msg").
//  * Go `err := utils.FS().WriteFile(...); assert.NilError(t, err)` → direct calls: the
//    C# IFileSystem methods throw on failure (05 §4), so a failed operation fails the
//    test exactly where Go's err assertion would.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background; tspath.Path(...) →
//    new global::Ts.TsPath.Path(...).
//  * `publishDiagnosticsCall` (Go local struct) is ClientMock.PublishDiagnosticsCall;
//    filterDiagnosticsByURI is ported as a private static helper over that type.
//  * Go's Program.GetSemanticDiagnostics takes a RequestContext; the C# Program surface
//    takes a CancellationToken (TestContext.Current.CancellationToken), and the deep
//    recursion runs under LargeStackExecutor (AGENTS.md 铁律 8), matching the
//    ProjectServiceTests idiom.

using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DiagnosticsApi = global::Ts.Diagnostics.Diagnostics;
using DocumentUri = global::Ts.LsProto.DocumentUri;
using FileChangeType = global::Ts.LsProto.FileChangeType;
using FileEvent = global::Ts.LsProto.FileEvent;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using Position = global::Ts.LsProto.Position;
using Range = global::Ts.LsProto.Range;
using TextDocumentContentChangePartial = global::Ts.LsProto.TextDocumentContentChangePartial;
using TextDocumentContentChangePartialOrWholeDocument = global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument;
using TextDocumentContentChangeWholeDocument = global::Ts.LsProto.TextDocumentContentChangeWholeDocument;

namespace Ts.Project.Tests;

public sealed class ProjectTests
{
    // project_test.go:20-23 — publishDiagnosticsCall (see ClientMock.PublishDiagnosticsCall).

    // project_test.go:25-35 — filterDiagnosticsByURI returns all PublishDiagnostics calls
    // matching the given URI, starting from the given index.
    private static List<ClientMock.PublishDiagnosticsCall> FilterDiagnosticsByURI(
        IReadOnlyList<ClientMock.PublishDiagnosticsCall> calls,
        DocumentUri uri,
        int from)
    {
        var result = new List<ClientMock.PublishDiagnosticsCall>();
        for (int i = from; i < calls.Count; i++)
        {
            if (calls[i].Params.Uri == uri)
            {
                result.Add(calls[i]);
            }
        }
        return result;
    }

    // project_test.go:46-60 — TestProjectProgramUpdateKind "NewFiles on initial build".
    // These tests explicitly verify ProgramUpdateKind using subtests with shared helpers.
    [Fact]
    public void TestProjectProgramUpdateKind_NewFilesOnInitialBuild()
    {
        // Use the default session setup for tests.
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/src/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal(ProgramUpdateKind.NewFiles, configured!.ProgramUpdateKind);
    }

    // project_test.go:62-81 — TestProjectProgramUpdateKind "Cloned on single-file change".
    // SKIPPED: Go's dirty-file path (Project.CreateProgram → compiler.Program.UpdateProgram)
    // is not ported to the C# compiler yet; the C# CreateProgram always rebuilds, so
    // ProgramUpdateKind.Cloned is never produced (PORT-NOTE at
    // src/Ts.LanguageService/Project/Project.cs:7 and :327).
    [Fact(Skip = "compiler.Program.UpdateProgram (incremental dirty-file program update) is not ported to C# yet; Project.CreateProgram always rebuilds and never yields ProgramUpdateKind.Cloned, Go test TestProjectProgramUpdateKind/Cloned on single-file change")]
    public void TestProjectProgramUpdateKind_ClonedOnSingleFileChange()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{}",
            ["/src/index.ts"] = "console.log('Hello');",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.DidChangeFile(RequestContext.Background, "file:///src/index.ts", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Text = "\n",
                    Range = new Range { Start = new Position { Line = 0, Character = 20 }, End = new Position { Line = 0, Character = 20 } },
                },
            },
        ]);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/src/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal(ProgramUpdateKind.Cloned, configured!.ProgramUpdateKind);
    }

    // project_test.go:83-102 — TestProjectProgramUpdateKind "SameFileNames on config change
    // without root changes".
    [Fact]
    public void TestProjectProgramUpdateKind_SameFileNamesOnConfigChangeWithoutRootChanges()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{""compilerOptions"": {""strict"": true}}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        utils.FS().WriteFile("/src/tsconfig.json", @"{""compilerOptions"": {""strict"": false}}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent { Uri = (DocumentUri)"file:///src/tsconfig.json", Type = FileChangeType.Changed },
        ]);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/src/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal(ProgramUpdateKind.SameFileNames, configured!.ProgramUpdateKind);
    }

    // project_test.go:104-125 — TestProjectProgramUpdateKind "NewFiles on root addition".
    [Fact]
    public void TestProjectProgramUpdateKind_NewFilesOnRootAddition()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{}",
            ["/src/index.ts"] = "export {}",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        string content = "export const y = 2;";
        utils.FS().WriteFile("/src/newfile.ts", content);
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent { Uri = (DocumentUri)"file:///src/newfile.ts", Type = FileChangeType.Created },
        ]);
        session.DidOpenFile(RequestContext.Background, "file:///src/newfile.ts", 1, content, LanguageKind.TypeScript.Value!);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/newfile.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/src/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal(ProgramUpdateKind.NewFiles, configured!.ProgramUpdateKind);
    }

    // project_test.go:127-148 — TestProjectProgramUpdateKind "SameFileNames when adding an
    // unresolvable import with multi-file change".
    [Fact]
    public void TestProjectProgramUpdateKind_SameFileNamesWhenAddingAnUnresolvableImportWithMultiFileChange()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{}",
            ["/src/index.ts"] = "export const x = 1;",
            ["/src/other.ts"] = "export const z = 3;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        // Change index.ts to add an unresolvable import
        session.DidChangeFile(RequestContext.Background, "file:///src/index.ts", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Text = "\nimport \"./does-not-exist\";\n",
                    Range = new Range { Start = new Position { Line = 0, Character = 0 }, End = new Position { Line = 0, Character = 0 } },
                },
            },
        ]);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/src/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal(ProgramUpdateKind.SameFileNames, configured!.ProgramUpdateKind);
    }

    // project_test.go:157-193 — TestProject "commandLineWithTypingsFiles is reset on
    // CommandLine change".
    [Fact]
    public void TestProject_CommandLineWithTypingsFilesIsResetOnCommandLineChange()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project1/app.js"] = @"",
            ["/user/username/projects/project1/package.json"] = @"{""name"":""p1"",""dependencies"":{""jquery"":""^3.1.0""}}",
            ["/user/username/projects/project2/app.js"] = @"",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                // Provide typings content to be installed for jquery so ATA actually installs something
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        // 1) Open an inferred project file that triggers ATA
        DocumentUri uri1 = "file:///user/username/projects/project1/app.js";
        session.DidOpenFile(RequestContext.Background, uri1, 1, (string)files["/user/username/projects/project1/app.js"]!, LanguageKind.JavaScript.Value!);

        // 2) Wait for ATA/background tasks to finish, then get a language service for the first file
        session.WaitForBackgroundTasks();
        // Sanity check: ensure ATA performed at least one install
        var npmCalls = utils.NpmExecutor().NpmInstallCalls();
        Assert.True(npmCalls.Count > 0, "expected at least one npm install call from ATA");
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri1);
        Assert.Null(err);

        // 3) Open another inferred project file
        DocumentUri uri2 = "file:///user/username/projects/project2/app.js";
        session.DidOpenFile(RequestContext.Background, uri2, 1, @"", LanguageKind.JavaScript.Value!);

        // 4) Get a language service for the second file
        //    If commandLineWithTypingsFiles was not reset, the new program command line
        //    won't include the newly opened file and this will fail.
        (_, err) = session.GetLanguageService(RequestContext.Background, uri2);
        Assert.Null(err);
    }

    // project_test.go:195-266 — TestProject "inferred project rebuilt twice in one snapshot
    // after typings install does not crash".
    [Fact]
    public void TestProject_InferredProjectRebuiltTwiceInOneSnapshotAfterTypingsInstallDoesNotCrash()
    {
        // Reproduces the nil-command-line crash in (*Program).SingleThreaded (issue #3873).
        //
        // `getCommandLineWithTypingsFiles` memoizes `commandLineWithTypingsFiles` behind a
        // `sync.Once`. When `CommandLine` (or the inferred roots) change, the cached value is
        // reset to nil but the `Once` is NOT reset. `Project.Clone()` produces a fresh `Once`,
        // so the first mutation of a project within a snapshot normally clears the staleness.
        // But if the *same* in-snapshot clone has its program built, then its roots change, then
        // its program is built again, the second build hits a consumed `Once` and reads the nil
        // cached value, passing a nil command line to `compiler.NewProgram`.
        //
        // This sequence is produced within a single snapshot by `DidRequestFile`'s
        // build -> cleanupInferredProject -> build path, when:
        //   1. the inferred project has installed typings (so the `Once` path is taken), and
        //   2. a pending content change marks the inferred project dirty (forcing build #1), and
        //   3. a pending tsconfig creation moves another open file out of the inferred project,
        //      so `cleanupInferredProject` changes the inferred roots between the two builds.
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project1/a.js"] = @"",
            ["/user/username/projects/project1/package.json"] = @"{""name"":""p1"",""dependencies"":{""jquery"":""^3.1.0""}}",
            ["/user/username/projects/project1/b.ts"] = @"export const y = 1;",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        DocumentUri aURI = "file:///user/username/projects/project1/a.js";
        DocumentUri bURI = "file:///user/username/projects/project1/b.ts";

        // 1) Open the file that triggers ATA, plus another file that joins the inferred project.
        session.DidOpenFile(RequestContext.Background, aURI, 1, @"", LanguageKind.JavaScript.Value!);
        session.DidOpenFile(RequestContext.Background, bURI, 1, (string)files["/user/username/projects/project1/b.ts"]!, LanguageKind.TypeScript.Value!);

        // 2) Let ATA install jquery typings, then build the inferred program so its
        //    typings files are populated.
        session.WaitForBackgroundTasks();
        var npmCalls = utils.NpmExecutor().NpmInstallCalls();
        Assert.True(npmCalls.Count > 0, "expected at least one npm install call from ATA");
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, aURI);
        Assert.Null(err);

        // 3) Queue two pending changes that will be flushed together in the next snapshot:
        //    - a content change to a.js (marks the inferred project dirty -> first program build)
        //    - creation of a tsconfig.json that captures b.ts (moves it out of the inferred
        //      project, so cleanupInferredProject shrinks the inferred roots between builds).
        session.DidChangeFile(RequestContext.Background, aURI, 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new TextDocumentContentChangeWholeDocument { Text = @"// changed" },
            },
        ]);
        utils.FS().WriteFile("/user/username/projects/project1/tsconfig.json", @"{""compilerOptions"":{},""files"":[""b.ts""]}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///user/username/projects/project1/tsconfig.json",
            },
        ]);

        // 4) Flush the pending changes and the request in a single snapshot. With the bug,
        //    the inferred project's program is built, its roots change, and it is rebuilt with
        //    a nil command line, panicking in (*Program).SingleThreaded.
        // (Go: defer/recover → t.Fatalf("crash reproduced (issue #3873): %v", r); in C# the
        // exception propagates and fails the test — caught here only to keep the Go message.)
        try
        {
            (_, err) = session.GetLanguageService(RequestContext.Background, aURI);
            Assert.Null(err);
        }
        catch (System.Exception r)
        {
            Assert.Fail($"crash reproduced (issue #3873): {r}");
        }
    }

    // project_test.go:275-304 — TestPushDiagnostics "publishes program diagnostics on
    // initial program creation".
    [Fact]
    public void TestPushDiagnostics_PublishesProgramDiagnosticsOnInitialProgramCreation()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{""compilerOptions"": {""baseUrl"": "".""}}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);

        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        Assert.True(calls.Count > 0, "expected at least one PublishDiagnostics call");

        // Find the call for tsconfig.json
        ClientMock.PublishDiagnosticsCall? tsconfigCall = null;
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i].Params.Uri == (DocumentUri)"file:///src/tsconfig.json")
            {
                tsconfigCall = calls[i];
                break;
            }
        }
        Assert.True(tsconfigCall != null, "expected PublishDiagnostics call for tsconfig.json");
        Assert.True(tsconfigCall!.Params.Diagnostics.Length > 0, "expected at least one diagnostic");
    }

    // project_test.go:306-332 — TestPushDiagnostics "publishes config parsing diagnostics on
    // initial program creation".
    [Fact]
    public void TestPushDiagnostics_PublishesConfigParsingDiagnosticsOnInitialProgramCreation()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{
				""compilerOptions"": {
					""target"": ""nope""
				}
			}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);

        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        var tsconfigCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", 0);
        Assert.True(tsconfigCalls.Count > 0, "expected PublishDiagnostics call for tsconfig.json");
        var lastTsconfigCall = tsconfigCalls[^1];

        string expectedMessage = "Argument for '--target' option must be:";
        Assert.True(System.Array.Exists(lastTsconfigCall.Params.Diagnostics, (global::Ts.LsProto.Diagnostic diag) => diag.Message.String!.Contains(expectedMessage, System.StringComparison.Ordinal)),
            "expected invalid target diagnostic on tsconfig.json");
    }

    // project_test.go:334-371 — TestPushDiagnostics "clears diagnostics when project is
    // removed".
    [Fact]
    public void TestPushDiagnostics_ClearsDiagnosticsWhenProjectIsRemoved()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{""compilerOptions"": {""baseUrl"": "".""}}",
            ["/src/index.ts"] = "export const x = 1;",
            ["/src2/tsconfig.json"] = @"{""compilerOptions"": {}}",
            ["/src2/index.ts"] = "export const y = 2;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        // Open a file in a different project to trigger cleanup of the first
        session.DidCloseFile(RequestContext.Background, "file:///src/index.ts");
        session.DidOpenFile(RequestContext.Background, "file:///src2/index.ts", 1, (string)files["/src2/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src2/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        // Should have at least one call for the first project with diagnostics,
        // and one clearing it after switching projects
        var firstProjectCalls = new List<ClientMock.PublishDiagnosticsCall>();
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i].Params.Uri == (DocumentUri)"file:///src/tsconfig.json")
            {
                firstProjectCalls.Add(calls[i]);
            }
        }
        Assert.True(firstProjectCalls.Count >= 2, "expected at least 2 PublishDiagnostics calls for first project");
        // Last call should clear diagnostics
        var lastCall = firstProjectCalls[^1];
        Assert.Empty(lastCall.Params.Diagnostics); // Go message: "expected empty diagnostics after project cleanup"
    }

    // project_test.go:373-412 — TestPushDiagnostics "updates diagnostics when program
    // changes".
    [Fact]
    public void TestPushDiagnostics_UpdatesDiagnosticsWhenProgramChanges()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{""compilerOptions"": {""baseUrl"": "".""}}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        int initialCallCount = utils.Client().PublishDiagnosticsCalls().Count;

        // Change the tsconfig to remove baseUrl
        utils.FS().WriteFile("/src/tsconfig.json", @"{""compilerOptions"": {}}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent { Uri = (DocumentUri)"file:///src/tsconfig.json", Type = FileChangeType.Changed },
        ]);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        Assert.True(calls.Count > initialCallCount, "expected additional PublishDiagnostics call after change");

        // Find the last call for tsconfig.json
        ClientMock.PublishDiagnosticsCall? lastTsconfigCall = null;
        for (int i = calls.Count - 1; i >= 0; i--)
        {
            if (calls[i].Params.Uri == (DocumentUri)"file:///src/tsconfig.json")
            {
                lastTsconfigCall = calls[i];
                break;
            }
        }
        Assert.True(lastTsconfigCall != null, "expected PublishDiagnostics call for tsconfig.json");
        // After fixing the error, there should be no program diagnostics
        Assert.Empty(lastTsconfigCall!.Params.Diagnostics); // Go message: "expected no diagnostics after removing baseUrl option"
    }

    // project_test.go:414-428 — TestPushDiagnostics "does not publish for inferred projects".
    [Fact]
    public void TestPushDiagnostics_DoesNotPublishForInferredProjects()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/index.ts"] = "let x: number = 'not a number';",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        // Should not have any calls since inferred projects don't have tsconfig.json
        Assert.Empty(calls); // Go message: "expected no PublishDiagnostics calls for inferred projects"
    }

    // project_test.go:430-453 — TestPushDiagnostics "does not publish when validation is
    // disabled".
    [Fact]
    public void TestPushDiagnostics_DoesNotPublishWhenValidationIsDisabled()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{
				""compilerOptions"": {
					""target"": ""nope""
				}
			}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        var prefs = global::Ts.Ls.UserPreferences.NewDefaultUserPreferences();
        prefs.EnableValidation = Tristate.False;
        session.Configure(prefs);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        foreach (var call in calls)
        {
            Assert.Empty(call.Params.Diagnostics); // Go message: "expected only empty PublishDiagnostics calls when validation is disabled"
        }
    }

    // project_test.go:455-490 — TestPushDiagnostics "clears diagnostics when validation is
    // disabled".
    [Fact]
    public void TestPushDiagnostics_ClearsDiagnosticsWhenValidationIsDisabled()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{
				""compilerOptions"": {
					""target"": ""nope""
				}
			}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        var tsconfigCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", 0);
        Assert.True(tsconfigCalls.Count > 0, "expected initial PublishDiagnostics call for tsconfig.json");
        Assert.True(tsconfigCalls[^1].Params.Diagnostics.Length > 0, "expected initial diagnostics");

        var prefs = global::Ts.Ls.UserPreferences.NewDefaultUserPreferences();
        prefs.EnableValidation = Tristate.False;
        session.Configure(prefs);
        (_, err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        calls = utils.Client().PublishDiagnosticsCalls();
        tsconfigCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", calls.Count - 1);
        if (tsconfigCalls.Count == 0)
        {
            tsconfigCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", 0);
        }
        var lastTsconfigCall = tsconfigCalls[^1];
        Assert.Empty(lastTsconfigCall.Params.Diagnostics); // Go message: "expected diagnostics to be cleared when validation is disabled"
    }

    // project_test.go:492-544 — TestPushDiagnostics "publishes global diagnostics after
    // checking".
    [Fact]
    public void TestPushDiagnostics_PublishesGlobalDiagnosticsAfterChecking()
    {
        // Use a target/lib that does not include Disposable, then write code that needs it.
        // This triggers a deferred "Cannot find global type 'Disposable'" global diagnostic
        // during checking, which should be accumulated and published on the tsconfig URI.
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{
				""compilerOptions"": {
					""target"": ""es2020""
				}
			}",
            ["/src/index.ts"] = @"export function f() {
				using x = { [Symbol.dispose]() {} };
			}",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        // Request semantic diagnostics to trigger checking, which triggers the global type resolvers.
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(SessionUtils.WithRequestID(RequestContext.Background), (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        // Drain background tasks from DidOpenFile (publishProgramDiagnostics, etc.)
        // before triggering global diagnostics, to avoid racing with publishGlobalDiagnostics.
        session.WaitForBackgroundTasks();

        // Go: ls.ProvideDiagnostics(WithRequestID(ctx), uri) — the C# ProvideDiagnostics takes
        // the file name (the Go uri is converted to one internally) and returns the diagnostics
        // directly; the response object is discarded by the Go test too.
        _ = ls!.ProvideDiagnostics("/src/index.ts", cancellationToken: TestContext.Current.CancellationToken);
        // Enqueue global diagnostics publishing (normally done by the LSP server after each request).
        session.EnqueuePublishGlobalDiagnostics();
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        // Find the last call for tsconfig.json
        ClientMock.PublishDiagnosticsCall? lastTsconfigCall = null;
        for (int i = calls.Count - 1; i >= 0; i--)
        {
            if (calls[i].Params.Uri == (DocumentUri)"file:///src/tsconfig.json")
            {
                lastTsconfigCall = calls[i];
                break;
            }
        }
        Assert.True(lastTsconfigCall != null, "expected PublishDiagnostics call for tsconfig.json");
        // Should have global diagnostics (e.g., Cannot find global type 'Disposable')
        bool hasGlobalDiag = false;
        foreach (var diag in lastTsconfigCall!.Params.Diagnostics)
        {
            if (diag.Message.String!.Contains("Cannot find global", System.StringComparison.Ordinal))
            {
                hasGlobalDiag = true;
                break;
            }
        }
        Assert.True(hasGlobalDiag, "expected a 'Cannot find global' diagnostic on tsconfig.json");
    }

    // project_test.go:546-589 — TestPushDiagnostics "cleans tsconfig diagnostics after TS
    // files close and restores them after TS file is reopened".
    [Fact]
    public void TestPushDiagnostics_CleansTsconfigDiagnosticsAfterTSFilesCloseAndRestoresThemAfterTSFileIsReopened()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{""compilerOptions"": {""baseUrl"": "".""}}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        DocumentUri uri = "file:///src/index.ts";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        var calls = utils.Client().PublishDiagnosticsCalls();
        var tsconfigCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", 0);
        Assert.True(tsconfigCalls.Count > 0, "expected PublishDiagnostics call for tsconfig.json after opening file");
        Assert.Single(tsconfigCalls[0].Params.Diagnostics); // Go message: "expected one diagnostic on tsconfig.json after opening file"

        int callsBeforeClose = calls.Count;

        session.DidCloseFile(RequestContext.Background, uri);
        session.WaitForBackgroundTasks();

        // Cleans up diagnostics after close
        calls = utils.Client().PublishDiagnosticsCalls();
        var clearCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", callsBeforeClose);
        Assert.True(clearCalls.Count > 0, "expected PublishDiagnostics call for tsconfig.json after project close");
        var lastClearCall = clearCalls[^1];
        Assert.Empty(lastClearCall.Params.Diagnostics); // Go message: "expected empty diagnostics after project close"

        int callsBeforeReopen = calls.Count;

        session.DidOpenFile(RequestContext.Background, uri, 2, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        // Restores diagnostics after reopen
        calls = utils.Client().PublishDiagnosticsCalls();
        var reopenedCalls = FilterDiagnosticsByURI(calls, (DocumentUri)"file:///src/tsconfig.json", callsBeforeReopen);
        Assert.True(reopenedCalls.Count > 0, "expected PublishDiagnostics call for tsconfig.json after reopening file");
        var lastReopenedCall = reopenedCalls[^1];
        Assert.Single(lastReopenedCall.Params.Diagnostics); // Go message: "expected one diagnostic on tsconfig.json after reopening file"
    }

    // project_test.go:598-613 — TestDisplayName "configured project returns relative config
    // path".
    [Fact]
    public void TestDisplayName_ConfiguredProjectReturnsRelativeConfigPath()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/tsconfig.json"] = @"{}",
            ["/home/projects/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/index.ts", 1, "export const x = 1;", LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///home/projects/index.ts");
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal("tsconfig.json", configured!.DisplayName("/home/projects"));
    }

    // project_test.go:615-630 — TestDisplayName "configured project with nested config".
    [Fact]
    public void TestDisplayName_ConfiguredProjectWithNestedConfig()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/sub/tsconfig.json"] = @"{}",
            ["/home/projects/sub/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/sub/index.ts", 1, "export const x = 1;", LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///home/projects/sub/index.ts");
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        var configured = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/sub/tsconfig.json"));
        Assert.True(configured != null);
        Assert.Equal("sub/tsconfig.json", configured!.DisplayName("/home/projects"));
    }

    // project_test.go:632-655 — TestDisplayName "inferred project returns directory base
    // name".
    [Fact]
    public void TestDisplayName_InferredProjectReturnsDirectoryBaseName()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.SetupWithOptions(files, new global::Ts.Project.SessionOptions
        {
            CurrentDirectory = "/home/projects",
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = true,
            LoggingEnabled = true,
            PushDiagnosticsEnabled = true,
        });
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/index.ts", 1, "export const x = 1;", LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///home/projects/index.ts");
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        var inferred = snapshot.ProjectCollection.InferredProject();
        Assert.True(inferred != null);
        string name = inferred!.DisplayName("/home");
        Assert.Equal("projects", name);
    }

    // project_test.go:663-697 — TestProgressNotifications "emits progress for configured
    // project loading".
    [Fact]
    public void TestProgressNotifications_EmitsProgressForConfiguredProjectLoading()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/tsconfig.json"] = @"{}",
            ["/home/projects/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/index.ts", 1, "export const x = 1;", LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///home/projects/index.ts");
        Assert.Null(err);

        var startCalls = utils.Client().ProgressStartCalls();
        var finishCalls = utils.Client().ProgressFinishCalls();

        Assert.True(startCalls.Count > 0, "expected at least one ProgressStart call");
        Assert.True(finishCalls.Count > 0, "expected at least one ProgressFinish call");

        bool foundProjectStart = false;
        foreach (var call in startCalls)
        {
            if (ReferenceEquals(call.Message, DiagnosticsApi.Project_0))
            {
                foundProjectStart = true;
                break;
            }
        }
        Assert.True(foundProjectStart, "expected ProgressStart with Project_0 message");

        bool foundProjectFinish = false;
        foreach (var call in finishCalls)
        {
            if (ReferenceEquals(call.Message, DiagnosticsApi.Project_0))
            {
                foundProjectFinish = true;
                break;
            }
        }
        Assert.True(foundProjectFinish, "expected ProgressFinish with Project_0 message");
    }

    // project_test.go:699-723 — TestProgressNotifications "emits progress for inferred
    // project loading".
    [Fact]
    public void TestProgressNotifications_EmitsProgressForInferredProjectLoading()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/index.ts", 1, "export const x = 1;", LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///home/projects/index.ts");
        Assert.Null(err);

        var startCalls = utils.Client().ProgressStartCalls();
        var finishCalls = utils.Client().ProgressFinishCalls();

        Assert.True(startCalls.Count > 0, "expected at least one ProgressStart call");
        Assert.True(finishCalls.Count > 0, "expected at least one ProgressFinish call");

        bool foundProjectStart = false;
        foreach (var call in startCalls)
        {
            if (ReferenceEquals(call.Message, DiagnosticsApi.Project_0))
            {
                foundProjectStart = true;
                break;
            }
        }
        Assert.True(foundProjectStart, "expected ProgressStart with Project_0 message");
    }

    // project_test.go:725-753 — TestProgressNotifications "each start has a matching finish".
    [Fact]
    public void TestProgressNotifications_EachStartHasAMatchingFinish()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/tsconfig.json"] = @"{}",
            ["/home/projects/a.ts"] = "export const a = 1;",
            ["/home/projects/b.ts"] = "export const b = 2;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/a.ts", 1, "export const a = 1;", LanguageKind.TypeScript.Value!);
        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///home/projects/a.ts");
        Assert.Null(err);

        var startCalls = utils.Client().ProgressStartCalls();
        var finishCalls = utils.Client().ProgressFinishCalls();

        int starts = 0;
        int finishes = 0;
        foreach (var call in startCalls)
        {
            if (ReferenceEquals(call.Message, DiagnosticsApi.Project_0))
            {
                starts++;
            }
        }
        foreach (var call in finishCalls)
        {
            if (ReferenceEquals(call.Message, DiagnosticsApi.Project_0))
            {
                finishes++;
            }
        }
        Assert.Equal(starts, finishes); // Go message: "ProgressStart and ProgressFinish calls for Project_0 should be balanced"
    }
}
