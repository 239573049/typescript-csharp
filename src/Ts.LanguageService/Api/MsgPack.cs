// Minimal self-contained MessagePack runtime for the api wire protocol.
//
// Go uses github.com/vmihailenco/msgpack/v5 indirectly through api/protocol_msgpack.go,
// but the wire subset that protocol actually emits is tiny: fixed 3-element arrays
// (0x93), positive fixint / uint8 message types (0x00-0x7F / 0xCC), and bin8/bin16/bin32
// (0xC4-0xC6) for method and payload. Per plan/07-aot-constraints.md §4.2 #5 the api
// msgpack face is a handwritten byte codec with zero third-party dependencies; this
// file is that codec.
//
// Coverage (exactly the formats the api wire protocol uses, plus the neighboring
// families needed to round-trip test payloads): nil/bool, positive fixint and the
// full int/uint families (int8-64/uint8-64), fixstr/str8/16/32, bin8/16/32,
// fixarray/array16/32, fixmap/map16/32, float32/float64. The Go api code emits NO
// ext/fixext types, so none are implemented; a foreign ext marker is a decode error.
//
// All integers are big-endian per the MessagePack spec. No reflection, no
// culture-sensitive APIs (plan/07 §1.3/§7).

using System;
using System.IO;

namespace Ts.Api;

// Writer emits MessagePack bytes to a stream. The framing code in ProtocolMsgPack.cs
// calls Flush after each message, mirroring Go's bufio.Writer.Flush in writeTuple.
public sealed class MsgPackWriter
{
    private readonly Stream w;

    public MsgPackWriter(Stream w)
    {
        this.w = w;
    }

    public void WriteByte(byte b)
    {
        w.WriteByte(b);
    }

    // WriteNil emits 0xC0.
    public void WriteNil()
    {
        WriteByte(0xC0);
    }

    // WriteBool emits 0xC2 (false) / 0xC3 (true).
    public void WriteBool(bool b)
    {
        WriteByte(b ? (byte)0xC3 : (byte)0xC2);
    }

    // WriteInt encodes a signed integer using the smallest family per the spec:
    // positive fixint (< 0x80), negative fixint (>= -0x20), then int8/16/32/64.
    public void WriteInt(long v)
    {
        if (v >= 0)
        {
            WriteUInt((ulong)v);
            return;
        }
        if (v >= -0x20)
        {
            WriteByte((byte)v);
        }
        else if (v >= sbyte.MinValue)
        {
            WriteByte(0xD0);
            WriteByte((byte)(sbyte)v);
        }
        else if (v >= short.MinValue)
        {
            WriteByte(0xD1);
            WriteUInt16((ushort)(short)v);
        }
        else if (v >= int.MinValue)
        {
            WriteByte(0xD2);
            WriteUInt32((uint)(int)v);
        }
        else
        {
            WriteByte(0xD3);
            WriteUInt64((ulong)v);
        }
    }

    // WriteUInt encodes an unsigned integer: positive fixint (< 0x80), then
    // uint8/16/32/64.
    public void WriteUInt(ulong v)
    {
        if (v < 0x80)
        {
            WriteByte((byte)v);
        }
        else if (v <= byte.MaxValue)
        {
            WriteByte(0xCC);
            WriteByte((byte)v);
        }
        else if (v <= ushort.MaxValue)
        {
            WriteByte(0xCD);
            WriteUInt16((ushort)v);
        }
        else if (v <= uint.MaxValue)
        {
            WriteByte(0xCE);
            WriteUInt32((uint)v);
        }
        else
        {
            WriteByte(0xCF);
            WriteUInt64(v);
        }
    }

    public void WriteFloat32(float v)
    {
        WriteByte(0xCA);
        WriteUInt32(BitConverter.SingleToUInt32Bits(v));
    }

    public void WriteFloat64(double v)
    {
        WriteByte(0xCB);
        WriteUInt64(BitConverter.DoubleToUInt64Bits(v));
    }

