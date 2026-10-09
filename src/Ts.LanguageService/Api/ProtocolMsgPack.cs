// Ported from tsgo internal/api/protocol_msgpack.go (whole file).
//
// The wire format is a custom msgpack-based tuple: [MessageType, method, payload]
// where the tuple is a fixed 3-element array (0x93), the message type is written as a
// positive fixint (or read back as fixint/uint8), and method/payload are bin8/16/32.
// Payloads themselves are JSON bytes (or raw binary for RawBinary results).
// Byte compatibility with tsgo's api client is the acceptance criterion: framing,
// field order, and size-prefix families are reproduced exactly.

using System.Text;

using System;
using Ts.JsonRpc;

namespace Ts.Api;

// MessageType represents the type of message in the msgpack protocol.
// protocol_msgpack.go:14-24.
public enum MessageType : byte
{
    Unknown = 0,
    Request = 1,
    CallResponse = 2,
    CallError = 3,
    Response = 4,
    Error = 5,
    Call = 6,
}

public static class MessageTypeExtensions
{
    // protocol_msgpack.go:26-28.
    public static bool IsValid(this MessageType m)
    {
        return m >= MessageType.Request && m <= MessageType.Call;
    }

    // stringer_generated.go:24-30 — String (stringer -type=MessageType). Values outside
    // the known range render as "MessageType(<n>)".
    public static string String(this MessageType m)
    {
        return m switch
        {
            MessageType.Unknown => "MessageTypeUnknown",
            MessageType.Request => "MessageTypeRequest",
            MessageType.CallResponse => "MessageTypeCallResponse",
            MessageType.CallError => "MessageTypeCallError",
            MessageType.Response => "MessageTypeResponse",
            MessageType.Error => "MessageTypeError",
            MessageType.Call => "MessageTypeCall",
            _ => $"MessageType({(int)m})",
        };
    }
}

// MessagePackProtocol implements the Protocol interface using the custom
// msgpack-based tuple format: [MessageType, method, payload].
public sealed class MessagePackProtocol : Protocol
{
    // MessagePack format constants (protocol_msgpack.go:31-37).
    private const byte MsgpackFixedArray3 = 0x93;
    private const byte MsgpackBin8 = 0xC4;
    private const byte MsgpackBin16 = 0xC5;
    private const byte MsgpackBin32 = 0xC6;
    private const byte MsgpackU8 = 0xCC;

    private readonly MsgPackReader r;
    private readonly MsgPackWriter w;

    // NewMessagePackProtocol creates a new msgpack protocol handler.
    public MessagePackProtocol(System.IO.Stream rw)
    {
        r = new MsgPackReader(rw);
        w = new MsgPackWriter(rw);
    }

    // ReadMessage implements Protocol.
    // Returns (message, ok): ok == false means clean EOF (Go io.EOF).
    public (Message? Message, bool Ok) ReadMessage()
    {
        (MessageType msgType, string method, byte[]? payload, bool ok) = ReadTuple();
        if (!ok)
        {
            return (null, false);
        }

        // Convert msgpack message type to JSON-RPC message
        var msg = new Message();

        switch (msgType)
        {
            case MessageType.Request:
                // Client request - needs an ID for response
                // We use the method as a pseudo-ID since this protocol doesn't have explicit IDs
                msg.Id = global::Ts.JsonRpc.Id.NewIdString(method);
                msg.Method = method;
                msg.Params = payload;
                break;
            case MessageType.CallResponse:
                // Response to our Call - use method as ID
                // Note: Method must be empty for IsResponse() to return true
                msg.Id = global::Ts.JsonRpc.Id.NewIdString(method);
                msg.Result = payload;
                break;
            case MessageType.CallError:
                // Error response to our Call
                // Note: Method must be empty for IsResponse() to return true
                msg.Id = global::Ts.JsonRpc.Id.NewIdString(method);
                msg.Error = new global::Ts.JsonRpc.ResponseError
                {
                    Code = global::Ts.JsonRpc.ErrorCodes.CodeInternalError,
                    Message = Encoding.UTF8.GetString(payload ?? []),
                };
                break;
            default:
                throw new MsgPackException($"unexpected message type: {(int)msgType}");
        }

        return (msg, true);
    }

