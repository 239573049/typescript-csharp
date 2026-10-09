// Ported from tsgo internal/api/transport.go, transport_unix.go, transport_windows.go
// (single runtime-branched file per plan/06 §9.1: "transport_unix.go/transport_windows.go
// → Transport.cs 单文件运行时分支"; UnixDomainSocketEndPoint for Unix, NamedPipeServerStream
// for Windows).

using System.Net.Sockets;

using System;

namespace Ts.Api;

// Transport is an interface for accepting connections from API clients.
// (transport.go:9-14)
public interface Transport
{
    // Accept waits for and returns the next connection.
    // PORT-NOTE: Go returns (io.ReadWriteCloser, error); C# returns a System.IO.Stream
    // (read+write+dispose cover io.ReadWriteCloser's surface) and throws on failure.
    System.IO.Stream Accept();

    // Close stops the transport from accepting new connections.
    void Close();
}

// PipeTransport accepts connections on a Unix domain socket or Windows named pipe.
// (transport.go:17-19)
public sealed class PipeTransport : Transport
{
    private readonly Socket? unixListener;
    private readonly System.Func<System.IO.Pipes.NamedPipeServerStream>? pipeFactory;
    private readonly string path;

    private PipeTransport(Socket unixListener, string path)
    {
        this.unixListener = unixListener;
        this.path = path;
    }

    private PipeTransport(System.Func<System.IO.Pipes.NamedPipeServerStream> pipeFactory, string path)
    {
        this.pipeFactory = pipeFactory;
        this.path = path;
    }

    // NewPipeTransport creates a new transport listening on the given path.
    // On Unix, this creates a Unix domain socket. On Windows, this creates a named pipe.
    // (transport.go:23-29)
    public static PipeTransport NewPipeTransport(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Go: CreateNamedPipe(path, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE, ...) — the
            // pipe name is the first ctor argument; .NET's transmission mode is Byte.
            return new PipeTransport(() => new System.IO.Pipes.NamedPipeServerStream(
                path,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                System.IO.Pipes.PipeTransmissionMode.Byte), path);
        }
        // transport_unix.go:13-16 — newPipeListener: remove any existing socket file.
        try
        {
            System.IO.File.Delete(path);
        }
        catch (System.IO.FileNotFoundException)
        {
            // No existing socket file.
        }
        catch (System.IO.DirectoryNotFoundException)
        {
            // No existing socket file.
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        return new PipeTransport(listener, path);
    }

    // Accept implements Transport. (transport.go:32-34)
    public System.IO.Stream Accept()
    {
        if (unixListener is not null)
        {
            Socket conn = unixListener.Accept();
            return new NetworkStream(conn, ownsSocket: true);
        }
        System.IO.Pipes.NamedPipeServerStream pipe = pipeFactory!();
        pipe.WaitForConnection();
        return pipe;
    }

    // Close implements Transport. (transport.go:37-39)
    public void Close()
    {
        unixListener?.Close();
        // Named-pipe factories have no listener handle; each accepted pipe closes with
        // its stream. (Go's winio listener Close is similarly a no-op once created.)
    }

    // Path returns the path of the pipe/socket. (transport.go:42-44)
    public string Path()
    {
        return path;
    }
}

// StdioTransport wraps stdin/stdout as a single connection transport.
// It only accepts one connection. (transport.go:47-52)
public sealed class StdioTransport : Transport
{
    private readonly System.IO.Stream stdin;
    private readonly System.IO.Stream stdout;
    private bool used;

    // NewStdioTransport creates a transport using the given stdin/stdout.
    // (transport.go:55-60)
    public StdioTransport(System.IO.Stream stdin, System.IO.Stream stdout)
    {
        this.stdin = stdin;
        this.stdout = stdout;
    }

    // Accept implements Transport. (transport.go:63-74)
    public System.IO.Stream Accept()
    {
        if (used)
        {
            throw new System.IO.EndOfStreamException("EOF");
        }
        used = true;
        return new DuplexStream(stdin, stdout);
    }

    // Close implements Transport. (transport.go:77-79)
    public void Close()
    {
    }
}

// stdioConn (transport.go:81-95) — combines a read side and a write side into one
// duplex stream; Close closes both like Go's stdioConn.Close.
internal sealed class DuplexStream : System.IO.Stream
{
    private readonly System.IO.Stream reader;
    private readonly System.IO.Stream writer;

    public DuplexStream(System.IO.Stream reader, System.IO.Stream writer)
    {
        this.reader = reader;
        this.writer = writer;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override int Read(byte[] buffer, int offset, int count)
    {
        return reader.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        writer.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        writer.Flush();
    }

    public override long Seek(long offset, System.IO.SeekOrigin origin)
    {
        throw new System.IO.IOException("duplex stream is not seekable");
    }

    public override void SetLength(long value)
    {
        throw new System.IO.IOException("duplex stream is not seekable");
    }

    public override long Length => throw new System.IO.IOException("duplex stream is not seekable");

    public override long Position
    {
        get => throw new System.IO.IOException("duplex stream is not seekable");
        set => throw new System.IO.IOException("duplex stream is not seekable");
    }

    // transport.go:88-95 — Close closes stdin then stdout, returning the first error.
    public override void Close()
    {
        reader.Close();
        writer.Close();
    }
}

public static class TransportFunctions
{
    // GeneratePipePath returns a platform-appropriate pipe path for the given name.
    // (transport_unix.go:19-22 / transport_windows.go:17-19)
    public static string GeneratePipePath(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return @"\\.\pipe\" + name;
        }
        return System.IO.Path.Join(System.IO.Path.GetTempPath(), name);
    }
}
