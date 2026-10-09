// Tests for the api JSON-RPC protocol face (protocol_jsonrpc.go): LSP Content-Length
// framing in/out, envelope shapes for request/response/error, and raw-JSON payload
// passthrough (RawBinary inlines verbatim instead of being base64'd).

using System.IO;
using System.Text;
using System.Text.Json;
using Ts.Api;
using Xunit;

namespace Ts.Api.Tests;

public class ProtocolJsonRpcTests
{
    [Fact]
    public void WriteThenRead_RoundTripsRequest()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"file\":\"a.ts\"}");
        using var ms = new MemoryStream();
        var protocol = new JsonRpcProtocol(ms);
        protocol.WriteRequest(null, "getSourceFile", payload);

        ms.Position = 0;
        var reader = new JsonRpcProtocol(ms);
        (global::Ts.JsonRpc.Message? message, bool ok) = reader.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsNotification());
        Assert.Equal("getSourceFile", message.Method);
        Assert.Equal(payload, message.Params);
    }

    [Fact]
    public void WriteThenRead_RoundTripsResponseWithId()
    {
        byte[] result = Encoding.UTF8.GetBytes("true");
        using var ms = new MemoryStream();
        var protocol = new JsonRpcProtocol(ms);
        protocol.WriteResponse(global::Ts.JsonRpc.Id.NewIdInt(7), result);

        ms.Position = 0;
        var reader = new JsonRpcProtocol(ms);
        (global::Ts.JsonRpc.Message? message, bool ok) = reader.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsResponse());
        Assert.Equal(7, message.Id?.IntValue);
        Assert.Equal(result, message.Result);
    }

    [Fact]
    public void WriteError_ProducesErrorEnvelope()
    {
        using var ms = new MemoryStream();
        var protocol = new JsonRpcProtocol(ms);
        protocol.WriteError(global::Ts.JsonRpc.Id.NewIdString("9"), new global::Ts.JsonRpc.ResponseError
        {
            Code = global::Ts.JsonRpc.ErrorCodes.CodeInternalError,
            Message = "boom",
        });

        ms.Position = 0;
        var reader = new JsonRpcProtocol(ms);
        (global::Ts.JsonRpc.Message? message, bool ok) = reader.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.True(message!.IsResponse());
        Assert.Equal("9", message.Id?.StringValue);
        Assert.NotNull(message.Error);
        Assert.Equal(global::Ts.JsonRpc.ErrorCodes.CodeInternalError, message.Error!.Code);
        Assert.Equal("boom", message.Error.Message);
    }

    [Fact]
    public void ReadMessage_OnEmptyStream_ReturnsNotOk()
    {
        using var ms = new MemoryStream();
        var reader = new JsonRpcProtocol(ms);

        (global::Ts.JsonRpc.Message? message, bool ok) = reader.ReadMessage();

        Assert.False(ok);
        Assert.Null(message);
    }

    // RawBinary rides the params slot as raw JSON (protocol_jsonrpc.go writes the bytes
    // verbatim via json.RawMessage semantics) — it must not be base64- or string-encoded.
    [Fact]
    public void WriteRequest_InlinesRawBinaryVerbatim()
    {
        byte[] raw = Encoding.UTF8.GetBytes("{\"k\":1}");
        using var ms = new MemoryStream();
        var protocol = new JsonRpcProtocol(ms);
        protocol.WriteRequest(null, "echo", new RawBinary(raw));

        ms.Position = 0;
        var reader = new JsonRpcProtocol(ms);
        (global::Ts.JsonRpc.Message? message, bool ok) = reader.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.Equal(raw, message!.Params);
    }

    // Typed payloads go through the source-generated ApiJsonContext face (plan/07 §4.2 #5).
    [Fact]
    public void WriteRequest_SerializesTypedParamsThroughApiJsonContext()
    {
        using var ms = new MemoryStream();
        var protocol = new JsonRpcProtocol(ms);
        protocol.WriteRequest(null, "release", new ReleaseParams { Snapshot = new SnapshotId(3) });

        ms.Position = 0;
        var reader = new JsonRpcProtocol(ms);
        (global::Ts.JsonRpc.Message? message, bool ok) = reader.ReadMessage();

        Assert.True(ok);
        Assert.NotNull(message);
        Assert.NotNull(message!.Params);
        using var doc = JsonDocument.Parse(message.Params);
        Assert.Equal(3UL, doc.RootElement.GetProperty("snapshot").GetUInt64());
    }
}
