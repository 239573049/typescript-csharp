// Ported from tsgo internal/project/projectcollectionbuilder_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go t.Run subtest → one [Fact] named TestProjectCollectionBuilder_<subtest>.
//  * gotest.tools: assert.Equal(t, actual, expected) → Assert.Equal(expected, actual);
//    assert.Assert(t, x != nil[, msg]) → Assert.NotNull(x); `== nil` → Assert.Null(x);
//    assert.DeepEqual(t, actual, []string{...}) → Assert.Equal(expectedArray, actual)
//    (xUnit compares sequences element-wise); assert.NilError(t, err) → Assert.Null(err).
//  * Two Go assert.Equal calls compare interface values by pointer identity
//    (defaultProject == srcProject, requestSnapshot == snapshot) — those are Assert.Same.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build (unreachable, and CS0162 fatal).
//  * files["...].(string) → (string)files["..."]!; map[string]any → Dictionary<string, object?>.
//  * lsproto.LanguageKindTypeScript → LanguageKind.TypeScript.Value!, PositionEncodingKindUTF8
//    → PositionEncodingKind.UTF8, FileChangeTypeChanged → FileChangeType.Changed.
//  * Untitled files (uri "untitled:Untitled-1") are supported in C#: Session.DidOpenFile
//    detects the scheme and routes to an untitled project.

using System;
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

