// Ported from tsgo internal/sourcemap/generator_test.go

using Xunit;

namespace Ts.SourceMap.Tests;

public class GeneratorTests
{
    private static Generator NewGen()
    {
        return Generator.NewGenerator("main.js", "/", "/", new global::Ts.TsPath.ComparePathsOptions());
    }

    // gotest assert.DeepEqual(t, actual, expected) on RawSourceMap → field-by-field compare.
    private static void AssertRawSourceMap(RawSourceMap expected, RawSourceMap actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.File, actual.File);
        Assert.Equal(expected.SourceRoot, actual.SourceRoot);
        Assert.Equal<string>(expected.Sources, actual.Sources);
        Assert.Equal<string>(expected.Names, actual.Names);
        Assert.Equal(expected.Mappings, actual.Mappings);
        if (expected.SourcesContent == null)
        {
            Assert.Null(actual.SourcesContent);
        }
        else
        {
            Assert.NotNull(actual.SourcesContent);
            Assert.Equal((System.Collections.IEnumerable?)expected.SourcesContent, (System.Collections.IEnumerable?)actual.SourcesContent!);
        }
    }

    [Fact]
    public void Empty()
    {
        var gen = NewGen();
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = [],
            Mappings = "",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void Empty_Serialized()
    {
        var gen = NewGen();
        string actual = gen.String();
        const string expected = """{"version":3,"file":"main.js","sourceRoot":"/","sources":[],"names":[],"mappings":""}""";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AddSource()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        RawSourceMap sourceMap = gen.RawSourceMap();
        Assert.Equal(0, sourceIndex);
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void SetSourceContent()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        const string sourceContent = "foo";
        Assert.Null(gen.SetSourceContent(sourceIndex, sourceContent));
        RawSourceMap sourceMap = gen.RawSourceMap();
        Assert.Equal(0, sourceIndex);
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "",
            Names = [],
            SourcesContent = [sourceContent],
        }, sourceMap);
    }

    [Fact]
    public void SetSourceContent_ForSecondSourceOnly()
    {
        var gen = NewGen();
        gen.AddSource("/skipped.ts");
        int sourceIndex = gen.AddSource("/main.ts");
        const string sourceContent = "foo";
        Assert.Null(gen.SetSourceContent(sourceIndex, sourceContent));
        RawSourceMap sourceMap = gen.RawSourceMap();
        Assert.Equal(1, sourceIndex);
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["skipped.ts", "main.ts"],
            Mappings = "",
            Names = [],
            SourcesContent = [null, sourceContent],
        }, sourceMap);
    }

    [Fact]
    public void SetSourceContent_SourceIndexOutOfRange()
    {
        var gen = NewGen();
        Assert.Equal("sourceIndex is out of range", gen.SetSourceContent(-1, ""));
        Assert.Equal("sourceIndex is out of range", gen.SetSourceContent(0, ""));
    }

    [Fact]
    public void SetSourceContent_ForSecondSourceOnly_Serialized()
    {
        var gen = NewGen();
        gen.AddSource("/skipped.ts");
        int sourceIndex = gen.AddSource("/main.ts");
        const string sourceContent = "foo";
        Assert.Null(gen.SetSourceContent(sourceIndex, sourceContent));
        string actual = gen.String();
        const string expected = """{"version":3,"file":"main.js","sourceRoot":"/","sources":["skipped.ts","main.ts"],"names":[],"mappings":"","sourcesContent":[null,"foo"]}""";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AddName()
    {
        var gen = NewGen();
        int nameIndex = gen.AddName("foo");
        RawSourceMap sourceMap = gen.RawSourceMap();
        Assert.Equal(0, nameIndex);
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = [],
            Mappings = "",
            Names = ["foo"],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddGeneratedMapping()
    {
        var gen = NewGen();
        Assert.Null(gen.AddGeneratedMapping(0, 0));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = [],
            Mappings = "A",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddGeneratedMapping_OnSecondLineOnly()
    {
        var gen = NewGen();
        Assert.Null(gen.AddGeneratedMapping(1, 0));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = [],
            Mappings = ";A",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddSourceMapping()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(0, 0, sourceIndex, 0, 0));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAA",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddSourceMapping_NextGeneratedCharacter()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(0, 0, sourceIndex, 0, 0));
        Assert.Null(gen.AddSourceMapping(0, 1, sourceIndex, 0, 0));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAA,CAAA",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddSourceMapping_NextGeneratedAndSourceCharacter()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(0, 0, sourceIndex, 0, 0));
        Assert.Null(gen.AddSourceMapping(0, 1, sourceIndex, 0, 1));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAA,CAAC",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddSourceMapping_NextGeneratedLine()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(0, 0, sourceIndex, 0, 0));
        Assert.Null(gen.AddSourceMapping(1, 0, sourceIndex, 0, 0));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAA;AAAA",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddSourceMapping_PreviousSourceCharacter()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(0, 0, sourceIndex, 0, 1));
        Assert.Null(gen.AddSourceMapping(0, 1, sourceIndex, 0, 0));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAC,CAAD",
            Names = [],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddNamedSourceMapping()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        int nameIndex = gen.AddName("foo");
        Assert.Null(gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, 0, nameIndex));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAAA",
            Names = ["foo"],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddNamedSourceMapping_WithPreviousName()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        int nameIndex1 = gen.AddName("foo");
        int nameIndex2 = gen.AddName("bar");
        Assert.Null(gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, 0, nameIndex2));
        Assert.Null(gen.AddNamedSourceMapping(0, 1, sourceIndex, 0, 0, nameIndex1));
        RawSourceMap sourceMap = gen.RawSourceMap();
        AssertRawSourceMap(new RawSourceMap
        {
            Version = 3,
            File = "main.js",
            SourceRoot = "/",
            Sources = ["main.ts"],
            Mappings = "AAAAC,CAAAD",
            Names = ["foo", "bar"],
            SourcesContent = null,
        }, sourceMap);
    }

    [Fact]
    public void AddGeneratedMapping_GeneratedLineCannotBacktrack()
    {
        var gen = NewGen();
        Assert.Null(gen.AddGeneratedMapping(1, 0));
        Assert.Equal("generatedLine cannot backtrack", gen.AddGeneratedMapping(0, 0));
    }

    [Fact]
    public void AddGeneratedMapping_GeneratedCharacterCannotBeNegative()
    {
        var gen = NewGen();
        Assert.Null(gen.AddGeneratedMapping(0, 0));
        Assert.Equal("generatedCharacter cannot be negative", gen.AddGeneratedMapping(0, -1));
    }

    [Fact]
    public void AddSourceMapping_GeneratedLineCannotBacktrack()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(1, 0, sourceIndex, 0, 0));
        Assert.Equal("generatedLine cannot backtrack", gen.AddSourceMapping(0, 0, sourceIndex, 0, 0));
    }

    [Fact]
    public void AddSourceMapping_GeneratedCharacterCannotBeNegative()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Null(gen.AddSourceMapping(0, 0, sourceIndex, 0, 0));
        Assert.Equal("generatedCharacter cannot be negative", gen.AddSourceMapping(0, -1, sourceIndex, 0, 0));
    }

    [Fact]
    public void AddSourceMapping_SourceIndexIsOutOfRange()
    {
        var gen = NewGen();
        Assert.Equal("sourceIndex is out of range", gen.AddSourceMapping(0, 0, -1, 0, 0));
        Assert.Equal("sourceIndex is out of range", gen.AddSourceMapping(0, 0, 0, 0, 0));
    }

    [Fact]
    public void AddSourceMapping_SourceLineCannotBeNegative()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Equal("sourceLine cannot be negative", gen.AddSourceMapping(0, 0, sourceIndex, -1, 0));
    }

    [Fact]
    public void AddSourceMapping_SourceCharacterCannotBeNegative()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Equal("sourceCharacter cannot be negative", gen.AddSourceMapping(0, 0, sourceIndex, 0, -1));
    }

    [Fact]
    public void AddNamedSourceMapping_GeneratedLineCannotBacktrack()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        int nameIndex = gen.AddName("foo");
        Assert.Null(gen.AddNamedSourceMapping(1, 0, sourceIndex, 0, 0, nameIndex));
        Assert.Equal("generatedLine cannot backtrack", gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, 0, nameIndex));
    }

    [Fact]
    public void AddNamedSourceMapping_GeneratedCharacterCannotBeNegative()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        int nameIndex = gen.AddName("foo");
        Assert.Null(gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, 0, nameIndex));
        Assert.Equal("generatedCharacter cannot be negative", gen.AddNamedSourceMapping(0, -1, sourceIndex, 0, 0, nameIndex));
    }

    [Fact]
    public void AddNamedSourceMapping_SourceIndexIsOutOfRange()
    {
        var gen = NewGen();
        int nameIndex = gen.AddName("foo");
        Assert.Equal("sourceIndex is out of range", gen.AddNamedSourceMapping(0, 0, -1, 0, 0, nameIndex));
        Assert.Equal("sourceIndex is out of range", gen.AddNamedSourceMapping(0, 0, 0, 0, 0, nameIndex));
    }

    [Fact]
    public void AddNamedSourceMapping_SourceLineCannotBeNegative()
    {
        var gen = NewGen();
        int nameIndex = gen.AddName("foo");
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Equal("sourceLine cannot be negative", gen.AddNamedSourceMapping(0, 0, sourceIndex, -1, 0, nameIndex));
    }

    [Fact]
    public void AddNamedSourceMapping_SourceCharacterCannotBeNegative()
    {
        var gen = NewGen();
        int nameIndex = gen.AddName("foo");
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Equal("sourceCharacter cannot be negative", gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, -1, nameIndex));
    }

    [Fact]
    public void AddNamedSourceMapping_NameIndexIsOutOfRange()
    {
        var gen = NewGen();
        int sourceIndex = gen.AddSource("/main.ts");
        Assert.Equal("nameIndex is out of range", gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, 0, -1));
        Assert.Equal("nameIndex is out of range", gen.AddNamedSourceMapping(0, 0, sourceIndex, 0, 0, 0));
    }
}
