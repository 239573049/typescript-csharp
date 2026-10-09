// Ported from tsgo internal/api/protocol_jsonrpc.go (whole file).
//
// PORT-NOTE: Go (de)serializes the JSON-RPC envelope with the internal json package
// (go-json-experiment). Per plan/07 §4.2 #5 the C# face uses System.Text.Json source
// generation for typed payloads; the raw envelope here is written with a handwritten
// Utf8JsonWriter (same pattern as Lsp/Server.cs WriteMessage) because Message.Params /
// Message.Result are raw JSON bytes (Ts.JsonRpc.Message stores byte[]?) that must be
// inlined verbatim on the wire — STJ would base64-encode a byte[]. Reads use a
// handwritten Utf8JsonReader for the same reason.

using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ts.JsonRpc;

namespace Ts.Api;

// JSONRPCProtocol implements the Protocol interface using JSON-RPC 2.0
// with the LSP base protocol framing (Content-Length headers).
public sealed class JsonRpcProtocol : Protocol
{
    private readonly global::Ts.JsonRpc.Reader reader;
    private readonly global::Ts.JsonRpc.Writer writer;

    // NewJSONRPCProtocol creates a new JSON-RPC protocol handler.
    public JsonRpcProtocol(System.IO.Stream rw)
    {
        reader = new global::Ts.JsonRpc.Reader(rw);
        writer = new global::Ts.JsonRpc.Writer(rw);
    }

    // ReadMessage implements Protocol.
    public (Message? Message, bool Ok) ReadMessage()
    {
        (byte[]? data, bool ok) = reader.Read();
        if (!ok)
        {
            return (null, false);
        }

        Message msg = ParseMessage(data!);
        return (msg, true);
    }

    // WriteRequest implements Protocol.
    public void WriteRequest(global::Ts.JsonRpc.Id? id, string method, object? @params)
    {
        byte[] data = WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            if (id is not null)
            {
                json.WritePropertyName("id");
                WriteId(json, id);
            }
            json.WriteString("method", method);
            if (@params is not null)
            {
                json.WritePropertyName("params");
                WriteValue(json, @params);
            }
            json.WriteEndObject();
        });
        writer.Write(data);
    }

    // WriteNotification implements Protocol.
    public void WriteNotification(string method, object? @params)
    {
        byte[] data = WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteString("method", method);
            if (@params is not null)
            {
                json.WritePropertyName("params");
                WriteValue(json, @params);
            }
            json.WriteEndObject();
        });
        writer.Write(data);
    }

    // WriteResponse implements Protocol.
    public void WriteResponse(global::Ts.JsonRpc.Id? id, object? result)
    {
        byte[] data = WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            if (id is not null)
            {
                json.WritePropertyName("id");
                WriteId(json, id);
            }
            // Go: if result == nil { result = json.Value("null") } (protocol_jsonrpc.go:71-73).
            json.WritePropertyName("result");
            if (result is null)
            {
                json.WriteNullValue();
            }
            else
            {
                WriteValue(json, result);
            }
            json.WriteEndObject();
        });
        writer.Write(data);
    }

    // WriteError implements Protocol.
    public void WriteError(global::Ts.JsonRpc.Id? id, global::Ts.JsonRpc.ResponseError? err)
    {
        byte[] data = WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            if (id is not null)
            {
                json.WritePropertyName("id");
                WriteId(json, id);
            }
            json.WritePropertyName("error");
            json.WriteStartObject();
            json.WriteNumber("code", err?.Code ?? 0);
            json.WriteString("message", err?.Message ?? "");
            json.WriteEndObject();
            json.WriteEndObject();
        });
        writer.Write(data);
    }

    // plan/07 §4.1 #4 — api writers use UnsafeRelaxedJsonEscaping (the Go internal json
    // writer does not HTML-escape; byte fidelity on the wire matters for conformance).
    private static readonly JsonWriterOptions JsonRpcWriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static byte[] WriteMessage(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, JsonRpcWriterOptions))
        {
            write(json);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteId(Utf8JsonWriter json, global::Ts.JsonRpc.Id id)
    {
        if (id.IsString)
        {
            json.WriteStringValue(id.StringValue);
        }
        else
        {
            json.WriteNumberValue(id.IntValue);
        }
    }

    // WriteValue serializes one params/result value. RawBinary is inlined as raw JSON
    // bytes (Go: json.Value carries pre-encoded JSON; RawBinary rides the same slot).
    // Typed objects go through ApiJsonContext source generation (plan/07 §4.2 #5).
    private static void WriteValue(Utf8JsonWriter json, object value)
    {
        if (value is RawBinary raw)
        {
            json.WriteRawValue(raw.Span, skipInputValidation: false);
            return;
        }
        if (value is byte[] rawJson)
        {
            json.WriteRawValue(rawJson, skipInputValidation: false);
            return;
        }
        if (value is string s)
        {
            json.WriteStringValue(s);
            return;
        }
        if (value is bool b)
        {
            json.WriteBooleanValue(b);
            return;
        }
        global::Ts.Api.ApiJsonContext.SerializePolymorphic(json, value);
    }

    // ParseMessage parses one JSON-RPC 2.0 envelope into Ts.JsonRpc.Message, keeping
    // params/result as raw JSON bytes (Go: json.Value — the exact wire bytes). Unknown
    // fields are ignored, matching Go json.Unmarshal semantics.
    internal static Message ParseMessage(byte[] data)
    {
        var msg = new Message();
        var reader = new Utf8JsonReader(data);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new MsgPackException("api: invalid JSON-RPC message: expected object");
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            switch (key)
            {
                case "jsonrpc":
                    // Value is the constant "2.0"; consumed and ignored.
                    reader.Skip();
                    break;
                case "id":
                    msg.Id = ParseId(ref reader);
                    break;
                case "method":
                    msg.Method = reader.GetString() ?? "";
                    break;
                case "params":
                    msg.Params = ReadRawToken(data, ref reader);
                    break;
                case "result":
                    msg.Result = ReadRawToken(data, ref reader);
                    break;
                case "error":
                    msg.Error = ParseResponseError(data, ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return msg;
    }

    // ReadRawToken captures the raw JSON bytes of the current token, preserving the
    // exact wire representation (escapes, number formatting) like Go's json.Value.
    private static byte[] ReadRawToken(byte[] data, ref Utf8JsonReader reader)
    {
        // PORT-NOTE: Position.GetInteger() throws for array-backed readers; BytesConsumed
        // is the equivalent consumed-bytes offset for byte[] input.
        int start = (int)reader.TokenStartIndex;
        reader.Skip();
        int end = (int)reader.BytesConsumed;
        return data[start..end];
    }

    private static global::Ts.JsonRpc.Id? ParseId(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return global::Ts.JsonRpc.Id.NewIdString(reader.GetString() ?? "");
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            return global::Ts.JsonRpc.Id.NewIdInt(reader.GetInt32());
        }
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        throw new MsgPackException("api: invalid JSON-RPC message: id must be string or number");
    }

    private static global::Ts.JsonRpc.ResponseError? ParseResponseError(byte[] data, ref Utf8JsonReader reader)
    {
        // The parse loop has already consumed the property name and reads the value token
        // before dispatching here, so the current token IS the error value.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        var err = new global::Ts.JsonRpc.ResponseError();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new MsgPackException("api: invalid JSON-RPC message: error must be an object");
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            switch (key)
            {
                case "code":
                    err.Code = reader.GetInt32();
                    break;
                case "message":
                    err.Message = reader.GetString() ?? "";
                    break;
                case "data":
                    err.Data = ReadRawToken(data, ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return err;
    }
}
