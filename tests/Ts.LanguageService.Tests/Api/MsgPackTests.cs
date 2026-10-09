// Tests for the self-contained MessagePack runtime (MsgPack.cs): wire-exact byte frames
// for every fixint/str/bin/array/map family and decode boundaries, plus EOF handling.
// The expected byte sequences follow the MessagePack spec the Go api wire uses
// (internal/api/protocol_msgpack.go + vmihailenco/msgpack semantics).

using System;
using System.Collections.Generic;
using System.IO;
using Ts.Api;
using Xunit;

namespace Ts.Api.Tests;

public class MsgPackTests
{
    private static byte[] Encode(Action<MsgPackWriter> write)
    {
        using var ms = new MemoryStream();
        var w = new MsgPackWriter(ms);
        write(w);
        w.FlushOrThrow();
        return ms.ToArray();
    }

    private static T Decode<T>(byte[] data, Func<MsgPackReader, T> read)
    {
        using var ms = new MemoryStream(data);
        var r = new MsgPackReader(ms);
        return read(r);
    }

    // ── nil / bool ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nil_EncodesAsC0()
    {
        byte[] data = Encode(w => w.WriteNil());
        Assert.Equal([0xC0], data);
    }

    [Theory]
    [InlineData(true, new byte[] { 0xC3 })]
    [InlineData(false, new byte[] { 0xC2 })]
    public void Bool_UsesC2C3(bool value, byte[] expected)
    {
        byte[] data = Encode(w => w.WriteBool(value));
        Assert.Equal(expected, data);
        Assert.Equal(value, Decode(data, r => r.ReadBool()));
    }

    // ── integers ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, new byte[] { 0x00 })]
    [InlineData(1L, new byte[] { 0x01 })]
    [InlineData(127L, new byte[] { 0x7F })]
    [InlineData(-1L, new byte[] { 0xFF })]
    [InlineData(-32L, new byte[] { 0xE0 })]
    [InlineData(128L, new byte[] { 0xCC, 0x80 })]
    [InlineData(255L, new byte[] { 0xCC, 0xFF })]
    [InlineData(-33L, new byte[] { 0xD0, 0xDF })]
    [InlineData(256L, new byte[] { 0xCD, 0x01, 0x00 })]
    [InlineData(65535L, new byte[] { 0xCD, 0xFF, 0xFF })]
    [InlineData(-256L, new byte[] { 0xD1, 0xFF, 0x00 })]
    [InlineData(65536L, new byte[] { 0xCE, 0x00, 0x01, 0x00, 0x00 })]
    [InlineData(-65536L, new byte[] { 0xD2, 0xFF, 0xFF, 0x00, 0x00 })]
    public void Int_PicksMinimalFamily(long value, byte[] expected)
    {
        byte[] data = Encode(w => w.WriteInt(value));
        Assert.Equal(expected, data);
        Assert.Equal(value, Decode(data, r => r.ReadInt()));
    }

