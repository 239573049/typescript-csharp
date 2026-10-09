// Ported from tsgo internal/lsp/lsproto/jsonrpc.go (whole file) plus the
// request-envelope helpers of internal/lsp/lsproto/lsp.go (RequestInfo.UnmarshalResult,
// RequestInfo.NewRequestMessage, NotificationInfo.NewNotificationMessage, UnmarshalParams,
// WithClientCapabilities' C# replacement is Project/LsProto.Missing.cs).
//
// PORT-NOTE: the generated Protocol.g.cs deliberately stops at the Info registry; the
// generator (tools/generator/generate-lsp.ts:2502) says these Message helpers "ride the
// future lsproto/jsonrpc.go port". They are hosted here, hand-written, so the server port
// does not touch generated files. Go stores inbound Params/Result as raw json.Value; C#
// keeps them as raw JsonElement (deferred decode, same shape). Serialization is a
// hand-rolled Utf8JsonWriter emitter (AOT-safe, plan/07 §4) matching Go's field order and
// `omitzero` behavior, with one deliberate difference: a null result is emitted as
// `"result":null` (Go omits the key) because JSON-RPC requires result XOR error and the
// C# replay baseline expects the member to be present.

using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Ts.LsProto;

// jsonrpc.go:10-17 — NewID. Go wraps it because lsproto has its own IntegerOrString type;
// this converts the generated C# IntegerOrString to the jsonrpc one and forwards to
// JsonRpc.Id.NewId.
public static class LsProtoNewId
{
    public static global::Ts.JsonRpc.Id NewId(IntegerOrString rawValue) =>
        global::Ts.JsonRpc.Id.NewId(new global::Ts.JsonRpc.IntegerOrString { Integer = rawValue.Integer, String = rawValue.String });
}

// jsonrpc.go:19-31 — Message (Kind + private request/response payload).
public sealed class Message
{
    private readonly object msg; // RequestMessage | ResponseMessage

    private Message(global::Ts.JsonRpc.MessageKind kind, object msg)
    {
        Kind = kind;
        this.msg = msg;
    }

    public global::Ts.JsonRpc.MessageKind Kind { get; }

    // jsonrpc.go:24-30 — AsRequest / AsResponse.
    public RequestMessage AsRequest() => (RequestMessage)msg;

    public ResponseMessage AsResponse() => (ResponseMessage)msg;

    // jsonrpc.go:87-96 — RequestMessage.Message().
    public static Message OfRequest(RequestMessage request)
    {
        var kind = request.Id is not null
            ? global::Ts.JsonRpc.MessageKind.Request
            : global::Ts.JsonRpc.MessageKind.Notification;
        return new Message(kind, request);
    }

    // jsonrpc.go:125-130 — ResponseMessage.Message().
    public static Message OfResponse(ResponseMessage response) =>
        new(global::Ts.JsonRpc.MessageKind.Response, response);

    // jsonrpc.go:76-78 — MarshalJSON (field order and omitzero behavior mirror Go's
    // RequestMessage/ResponseMessage tags).
    public void WriteTo(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("jsonrpc", global::Ts.JsonRpc.JsonRpcVersion.Value);
        if (msg is RequestMessage request)
        {
            if (request.Id is not null)
            {
                WriteId(json, request.Id);
            }
            json.WriteString("method", request.Method.Value);
            if (request.Params is { } parameters)
            {
                json.WritePropertyName("params");
                parameters.WriteTo(json);
            }
        }
        else
        {
            var response = (ResponseMessage)msg;
            if (response.Id is not null)
            {
                WriteId(json, response.Id);
            }
            if (response.Result is { } result)
            {
                json.WritePropertyName("result");
                result.WriteTo(json);
            }
            else if (response.Error is null)
            {
                // Deliberate difference from Go's omitzero (see file header): a successful
                // response with no result emits an explicit null result.
                json.WriteNull("result");
            }
            if (response.Error is { } error)
            {
                json.WritePropertyName("error");
                WriteError(json, error);
            }
        }
        json.WriteEndObject();
    }

    private static void WriteId(Utf8JsonWriter json, global::Ts.JsonRpc.Id id)
    {
        json.WritePropertyName("id");
        if (id.IsString)
        {
            json.WriteStringValue(id.StringValue);
        }
        else
        {
            json.WriteNumberValue(id.IntValue);
        }
    }

    private static void WriteError(Utf8JsonWriter json, global::Ts.JsonRpc.ResponseError error)
    {
        json.WriteStartObject();
        json.WriteNumber("code", error.Code);
        json.WriteString("message", error.Message);
        json.WriteEndObject();
    }

