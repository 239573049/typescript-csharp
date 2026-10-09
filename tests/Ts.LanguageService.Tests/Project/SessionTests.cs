// Ported from tsgo internal/project/session_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one xUnit class; t.Run subtests → [Fact]s named
//    Parent_Child (doubly nested groups flatten with underscores). The
//    "change program file not in tsconfig root files" workspaceDir loop → [Theory] rows.
//  * gotest.tools assert.Equal(t, actual, expected) → Assert.Equal(expected, actual)
//    (argument order swapped); identity comparisons → Assert.Same / Assert.NotSame;
//    assert.Assert(cond, msg, args...) → Assert.True(cond, $"msg") with the Go message
//    inlined; assert.Check(cond) → Assert.True(cond) / Assert.Null / Assert.NotNull.
//  * Go `err := utils.FS().WriteFile/Remove(...); assert.NilError(t, err)` → direct calls:
//    the C# IFileSystem methods throw on failure (05 §4), so a failed operation fails the
//    test exactly where Go's err assertion would.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background;
//    projecttestutil.WithRequestID(t.Context()) → SessionUtils.WithRequestID(RequestContext.Background);
//    tspath.Path(...) → new global::Ts.TsPath.Path(...). The Go lowercased
//    "/home/projects/ts/..." path spellings carry over verbatim (the FS is case-insensitive
//    and Path folds directory case).
//  * program.GetSemanticDiagnostics takes the request CancellationToken in C#
//    (RequestContext.Cancellation) and runs under LargeStackExecutor (AGENTS.md 铁律 8),
//    matching the ProjectServiceTests idiom.
//  * Go `t.Logf` diagnostic loops are dropped (assertion-bearing code is kept verbatim).
//  * `vfstest.Symlink(...)` → VfsTestUtil.Symlink(...); `fs.ModePerm` →
//    (FileMode)0b_111_111_111 /* 0o777 */.

using System;
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

namespace Ts.Project.Tests;

