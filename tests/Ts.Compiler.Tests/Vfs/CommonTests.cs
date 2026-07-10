// Tests for Ts.Vfs.Internal.Common. No Go counterpart file exists (tsgo's vfs/internal
// has no _test.go); the DecodeBytes table below is mandated by the phase-1 vfs work
// item to pin the BOM sniffing + UTF-16 decoding semantics of internal.go:170-194.

using System;
using Ts.Vfs.Internal;
using Xunit;

namespace Ts.Vfs.Tests;

public class CommonTests
{
    public static TheoryData<string, byte[], string> DecodeBytesData()
    {
        return new TheoryData<string, byte[], string>
        {
            // No BOM: bytes are taken as (UTF-8) text unchanged.
            { "empty", Array.Empty<byte>(), "" },
            { "no BOM ascii", "hello"u8.ToArray(), "hello" },
            { "no BOM two bytes", "hi"u8.ToArray(), "hi" },
            { "no BOM multibyte utf-8", "héllo wörld — ✓"u8.ToArray(), "héllo wörld — ✓" },
            { "no BOM astral utf-8", "a\U0001F600b"u8.ToArray(), "a\U0001F600b" },

            // UTF-8 BOM: stripped once, remainder taken as-is.
            { "utf-8 BOM", new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 }, "hi" },
            { "utf-8 BOM only", new byte[] { 0xEF, 0xBB, 0xBF }, "" },
            { "utf-8 double BOM keeps second", new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }, "\uFEFF" },

            // UTF-16 LE (BOM FF FE).
            { "utf-16 LE", new byte[] { 0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00 }, "hi" },
            { "utf-16 LE BOM only", new byte[] { 0xFF, 0xFE }, "" },
            { "utf-16 LE astral pair", new byte[] { 0xFF, 0xFE, 0x3D, 0xD8, 0x00, 0xDE }, "\U0001F600" },
            { "utf-16 LE lone high surrogate", new byte[] { 0xFF, 0xFE, 0x3D, 0xD8 }, "\uFFFD" },
            { "utf-16 LE lone low surrogate", new byte[] { 0xFF, 0xFE, 0x00, 0xDE }, "\uFFFD" },
            { "utf-16 LE high surrogate then bmp", new byte[] { 0xFF, 0xFE, 0x3D, 0xD8, 0x41, 0x00 }, "\uFFFDA" },
            // Go: ints := make([]uint16, len(s)/2) — a trailing odd byte is dropped.
            { "utf-16 LE odd trailing byte dropped", new byte[] { 0xFF, 0xFE, 0x68, 0x00, 0x69 }, "h" },
            // A second FF FE after the BOM decodes as content (U+FEFF).
            { "utf-16 LE inner BOM char", new byte[] { 0xFF, 0xFE, 0xFF, 0xFE }, "\uFEFF" },

            // UTF-16 BE (BOM FE FF).
            { "utf-16 BE", new byte[] { 0xFE, 0xFF, 0x00, 0x68, 0x00, 0x69 }, "hi" },
            { "utf-16 BE BOM only", new byte[] { 0xFE, 0xFF }, "" },
            { "utf-16 BE astral pair", new byte[] { 0xFE, 0xFF, 0xD8, 0x3D, 0xDE, 0x00 }, "\U0001F600" },
            { "utf-16 BE lone high surrogate", new byte[] { 0xFE, 0xFF, 0xD8, 0x3D }, "\uFFFD" },
            { "utf-16 BE noncharacter U+FFFE passes through", new byte[] { 0xFE, 0xFF, 0xFF, 0xFE }, "\uFFFE" },
        };
    }

    [Theory]
    [MemberData(nameof(DecodeBytesData))]
    public void DecodeBytes(string name, byte[] input, string expected)
    {
        _ = name; // display-only
        Assert.Equal(expected, Common.DecodeBytes(input));
    }

    [Fact]
    public void RootLength_Posix()
    {
        Assert.Equal(1, Common.RootLength("/foo/bar"));
    }

    [Fact]
    public void RootLength_Dos()
    {
        Assert.Equal(3, Common.RootLength("c:/foo/bar"));
    }

    [Fact]
    public void RootLength_Url()
    {
        // Encoded (negative) URL root lengths are decoded (internal.go:20-28).
        Assert.Equal(8, Common.RootLength("file:///foo"));
    }

    [Fact]
    public void RootLength_RelativePanics()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Common.RootLength("bar"));
        Assert.Equal("vfs: path \"bar\" is not absolute", ex.Message);
    }

    [Fact]
    public void SplitPath_Posix()
    {
        Assert.Equal(("/", "foo/bar"), Common.SplitPath("/foo/bar"));
    }

    [Fact]
    public void SplitPath_Dos()
    {
        Assert.Equal(("c:/", "foo/bar"), Common.SplitPath("c:/foo/bar"));
    }

    [Fact]
    public void SplitPath_TrailingSeparatorRemoved()
    {
        Assert.Equal(("/", "foo"), Common.SplitPath("/foo/"));
    }

    [Fact]
    public void SplitPath_NormalizesDotSegments()
    {
        Assert.Equal(("/", "dir1"), Common.SplitPath("/dir1/./"));
    }
}
