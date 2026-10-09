// Ported from tsgo internal/lsp/replay_test.go (whole file).
//
// PORT-NOTE: Go's test flags (-replay, -testDir, -simple, -superSimple) map to the
// environment variables TSCS_REPLAY, TSCS_REPLAY_TESTDIR, TSCS_REPLAY_SIMPLE and
// TSCS_REPLAY_SUPERSIMPLE (the TSCS_* test-knob convention; `dotnet test` has no
// per-package flag surface). Without TSCS_REPLAY the test skips, exactly like Go.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using Id = global::Ts.JsonRpc.Id;
using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ReplayTests
{
    private static readonly string? replay = Environment.GetEnvironmentVariable("TSCS_REPLAY");
    private static readonly string? testDir = Environment.GetEnvironmentVariable("TSCS_REPLAY_TESTDIR");
    private static readonly bool simple = Environment.GetEnvironmentVariable("TSCS_REPLAY_SIMPLE") is "1" or "true";
    private static readonly bool superSimple = Environment.GetEnvironmentVariable("TSCS_REPLAY_SUPERSIMPLE") is "1" or "true";

    // replay_test.go:34-38 — rawMessage.
    private sealed class RawMessage
    {
        public string Kind = "";
        public string Method = "";
        public JsonElement? Params;
    }

    // replay_test.go:40-217 — TestReplay.
    [Fact]
    public void TestReplay()
    {
        if (string.IsNullOrEmpty(replay))
        {
            Assert.Skip("no replay file specified");
        }
        if (string.IsNullOrEmpty(testDir))
        {
            Assert.Fail("testDir must be specified");
        }
        LsProtoT.DocumentUri testDirUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(testDir);

        global::Ts.Vfs.IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(global::Ts.Vfs.OsVfs.OsVfsUtil.FS());
        string defaultLibraryPath = global::Ts.Bundled.Bundled.LibPath();
        string typingsLocation = global::Ts.Vfs.OsVfs.OsVfsUtil.GetGlobalTypingsCacheLocation();
        var serverOpts = new ServerOptions
        {
            Err = Console.Error,
            Cwd = global::Ts.TsPath.TsPath.NormalizeSlashes(Directory.GetCurrentDirectory()),
            FS = fs,
            DefaultLibraryPath = defaultLibraryPath,
            TypingsLocation = typingsLocation,
            NpmInstall = NpmInstall,
        };

        var (client, closeClient) = LSPClient.NewLSPClient(serverOpts, null);
        try
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(replay);
            }
            catch (IOException e)
            {
                Assert.Fail($"failed to read replay file: {e.Message}");
                return;
            }

            if (lines.Length == 0)
            {
                Assert.Fail("replay file is empty");
            }

            string rootDirPlaceholder = "@PROJECT_ROOT@";
            string rootDirUriPlaceholder = "@PROJECT_ROOT_URI@";
            string firstLine = lines[0];
            try
            {
                using JsonDocument initObj = JsonDocument.Parse(firstLine);
                if (initObj.RootElement.TryGetProperty("rootDirPlaceholder", out JsonElement placeholder) && placeholder.GetString() is { Length: > 0 } p)
                {
                    rootDirPlaceholder = p;
                }
                if (initObj.RootElement.TryGetProperty("rootDirUriPlaceholder", out JsonElement uriPlaceholder) && uriPlaceholder.GetString() is { Length: > 0 } up)
                {
                    rootDirUriPlaceholder = up;
                }
            }
            catch (JsonException e)
            {
                Assert.Fail($"failed to parse initial arguments: {e.Message}");
            }

            var messages = new List<RawMessage>();
            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                string line = ReplaceRootDir(lines[lineIndex], rootDirPlaceholder, testDir, rootDirUriPlaceholder, testDirUri.Value);
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    messages.Add(new RawMessage
                    {
                        Kind = root.TryGetProperty("kind", out JsonElement kind) ? kind.GetString() ?? "" : "",
                        Method = root.TryGetProperty("method", out JsonElement method) ? method.GetString() ?? "" : "",
                        Params = root.TryGetProperty("params", out JsonElement @params) ? @params.Clone() : null,
                    });
                }
                catch (JsonException e)
                {
                    Assert.Fail($"failed to parse message: {e.Message}");
                }
            }

            if (simple)
            {
                // Include only initialization, file opening/changing/closing, and shutdown messages, plus the final request.
                var newMessages = new List<RawMessage>();
                int i;
                for (i = 0; i < messages.Count && IsInitializationMessage(messages[i]); i++)
                {
                    newMessages.Add(messages[i]);
                }
                int j;
                for (j = messages.Count - 1; j >= 0 && IsExitMessage(messages[j]); j--)
                {
                }
                for (int k = i; k <= j; k++)
                {
                    RawMessage msg = messages[k];
                    if (msg.Method is "textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")
                    {
                        newMessages.Add(msg);
                    }
                }
                for (int k = Math.Max(i, j); k < messages.Count; k++)
                {
                    newMessages.Add(messages[k]);
                }
                messages = newMessages;
            }
            else if (superSimple)
            {
                // Include only initialization, shutdown, the last file open and the final request.
                // We assume here the final request will be for the file that was opened last.
                var newMessages = new List<RawMessage>();
                int i;
                for (i = 0; i < messages.Count && IsInitializationMessage(messages[i]); i++)
                {
                    newMessages.Add(messages[i]);
                }

                int j;
                for (j = messages.Count - 1; j >= 0 && IsExitMessage(messages[j]); j--)
                {
                }
                int openIdx;
                for (openIdx = j; openIdx >= i; openIdx--)
                {
                    RawMessage msg = messages[openIdx];
                    if (msg.Method == "textDocument/didOpen")
                    {
                        newMessages.Add(msg);
                        break;
                    }
                }
                for (int k = Math.Max(openIdx + 1, j); k < messages.Count; k++)
                {
                    newMessages.Add(messages[k]);
                }
                messages = newMessages;
            }

            foreach (RawMessage rawMsg in messages)
            {
                global::Ts.JsonRpc.MessageKind kind;
                Id? reqID = null;
                switch (rawMsg.Kind)
                {
                    case "request":
                        kind = global::Ts.JsonRpc.MessageKind.Request;
                        reqID = LsProtoT.LsProtoNewId.NewId(LsProtoT.IntegerOrString.FromInteger(client.NextID()));
                        break;
                    case "notification":
                        kind = global::Ts.JsonRpc.MessageKind.Notification;
                        break;
                    default:
                        Assert.Fail($"unknown message kind: {rawMsg.Kind}");
                        return;
                }

                // Go: marshal {jsonrpc, id, method, params} and unmarshal it into an lsproto.Message.
                byte[] rpcData = MarshalRpcMessage(reqID, rawMsg);
                LsProtoT.Message msg;
                try
                {
                    msg = LsProtoT.Message.Parse(rpcData);
                }
                catch (LsProtoT.LspErrorException e)
                {
                    Assert.Fail($"failed to unmarshal rpc message into lsproto.Message: {e.Message}");
                    return;
                }

                switch (kind)
                {
                    case global::Ts.JsonRpc.MessageKind.Request:
                        var (response, ok) = client.SendRequestWorker(msg.AsRequest(), reqID!);
                        if (!ok)
                        {
                            Assert.Fail($"failed to send request for method {rawMsg.Method}");
                        }
                        if (response!.Error is not null)
                        {
                            Assert.Fail($"server returned error for method {rawMsg.Method} params {rawMsg.Params?.GetRawText()}:\n{response.Error}");
                        }
                        break;
                    case global::Ts.JsonRpc.MessageKind.Notification:
                        client.WriteMsg(msg);
                        break;
                    default:
                        Assert.Fail($"unknown message kind: {rawMsg.Kind}");
                        break;
                }
            }
        }
        finally
        {
            Exception? err = closeClient();
            if (err is not null)
            {
                Assert.Fail($"goroutine error: {err.Message}");
            }
        }
    }

    // Go: strings.NewReplacer(rootDirPlaceholder, testDir, rootDirUriPlaceholder, testDirUri)
    // — a single left-to-right pass; at each position the earlier-listed pattern wins.
    private static string ReplaceRootDir(string line, string placeholder, string dir, string uriPlaceholder, string dirUri)
    {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (placeholder.Length > 0 && string.CompareOrdinal(line, i, placeholder, 0, placeholder.Length) == 0)
            {
                sb.Append(dir);
                i += placeholder.Length;
            }
            else if (uriPlaceholder.Length > 0 && string.CompareOrdinal(line, i, uriPlaceholder, 0, uriPlaceholder.Length) == 0)
            {
                sb.Append(dirUri);
                i += uriPlaceholder.Length;
            }
            else
            {
                sb.Append(line[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static byte[] MarshalRpcMessage(Id? reqID, RawMessage rawMsg)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            // Go writes `"id": null` for notifications, which decodes to a nil ID; the
            // C# writer omits the member, which decodes identically.
            if (reqID is not null)
            {
                json.WriteNumber("id", reqID.IntValue);
            }
            json.WriteString("method", rawMsg.Method);
            if (rawMsg.Params is { } @params)
            {
                json.WritePropertyName("params");
                @params.WriteTo(json);
            }
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    // cmd/tsgo/lsp.go NpmInstall (replay_test.go:60-64).
    private static (byte[] Output, Exception? Error) NpmInstall(string cwd, string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("npm")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return (output.ToArray(), new InvalidOperationException($"exit status {process.ExitCode}"));
            }
            return (output.ToArray(), null);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return ([], e);
        }
    }

    // replay_test.go:219-221 — isInitializationMessage.
    private static bool IsInitializationMessage(RawMessage msg)
    {
        return msg.Method is "initialize" or "initialized";
    }

    // replay_test.go:223-225 — isExitMessage.
    private static bool IsExitMessage(RawMessage msg)
    {
        return msg.Method is "exit" or "shutdown";
    }
}
