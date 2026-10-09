// Ported from tsgo internal/project/projectreferencesprogram_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one xUnit class; t.Run subtests → [Fact]s named
//    Parent_Subtest.
//  * gotest.tools assert.Equal(t, actual, expected) → Assert.Equal(expected, actual)
//    (argument order swapped); assert.Check(t, x != y) → Assert.NotSame(y, x);
//    assert.Assert(t, x == y) / (a == b) → Assert.Same(b, a).
//  * Go `err := utils.FS().WriteFile(...); assert.NilError(t, err)` → direct calls:
//    the C# IFileSystem methods throw on failure (05 §4), so a failed operation fails
//    the test exactly where Go's err assertion would.
//  * `projecttestutil.WithRequestID(t.Context())` → a RequestContext carrying request
//    id "0" (ProjectTestUtil.WithRequestID), the same request context the language
//    service requests use.
//  * Go builds the package tsconfig.json with core.StringifyJson; core.StringifyJson is
//    still DEFERRED in C# (Core/Core.cs:522), so the same object is written by hand as
//    the equivalent JSON text (identical parse result).
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background.

using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using FileChangeType = global::Ts.LsProto.FileChangeType;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using TextDocumentContentChangePartialOrWholeDocument = global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument;
using TextDocumentContentChangeWholeDocument = global::Ts.LsProto.TextDocumentContentChangeWholeDocument;

namespace Ts.Project.Tests;

public sealed class ProjectReferencesProgramTests
{
    // projectreferencesprogram_test.go:17-23 — "program for referenced project".
    [Fact]
    public void TestProjectReferencesProgram_ProgramForReferencedProject()
    {
        var files = FilesForReferencedProjectProgram(disableSourceOfProjectReferenceRedirect: false);
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        var uri = new DocumentUri("file:///user/username/projects/myproject/main/main.ts");
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/myproject/main/main.ts"]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? file = p.Program!.GetSourceFileByPath(new global::Ts.TsPath.Path("/user/username/projects/myproject/dependency/fns.ts"));
        Assert.NotNull(file);
        global::Ts.Ast.SourceFile? dtsFile = p.Program.GetSourceFileByPath(new global::Ts.TsPath.Path("/user/username/projects/myproject/decls/fns.d.ts"));
        Assert.Null(dtsFile);
    }

    // projectreferencesprogram_test.go:25-75 — "program with disableSourceOfProjectReferenceRedirect".
    [Fact]
    public void TestProjectReferencesProgram_ProgramWithDisableSourceOfProjectReferenceRedirect()
    {
        var files = FilesForReferencedProjectProgram(disableSourceOfProjectReferenceRedirect: true);
        files["/user/username/projects/myproject/decls/fns.d.ts"] = @"
			export declare function fn1(): void;
			export declare function fn2(): void;
			export declare function fn3(): void;
			export declare function fn4(): void;
			export declare function fn5(): void;
		";
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        var uri = new DocumentUri("file:///user/username/projects/myproject/main/main.ts");
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/myproject/main/main.ts"]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? file = p.Program!.GetSourceFileByPath(new global::Ts.TsPath.Path("/user/username/projects/myproject/dependency/fns.ts"));
        Assert.Null(file);
        global::Ts.Ast.SourceFile? dtsFile = p.Program.GetSourceFileByPath(new global::Ts.TsPath.Path("/user/username/projects/myproject/decls/fns.d.ts"));
        Assert.NotNull(dtsFile);
    }