    // WriteString emits a str family header plus UTF-8 bytes.
    public void WriteString(string s)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(s);
        int length = data.Length;
        if (length < 32)
        {
            WriteByte((byte)(0xA0 | length));
        }
        else if (length < 256)
        {
            WriteByte(0xD9);
            WriteByte((byte)length);
        }
        else if (length < 1 << 16)
        {
            WriteByte(0xDA);
            WriteUInt16((ushort)length);
        }
        else
        {
            WriteByte(0xDB);
            WriteUInt32((uint)length);
        }
        Write(data);
    }

    // WriteBinHeader emits the smallest bin family header for the given length.
    public void WriteBinHeader(int length)
    {
        if (length < 256)
        {
            WriteByte(0xC4);
            WriteByte((byte)length);
        }
        else if (length < 1 << 16)
        {
            WriteByte(0xC5);
            WriteUInt16((ushort)length);
        }
        else
        {
            WriteByte(0xC6);
            WriteUInt32((uint)length);
        }
    }

    // WriteBin emits bin8/16/32 + payload (mirrors Go writeBin in protocol_msgpack.go).
    public void WriteBin(ReadOnlySpan<byte> data)
    {
        WriteBinHeader(data.Length);
        Write(data);
    }

    // WriteArrayHeader emits fixarray (< 16), array16, or array32.
    public void WriteArrayHeader(int length)
    {
        if (length < 16)
        {
            WriteByte((byte)(0x90 | length));
        }
        else if (length < 1 << 16)
        {
            WriteByte(0xDC);
            WriteUInt16((ushort)length);
        }
        else
        {
            WriteByte(0xDD);
            WriteUInt32((uint)length);
        }
    }

    // WriteMapHeader emits fixmap (< 16), map16, or map32.
    public void WriteMapHeader(int length)
    {
        if (length < 16)
        {
            WriteByte((byte)(0x80 | length));
        }
        else if (length < 1 << 16)
        {
            WriteByte(0xDE);
            WriteUInt16((ushort)length);
        }
        else
        {
            WriteByte(0xDF);
            WriteUInt32((uint)length);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        w.Write(data);
    }

    // FlushOrThrow flushes the underlying stream (Go bufio.Writer.Flush in writeTuple).
    public void FlushOrThrow()
    {
        w.Flush();
    }

    private void WriteUInt16(ushort v)
    {
        WriteByte((byte)(v >> 8));
        WriteByte((byte)v);
    }

    private void WriteUInt32(uint v)
    {
        WriteByte((byte)(v >> 24));
        WriteByte((byte)(v >> 16));
        WriteByte((byte)(v >> 8));
        WriteByte((byte)v);
    }

    private void WriteUInt64(ulong v)
    {
        WriteUInt32((uint)(v >> 32));
        WriteUInt32((uint)v);
    }
}

// Reader parses MessagePack bytes from a stream. Read* methods throw
// MsgPackException on any malformed input (Go returns non-nil errors which the
// protocol layer propagates unchanged; the exception carries the same message text).
public sealed class MsgPackReader
{
    private readonly Stream r;

    public MsgPackReader(Stream r)
    {
        this.r = r;
    }

    // ReadByte reads a single byte. Returns -1 at clean EOF (Go io.EOF).
    public int ReadByte()
    {
        return r.ReadByte();
    }

    private byte ReadByteOrThrow()
    {
        int b = r.ReadByte();
        if (b == -1)
        {
            throw MsgPackException.Eof();
        }
        return (byte)b;
    }

    public bool ReadBool()
    {
        byte t = ReadByteOrThrow();
        if (t == 0xC2)
        {
            return false;
        }
        if (t == 0xC3)
        {
            return true;
        }
        throw new MsgPackException($"msgpack: expected bool, received: 0x{t:x2}");
    }

    // ReadInt decode accepting any integer family (and float64 truncation is NOT
    // performed — a float marker is an error).
    public long ReadInt()
    {
        byte t = ReadByteOrThrow();
        if (t <= 0x7F)
        {
            return t;
        }
        if (t >= 0xE0)
        {
            return (sbyte)t;
        }
        return t switch
        {
            0xCC => ReadByteOrThrow(),
            0xCD => ReadUInt16(),
            0xCE => ReadUInt32(),
            0xCF => (long)ReadUInt64(),
            0xD0 => (sbyte)ReadByteOrThrow(),
            0xD1 => (short)ReadUInt16(),
            0xD2 => (int)ReadUInt32(),
            0xD3 => (long)ReadUInt64(),
            _ => throw new MsgPackException($"msgpack: expected integer, received: 0x{t:x2}"),
        };
    }

    public ulong ReadUInt()
    {
        byte t = ReadByteOrThrow();
        if (t <= 0x7F)
        {
            return t;
        }
        return t switch
        {
            0xCC => ReadByteOrThrow(),
            0xCD => ReadUInt16(),
            0xCE => ReadUInt32(),
            0xCF => ReadUInt64(),
            _ => throw new MsgPackException($"msgpack: expected unsigned integer, received: 0x{t:x2}"),
        };
    }

    public float ReadFloat32()
    {
        byte t = ReadByteOrThrow();
        if (t != 0xCA)
        {
            throw new MsgPackException($"msgpack: expected float32, received: 0x{t:x2}");
        }
        return BitConverter.UInt32BitsToSingle(ReadUInt32());
    }

    public double ReadFloat64()
    {
        byte t = ReadByteOrThrow();
        if (t != 0xCB)
        {
            throw new MsgPackException($"msgpack: expected float64, received: 0x{t:x2}");
        }
        return BitConverter.UInt64BitsToDouble(ReadUInt64());
    }

    public string ReadString()
    {
        byte t = ReadByteOrThrow();
        int length;
        if ((t & 0xE0) == 0xA0)
        {
            length = t & 0x1F;
        }
        else
        {
            length = t switch
            {
                0xD9 => ReadByteOrThrow(),
                0xDA => ReadUInt16(),
                0xDB => checked((int)ReadUInt32()),
                _ => throw new MsgPackException($"msgpack: expected string, received: 0x{t:x2}"),
            };
        }
        byte[] data = ReadFull(length);
        return System.Text.Encoding.UTF8.GetString(data);
    }

