// Ported from tsgo internal/api/protocol.go (whole file).

using Ts.JsonRpc;

namespace Ts.Api;

// protocol.go:8 — Message is an alias for jsonrpc.Message for convenience.
// In C# the alias collapses to a using at use sites; Ts.JsonRpc.Message is the type.

// protocol.go:11-22 — Protocol defines the interface for reading and writing API messages.
//
// PORT-NOTE: Go returns `error` from every method. Per plan/05 §4 the error reaches the
// conn boundary where it is either propagated to the Run loop (read errors) or rendered
// into a JSON-RPC error response (write failures, which Go escalates via panic at the
// caller). C# realizes those paths with thrown exceptions: read-side errors throw
// (MsgPackException / JsonRpcException / IOException), and write failures throw
// IOException-derived exceptions that the conn's panic-style handling converts exactly
// like Go's `panic(fmt.Sprintf("api: failed to write ..."))`.
public interface Protocol
{
    // ReadMessage reads the next message from the connection.
    // Returns (message, ok): ok == false means clean EOF (Go io.EOF).
    (Message? Message, bool Ok) ReadMessage();

    // WriteRequest writes a request message.
    void WriteRequest(global::Ts.JsonRpc.Id? id, string method, object? @params);

    // WriteNotification writes a notification message (no ID).
    void WriteNotification(string method, object? @params);

    // WriteResponse writes a successful response.
    void WriteResponse(global::Ts.JsonRpc.Id? id, object? result);

    // WriteError writes an error response.
    void WriteError(global::Ts.JsonRpc.Id? id, global::Ts.JsonRpc.ResponseError? err);
}