    [Fact]
    public void Int64Extremes_RoundTrip()
    {
        foreach (long v in new[] { long.MaxValue, long.MinValue })
        {
            byte[] data = Encode(w => w.WriteInt(v));
            Assert.Equal(v, Decode(data, r => r.ReadInt()));
        }
        byte[] max = Encode(w => w.WriteInt(long.MaxValue));
        Assert.Equal(new byte[] { 0xCF, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, max);
        byte[] min = Encode(w => w.WriteInt(long.MinValue));
        Assert.Equal(new byte[] { 0xD3, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, min);
    }

    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(127UL, new byte[] { 0x7F })]
    [InlineData(128UL, new byte[] { 0xCC, 0x80 })]
    [InlineData(255UL, new byte[] { 0xCC, 0xFF })]
    [InlineData(256UL, new byte[] { 0xCD, 0x01, 0x00 })]
    [InlineData(65535UL, new byte[] { 0xCD, 0xFF, 0xFF })]
    [InlineData(65536UL, new byte[] { 0xCE, 0x00, 0x01, 0x00, 0x00 })]
    public void UInt_PicksMinimalFamily(ulong value, byte[] expected)
    {
        byte[] data = Encode(w => w.WriteUInt(value));
        Assert.Equal(expected, data);
        Assert.Equal(value, Decode(data, r => r.ReadUInt()));
    }

    // ── floats ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Float64_IsCBPlusBigEndian()
    {
        byte[] data = Encode(w => w.WriteFloat64(1.5));
        Assert.Equal(new byte[] { 0xCB, 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, data);
        Assert.Equal(1.5, Decode(data, r => r.ReadFloat64()));
    }

    [Fact]
    public void Float32_IsCAPlusBigEndian()
    {
        byte[] data = Encode(w => w.WriteFloat32(1.0f));
        Assert.Equal(new byte[] { 0xCA, 0x3F, 0x80, 0x00, 0x00 }, data);
        Assert.Equal(1.0f, Decode(data, r => r.ReadFloat32()));
    }

    // ── strings ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void String_ShortUsesFixstr()
    {
        byte[] data = Encode(w => w.WriteString("a"));
        Assert.Equal(new byte[] { 0xA1, (byte)'a' }, data);
        Assert.Equal("a", Decode(data, r => r.ReadString()));
    }

    [Fact]
    public void String_At31UsesFixstr_At32UsesStr8()
    {
        string s31 = new('x', 31);
        byte[] data31 = Encode(w => w.WriteString(s31));
        Assert.Equal(0xA0 | 31, data31[0]);
        Assert.Equal(s31, Decode(data31, r => r.ReadString()));

        string s32 = new('x', 32);
        byte[] data32 = Encode(w => w.WriteString(s32));
        Assert.Equal(new byte[] { 0xD9, 32 }, data32[..2]);
        Assert.Equal(s32, Decode(data32, r => r.ReadString()));
    }

    [Fact]
    public void String_At256UsesStr16()
    {
        string s256 = new('y', 256);
        byte[] data = Encode(w => w.WriteString(s256));
        Assert.Equal(new byte[] { 0xDA, 0x01, 0x00 }, data[..3]);
        Assert.Equal(s256, Decode(data, r => r.ReadString()));
    }

    // ── binary ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bin_BoundariesRoundTrip()
    {
        foreach (int len in new[] { 0, 1, 255, 256, 65535, 65536 })
        {
            byte[] payload = new byte[len];
            for (int i = 0; i < len; i++)
            {
                payload[i] = (byte)(i & 0xFF);
            }
            byte[] data = Encode(w => w.WriteBin(payload));
            byte[] expectedHeader = len switch
            {
                <= 255 => [0xC4, (byte)len],
                <= 65535 => [0xC5, (byte)(len >> 8), (byte)len],
                _ => [0xC6, (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len],
            };
            Assert.Equal(expectedHeader, data[..expectedHeader.Length]);
            Assert.Equal(payload, Decode(data, r => r.ReadBin()));
        }
    }

    // ── arrays / maps ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArrayHeader_SmallUsesFixarray_16UsesArray16()
    {
        byte[] small = Encode(w => w.WriteArrayHeader(3));
        Assert.Equal([0x93], small);
        Assert.Equal(3, Decode(small, r => r.ReadArrayHeader()));

        byte[] mid = Encode(w => w.WriteArrayHeader(16));
        Assert.Equal(new byte[] { 0xDC, 0x00, 0x10 }, mid);
        Assert.Equal(16, Decode(mid, r => r.ReadArrayHeader()));
    }

    [Fact]
    public void MapHeader_SmallUsesFixmap_16UsesMap16()
    {
        byte[] small = Encode(w => w.WriteMapHeader(1));
        Assert.Equal([0x81], small);
        Assert.Equal(1, Decode(small, r => r.ReadMapHeader()));

        byte[] mid = Encode(w => w.WriteMapHeader(16));
        Assert.Equal(new byte[] { 0xDE, 0x00, 0x10 }, mid);
        Assert.Equal(16, Decode(mid, r => r.ReadMapHeader()));
    }

    // ── EOF ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadByte_OnEmptyStream_ReportsEof()
    {
        using var ms = new MemoryStream([]);
        var r = new MsgPackReader(ms);
        Assert.Equal(-1, r.ReadByte());

        MsgPackException e = Assert.Throws<MsgPackException>(() => r.ReadBin());
        Assert.True(e.IsEof);
    }
}
