// Ported from tsgo internal/project/configfilechanges_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one xUnit class; t.Run subtests → [Fact]s named
//    Parent_Subtest.
//  * gotest.tools assert.Equal(t, actual, expected) → Assert.Equal(expected, actual)
//    (argument order swapped).
//  * Go `err := utils.FS().WriteFile/Remove(...); assert.NilError(t, err)` → direct
//    calls: the C# IFileSystem methods throw on failure (05 §4), so a failed operation
//    fails the test exactly where Go's err assertion would.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background.

using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using FileChangeType = global::Ts.LsProto.FileChangeType;
using LanguageKind = global::Ts.LsProto.LanguageKind;

namespace Ts.Project.Tests;

public sealed class ConfigFileChangesTests
{
    // configfilechanges_test.go:21-30 — files.
    private static Dictionary<string, object?> Files()
    {
        return new Dictionary<string, object?>
        {
            ["/tsconfig.more-base.json"] = @"{}",
            ["/tsconfig.base.json"] = @"{""extends"": ""../tsconfig.more-base.json"", ""compilerOptions"": {""strict"": true}}",
            ["/src/tsconfig.json"] = @"{""extends"": ""../tsconfig.base.json"", ""compilerOptions"": {""target"": ""es6""}, ""references"": [{""path"": ""../utils""}]}",
            ["/src/index.ts"] = @"console.log(""Hello, world!"");",
            ["/src/subfolder/foo.ts"] = @"export const foo = ""bar"";",

            ["/utils/tsconfig.json"] = @"{""compilerOptions"": {""composite"": true}}",
            ["/utils/index.ts"] = @"console.log(""Hello, test!"");",
        };
    }

    // configfilechanges_test.go:32-49 — "should update program options on config file change".
    [Fact]
    public void TestConfigFileChanges_ShouldUpdateProgramOptionsOnConfigFileChange()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().WriteFile("/src/tsconfig.json", @"{""extends"": ""../tsconfig.base.json"", ""compilerOptions"": {""target"": ""esnext""}, ""references"": [{""path"": ""../utils""}]}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///src/tsconfig.json"),
                Type = FileChangeType.Changed,
            },
        ]);

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/index.ts");
        Assert.Null(err);
        Assert.Equal(ScriptTarget.ESNext, ls!.GetProgram().Options().Target);
    }

    // configfilechanges_test.go:51-68 — "should update project on extended config file change".
    [Fact]
    public void TestConfigFileChanges_ShouldUpdateProjectOnExtendedConfigFileChange()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().WriteFile("/tsconfig.base.json", @"{""compilerOptions"": {""strict"": false}}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///tsconfig.base.json"),
                Type = FileChangeType.Changed,
            },
        ]);

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/index.ts");
        Assert.Null(err);
        Assert.Equal(Tristate.False, ls!.GetProgram().Options().Strict);
    }

    // configfilechanges_test.go:70-87 — "should update project on doubly extended config
    // file change".
    [Fact]
    public void TestConfigFileChanges_ShouldUpdateProjectOnDoublyExtendedConfigFileChange()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().WriteFile("/tsconfig.more-base.json", @"{""compilerOptions"": {""verbatimModuleSyntax"": true}}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///tsconfig.more-base.json"),
                Type = FileChangeType.Changed,
            },
        ]);

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/index.ts");
        Assert.Null(err);
        Assert.Equal(Tristate.True, ls!.GetProgram().Options().VerbatimModuleSyntax);
    }

    // configfilechanges_test.go:89-108 — "should update project on referenced config
    // file change".
    [Fact]
    public void TestConfigFileChanges_ShouldUpdateProjectOnReferencedConfigFileChange()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        var snapshotBefore = session.Snapshot()!;

        utils.FS().WriteFile("/utils/tsconfig.json", @"{""compilerOptions"": {""composite"": true, ""target"": ""esnext""}}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///utils/tsconfig.json"),
                Type = FileChangeType.Changed,
            },
        ]);

        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/index.ts");
        Assert.Null(err);
        var snapshotAfter = session.Snapshot()!;
        // Go message: "Snapshot should be updated after config file change"
        Assert.True(!ReferenceEquals(snapshotAfter, snapshotBefore));
    }

    // configfilechanges_test.go:110-129 — "should close project on config file deletion".
    [Fact]
    public void TestConfigFileChanges_ShouldCloseProjectOnConfigFileDeletion()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().Remove("/src/tsconfig.json");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///src/tsconfig.json"),
                Type = FileChangeType.Deleted,
            },
        ]);

        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/index.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        Assert.True(snapshot.ProjectCollection.Projects().Count == 1);
        Assert.True(snapshot.ProjectCollection.InferredProject() != null);
    }

    // configfilechanges_test.go:131-169 — "config file creation then deletion".
    [Fact]
    public void TestConfigFileChanges_ConfigFileCreationThenDeletion()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);
        session.DidOpenFile(RequestContext.Background, "file:///src/subfolder/foo.ts", 1, (string)files["/src/subfolder/foo.ts"]!, LanguageKind.TypeScript.Value!);

        utils.FS().WriteFile("/src/subfolder/tsconfig.json", @"{}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///src/subfolder/tsconfig.json"),
                Type = FileChangeType.Created,
            },
        ]);

        (_, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/subfolder/foo.ts");
        Assert.Null(err);
        var snapshot = session.Snapshot()!;
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count);
        Assert.Equal("/src/subfolder/tsconfig.json", snapshot.GetDefaultProject("file:///src/subfolder/foo.ts")!.Name());

        utils.FS().Remove("/src/subfolder/tsconfig.json");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///src/subfolder/tsconfig.json"),
                Type = FileChangeType.Deleted,
            },
        ]);

        (_, err) = session.GetLanguageService(RequestContext.Background, "file:///src/subfolder/foo.ts");
        Assert.Null(err);
        snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.json", snapshot.GetDefaultProject("file:///src/subfolder/foo.ts")!.Name());
        Assert.Equal(2, snapshot.ProjectCollection.Projects().Count); // Old project will be cleaned up on next file open

        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        snapshot = session.Snapshot()!;
        Assert.Single(snapshot.ProjectCollection.Projects()); // Go: len(...) == 1
    }

    // configfilechanges_test.go:171-199 — "should update project when missing extended
    // config is created".
    [Fact]
    public void TestConfigFileChanges_ShouldUpdateProjectWhenMissingExtendedConfigIsCreated()
    {
        // Start with a project whose tsconfig extends a base config that doesn't exist yet
        var missingBaseFiles = new Dictionary<string, object?>();
        foreach ((string k, object? v) in Files())
        {
            if (k == "/tsconfig.base.json")
            {
                continue;
            }
            missingBaseFiles[k] = v;
        }

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(missingBaseFiles);
        session.DidOpenFile(RequestContext.Background, "file:///src/index.ts", 1, (string)missingBaseFiles["/src/index.ts"]!, LanguageKind.TypeScript.Value!);

        // Create the previously-missing base config file that is extended by /src/tsconfig.json
        utils.FS().WriteFile("/tsconfig.base.json", @"{""compilerOptions"": {""strict"": true}}");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new global::Ts.LsProto.FileEvent
            {
                Uri = new global::Ts.LsProto.DocumentUri("file:///tsconfig.base.json"),
                Type = FileChangeType.Created,
            },
        ]);

        // Accessing the language service should trigger project update
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///src/index.ts");
        Assert.Null(err);
        Assert.Equal(Tristate.True, ls!.GetProgram().Options().Strict);
    }
}
