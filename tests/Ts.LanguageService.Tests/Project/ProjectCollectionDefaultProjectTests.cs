// Ported from tsgo internal/project/projectcollectiondefaultproject_test.go (whole file).
//
// PORT-NOTEs:
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background.
//  * Go's `files[string(uri)[7:]].(string)` (strip the "file:///" prefix) →
//    `files[uri.Value[7..]]`.

using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using LanguageKind = global::Ts.LsProto.LanguageKind;

namespace Ts.Project.Tests;

public sealed class ProjectCollectionDefaultProjectTests
{
    // projectcollectiondefaultproject_test.go:16-127 — TestProjectCollectionDefaultProject.
    [Fact]
    public void TestProjectCollectionDefaultProject()
    {
        // Project 1 references project 2, which does not have open files.
        // File project1/dist/index.d.ts does not belong to any tsconfig.json, but is included in programs for
        // projects 3 and 4 via project 1's output.
        // When looking for a default project for project1/dist/index.d.ts,
        // we should not try to unconditionally access project 2,
        // which isn't loaded because of `disableReferencedProjectLoad`.
        var files = new Dictionary<string, object?>
        {
            ["/project1/tsconfig.json"] = @"{
				""extends"": ""../tsconfig.json"",
				""files"": [],
				""include"": [""src/**/*""],
				""references"": [
					{
						""path"": ""../project2""
					}
				],
				""compilerOptions"": {
					""composite"": true,
					""outDir"": ""./dist"",
					""rootDir"": ""./src"",
				}
			}",
            ["/project1/src/index.ts"] = @"export const foo = 42;
		export type Bar = { a: string };",
            ["/project1/dist/index.d.ts"] = @"export declare const foo = 42;
			export type Bar = {
				a: string;
			};",
            ["/project2/tsconfig.json"] = @"{
				""extends"": ""../tsconfig.json"",
				""files"": [],
				""include"": [""src/**/*""],
				""compilerOptions"": {
					""composite"": true,
					""outDir"": ""./dist"",
					""rootDir"": ""./src""
				}
			}",
            ["/project3/tsconfig.json"] = @"{
				""extends"": ""../tsconfig.json"",
				""files"": [],
				""include"": [""src/**/*""],
				""references"": [
					{
						""path"": ""../project1""
					}
				],
				""compilerOptions"": {
					""composite"": true,
					""outDir"": ""./dist"",
					""rootDir"": ""./src"",
				}
			}",
            ["/project3/src/index.ts"] = @"import { Bar } from ""../../project1/dist/index.js"";
			declare const b: Bar;
			const x: string = b.a;",
            ["/project4/tsconfig.json"] = @"{
				""extends"": ""../tsconfig.json"",
				""files"": [],
				""include"": [""src/**/*""],
				""references"": [
					{
						""path"": ""../project1""
					}
				],
				""compilerOptions"": {
					""composite"": true,
					""outDir"": ""./dist"",
					""rootDir"": ""./src"",
				}
			}",
            ["/project4/src/index.ts"] = @"import { Bar } from ""../../project1/dist/index.js"";
declare const b: Bar;
const x: string = b.a;",
            ["/tsconfig.json"] = @"{
				""compilerOptions"": {
					""disableReferencedProjectLoad"": true,
					""disableSolutionSearching"": true,
					""disableSourceOfProjectReferenceRedirect"": true
				},
				""files"": [],
				""references"": [
					{
						""path"": ""./project1""
					},
					{
						""path"": ""./project2""
					},
					{
						""path"": ""./project3""
					},
					{
						""path"": ""./project4""
					}
				]
			}",
        };
        DocumentUri[] uris =
        [
            "file:///project1/dist/index.d.ts",
            "file:///project1/src/index.ts",
            "file:///project3/src/index.ts",
            "file:///project4/src/index.ts",
        ];
        (global::Ts.Project.Session session, _) = SessionUtils.Setup(files);
        // Should not crash.
        foreach (var uri in uris)
        {
            string content = (string)files[uri.Value[7..]]!;
            session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.TypeScript.Value!);
        }
    }
}
