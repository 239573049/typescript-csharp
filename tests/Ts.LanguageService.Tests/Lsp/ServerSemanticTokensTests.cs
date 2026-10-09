// Ported from tsgo internal/lsp/server_semantictokens_test.go (whole file).
//
// PORT-NOTE: Go's `if !bundled.Embedded { t.Skip(...) }` guards are omitted: Bundled.Embedded
// is `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).

using System;
using System.Collections.Generic;
using Ts.TestUtil.LspTestUtil;
using Xunit;

using LsProtoT = global::Ts.LsProto;

namespace Ts.Lsp.Tests;

public sealed class ServerSemanticTokensTests
{
    // server_semantictokens_test.go:16-93 — TestSemanticTokensCRLF reproduces a crash where semantic tokens panics with
    // "token spans multiple lines" when the editor opens a file with CRLF line endings
    // but the project originally loaded the file from disk with LF line endings.
    //
    // The SourceFile AST keeps positions from the LF text, but the converter's
    // line map is recomputed from the CRLF overlay, causing a mismatch.
    [Fact]
    public void TestSemanticTokensCRLF()
    {
        // Enough lines so the cumulative \r\n vs \n offset difference
        // causes an LF-based position to land on a \r in the CRLF text.
        const string fileOnDisk = "var x\nvar x\nvar x\nvar x\nvar x\nvar x\nconst a = 1\n";
        string fileFromEditor = fileOnDisk.Replace("\n", "\r\n", StringComparison.Ordinal);

        var files = new Dictionary<string, string>
        {
            ["/home/projects/tsconfig.json"] = "{}",
            ["/home/projects/test.ts"] = fileOnDisk,
            ["/home/projects/other.ts"] = "export {}",
        };
        global::Ts.Vfs.IFileSystem fs = ServerTestUtil.WrapMapFS(files);

        var (client, closeClient) = LSPClient.NewLSPClient(
            ServerTestUtil.Options("/home/projects", fs),
            ServerTestUtil.OnServerRequest(null, acceptWorkDoneProgressCreate: false));
        try
        {
            ServerTestUtil.Initialize(client, new LsProtoT.ClientCapabilities
            {
                TextDocument = new LsProtoT.TextDocumentClientCapabilities
                {
                    SemanticTokens = new LsProtoT.SemanticTokensClientCapabilities
                    {
                        Requests = new LsProtoT.ClientSemanticTokensRequestOptions
                        {
                            Full = new LsProtoT.BooleanOrClientSemanticTokensRequestFullDelta { Boolean = true },
                        },
                        TokenTypes = ["namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator", "decorator"],
                        TokenModifiers = ["declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary", "local"],
                        Formats = [],
                    },
                },
            });

            // Open another project file to force the project to load test.ts from disk (LF).
            var otherUri = new LsProtoT.DocumentUri("file:///home/projects/other.ts");
            ServerTestUtil.DidOpen(client, otherUri, files["/home/projects/other.ts"]);
            var (msg1, _, _) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentSemanticTokensFullInfo, new LsProtoT.SemanticTokensParams
            {
                TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = otherUri },
            });
            Assert.True(msg1!.AsResponse().Error is null, "Initial request failed");

            // Open test.ts with CRLF content; the project already parsed it from disk (LF).
            var uri = new LsProtoT.DocumentUri("file:///home/projects/test.ts");
            ServerTestUtil.DidOpen(client, uri, fileFromEditor);

            // This panics: AST positions are LF-based but the line map is CRLF-based.
            var (msg, _, _) = LSPClient.SendRequest(client, LsProtoT.Infos.TextDocumentSemanticTokensFullInfo, new LsProtoT.SemanticTokensParams
            {
                TextDocument = new LsProtoT.TextDocumentIdentifier { Uri = uri },
            });
            if (msg!.AsResponse().Error is { } error)
            {
                Assert.Fail($"Semantic tokens request failed: {error.Message}");
            }
        }
        finally
        {
            _ = closeClient();
        }
    }
}