    // projectreferencesprogram_test.go:77-97 — "references through symlink with index and typings".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkWithIndexAndTypings()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferences(preserveSymlinks: false, scope: "");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:99-119 — "references through symlink with index and typings with preserveSymlinks".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkWithIndexAndTypingsWithPreserveSymlinks()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferences(preserveSymlinks: true, scope: "");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:121-141 — "references through symlink with index and typings scoped package".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkWithIndexAndTypingsScopedPackage()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferences(preserveSymlinks: false, scope: "@issue/");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:143-163 — "references through symlink with index and typings with scoped package preserveSymlinks".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkWithIndexAndTypingsWithScopedPackagePreserveSymlinks()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferences(preserveSymlinks: true, scope: "@issue/");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:165-185 — "references through symlink referencing from subFolder".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkReferencingFromSubFolder()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferencesInSubfolder(preserveSymlinks: false, scope: "");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:187-207 — "references through symlink referencing from subFolder with preserveSymlinks".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkReferencingFromSubFolderWithPreserveSymlinks()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferencesInSubfolder(preserveSymlinks: true, scope: "");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:209-229 — "references through symlink referencing from subFolder scoped package".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkReferencingFromSubFolderScopedPackage()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferencesInSubfolder(preserveSymlinks: false, scope: "@issue/");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:231-251 — "references through symlink referencing from subFolder with scoped package preserveSymlinks".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkReferencingFromSubFolderWithScopedPackagePreserveSymlinks()
    {
        (var files, string aTest, string bFoo, string bBar) = FilesForSymlinkReferencesInSubfolder(preserveSymlinks: true, scope: "@issue/");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Empty(snapshot.ProjectCollection.Projects());

        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aTest);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aTest]!, LanguageKind.TypeScript.Value!);

        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
        global::Ts.Project.Project p = projects[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        global::Ts.Ast.SourceFile? fooFile = p.Program!.GetSourceFile(bFoo);
        Assert.NotNull(fooFile);
        global::Ts.Ast.SourceFile? barFile = p.Program.GetSourceFile(bBar);
        Assert.NotNull(barFile);
    }

    // projectreferencesprogram_test.go:253-270 — "references through symlink with directory index subpath (issue 4373)".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkWithDirectoryIndexSubpathIssue4373()
    {
        (var files, string aIndex, string bFile) = FilesForDirectorySubpathSymlinkReferences(scope: "");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aIndex);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aIndex]!, LanguageKind.TypeScript.Value!);

        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        global::Ts.Project.Project p = snapshot.ProjectCollection.Projects()[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        // The import must redirect to source, so the source file is part of the program...
        Assert.NotNull(p.Program!.GetSourceFile(bFile));
        // ...and there must be no `TS2307: Cannot find module 'b/lib/File'` diagnostic.
        RequestContext requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        global::Ts.Ast.Diagnostic[] diagnostics = global::Ts.Core.LargeStackExecutor.Run(() => p.Program.GetSemanticDiagnostics(requestCtx.Cancellation, p.Program.GetSourceFile(aIndex)));
        Assert.Empty(diagnostics);
    }

    // projectreferencesprogram_test.go:272-287 — "references through symlink with directory index subpath scoped package".
    [Fact]
    public void TestProjectReferencesProgram_ReferencesThroughSymlinkWithDirectoryIndexSubpathScopedPackage()
    {
        (var files, string aIndex, string bFile) = FilesForDirectorySubpathSymlinkReferences(scope: "@issue/");
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        DocumentUri uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aIndex);
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files[aIndex]!, LanguageKind.TypeScript.Value!);

        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        global::Ts.Project.Project p = snapshot.ProjectCollection.Projects()[0];
        Assert.Equal(global::Ts.Project.Kind.Configured, p.Kind);

        Assert.NotNull(p.Program!.GetSourceFile(bFile));
        RequestContext requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        global::Ts.Ast.Diagnostic[] diagnostics = global::Ts.Core.LargeStackExecutor.Run(() => p.Program.GetSemanticDiagnostics(requestCtx.Cancellation, p.Program.GetSourceFile(aIndex)));
        Assert.Empty(diagnostics);
    }

    // projectreferencesprogram_test.go:289-313 — "when new file is added to referenced project".
    [Fact]
    public void TestProjectReferencesProgram_WhenNewFileIsAddedToReferencedProject()
    {
        var files = FilesForReferencedProjectProgram(disableSourceOfProjectReferenceRedirect: false);
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/main/main.ts");
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/myproject/main/main.ts"]!, LanguageKind.TypeScript.Value!);
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        global::Ts.Compiler.Program programBefore = snapshot.ProjectCollection.Projects()[0].Program!;

        utils.FS().WriteFile("/user/username/projects/myproject/dependency/fns2.ts", @"export const x = 2;");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Type = FileChangeType.Created,
                Uri = new DocumentUri("file:///user/username/projects/myproject/dependency/fns2.ts"),
            },
        ]);

        (global::Ts.Ls.LanguageService? _, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotSame(programBefore, snapshot.ProjectCollection.Projects()[0].Program!);
    }

    // projectreferencesprogram_test.go:315-413 — "dropped project reference does not crash on later
    // change to the dropped config".
    [Fact]
    public void TestProjectReferencesProgram_DroppedProjectReferenceDoesNotCrashOnLaterChangeToTheDroppedConfig()
    {
        // Regression test for https://github.com/microsoft/typescript-go/issues/3942.
        // A project reference dropped from a tsconfig used to leave a stale entry in
        // the referenced config's retainingProjects. When the referencing project was
        // later deleted and the referenced config subsequently changed, the stale entry
        // named a project that no longer existed, crashing markProjectsAffectedByConfigChanges.
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/myproject/main/tsconfig.json"] = @"{
					""compilerOptions"": {
						""composite"": true
					},
					""references"": [{ ""path"": ""../dependency"" }]
				}",
            ["/user/username/projects/myproject/main/main.ts"] = @"
					import { fn1 } from '../dependency/fns'
					fn1();
				",
            ["/user/username/projects/myproject/dependency/tsconfig.json"] = @"{
					""compilerOptions"": {
						""composite"": true
					}
				}",
            ["/user/username/projects/myproject/dependency/fns.ts"] = @"
					export function fn1() { }
				",
            ["/user/username/projects/myproject/other/tsconfig.json"] = @"{
					""compilerOptions"": {
						""composite"": true
					}
				}",
            ["/user/username/projects/myproject/other/other.ts"] = @"export const y = 1;",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        var mainURI = new DocumentUri("file:///user/username/projects/myproject/main/main.ts");
        string mainContent = (string)files["/user/username/projects/myproject/main/main.ts"]!;
        var otherURI = new DocumentUri("file:///user/username/projects/myproject/other/other.ts");
        string otherContent = (string)files["/user/username/projects/myproject/other/other.ts"]!;

        // 1. Open main.ts. The main project's program resolves the `../dependency`
        //    project reference, so the dependency config's retainingProjects gains
        //    the main project path.
        session.DidOpenFile(RequestContext.Background, mainURI, 1, mainContent, LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();
        global::Ts.Project.Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/dependency/tsconfig.json")));

        // 2. Remove the project reference from main/tsconfig.json and rebuild.
        //    The new program no longer references dependency.
        utils.FS().WriteFile("/user/username/projects/myproject/main/tsconfig.json", @"{
				""compilerOptions"": {
					""composite"": true
				}
			}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = new DocumentUri("file:///user/username/projects/myproject/main/tsconfig.json"),
            },
        ]);
        (global::Ts.Ls.LanguageService? _, System.Exception? err) = session.GetLanguageService(RequestContext.Background, mainURI);
        Assert.Null(err);

        // 3. Close main.ts and open an unrelated file. Opening triggers cleanup of
        //    orphaned configured projects, deleting the main project.
        session.DidCloseFile(RequestContext.Background, mainURI);
        session.DidOpenFile(RequestContext.Background, otherURI, 1, otherContent, LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();
        snapshot = session.Snapshot()!;
        Assert.Null(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/main/tsconfig.json")));
        // Dropping the reference releases main from dependency's retainingProjects,
        // so the now-unreferenced dependency config is cleaned up and no stale entry
        // survives to crash a later config change.
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/dependency/tsconfig.json")));

        // 4. Change dependency/tsconfig.json and flush. This used to copy the stale
        //    retainingProjects into affectedProjects and crash
        //    markProjectsAffectedByConfigChanges loading the deleted main project.
        utils.FS().WriteFile("/user/username/projects/myproject/dependency/tsconfig.json", @"{
				""compilerOptions"": {
					""composite"": true,
					""strict"": true
				}
			}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = new DocumentUri("file:///user/username/projects/myproject/dependency/tsconfig.json"),
            },
        ]);
        (global::Ts.Ls.LanguageService? _, System.Exception? err2) = session.GetLanguageService(RequestContext.Background, otherURI);
        Assert.Null(err2);
        session.WaitForBackgroundTasks();
    }

    // projectreferencesprogram_test.go:416-452 — filesForReferencedProjectProgram.
    private static Dictionary<string, object?> FilesForReferencedProjectProgram(bool disableSourceOfProjectReferenceRedirect)
    {
        return new Dictionary<string, object?>
        {
            ["/user/username/projects/myproject/main/tsconfig.json"] = @"{
				""compilerOptions"": {
					""composite"": true__DISABLE_SOURCE_OF_PROJECT_REFERENCE_REDIRECT__
				},
				""references"": [{ ""path"": ""../dependency"" }]
			}".Replace("__DISABLE_SOURCE_OF_PROJECT_REFERENCE_REDIRECT__", disableSourceOfProjectReferenceRedirect ? @", ""disableSourceOfProjectReferenceRedirect"": true" : ""),
            ["/user/username/projects/myproject/main/main.ts"] = @"
				import {
					fn1,
					fn2,
					fn3,
					fn4,
					fn5
				} from '../decls/fns'
				fn1();
				fn2();
				fn3();
				fn4();
				fn5();
			",
            ["/user/username/projects/myproject/dependency/tsconfig.json"] = @"{
				""compilerOptions"": {
					""composite"": true,
					""declarationDir"": ""../decls""
				},
			}",
            ["/user/username/projects/myproject/dependency/fns.ts"] = @"
				export function fn1() { }
				export function fn2() { }
				export function fn3() { }
				export function fn4() { }
				export function fn5() { }
			",
        };
    }

    // projectreferencesprogram_test.go:454-476 — filesForSymlinkReferences.
    private static (Dictionary<string, object?> Files, string ATest, string BFoo, string BBar) FilesForSymlinkReferences(bool preserveSymlinks, string scope)
    {
        string aTest = "/user/username/projects/myproject/packages/A/src/index.ts";
        string bFoo = "/user/username/projects/myproject/packages/B/src/index.ts";
        string bBar = "/user/username/projects/myproject/packages/B/src/bar.ts";
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/myproject/packages/B/package.json"] = @"{
				""main"": ""lib/index.js"",
				""types"": ""lib/index.d.ts""
			}",
            [aTest] = string.Format(@"
				import {{ foo }} from '{0}b';
				import {{ bar }} from '{0}b/lib/bar';
				foo();
				bar();
			", scope),
            [bFoo] = @"export function foo() { }",
            [bBar] = @"export function bar() { }",
            [string.Format("/user/username/projects/myproject/node_modules/{0}b", scope)] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/packages/B"),
        };
        AddConfigForPackage(files, "A", preserveSymlinks, ["../B"]);
        AddConfigForPackage(files, "B", preserveSymlinks, null);
        return (files, aTest, bFoo, bBar);
    }

    // projectreferencesprogram_test.go:478-497 — filesForSymlinkReferencesInSubfolder.
    private static (Dictionary<string, object?> Files, string ATest, string BFoo, string BBar) FilesForSymlinkReferencesInSubfolder(bool preserveSymlinks, string scope)
    {
        string aTest = "/user/username/projects/myproject/packages/A/src/test.ts";
        string bFoo = "/user/username/projects/myproject/packages/B/src/foo.ts";
        string bBar = "/user/username/projects/myproject/packages/B/src/bar/foo.ts";
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/myproject/packages/B/package.json"] = @"{}",
            ["/user/username/projects/myproject/packages/A/src/test.ts"] = string.Format(@"
				import {{ foo }} from '{0}b/lib/foo';
				import {{ bar }} from '{0}b/lib/bar/foo';
				foo();
				bar();
			", scope),
            [bFoo] = @"export function foo() { }",
            [bBar] = @"export function bar() { }",
            [string.Format("/user/username/projects/myproject/node_modules/{0}b", scope)] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/packages/B"),
        };
        AddConfigForPackage(files, "A", preserveSymlinks, ["../B"]);
        AddConfigForPackage(files, "B", preserveSymlinks, null);
        return (files, aTest, bFoo, bBar);
    }

    // projectreferencesprogram_test.go:499-517 — filesForDirectorySubpathSymlinkReferences.
    private static (Dictionary<string, object?> Files, string AIndex, string BFile) FilesForDirectorySubpathSymlinkReferences(string scope)
    {
        string aIndex = "/user/username/projects/myproject/packages/a/src/index.ts";
        string bFile = "/user/username/projects/myproject/packages/b/src/File/index.ts";
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/myproject/packages/b/package.json"] = @"{
				""main"": ""lib/index.js"",
				""types"": ""lib/index.d.ts""
			}",
            [aIndex] = string.Format(@"
				import {{ helper }} from ""{0}b/lib/File"";
				export const result: number = helper();
			", scope),
            [bFile] = @"export function helper(): number { return 1; }",
            [string.Format("/user/username/projects/myproject/node_modules/{0}b", scope)] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/packages/b"),
        };
        AddConfigForPackage(files, "a", preserveSymlinks: false, ["../b"]);
        AddConfigForPackage(files, "b", preserveSymlinks: false, null);
        return (files, aIndex, bFile);
    }

    // projectreferencesprogram_test.go:519-539 — addConfigForPackage.
    private static void AddConfigForPackage(Dictionary<string, object?> files, string packageName, bool preserveSymlinks, string[]? references)
    {
        var compilerOptions = new List<string>
        {
            @"""outDir"": ""lib""",
            @"""rootDir"": ""src""",
            @"""composite"": true",
        };
        if (preserveSymlinks)
        {
            compilerOptions.Add(@"""preserveSymlinks"": true");
        }
        var referencesToAdd = new List<string>();
        foreach (string @ref in references ?? [])
        {
            referencesToAdd.Add(string.Format(@"{{ ""path"": ""{0}"" }}", @ref));
        }
        files[string.Format("/user/username/projects/myproject/packages/{0}/tsconfig.json", packageName)] = string.Format(@"{{
			""compilerOptions"": {{
				{0}
			}},
			""include"": [""src""],
			""references"": [
				{1}
			]
		}}", string.Join("," + "\n\t\t\t\t", compilerOptions), string.Join("," + "\n\t\t\t\t", referencesToAdd));
    }
}