    private (MessageType MsgType, string Method, byte[] Payload, bool Ok) ReadTuple()
    {
        // Read fixed array marker (0x93 = 3-element array)
        int t = r.ReadByte();
        if (t == -1)
        {
            return (0, "", null!, false);
        }
        if (t != MsgpackFixedArray3)
        {
            throw new MsgPackException($"api: invalid request: expected fixed 3-element array (0x93), received: 0x{t:x2}");
        }

        // Read message type - can be positive fixint (0x00-0x7F) or uint8 (0xCC + value)
        t = r.ReadByte();
        if (t == -1)
        {
            throw MsgPackException.Eof();
        }
        byte rawType;
        if (t <= 0x7F)
        {
            // Positive fixint - the byte IS the value
            rawType = (byte)t;
        }
        else if (t == MsgpackU8)
        {
            // uint8 marker - next byte is the value
            int b = r.ReadByte();
            if (b == -1)
            {
                throw MsgPackException.Eof();
            }
            rawType = (byte)b;
        }
        else
        {
            throw new MsgPackException($"api: invalid request: expected positive fixint or uint8 marker, received: 0x{t:x2}");
        }
        var msgType = (MessageType)rawType;
        if (!msgType.IsValid())
        {
            throw new MsgPackException($"api: invalid request: unknown message type: {(int)msgType}");
        }

        // Read method (binary)
        byte[] methodBytes = r.ReadBin();
        string method = Encoding.UTF8.GetString(methodBytes);

        // Read payload (binary)
        byte[] payload = r.ReadBin();

        return (msgType, method, payload, true);
    }

    // WriteRequest implements Protocol.
    public void WriteRequest(global::Ts.JsonRpc.Id? id, string method, object? @params)
    {
        // For msgpack protocol, requests from server are "Call" type
        byte[] payload = EncodePayload(@params);
        WriteTuple(MessageType.Call, method, payload);
    }

    // WriteNotification implements Protocol.
    public void WriteNotification(string method, object? @params)
    {
        // Msgpack protocol doesn't distinguish notifications from calls
        WriteRequest(null, method, @params);
    }

    // WriteResponse implements Protocol.
    public void WriteResponse(global::Ts.JsonRpc.Id? id, object? result)
    {
        string method = "";
        if (id is not null)
        {
            method = id.ToString();
        }

        // Check if result is raw binary (for efficient binary transport)
        byte[] payload = result is RawBinary raw ? raw.ToArray() : EncodePayload(result);

        WriteTuple(MessageType.Response, method, payload);
    }

    // WriteError implements Protocol.
    public void WriteError(global::Ts.JsonRpc.Id? id, global::Ts.JsonRpc.ResponseError? respErr)
    {
        string method = "";
        if (id is not null)
        {
            method = id.ToString();
        }
        WriteTuple(MessageType.Error, method, Encoding.UTF8.GetBytes(respErr?.Message ?? ""));
    }

    // EncodePayload JSON-encodes a params/result value (Go json.Marshal at
    // protocol_msgpack.go:185/212). RawBinary is handled by the callers.
    private static byte[] EncodePayload(object? value)
    {
        if (value is null)
        {
            return "null"u8.ToArray();
        }
        if (value is RawBinary raw)
        {
            return raw.ToArray();
        }
        if (value is byte[] rawJson)
        {
            return rawJson;
        }
        return global::Ts.Api.ApiJsonContext.SerializeToBytes(value);
    }

    private void WriteTuple(MessageType msgType, string method, byte[] payload)
    {
        // Write fixed array marker
        w.WriteByte(MsgpackFixedArray3);
        // Write message type as positive fixint (values 0-127 are written directly)
        w.WriteByte((byte)msgType);
        // Write method
        WriteBin(Encoding.UTF8.GetBytes(method));
        // Write payload
        WriteBin(payload);
        w.FlushOrThrow();
    }

    private void WriteBin(byte[] data)
    {
        int length = data.Length;
        if (length < 256)
        {
            w.WriteByte(MsgpackBin8);
            w.WriteByte((byte)length);
        }
        else if (length < 1 << 16)
        {
            w.WriteByte(MsgpackBin16);
            WriteUInt16BigEndian((ushort)length);
        }
        else
        {
            w.WriteByte(MsgpackBin32);
            WriteUInt32BigEndian((uint)length);
        }
        w.Write(data);
    }

    private void WriteUInt16BigEndian(ushort v)
    {
        w.WriteByte((byte)(v >> 8));
        w.WriteByte((byte)v);
    }

    private void WriteUInt32BigEndian(uint v)
    {
        w.WriteByte((byte)(v >> 24));
        w.WriteByte((byte)(v >> 16));
        w.WriteByte((byte)(v >> 8));
        w.WriteByte((byte)v);
    }
}

// RawBinary is a marker type for binary data that should be written
// directly by MessagePackProtocol instead of being JSON-encoded
// (protocol_msgpack.go:278-280).
public sealed class RawBinary
{
    private readonly byte[] data;

    public RawBinary(byte[] data)
    {
        this.data = data;
    }

    public static implicit operator RawBinary(byte[] data)
    {
        return new RawBinary(data);
    }

    public byte[] ToArray()
    {
        return data;
    }

    public ReadOnlySpan<byte> Span => data;

    public int Length => data.Length;
}
