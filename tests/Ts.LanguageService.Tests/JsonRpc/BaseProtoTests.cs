// Tests for the jsonrpc Content-Length framing (ported from tsgo internal/jsonrpc/baseproto.go).
// Verifies byte-exact frame read/write round-trips (Content-Length header + JSON body).

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
}