    // jsonrpc.go:32-74 — UnmarshalJSON. A message with an ID and no method is a
    // response; otherwise it is a request or notification with raw params. Malformed
    // JSON throws ErrorCodeInvalidRequest.
    public static Message Parse(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("message must be a JSON object");
            }

            global::Ts.JsonRpc.Id? id = null;
            string method = "";
            JsonElement? parameters = null;
            JsonElement? result = null;
            global::Ts.JsonRpc.ResponseError? error = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "id":
                        // Go: *jsonrpc.ID — a null id unmarshals to nil.
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            id = global::Ts.JsonRpc.Id.NewIdString(property.Value.GetString()!);
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Number)
                        {
                            id = global::Ts.JsonRpc.Id.NewIdInt(property.Value.GetInt32());
                        }
                        break;
                    case "method":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw new JsonException("method must be a string");
                        }
                        method = property.Value.GetString()!;
                        break;
                    case "params":
                        parameters = property.Value.Clone();
                        break;
                    case "result":
                        result = property.Value.Clone();
                        break;
                    case "error":
                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            int code = 0;
                            string message = "";
                            foreach (JsonProperty errorField in property.Value.EnumerateObject())
                            {
                                if (errorField.NameEquals("code"))
                                {
                                    code = errorField.Value.GetInt32();
                                }
                                else if (errorField.NameEquals("message"))
                                {
                                    message = errorField.Value.GetString() ?? "";
                                }
                            }
                            error = new global::Ts.JsonRpc.ResponseError { Code = code, Message = message };
                        }
                        break;
                }
            }

            // We don't have a method in the response, so we have no idea what to decode.
            if (id is not null && method == "")
            {
                return Message.OfResponse(new ResponseMessage { Id = id, Result = result, Error = error });
            }

            return Message.OfRequest(new RequestMessage { Id = id, Method = new Method(method), Params = parameters });
        }
        catch (JsonException e)
        {
            throw new LspErrorException(
                ErrorCode.InvalidRequest,
                $"{LspErrorException.CodeToString(ErrorCode.InvalidRequest)}: {e.Message}", e);
        }
    }
}

// jsonrpc.go:80-96 — RequestMessage (a notification when ID is nil).
public sealed class RequestMessage
{
    public global::Ts.JsonRpc.Id? Id { get; init; }

    public Method Method { get; init; }

    // Go: Params any `json:"params,omitzero"` — raw JSON; nil means absent.
    public JsonElement? Params { get; init; }

    public Message Message() => global::Ts.LsProto.Message.OfRequest(this);

    // lsp.go NewRequestMessage — builds an outgoing request with the params serialized
    // via the source-generated type info. NoParams (omitzero in Go) is left absent.
    public static RequestMessage New<TParams, TResult>(
        global::Ts.LsProto.RequestInfo<TParams, TResult> info,
        global::Ts.JsonRpc.Id id,
        TParams parameters)
    {
        return new RequestMessage
        {
            Id = id,
            Method = info.Method,
            Params = SerializeParams(parameters, info.ParamsTypeInfo),
        };
    }

    // lsp.go NotificationInfo.NewNotificationMessage.
    public static RequestMessage New<TParams>(
        global::Ts.LsProto.NotificationInfo<TParams> info,
        TParams parameters)
    {
        return new RequestMessage
        {
            Method = info.Method,
            Params = SerializeParams(parameters, info.ParamsTypeInfo),
        };
    }

    private static JsonElement? SerializeParams<TParams>(TParams parameters, JsonTypeInfo<TParams> typeInfo)
    {
        if (parameters is null || parameters is NoParams)
        {
            return null;
        }
        return JsonSerializer.SerializeToElement(parameters, typeInfo);
    }
}

// jsonrpc.go:118-130 — ResponseMessage.
public sealed class ResponseMessage
{
    public global::Ts.JsonRpc.Id? Id { get; init; }

    // Go: Result any `json:"result,omitzero"`. Outgoing results are pre-serialized to
    // raw JSON by the sender; null means "no result payload" (written as null, see
    // Message.WriteTo).
    public JsonElement? Result { get; init; }

    public global::Ts.JsonRpc.ResponseError? Error { get; init; }

    public Message Message() => global::Ts.LsProto.Message.OfResponse(this);
}

// Go: the lsproto.ErrorCode values double as error values (errors.Is targets). C# wraps
// the code in this exception type; LspErrors.CodeOf walks the chain for it.
public sealed class LspErrorException : Exception
{
    public ErrorCode Code { get; }

