// Tests for the FourslashParser port (tsgo internal/fourslash/test_parser.go): the
// `// @Filename:` multi-file split, `/*N*/` markers, `[|…|]` ranges, `{| … |}` object markers,
// symlink/global-option directives, and the Go failure messages. The parse output is the raw
// UTF-16 currency (positions are C# string offsets — see FourslashModel.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.TestUtil.Fourslash;
using Xunit;

namespace Ts.Fourslash.Tests.Parser;

public sealed class FourslashParserTests
{
    [Fact]
    public void ParsesSingleImplicitFileWithMarkers()
    {
        var project = FourslashParser.ParseTestData(
            """
            const value = /*a*/1;
            """,
            "test.ts");

        var file = Assert.Single(project.Files);
        Assert.Equal("/test.ts", file.VirtualFileName);
        Assert.Equal("const value = 1;", file.Content);

        var marker = Assert.Single(project.Markers);
        Assert.Equal(14, marker.Position);
        Assert.Equal(new[] { "a" }, project.MarkerPositions.Keys.ToArray());
        Assert.Equal("/test.ts", project.MarkerPositions["a"].FileName);
        Assert.Equal(14, project.MarkerPositions["a"].Position);
    }

    [Fact]
    public void ParsesMultiFileTestWithRangeAndOptions()
    {
        var project = FourslashParser.ParseTestData(
            """
            // @strict: true
            // @Filename: /a.ts
            [|/*r*/export const foo = 1;|]

            // @Filename: /b.ts
            import { foo } from "./a";
            foo/*use*/;
            """,
            "test.ts");

        Assert.Equal(2, project.Files.Count);
        Assert.Equal("/a.ts", project.Files[0].VirtualFileName);
        Assert.Equal("/b.ts", project.Files[1].VirtualFileName);
        Assert.Equal("export const foo = 1;\n", project.Files[0].Content);

        Assert.Equal(2, project.Markers.Count);
        Assert.NotNull(project.MarkerPositions.GetValueOrDefault("r"));
        Assert.NotNull(project.MarkerPositions.GetValueOrDefault("use"));

        // Range: the stripped span in /a.ts plus the name of the marker it captured.
        var range = Assert.Single(project.Ranges);
        Assert.Equal("/a.ts", range.FileName);
        Assert.Equal("r", range.Name);
        Assert.Equal(0, range.Start);
        Assert.Equal("export const foo = 1;".Length, range.End);

        Assert.True(project.GlobalOptions.ContainsKey("strict"));
    }

    [Fact]
    public void ParsesObjectMarkerInsideRange()
    {
        var project = FourslashParser.ParseTestData(
            """
            const x = [|{| "name": "obj", "insert": true |}x|];
            """,
            "test.ts");

        var range = Assert.Single(project.Ranges);
        Assert.Equal("obj", range.Name);

        var marker = Assert.Single(project.Markers);
        Assert.Equal("obj", marker.Name);
        Assert.NotNull(marker.Data);
        Assert.True(marker.Data!.ContainsKey("insert"));
    }

    [Fact]
    public void HandlesEmojiAsSingleUtf16CodeUnit()
    {
        var project = FourslashParser.ParseTestData(
            """
            const emoji = "😀";
            const value = /*a*/1;
            value + value;
            """,
            "test.ts");

        var file = Assert.Single(project.Files);
        // 😀 is one UTF-16 code unit beyond the quote; positions count code units.
        Assert.Equal("const emoji = \"😀\";\nconst value = 1;\nvalue + value;", file.Content);
        Assert.Equal("const value = ".Length + "const emoji = \"😀\";\n".Length, project.MarkerPositions["a"].Position);
    }

    [Fact]
    public void RejectsUnterminatedRangeWithGoMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FourslashParser.ParseTestData("[|abcd", "test.ts"));
        Assert.Contains("Unterminated range.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsRangeEndWithNoMatchingStart()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FourslashParser.ParseTestData("abcd|]", "test.ts"));
        Assert.Contains("Found range end with no matching start.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateMarkerNames()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FourslashParser.ParseTestData("/*a*/\n/*a*/", "test.ts"));
        Assert.Contains("Duplicate marker name:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsGlobalOptionsAlongsideConfigFile()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FourslashParser.ParseTestData(
                """
                // @module: commonjs
                // @Filename: /tsconfig.json
                {}
                """,
                "test.ts"));
        Assert.Contains(
            "It is not allowed to use global options along with config files.",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChompsLeadingSpaceFromEveryLine()
    {
        var project = FourslashParser.ParseTestData(
            """
                const chomped = true;
                """,
            "test.ts");

        var file = Assert.Single(project.Files);
        Assert.Equal("const chomped = true;", file.Content);
    }

    [Fact]
    public void GetMarkerResolvesTheSingleAnonymousMarker()
    {
        var project = FourslashParser.ParseTestData(
            """
            const x = {| "insert": true |}x;
            """,
            "test.ts");

        // The empty name resolves the single anonymous (object) marker.
        var anonymous = project.GetMarker("");
        Assert.NotEqual(-1, anonymous.Position);
        Assert.Null(project.Markers.Single(m => m.Data != null).Name);
    }
}
