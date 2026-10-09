// Ported from tsgo internal/project/snapshotfs_test.go (whole file).
//
// PORT-NOTES:
//  - Go's `&SnapshotFS{...}` field-literal construction maps to the internal positional
//    constructor (fields keep their Go order).
//  - `testFS.Remove(...)` returns void in C# (error → exception, plan/05 §4), so Go's
//    assert.NilError on Remove disappears.
//  - `len(snapshot.nodeModulesRealpathAliases)` tolerates a nil Go map; the C# Dictionary
//    may be null, hence `?.Count ?? 0`.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ts.Collections;
using Ts.LsProto;
using Ts.Project.Dirty;
using Ts.TsPath;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Project.Tests;

public sealed class SnapshotFsTests
{
    // snapshotfs_test.go:19-21 — toPath casts the string as-is (no normalization).
    private static Path ToPath(string fileName) => new(fileName);

    public sealed class SnapshotFSBuilderTests
    {
        // snapshotfs_test.go:23-61 — "builds directory tree on file add".
        [Fact]
        public void BuildsDirectoryTreeOnFileAdd()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "const foo = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(), // prevOverlays
                new Dictionary<Path, Overlay>(), // overlays
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(), // nodeModulesRealpathAliases
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Read the file to add it to the diskFiles
            FileHandle? fh = builder.GetFile("/src/foo.ts");
            Assert.NotNull(fh);
            Assert.Equal("const foo = 1;", fh!.Content());

            // Finalize and check directories
            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.True(changed, "should have changed");

            // Check that directory structure was built
            // /src should contain /src/foo.ts
            Assert.True(snapshot.DiskDirectories!.TryGetValue(new Path("/src"), out CloneableMap<Path, string>? srcDir), "/src directory should exist");
            Assert.True(srcDir!.ContainsKey(new Path("/src/foo.ts")), "/src should contain /src/foo.ts");

            // / should contain /src
            Assert.True(snapshot.DiskDirectories!.TryGetValue(new Path("/"), out CloneableMap<Path, string>? rootDir), "/ directory should exist");
            Assert.True(rootDir!.ContainsKey(new Path("/src")), "/ should contain /src");
        }

        // snapshotfs_test.go:63-96 — "builds nested directory tree".
        [Fact]
        public void BuildsNestedDirectoryTree()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/nested/deep/file.ts"] = "export const x = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Read the file to add it to the diskFiles
            FileHandle? fh = builder.GetFile("/src/nested/deep/file.ts");
            Assert.NotNull(fh);

            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.True(changed, "should have changed");

