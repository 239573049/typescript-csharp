// Ported from tsgo internal/jsonrpc/baseproto.go
//
// Base protocol for JSON-RPC with Content-Length headers (as used by LSP).
// https://microsoft.github.io/language-server-protocol/specifications/base/0.9/specification/
//
// PORT-NOTE: Go reads/writes over bufio.Reader/bufio.Writer with byte-exact header
// handling. We reproduce the same byte semantics directly on System.IO.Stream:
// header lines are read byte-by-byte up to and including '\n', compared with
// Ordinal ASCII byte equality, and the body is read as an exact-length byte block.
// All comparisons are on raw bytes (not decoded strings) to stay faithful to the
// LSP wire (UTF-8, CRLF framing).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ts.JsonRpc;

// Sentinel errors mirroring the Go package-level error vars. Returned via the
// (data, error) Try-pattern (plan/05 §4) so framing callers can distinguish EOF
// from malformed headers exactly as Go does.
public static class BaseProtoErrors
{
    public const string ErrInvalidHeader = "jsonrpc: invalid header";
    public const string ErrInvalidContentLength = "jsonrpc: invalid content length";
    public const string ErrNoContentLength = "jsonrpc: no content length";
}

// Thrown for header/content-length parse failures. Carries the Go sentinel text so
// callers can classify it, and (for invalid-header) the offending line bytes.
public sealed class JsonRpcFramingException : Exception
{
    public string Sentinel { get; }

    public JsonRpcFramingException(string sentinel, string message)
        : base(message)
    {
        Sentinel = sentinel;
    }
}

// Reader reads JSON-RPC messages with Content-Length framing.
//
// PORT-NOTE: Go wraps the source in a bufio.Reader. .NET streams are commonly
// already buffered; to keep byte-exactness (ReadBytes('\n')) we read one byte at a
// time from the underlying stream and accumulate header lines ourselves.
public sealed class Reader
{
    private readonly Stream r;

