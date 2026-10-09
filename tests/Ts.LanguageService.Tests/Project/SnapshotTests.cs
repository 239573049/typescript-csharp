// Ported from tsgo internal/project/snapshot_test.go (whole file).
//
// PORT-NOTES:
//  - Go's BenchmarkSnapshotCloneRefCost (snapshot_test.go:215-298) is a testing.B
//    benchmark, not a test; it also pokes the session-private pendingFileChanges
//    slice directly. Not ported — no xUnit equivalent.
//  - t.Cleanup(session.Close) → try/finally in the subtest that registers it.
//  - Multi-line Go backtick JSON strings keep their exact content via raw string
//    literals (whitespace inside the JSON is insignificant to the parser).

using System;
using System.Collections.Generic;

using Ts.LsProto;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;
using RequestContext = Ts.Core.RequestContext;
using Path = Ts.TsPath.Path;
using Range = Ts.LsProto.Range;

namespace Ts.Project.Tests;

public sealed class SnapshotTests
{
    // snapshot_test.go:21-36 — setup.
    private static Session Setup(Dictionary<string, string> files)
    {
        IFileSystem fs = Ts.Bundled.Bundled.WrapFS(VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/));
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
        });
    }

    // snapshot_test.go:38-71 — "compilerHost gets frozen with snapshot's FS only once".
    [Fact]
    public void Snapshot_CompilerHostGetsFrozenWithSnapshotFsOnlyOnce()
    {
        var files = new Dictionary<string, string>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = "{}",
            ["/home/projects/TS/p1/index.ts"] = "console.log('Hello, world!');",
        };
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p1/index.ts"), 1, files["/home/projects/TS/p1/index.ts"], LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        Snapshot snapshotBefore = session.Snapshot()!;

        session.DidChangeFile(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p1/index.ts"), 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromPartial(new TextDocumentContentChangePartial
            {
                Text = "\n",
                Range = new Range
                {
                    Start = new Position { Line = 0, Character = 24 },
                    End = new Position { Line = 0, Character = 24 },
                },
            }),
        ]);
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p1/index.ts"));
        Assert.Null(err);
        Snapshot snapshotAfter = session.Snapshot()!;

        // Configured project was updated by a clone
        Assert.Equal(ProgramUpdateKind.Cloned, snapshotAfter.ProjectCollection.ConfiguredProject(new Path("/home/projects/ts/p1/tsconfig.json"))!.ProgramUpdateKind);
        // Inferred project wasn't updated last snapshot change, so its program update kind is still NewFiles
        Assert.Same(snapshotBefore.ProjectCollection.InferredProject(), snapshotAfter.ProjectCollection.InferredProject());
        Assert.Equal(ProgramUpdateKind.NewFiles, snapshotAfter.ProjectCollection.InferredProject()!.ProgramUpdateKind);
        // host for inferred project should not change
        Assert.Same(snapshotAfter.ProjectCollection.InferredProject()!.host!.sourceFS.source, snapshotBefore.fs);
    }

    // snapshot_test.go:73-101 — "cached disk files are cleaned up".
    [Fact]
    public void Snapshot_CachedDiskFilesAreCleanedUp()
    {
        var files = new Dictionary<string, string>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = "{}",
            ["/home/projects/TS/p1/index.ts"] = "import { a } from './a'; console.log(a);",
            ["/home/projects/TS/p1/a.ts"] = "export const a = 1;",
            ["/home/projects/TS/p2/tsconfig.json"] = "{}",
            ["/home/projects/TS/p2/index.ts"] = "import { b } from './b'; console.log(b);",
            ["/home/projects/TS/p2/b.ts"] = "export const b = 2;",
        };
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p1/index.ts"), 1, files["/home/projects/TS/p1/index.ts"], LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p2/index.ts"), 1, files["/home/projects/TS/p2/index.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshotBefore = session.Snapshot()!;

        // a.ts and b.ts are cached
        Assert.True(snapshotBefore.fs.DiskFiles!.ContainsKey(new Path("/home/projects/ts/p1/a.ts")), "a.ts should be cached");
        Assert.True(snapshotBefore.fs.DiskFiles!.ContainsKey(new Path("/home/projects/ts/p2/b.ts")), "b.ts should be cached");

        // Close p1's only open file
        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p1/index.ts"));
        // Next open file is unrelated to p1, triggers p1 closing and file cache cleanup
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        Snapshot snapshotAfter = session.Snapshot()!;

        // a.ts is cleaned up, b.ts is still cached
        Assert.False(snapshotAfter.fs.DiskFiles!.ContainsKey(new Path("/home/projects/ts/p1/a.ts")), "a.ts should be cleaned up");
        Assert.True(snapshotAfter.fs.DiskFiles!.ContainsKey(new Path("/home/projects/ts/p2/b.ts")), "b.ts should still be cached");
    }

    // snapshot_test.go:103-119 — "GetFile returns nil for non-existent files".
    [Fact]
    public void Snapshot_GetFileReturnsNilForNonExistentFiles()
    {
        var files = new Dictionary<string, string>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = "{}",
            ["/home/projects/TS/p1/index.ts"] = "console.log('Hello, world!');",
        };
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///home/projects/TS/p1/index.ts"), 1, files["/home/projects/TS/p1/index.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;

        FileHandle? handle = snapshot.GetFile("/home/projects/TS/p1/nonexistent.ts");
        Assert.Null(handle);

        // Test that ReadFile returns false for non-existent file
        (_, bool ok) = snapshot.ReadFile("/home/projects/TS/p1/nonexistent.ts");
        Assert.False(ok, "ReadFile should return false for non-existent file");
    }

    // snapshot_test.go:121-173 — "program change loads node_modules dependency and
    // auto-imports includes it".
    [Fact]
    public void Snapshot_ProgramChangeLoadsNodeModulesDependencyAndAutoImportsIncludesIt()
    {
        var files = new Dictionary<string, string>
        {
            ["/home/projects/otherproject/tsconfig.json"] = """
                {
                    "compilerOptions": {
                        "module": "commonjs"
                    }
                }
                """,
            ["/home/projects/otherproject/index.ts"] = "",
            ["/home/projects/node_modules/foo/package.json"] = """
                {
                    "types": "index.d.ts",
                    "typesVersions": {
                        "*": {
                            "bar/*": ["dist/*"],
                            "exact-match": ["dist/index.d.ts"],
                            "foo/*": ["dist/*"],
                            "*": ["dist/*"]
                        }
                    }
                }
                """,
            ["/home/projects/node_modules/foo/nope.d.ts"] = "export const nope = 0;",
            ["/home/projects/node_modules/foo/dist/index.d.ts"] = "export const index = 0;",
            ["/home/projects/node_modules/foo/dist/blah.d.ts"] = "export const blah = 0;",
            ["/home/projects/node_modules/foo/dist/foo/onlyInFooFolder.d.ts"] = "export const foo = 0;",
            ["/home/projects/node_modules/foo/dist/subfolder/one.d.ts"] = "export const one = 0;",
        };
        Session session = Setup(files);
        try
        {
            RequestContext ctx = RequestContext.Background;
            var otherIndexURI = new DocumentUri("file:///home/projects/otherproject/index.ts");

            // Open the file
            session.DidOpenFile(ctx, otherIndexURI, 1, files["/home/projects/otherproject/index.ts"], LanguageKind.TypeScript.Value!);

            // Insert import statement:
            // This will trigger both a program rebuild which will include the node_modules files,
            // and an auto-import collection which should find the exports from those files.
            session.DidChangeFile(ctx, otherIndexURI, 2,
            [
                TextDocumentContentChangePartialOrWholeDocument.FromPartial(new TextDocumentContentChangePartial
                {
                    Text = "import {} from \"foo/foo/subfolder/one\";",
                    Range = new Range
                    {
                        Start = new Position { Line = 0, Character = 0 },
                        End = new Position { Line = 0, Character = 0 },
                    },
                }),
            ]);

            // Now trigger snapshot clone with both program update and auto-imports registry building.
            (_, Exception? err) = session.GetCurrentLanguageServiceWithAutoImports(ctx, otherIndexURI);
            Assert.Null(err);
        }
        finally
        {
            // Go: t.Cleanup(session.Close).
            session.Close();
        }
    }

    // snapshot_test.go:175-212 — "fallback rebuild with recomputed parse options is safe
    // for later clone".
    [Fact]
    public void Snapshot_FallbackRebuildWithRecomputedParseOptionsIsSafeForLaterClone()
    {
        var testFiles = new Dictionary<string, string>
        {
            ["/project/node_modules/pkg/index.ts"] = "export const pkg = 0;",
            ["/project/src/other.ts"] = "export const other = 1;",
        };
        Session session = Setup(testFiles);
        var pkgURI = new DocumentUri("file:///project/node_modules/pkg/index.ts");
        var otherURI = new DocumentUri("file:///project/src/other.ts");

        session.DidOpenFile(RequestContext.Background, pkgURI, 1, testFiles["/project/node_modules/pkg/index.ts"], LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, otherURI, 1, testFiles["/project/src/other.ts"], LanguageKind.TypeScript.Value!);
        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, pkgURI);
        Assert.Null(err);

        session.fs.fs.WriteFile("/project/node_modules/pkg/package.json", "{ \"type\": \"module\" }");
        session.DidChangeFile(RequestContext.Background, pkgURI, 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new TextDocumentContentChangeWholeDocument
            {
                Text = "import \"./missing\"; export const pkg = 1;",
            }),
        ]);
        (_, err) = session.GetLanguageService(RequestContext.Background, pkgURI);
        Assert.Null(err);

        session.DidChangeFile(RequestContext.Background, otherURI, 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new TextDocumentContentChangeWholeDocument
            {
                Text = "export const other = 2;",
            }),
        ]);
        (_, err) = session.GetLanguageService(RequestContext.Background, otherURI);
        Assert.Null(err);
    }
}