            // Check the complete directory tree
            Assert.True(snapshot.DiskDirectories![new Path("/src/nested/deep")].ContainsKey(new Path("/src/nested/deep/file.ts")));
            Assert.True(snapshot.DiskDirectories![new Path("/src/nested")].ContainsKey(new Path("/src/nested/deep")));
            Assert.True(snapshot.DiskDirectories![new Path("/src")].ContainsKey(new Path("/src/nested")));
            Assert.True(snapshot.DiskDirectories![new Path("/")].ContainsKey(new Path("/src")));
        }

        // snapshotfs_test.go:98-146 — "removes directory entries on file delete".
        [Fact]
        public void RemovesDirectoryEntriesOnFileDelete()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "const foo = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            // Start with existing diskFiles and directories
            var existingDiskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/foo.ts")] = DiskFile.NewDiskFile("/src/foo.ts", "const foo = 1;"),
            };
            var existingDirs = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new CloneableMap<Path, string> { [new Path("/src/foo.ts")] = "foo.ts" },
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                existingDiskFiles,
                existingDirs,
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Mark the file for deletion by loading and deleting
            (SyncMapEntry<Path, DiskFile>? entry, bool entryOk) = builder.DiskFiles.Load(new Path("/src/foo.ts"));
            if (entryOk)
            {
                entry!.Delete();
            }

            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.True(changed, "should have changed");

            // File should be deleted
            Assert.False(snapshot.DiskFiles!.ContainsKey(new Path("/src/foo.ts")), "file should be deleted");

            // Directory tree should be cleaned up
            Assert.False(snapshot.DiskDirectories!.ContainsKey(new Path("/src")), "/src directory should be removed");
            Assert.False(snapshot.DiskDirectories!.ContainsKey(new Path("/")), "root directory should be removed");
        }

        // snapshotfs_test.go:148-210 — "removes only empty directories on file delete".
        [Fact]
        public void RemovesOnlyEmptyDirectoriesOnFileDelete()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "const foo = 1;",
                ["/src/bar.ts"] = "const bar = 2;",
            }, false /*useCaseSensitiveFileNames*/);

            // Start with existing diskFiles and directories
            var existingDiskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/foo.ts")] = DiskFile.NewDiskFile("/src/foo.ts", "const foo = 1;"),
                [new Path("/src/bar.ts")] = DiskFile.NewDiskFile("/src/bar.ts", "const bar = 2;"),
            };
            var existingDirs = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new CloneableMap<Path, string>
                {
                    [new Path("/src/foo.ts")] = "foo.ts",
                    [new Path("/src/bar.ts")] = "bar.ts",
                },
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                existingDiskFiles,
                existingDirs,
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Delete only foo.ts
            (SyncMapEntry<Path, DiskFile>? entry, bool entryOk) = builder.DiskFiles.Load(new Path("/src/foo.ts"));
            if (entryOk)
            {
                entry!.Delete();
            }

            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.True(changed, "should have changed");

            // foo.ts should be deleted
            Assert.False(snapshot.DiskFiles!.ContainsKey(new Path("/src/foo.ts")), "foo.ts should be deleted");

            // bar.ts should still exist
            Assert.True(snapshot.DiskFiles!.ContainsKey(new Path("/src/bar.ts")), "bar.ts should still exist");

            // /src directory should still exist with bar.ts
            Assert.True(snapshot.DiskDirectories!.TryGetValue(new Path("/src"), out CloneableMap<Path, string>? srcDir), "/src directory should still exist");
            Assert.False(srcDir!.ContainsKey(new Path("/src/foo.ts")), "/src should not contain foo.ts");
            Assert.True(srcDir.ContainsKey(new Path("/src/bar.ts")), "/src should contain bar.ts");

            // root should still contain /src
            Assert.True(snapshot.DiskDirectories!.TryGetValue(new Path("/"), out CloneableMap<Path, string>? rootDir), "root directory should still exist");
            Assert.True(rootDir!.ContainsKey(new Path("/src")), "root should contain /src");
        }

        // snapshotfs_test.go:212-256 — "adds file to existing directory".
        [Fact]
        public void AddsFileToExistingDirectory()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "const foo = 1;",
                ["/src/bar.ts"] = "const bar = 2;",
            }, false /*useCaseSensitiveFileNames*/);

            // Start with existing file and directories
            var existingDiskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/foo.ts")] = DiskFile.NewDiskFile("/src/foo.ts", "const foo = 1;"),
            };
            var existingDirs = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new CloneableMap<Path, string> { [new Path("/src/foo.ts")] = "foo.ts" },
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                existingDiskFiles,
                existingDirs,
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Read bar.ts to add it
            FileHandle? fh = builder.GetFile("/src/bar.ts");
            Assert.NotNull(fh);

            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.True(changed, "should have changed");

            // /src should contain both files
            CloneableMap<Path, string> srcDir = snapshot.DiskDirectories![new Path("/src")];
            Assert.True(srcDir.ContainsKey(new Path("/src/foo.ts")), "/src should contain foo.ts");
            Assert.True(srcDir.ContainsKey(new Path("/src/bar.ts")), "/src should contain bar.ts");
        }

        // snapshotfs_test.go:258-295 — "no change when no files added or deleted".
        [Fact]
        public void NoChangeWhenNoFilesAddedOrDeleted()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "const foo = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            var existingDiskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/foo.ts")] = DiskFile.NewDiskFile("/src/foo.ts", "const foo = 1;"),
            };
            var existingDirs = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new CloneableMap<Path, string> { [new Path("/src/foo.ts")] = "foo.ts" },
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                existingDiskFiles,
                existingDirs,
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Don't add or delete any files
            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.False(changed, "should not have changed");

            // Directories should remain the same
            CloneableMap<Path, string> srcDir = snapshot.DiskDirectories![new Path("/src")];
            Assert.True(srcDir.ContainsKey(new Path("/src/foo.ts")));
        }

        // snapshotfs_test.go:297-324 — "overlay files are returned over disk files".
        [Fact]
        public void OverlayFilesAreReturnedOverDiskFiles()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "const foo = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/foo.ts")] = Overlay.NewOverlay("/src/foo.ts", "const foo = 999;", 0, Ts.Core.ScriptKind.Unknown),
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(), // prevOverlays
                overlays,
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Should return overlay content
            FileHandle? fh = builder.GetFile("/src/foo.ts");
            Assert.NotNull(fh);
            Assert.Equal("const foo = 999;", fh!.Content());
        }

        // snapshotfs_test.go:326-426 — "multiple files added and deleted in single cycle".
        [Fact]
        public void MultipleFilesAddedAndDeletedInSingleCycle()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/a.ts"] = "const a = 1;",
                ["/src/b.ts"] = "const b = 2;",
                ["/lib/utils.ts"] = "export const util = 1;",
                ["/lib/helpers.ts"] = "export const helper = 1;",
                ["/other/single.ts"] = "const single = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            // Start with some existing files
            var existingDiskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/a.ts")] = DiskFile.NewDiskFile("/src/a.ts", "const a = 1;"),
                [new Path("/other/single.ts")] = DiskFile.NewDiskFile("/other/single.ts", "const single = 1;"),
            };
            var existingDirs = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string>
                {
                    [new Path("/src")] = "src",
                    [new Path("/other")] = "other",
                },
                [new Path("/src")] = new CloneableMap<Path, string> { [new Path("/src/a.ts")] = "a.ts" },
                [new Path("/other")] = new CloneableMap<Path, string> { [new Path("/other/single.ts")] = "single.ts" },
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(), // prevOverlays
                new Dictionary<Path, Overlay>(), // overlays
                existingDiskFiles,
                existingDirs,
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Add new files
            FileHandle? fh = builder.GetFile("/src/b.ts");
            Assert.NotNull(fh);
            fh = builder.GetFile("/lib/utils.ts");
            Assert.NotNull(fh);
            fh = builder.GetFile("/lib/helpers.ts");
            Assert.NotNull(fh);

            // Delete existing files
            (SyncMapEntry<Path, DiskFile>? entryA, bool entryAOk) = builder.DiskFiles.Load(new Path("/src/a.ts"));
            if (entryAOk)
            {
                entryA!.Delete();
            }
            (SyncMapEntry<Path, DiskFile>? entrySingle, bool entrySingleOk) = builder.DiskFiles.Load(new Path("/other/single.ts"));
            if (entrySingleOk)
            {
                entrySingle!.Delete();
            }

            (SnapshotFS snapshot, bool changed) = builder.Finalize();
            Assert.True(changed, "should have changed");

            // Verify deleted files are gone
            Assert.False(snapshot.DiskFiles!.ContainsKey(new Path("/src/a.ts")), "/src/a.ts should be deleted");
            Assert.False(snapshot.DiskFiles!.ContainsKey(new Path("/other/single.ts")), "/other/single.ts should be deleted");

            // Verify added files exist
            Assert.True(snapshot.DiskFiles!.ContainsKey(new Path("/src/b.ts")), "/src/b.ts should exist");
            Assert.True(snapshot.DiskFiles!.ContainsKey(new Path("/lib/utils.ts")), "/lib/utils.ts should exist");
            Assert.True(snapshot.DiskFiles!.ContainsKey(new Path("/lib/helpers.ts")), "/lib/helpers.ts should exist");

            // Verify /other directory is cleaned up (was only entry deleted)
            Assert.False(snapshot.DiskDirectories!.ContainsKey(new Path("/other")), "/other directory should be removed");

            // Verify /src still exists with b.ts (a.ts deleted, b.ts added)
            Assert.True(snapshot.DiskDirectories!.TryGetValue(new Path("/src"), out CloneableMap<Path, string>? srcDir), "/src directory should exist");
            Assert.False(srcDir!.ContainsKey(new Path("/src/a.ts")), "/src should not contain a.ts");
            Assert.True(srcDir.ContainsKey(new Path("/src/b.ts")), "/src should contain b.ts");

            // Verify /lib was created with both files
            Assert.True(snapshot.DiskDirectories!.TryGetValue(new Path("/lib"), out CloneableMap<Path, string>? libDir), "/lib directory should exist");
            Assert.True(libDir!.ContainsKey(new Path("/lib/utils.ts")), "/lib should contain utils.ts");
            Assert.True(libDir.ContainsKey(new Path("/lib/helpers.ts")), "/lib should contain helpers.ts");

            // Verify root contains /src and /lib but not /other
            CloneableMap<Path, string> rootDir = snapshot.DiskDirectories![new Path("/")];
            Assert.True(rootDir.ContainsKey(new Path("/src")), "root should contain /src");
            Assert.True(rootDir.ContainsKey(new Path("/lib")), "root should contain /lib");
            Assert.False(rootDir.ContainsKey(new Path("/other")), "root should not contain /other");
        }

        // snapshotfs_test.go:428-469 — "overlay directories are computed from overlays".
        [Fact]
        public void OverlayDirectoriesAreComputedFromOverlays()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>(), false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/overlay.ts")] = Overlay.NewOverlay("/src/overlay.ts", "const x = 1;", 0, Ts.Core.ScriptKind.Unknown),
                [new Path("/src/nested/deep.ts")] = Overlay.NewOverlay("/src/nested/deep.ts", "const y = 2;", 0, Ts.Core.ScriptKind.Unknown),
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(), // prevOverlays
                overlays,
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Check overlayDirectories was built correctly
            Assert.True(builder.overlayDirectories.TryGetValue(new Path("/src"), out Dictionary<Path, string>? srcDir), "/src overlay directory should exist");
            Assert.True(srcDir!.ContainsKey(new Path("/src/overlay.ts")), "/src should contain overlay.ts");
            Assert.True(srcDir.ContainsKey(new Path("/src/nested")), "/src should contain nested/");

            Assert.True(builder.overlayDirectories.TryGetValue(new Path("/src/nested"), out Dictionary<Path, string>? nestedDir), "/src/nested overlay directory should exist");
            Assert.True(nestedDir!.ContainsKey(new Path("/src/nested/deep.ts")), "/src/nested should contain deep.ts");

            Assert.True(builder.overlayDirectories.TryGetValue(new Path("/"), out Dictionary<Path, string>? rootDir), "/ overlay directory should exist");
            Assert.True(rootDir!.ContainsKey(new Path("/src")), "/ should contain /src");
        }

        // snapshotfs_test.go:471-499 — "GetAccessibleEntries combines disk and overlay".
        [Fact]
        public void GetAccessibleEntriesCombinesDiskAndOverlay()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/disk.ts"] = "const disk = 1;",
            }, false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/overlay.ts")] = Overlay.NewOverlay("/src/overlay.ts", "const overlay = 1;", 0, Ts.Core.ScriptKind.Unknown),
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(), // prevOverlays
                overlays,
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            Entries entries = builder.GetAccessibleEntries("/src");

            // Should contain both disk file and overlay file (both as basenames)
            Assert.Contains("disk.ts", entries.Files!);
            Assert.Contains("overlay.ts", entries.Files!);
        }

        // snapshotfs_test.go:501-544 — "GetAccessibleEntries is safe under concurrent calls".
        [Fact]
        public async Task GetAccessibleEntriesIsSafeUnderConcurrentCalls()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/a.ts"] = "",
                ["/src/b.ts"] = "",
                ["/src/c.ts"] = "",
                ["/src/d.ts"] = "",
                ["/src/e.ts"] = "",
            }, false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/overlay.ts")] = Overlay.NewOverlay("/src/overlay.ts", "", 0, Ts.Core.ScriptKind.Unknown),
            };

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(), // prevOverlays
                overlays,
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            _ = builder.fs.GetAccessibleEntries("/src");

            // Go: start channel + wg.Go × 50 goroutines.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ct = TestContext.Current.CancellationToken;
            var tasks = new Task[50];
            for (int i = 0; i < 50; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    await start.Task;
                    _ = builder.GetAccessibleEntries("/src");
                }, ct);
            }
            start.SetResult();
            await Task.WhenAll(tasks);

            // Sanity: the overlay is still reported after all the concurrent calls.
            Entries entries = builder.GetAccessibleEntries("/src");
            Assert.Contains("overlay.ts", entries.Files!);
        }
    }

    public sealed class SnapshotFSTests
    {
        // snapshotfs_test.go:554-578 — TestSnapshotFS/"GetFile returns overlay file".
        [Fact]
        public void GetFileReturnsOverlayFile()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "disk content",
            }, false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/foo.ts")] = Overlay.NewOverlay("/src/foo.ts", "overlay content", 0, Ts.Core.ScriptKind.Unknown),
            };

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                overlays,
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            FileHandle? fh = snapshot.GetFile("/src/foo.ts");
            Assert.NotNull(fh);
            Assert.Equal("overlay content", fh!.Content());
        }

        // snapshotfs_test.go:580-602 — "GetFile returns disk file when not in overlay".
        [Fact]
        public void GetFileReturnsDiskFileWhenNotInOverlay()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "disk content",
            }, false /*useCaseSensitiveFileNames*/);

            var diskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/foo.ts")] = DiskFile.NewDiskFile("/src/foo.ts", "disk content"),
            };

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                diskFiles,
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            FileHandle? fh = snapshot.GetFile("/src/foo.ts");
            Assert.NotNull(fh);
            Assert.Equal("disk content", fh!.Content());
        }

        // snapshotfs_test.go:604-622 — "GetFile reads from fs when not cached".
        [Fact]
        public void GetFileReadsFromFsWhenNotCached()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "fs content",
            }, false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            FileHandle? fh = snapshot.GetFile("/src/foo.ts");
            Assert.NotNull(fh);
            Assert.Equal("fs content", fh!.Content());
        }

        // snapshotfs_test.go:624-639 — "GetFile returns nil for non-existent file".
        [Fact]
        public void GetFileReturnsNilForNonExistentFile()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>(), false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            FileHandle? fh = snapshot.GetFile("/src/nonexistent.ts");
            Assert.Null(fh);
        }

        // snapshotfs_test.go:641-662 — "isOpenFile returns true for overlays".
        [Fact]
        public void IsOpenFileReturnsTrueForOverlays()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>(), false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/foo.ts")] = Overlay.NewOverlay("/src/foo.ts", "overlay content", 0, Ts.Core.ScriptKind.Unknown),
            };

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                overlays,
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            Assert.True(snapshot.IsOpenFile("/src/foo.ts"), "overlay file should be open");
            Assert.False(snapshot.IsOpenFile("/src/bar.ts"), "non-overlay file should not be open");
        }

        // snapshotfs_test.go:664-689 — "GetFileByPath uses provided path".
        [Fact]
        public void GetFileByPathUsesProvidedPath()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "disk content",
            }, false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/foo.ts")] = Overlay.NewOverlay("/src/foo.ts", "overlay content", 0, Ts.Core.ScriptKind.Unknown),
            };

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                overlays,
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            // GetFileByPath should use the provided path directly
            FileHandle? fh = snapshot.GetFileByPath("/src/foo.ts", new Path("/src/foo.ts"));
            Assert.NotNull(fh);
            Assert.Equal("overlay content", fh!.Content());
        }

        // snapshotfs_test.go:691-734 — "GetAccessibleEntries combines disk and overlay directories".
        [Fact]
        public void GetAccessibleEntriesCombinesDiskAndOverlayDirectories()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>(), false /*useCaseSensitiveFileNames*/);

            var overlays = new Dictionary<Path, Overlay>
            {
                [new Path("/src/overlay.ts")] = Overlay.NewOverlay("/src/overlay.ts", "overlay content", 0, Ts.Core.ScriptKind.Unknown),
            };
            var overlayDirectories = new Dictionary<Path, Dictionary<Path, string>>
            {
                [new Path("/")] = new Dictionary<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new Dictionary<Path, string> { [new Path("/src/overlay.ts")] = "overlay.ts" },
            };
            var diskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/disk.ts")] = DiskFile.NewDiskFile("/src/disk.ts", "disk content"),
            };
            var diskDirectories = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new CloneableMap<Path, string> { [new Path("/src/disk.ts")] = "disk.ts" },
            };

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                overlays,
                overlayDirectories,
                diskFiles,
                diskDirectories,
                null);

            Entries entries = snapshot.GetAccessibleEntries("/src");

            // Should contain both disk file and overlay file (both as basenames)
            Assert.Contains("disk.ts", entries.Files!);
            Assert.Contains("overlay.ts", entries.Files!);
        }
    }

    public sealed class SourceFSTests
    {
        // snapshotfs_test.go:744-770 — TestSourceFS/"tracks files when tracking enabled".
        [Fact]
        public void TracksFilesWhenTrackingEnabled()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "content",
            }, false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            SourceFS sourceFS = SourceFS.NewSourceFS(true /*tracking*/, snapshot, SnapshotFsTests.ToPath);

            // File should not be seen yet
            Assert.False(sourceFS.SeenFile(new Path("/src/foo.ts")));

            // Read the file
            FileHandle? fh = sourceFS.GetFile("/src/foo.ts");
            Assert.NotNull(fh);

            // Now it should be seen
            Assert.True(sourceFS.SeenFile(new Path("/src/foo.ts")));
        }

        // snapshotfs_test.go:772-795 — "does not track files when tracking disabled".
        [Fact]
        public void DoesNotTrackFilesWhenTrackingDisabled()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "content",
            }, false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            SourceFS sourceFS = SourceFS.NewSourceFS(false /*tracking*/, snapshot, SnapshotFsTests.ToPath);

            // Read the file
            FileHandle? fh = sourceFS.GetFile("/src/foo.ts");
            Assert.NotNull(fh);

            // Should not be seen since tracking is disabled
            Assert.False(sourceFS.SeenFile(new Path("/src/foo.ts")));
        }

        // snapshotfs_test.go:797-825 — "DisableTracking stops tracking".
        [Fact]
        public void DisableTrackingStopsTracking()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "content",
                ["/src/bar.ts"] = "content",
            }, false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            SourceFS sourceFS = SourceFS.NewSourceFS(true /*tracking*/, snapshot, SnapshotFsTests.ToPath);

            // Read foo while tracking
            sourceFS.GetFile("/src/foo.ts");
            Assert.True(sourceFS.SeenFile(new Path("/src/foo.ts")));

            // Disable tracking
            sourceFS.DisableTracking();

            // Read bar after tracking disabled
            sourceFS.GetFile("/src/bar.ts");
            Assert.False(sourceFS.SeenFile(new Path("/src/bar.ts")));
        }

        // snapshotfs_test.go:827-846 — "FileExists returns true for files in source".
        [Fact]
        public void FileExistsReturnsTrueForFilesInSource()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "content",
            }, false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            SourceFS sourceFS = SourceFS.NewSourceFS(false /*tracking*/, snapshot, SnapshotFsTests.ToPath);

            Assert.True(sourceFS.FileExists("/src/foo.ts"));
            Assert.False(sourceFS.FileExists("/src/nonexistent.ts"));
        }

        // snapshotfs_test.go:848-871 — "ReadFile returns content for files in source".
        [Fact]
        public void ReadFileReturnsContentForFilesInSource()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/src/foo.ts"] = "file content",
            }, false /*useCaseSensitiveFileNames*/);

            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                null);

            SourceFS sourceFS = SourceFS.NewSourceFS(false /*tracking*/, snapshot, SnapshotFsTests.ToPath);

            Assert.True(sourceFS.TryReadFile("/src/foo.ts", out string content));
            Assert.Equal("file content", content);

            Assert.False(sourceFS.TryReadFile("/src/nonexistent.ts", out content));
        }
    }

    public sealed class AutoImportBuilderFSTests
    {
        // snapshotfs_test.go:888-937 — TestAutoImportBuilderFS/"symlink cache mismatch: file
        // cached at symlink path, missed at realpath after deletion".
        //
        // This test demonstrates that autoImportBuilderFS stores files in untrackedFiles keyed
        // by the path derived from the filename. When module resolution reads a file via its
        // symlink path, the file is cached at the symlink path key. If the file is subsequently
        // requested by its realpath (which is the typical case after module resolution resolves
        // the symlink), the cache is bypassed. If the file was deleted from disk between those
        // two operations, the realpath read fails and returns nil. This is the mechanism behind
        // the nil source file crash in aliasResolver.GetSourceFile.
        [Fact]
        public void SymlinkCacheMismatchFileCachedAtSymlinkPathMissedAtRealpathAfterDeletion()
        {
            // Create a VFS with a real file and a symlinked directory pointing to it.
            // /real/pkg/index.d.ts is the real file.
            // /project/node_modules/pkg is a symlink to /real/pkg.
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/real/pkg/index.d.ts"] = "export declare const x: number;",
                ["/project/node_modules/pkg"] = VfsTestUtil.Symlink("/real/pkg"),
            }, true /*useCaseSensitiveFileNames*/);

            // Verify symlink works as expected
            string symlinkPath = "/project/node_modules/pkg/index.d.ts";
            string realpathPath = testFS.Realpath(symlinkPath);
            Assert.Equal("/real/pkg/index.d.ts", realpathPath);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            var autoImportFS = new AutoImportBuilderFS(builder);

            // Step 1: Read the file via its symlink path (simulating what module resolution does
            // during FileExists). This caches the file in untrackedFiles at the symlink path key.
            FileHandle? fh = autoImportFS.GetFile(symlinkPath);
            Assert.NotNull(fh);
            Assert.Equal("export declare const x: number;", fh!.Content());

            // Step 2: Simulate a file deletion from disk (e.g., npm install running concurrently).
            // This deletes both the real file and effectively breaks the symlink.
            testFS.Remove("/real/pkg/index.d.ts");

            // Step 3: Request the file by its realpath (simulating what GetSourceFile does after
            // the checker resolves the module). This bypasses the symlink-path cache entry in
            // untrackedFiles because the realpath has a different key.
            FileHandle? fh2 = autoImportFS.GetFile(realpathPath);
            // The file was cached at the symlink path, but the realpath lookup misses the cache
            // and goes to disk where the file is now deleted. This returns nil.
            Assert.Null(fh2);
        }
    }

    public sealed class RealpathAliasLifecycleTests
    {
        // snapshotfs_test.go:947-986 — "alias recorded when reading symlinked node_modules file".
        [Fact]
        public void AliasRecordedWhenReadingSymlinkedNodeModulesFile()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib"", ""main"": ""index.js""}",
                ["/packages/mylib/index.d.ts"] = "export declare const x: number;",
                ["/project/node_modules/nolink/package.json"] = @"{""name"": ""nolink""}",
            }, false);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Read a file through the symlink — should record an alias.
            FileHandle? fh = builder.GetFile("/project/node_modules/mylib/package.json");
            Assert.NotNull(fh);
            Assert.Equal(@"{""name"": ""mylib"", ""main"": ""index.js""}", fh!.Content());

            // Read a non-symlinked node_modules file — should NOT record an alias.
            FileHandle? fh2 = builder.GetFile("/project/node_modules/nolink/package.json");
            Assert.NotNull(fh2);

            (SnapshotFS snapshot, _) = builder.Finalize();

            // Alias exists for the symlinked file.
            Assert.True(snapshot.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases), "alias should exist for realpath of symlinked file");
            Assert.True(aliases!.paths.Has(new Path("/project/node_modules/mylib/package.json")));

            // No alias for the non-symlinked file.
            Assert.False(snapshot.NodeModulesRealpathAliases!.ContainsKey(new Path("/project/node_modules/nolink/package.json")), "no alias should exist for non-symlinked file");
        }

        // snapshotfs_test.go:988-1011 — "no alias recorded for files outside node_modules".
        [Fact]
        public void NoAliasRecordedForFilesOutsideNodeModules()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/link"] = VfsTestUtil.Symlink("/elsewhere"),
                ["/elsewhere/index.ts"] = "export const x = 1;",
            }, false);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            FileHandle? fh = builder.GetFile("/project/link/index.ts");
            Assert.NotNull(fh);

            (SnapshotFS snapshot, _) = builder.Finalize();
            Assert.Equal(0, snapshot.NodeModulesRealpathAliases?.Count ?? 0);
        }

        // snapshotfs_test.go:1013-1051 — "aliases carried over across snapshots".
        [Fact]
        public void AliasesCarriedOverAcrossSnapshots()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
            }, false);

            // Build first snapshot.
            var builder1 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder1.GetFile("/project/node_modules/mylib/package.json");
            (SnapshotFS snapshot1, _) = builder1.Finalize();

            // Build second snapshot from the first, without reading the file again.
            var builder2 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                snapshot1.DiskFiles!,
                snapshot1.DiskDirectories!,
                snapshot1.NodeModulesRealpathAliases!,
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            (SnapshotFS snapshot2, _) = builder2.Finalize();

            // Alias should still be present.
            Assert.True(snapshot2.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases), "alias should survive across snapshots");
            Assert.True(aliases!.paths.Has(new Path("/project/node_modules/mylib/package.json")));
        }

        // snapshotfs_test.go:1053-1110 — "alias pruned when symlinked file is deleted".
        [Fact]
        public void AliasPrunedWhenSymlinkedFileIsDeleted()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
                ["/packages/mylib/index.d.ts"] = "export declare const x: number;",
            }, false);

            // Build first snapshot — read both files.
            var builder1 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder1.GetFile("/project/node_modules/mylib/package.json");
            builder1.GetFile("/project/node_modules/mylib/index.d.ts");
            (SnapshotFS snapshot1, _) = builder1.Finalize();

            // Both should be aliased under the same realpath directory but separate files.
            Assert.True(snapshot1.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/mylib/package.json")));
            Assert.True(snapshot1.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/mylib/index.d.ts")));

            // Build second snapshot — delete one file via markDirtyFiles.
            var builder2 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                snapshot1.DiskFiles!,
                snapshot1.DiskDirectories!,
                snapshot1.NodeModulesRealpathAliases!,
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Simulate deletion of index.d.ts from the disk file cache.
            (SyncMapEntry<Path, DiskFile>? entry, bool entryOk) = builder2.DiskFiles.Load(new Path("/project/node_modules/mylib/index.d.ts"));
            if (entryOk)
            {
                entry!.Delete();
            }

            (SnapshotFS snapshot2, _) = builder2.Finalize();

            // package.json alias should remain.
            Assert.True(snapshot2.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases), "package.json alias should survive");
            Assert.True(aliases!.paths.Has(new Path("/project/node_modules/mylib/package.json")));

            // index.d.ts alias should be fully pruned (empty set → removed from map).
            Assert.False(snapshot2.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/mylib/index.d.ts")), "index.d.ts alias should be pruned after deletion");
        }

        // snapshotfs_test.go:1112-1143 — "multiple symlinks to same realpath".
        [Fact]
        public void MultipleSymlinksToSameRealpath()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/project/node_modules/alias"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
            }, false);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            // Read via both symlinks.
            FileHandle? fh1 = builder.GetFile("/project/node_modules/mylib/package.json");
            Assert.NotNull(fh1);
            FileHandle? fh2 = builder.GetFile("/project/node_modules/alias/package.json");
            Assert.NotNull(fh2);

            (SnapshotFS snapshot, _) = builder.Finalize();

            Assert.True(snapshot.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases), "alias should exist");
            Assert.True(aliases!.paths.Has(new Path("/project/node_modules/mylib/package.json")));
            Assert.True(aliases.paths.Has(new Path("/project/node_modules/alias/package.json")));
        }

        // snapshotfs_test.go:1145-1189 — "multiple symlinks pruned individually".
        [Fact]
        public void MultipleSymlinksPrunedIndividually()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/project/node_modules/alias"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
            }, false);

            // Build first snapshot – read via both symlinks.
            var builder1 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder1.GetFile("/project/node_modules/mylib/package.json");
            builder1.GetFile("/project/node_modules/alias/package.json");
            (SnapshotFS snapshot1, _) = builder1.Finalize();

            // Build second snapshot – delete ONE of the symlink disk entries.
            var builder2 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                snapshot1.DiskFiles!,
                snapshot1.DiskDirectories!,
                snapshot1.NodeModulesRealpathAliases!,
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            (SyncMapEntry<Path, DiskFile>? entry, bool entryOk) = builder2.DiskFiles.Load(new Path("/project/node_modules/alias/package.json"));
            if (entryOk)
            {
                entry!.Delete();
            }
            (SnapshotFS snapshot2, _) = builder2.Finalize();

            // The realpath alias set should still exist, but only contain the surviving symlink.
            Assert.True(snapshot2.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases), "alias set should still exist");
            Assert.True(aliases!.paths.Has(new Path("/project/node_modules/mylib/package.json")), "surviving symlink should remain");
            Assert.False(aliases.paths.Has(new Path("/project/node_modules/alias/package.json")), "deleted symlink should be pruned");
        }

        // snapshotfs_test.go:1191-1220 — "expandRealpathAliases expands change events".
        [Fact]
        public void ExpandRealpathAliasesExpandsChangeEvents()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
            }, false);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder.GetFile("/project/node_modules/mylib/package.json");
            (SnapshotFS snapshot, _) = builder.Finalize();

            // Simulate a watch event on the REALPATH.
            var change = new FileChangeSummary();
            change.Changed.Add(new DocumentUri("file:///packages/mylib/package.json"));

            FileChangeSummary expanded = snapshot.ExpandRealpathAliases(change);

            // Should now also contain the symlink path.
            Assert.True(expanded.Changed.Has(new DocumentUri("file:///packages/mylib/package.json")), "original event should remain");
            Assert.True(expanded.Changed.Has(new DocumentUri("file:///project/node_modules/mylib/package.json")), "symlink event should be added");
        }

        // snapshotfs_test.go:1222-1249 — "expandRealpathAliases expands delete events".
        [Fact]
        public void ExpandRealpathAliasesExpandsDeleteEvents()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
            }, false);

            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder.GetFile("/project/node_modules/mylib/package.json");
            (SnapshotFS snapshot, _) = builder.Finalize();

            // Simulate a delete watch event on the REALPATH.
            var change = new FileChangeSummary();
            change.Deleted.Add(new DocumentUri("file:///packages/mylib/package.json"));

            FileChangeSummary expanded = snapshot.ExpandRealpathAliases(change);

            Assert.True(expanded.Deleted.Has(new DocumentUri("file:///project/node_modules/mylib/package.json")), "symlink deletion should be added");
        }

        // snapshotfs_test.go:1251-1264 — "expandRealpathAliases is a no-op with no aliases".
        [Fact]
        public void ExpandRealpathAliasesIsNoopWithNoAliases()
        {
            var snapshot = new SnapshotFS(
                SnapshotFsTests.ToPath,
                VfsTestUtil.FromMap<string>(null, false),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Dictionary<Path, string>>(),
                null,
                null,
                null);

            var change = new FileChangeSummary();
            change.Changed.Add(new DocumentUri("file:///some/file.ts"));

            FileChangeSummary expanded = snapshot.ExpandRealpathAliases(change);
            Assert.Equal(1, expanded.Changed.Len());
            Assert.True(expanded.Changed.Has(new DocumentUri("file:///some/file.ts")));
        }

        // snapshotfs_test.go:1266-1324 — "markDirtyFiles invalidates symlinked file via realpath event".
        [Fact]
        public void MarkDirtyFilesInvalidatesSymlinkedFileViaRealpathEvent()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib"", ""main"": ""index.js""}",
            }, false);

            // Build first snapshot — read the symlinked file.
            var builder1 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            FileHandle? fh = builder1.GetFile("/project/node_modules/mylib/package.json");
            Assert.NotNull(fh);
            Assert.Equal(@"{""name"": ""mylib"", ""main"": ""index.js""}", fh!.Content());
            (SnapshotFS snapshot1, _) = builder1.Finalize();

            // Modify the real file on disk.
            testFS.WriteFile("/packages/mylib/package.json", @"{""name"": ""mylib""}");

            // Build second snapshot — simulate realpath change event, expanded via aliases.
            var builder2 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                snapshot1.DiskFiles!,
                snapshot1.DiskDirectories!,
                snapshot1.NodeModulesRealpathAliases!,
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            var change = new FileChangeSummary();
            change.Changed.Add(new DocumentUri("file:///packages/mylib/package.json"));

            // Expand the realpath event to include the symlink path.
            change = snapshot1.ExpandRealpathAliases(change);
            // Now mark dirty — should find the file under the symlink key.
            builder2.MarkDirtyFiles(change);

            // Trigger reload by reading the file (simulates program construction).
            fh = builder2.GetFile("/project/node_modules/mylib/package.json");
            Assert.NotNull(fh);
            Assert.Equal(@"{""name"": ""mylib""}", fh!.Content());

            (SnapshotFS snapshot2, _) = builder2.Finalize();

            // The file should have been reloaded with new content.
            Assert.True(snapshot2.DiskFiles!.TryGetValue(new Path("/project/node_modules/mylib/package.json"), out DiskFile? file), "file should still be in diskFiles");
            Assert.Equal(@"{""name"": ""mylib""}", file!.Content());
        }

        // snapshotfs_test.go:1326-1374 — "alias clone isolation between snapshots".
        [Fact]
        public void AliasCloneIsolationBetweenSnapshots()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/project/node_modules/other"] = VfsTestUtil.Symlink("/packages/other"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
                ["/packages/other/package.json"] = @"{""name"": ""other""}",
            }, false);

            // Build first snapshot — read only mylib.
            var builder1 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder1.GetFile("/project/node_modules/mylib/package.json");
            (SnapshotFS snapshot1, _) = builder1.Finalize();

            // Build second snapshot — also read other.
            var builder2 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                snapshot1.DiskFiles!,
                snapshot1.DiskDirectories!,
                snapshot1.NodeModulesRealpathAliases!,
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder2.GetFile("/project/node_modules/other/package.json");
            (SnapshotFS snapshot2, _) = builder2.Finalize();

            // snapshot1 should only have mylib alias.
            Assert.True(snapshot1.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/mylib/package.json")), "snapshot1 should have mylib alias");
            Assert.False(snapshot1.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/other/package.json")), "snapshot1 should NOT have other alias — it was added in a later snapshot");

            // snapshot2 should have both.
            Assert.True(snapshot2.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/mylib/package.json")), "snapshot2 should have mylib alias");
            Assert.True(snapshot2.NodeModulesRealpathAliases!.ContainsKey(new Path("/packages/other/package.json")), "snapshot2 should have other alias");
        }

        // snapshotfs_test.go:1376-1431 — "adding symlink to inherited realpath key does not mutate previous snapshot".
        [Fact]
        public void AddingSymlinkToInheritedRealpathKeyDoesNotMutatePreviousSnapshot()
        {
            IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/project/node_modules/mylib"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/project/node_modules/alias"] = VfsTestUtil.Symlink("/packages/mylib"),
                ["/packages/mylib/package.json"] = @"{""name"": ""mylib""}",
            }, false);

            // Snapshot 1: read via one symlink only.
            var builder1 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder1.GetFile("/project/node_modules/mylib/package.json");
            (SnapshotFS snapshot1, _) = builder1.Finalize();

            // Verify snapshot1 has exactly one alias for the realpath.
            Assert.True(snapshot1.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases1));
            Assert.Equal(1, aliases1!.paths.Len());
            Assert.True(aliases1.paths.Has(new Path("/project/node_modules/mylib/package.json")));

            // Snapshot 2: read via the SECOND symlink, which maps to the same realpath.
            // This exercises the case where LoadOrStore finds the key in the base map
            // and must clone-on-write rather than mutating the shared set.
            var builder2 = SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                snapshot1.DiskFiles!,
                snapshot1.DiskDirectories!,
                snapshot1.NodeModulesRealpathAliases!,
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
            builder2.GetFile("/project/node_modules/alias/package.json");
            (SnapshotFS snapshot2, _) = builder2.Finalize();

            // Snapshot 2 should have both symlinks.
            Assert.True(snapshot2.NodeModulesRealpathAliases!.TryGetValue(new Path("/packages/mylib/package.json"), out RealpathAliasSet? aliases2));
            Assert.Equal(2, aliases2!.paths.Len());
            Assert.True(aliases2.paths.Has(new Path("/project/node_modules/mylib/package.json")));
            Assert.True(aliases2.paths.Has(new Path("/project/node_modules/alias/package.json")));

            // Snapshot 1 must NOT have been mutated — it should still have only one alias.
            Assert.Equal(1, aliases1.paths.Len());
            Assert.False(aliases1.paths.Has(new Path("/project/node_modules/alias/package.json")),
                "snapshot1 must not contain alias added in snapshot2");
        }
    }

    public sealed class ExpandAndFilterWatchEventsTests
    {
        // snapshotfs_test.go:1441-1452 — newBuilder helper.
        private static SnapshotFSBuilder NewBuilder(IFileSystem testFS)
        {
            return SnapshotFSBuilder.NewSnapshotFSBuilder(
                testFS,
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, DiskFile>(),
                new Dictionary<Path, CloneableMap<Path, string>>(),
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);
        }

        // snapshotfs_test.go:1454-1469 — "preserves node_modules directory deletion even when untracked".
        [Fact]
        public void PreservesNodeModulesDirectoryDeletionEvenWhenUntracked()
        {
            // node_modules package files are read transiently and never tracked in
            // diskDirectories, so a node_modules directory deletion can neither be
            // expanded nor matched by extension. It must still be preserved.
            SnapshotFSBuilder builder = NewBuilder(VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/project/index.ts"] = "export const x = 1;",
            }, false));

            var change = new FileChangeSummary();
            change.Deleted.Add(new DocumentUri("file:///project/node_modules"));

            FileChangeSummary expanded = builder.ExpandAndFilterWatchEvents(change);
            Assert.True(expanded.Deleted.Has(new DocumentUri("file:///project/node_modules")),
                "bare node_modules directory deletion should be preserved");
        }

        // snapshotfs_test.go:1471-1483 — "preserves deletion of a package directory inside node_modules".
        [Fact]
        public void PreservesDeletionOfPackageDirectoryInsideNodeModules()
        {
            SnapshotFSBuilder builder = NewBuilder(VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/project/index.ts"] = "export const x = 1;",
            }, false));

            var change = new FileChangeSummary();
            change.Deleted.Add(new DocumentUri("file:///project/node_modules/@scope/pkg"));

            FileChangeSummary expanded = builder.ExpandAndFilterWatchEvents(change);
            Assert.True(expanded.Deleted.Has(new DocumentUri("file:///project/node_modules/@scope/pkg")),
                "package directory deletion inside node_modules should be preserved");
        }

        // snapshotfs_test.go:1485-1497 — "drops irrelevant untracked deletion outside node_modules".
        [Fact]
        public void DropsIrrelevantUntrackedDeletionOutsideNodeModules()
        {
            SnapshotFSBuilder builder = NewBuilder(VfsTestUtil.FromMap(new Dictionary<string, string>
            {
                ["/project/index.ts"] = "export const x = 1;",
            }, false));

            var change = new FileChangeSummary();
            change.Deleted.Add(new DocumentUri("file:///project/build"));

            FileChangeSummary expanded = builder.ExpandAndFilterWatchEvents(change);
            Assert.Equal(0, expanded.Deleted.Len());
        }

        // snapshotfs_test.go:1499-1527 — "expands tracked directory deletion into file deletions".
        [Fact]
        public void ExpandsTrackedDirectoryDeletionIntoFileDeletions()
        {
            var existingDiskFiles = new Dictionary<Path, DiskFile>
            {
                [new Path("/src/foo.ts")] = DiskFile.NewDiskFile("/src/foo.ts", "const foo = 1;"),
            };
            var existingDirs = new Dictionary<Path, CloneableMap<Path, string>>
            {
                [new Path("/")] = new CloneableMap<Path, string> { [new Path("/src")] = "src" },
                [new Path("/src")] = new CloneableMap<Path, string> { [new Path("/src/foo.ts")] = "foo.ts" },
            };
            var builder = SnapshotFSBuilder.NewSnapshotFSBuilder(
                VfsTestUtil.FromMap(new Dictionary<string, string> { ["/src/foo.ts"] = "const foo = 1;" }, false),
                new Dictionary<Path, Overlay>(),
                new Dictionary<Path, Overlay>(),
                existingDiskFiles,
                existingDirs,
                new Dictionary<Path, RealpathAliasSet>(),
                PositionEncodingKind.UTF16.Value!,
                SnapshotFsTests.ToPath);

            var change = new FileChangeSummary();
            change.Deleted.Add(new DocumentUri("file:///src"));

            FileChangeSummary expanded = builder.ExpandAndFilterWatchEvents(change);
            Assert.True(expanded.Deleted.Has(new DocumentUri("file:///src/foo.ts")),
                "tracked directory deletion should expand to contained file deletions");
            Assert.False(expanded.Deleted.Has(new DocumentUri("file:///src")),
                "the directory URI itself should be replaced by its files");
        }
    }
}
