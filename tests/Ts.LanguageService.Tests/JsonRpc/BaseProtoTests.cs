// Tests for the jsonrpc Content-Length framing (ported from tsgo internal/jsonrpc/baseproto.go).
// The behaviour-level tests of internal/lsp/lsproto/baseproto_test.go live here too: the
// lsproto BaseReader/BaseWriter are thin wrappers over jsonrpc.Reader/Writer
// (lsproto/baseproto.go), and the C# port exposes the implementation only under Ts.JsonRpc.
//
// baseproto_test.go PORT-NOTEs:
//  * Go asserts exact error text; the parse-error case embeds strconv's wording, which the
//    C# port does not reproduce (BaseProto.cs:114-118), so errText is matched by prefix.
//  * The invalid-header `%q` rendering embeds backslash escapes in Go; QuoteBytes here embeds
//    the raw line, so the assertion checks the stable part (the bare line).
//
// Verifies byte-exact frame read/write round-trips (Content-Length header + JSON body).

using System;
using System.IO;
using System.Text;
using Ts.JsonRpc;
using Xunit;

namespace Ts.JsonRpc.Tests;

public class BaseProtoTests
{
    [Fact]
    public void WriteThenRead_RoundTripsPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"foo\"}");

        using var stream = new MemoryStream();
        var writer = new Writer(stream);
        writer.Write(payload);

        stream.Position = 0;
        var reader = new Reader(stream);
        (byte[]? data, bool ok) = reader.Read();

        Assert.True(ok);
        Assert.NotNull(data);
        Assert.Equal(payload, data);
    }

    [Fact]
    public void Write_EmitsExactContentLengthHeaderBytes()
    {
        byte[] payload = Encoding.UTF8.GetBytes("abc");

        using var stream = new MemoryStream();
        new Writer(stream).Write(payload);

        // Header must be exactly "Content-Length: 3\r\n\r\n" followed by the body.
        byte[] expected = Encoding.ASCII.GetBytes("Content-Length: 3\r\n\r\nabc");
        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void Read_AtCleanEof_ReturnsNotOk()
    {
        using var stream = new MemoryStream(); // empty
        var reader = new Reader(stream);
        (byte[]? data, bool ok) = reader.Read();

        Assert.False(ok);
        Assert.Null(data);
    }

    [Fact]
    public void Read_MultipleFramesInSequence()
    {
        byte[] first = Encoding.UTF8.GetBytes("one");
        byte[] second = Encoding.UTF8.GetBytes("second-frame");

        using var stream = new MemoryStream();
        var writer = new Writer(stream);
        writer.Write(first);
        writer.Write(second);

        stream.Position = 0;
        var reader = new Reader(stream);

        (byte[]? d1, bool ok1) = reader.Read();
        (byte[]? d2, bool ok2) = reader.Read();
        (byte[]? d3, bool ok3) = reader.Read();

        Assert.True(ok1);
        Assert.Equal(first, d1);
        Assert.True(ok2);
        Assert.Equal(second, d2);
        Assert.False(ok3); // EOF after the two frames
    }

    [Fact]
    public void Read_InvalidHeaderLine_Throws()
    {
        // A header line with no colon is invalid.
        byte[] bytes = Encoding.ASCII.GetBytes("NotAHeaderLine\r\n\r\n");
        using var stream = new MemoryStream(bytes);
        var reader = new Reader(stream);

        var ex = Assert.Throws<JsonRpcFramingException>(() => reader.Read());
        Assert.Equal(BaseProtoErrors.ErrInvalidHeader, ex.Sentinel);
    }

    [Fact]
    public void Read_MissingContentLength_Throws()
    {
        // Header block with no Content-Length terminates at CRLF; contentLength stays 0.
        byte[] bytes = Encoding.ASCII.GetBytes("X-Other: 1\r\n\r\n");
        using var stream = new MemoryStream(bytes);
        var reader = new Reader(stream);

        var ex = Assert.Throws<JsonRpcFramingException>(() => reader.Read());
        Assert.Equal(BaseProtoErrors.ErrNoContentLength, ex.Sentinel);
    }

    // baseproto_test.go:108-137 — TestBaseWriter.
    public static TheoryData<string, byte[], byte[]> BaseWriterData()
    {
        return new TheoryData<string, byte[], byte[]>
        {
            { "empty", Encoding.ASCII.GetBytes("{}"), Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{}") },
            {
                "bigger object",
                Encoding.ASCII.GetBytes("{\"key\":\"value\"}"),
                Encoding.ASCII.GetBytes("Content-Length: 15\r\n\r\n{\"key\":\"value\"}")
            },
        };
    }

    [Theory]
    [MemberData(nameof(BaseWriterData))]
    public void Write_FramesValue(string name, byte[] value, byte[] expected)
    {
        _ = name; // Go's t.Run name.
        using var stream = new MemoryStream();
        var writer = new Writer(stream);
        writer.Write(value);
        Assert.Equal(expected, stream.ToArray());
    }

    // baseproto_test.go:139-151 — TestBaseWriterWriteError. Go's errorWriter returns
    // ("test error") on every write; the C# Stream surface reports it by throwing.
    [Fact]
    public void Write_PropagatesStreamError()
    {
        using var stream = new ErrorStream();
        var writer = new Writer(stream);
        var ex = Assert.Throws<IOException>(() => writer.Write(Encoding.ASCII.GetBytes("{}")));
        Assert.Contains("test error", ex.Message, StringComparison.Ordinal);
    }

    // baseproto_test.go:147-151 — errorWriter: every write fails.
    private sealed class ErrorStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get => 0; set { } }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("test error");
    }
}
