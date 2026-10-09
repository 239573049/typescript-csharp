// Ported from tsgo internal/api/callbackfs.go (whole file).
//
// PORT-NOTE: Go vfs.FS → Ts.Vfs.IFileSystem. Go's ReadFile(path) (contents, ok) maps to
// IFileSystem.TryReadFile(path, out contents) (plan/05 §4.1). Response payloads are raw
// JSON bytes, so the tiny wrapper shapes ({"content": ...}, entries, string) are parsed
// with a handwritten Utf8JsonReader (plan/07 §4.3 #3: shape trivial → no context).

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ts.Api;

// callbackFS wraps a base filesystem and delegates certain operations
// to the client via RPC callbacks. This allows the API client to provide
// a virtual filesystem (e.g., in-memory files for testing).
//
// The callbacks to enable are specified at construction time via the
// --callbacks CLI flag. The connection is set via SetConnection after
// the transport connection is established.
// (callbackfs.go:12-26)
public sealed class CallbackFs : global::Ts.Vfs.IFileSystem
{
    private readonly global::Ts.Vfs.IFileSystem @base;

    // Go: enabledCallbacks map[string]bool. (callbackfs.go:21)
    private readonly Dictionary<string, bool> enabledCallbacks = [];

    // conn and ctx are set after connection is established (callbackfs.go:23-25).
    private Conn? conn;
    private global::Ts.Core.RequestContext ctx;

    // Callback names that can be enabled (callbackfs.go:29-35).
    public const string CallbackReadFile = "readFile";
    public const string CallbackFileExists = "fileExists";
    public const string CallbackDirectoryExists = "directoryExists";
    public const string CallbackGetAccessibleEntries = "getAccessibleEntries";
    public const string CallbackRealpath = "realpath";

    // isCallbackName (callbackfs.go:37-48).
    public static bool IsCallbackName(string name)
    {
        return name switch
        {
            CallbackReadFile or
            CallbackFileExists or
            CallbackDirectoryExists or
            CallbackGetAccessibleEntries or
            CallbackRealpath => true,
            _ => false,
        };
    }

    // newCallbackFS creates a new callbackFS wrapping the given base filesystem.
    // The callbacks slice specifies which filesystem operations should be delegated
    // to the client (e.g., "readFile", "fileExists"). (callbackfs.go:50-65)
    public CallbackFs(global::Ts.Vfs.IFileSystem @base, string[] callbacks)
    {
        this.@base = @base;
        foreach (string cb in callbacks)
        {
            if (!IsCallbackName(cb))
            {
                // Go: panic("unknown callback name: " + cb).
                throw new InvalidOperationException("unknown callback name: " + cb);
            }
            enabledCallbacks[cb] = true;
        }
    }

    // SetConnection sets the RPC connection for callbacks.
    // This must be called after the transport connection is established
    // but before any filesystem operations that need callbacks.
    // (callbackfs.go:67-73)
    public void SetConnection(global::Ts.Core.RequestContext ctx, Conn conn)
    {
        this.ctx = ctx;
        this.conn = conn;
    }

    // isEnabled returns true if the named callback is enabled. (callbackfs.go:75-78)
    private bool IsEnabled(string name)
    {
        return enabledCallbacks.TryGetValue(name, out bool enabled) && enabled;
    }

    // call invokes a callback on the client and returns the result.
    // (callbackfs.go:80-91) PORT-NOTE: Go returns ([]byte, error) and callers panic on
    // error; C# throws and the callers' catch sites are identical panics.
    private byte[]? Call(string name, string arg)
    {
        if (conn is null)
        {
            throw new InvalidOperationException($"CallbackFS: {name} called before connection set");
        }

        return conn.Call(ctx, name, arg);
    }

    // UseCaseSensitiveFileNames implements vfs.FS. (callbackfs.go:93-96)
    public bool UseCaseSensitiveFileNames()
    {
        return @base.UseCaseSensitiveFileNames();
    }

    // ReadFile implements vfs.FS.
    //
    // The readFile callback uses a wrapped response format to distinguish three states:
    //   - undefined (fall back to real FS): null or empty on wire
    //   - null (not found, no fallback): {"content": null}
    //   - string content: {"content": "..."}
    // (callbackfs.go:98-124) — Go (contents, ok) → TryReadFile(out contents).
    public bool TryReadFile(string path, out string contents)
    {
        if (IsEnabled(CallbackReadFile))
        {
            byte[]? result = Call(CallbackReadFile, path);
            if (result is { Length: > 0 } raw && System.Text.Encoding.UTF8.GetString(raw) != "null")
            {
                string? content = ParseContentWrapper(raw);
                if (content is null)
                {
                    contents = "";
                    return false;
                }
                contents = content;
                return true;
            }
        }
        return @base.TryReadFile(path, out contents);
    }

