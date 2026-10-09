// Ported from tsgo internal/api/conn.go (whole file).

using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using System;
using System.Text.Json;

namespace Ts.Api;

// Go sentinel errors (conn.go:10-13). Ported as message constants carried by
// thrown exceptions (plan/05 §4.3); nothing in the package branches on them.
public static class ApiErrors
{
    public const string ErrConnClosed = "api: connection closed";
    public const string ErrRequestTimeout = "api: request timeout";
}

// ApiRequestException realizes Go's `(any, error)` return from Handler.HandleRequest:
// the handler signals an error by throwing, and the connection renders err.Error()
// into a JSON-RPC error response with CodeInternalError (plan/05 §4.3).
public sealed class ApiRequestException : Exception
{
    public ApiRequestException(string message) : base(message)
    {
    }
}

// Handler processes incoming API requests and notifications.
// conn.go:15-21.
public interface Handler
{
    // HandleRequest handles an incoming request and returns a result or error.
    // PORT-NOTE: Go returns (any, error); the error return is realized as a thrown
    // ApiRequestException (error text must reach the wire verbatim), while runtime
    // failures surface as ordinary exceptions — the connection's catch-all reproduces
    // Go's recover() at the same boundary and formats the panic-style message.
    object? HandleRequest(global::Ts.Core.RequestContext ctx, string method, byte[]? @params);

    // HandleNotification handles an incoming notification.
    void HandleNotification(global::Ts.Core.RequestContext ctx, string method, byte[]? @params);
}

// Conn represents a bidirectional connection for API communication.
// conn.go:23-34.
public interface Conn
{
    // Run starts processing messages on the connection.
    // It blocks until the context is cancelled or an error occurs.
    Exception? Run(global::Ts.Core.RequestContext ctx);

    // Call sends a request to the client and waits for a response.
    // PORT-NOTE: Go returns (json.Value, error); C# returns the raw response JSON bytes
    // (json.Value ≅ byte[]) and throws ApiRequestException for remote errors
    // ("api: remote error [code]: message", text preserved from conn_async.go:220).
    byte[]? Call(global::Ts.Core.RequestContext ctx, string method, object? @params);

    // Notify sends a notification to the client (no response expected).
    void Notify(global::Ts.Core.RequestContext ctx, string method, object? @params);
}

public static class ConnHelpers
{
    // conn.go:36-46 — UnmarshalParams is a helper to unmarshal params into a typed
    // struct. Empty params deserialize to the zero value (Go returns nil, nil); parse
    // failures throw JsonException (Go returns the error for the caller to wrap).
    public static T? UnmarshalParams<T>(byte[]? @params, JsonTypeInfo<T> typeInfo)
    {
        if (@params is null || @params.Length == 0)
        {
            return default;
        }
        return System.Text.Json.JsonSerializer.Deserialize(@params, typeInfo);
    }
}