public sealed class SessionTests
{
    // session_test.go:28-40 — defaultFiles.
    private static Dictionary<string, object?> DefaultFiles()
    {
        return new Dictionary<string, object?>
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
        };
    }

    // session_test.go:44-64 — "DidOpenFile" → "create configured project".
    [Fact]
    public void DidOpenFile_CreateConfiguredProject()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);
        var snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());

        var configuredProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json"));
        Assert.NotNull(configuredProject);

        // Get language service to access the program
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") != null);
        Assert.Equal("export const x = 1;", program.GetSourceFile("/home/projects/TS/p1/src/x.ts")!.Text());
    }

    // session_test.go:66-81 — "DidOpenFile" → "create inferred project".
    [Fact]
    public void DidOpenFile_CreateInferredProject()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/config.ts", 1, (string)defaultFiles["/home/projects/TS/p1/config.ts"]!, LanguageKind.TypeScript.Value!);

        // Find tsconfig, load, notice config.ts is not included, create inferred project
        var snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);

        // Should have both configured project (for tsconfig.json) and inferred project
        var configuredProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/projects/ts/p1/tsconfig.json"));
        var inferredProject = snapshot.ProjectCollection.InferredProject();
        Assert.NotNull(configuredProject);
        Assert.NotNull(inferredProject);
    }

    // session_test.go:83-95 — "DidOpenFile" → "inferred project for in-memory files".
    [Fact]
    public void DidOpenFile_InferredProjectForInMemoryFiles()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/config.ts", 1, (string)defaultFiles["/home/projects/TS/p1/config.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-1", 1, "x", LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-2", 1, "y", LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;

        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);
    }

    // session_test.go:97-113 — "DidOpenFile" → "inferred project JS file".
    [Fact]
    public void DidOpenFile_InferredProjectJSFile()
    {
        var jsFiles = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/index.js"] = @"import { x } from ""./x"";",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(jsFiles);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/index.js", 1, (string)jsFiles["/home/projects/TS/p1/index.js"]!, LanguageKind.JavaScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());

        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/index.js");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/index.js") != null);
    }

    // session_test.go:116-158 — "watchChange and didOpen in same batch rebuilds program".
    [Fact]
    public void WatchChangeAndDidOpenInSameBatchRebuildsProgram()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""strict"": true
				}
			}",
            ["/home/projects/TS/p1/src/a.ts"] = "export const a = 1;\n",
            ["/home/projects/TS/p1/src/b.ts"] = "export const b = 1;\n",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        string oldContent = (string)files["/home/projects/TS/p1/src/a.ts"]!;

        // Open b.ts to create the project; a.ts is included via tsconfig.
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/b.ts", 1, (string)files["/home/projects/TS/p1/src/b.ts"]!, LanguageKind.TypeScript.Value!);

        // Verify a.ts is in the program with the original content.
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/b.ts");
        Assert.Null(err);
        Assert.Equal(oldContent, ls!.GetProgram().GetSourceFile("/home/projects/TS/p1/src/a.ts")!.Text());

        // Modify a.ts on disk (simulate a build tool or git checkout).
        string newContent = "export const a = 2;\nexport const extra = true;\n";
        utils.FS().WriteFile("/home/projects/TS/p1/src/a.ts", newContent);

        // Queue a watch event for the disk change (not flushed yet).
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent { Type = FileChangeType.Changed, Uri = (DocumentUri)"file:///home/projects/TS/p1/src/a.ts" },
        ]);

        // Open a.ts in the editor—flushes both watch event and didOpen together.
        // Before the fix, processChanges would discard the watch event,
        // leaving the project with a stale SourceFile and a mismatched line map.
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/a.ts", 1, newContent, LanguageKind.TypeScript.Value!);

        // The program's SourceFile must reflect the overlay (new) content.
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/a.ts");
        Assert.Null(err);
        Assert.Equal(newContent, ls!.GetProgram().GetSourceFile("/home/projects/TS/p1/src/a.ts")!.Text());
    }

    // session_test.go:162-197 — "DidChangeFile" → "update file and program".
    [Fact]
    public void DidChangeFile_UpdateFileAndProgram()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();

        session.DidChangeFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 17 },
                        End = new Position { Line = 0, Character = 18 },
                    },
                    Text = "2",
                },
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        var programAfter = lsAfter!.GetProgram();

        // Program should change due to the file content change
        Assert.True(programAfter != programBefore);
        Assert.Equal("export const x = 2;", programAfter.GetSourceFile("/home/projects/TS/p1/src/x.ts")!.Text());
    }

    // session_test.go:199-235 — "DidChangeFile" → "update untitled file".
    [Fact]
    public void DidChangeFile_UpdateUntitledFile()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);

        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-1", 1, "let x = 1;", LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "untitled:Untitled-1");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();
        string untitledFileName = global::Ts.LsProto.DocumentUriExtensions.FileName(new DocumentUri("untitled:Untitled-1"));
        Assert.Equal("let x = 1;", programBefore.GetSourceFile(untitledFileName)!.Text());

        session.DidChangeFile(RequestContext.Background, "untitled:Untitled-1", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 8 },
                        End = new Position { Line = 0, Character = 9 },
                    },
                    Text = "2",
                },
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "untitled:Untitled-1");
        Assert.Null(err);
        var programAfter = lsAfter!.GetProgram();

        Assert.True(programAfter != programBefore);
        Assert.Equal("let x = 2;", programAfter.GetSourceFile(untitledFileName)!.Text());
    }

    // session_test.go:237-272 — "DidChangeFile" → "unchanged source files are reused".
    [Fact]
    public void DidChangeFile_UnchangedSourceFilesAreReused()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();
        var indexFileBefore = programBefore.GetSourceFile("/home/projects/TS/p1/src/index.ts");

        session.DidChangeFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 0 },
                        End = new Position { Line = 0, Character = 0 },
                    },
                    Text = ";",
                },
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        var programAfter = lsAfter!.GetProgram();

        // Unchanged file should be reused
        Assert.Same(indexFileBefore, programAfter.GetSourceFile("/home/projects/TS/p1/src/index.ts"));
    }

    // session_test.go:274-312 — "DidChangeFile" → "change can pull in new files".
    [Fact]
    public void DidChangeFile_ChangeCanPullInNewFiles()
    {
        var files = DefaultFiles();
        files["/home/projects/TS/p1/y.ts"] = @"export const y = 2;";
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Verify y.ts is not initially in the program
        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();
        Assert.True(programBefore.GetSourceFile("/home/projects/TS/p1/y.ts") == null);

        session.DidChangeFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 0 },
                        End = new Position { Line = 0, Character = 0 },
                    },
                    Text = @"import { y } from ""../y"";\n",
                },
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programAfter = lsAfter!.GetProgram();

        // y.ts should now be included in the program
        Assert.True(programAfter.GetSourceFile("/home/projects/TS/p1/y.ts") != null);
    }

    // session_test.go:314-373 — "DidChangeFile" → "single-file change followed by config
    // change reloads program".
    [Fact]
    public void DidChangeFile_SingleFileChangeFollowedByConfigChangeReloadsProgram()
    {
        var files = DefaultFiles();
        files["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""include"": [""src/index.ts""]
			}";
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();
        Assert.Equal(2, programBefore.SourceFiles().Length);

        session.DidChangeFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 2,
        [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                Partial = new TextDocumentContentChangePartial
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 0 },
                        End = new Position { Line = 0, Character = 0 },
                    },
                    Text = "\n",
                },
            },
        ]);

        utils.FS().WriteFile("/home/projects/TS/p1/tsconfig.json", @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""include"": [""./**/*""]
			}");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/tsconfig.json",
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programAfter = lsAfter!.GetProgram();
        Assert.Equal(3, programAfter.SourceFiles().Length);
    }

    // session_test.go:380-406 — "DidCloseFile" → "Configured projects" → "delete a file,
    // close it, recreate it".
    [Fact]
    public void DidCloseFile_ConfiguredProjects_DeleteAFileCloseItRecreateIt()
    {
        var files = DefaultFiles();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)files["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().Remove("/home/projects/TS/p1/src/x.ts");

        session.DidCloseFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") == null);

        utils.FS().WriteFile("/home/projects/TS/p1/src/x.ts", "");

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, "", LanguageKind.TypeScript.Value!);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") != null);
        Assert.Equal("", program.GetSourceFile("/home/projects/TS/p1/src/x.ts")!.Text());
    }

    // session_test.go:411-440 — "DidCloseFile" → "Inferred projects" → "delete a file,
    // close it, recreate it".
    [Fact]
    public void DidCloseFile_InferredProjects_DeleteAFileCloseItRecreateIt()
    {
        var files = DefaultFiles();
        files.Remove("/home/projects/TS/p1/tsconfig.json");
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)files["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().Remove("/home/projects/TS/p1/src/x.ts");

        session.DidCloseFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");

        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") == null);

        utils.FS().WriteFile("/home/projects/TS/p1/src/x.ts", "");

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, "", LanguageKind.TypeScript.Value!);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") != null);
        Assert.Equal("", program.GetSourceFile("/home/projects/TS/p1/src/x.ts")!.Text());
    }

    // session_test.go:442-449 — "DidCloseFile" → "Inferred projects" → "close untitled file".
    [Fact]
    public void DidCloseFile_InferredProjects_CloseUntitledFile()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);

        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-1", 1, "let x = 1;", LanguageKind.TypeScript.Value!);
        session.DidCloseFile(RequestContext.Background, "untitled:Untitled-1");
        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-2", 1, "", LanguageKind.TypeScript.Value!);
    }

    // session_test.go:455-480 — "DidSaveFile" → "save event first".
    [Fact]
    public void DidSaveFile_SaveEventFirst()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.Equal(1ul, snapshot.ID());

        session.DidSaveFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/src/index.ts",
            },
        ]);

        session.WaitForBackgroundTasks();
        snapshot = session.Snapshot()!;
        // We didn't need a snapshot change, but the session overlays should be updated.
        Assert.Equal(1ul, snapshot.ID());

        // Open another file to force a snapshot update so we can see the changes.
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.True(snapshot.GetFile("/home/projects/TS/p1/src/index.ts")!.MatchesDiskText());
    }

    // session_test.go:482-507 — "DidSaveFile" → "watch event first".
    [Fact]
    public void DidSaveFile_WatchEventFirst()
    {
        var defaultFiles = DefaultFiles();
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(defaultFiles);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.Equal(1ul, snapshot.ID());

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/src/index.ts",
            },
        ]);
        session.DidSaveFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");

        session.WaitForBackgroundTasks();
        snapshot = session.Snapshot()!;
        // We didn't need a snapshot change, but the session overlays should be updated.
        Assert.Equal(1ul, snapshot.ID());

        // Open another file to force a snapshot update so we can see the changes.
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)defaultFiles["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.True(snapshot.GetFile("/home/projects/TS/p1/src/index.ts")!.MatchesDiskText());
    }

    // session_test.go:512-545 — "Source file sharing" → "projects with similar options share
    // source files".
    [Fact]
    public void SourceFileSharing_ProjectsWithSimilarOptionsShareSourceFiles()
    {
        var files = DefaultFiles();
        files["/home/projects/TS/p2/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true,
					""noCheck"": true
				}
			}";
        files["/home/projects/TS/p2/src/index.ts"] = @"import { x } from ""../../p1/src/x"";";
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p2/src/index.ts", 1, (string)files["/home/projects/TS/p2/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);

        (global::Ts.Ls.LanguageService? ls1, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program1 = ls1!.GetProgram();

        (global::Ts.Ls.LanguageService? ls2, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p2/src/index.ts");
        Assert.Null(err);
        var program2 = ls2!.GetProgram();

        Assert.Same(program1.GetSourceFile("/home/projects/TS/p1/src/x.ts"), program2.GetSourceFile("/home/projects/TS/p1/src/x.ts"));
    }

    // session_test.go:547-580 — "Source file sharing" → "projects with different options do
    // not share source files".
    [Fact]
    public void SourceFileSharing_ProjectsWithDifferentOptionsDoNotShareSourceFiles()
    {
        var files = DefaultFiles();
        files["/home/projects/TS/p2/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true,
					""moduleDetection"": ""auto""
				},
				""include"": [""src""]
			}";
        files["/home/projects/TS/p2/src/index.ts"] = @"import { x } from ""../../p1/src/x"";";
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p2/src/index.ts", 1, (string)files["/home/projects/TS/p2/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);

        (global::Ts.Ls.LanguageService? ls1, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program1 = ls1!.GetProgram();

        (global::Ts.Ls.LanguageService? ls2, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p2/src/index.ts");
        Assert.Null(err);
        var program2 = ls2!.GetProgram();

        var x1 = program1.GetSourceFile("/home/projects/TS/p1/src/x.ts");
        var x2 = program2.GetSourceFile("/home/projects/TS/p1/src/x.ts");
        Assert.True(x1 != null && x2 != null);
        Assert.True(x1 != x2);
    }

    // session_test.go:586-612 — "DidChangeWatchedFiles" → "change open file".
    [Fact]
    public void DidChangeWatchedFiles_ChangeOpenFile()
    {
        var files = DefaultFiles();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)files["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();

        utils.FS().WriteFile("/home/projects/TS/p1/src/x.ts", @"export const x = 2;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/src/x.ts",
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        // Program should remain the same since the file is open and changes are handled through DidChangeTextDocument
        Assert.Same(programBefore, lsAfter!.GetProgram());
    }

    // session_test.go:614-638 — "DidChangeWatchedFiles" → "change closed program file".
    [Fact]
    public void DidChangeWatchedFiles_ChangeClosedProgramFile()
    {
        var files = DefaultFiles();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();

        utils.FS().WriteFile("/home/projects/TS/p1/src/x.ts", @"export const x = 2;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/src/x.ts",
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        Assert.True(lsAfter!.GetProgram() != programBefore);
    }

    // session_test.go:640-689 — "DidChangeWatchedFiles" → "change program file not in
    // tsconfig root files" (workspaceDir loop → Theory rows).
    [Theory]
    [InlineData("/")]
    [InlineData("/home/projects/TS/p1")]
    [InlineData("/somewhere/else/entirely")]
    public void DidChangeWatchedFiles_ChangeProgramFileNotInTsconfigRootFiles(string workspaceDir)
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""strict"": true
				},
				""files"": [""src/index.ts""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { x } from ""../../x"";",
            ["/home/projects/TS/x.ts"] = @"export const x = 1;",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithOptions(files, new global::Ts.Project.SessionOptions
        {
            CurrentDirectory = workspaceDir,
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation,
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = true,
            LoggingEnabled = true,
        });
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? lsBefore, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var programBefore = lsBefore!.GetProgram();
        session.WaitForBackgroundTasks();

        Assert.True(utils.WatchesFile("/home/projects/ts/x.ts"));

        utils.FS().WriteFile("/home/projects/TS/x.ts", @"export const x = 2;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/x.ts",
            },
        ]);

        (global::Ts.Ls.LanguageService? lsAfter, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        Assert.True(lsAfter!.GetProgram() != programBefore);
    }

    // session_test.go:691-733 — "DidChangeWatchedFiles" → "change config file".
    [Fact]
    public void DidChangeWatchedFiles_ChangeConfigFile()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""strict"": false
				}
			}",
            ["/home/projects/TS/p1/src/x.ts"] = @"export declare const x: number | undefined;",
            ["/home/projects/TS/p1/src/index.ts"] = @"
					import { x } from ""./x"";
					let y: number = x;",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        utils.FS().WriteFile("/home/projects/TS/p1/tsconfig.json", @"{
				""compilerOptions"": {
					""noLib"": false,
					""strict"": true
				}
			}");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/tsconfig.json",
            },
        ]);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
    }

    // session_test.go:735-778 — "DidChangeWatchedFiles" → "delete explicitly included file".
    [Fact]
    public void DidChangeWatchedFiles_DeleteExplicitlyIncludedFile()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""src/index.ts"", ""src/x.ts""]
			}",
            ["/home/projects/TS/p1/src/x.ts"] = @"export declare const x: number | undefined;",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { x } from ""./x"";",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/x.ts"));
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        utils.FS().Remove("/home/projects/TS/p1/src/x.ts");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = "file:///home/projects/TS/p1/src/x.ts",
            },
        ]);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        // File name is still in the command line, was explicitly included
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/x.ts"));
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/x.ts") == null);

        // Open file to trigger cleanup
        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-1", 1, "", LanguageKind.TypeScript.Value!);
        var snapshot = session.Snapshot()!;
        Assert.True(snapshot.GetFile("/home/projects/TS/p1/src/x.ts") == null);
    }

    // session_test.go:780-822 — "DidChangeWatchedFiles" → "delete wildcard included file".
    [Fact]
    public void DidChangeWatchedFiles_DeleteWildcardIncludedFile()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"let x = 2;",
            ["/home/projects/TS/p1/src/x.ts"] = @"let y = x;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts", 1, (string)files["/home/projects/TS/p1/src/x.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/index.ts"));
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/x.ts"))));

        utils.FS().Remove("/home/projects/TS/p1/src/index.ts");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = "file:///home/projects/TS/p1/src/index.ts",
            },
        ]);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/x.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        // File name is gone from the command line, was originally included via wildcard
        Assert.True(!System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/index.ts"));
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/x.ts"))));

        // Open file to trigger cleanup
        session.DidOpenFile(RequestContext.Background, "untitled:Untitled-1", 1, "", LanguageKind.TypeScript.Value!);
        var snapshot = session.Snapshot()!;
        Assert.True(snapshot.GetFile("/home/projects/TS/p1/src/index.ts") == null);
    }

    // session_test.go:824-868 — "DidChangeWatchedFiles" → "delete directory with wildcard
    // included files".
    [Fact]
    public void DidChangeWatchedFiles_DeleteDirectoryWithWildcardIncludedFiles()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { x } from ""./sub/x"";",
            ["/home/projects/TS/p1/src/sub/x.ts"] = @"export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/sub/x.ts"));
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        // Delete the entire subdirectory from the file system.
        utils.FS().Remove("/home/projects/TS/p1/src/sub");

        // When a directory is deleted, the client typically sends a single deletion
        // event for the directory itself. Because the registered glob pattern includes
        // file extensions (e.g. **/*.{ts,...}), the directory path does not match
        // and the event is filtered out, so the server is never notified.
        // Simulate this by sending a delete event for the directory URI.
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = "file:///home/projects/TS/p1/src/sub",
            },
        ]);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        // The directory was deleted, so the file should no longer be in the program.
        Assert.True(!System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/sub/x.ts"));
        // The import should now be an error since the module is missing.
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
    }

    // session_test.go:870-912 — "DidChangeWatchedFiles" → "delete directory with
    // program-only files".
    [Fact]
    public void DidChangeWatchedFiles_DeleteDirectoryWithProgramOnlyFiles()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""src/index.ts""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { x } from ""./sub/x"";",
            ["/home/projects/TS/p1/src/sub/x.ts"] = @"export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/index.ts"));
        // x.ts is not in "files" but is pulled in via the import.
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/sub/x.ts") != null);
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        // Delete the entire subdirectory from the file system.
        utils.FS().Remove("/home/projects/TS/p1/src/sub");

        // Send a delete event for the directory URI.
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = "file:///home/projects/TS/p1/src/sub",
            },
        ]);

        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        // The directory was deleted, so the file should no longer be resolvable.
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/sub/x.ts") == null);
        // The import should now be an error since the module is missing.
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
    }

    // session_test.go:914-955 — "DidChangeWatchedFiles" → "delete sibling folder schedules
    // diagnostics refresh".
    [Fact]
    public void DidChangeWatchedFiles_DeleteSiblingFolderSchedulesDiagnosticsRefresh()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""index.ts""]
			}",
            ["/home/projects/TS/p1/index.ts"] = @"import { content } from ""./f/content"";