    // NewReader creates a new Reader.
    public Reader(Stream r)
    {
        this.r = r;
    }

    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] ContentLengthKey = "Content-Length"u8.ToArray();

    // Read reads the next message payload.
    //
    // Returns (data, ok): ok == false means clean EOF (Go's io.EOF). Malformed
    // headers throw JsonRpcFramingException (Go returns a non-EOF error).
    public (byte[]? Data, bool Ok) Read()
    {
        long contentLength = 0;

        for (; ; )
        {
            // Go: line, err := r.r.ReadBytes('\n')
            (byte[]? line, bool eof) = ReadBytesUntilNewline();
            if (line is null)
            {
                // EOF with no bytes read at all → Go returns nil, io.EOF.
                if (eof)
                {
                    return (null, false);
                }
                // Any other read error surfaces as a framing exception (Go wraps it).
                throw new JsonRpcFramingException(
                    "jsonrpc: read header",
                    "jsonrpc: read header: unexpected end of stream");
            }

            // Go: if bytes.Equal(line, []byte("\r\n")) { break }
            if (BytesEqual(line, CrLf))
            {
                break;
            }

            // Go: key, value, ok := bytes.Cut(line, []byte(":"))
            int colon = IndexOfByte(line, (byte)':');
            if (colon < 0)
            {
                throw new JsonRpcFramingException(
                    BaseProtoErrors.ErrInvalidHeader,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{BaseProtoErrors.ErrInvalidHeader}: {QuoteBytes(line)}"));
            }

            // key = line[:colon]; value = line[colon+1:]
            byte[] key = line[..colon];
            byte[] value = line[(colon + 1)..];

            // Go: if bytes.Equal(key, []byte("Content-Length"))
            if (BytesEqual(key, ContentLengthKey))
            {
                string trimmed = Encoding.UTF8.GetString(TrimSpace(value));
                // Go: strconv.ParseInt(string(bytes.TrimSpace(value)), 10, 64)
                if (!long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out contentLength))
                {
                    throw new JsonRpcFramingException(
                        BaseProtoErrors.ErrInvalidContentLength,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{BaseProtoErrors.ErrInvalidContentLength}: parse error"));
                }
                if (contentLength < 0)
                {
                    throw new JsonRpcFramingException(
                        BaseProtoErrors.ErrInvalidContentLength,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{BaseProtoErrors.ErrInvalidContentLength}: negative value {contentLength}"));
                }
            }
        }

        // Go: if contentLength <= 0 { return nil, ErrNoContentLength }
        if (contentLength <= 0)
        {
            throw new JsonRpcFramingException(BaseProtoErrors.ErrNoContentLength, BaseProtoErrors.ErrNoContentLength);
        }

        // Go: data := make([]byte, contentLength); io.ReadFull(r.r, data)
        var data = new byte[contentLength];
        if (!ReadFull(data))
        {
            throw new JsonRpcFramingException(
                "jsonrpc: read content",
                "jsonrpc: read content: unexpected end of stream");
        }

        return (data, true);
    }

    // ReadBytesUntilNewline mirrors bufio.Reader.ReadBytes('\n'):
    // returns all bytes up to and including the first '\n'. On EOF before any '\n':
    //   - if bytes were read, returns those bytes (Go returns data + io.EOF; here
    //     the caller treats a non-CRLF line at that point as a header line).
    //   - if no bytes were read, returns (null, eof=true).
    private (byte[]? Line, bool Eof) ReadBytesUntilNewline()
    {
        var buf = new List<byte>(64);
        for (; ; )
        {
            int b = r.ReadByte();
            if (b < 0)
            {
                // EOF.
                if (buf.Count == 0)
                {
                    return (null, true);
                }
                // Go's ReadBytes returns the partial data with io.EOF. The caller loop
                // only breaks on exact "\r\n"; a partial line here won't match, so it
                // will be treated as a (possibly invalid) header line, matching Go.
                return (buf.ToArray(), true);
            }
            buf.Add((byte)b);
            if (b == '\n')
            {
                return (buf.ToArray(), false);
            }
        }
    }

    // ReadFull mirrors io.ReadFull: fills the whole buffer or reports failure.
    private bool ReadFull(byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int n = r.Read(data, offset, data.Length - offset);
            if (n <= 0)
            {
                return false;
            }
            offset += n;
        }
        return true;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static int IndexOfByte(byte[] a, byte target)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == target)
            {
                return i;
            }
        }
        return -1;
    }

    // Mirrors bytes.TrimSpace over ASCII whitespace (space, \t, \n, \v, \f, \r).
    private static byte[] TrimSpace(byte[] a)
    {
        int start = 0;
        int end = a.Length;
        while (start < end && IsAsciiSpace(a[start]))
        {
            start++;
        }
        while (end > start && IsAsciiSpace(a[end - 1]))
        {
            end--;
        }
        return a[start..end];
    }

    private static bool IsAsciiSpace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\v' || b == (byte)'\f' || b == (byte)'\r';

    // Reproduces the %q rendering of the offending line for the error message.
    private static string QuoteBytes(byte[] a) => "\"" + Encoding.UTF8.GetString(a) + "\"";
}

// Writer writes JSON-RPC messages with Content-Length framing.
public sealed class Writer
{
    private readonly Stream w;

    // NewWriter creates a new Writer.
    public Writer(Stream w)
    {
        this.w = w;
    }

    // Write writes a message payload with Content-Length header.
    //
    // Go: fmt.Fprintf(w.w, "Content-Length: %d\r\n\r\n", len(data)) then the body,
    // then Flush. The header is pure ASCII; we emit its bytes directly.
    public void Write(byte[] data)
    {
        string header = string.Create(CultureInfo.InvariantCulture, $"Content-Length: {data.Length}\r\n\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        w.Write(headerBytes, 0, headerBytes.Length);
        w.Write(data, 0, data.Length);
        w.Flush();
    }
}