    // ReadBin reads a bin8/16/32 payload (mirrors Go readBin in protocol_msgpack.go).
    public byte[] ReadBin()
    {
        byte t = ReadByteOrThrow();
        int size = t switch
        {
            0xC4 => ReadByteOrThrow(),
            0xC5 => ReadUInt16(),
            0xC6 => checked((int)ReadUInt32()),
            _ => throw new MsgPackException($"msgpack: expected binary data (0xc4-0xc6), received: 0x{t:x2}"),
        };
        return ReadFull(size);
    }

    // ReadArrayHeader returns the element count for fixarray/array16/array32.
    public int ReadArrayHeader()
    {
        byte t = ReadByteOrThrow();
        if ((t & 0xF0) == 0x90)
        {
            return t & 0x0F;
        }
        return t switch
        {
            0xDC => ReadUInt16(),
            0xDD => checked((int)ReadUInt32()),
            _ => throw new MsgPackException($"msgpack: expected array, received: 0x{t:x2}"),
        };
    }

    // ReadMapHeader returns the pair count for fixmap/map16/map32.
    public int ReadMapHeader()
    {
        byte t = ReadByteOrThrow();
        if ((t & 0xF0) == 0x80)
        {
            return t & 0x0F;
        }
        return t switch
        {
            0xDE => ReadUInt16(),
            0xDF => checked((int)ReadUInt32()),
            _ => throw new MsgPackException($"msgpack: expected map, received: 0x{t:x2}"),
        };
    }

    // Skip consumes one value of any supported format (used by tests and by callers
    // that need to tolerate trailing structure).
    public void Skip()
    {
        byte t = ReadByteOrThrow();
        switch (t)
        {
            case 0xC0: // nil
            case 0xC2: // false
            case 0xC3: // true
                return;
            case >= 0x00 and <= 0x7F: // positive fixint
            case >= 0xE0: // negative fixint
                return;
            case 0xCC or 0xD0: ReadByteOrThrow(); return;
            case 0xCD or 0xD1: ReadUInt16(); return;
            case 0xCE or 0xD2 or 0xCA: ReadUInt32(); return;
            case 0xCF or 0xD3 or 0xCB: ReadUInt64(); return;
            case >= 0xA0 and <= 0xBF: ReadFull(t & 0x1F); return;
            case 0xD9: ReadFull(ReadByteOrThrow()); return;
            case 0xDA: ReadFull(ReadUInt16()); return;
            case 0xDB: ReadFull(checked((int)ReadUInt32())); return;
            case 0xC4: ReadFull(ReadByteOrThrow()); return;
            case 0xC5: ReadFull(ReadUInt16()); return;
            case 0xC6: ReadFull(checked((int)ReadUInt32())); return;
            case >= 0x90 and <= 0x9F: SkipN(t & 0x0F); return;
            case 0xDC: SkipN(ReadUInt16()); return;
            case 0xDD: SkipN(checked((int)ReadUInt32())); return;
            case >= 0x80 and <= 0x8F: SkipN(2 * (t & 0x0F)); return;
            case 0xDE: SkipN(2 * ReadUInt16()); return;
            case 0xDF: SkipN(2 * checked((int)ReadUInt32())); return;
            default:
                throw new MsgPackException($"msgpack: unsupported format: 0x{t:x2}");
        }
    }

    private void SkipN(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Skip();
        }
    }

    private ushort ReadUInt16()
    {
        ushort hi = ReadByteOrThrow();
        ushort lo = ReadByteOrThrow();
        return (ushort)((hi << 8) | lo);
    }

    private uint ReadUInt32()
    {
        uint v = ReadUInt16();
        v = (v << 8) | ReadByteOrThrow();
        v = (v << 8) | ReadByteOrThrow();
        return v;
    }

    private ulong ReadUInt64()
    {
        ulong v = ReadUInt32();
        v = (v << 32) | ReadUInt32();
        return v;
    }

    private byte[] ReadFull(int count)
    {
        if (count < 0)
        {
            throw MsgPackException.Eof();
        }
        byte[] payload = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = r.Read(payload, read, count - read);
            if (n <= 0)
            {
                throw MsgPackException.Eof();
            }
            read += n;
        }
        return payload;
    }
}

// MsgPackException mirrors the error values returned by Go's msgpack framing code;
// Eof carries Go's io.EOF text so callers can classify clean EOF (plan/05 §4).
public sealed class MsgPackException : Exception
{
    public bool IsEof { get; }

    public MsgPackException(string message) : base(message)
    {
    }

    private MsgPackException(string message, bool isEof) : base(message)
    {
        IsEof = isEof;
    }

    public static MsgPackException Eof()
    {
        return new MsgPackException("EOF", isEof: true);
    }
}
