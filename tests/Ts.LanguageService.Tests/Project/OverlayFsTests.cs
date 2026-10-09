// Ported from tsgo internal/project/overlayfs_test.go (whole file).

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.LsProto;
using Ts.TsPath;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

// DocumentUri.FileName / Set<T>.Has/.Len are extension methods (Go nil-tolerant
// receivers); pull them in as statics for parity with the production call sites.
using static Ts.LsProto.DocumentUriExtensions;

namespace Ts.Project.Tests;

public sealed class OverlayFsTests
{
    // overlayfs_test.go:16-29 — createOverlayFS helper.
    private static OverlayFS CreateOverlayFS()
    {
        IFileSystem testFS = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/test1.ts"] = "// existing content",
            ["/test2.ts"] = "// existing content",
        }, false /*useCaseSensitiveFileNames*/);
        return OverlayFS.NewOverlayFS(
            testFS,
            new Dictionary<Path, Overlay>(),
            Ts.LsProto.PositionEncodingKind.UTF16.Value!,
            fileName => new Path(fileName));
    }

    // overlayfs_test.go:32-35 — Test URI constants.
    private static readonly Ts.LsProto.DocumentUri TestURI1 = new("file:///test1.ts");
    private static readonly Ts.LsProto.DocumentUri TestURI2 = new("file:///test2.ts");

    // overlayfs_test.go:37-67 — TestProcessChanges/"multiple opens should panic".
    [Fact]
    public void ProcessChanges_MultipleOpensShouldPanic()
    {
        OverlayFS fs = CreateOverlayFS();

        var changes = new List<FileChange>
        {
            new()
            {
                Kind = FileChangeKind.Open,
                URI = TestURI1,
                Version = 1,
                Content = "const x = 1;",
                LanguageKind = Ts.LsProto.LanguageKind.TypeScript.Value!,
            },
            new()
            {
                Kind = FileChangeKind.Open,
                URI = TestURI2,
                Version = 1,
                Content = "const y = 2;",
                LanguageKind = Ts.LsProto.LanguageKind.TypeScript.Value!,
            },
        };

        // Go: assert.Assert(t, func() (panicked bool) { defer/recover; fs.processChanges(changes); return false }()).
        Assert.Throws<InvalidOperationException>(() => fs.ProcessChanges(changes));
    }

    // overlayfs_test.go:69-86 — TestProcessChanges/"watch create then delete becomes nothing".
    [Fact]
    public void ProcessChanges_WatchCreateThenDeleteBecomesNothing()
    {
        OverlayFS fs = CreateOverlayFS();

        var changes = new List<FileChange>
        {
            new() { Kind = FileChangeKind.WatchCreate, URI = TestURI1 },
            new() { Kind = FileChangeKind.WatchDelete, URI = TestURI1 },
        };

        (FileChangeSummary result, _) = fs.ProcessChanges(changes);
        Assert.True(result.IsEmpty());
    }

    // overlayfs_test.go:88-108 — TestProcessChanges/"watch delete then create becomes change".
    [Fact]
    public void ProcessChanges_WatchDeleteThenCreateBecomesChange()
    {
        OverlayFS fs = CreateOverlayFS();

        var changes = new List<FileChange>
        {
            new() { Kind = FileChangeKind.WatchDelete, URI = TestURI1 },
            new() { Kind = FileChangeKind.WatchCreate, URI = TestURI1 },
        };

        (FileChangeSummary result, _) = fs.ProcessChanges(changes);

        Assert.Equal(0, result.Created.Len());
        Assert.Equal(0, result.Deleted.Len());
        Assert.True(result.Changed.Has(TestURI1));
    }

    // overlayfs_test.go:110-133 — TestProcessChanges/"multiple watch changes deduplicated".
    [Fact]
    public void ProcessChanges_MultipleWatchChangesDeduplicated()
    {
        OverlayFS fs = CreateOverlayFS();

        var changes = new List<FileChange>
        {
            new() { Kind = FileChangeKind.WatchChange, URI = TestURI1 },
            new() { Kind = FileChangeKind.WatchChange, URI = TestURI1 },
            new() { Kind = FileChangeKind.WatchChange, URI = TestURI1 },
        };

        (FileChangeSummary result, _) = fs.ProcessChanges(changes);

        Assert.True(result.Changed.Has(TestURI1));
        Assert.Equal(1, result.Changed.Len());
    }

    // overlayfs_test.go:135-164 — TestProcessChanges/"save marks overlay as matching disk".
    [Fact]
    public void ProcessChanges_SaveMarksOverlayAsMatchingDisk()
    {
        OverlayFS fs = CreateOverlayFS();

        // First create an overlay
        fs.ProcessChanges(new List<FileChange>
        {
            new()
            {
                Kind = FileChangeKind.Open,
                URI = TestURI1,
                Version = 1,
                Content = "const x = 1;",
                LanguageKind = Ts.LsProto.LanguageKind.TypeScript.Value!,
            },
        });
        // Then save
        (FileChangeSummary result, _) = fs.ProcessChanges(new List<FileChange>
        {
            new() { Kind = FileChangeKind.Save, URI = TestURI1 },
        });
        // We don't observe saves for snapshot changes,
        // so they're not included in the summary
        Assert.True(result.IsEmpty());

        // Check that the overlay is marked as matching disk text
        FileHandle? fh = fs.GetFile(TestURI1.FileName());
        Assert.NotNull(fh);
        Assert.True(fh!.MatchesDiskText());
    }

    // overlayfs_test.go:166-184 — TestProcessChanges/"open falls back to file extension for unknown language kind".
    [Fact]
    public void ProcessChanges_OpenFallsBackToFileExtensionForUnknownLanguageKind()
    {
        OverlayFS fs = CreateOverlayFS();
        var uri = new Ts.LsProto.DocumentUri("file:///test1.mts");

        fs.ProcessChanges(new List<FileChange>
        {
            new()
            {
                Kind = FileChangeKind.Open,
                URI = uri,
                Version = 1,
                Content = "export const x = 1;",
                LanguageKind = "mts",
            },
        });

        FileHandle? fh = fs.GetFile(uri.FileName());
        Assert.NotNull(fh);
        Assert.Equal(Ts.Core.ScriptKind.TS, fh!.Kind());
    }

    // overlayfs_test.go:186-219 — TestProcessChanges/"watch change on overlay marks as not matching disk".
    [Fact]
    public void ProcessChanges_WatchChangeOnOverlayMarksAsNotMatchingDisk()
    {
        OverlayFS fs = CreateOverlayFS();

        // First create an overlay
        fs.ProcessChanges(new List<FileChange>
        {
            new()
            {
                Kind = FileChangeKind.Open,
                URI = TestURI1,
                Version = 1,
                Content = "const x = 1;",
                LanguageKind = Ts.LsProto.LanguageKind.TypeScript.Value!,
            },
        });
        Assert.False(fs.GetFile(TestURI1.FileName())!.MatchesDiskText());

        // Then save
        fs.ProcessChanges(new List<FileChange>
        {
            new() { Kind = FileChangeKind.Save, URI = TestURI1 },
        });
        Assert.True(fs.GetFile(TestURI1.FileName())!.MatchesDiskText());

        // Now process a watch change
        fs.ProcessChanges(new List<FileChange>
        {
            new() { Kind = FileChangeKind.WatchChange, URI = TestURI1 },
        });
        Assert.False(fs.GetFile(TestURI1.FileName())!.MatchesDiskText());
    }

    // overlayfs_test.go:221-236 — TestProcessChanges/"save without overlay should not panic".
    [Fact]
    public void ProcessChanges_SaveWithoutOverlayShouldNotPanic()
    {
        OverlayFS fs = CreateOverlayFS();

        // Save a file that was never opened (no overlay exists).
        // This can happen when an editor sends didSave for a file
        // that is not managed by the LSP server (e.g., package.json).
        (FileChangeSummary result, _) = fs.ProcessChanges(new List<FileChange>
        {
            new() { Kind = FileChangeKind.Save, URI = TestURI1 },
        });
        // Should be treated as a disk change
        Assert.True(result.Changed.Has(TestURI1));
    }

    // overlayfs_test.go:238-275 — TestProcessChanges/"close then open in same batch marks as changed".
    [Fact]
    public void ProcessChanges_CloseThenOpenInSameBatchMarksAsChanged()
    {
        OverlayFS fs = CreateOverlayFS();

        // First create an overlay
        fs.ProcessChanges(new List<FileChange>
        {
            new()
            {
                Kind = FileChangeKind.Open,
                URI = TestURI1,
                Version = 1,
                Content = "const x = 1;",
                LanguageKind = Ts.LsProto.LanguageKind.TypeScript.Value!,
            },
        });

        // Now close and reopen in the same batch (like Neovim does for file reload)
        (FileChangeSummary result, _) = fs.ProcessChanges(new List<FileChange>
        {
            new() { Kind = FileChangeKind.Close, URI = TestURI1 },
            new()
            {
                Kind = FileChangeKind.Open,
                URI = TestURI1,
                Version = 0,
                Content = "const x = 2;",
                LanguageKind = Ts.LsProto.LanguageKind.TypeScript.Value!,
            },
        });

        // Should not be marked as opened since it was already open
        Assert.True(string.IsNullOrEmpty(result.Opened.Value), "close then open should not mark as opened");
        // Should also be marked as changed since it was closed and reopened
        Assert.True(result.Changed.Has(TestURI1), "close then open should mark as changed");
        // Should have the new content
        FileHandle? fh = fs.GetFile(TestURI1.FileName());
        Assert.Equal("const x = 2;", fh!.Content());
    }
}
