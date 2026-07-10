using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class TristateTests
{
    [Fact]
    public void ZeroValueIsUnknown()
    {
        Tristate t = default;
        Assert.Equal(Tristate.Unknown, t);
        Assert.True(t.IsUnknown());
        Assert.True(t.IsTrueOrUnknown());
        Assert.True(t.IsFalseOrUnknown());
    }

    [Fact]
    public void PredicatesMatchGoSemantics()
    {
        Assert.True(Tristate.True.IsTrue());
        Assert.False(Tristate.True.IsFalse());
        Assert.True(Tristate.True.IsTrueOrUnknown());
        Assert.True(Tristate.False.IsFalseOrUnknown());
        Assert.False(Tristate.False.IsTrueOrUnknown());
    }

    [Fact]
    public void DefaultIfUnknownOnlyReplacesUnknown()
    {
        Assert.Equal(Tristate.True, Tristate.Unknown.DefaultIfUnknown(Tristate.True));
        Assert.Equal(Tristate.False, Tristate.False.DefaultIfUnknown(Tristate.True));
    }

    [Fact]
    public void BoolToTristateMapsBothValues()
    {
        Assert.Equal(Tristate.True, TristateExtensions.BoolToTristate(true));
        Assert.Equal(Tristate.False, TristateExtensions.BoolToTristate(false));
    }

    // The reflection-based JsonSerializer entry points are banned repo-wide
    // (07-aot-constraints.md); drive the converter directly instead.
    private static string WriteViaConverter(Tristate value)
    {
        var converter = new TristateJsonConverter();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            converter.Write(writer, value, new JsonSerializerOptions());
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Tristate ReadViaConverter(string json)
    {
        var converter = new TristateJsonConverter();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(reader.Read());
        return converter.Read(ref reader, typeof(Tristate), new JsonSerializerOptions());
    }

    [Theory]
    [InlineData(Tristate.True, "true")]
    [InlineData(Tristate.False, "false")]
    [InlineData(Tristate.Unknown, "null")]
    public void WritesGoWireFormat(Tristate value, string expected)
    {
        Assert.Equal(expected, WriteViaConverter(value));
    }

    [Theory]
    [InlineData("true", Tristate.True)]
    [InlineData("false", Tristate.False)]
    [InlineData("null", Tristate.Unknown)]
    [InlineData("\"yes\"", Tristate.Unknown)]
    [InlineData("1", Tristate.Unknown)]
    public void ReadsAnythingElseAsUnknown(string json, Tristate expected)
    {
        Assert.Equal(expected, ReadViaConverter(json));
    }
}
