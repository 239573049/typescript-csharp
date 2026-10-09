// Ported from tsgo internal/project/refcountcache_test.go (whole file).
//
// PORT-NOTES:
//  - Go holds *refCountCacheEntry pointers across map deletions (mainEntry.refCount == 0
//    after Deref+Delete); the C# Ts.Collections.SyncMap TryLoad hands out the same live
//    RefCountCacheEntry reference, so holding the out value reproduces the semantics.
//  - Go's key.FileName (promoted from the embedded ParseOptions) maps to
//    key.SourceFileParseOptions.FileName.
//  - Go's nested subtest tree is flattened into ParseCache_* / ExtendedConfigCache_* facts.

using System;
using System.Collections.Generic;

using Ts.LsProto;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;
using Path = Ts.TsPath.Path;
using Range = Ts.LsProto.Range;
using RequestContext = Ts.Core.RequestContext;
using SourceFile = Ts.Ast.SourceFile;

namespace Ts.Project.Tests;

public sealed class RefCountCacheTests
{
    // refcountcache_test.go:23-38 — setup.
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

    // refcountcache_test.go:43-46 — the shared parseCache file set.
    private static Dictionary<string, string> ParseCacheFiles() => new()
    {
        ["/user/username/projects/myproject/src/main.ts"] = "const x = 1;",
        ["/user/username/projects/myproject/src/utils.ts"] = "export function util() {}",
    };