    private static string? ParseContentWrapper(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("callbackfs: expected object");
        }
        string? content = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            if (key == "content")
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    content = reader.GetString();
                }
                else if (reader.TokenType != JsonTokenType.Null)
                {
                    throw new JsonException("callbackfs: content must be a string or null");
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return content;
    }

    // FileExists implements vfs.FS. (callbackfs.go:126-138)
    public bool FileExists(string path)
    {
        if (IsEnabled(CallbackFileExists))
        {
            byte[]? result = Call(CallbackFileExists, path);
            if (result is { Length: > 0 } raw && System.Text.Encoding.UTF8.GetString(raw) != "null")
            {
                return System.Text.Encoding.UTF8.GetString(raw) == "true";
            }
        }
        return @base.FileExists(path);
    }

    // DirectoryExists implements vfs.FS. (callbackfs.go:140-152)
    public bool DirectoryExists(string path)
    {
        if (IsEnabled(CallbackDirectoryExists))
        {
            byte[]? result = Call(CallbackDirectoryExists, path);
            if (result is { Length: > 0 } raw && System.Text.Encoding.UTF8.GetString(raw) != "null")
            {
                return System.Text.Encoding.UTF8.GetString(raw) == "true";
            }
        }
        return @base.DirectoryExists(path);
    }

    // GetAccessibleEntries implements vfs.FS. (callbackfs.go:154-178)
    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        if (IsEnabled(CallbackGetAccessibleEntries))
        {
            byte[]? result = Call(CallbackGetAccessibleEntries, path);
            if (result is { Length: > 0 } raw)
            {
                (global::Ts.Vfs.Entries? entries, bool ok) = ParseEntries(raw);
                if (ok && entries is not null)
                {
                    return entries.Value;
                }
            }
        }
        return @base.GetAccessibleEntries(path);
    }

    private static (global::Ts.Vfs.Entries? Entries, bool Ok) ParseEntries(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("callbackfs: expected object");
        }
        string[]? files = null;
        string[]? directories = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            if (key == "files" || key == "directories")
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    continue;
                }
                // The value token is already current (StartArray) — do not read past it.
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new JsonException("callbackfs: expected array");
                }
                var list = new System.Collections.Generic.List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    list.Add(reader.GetString() ?? "");
                }
                if (key == "files")
                {
                    files = [.. list];
                }
                else
                {
                    directories = [.. list];
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return (new global::Ts.Vfs.Entries { Files = files, Directories = directories }, true);
    }

    // Realpath implements vfs.FS. (callbackfs.go:180-196)
    public string Realpath(string path)
    {
        if (IsEnabled(CallbackRealpath))
        {
            byte[]? result = Call(CallbackRealpath, path);
            if (result is { Length: > 0 } raw && System.Text.Encoding.UTF8.GetString(raw) != "null")
            {
                return ParseJsonString(raw);
            }
        }
        return @base.Realpath(path);
    }

    private static string ParseJsonString(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("callbackfs: expected string");
        }
        return reader.GetString() ?? "";
    }

    // WriteFile implements vfs.FS - always delegates to base (no callback support).
    // (callbackfs.go:198-201) PORT-NOTE: Go returns error; C# throws IOException-family
    // exceptions from the base implementation (plan/05 §4.1).
    public void WriteFile(string path, string data)
    {
        @base.WriteFile(path, data);
    }

    // AppendFile implements vfs.FS - always delegates to base (no callback support).
    // (callbackfs.go:203-206)
    public void AppendFile(string path, string data)
    {
        @base.AppendFile(path, data);
    }

    // Remove implements vfs.FS - always delegates to base (no callback support).
    // (callbackfs.go:208-211)
    public void Remove(string path)
    {
        @base.Remove(path);
    }

    // Chtimes implements vfs.FS - always delegates to base (no callback support).
    // (callbackfs.go:213-216)
    public void Chtimes(string path, System.DateTime aTime, System.DateTime mTime)
    {
        @base.Chtimes(path, aTime, mTime);
    }

    // Stat implements vfs.FS - always delegates to base (no callback support).
    // (callbackfs.go:218-221)
    public global::Ts.Vfs.IFileInfo? Stat(string path)
    {
        return @base.Stat(path);
    }

    // WalkDir implements vfs.FS - always delegates to base (no callback support).
    // (callbackfs.go:223-226)
    public Exception? WalkDir(string root, global::Ts.Vfs.WalkDirFunc walkFn)
    {
        return @base.WalkDir(root, walkFn);
    }
}
