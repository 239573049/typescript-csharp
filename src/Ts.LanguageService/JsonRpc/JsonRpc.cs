// Ported from tsgo internal/jsonrpc/jsonrpc.go
//
// Package jsonrpc provides generic JSON-RPC 2.0 types and utilities
// that can be shared between LSP and other JSON-RPC based protocols.
//
// PORT-NOTE: Go marshals/unmarshals these types via custom (Un)MarshalJSON
// methods over an internal `json` package. In C# the wire encoding is handled by
// System.Text.Json *source generation only* (no reflection serializer, per
// plan/07-aot-constraints.md). This file keeps the value types and their
// classification helpers 1:1 with Go; the STJ converters that realize the exact
// wire shape live in JsonRpcJsonContext.cs.

using System;
using System.Globalization;

namespace Ts.JsonRpc;

// JSONRPCVersion represents the JSON-RPC version field, always "2.0".
public readonly struct JsonRpcVersion
{
    // Go: const jsonRPCVersion = `"2.0"` ; MarshalJSON emits the literal bytes.
    public const string JsonRpcVersionLiteral = "\"2.0\"";

    // Go: the bare value written on the wire is the string "2.0".
    public const string Value = "2.0";
}

// ID represents a JSON-RPC message ID, which can be either a string or integer.
//
// PORT-NOTE: Go models this as a struct with two private fields (str, int) and a
// convention that an all-zero value means "integer 0". We keep the same field
// layout so classification (String()/MarshalJSON()/TryInt()) is byte-identical.
public sealed class Id : IEquatable<Id>
{
    // Go: str string
    internal string str = "";

    // Go: int int32
    internal int intValue;

    // NewID creates an ID from an IntegerOrString value.
    public static Id NewId(IntegerOrString rawValue)
    {
        if (rawValue.String is not null)
        {
            return new Id { str = rawValue.String };
        }
        // Go: return &ID{int: *rawValue.Integer} — unconditional deref, matched.
        return new Id { intValue = rawValue.Integer!.Value };
    }

    // NewIDString creates a string ID.
    public static Id NewIdString(string str)
    {
        return new Id { str = str };
    }

    // NewIDInt creates an integer ID.
    public static Id NewIdInt(int i)
    {
        return new Id { intValue = i };
    }

    // Go: func (id *ID) String() string
    public override string ToString()
    {
        if (str != "")
        {
            return str;
        }
        return intValue.ToString(CultureInfo.InvariantCulture);
    }

    // Go: func (id *ID) TryInt() (int32, bool)
    // PORT-NOTE: Go allows a nil receiver; the nil branch is provided as a static
    // helper so callers holding a possibly-null Id behave identically.
    public static (int, bool) TryInt(Id? id)
    {
        if (id is null || id.str != "")
        {
            return (0, false);
        }
        return (id.intValue, true);
    }

    // Go: func (id *ID) MustInt() int32
    public int MustInt()
    {
        if (str != "")
        {
            throw new InvalidOperationException("ID is not an integer");
        }
        return intValue;
    }

    // Helpers for the STJ converter (mirror the Go MarshalJSON/UnmarshalJSON branch selectors).
    public bool IsString => str != "";

    public string StringValue => str;

    public int IntValue => intValue;

    // Go compares ID structs with == (map keys in the LSP server); value equality here.
    public bool Equals(Id? other) => other is not null && str == other.str && intValue == other.intValue;

    public override bool Equals(object? obj) => obj is Id other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(str, intValue);
}

// IntegerOrString is a helper type for creating IDs.
public sealed class IntegerOrString
{
    public int? Integer;
    public string? String;
}

// ResponseError represents a JSON-RPC error response.
public sealed class ResponseError
{
    // Go: Code int32 `json:"code"`
    public int Code;

    // Go: Message string `json:"message"`
    public string Message = "";

    // Go: Data any `json:"data,omitzero"`
    public object? Data;

    // Go: func (r *ResponseError) String() string
    // PORT-NOTE: nil-receiver branch preserved as a static helper.
    public static string StringOf(ResponseError? r)
    {
        if (r is null)
        {
            return "";
        }
        // Go marshals Data and, if that fails, includes it in the string. In C# the
        // data payload is opaque here; we reproduce the common (success) branch and,
        // faithful to Go, do not embed the raw data when marshalling would succeed.
        return string.Create(CultureInfo.InvariantCulture, $"[{r.Code}]: {r.Message}");
    }

    public override string ToString() => StringOf(this);

    // Go: func (r *ResponseError) Error() string { return r.String() }
    public string ErrorMessage => ToString();
}

// Standard JSON-RPC error codes.
public static class ErrorCodes
{
    public const int CodeParseError = -32700;
    public const int CodeInvalidRequest = -32600;
    public const int CodeMethodNotFound = -32601;
    public const int CodeInvalidParams = -32602;
    public const int CodeInternalError = -32603;
}

// MessageKind indicates what type of message this is.
public enum MessageKind
{
    // Go: iota order — Notification, Request, Response.
    Notification = 0,
    Request = 1,
    Response = 2,
}

// Message represents a raw JSON-RPC message that can be a request, notification, or response.
// Unlike lsproto.Message, this keeps params/result as raw JSON for generic handling.
//
// PORT-NOTE: Go stores Params/Result as `json.Value` (raw JSON bytes). We keep the
// raw JSON as UTF-8 byte spans (byte[]?) so the framing layer stays byte-faithful
// and no reflection-based (de)serialization is required.
public sealed class Message
{
    // Go: JSONRPC JSONRPCVersion `json:"jsonrpc"`
    public JsonRpcVersion JsonRpc;

    // Go: ID *ID `json:"id,omitzero"`
    public Id? Id;

    // Go: Method string `json:"method,omitzero"`
    public string Method = "";

    // Go: Params json.Value `json:"params,omitzero"`
    public byte[]? Params;

    // Go: Result json.Value `json:"result,omitzero"`
    public byte[]? Result;

    // Go: Error *ResponseError `json:"error,omitzero"`
    public ResponseError? Error;

    // Go: func (m *Message) Kind() MessageKind
    public MessageKind Kind()
    {
        if (Id is not null && Method == "")
        {
            return MessageKind.Response;
        }
        if (Id is null)
        {
            return MessageKind.Notification;
        }
        return MessageKind.Request;
    }

    // Go: func (m *Message) IsRequest() bool
    public bool IsRequest()
    {
        return Id is not null && Method != "";
    }

    // Go: func (m *Message) IsNotification() bool
    public bool IsNotification()
    {
        return Id is null && Method != "";
    }

    // Go: func (m *Message) IsResponse() bool
    public bool IsResponse()
    {
        return Id is not null && Method == "";
    }
}

// RequestMessage is a convenience type for creating request/notification messages.
public sealed class RequestMessage
{
    // Go: JSONRPC JSONRPCVersion `json:"jsonrpc"`
    public JsonRpcVersion JsonRpc;

    // Go: ID *ID `json:"id,omitzero"`
    public Id? Id;

    // Go: Method string `json:"method"`
    public string Method = "";

    // Go: Params any `json:"params,omitzero"`
    public object? Params;
}

// ResponseMessage is a convenience type for creating response messages.
public sealed class ResponseMessage
{
    // Go: JSONRPC JSONRPCVersion `json:"jsonrpc"`
    public JsonRpcVersion JsonRpc;

    // Go: ID *ID `json:"id,omitzero"`
    public Id? Id;

    // Go: Result any `json:"result,omitzero"`
    public object? Result;

    // Go: Error *ResponseError `json:"error,omitzero"`
    public ResponseError? Error;
}
