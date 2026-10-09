// Tests for the api MessagePack protocol framing (protocol_msgpack.go): byte-exact
// [0x93, type, bin(method), bin(payload)] tuples in both directions, the uint8 message
// type escape on read, the CallError shape, and the MessageType stringer.

using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.Api;
using Xunit;

namespace Ts.Api.Tests;

public class ProtocolMsgPackTests
{
    // MessageType.String (stringer_generated.go): "MessageType" + camel-cased constant.
    [Theory]
    [InlineData(MessageType.Unknown, "MessageTypeUnknown")]
    [InlineData(MessageType.Request, "MessageTypeRequest")]
    [InlineData(MessageType.CallResponse, "MessageTypeCallResponse")]
    [InlineData(MessageType.CallError, "MessageTypeCallError")]
    [InlineData(MessageType.Response, "MessageTypeResponse")]
    [InlineData(MessageType.Error, "MessageTypeError")]
    [InlineData(MessageType.Call, "MessageTypeCall")]
    public void MessageType_StringerNames(MessageType m, string expected)
    {
        Assert.Equal(expected, m.String());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void MessageType_IsValidBounds(byte value, bool expected)
    {
        Assert.Equal(expected, ((MessageType)value).IsValid());
    }

    private static byte[] FramedRequest(string method, byte[] payload)
    {
        // [0x93, fixint(Request=1), bin8(method), bin8(payload)] — Go's ReadMessage maps
        // MessageTypeRequest (the incoming call) to a JSON-RPC request with the method as
        // pseudo-ID; MessageTypeCall (6) is write-only and errors on read
        // (protocol_msgpack.go:66-73, 107).
        var bytes = new List<byte> { 0x93, 0x01, 0xC4, (byte)method.Length };
        bytes.AddRange(Encoding.UTF8.GetBytes(method));
        bytes.Add(0xC4);
        bytes.Add((byte)payload.Length);
        bytes.AddRange(payload);
        return [.. bytes];
    }

    [Fact]
    public void WriteRequest_EmitsByteExactCallTuple()
    {
        using var ms = new MemoryStream();
        var protocol = new MessagePackProtocol(ms);
        protocol.WriteRequest(null, "foo", new RawBinary([0x01]));

        byte[] expected =
        [
            0x93, 0x06,
            0xC4, 0x03, (byte)'f', (byte)'o', (byte)'o',
            0xC4, 0x01, 0x01,
        ];
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public void WriteResponse_UsesIdStringAsMethod()
    {
        using var ms = new MemoryStream();
        var protocol = new MessagePackProtocol(ms);
        protocol.WriteResponse(global::Ts.JsonRpc.Id.NewIdString("42"), new RawBinary([0x07]));

        byte[] expected =
        [
            0x93, 0x04,
            0xC4, 0x02, (byte)'4', (byte)'2',
            0xC4, 0x01, 0x07,
        ];
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public void WriteError_SendsErrorTupleWithMessage()
    {
        using var ms = new MemoryStream();
        var protocol = new MessagePackProtocol(ms);
        protocol.WriteError(global::Ts.JsonRpc.Id.NewIdString("7"), new global::Ts.JsonRpc.ResponseError
        {
            Code = -32603,
            Message = "boom",
        });

        byte[] expected =
        [
            0x93, 0x05,
            0xC4, 0x01, (byte)'7',
            0xC4, 0x04, (byte)'b', (byte)'o', (byte)'o', (byte)'m',
        ];
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public void ReadMessage_DecodesCallRequest()
    {
        byte[] wire = FramedRequest("getSymbolAtPosition", [0xAB, 0xCD]);
        using var ms = new MemoryStream(wire);
        var protocol = new MessagePackProtocol(ms);

        (global::Ts.JsonRpc.Message? message, bool ok) = protocol.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsRequest());
        Assert.Equal("getSymbolAtPosition", message.Method);
        Assert.Equal([0xAB, 0xCD], message.Params);
        // The method doubles as the pseudo-ID (protocol_msgpack.go:69).
        Assert.Equal("getSymbolAtPosition", message.Id?.StringValue);
    }

    [Fact]
    public void ReadMessage_AcceptsUint8TypeEscape()
    {
        // Same tuple but with the message type written as 0xCC 0x01 (uint8 marker).
        var wire = new List<byte> { 0x93, 0xCC, 0x01, 0xC4, 0x02, (byte)'o', (byte)'k', 0xC4, 0x00 };
        using var ms = new MemoryStream([.. wire]);
        var protocol = new MessagePackProtocol(ms);

        (global::Ts.JsonRpc.Message? message, bool ok) = protocol.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsRequest());
        Assert.Equal("ok", message.Method);
    }

    [Fact]
    public void ReadMessage_DecodesCallResponseAsResponseWithResult()
    {
        // [0x93, 2 (CallResponse), bin(method), bin(payload)]
        var wire = new List<byte> { 0x93, 0x02, 0xC4, 0x02, (byte)'4', (byte)'2', 0xC4, 0x01, 0x09 };
        using var ms = new MemoryStream([.. wire]);
        var protocol = new MessagePackProtocol(ms);

        (global::Ts.JsonRpc.Message? message, bool ok) = protocol.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsResponse());
        Assert.Equal("42", message.Id?.StringValue);
        Assert.Equal([0x09], message.Result);
    }

    [Fact]
    public void ReadMessage_DecodesCallErrorAsResponseWithError()
    {
        // [0x93, 3 (CallError), bin(method), bin(message)]
        var wire = new List<byte> { 0x93, 0x03, 0xC4, 0x02, (byte)'1', (byte)'0', 0xC4, 0x03, (byte)'b', (byte)'a', (byte)'d' };
        using var ms = new MemoryStream([.. wire]);
        var protocol = new MessagePackProtocol(ms);

        (global::Ts.JsonRpc.Message? message, bool ok) = protocol.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsResponse());
        Assert.Equal("10", message.Id?.StringValue);
        Assert.NotNull(message.Error);
        Assert.Equal(global::Ts.JsonRpc.ErrorCodes.CodeInternalError, message.Error!.Code);
        Assert.Equal("bad", message.Error!.Message);
    }

    [Fact]
    public void ReadMessage_OnEmptyStream_ReturnsNotOk()
    {
        using var ms = new MemoryStream([]);
        var protocol = new MessagePackProtocol(ms);

        (global::Ts.JsonRpc.Message? message, bool ok) = protocol.ReadMessage();

        Assert.False(ok);
        Assert.Null(message);
    }

    // MessageTypeCall (6) is write-only: Go's ReadMessage errors on it
    // (protocol_msgpack.go:107 — `unexpected message type: %d`).
    [Fact]
    public void Read_RejectsWriteOnlyCallType()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"x\":1}");
        using var ms = new MemoryStream();
        var writer = new MessagePackProtocol(ms);
        writer.WriteRequest(null, "getSourceFile", payload);

        ms.Position = 0;
        var reader = new MessagePackProtocol(ms);
        Assert.Throws<MsgPackException>(() => reader.ReadMessage());
    }
}