    // refcountcache_test.go:48-87 — "reuse unchanged file".
    [Fact]
    public void ParseCache_ReuseUnchangedFile()
    {
        Dictionary<string, string> files = ParseCacheFiles();
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"), 1, files["/user/username/projects/myproject/src/main.ts"], LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/utils.ts"), 1, files["/user/username/projects/myproject/src/utils.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Ts.Compiler.Program program = snapshot.ProjectCollection.InferredProject()!.GetProgram()!;
        SourceFile main = program.GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        SourceFile utils = program.GetSourceFile("/user/username/projects/myproject/src/utils.ts")!;
        ParseCacheKey mainKey = ParseCache.NewParseCacheKey(main.ParseOptions(), main.Hash, main.ScriptKind);
        ParseCacheKey utilsKey = ParseCache.NewParseCacheKey(utils.ParseOptions(), utils.Hash, utils.ScriptKind);
        session.parseCache.entries.TryLoad(mainKey, out RefCountCacheEntry<SourceFile>? mainEntry);
        session.parseCache.entries.TryLoad(utilsKey, out RefCountCacheEntry<SourceFile>? utilsEntry);
        Assert.NotNull(mainEntry);
        Assert.NotNull(utilsEntry);
        Assert.Equal(1, mainEntry!.refCount);
        Assert.Equal(1, utilsEntry!.refCount);

        session.DidChangeFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"), 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromPartial(new TextDocumentContentChangePartial
            {
                Range = new Range
                {
                    Start = new Position { Line = 0, Character = 0 },
                    End = new Position { Line = 0, Character = 12 },
                },
                Text = "const x = 2;",
            }),
        ]);
        (Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"));
        Assert.Null(err);
        session.WaitForBackgroundTasks();
        SourceFile newMain = ls!.GetProgram().GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        ParseCacheKey newMainKey = ParseCache.NewParseCacheKey(newMain.ParseOptions(), newMain.Hash, newMain.ScriptKind);
        session.parseCache.entries.TryLoad(newMainKey, out RefCountCacheEntry<SourceFile>? newMainEntry);
        Assert.NotNull(newMainEntry);
        Assert.NotSame(main, newMain);
        Assert.NotSame(newMainEntry!, mainEntry!);
        Assert.Same(utils, ls.GetProgram().GetSourceFile("/user/username/projects/myproject/src/utils.ts"));
        // Old snapshot is deref'd immediately when replaced by UpdateSnapshot,
        // so old mainEntry is already disposed and utils refCount is already 1.
        Assert.Equal(0, mainEntry!.refCount);
        Assert.Equal(1, newMainEntry!.refCount);
        Assert.Equal(1, utilsEntry!.refCount);
    }

    // refcountcache_test.go:89-112 — "release file on close".
    [Fact]
    public void ParseCache_ReleaseFileOnClose()
    {
        Dictionary<string, string> files = ParseCacheFiles();
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"), 1, files["/user/username/projects/myproject/src/main.ts"], LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/utils.ts"), 1, files["/user/username/projects/myproject/src/utils.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Ts.Compiler.Program program = snapshot.ProjectCollection.InferredProject()!.GetProgram()!;
        SourceFile main = program.GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        SourceFile utils = program.GetSourceFile("/user/username/projects/myproject/src/utils.ts")!;
        ParseCacheKey mainKey = ParseCache.NewParseCacheKey(main.ParseOptions(), main.Hash, main.ScriptKind);
        ParseCacheKey utilsKey = ParseCache.NewParseCacheKey(utils.ParseOptions(), utils.Hash, utils.ScriptKind);
        session.parseCache.entries.TryLoad(mainKey, out RefCountCacheEntry<SourceFile>? mainEntry);
        session.parseCache.entries.TryLoad(utilsKey, out RefCountCacheEntry<SourceFile>? utilsEntry);
        Assert.NotNull(mainEntry);
        Assert.NotNull(utilsEntry);
        Assert.Equal(1, mainEntry!.refCount);
        Assert.Equal(1, utilsEntry!.refCount);

        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"));
        (Ts.Ls.LanguageService? _, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/utils.ts"));
        Assert.Null(err);
        session.WaitForBackgroundTasks();
        Assert.Equal(1, utilsEntry!.refCount);
        Assert.Equal(0, mainEntry!.refCount);
        bool ok = session.parseCache.entries.TryLoad(mainKey, out _);
        Assert.False(ok);
    }

    // refcountcache_test.go:114-170 — "unchanged program does not over-ref".
    [Fact]
    public void ParseCache_UnchangedProgramDoesNotOverRef()
    {
        // When a program is reused across snapshots without changes, we should
        // not accumulate extra refs. The ref count should stay at 1 per source file
        // until the program is finally disposed.
        Dictionary<string, string> files = ParseCacheFiles();
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"), 1, files["/user/username/projects/myproject/src/main.ts"], LanguageKind.TypeScript.Value!);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/utils.ts"), 1, files["/user/username/projects/myproject/src/utils.ts"], LanguageKind.TypeScript.Value!);

        // Get first snapshot and capture the program/entries
        Snapshot snapshot1 = session.Snapshot()!;
        Ts.Compiler.Program program1 = snapshot1.ProjectCollection.InferredProject()!.GetProgram()!;
        SourceFile main = program1.GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        ParseCacheKey mainKey = ParseCache.NewParseCacheKey(main.ParseOptions(), main.Hash, main.ScriptKind);
        session.parseCache.entries.TryLoad(mainKey, out RefCountCacheEntry<SourceFile>? mainEntry);
        Assert.NotNull(mainEntry);
        Assert.Equal(1, mainEntry!.refCount);

        // Change utils.ts to trigger a new snapshot, but main.ts stays the same
        // so main's source file should be reused.
        session.DidChangeFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/utils.ts"), 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromPartial(new TextDocumentContentChangePartial
            {
                Range = new Range
                {
                    Start = new Position { Line = 0, Character = 0 },
                    End = new Position { Line = 0, Character = 25 },
                },
                Text = "export function util2() {}",
            }),
        ]);

        // Get second snapshot - main.ts should be reused (program is new but shares source files)
        (Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"));
        Assert.Null(err);
        session.WaitForBackgroundTasks();
        Ts.Compiler.Program program2 = ls!.GetProgram();
        SourceFile main2 = program2.GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        Assert.Same(main, main2);

        // main.ts refCount should be 1: the old snapshot was immediately deref'd
        // when replaced, so only the new snapshot holds a ref.
        session.parseCache.entries.TryLoad(mainKey, out mainEntry);
        Assert.NotNull(mainEntry);
        Assert.Equal(1, mainEntry!.refCount);

        // Close files to trigger cleanup
        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"));
        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/utils.ts"));
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();

        // Entry should now be gone (refCount 0, deleted)
        bool ok = session.parseCache.entries.TryLoad(mainKey, out RefCountCacheEntry<SourceFile>? remaining);
        // Go: if ok { t.Logf("Entry still exists with refCount=%d", ...) }; assert.Assert(t, !ok, ...).
        Assert.False(ok, $"entry should be deleted after program is disposed (still exists with refCount={remaining?.refCount})");
    }

    // refcountcache_test.go:172-214 — "fallback rebuild does not double-ref changed file".
    [Fact]
    public void ParseCache_FallbackRebuildDoesNotDoubleRefChangedFile()
    {
        var testFiles = new Dictionary<string, string>
        {
            ["/user/username/projects/myproject/src/main.ts"] = "const x = 1;",
            ["/user/username/projects/myproject/src/utils.ts"] = "export const util = 1;",
        };
        Session session = Setup(testFiles);
        var mainURI = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        session.DidOpenFile(RequestContext.Background, mainURI, 1, testFiles["/user/username/projects/myproject/src/main.ts"], LanguageKind.TypeScript.Value!);

        (_, Exception? err) = session.GetLanguageService(RequestContext.Background, mainURI);
        Assert.Null(err);

        session.DidChangeFile(RequestContext.Background, mainURI, 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new TextDocumentContentChangeWholeDocument
            {
                Text = "import { util } from \"./utils\";\nconst x = util;",
            }),
        ]);

        (Ts.Ls.LanguageService? lsAfter, Exception? errAfter) = session.GetLanguageService(RequestContext.Background, mainURI);
        Assert.Null(errAfter);
        session.WaitForBackgroundTasks();

        Project? project = session.Snapshot()!.ProjectCollection.InferredProject();
        Assert.NotNull(project);
        Assert.Equal(ProgramUpdateKind.NewFiles, project!.ProgramUpdateKind);

        SourceFile main = lsAfter!.GetProgram().GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        ParseCacheKey mainKey = ParseCache.NewParseCacheKey(main.ParseOptions(), main.Hash, main.ScriptKind);
        bool entryOk = session.parseCache.entries.TryLoad(mainKey, out RefCountCacheEntry<SourceFile>? mainEntry);
        Assert.True(entryOk);
        Assert.Equal(1, mainEntry!.refCount);

        session.DidCloseFile(RequestContext.Background, mainURI);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();

        bool ok = session.parseCache.entries.TryLoad(mainKey, out _);
        Assert.False(ok);
    }

    // refcountcache_test.go:216-254 — "case-only duplicate loads are released on dispose".
    [Fact]
    public void ParseCache_CaseOnlyDuplicateLoadsAreReleasedOnDispose()
    {
        var testFiles = new Dictionary<string, string>
        {
            ["/user/username/projects/myproject/src/main.ts"] = "import { util as a } from \"./utils\";\nimport { util as b } from \"./UTILS\";\nconst x = a + b;",
            ["/user/username/projects/myproject/src/utils.ts"] = "export const util = 1;",
        };
        Session session = Setup(testFiles);
        var mainURI = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        session.DidOpenFile(RequestContext.Background, mainURI, 1, testFiles["/user/username/projects/myproject/src/main.ts"], LanguageKind.TypeScript.Value!);

        (Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, mainURI);
        Assert.Null(err);

        int projectEntries = 0;
        // map-iter: 序无关 — only a filtered count over entries.
        session.parseCache.entries.Range((ParseCacheKey key, RefCountCacheEntry<SourceFile> _) =>
        {
            if (key.SourceFileParseOptions.FileName.StartsWith("/user/username/projects/myproject/src/", StringComparison.Ordinal))
            {
                projectEntries++;
            }
            return true;
        });
        Assert.Equal(3, projectEntries);

        SourceFile? utils = ls!.GetProgram().GetSourceFile("/user/username/projects/myproject/src/utils.ts");
        Assert.NotNull(utils);

        session.DidCloseFile(RequestContext.Background, mainURI);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();

        projectEntries = 0;
        // map-iter: 序无关 — only a filtered count over entries.
        session.parseCache.entries.Range((ParseCacheKey key, RefCountCacheEntry<SourceFile> _) =>
        {
            if (key.SourceFileParseOptions.FileName.StartsWith("/user/username/projects/myproject/src/", StringComparison.Ordinal))
            {
                projectEntries++;
            }
            return true;
        });
        Assert.Equal(0, projectEntries);
    }

    // refcountcache_test.go:256-351 — "case-only duplicate imported from multiple files
    // is refcounted once".
    [Fact]
    public void ParseCache_CaseOnlyDuplicateImportedFromMultipleFilesIsRefcountedOnce()
    {
        // A file reached through a case-only-different file name from more than one
        // import site is parsed and acquired in the parse cache exactly once (same-casing
        // loads dedupe), but it must also be recorded as a duplicate exactly once.
        // Recording it once per import site would release it from the parse cache more
        // times than it was acquired, deleting the live entry out from under a program
        // that still references it and panicking the next time it is ref'd during a clone.
        var testFiles = new Dictionary<string, string>
        {
            // entry.ts imports the canonical casing first, then pulls in a.ts and b.ts,
            // which both import the same file through an upper-cased name.
            ["/user/username/projects/myproject/src/entry.ts"] = "import { dep } from './sub/dep';\nimport './a';\nimport './b';\nexport const e = dep;",
            ["/user/username/projects/myproject/src/a.ts"] = "import { dep } from './sub/DEP';\nexport const a = dep;",
            ["/user/username/projects/myproject/src/b.ts"] = "import { dep } from './sub/DEP';\nexport const b = dep;",
            ["/user/username/projects/myproject/src/sub/dep.ts"] = "export const dep = 1;",
            ["/user/username/projects/myproject/src/c.ts"] = "export const c = 1;",
        };
        Session session = Setup(testFiles);
        var entryURI = new DocumentUri("file:///user/username/projects/myproject/src/entry.ts");
        session.DidOpenFile(RequestContext.Background, entryURI, 1, testFiles["/user/username/projects/myproject/src/entry.ts"], LanguageKind.TypeScript.Value!);

        (Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, entryURI);
        Assert.Null(err);

        // The upper-cased name is recorded as a duplicate, and it should appear exactly once.
        Ts.Compiler.Program program = ls!.GetProgram();
        var dupKeys = new List<ParseCacheKey>();
        foreach (Ts.Compiler.DuplicateSourceFile dup in program.DuplicateSourceFiles())
        {
            if (dup.ParseOptions.FileName.EndsWith("/sub/DEP.ts", StringComparison.Ordinal))
            {
                dupKeys.Add(ParseCache.NewParseCacheKey(dup.ParseOptions, dup.Hash, dup.ScriptKind));
            }
        }
        Assert.Single(dupKeys);
        bool dupOk = session.parseCache.entries.TryLoad(dupKeys[0], out RefCountCacheEntry<SourceFile>? dupEntry);
        Assert.True(dupOk, "duplicate entry should exist in the parse cache");
        Assert.Equal(1, dupEntry!.refCount);

        // Force a full program rebuild (adding an import changes the file's module
        // structure). The old snapshot is disposed, releasing each of its source and
        // duplicate files exactly once. If the duplicate were recorded twice, the
        // shared cache entry would be released to zero and deleted here even though
        // the new program still references it.
        session.DidChangeFile(RequestContext.Background, entryURI, 2,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new TextDocumentContentChangeWholeDocument
            {
                Text = "import { dep } from \"./sub/dep\";\nimport \"./a\";\nimport \"./b\";\nimport \"./c\";\nexport const e = dep;",
            }),
        ]);
        (Ts.Ls.LanguageService? lsAfterRebuild, Exception? errAfterRebuild) = session.GetLanguageService(RequestContext.Background, entryURI);
        Assert.Null(errAfterRebuild);
        session.WaitForBackgroundTasks();

        // Every parse-cache key referenced by the live program must still exist.
        void AssertKeyAlive(ParseCacheKey key)
        {
            bool alive = session.parseCache.entries.TryLoad(key, out _);
            Assert.True(alive, $"live program references a deleted parse-cache entry: {key.SourceFileParseOptions.FileName}");
        }
        Ts.Compiler.Program rebuiltProgram = lsAfterRebuild!.GetProgram();
        foreach (SourceFile file in rebuiltProgram.SourceFiles())
        {
            AssertKeyAlive(ParseCache.NewParseCacheKey(file.ParseOptions(), file.Hash, file.ScriptKind));
        }
        foreach (Ts.Compiler.DuplicateSourceFile dup in rebuiltProgram.DuplicateSourceFiles())
        {
            AssertKeyAlive(ParseCache.NewParseCacheKey(dup.ParseOptions, dup.Hash, dup.ScriptKind));
        }

        // An incremental (clone) update re-references the duplicate files; this must
        // not panic with "cache entry not found".
        session.DidChangeFile(RequestContext.Background, entryURI, 3,
        [
            TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(new TextDocumentContentChangeWholeDocument
            {
                Text = "import { dep } from './sub/dep';\nimport './a';\nimport './b';\nimport './c';\nexport const e = dep + 0;",
            }),
        ]);
        (_, Exception? errIncremental) = session.GetLanguageService(RequestContext.Background, entryURI);
        Assert.Null(errIncremental);
        session.WaitForBackgroundTasks();

        // Closing the project releases everything cleanly.
        // (The configured project is not disposed until another file in another project is opened,
        // so we open an untitled file to trigger that.)
        session.DidCloseFile(RequestContext.Background, entryURI);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();

        int projectEntries = 0;
        // map-iter: 序无关 — only a filtered count over entries.
        session.parseCache.entries.Range((ParseCacheKey key, RefCountCacheEntry<SourceFile> _) =>
        {
            if (key.SourceFileParseOptions.FileName.StartsWith("/user/username/projects/myproject/src/", StringComparison.Ordinal))
            {
                projectEntries++;
            }
            return true;
        });
        Assert.Equal(0, projectEntries);
    }

    // refcountcache_test.go:354-363 — the shared extendedConfigCache file set.
    private static Dictionary<string, string> ExtendedConfigCacheFiles() => new()
    {
        ["/user/username/projects/myproject/tsconfig.json"] = """
            {
                "extends": "./tsconfig.base.json"
            }
            """,
        ["/user/username/projects/myproject/tsconfig.base.json"] = """
            {
                "compilerOptions": {}
            }
            """,
        ["/user/username/projects/myproject/src/main.ts"] = "const x = 1;",
    };

    // refcountcache_test.go:365-381 — "release extended configs with project close".
    [Fact]
    public void ExtendedConfigCache_ReleaseExtendedConfigsWithProjectClose()
    {
        Dictionary<string, string> files = ExtendedConfigCacheFiles();
        Session session = Setup(files);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"), 1, files["/user/username/projects/myproject/src/main.ts"], LanguageKind.TypeScript.Value!);
        Snapshot snapshot = session.Snapshot()!;
        Ts.TsOptions.ParsedCommandLine config = snapshot.ConfigFileRegistry.GetConfig(new Path("/user/username/projects/myproject/tsconfig.json"))!;
        Assert.Equal("/user/username/projects/myproject/tsconfig.base.json", config.ExtendedSourceFiles()![0]);
        session.extendedConfigCache.entries.TryLoad(new Path("/user/username/projects/myproject/tsconfig.base.json"), out OwnerCacheEntry<ExtendedConfigCacheEntry>? extendedConfigEntry);
        Assert.NotNull(extendedConfigEntry);
        Assert.Single(extendedConfigEntry!.owners);

        session.DidCloseFile(RequestContext.Background, new DocumentUri("file:///user/username/projects/myproject/src/main.ts"));
        session.DidOpenFile(RequestContext.Background, new DocumentUri("untitled:Untitled-1"), 1, "", LanguageKind.TypeScript.Value!);
        session.WaitForBackgroundTasks();
        bool ok = session.extendedConfigCache.entries.TryLoad(new Path("/user/username/projects/myproject/tsconfig.base.json"), out _);
        Assert.False(ok);
    }

    // refcountcache_test.go:383-418 — "release cache entries for unretained clone".
    [Fact]
    public void ExtendedConfigCache_ReleaseCacheEntriesForUnretainedClone()
    {
        Dictionary<string, string> files = ExtendedConfigCacheFiles();
        Session session = Setup(files);
        var uri = new DocumentUri("file:///user/username/projects/myproject/src/main.ts");
        Snapshot baseSnapshot = session.Snapshot()!;
        var extendedConfigPath = new Path("/user/username/projects/myproject/tsconfig.base.json");
        Snapshot clone = baseSnapshot.Clone(RequestContext.Background, new Snapshot.SnapshotChange
        {
            Reason = UpdateReason.RequestedLanguageServiceProjectNotLoaded,
            ResourceRequest = new Snapshot.ResourceRequest
            {
                Documents = [uri],
            },
        }, baseSnapshot.fs.Overlays, session);

        Project? project = clone.GetDefaultProject(uri);
        Assert.NotNull(project);
        Assert.Equal(clone.SnapshotId, project!.ProgramLastUpdate);

        SourceFile main = project.GetProgram()!.GetSourceFile("/user/username/projects/myproject/src/main.ts")!;
        ParseCacheKey mainKey = ParseCache.NewParseCacheKey(main.ParseOptions(), main.Hash, main.ScriptKind);
        bool entryOk = session.parseCache.entries.TryLoad(mainKey, out RefCountCacheEntry<SourceFile>? mainEntry);
        Assert.True(entryOk);
        Assert.Equal(1, mainEntry!.refCount);

        bool configOk = session.extendedConfigCache.entries.TryLoad(extendedConfigPath, out OwnerCacheEntry<ExtendedConfigCacheEntry>? extendedConfigEntry);
        Assert.True(configOk);
        Assert.Single(extendedConfigEntry!.owners);

        clone.Deref(session);

        bool parseOk = session.parseCache.entries.TryLoad(mainKey, out _);
        Assert.False(parseOk);

        bool configAfterOk = session.extendedConfigCache.entries.TryLoad(extendedConfigPath, out _);
        Assert.False(configAfterOk);
    }
}
