// Shared helpers for the ported internal/lsp server_*_test.go files.
//
// Go's onServerRequest closures answer workspace/configuration with `[]any{prefs}`,
// which json-marshals the *lsutil.UserPreferences through its MarshalJSONTo
// (UserPreferences.ToJsonElement here).

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Ts.TestUtil.LspTestUtil;

using RequestMessage = global::Ts.LsProto.RequestMessage;
using ResponseMessage = global::Ts.LsProto.ResponseMessage;

namespace Ts.Lsp.Tests;

internal static class ServerTestUtil
{
    // The JSON value `null` (Go: lsproto.Null{}).
    public static JsonElement JsonNull { get; } = JsonDocument.Parse("null").RootElement.Clone();

    public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // Go: lsutil.UserPreferences{IncludeCompletionsForModuleExports: core.TSTrue,
    // IncludeCompletionsForImportStatements: core.TSTrue} on the wire.
    public static JsonElement AutoImportPrefsConfig { get; } = new global::Ts.Ls.UserPreferences
    {
        IncludeCompletionsForModuleExports = global::Ts.Core.Tristate.True,
        IncludeCompletionsForImportStatements = global::Ts.Core.Tristate.True,
    }.ToJsonElement();

    // Go: &lsutil.UserPreferences{} on the wire.
    public static JsonElement EmptyPrefsConfig { get; } = new global::Ts.Ls.UserPreferences().ToJsonElement();

    public static global::Ts.Vfs.IFileSystem WrapMapFS(Dictionary<string, string> files) =>
        global::Ts.Bundled.Bundled.WrapFS(global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(files, false));

    // The common Go onServerRequest shape: answer workspace/configuration with
    // [prefs] (when prefs is non-null), the capability (un)registrations (and,
    // optionally, window/workDoneProgress/create) with null, everything else with
    // the default MethodNotFound.
    public static ServerRequestHandler OnServerRequest(JsonElement? prefs, bool acceptWorkDoneProgressCreate)
    {
        return (CancellationToken _, RequestMessage req) =>
        {
            if (prefs is { } p && req.Method == global::Ts.LsProto.Methods.WorkspaceConfiguration)
            {
                return new ResponseMessage
                {
                    Id = req.Id,
                    Result = Json("[" + p.GetRawText() + "]"),
                };
            }
            if (req.Method == global::Ts.LsProto.Methods.ClientRegisterCapability ||
                req.Method == global::Ts.LsProto.Methods.ClientUnregisterCapability ||
                (acceptWorkDoneProgressCreate && req.Method == global::Ts.LsProto.Methods.WindowWorkDoneProgressCreate))
            {
                return new ResponseMessage
                {
                    Id = req.Id,
                    Result = JsonNull,
                };
            }
            return null;
        };
    }

    public static ServerOptions Options(string cwd, global::Ts.Vfs.IFileSystem fs) => new()
    {
        Err = System.IO.TextWriter.Null,
        Cwd = cwd,
        FS = fs,
        DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
    };

    // Go: SendRequest(InitializeInfo, ...) + SendNotification(InitializedInfo) + <-InitComplete().
    public static void Initialize(LSPClient client, global::Ts.LsProto.ClientCapabilities capabilities)
    {
        var (initMsg, _, ok) = LSPClient.SendRequest(client, global::Ts.LsProto.Infos.InitializeInfo, new global::Ts.LsProto.InitializeParams
        {
            Capabilities = capabilities,
        });
        Xunit.Assert.True(ok && initMsg!.AsResponse().Error is null, "Initialize failed");
        LSPClient.SendNotification(client, global::Ts.LsProto.Infos.InitializedInfo, new global::Ts.LsProto.InitializedParams());
        client.Server.InitComplete().Wait(Xunit.TestContext.Current.CancellationToken);
    }

    public static void DidOpen(LSPClient client, global::Ts.LsProto.DocumentUri uri, string text)
    {
        LSPClient.SendNotification(client, global::Ts.LsProto.Infos.TextDocumentDidOpenInfo, new global::Ts.LsProto.DidOpenTextDocumentParams
        {
            TextDocument = new global::Ts.LsProto.TextDocumentItem { Uri = uri, LanguageId = global::Ts.LsProto.LanguageKind.TypeScript, Text = text },
        });
    }
}