export const value = content;",
            ["/home/projects/TS/p1/f/content.ts"] = @"export const content = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        DocumentUri contentURI = "file:///home/projects/TS/p1/f/content.ts";
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/index.ts", 1, (string)files["/home/projects/TS/p1/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, contentURI, 1, (string)files["/home/projects/TS/p1/f/content.ts"]!, LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        int baselineRefreshCount = utils.Client().RefreshDiagnosticsCalls().Count;

        utils.FS().Remove("/home/projects/TS/p1/f");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = "file:///home/projects/TS/p1/f",
            },
        ]);
        session.DidCloseFile(RequestContext.Background, contentURI);
        session.WaitForBackgroundTasks();

        int refreshCount = utils.Client().RefreshDiagnosticsCalls().Count;
        Assert.True(refreshCount > baselineRefreshCount,
            $"expected RefreshDiagnostics to be called after deleting /home/projects/TS/p1/f, got {refreshCount} calls (baseline {baselineRefreshCount})");
    }

    // session_test.go:957-1000 — "DidChangeWatchedFiles" → "delete sibling folder schedules
    // diagnostics refresh after opening third file".
    [Fact]
    public void DidChangeWatchedFiles_DeleteSiblingFolderSchedulesDiagnosticsRefreshAfterOpeningThirdFile()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""index.ts"", ""third.ts""]
			}",
            ["/home/projects/TS/p1/index.ts"] = @"import { content } from ""./f/content"";

