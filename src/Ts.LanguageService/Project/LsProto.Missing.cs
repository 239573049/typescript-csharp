// Residual project-side helpers over the generated tsgo internal/lsp/lsproto surface
// (Lsp/Protocol*.g.cs). The protocol subset that used to be hosted here (WatchKind,
// FileChangeType, FileSystemWatcher, FileEvent, Resolved*ClientCapabilities, telemetry
// events) is now consumed from Ts.LsProto directly; only the two pieces below remain.

namespace Ts.Project;

// lsp.go:293 — GetClientCapabilities reads the resolved capabilities from the request
// context. plan/01 §4.4 fixed the C# RequestContext shape (no context values), so the
// session-scoped capabilities travel on SessionOptions instead (identical semantics:
// immutable for the server lifetime). This helper is the single access point so the
// Go call shape stays recognizable.
public static class ClientCapabilitiesAccess
{
    public static global::Ts.LsProto.ResolvedClientCapabilities GetClientCapabilities(SessionOptions options) =>
        options.ClientCapabilities;
}

// lsp.go:52-55 — DocumentUri.Path.
public static class DocumentUriPathExtensions
{
    public static global::Ts.TsPath.Path Path(this global::Ts.LsProto.DocumentUri uri, bool useCaseSensitiveFileNames)
    {
        string fileName = global::Ts.LsProto.DocumentUriExtensions.FileName(uri);
        return global::Ts.TsPath.TsPath.ToPath(fileName, "", useCaseSensitiveFileNames);
    }
}
