using System;
using Ts.TestUtil.Fourslash;
using Xunit;

namespace Ts.Fourslash.Tests.Parser;

public sealed class FourslashParserTests
{
    [Fact]
    public void ParsesDirectivesFilesMarkersObjectMarkersAndNamedRanges()
    {
        const string source =
            "// @strict: true\n" +
            "// @Filename: alpha.ts\n" +
            "////const emoji = \"😀\";\n" +
            "////const /*decl*/value = 1;\n" +
            "////[|selection: value/**/ + {| \"name\": \"object\", \"kind\": \"write\", \"count\": 2 |}value|];\n" +
            "// @Filename: folder/beta.ts\n" +
            "////const /*use*/copy = value;\n";

        FourslashProject project = FourslashParser.Parse(source, "default.ts");

        Assert.Equal("true", project.Options["strict"]);
        Assert.Equal(2, project.Files.Count);
        FourslashFile alpha = project.Files[0];
        FourslashFile beta = project.Files[1];
        Assert.Equal("/alpha.ts", alpha.VirtualFileName);
        Assert.Equal("/folder/beta.ts", beta.VirtualFileName);
        Assert.Equal(
            "const emoji = \"😀\";\nconst value = 1;\nvalue + value;",
            alpha.Content);
        Assert.Equal("const copy = value;", beta.Content);

        Assert.Equal(
            alpha.Content.IndexOf("value", StringComparison.Ordinal),
            project.MarkerPositions["decl"].Position);
        Assert.Equal(
            beta.Content.IndexOf("copy", StringComparison.Ordinal),
            project.MarkerPositions["use"].Position);

        FourslashMarker anonymous = Assert.Single(project.Markers, marker => marker.Name is null);
        Assert.Equal(alpha.Content.IndexOf("value +", StringComparison.Ordinal) + "value".Length, anonymous.Position);

        FourslashMarker objectMarker = Assert.Single(
            project.Markers,
            marker => string.Equals(marker.Name, "object", StringComparison.Ordinal));
        Assert.NotNull(objectMarker.Data);
        Assert.Equal("write", objectMarker.Data["kind"].GetString());
        Assert.Equal(2, objectMarker.Data["count"].GetInt32());
        Assert.Equal(alpha.Content.IndexOf("value;", StringComparison.Ordinal), objectMarker.Position);

        FourslashRange selection = project.GetRange("selection");
        int selectedStart = alpha.Content.IndexOf("value + value", StringComparison.Ordinal);
        Assert.Equal(0, selection.Index);
        Assert.Equal("/alpha.ts", selection.FileName);
        Assert.Equal(selectedStart, selection.Start);
        Assert.Equal(selectedStart + "value + value".Length, selection.End);
    }

    [Fact]
    public void UsesUtf16CodeUnitOffsetsAndResolvesAnonymousMarker()
    {
        const string source =
            "////const s = \"😀\";\n" +
            "////const /**/value = 1;\n";

        FourslashProject project = FourslashParser.Parse(source, "utf16.ts");
        FourslashFile file = Assert.Single(project.Files);
        FourslashMarkerPosition marker = project.GetMarker("");

        Assert.Equal("/utf16.ts", marker.FileName);
        Assert.Equal(file.Content.IndexOf("value", StringComparison.Ordinal), marker.Position);
        Assert.Equal("const s = \"😀\";\nconst ".Length, marker.Position);
    }

    [Fact]
    public void NamesNestedRangesByExplicitPrefixOrContainedMarker()
    {
        const string source = "////[|outer: a[|/*inner*/bc|]d|]";

        FourslashProject project = FourslashParser.Parse(source, "ranges.ts");

        Assert.Equal("abcd", Assert.Single(project.Files).Content);
        Assert.Equal(2, project.Ranges.Count);
        Assert.Equal(new FourslashRange(0, "outer", "/ranges.ts", 0, 4), project.GetRange(0));
        Assert.Equal(new FourslashRange(1, "inner", "/ranges.ts", 1, 3), project.GetRange("inner"));
        Assert.Equal(1, project.MarkerPositions["inner"].Position);
    }

    [Fact]
    public void PreservesOrdinaryBlockCommentsWhileStrippingValidMarkers()
    {
        const string source = "////const x = /* ordinary comment */ 1; /*marker*/x;";

        FourslashProject project = FourslashParser.Parse(source, "comments.ts");
        FourslashFile file = Assert.Single(project.Files);

        Assert.Equal("const x = /* ordinary comment */ 1; x;", file.Content);
        Assert.Equal(file.Content.LastIndexOf('x'), project.MarkerPositions["marker"].Position);
    }

    [Fact]
    public void ReportsUnterminatedRange()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            FourslashParser.Parse("////const [|x = 1;", "broken.ts"));

        Assert.Contains("Unterminated range.", error.Message, StringComparison.Ordinal);
    }
}