    public LspErrorException(ErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public LspErrorException(ErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    // Go: ErrorCode.Error() (stringer with the ErrorCode prefix trimmed).
    public static string CodeToString(ErrorCode code) => code switch
    {
        ErrorCode.ParseError => "ParseError",
        ErrorCode.InvalidRequest => "InvalidRequest",
        ErrorCode.MethodNotFound => "MethodNotFound",
        ErrorCode.InvalidParams => "InvalidParams",
        ErrorCode.InternalError => "InternalError",
        ErrorCode.ServerNotInitialized => "ServerNotInitialized",
        ErrorCode.UnknownErrorCode => "UnknownErrorCode",
        ErrorCode.RequestFailed => "RequestFailed",
        ErrorCode.ServerCancelled => "ServerCancelled",
        ErrorCode.ContentModified => "ContentModified",
        ErrorCode.RequestCancelled => "RequestCancelled",
        _ => $"ErrorCode({(int)code})",
    };
}

// server.go:657-660 — userFacingRequestFailedError (a string error whose Unwrap is
// ErrorCodeRequestFailed).
public sealed class UserFacingRequestFailedException : Exception
{
    public UserFacingRequestFailedException(string message)
        : base(message)
    {
    }
}

// Go: errors.Is / errors.AsType over the wrapped error chains.
public static class LspErrors
{
    // Go: errors.AsType[lsproto.ErrorCode](err).
    public static ErrorCode? CodeOf(Exception? err)
    {
        while (err is not null)
        {
            if (err is LspErrorException lspError)
            {
                return lspError.Code;
            }
            if (err is UserFacingRequestFailedException)
            {
                return ErrorCode.RequestFailed;
            }
            err = err.InnerException;
        }
        return null;
    }

    // Go: errors.Is(err, io.EOF).
    public static bool IsEof(Exception? err)
    {
        while (err is not null)
        {
            if (err is EofException)
            {
                return true;
            }
            err = err.InnerException;
        }
        return false;
    }

    // Go: errors.Is(err, context.Canceled).
    public static bool IsCanceled(Exception? err)
    {
        while (err is not null)
        {
            if (err is OperationCanceledException)
            {
                return true;
            }
            err = err.InnerException;
        }
        return false;
    }
}

// Go: io.EOF, used as the graceful-exit error by handleExit and readLoop.
public sealed class EofException : Exception
{
    public static readonly EofException Instance = new();

    private EofException()
        : base("EOF")
    {
    }
}

// lsp.go UnmarshalParams — decodes the params of an inbound request or notification
// message into the requested type. Inbound messages store their params as a raw
// JsonElement (see LsProtoMessageReader); decoding is deferred to the point of dispatch
// so that param types for methods the server never handles are not forced into the
// binary.
//
// A NoParams method must be given no params; every other method must be given params as
// an object or array. A violation throws ErrorCodeInvalidParams.
public static class LsProtoUnmarshal
{
    public static TParams UnmarshalParams<TParams>(JsonElement? raw, JsonTypeInfo<TParams> typeInfo)
    {
        // params is the zero value of TParams; this asserts on its type, i.e. whether the
        // method was declared with NoParams.
        if (typeof(TParams) == typeof(NoParams))
        {
            if (raw is not null)
            {
                throw new LspErrorException(
                    ErrorCode.InvalidParams,
                    $"{LspErrorException.CodeToString(ErrorCode.InvalidParams)}: expected no params, got {raw.Value.ToString()}");
            }
            return default!;
        }

        // The base protocol defines params as `array | object`; reject anything else
        // (absent, null, or a scalar).
        if (raw is null ||
            (raw.Value.ValueKind != JsonValueKind.Object && raw.Value.ValueKind != JsonValueKind.Array))
        {
            throw new LspErrorException(
                ErrorCode.InvalidParams,
                $"{LspErrorException.CodeToString(ErrorCode.InvalidParams)}: params must be an object or array");
        }
        try
        {
            return JsonSerializer.Deserialize(raw.Value, typeInfo)
                ?? throw new LspErrorException(ErrorCode.InvalidParams, "params must not be null");
        }
        catch (JsonException e)
        {
            throw new LspErrorException(
                ErrorCode.InvalidParams,
                $"{LspErrorException.CodeToString(ErrorCode.InvalidParams)}: {e.Message}", e);
        }
    }

    // lsp.go RequestInfo.UnmarshalResult — decodes the result of a server-initiated
    // request response. A null/absent result unmarshals into Go's zero value.
    public static TResult UnmarshalResult<TResult>(JsonElement? result, JsonTypeInfo<TResult> typeInfo)
    {
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default!;
        }
        try
        {
            return JsonSerializer.Deserialize(result.Value, typeInfo) ?? default!;
        }
        catch (JsonException e)
        {
            throw new LspErrorException(ErrorCode.InvalidParams, e.Message, e);
        }
    }
}