export const value = content;",
            ["/home/projects/TS/p1/f/content.ts"] = @"export const content = 1;",
            ["/home/projects/TS/p1/third.ts"] = @"export const third = 3;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        DocumentUri contentURI = "file:///home/projects/TS/p1/f/content.ts";
        DocumentUri thirdURI = "file:///home/projects/TS/p1/third.ts";
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/index.ts", 1, (string)files["/home/projects/TS/p1/index.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, contentURI, 1, (string)files["/home/projects/TS/p1/f/content.ts"]!, LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        int baselineRefreshCount = utils.Client().RefreshDiagnosticsCalls().Count;

        utils.FS().Remove("/home/projects/TS/p1/f");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Deleted,
                Uri = "file:///home/projects/TS/p1/f",
            },
        ]);
        session.DidOpenFile(RequestContext.Background, thirdURI, 1, (string)files["/home/projects/TS/p1/third.ts"]!, LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();

        int refreshCount = utils.Client().RefreshDiagnosticsCalls().Count;
        Assert.True(refreshCount > baselineRefreshCount,
            $"expected RefreshDiagnostics to be called after deleting /home/projects/TS/p1/f and opening /home/projects/TS/p1/third.ts, got {refreshCount} calls (baseline {baselineRefreshCount})");
    }

    // session_test.go:1002-1040 — "DidChangeWatchedFiles" → "create explicitly included
    // file".
    [Fact]
    public void DidChangeWatchedFiles_CreateExplicitlyIncludedFile()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""src/index.ts"", ""src/y.ts""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { y } from ""./y"";",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();

        // Initially should have an error because y.ts is missing
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        // Add the missing file
        utils.FS().WriteFile("/home/projects/TS/p1/src/y.ts", @"export const y = 1;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/y.ts",
            },
        ]);

        // Error should be resolved
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/y.ts") != null);
    }

    // session_test.go:1042-1080 — "DidChangeWatchedFiles" → "create failed lookup location".
    [Fact]
    public void DidChangeWatchedFiles_CreateFailedLookupLocation()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""src/index.ts""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { z } from ""./z"";",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();

        // Initially should have an error because z.ts is missing
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        // Add a new file through failed lookup watch
        utils.FS().WriteFile("/home/projects/TS/p1/src/z.ts", @"export const z = 1;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/z.ts",
            },
        ]);

        // Error should be resolved and the new file should be included in the program
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/z.ts") != null);
    }

    // session_test.go:1082-1120 — "DidChangeWatchedFiles" → "create wildcard included file".
    [Fact]
    public void DidChangeWatchedFiles_CreateWildcardIncludedFile()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"a;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();

        // Initially should have an error because declaration for 'a' is missing
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        // Add a new file through wildcard watch
        utils.FS().WriteFile("/home/projects/TS/p1/src/a.ts", @"const a = 1;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/a.ts",
            },
        ]);

        // Error should be resolved and the new file should be included in the program
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/a.ts") != null);
    }

    // session_test.go:1122-1168 — "DidChangeWatchedFiles" → "irrelevant extension changes
    // are filtered out".
    [Fact]
    public void DidChangeWatchedFiles_IrrelevantExtensionChangesAreFilteredOut()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"export const x = 1;",
            ["/home/projects/TS/p1/src/data.txt"] = @"some text",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
        var oldProgram = program;

        // Modify an irrelevant file and send change/create events for files with
        // extensions that are not relevant to TypeScript compilation.
        utils.FS().WriteFile("/home/projects/TS/p1/src/data.txt", @"updated text");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/TS/p1/src/data.txt",
            },
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/styles.css",
            },
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/image.png",
            },
        ]);

        // The program should not have been rebuilt since all events had irrelevant extensions.
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.Same(oldProgram, program); // Go message: "program should not be rebuilt for irrelevant extension changes"
    }

    // session_test.go:1170-1220 — "DidChangeWatchedFiles" → "pnpm install links local
    // package".
    [Fact]
    public void DidChangeWatchedFiles_PnpmInstallLinksLocalPackage()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/pnpm/pnpm-workspace.yaml"] = @"packages:
  - 'packages/*'",
            ["/home/projects/pnpm/packages/alpha/package.json"] = @"{ ""name"": ""@repo/alpha"", ""main"": ""index.ts"" }",
            ["/home/projects/pnpm/packages/alpha/tsconfig.json"] = @"{
				""compilerOptions"": { ""noLib"": true, ""composite"": true }
			}",
            ["/home/projects/pnpm/packages/alpha/index.ts"] = @"export const alpha = 1;",
            ["/home/projects/pnpm/packages/beta/package.json"] = @"{ ""name"": ""@repo/beta"" }",
            ["/home/projects/pnpm/packages/beta/tsconfig.json"] = @"{
				""compilerOptions"": { ""noLib"": true }
			}",
            ["/home/projects/pnpm/packages/beta/index.ts"] = @"import { alpha } from ""@repo/alpha"";",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/pnpm/packages/beta/index.ts", 1, (string)files["/home/projects/pnpm/packages/beta/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Before pnpm install: the import is unresolved because node_modules/@repo/alpha doesn't exist.
        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/pnpm/packages/beta/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/pnpm/packages/beta/index.ts"))));

        // Simulate pnpm install: create a symlink from beta's node_modules/@repo/alpha to packages/alpha.
        var mapFS = (global::Ts.Vfs.VfsTest.MapFS)utils.FsFromFileMap().FSys();
        mapFS.MkdirAll("home/projects/pnpm/packages/beta/node_modules/@repo", (global::Ts.Vfs.FileMode)0b_111_111_111 /* 0o777 */);
        mapFS.AddSymlink("home/projects/pnpm/packages/beta/node_modules/@repo/alpha", "home/projects/pnpm/packages/alpha");

        // Fire watch events mimicking what VS Code sends for a pnpm install.
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent { Type = FileChangeType.Created, Uri = (DocumentUri)"file:///home/projects/pnpm/packages/beta/node_modules" },
            new FileEvent { Type = FileChangeType.Created, Uri = (DocumentUri)"file:///home/projects/pnpm/packages/beta/node_modules/%40repo" },
            new FileEvent { Type = FileChangeType.Created, Uri = (DocumentUri)"file:///home/projects/pnpm/packages/beta/node_modules/%40repo/alpha" },
            new FileEvent { Type = FileChangeType.Created, Uri = (DocumentUri)"file:///home/projects/pnpm/pnpm-lock.yaml" },
            new FileEvent { Type = FileChangeType.Changed, Uri = (DocumentUri)"file:///home/projects/pnpm/packages/beta/node_modules/.bin/tsc" },
            new FileEvent { Type = FileChangeType.Changed, Uri = (DocumentUri)"file:///home/projects/pnpm/packages/beta/node_modules/.bin/tsserver" },
        ]);

        // After pnpm install: the import should resolve.
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/pnpm/packages/beta/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        var diags = global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/pnpm/packages/beta/index.ts")));
        // Go: t.Logf("diagnostic: %s", d.String()) for each — dropped (logging only).
        Assert.Empty(diags);
    }

    // session_test.go:1222-1294 — "DidChangeWatchedFiles" → "symlinked node_modules
    // package.json change invalidates resolution".
    [Fact]
    public void DidChangeWatchedFiles_SymlinkedNodeModulesPackageJsonChangeInvalidatesResolution()
    {
        // Set up a project that imports a symlinked node_modules package.
        // The package resolves via "main" in package.json to dist/index.js.
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/myproject/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""module"": ""nodenext"",
					""moduleResolution"": ""nodenext""
				},
				""files"": [""src/index.ts""]
			}",
            ["/home/projects/myproject/src/index.ts"] = @"import { foo } from ""mylib"";",
            // The real package lives as a sibling directory
            ["/home/projects/mylib/package.json"] = @"{
				""name"": ""mylib"",
				""main"": ""dist/index.js""
			}",
            ["/home/projects/mylib/dist/index.js"] = @"exports.foo = function() { return 1; };",
            ["/home/projects/mylib/dist/index.d.ts"] = @"export declare function foo(): number;",
            // node_modules/mylib is a symlink to the sibling
            ["/home/projects/myproject/node_modules/mylib"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/home/projects/mylib"),
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithOptions(files, new global::Ts.Project.SessionOptions
        {
            CurrentDirectory = "/home/projects/myproject",
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation,
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = true,
            LoggingEnabled = true,
        });
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/myproject/src/index.ts", 1, (string)files["/home/projects/myproject/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Initial state: import resolves successfully via package.json main -> dist/index.d.ts
        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/myproject/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        session.WaitForBackgroundTasks();
        var diags = global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/myproject/src/index.ts")));
        // Go: t.Logf("initial diagnostic: %s", d.String()) for each — dropped (logging only).
        Assert.True(diags.Length == 0, "import should resolve initially");

        // Assert: watched file globs cover the realpath of package.json and dist/index.d.ts.
        // With a workspace dir set, watchers use RelativePattern with a base URI.
        Assert.True(utils.WatchesFile("/home/projects/mylib/package.json"), "realpath of package.json should be watched");
        Assert.True(utils.WatchesFile("/home/projects/mylib/dist/index.d.ts"), "realpath of dist/index.d.ts should be watched");

        // Edit package.json to remove "main" field
        utils.FS().WriteFile("/home/projects/mylib/package.json", @"{
				""name"": ""mylib""
			}");

        // Fire watch event for the realpath of the changed package.json.
        // A real editor would fire this for the realpath since it watches realpaths.
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///home/projects/mylib/package.json",
            },
        ]);

        // After removing "main" from package.json, the import should no longer resolve.
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/myproject/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        diags = global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/myproject/src/index.ts")));
        Assert.True(diags.Length > 0, "import should fail after removing main from package.json");
    }

    // session_test.go:1296-1334 — "DidChangeWatchedFiles" → "create file in non-existent
    // directory".
    [Fact]
    public void DidChangeWatchedFiles_CreateFileInNonExistentDirectory()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""files"": [""src/index.ts""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"import { helper } from ""./lib/helper"";",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Initially should have an error because lib/helper.ts doesn't exist
        // and src/lib/ directory doesn't exist either.
        var requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));

        // Create the directory and file.
        utils.FS().WriteFile("/home/projects/TS/p1/src/lib/helper.ts", @"export const helper = 1;");

        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/lib/helper.ts",
            },
        ]);

        // Error should be resolved.
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, program.GetSourceFile("/home/projects/TS/p1/src/index.ts"))));
        Assert.True(program.GetSourceFile("/home/projects/TS/p1/src/lib/helper.ts") != null);
    }

    // session_test.go:1336-1381 — "DidChangeWatchedFiles" → "create symlink directory
    // matching include pattern".
    [Fact]
    public void DidChangeWatchedFiles_CreateSymlinkDirectoryMatchingIncludePattern()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"export const x = 1;",
            ["/home/projects/TS/shared/utils.ts"] = @"export const util = ""hello"";",
            ["/home/projects/TS/shared/helpers.ts"] = @"export const helper = 42;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts", 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        var program = ls!.GetProgram();

        // Initially, project only has the one file in src/.
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/index.ts"));
        Assert.True(!System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/linked/utils.ts"));
        Assert.True(!System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/linked/helpers.ts"));

        // Create a symlink directory inside src/ that points to the shared directory.
        var mapFS = (global::Ts.Vfs.VfsTest.MapFS)utils.FsFromFileMap().FSys();
        mapFS.AddSymlink("home/projects/TS/p1/src/linked", "home/projects/TS/shared");

        // Send directory creation event (what VS Code sends when a symlink directory appears).
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Created,
                Uri = "file:///home/projects/TS/p1/src/linked",
            },
        ]);

        // After the symlink directory is created, the files inside it should be
        // picked up by the wildcard include pattern.
        (ls, err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        program = ls!.GetProgram();
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/index.ts"));
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/linked/utils.ts"));
        Assert.True(System.Array.Exists(program.CommandLine().ParsedConfig.FileNames!, (string f) => f == "/home/projects/TS/p1/src/linked/helpers.ts"));
    }

    // session_test.go:1384-1407 — "refreshes code lenses and inlay hints when relevant user
    // preferences change".
    [Fact]
    public void RefreshesCodeLensesAndInlayHintsWhenRelevantUserPreferencesChange()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);

        session.Configure(global::Ts.Ls.UserPreferences.NewDefaultUserPreferences());
        // Change user preferences for code lens and inlay hints.
        var newPrefs = session.Config();
        newPrefs.CodeLens.ReferencesCodeLensEnabled = Tristate.True;
        newPrefs.InlayHints.IncludeInlayFunctionLikeReturnTypeHints = Tristate.True;

        session.Configure(newPrefs);

        var codeLensRefreshCalls = utils.Client().RefreshCodeLensCalls();
        var inlayHintsRefreshCalls = utils.Client().RefreshInlayHintsCalls();
        // Go messages: "expected one RefreshCodeLens call after code lens preference change" /
        // "expected one RefreshInlayHints call after inlay hints preference change".
        Assert.Single(codeLensRefreshCalls);
        Assert.Single(inlayHintsRefreshCalls);
    }

    // session_test.go:1409-1434 — "schedules diagnostics refresh when
    // reportStyleChecksAsWarnings changes".
    [Fact]
    public void SchedulesDiagnosticsRefreshWhenReportStyleChecksAsWarningsChanges()
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{}",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        // Record the baseline count of RefreshDiagnostics calls.
        int baselineRefreshCount = utils.Client().RefreshDiagnosticsCalls().Count;

        // Toggle reportStyleChecksAsWarnings (default is true, so set it to false).
        var prefs = global::Ts.Ls.UserPreferences.NewDefaultUserPreferences();
        prefs.ReportStyleChecksAsWarnings = Tristate.False;
        session.Configure(prefs);
        session.WaitForBackgroundTasks();

        int refreshCount = utils.Client().RefreshDiagnosticsCalls().Count;
        Assert.True(refreshCount > baselineRefreshCount,
            $"expected RefreshDiagnostics to be called after reportStyleChecksAsWarnings change, got {refreshCount} calls (baseline {baselineRefreshCount})");
    }

    // session_test.go:1436-1482 — "config parsing".
    // SKIPPED: lsutil.ParseUserPreferences (internal/ls/lsutil/userpreferences.go) is not
    // ported to C# yet; no raw-dictionary preference parser exists in Ts.Ls (same reason as
    // the CustomConfigFileNameTests skips).
    [Fact(Skip = "lsutil.ParseUserPreferences (internal/ls/lsutil/userpreferences.go) is not ported to C# yet; no raw-dictionary preference parser exists in Ts.Ls, Go test TestSession/config parsing")]
    public void ConfigParsing()
    {
        // Ported body (compiles once ParseUserPreferences lands):
        // var files = new Dictionary<string, object?> { ["/src/tsconfig.json"] = "{}", ["/src/index.ts"] = "export const x = 1;" };
        // (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        // session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        // (_, Exception? err) = session.GetLanguageService(RequestContext.Background, (DocumentUri)"file:///src/index.ts");
        // Assert.Null(err);
        //
        // var configMap1 = new Dictionary<string, object?> { ["preferences"] = new Dictionary<string, object?> { ["useAliasesForRenames"] = true, ["quoteStyle"] = "single" }, ["unstable"] = new Dictionary<string, object?> { ["organizeImportsSort"] = "ordinalIgnoreCase" } };
        // session.Configure(UserPreferences.ParseUserPreferences(new Dictionary<string, object?> { ["js/ts"] = configMap1 }));
        // var actualConfig1 = session.Config();
        // var expectedPrefs1 = UserPreferences.NewDefaultUserPreferences();
        // expectedPrefs1.UseAliasesForRename = Tristate.True;
        // expectedPrefs1.QuotePreference = "single";
        // expectedPrefs1.OrganizeImportsSort = OrganizeImportsSort.OrdinalIgnoreCase;
        // Assert.Equivalent(expectedPrefs1, actualConfig1); // Go: assert.DeepEqual
        // ... (configMap2 mirrors with false/"double"/"ordinal")
    }

    // session_test.go:1487-1514 — "language service for closed files" → "closed file in
    // configured project not yet opened".
    [Fact]
    public void LanguageServiceForClosedFiles_ClosedFileInConfiguredProjectNotYetOpened()
    {
        // Set up a project where a tsconfig exists but no files have been opened.
        // Requesting language service for a file covered by that tsconfig should
        // work even though the file was never opened via didOpen.
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""strict"": true
				},
				""include"": [""src""]
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"export const x: number = 1;",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        // Do NOT open any file. Directly request language service for a closed file
        // that belongs to the configured project.
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/p1/src/index.ts");
        Assert.Null(err);
        Assert.True(ls != null);
        var program = ls!.GetProgram();
        Assert.True(program != null);
        var sourceFile = program.GetSourceFile("/home/projects/TS/p1/src/index.ts");
        Assert.True(sourceFile != null);
        Assert.Equal("export const x: number = 1;", sourceFile!.Text());
    }

    // session_test.go:1516-1535 — "language service for closed files" → "closed file with no
    // configured project creates inferred project".
    [Fact]
    public void LanguageServiceForClosedFiles_ClosedFileWithNoConfiguredProjectCreatesInferredProject()
    {
        // Set up a file that has no tsconfig. Requesting language service for it
        // should create an inferred project even though the file was never opened.
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/loose/index.ts"] = @"const greeting: string = ""hello"";",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        // Do NOT open any file. Directly request language service for a closed file
        // that has no configured project.
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///home/projects/TS/loose/index.ts");
        Assert.Null(err);
        Assert.True(ls != null);
        var program = ls!.GetProgram();
        Assert.True(program != null);
        var sourceFile = program.GetSourceFile("/home/projects/TS/loose/index.ts");
        Assert.True(sourceFile != null);
        Assert.Equal(@"const greeting: string = ""hello"";", sourceFile!.Text());
    }

    // session_test.go:1538-1575 — "jsconfig.json used for JS files when tsconfig.json exists
    // in same directory".
    [Fact]
    public void JsconfigJsonUsedForJSFilesWhenTsconfigJsonExistsInSameDirectory()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""strict"": true
				}
			}",
            ["/home/projects/TS/p1/jsconfig.json"] = @"{
				""compilerOptions"": {
					""noLib"": true,
					""checkJs"": true
				}
			}",
            ["/home/projects/TS/p1/index.ts"] = @"export const x: number = 1;",
            ["/home/projects/TS/p1/app.js"] = @"/** @type {number} */ var y = ""not a number"";",
        };
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);

        // Open the JS file - it should be assigned to the jsconfig.json project, not tsconfig.json
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/app.js", 1, (string)files["/home/projects/TS/p1/app.js"]!, LanguageKind.JavaScript.Value!);

        var snapshot = session.Snapshot()!;
        DocumentUri jsURI = "file:///home/projects/TS/p1/app.js";
        var defaultProject = snapshot.GetDefaultProject(jsURI);
        Assert.True(defaultProject != null, "JS file should have a default project");
        Assert.Equal("/home/projects/TS/p1/jsconfig.json", defaultProject!.Name()); // Go message: "JS file should belong to jsconfig.json project, not tsconfig.json"

        // Open the TS file - it should be assigned to tsconfig.json project
        session.DidOpenFile(RequestContext.Background, "file:///home/projects/TS/p1/index.ts", 1, (string)files["/home/projects/TS/p1/index.ts"]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        DocumentUri tsURI = "file:///home/projects/TS/p1/index.ts";
        var defaultTSProject = snapshot.GetDefaultProject(tsURI);
        Assert.True(defaultTSProject != null, "TS file should have a default project");
        Assert.Equal("/home/projects/TS/p1/tsconfig.json", defaultTSProject!.Name()); // Go message: "TS file should belong to tsconfig.json project"
    }
}