public sealed class ProjectCollectionBuilderTests
{
    // projectcollectionbuilder_test.go:26-60 — "when project found is solution referencing default project directly".
    [Fact]
    public void TestProjectCollectionBuilder_WhenProjectFoundIsSolutionReferencingDefaultProjectDirectly()
    {
        var files = FilesForSolutionConfigFile(["./tsconfig-src.json"], "", null);
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        string content = (string)files["/user/username/projects/myproject/src/main.ts"]!;

        // Ensure configured project is found for open file
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));

        // Ensure request can use existing snapshot
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        Snapshot requestSnapshot = session.Snapshot()!;
        Assert.Same(snapshot, requestSnapshot);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"))); // "solution config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"))); // "direct reference should be present"

        // Close the file and open one in an inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should have been released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));
    }

    // projectcollectionbuilder_test.go:62-100 — "when project found is solution referencing default project indirectly".
    [Fact]
    public void TestProjectCollectionBuilder_WhenProjectFoundIsSolutionReferencingDefaultProjectIndirectly()
    {
        var files = FilesForSolutionConfigFile(["./tsconfig-indirect1.json", "./tsconfig-indirect2.json"], "", null);
        ApplyIndirectProjectFiles(files, 1, "");
        ApplyIndirectProjectFiles(files, 2, "");
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        string content = (string)files["/user/username/projects/myproject/src/main.ts"]!;

        // Ensure configured project is found for open file
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Project? srcProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"));
        Assert.NotNull(srcProject);

        // Verify the default project is the source project
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.Same(srcProject, defaultProject);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"))); // "solution config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect1.json"))); // "direct reference should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"))); // "indirect reference should be present"

        // Close the file and open one in an inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect1.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect2.json")));
    }

    // projectcollectionbuilder_test.go:102-135 — "when project found is solution with disableReferencedProjectLoad referencing default project directly".
    [Fact]
    public void TestProjectCollectionBuilder_WhenProjectFoundIsSolutionWithDisableReferencedProjectLoadReferencingDefaultProjectDirectly()
    {
        var files = FilesForSolutionConfigFile(["./tsconfig-src.json"], "\"disableReferencedProjectLoad\": true", null);
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        string content = (string)files["/user/username/projects/myproject/src/main.ts"]!;

        // Ensure no configured project is created due to disableReferencedProjectLoad
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.Null(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));

        // Should use inferred project instead
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.NotNull(defaultProject);
        Assert.Equal(Kind.Inferred, defaultProject!.Kind);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"))); // "solution config should be present"
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"))); // "direct reference should not be present"

        // Close the file and open another one in the inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));
    }

    // projectcollectionbuilder_test.go:137-173 — "when project found is solution referencing default project indirectly through disableReferencedProjectLoad".
    [Fact]
    public void TestProjectCollectionBuilder_WhenProjectFoundIsSolutionReferencingDefaultProjectIndirectlyThroughDisableReferencedProjectLoad()
    {
        var files = FilesForSolutionConfigFile(["./tsconfig-indirect1.json"], "", null);
        ApplyIndirectProjectFiles(files, 1, "\"disableReferencedProjectLoad\": true");
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        string content = (string)files["/user/username/projects/myproject/src/main.ts"]!;

        // Ensure no configured project is created due to disableReferencedProjectLoad in indirect project
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.Null(snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));

        // Should use inferred project instead
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.NotNull(defaultProject);
        Assert.Equal(Kind.Inferred, defaultProject!.Kind);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"))); // "solution config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect1.json"))); // "solution direct reference should be present"
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"))); // "indirect reference should not be present"

        // Close the file and open another one in the inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect1.json")));
    }

    // projectcollectionbuilder_test.go:175-214 — "when project found is solution referencing default project indirectly through disableReferencedProjectLoad in one but without it in another".
    [Fact]
    public void TestProjectCollectionBuilder_WhenProjectFoundIsSolutionReferencingDefaultProjectIndirectlyThroughDisableReferencedProjectLoadInOneButWithoutItInAnother()
    {
        var files = FilesForSolutionConfigFile(["./tsconfig-indirect1.json", "./tsconfig-indirect2.json"], "", null);
        ApplyIndirectProjectFiles(files, 1, "\"disableReferencedProjectLoad\": true");
        ApplyIndirectProjectFiles(files, 2, "");
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        string content = (string)files["/user/username/projects/myproject/src/main.ts"]!;

        // Ensure configured project is found through the indirect project without disableReferencedProjectLoad
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Project? srcProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"));
        Assert.NotNull(srcProject);

        // Verify the default project is the source project (found through indirect2, not indirect1)
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.Same(srcProject, defaultProject);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"))); // "solution config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect1.json"))); // "direct reference 1 should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect2.json"))); // "direct reference 2 should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"))); // "indirect reference should be present"

        // Close the file and open another one in the inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect1.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-indirect2.json")));
    }

    // projectcollectionbuilder_test.go:216-256 — "when project found is project with own files referencing the file from referenced project".
    [Fact]
    public void TestProjectCollectionBuilder_WhenProjectFoundIsProjectWithOwnFilesReferencingTheFileFromReferencedProject()
    {
        var files = FilesForSolutionConfigFile(["./tsconfig-src.json"], "", ["\"./own/main.ts\""]);
        files["/user/username/projects/myproject/own/main.ts"] = @"
			import { foo } from '../src/main';
			foo;
			export function bar() {}
		";
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        string content = (string)files["/user/username/projects/myproject/src/main.ts"]!;

        // Ensure configured project is found for open file - should load both projects
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);
        Project? srcProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"));
        Assert.NotNull(srcProject);
        Project? ancestorProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"));
        Assert.NotNull(ancestorProject);

        // Verify the default project is the source project
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.Same(srcProject, defaultProject);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json"))); // "solution config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json"))); // "direct reference should be present"

        // Close the file and open another one in the inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/user/username/projects/myproject/tsconfig-src.json")));
    }

    // projectcollectionbuilder_test.go:258-336 — "when file is not part of first config tree found, looks into ancestor folder and its references to find default project".
    [Fact]
    public void TestProjectCollectionBuilder_WhenFileIsNotPartOfFirstConfigTreeFoundLooksIntoAncestorFolderAndItsReferencesToFindDefaultProject()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/src/projects/project/app/Component-demos.ts"] = @"
                import * as helpers from 'demos/helpers';
                export const demo = () => {
                    helpers;
                }
            ",
            ["/home/src/projects/project/app/Component.ts"] = "export const Component = () => {}",
            ["/home/src/projects/project/app/tsconfig.json"] = @"{
				""compilerOptions"": {
					""composite"": true,
					""outDir"": ""../app-dist/"",
				},
				""include"": [""**/*""],
				""exclude"": [""**/*-demos.*""],
			}",
            ["/home/src/projects/project/demos/helpers.ts"] = "export const foo = 1;",
            ["/home/src/projects/project/demos/tsconfig.json"] = @"{
				""compilerOptions"": {
					""composite"": true,
					""rootDir"": ""../"",
					""outDir"": ""../demos-dist/"",
					""paths"": {
						""demos/*"": [""./*""],
					},
				},
				""include"": [
					""**/*"",
					""../app/**/*-demos.*"",
				],
			}",
            ["/home/src/projects/project/tsconfig.json"] = @"{
				""compilerOptions"": {
					""outDir"": ""./dist/"",
				},
				""references"": [
					{ ""path"": ""./demos/tsconfig.json"" },
					{ ""path"": ""./app/tsconfig.json"" },
				],
				""files"": []
			}",
        };
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///home/src/projects/project/app/Component-demos.ts");
        string content = (string)files["/home/src/projects/project/app/Component-demos.ts"]!;

        // Ensure configured project is found for open file
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);
        Project? demoProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/src/projects/project/demos/tsconfig.json"));
        Assert.NotNull(demoProject);
        Project? solutionProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.json"));
        Assert.NotNull(solutionProject);

        // Verify the default project is the demos project (not the app project that excludes demos files)
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.Same(demoProject, defaultProject);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/app/tsconfig.json"))); // "app config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/demos/tsconfig.json"))); // "demos config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.json"))); // "solution config should be present"

        // Close the file and open another one in the inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/app/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/demos/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.json")));
    }

    // projectcollectionbuilder_test.go:338-394 — "when dts file is next to ts file and included as root in referenced project".
    [Fact]
    public void TestProjectCollectionBuilder_WhenDtsFileIsNextToTsFileAndIncludedAsRootInReferencedProject()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/src/projects/project/src/index.d.ts"] = @"
                 declare global {
                    interface Window {
                        electron: ElectronAPI
                        api: unknown
                    }
                }
            ",
            ["/home/src/projects/project/src/index.ts"] = "const api = {}",
            ["/home/src/projects/project/tsconfig.json"] = @"{
				""include"": [
					""src/*.d.ts"",
				],
				""references"": [{ ""path"": ""./tsconfig.node.json"" }],
			}",
            ["/home/src/projects/project/tsconfig.node.json"] = @"{
				""include"": [""src/**/*""],
                ""compilerOptions"": {
                    ""composite"": true,
                },
			}",
        };
        (Session session, _) = SessionUtils.Setup(files);
        var uri = new DocumentUri("file:///home/src/projects/project/src/index.d.ts");
        string content = (string)files["/home/src/projects/project/src/index.d.ts"]!;

        // Ensure configured projects are found for open file
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);
        Project? rootProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.json"));
        Assert.NotNull(rootProject);

        // Verify the default project is inferred
        Project? defaultProject = snapshot.GetDefaultProject(uri);
        Assert.NotNull(defaultProject);
        Assert.Equal(Kind.Inferred, defaultProject!.Kind);

        // Searched configs should be present while file is open
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.json"))); // "root config should be present"
        Assert.NotNull(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.node.json"))); // "node config should be present"

        // Close the file and open another one in the inferred project
        session.DidCloseFile(RequestContext.Background, uri);
        var dummyUri = new DocumentUri("file:///user/username/workspaces/dummy/dummy.ts");
        session.DidOpenFile(RequestContext.Background, dummyUri, 1, "const x = 1;", LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Config files should be released
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.json")));
        Assert.Null(snapshot.ConfigFileRegistry.GetConfig(new global::Ts.TsPath.Path("/home/src/projects/project/tsconfig.node.json")));
    }

    // projectcollectionbuilder_test.go:396-426 — "#1630".
    [Fact]
    public void TestProjectCollectionBuilder_Issue1630()
    {
        var files = new Dictionary<string, object?>
        {
            ["/project/lib/tsconfig.json"] = @"{
				""files"": [""a.ts""]
			}",
            ["/project/lib/a.ts"] = "export const a = 1;",
            ["/project/lib/b.ts"] = "export const b = 1;",
            ["/project/tsconfig.json"] = @"{
				""files"": [],
				""references"": [{ ""path"": ""./lib"" }],
				""compilerOptions"": {
					""disableReferencedProjectLoad"": true
				}
			}",
            ["/project/index.ts"] = "",
        };

        (Session session, _) = SessionUtils.Setup(files);

        // opening b.ts puts /project/lib/tsconfig.json in the config file registry and creates the project,
        // but the project is ultimately not a match
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/lib/b.ts"), 1, (string)files["/project/lib/b.ts"]!, LanguageKind.TypeScript.Value!);
        // opening an unrelated file triggers cleanup of /project/lib/tsconfig.json since no open file is part of that project,
        // but will keep the config file in the registry since lib/b.ts is still open
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        // Opening index.ts searches /project/tsconfig.json and then checks /project/lib/tsconfig.json without opening it.
        // No early return on config file existence means we try to find an already open project, which returns nil,
        // triggering a crash.
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/index.ts"), 1, (string)files["/project/index.ts"]!, LanguageKind.TypeScript.Value!);
    }

    // projectcollectionbuilder_test.go:428-455 — "inferred project root files are in stable order".
    [Fact]
    public void TestProjectCollectionBuilder_InferredProjectRootFilesAreInStableOrder()
    {
        var files = new Dictionary<string, object?>
        {
            ["/project/a.ts"] = "export const a = 1;",
            ["/project/b.ts"] = "export const b = 1;",
            ["/project/c.ts"] = "export const c = 1;",
        };

        (Session session, _) = SessionUtils.Setup(files);

        // b, c, a
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/b.ts"), 1, (string)files["/project/b.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/c.ts"), 1, (string)files["/project/c.ts"]!, LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///project/a.ts"), 1, (string)files["/project/a.ts"]!, LanguageKind.TypeScript.Value!);

        Snapshot snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects());
        Project? inferredProject = snapshot.ProjectCollection.InferredProject();
        Assert.NotNull(inferredProject);
        // It's more bookkeeping to maintain order of opening, since any file can move into or out of
        // the inferred project due to changes in other projects. Order shouldn't matter for correctness,
        // we just want it to be consistent, in case there are observable type ordering issues.
        Assert.Equal(new[]
        {
            "/project/a.ts",
            "/project/b.ts",
            "/project/c.ts",
        }, inferredProject!.Program!.CommandLine().FileNames());
    }

    // projectcollectionbuilder_test.go:457-498 — "project lookup terminates".
    [Fact]
    public void TestProjectCollectionBuilder_ProjectLookupTerminates()
    {
        var files = new Dictionary<string, object?>
        {
            ["/tsconfig.json"] = @"{
				""files"": [],
				""references"": [
					{
						""path"": ""./packages/pkg1""
					},
					{
						""path"": ""./packages/pkg2""
					},
				]
			}",
            ["/packages/pkg1/tsconfig.json"] = @"{
				""include"": [""src/**/*.ts""],
				""compilerOptions"": {
					""composite"": true,
				},
				""references"": [
					{
						""path"": ""../pkg2""
					},
				]
			}",
            ["/packages/pkg2/tsconfig.json"] = @"{
				""include"": [""src/**/*.ts""],
				""compilerOptions"": {
					""composite"": true,
				},
				""references"": [
					{
						""path"": ""../pkg1""
					},
				]
			}",
            ["/script.ts"] = "export const a = 1;",
        };
        (Session session, _) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///script.ts"), 1, (string)files["/script.ts"]!, LanguageKind.TypeScript.Value!);
        // Test should terminate
    }

    // projectcollectionbuilder_test.go:500-542 — "file moves to inferred project after import is deleted".
    [Fact]
    public void TestProjectCollectionBuilder_FileMovesToInferredProjectAfterImportIsDeleted()
    {
        // This test verifies that when a node_modules dependency file is open and its import
        // is deleted from the project root, requesting language service for the dependency
        // correctly moves it to an inferred project.
        var files = new Dictionary<string, object?>
        {
            ["/project/tsconfig.json"] = "{\"compilerOptions\": {\"strict\": true}}",
            ["/project/index.ts"] = "import { helper } from \"./node_modules/dep/index\";",
            ["/project/node_modules/dep/index.d.ts"] = "export declare function helper(): void;",
        };
        (Session session, _) = SessionUtils.Setup(files);

        // Step 1: Open the project root file
        var rootUri = new DocumentUri("file:///project/index.ts");
        session.DidOpenFile(RequestContext.Background, rootUri, 1, (string)files["/project/index.ts"]!, LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, rootUri);
        Assert.Null(err);

        // Step 2: Open the node_modules dependency file - should be in the configured project
        var depUri = new DocumentUri("file:///project/node_modules/dep/index.d.ts");
        session.DidOpenFile(RequestContext.Background, depUri, 1, (string)files["/project/node_modules/dep/index.d.ts"]!, LanguageKind.TypeScript.Value!);

        Snapshot snapshot = session.Snapshot()!;
        Project? configuredProject = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/project/tsconfig.json"));
        Assert.NotNull(configuredProject); // "configured project should exist"
        Project? defaultProject = snapshot.GetDefaultProject(depUri);
        Assert.Same(configuredProject, defaultProject); // "dependency should be in the configured project initially"

        // Step 3: Delete the import from the root file
        session.DidChangeFile(RequestContext.Background, rootUri, 2, [
            new TextDocumentContentChangePartialOrWholeDocument
            {
                WholeDocument = new TextDocumentContentChangeWholeDocument { Text = "// import removed" },
            },
        ]);

        // Step 4: Request language service for the dependency - it should now be in an inferred project
        (global::Ts.Ls.LanguageService? ls, Exception? err2) = session.GetLanguageService(RequestContext.Background, depUri);
        Assert.Null(err2);
        Assert.NotNull(ls); // "language service should be available for dependency"

        snapshot = session.Snapshot()!;
        defaultProject = snapshot.GetDefaultProject(depUri);
        Assert.NotNull(defaultProject); // "dependency should have a default project"
        Assert.Equal(Kind.Inferred, defaultProject!.Kind); // "dependency should be in an inferred project after import is deleted"
    }

    // projectcollectionbuilder_test.go:544-599 — "should update project on package.json change".
    [Fact]
    public void TestProjectCollectionBuilder_ShouldUpdateProjectOnPackageJsonChange()
    {
        // Set up a project with package.json "imports" that affect module resolution.
        // The package.json is not a program file, but it IS an affecting location.
        // When it changes, the project should be marked dirty and the program should be rebuilt.
        var packageJsonFiles = new Dictionary<string, object?>
        {
            ["/home/projects/myproject/tsconfig.json"] = @"{
				""compilerOptions"": {
					""module"": ""nodenext"",
					""moduleResolution"": ""nodenext"",
					""noLib"": true,
					""noEmit"": true
				}
			}",
            ["/home/projects/myproject/package.json"] = @"{
				""name"": ""myproject"",
				""type"": ""module"",
				""imports"": {
					""#utils"": ""./src/utils.ts""
				}
			}",
            ["/home/projects/myproject/src/index.ts"] = "import { add } from \"#utils\";",
            ["/home/projects/myproject/src/utils.ts"] = "export function add(a: number, b: number) { return a + b; }",
        };

        (Session session, SessionUtils utils) = SessionUtils.Setup(packageJsonFiles);
        var indexUri = new DocumentUri("file:///home/projects/myproject/src/index.ts");
        session.DidOpenFile(RequestContext.Background, indexUri, 1, (string)packageJsonFiles["/home/projects/myproject/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Verify initial state: #utils resolves to utils.ts, so utils.ts is in the program
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, indexUri);
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();
        RequestContext requestCtx = SessionUtils.WithRequestID(RequestContext.Background);
        Assert.Empty(global::Ts.Core.LargeStackExecutor.Run(() => program.GetSemanticDiagnostics(requestCtx.Cancellation, null))); // "should have no diagnostics with correct package.json"

        // Now change the package.json to point #utils at a non-existent file
        utils.FS().WriteFile("/home/projects/myproject/package.json", @"{
				""name"": ""myproject"",
				""type"": ""module"",
				""imports"": {
					""#utils"": ""./src/nonexistent.ts""
				}
			}");
        session.DidChangeWatchedFiles(RequestContext.Background, [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new DocumentUri("file:///home/projects/myproject/package.json"),
                Type = FileChangeType.Changed,
            },
        ]);

        (ls, err) = session.GetLanguageService(RequestContext.Background, indexUri);
        Assert.Null(err);
        global::Ts.Compiler.Program updatedProgram = ls!.GetProgram();
        Assert.Single(global::Ts.Core.LargeStackExecutor.Run(() => updatedProgram.GetSemanticDiagnostics(requestCtx.Cancellation, null))); // "should have diagnostics after package.json change"
    }

    // projectcollectionbuilder_test.go:602-636 — filesForSolutionConfigFile.
    private static Dictionary<string, object?> FilesForSolutionConfigFile(string[] solutionRefs, string compilerOptions, string[]? ownFiles)
    {
        string compilerOptionsStr = "";
        if (compilerOptions != "")
        {
            compilerOptionsStr = "\"compilerOptions\": {\n\t\t\t" + compilerOptions + "\n\t\t},";
        }
        string ownFilesStr = "";
        if (ownFiles is { Length: > 0 })
        {
            ownFilesStr = string.Join(",", ownFiles);
        }
        var referencesStr = new List<string>(solutionRefs.Length);
        foreach (string reference in solutionRefs)
        {
            referencesStr.Add($"{{ \"path\": \"{reference}\" }}");
        }
        return new Dictionary<string, object?>
        {
            ["/user/username/projects/myproject/tsconfig.json"] = $@"{{
			{compilerOptionsStr}
			""files"": [{ownFilesStr}],
			""references"": [
				{string.Join(",", referencesStr)}
			]
		}}",
            ["/user/username/projects/myproject/tsconfig-src.json"] = @"{
			""compilerOptions"": {
				""composite"": true,
				""outDir"": ""./target"",
			},
			""include"": [""./src/**/*""]
		}",
            ["/user/username/projects/myproject/src/main.ts"] = @"
			import { foo } from './helpers/functions';
			export { foo };",
            ["/user/username/projects/myproject/src/helpers/functions.ts"] = "export const foo = 1;",
        };
    }

    // projectcollectionbuilder_test.go:638-640 — applyIndirectProjectFiles.
    private static void ApplyIndirectProjectFiles(Dictionary<string, object?> files, int projectIndex, string compilerOptions)
    {
        foreach ((string key, object? value) in FilesForIndirectProject(projectIndex, compilerOptions))
        {
            files[key] = value;
        }
    }

    // projectcollectionbuilder_test.go:642-662 — filesForIndirectProject.
    private static Dictionary<string, object?> FilesForIndirectProject(int projectIndex, string compilerOptions)
    {
        return new Dictionary<string, object?>
        {
            [$"/user/username/projects/myproject/tsconfig-indirect{projectIndex}.json"] = $@"{{
			""compilerOptions"": {{
				""composite"": true,
				""outDir"": ""./target/"",
				{compilerOptions}
			}},
			""files"": [
				""./indirect{projectIndex}/main.ts""
			],
			""references"": [
				{{
				""path"": ""./tsconfig-src.json""
				}}
			]
		}}",
            [$"/user/username/projects/myproject/indirect{projectIndex}/main.ts"] = "export const indirect = 1;",
        };
    }
}
