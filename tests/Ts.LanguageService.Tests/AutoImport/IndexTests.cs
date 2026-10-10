// Ported from tsgo internal/ls/autoimport/index_test.go (whole file).
//
// PORT-NOTE: Go reads the unexported `entries`/`index` fields directly (same-package test);
// here they are internal, visible via InternalsVisibleTo (plan/05-conventions.md §16).
//
// PORT-NOTE: Go's Clone has a nil-receiver check (`var idx *Index[T]; idx.Clone(...)` returns
// nil). The C# port keeps Clone as an instance method and, per plan/05 §1.1/§16, expresses
// the nil receiver at the call site with the null-conditional operator, so the "handles nil
// index" case asserts that shape.
//
// PORT-NOTE: Go's `t.Run` scenario groups become one [Fact] each, named
// IndexClone_<SubName> (plan/05-conventions.md §16.1).

using System.Collections.Generic;
using Ts.Ls.AutoImport;
using Xunit;

namespace Ts.Ls.AutoImport.Tests;

// index_test.go:9-14 — testEntry.
internal sealed class TestEntry : Named
{
    private readonly string name;
    private readonly string package_;

    public TestEntry(string name, string package_)
    {
        this.name = name;
        this.package_ = package_;
    }

    public string Name() => name;

    public string Package => package_;
}

public sealed class IndexTests
{
    // index_test.go:19-50 — TestIndexClone/"filters entries by package".
    [Fact]
    public void IndexClone_FiltersEntriesByPackage()
    {
        var idx = new Index<TestEntry>();
        idx.InsertAsWords(new TestEntry("fooBar", "pkg-a"));
        idx.InsertAsWords(new TestEntry("bazQux", "pkg-b"));
        idx.InsertAsWords(new TestEntry("fooQux", "pkg-a"));

        // Clone excluding pkg-b
        Index<TestEntry> cloned = idx.Clone(e => e.Package != "pkg-b");

        // Original should have all 3 entries
        Assert.Equal(3, idx.entries.Count);

        // Cloned should have 2 entries (only pkg-a)
        Assert.Equal(2, cloned.entries.Count);

        // Search should work on cloned index
        List<TestEntry> results = cloned.Find("fooBar", true);
        Assert.Single(results);
        Assert.Equal("fooBar", results[0].Name());

        // bazQux should not be in cloned index
        results = cloned.Find("bazQux", true);
        Assert.Empty(results);

        // Word prefix search should work
        results = cloned.SearchWordPrefix("foo");
        Assert.Equal(2, results.Count);
    }

    // index_test.go:52-58 — TestIndexClone/"handles nil index".
    [Fact]
    public void IndexClone_HandlesNilIndex()
    {
        Index<TestEntry>? idx = null;
        Index<TestEntry>? cloned = idx?.Clone(e => true);
        Assert.Null(cloned);
    }

    // index_test.go:60-66 — TestIndexClone/"handles empty index".
    [Fact]
    public void IndexClone_HandlesEmptyIndex()
    {
        var idx = new Index<TestEntry>();
        Index<TestEntry> cloned = idx.Clone(e => true);
        Assert.Empty(cloned.entries);
    }

    // index_test.go:68-78 — TestIndexClone/"filters all entries".
    [Fact]
    public void IndexClone_FiltersAllEntries()
    {
        var idx = new Index<TestEntry>();
        idx.InsertAsWords(new TestEntry("fooBar", "pkg-a"));
        idx.InsertAsWords(new TestEntry("bazQux", "pkg-b"));

        Index<TestEntry> cloned = idx.Clone(e => false);
        Assert.Empty(cloned.entries);
        Assert.Empty(cloned.index!);
    }
}
