using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Ts.Lsp.Tests;

public sealed class ServerReplayTests
{
    [Fact]
    public void StdioReplayInitializeOpenDiagnosticsAndHover()
    {
        const string source = "const x: number = \"oops\";\nx;\n";
        var escapedSource = source
            .Replace("\\", "\\\\", System.StringComparison.Ordinal)
            .Replace("\"", "\\\"", System.StringComparison.Ordinal)
            .Replace("\n", "\\n", System.StringComparison.Ordinal);
        using var input = new MemoryStream();
        var inputWriter = new global::Ts.JsonRpc.Writer(input);
        inputWriter.Write(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":null,\"rootUri\":null,\"capabilities\":{}}}"));
        inputWriter.Write(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///src/main.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"" + escapedSource + "\"}}}"));
        inputWriter.Write(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///src/main.ts\"},\"position\":{\"line\":0,\"character\":6}}}"));
        input.Position = 0;

        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var server = new global::Ts.Lsp.Server(new global::Ts.Lsp.ServerOptions
        {
            In = input,
            Out = output,
            Err = errors,
            Cwd = "/src",
            DefaultLibraryPath = "/lib",
        });

        var exitCode = server.Run(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("", errors.ToString());
        var messages = ReadMessages(output);

        var initialize = Assert.Single(messages, message =>
            message.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1);
        var capabilities = initialize.RootElement.GetProperty("result").GetProperty("capabilities");
        Assert.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString());
        Assert.True(capabilities.GetProperty("textDocumentSync").GetProperty("openClose").GetBoolean());
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());

        var publish = Assert.Single(messages, message =>
            message.RootElement.TryGetProperty("method", out var method) &&
            string.Equals(method.GetString(), "textDocument/publishDiagnostics", System.StringComparison.Ordinal));
        var diagnostics = publish.RootElement.GetProperty("params").GetProperty("diagnostics");
        Assert.Contains(diagnostics.EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetInt32() == 2322 &&
            diagnostic.GetProperty("message").GetString()!.Contains("not assignable", System.StringComparison.Ordinal));

        var hover = Assert.Single(messages, message =>
            message.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 2);
        Assert.True(hover.RootElement.TryGetProperty("result", out var hoverResult));
        Assert.True(hoverResult.ValueKind is JsonValueKind.Null or JsonValueKind.Object);

        foreach (var message in messages)
        {
            message.Dispose();
        }
    }

    [Fact]
    public void IncrementalDidChangeAppliesUtf16RangeBeforeRechecking()
    {
        // The emoji occupies two UTF-16 code units, so the numeric literal starts at
        // LSP character 34 (but Unicode-scalar character 33). This catches accidental
        // rune/code-point indexing in the didChange overlay path.
        const string source = "const s = \"😀\"; const n: number = 1;\n";
        var escapedSource = source
            .Replace("\\", "\\\\", System.StringComparison.Ordinal)
            .Replace("\"", "\\\"", System.StringComparison.Ordinal)
            .Replace("\n", "\\n", System.StringComparison.Ordinal);
        using var input = new MemoryStream();
        var inputWriter = new global::Ts.JsonRpc.Writer(input);
        inputWriter.Write(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":null,\"rootUri\":null,\"capabilities\":{}}}"));
        inputWriter.Write(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///src/change.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"" + escapedSource + "\"}}}"));
        inputWriter.Write(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"file:///src/change.ts\",\"version\":2},\"contentChanges\":[{\"range\":{\"start\":{\"line\":0,\"character\":34},\"end\":{\"line\":0,\"character\":35}},\"rangeLength\":1,\"text\":\"\\\"bad\\\"\"}]}}"));
        input.Position = 0;

        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var server = new global::Ts.Lsp.Server(new global::Ts.Lsp.ServerOptions
        {
            In = input,
            Out = output,
            Err = errors,
            Cwd = "/src",
            DefaultLibraryPath = "/lib",
        });

        Assert.Equal(0, server.Run(TestContext.Current.CancellationToken));
        Assert.Equal("", errors.ToString());
        var messages = ReadMessages(output);
        JsonDocument? changedDiagnostics = null;
        foreach (var message in messages)
        {
            if (message.RootElement.TryGetProperty("method", out var method) &&
                string.Equals(method.GetString(), "textDocument/publishDiagnostics", System.StringComparison.Ordinal) &&
                message.RootElement.GetProperty("params").GetProperty("version").GetInt32() == 2)
            {
                changedDiagnostics = message;
            }
        }

        Assert.NotNull(changedDiagnostics);
        Assert.Contains(
            changedDiagnostics.RootElement.GetProperty("params").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetInt32() == 2322);

        foreach (var message in messages)
        {
            message.Dispose();
        }
    }

    private static List<JsonDocument> ReadMessages(MemoryStream output)
    {
        output.Position = 0;
        var reader = new global::Ts.JsonRpc.Reader(output);
        var messages = new List<JsonDocument>();
        while (true)
        {
            var (data, ok) = reader.Read();
            if (!ok)
            {
                return messages;
            }
            messages.Add(JsonDocument.Parse(data!));
        }
    }
}
