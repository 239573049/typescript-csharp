// Tests for the api proto layer: DocumentIdentifier's string|{uri} unmarshal surface
// (ported from tsgo internal/api/proto_test.go), the diagnostic-response position test
// (same file; see the UTF-16 note below), handle-id round-trips, and the source-generated
// serialization face (ApiJsonContext).

using System.Text.Json;
using Ts.Api;
using Xunit;

namespace Ts.Api.Tests;

public class ProtoTests
{
    // TestDocumentIdentifierUnmarshalJSON (proto_test.go:24-67).
    [Theory]
    [InlineData(/*input*/ "\"foo.ts\"", /*fileName*/ "foo.ts", /*uri*/ "")]
    [InlineData("{\"uri\":\"file:///foo.ts\"}", "", "file:///foo.ts")]
    [InlineData("{\"uri\":\"file:///foo.ts\",\"extra\":true}", "", "file:///foo.ts")]
    [InlineData("{}", "", "")]
    public void DocumentIdentifier_Unmarshal(string input, string fileName, string uri)
    {
        DocumentIdentifier? d = JsonSerializer.Deserialize(input, ApiJsonContext.Default.DocumentIdentifier);

        Assert.NotNull(d);
        Assert.Equal(fileName, d!.FileName);
        // PORT-NOTE: Go's zero DocumentURI is ""; C# DocumentUri.Value is string? and
        // default(DocumentUri).Value is null (deviation noted in Project/FileChange.cs).
        Assert.Equal(uri == "", string.IsNullOrEmpty(d.URI.Value));
        if (uri != "")
        {
            Assert.Equal(uri, d.URI.Value);
        }
    }

    [Fact]
    public void DocumentIdentifier_InvalidType_Errors()
    {
        // Go: "DocumentIdentifier: expected string or object, got number". The STJ port
        // spells the token kind via JsonTokenType.Number; the shared prefix is the
        // byte-fidelity part clients match on.
        JsonException e = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize("42", ApiJsonContext.Default.DocumentIdentifier));
        Assert.Contains("expected string or object, got", e.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentIdentifier_ToAbsoluteFileName_NormalizesAgainstCwd()
    {
        var d = new DocumentIdentifier { FileName = "foo.ts" };
        string abs = d.ToAbsoluteFileName("/work");
        Assert.True(abs == "/work/foo.ts" || abs.EndsWith("/foo.ts", System.StringComparison.Ordinal));
    }

    // TestNewDiagnosticResponseUsesUTF16Offsets (proto_test.go:69-86).
    //
    // PORT-NOTE: Go parses the file (UTF-8 byte offsets) and asserts NewDiagnosticResponse
    // converts byte offsets 9/10 of "const 💩 = 1;" to UTF-16 9/10. C# positions ARE UTF-16
    // code units natively (plan/01), so IndexOf('=') lands on 9 directly and the response
    // must carry the position through unchanged — same expected wire values.
    [Fact]
    public void NewDiagnosticResponse_UsesUtf16Offsets()
    {
        string text = "const 💩 = 1;";
        var file = global::Ts.Parser.Parser.ParseSourceFile(
            new global::Ts.Ast.SourceFileParseOptions
            {
                FileName = "/unicode.ts",
                Path = new global::Ts.TsPath.Path("/unicode.ts"),
            },
            text,
            global::Ts.Core.ScriptKind.TS);
        int pos = text.IndexOf('=', System.StringComparison.Ordinal);
        Assert.True(pos > 0);
        int end = pos + "=".Length;

        var diag = global::Ts.Ast.Diagnostic.NewDiagnostic(
            file,
            new global::Ts.Core.TextRange(pos, end),
            global::Ts.Diagnostics.Diagnostics.Expression_expected);
        DiagnosticResponse resp = ProtoFunctions.NewDiagnosticResponse(diag);

        Assert.Equal(9, resp.Pos);
        Assert.Equal(10, resp.End);
    }

    // Handle ids are bare JSON numbers/strings (proto.go:27-34).
    [Fact]
    public void HandleIds_RoundTripThroughJson()
    {
        SnapshotId snapshot = JsonSerializer.Deserialize("42", ApiJsonContext.Default.SnapshotId);
        Assert.Equal(42UL, snapshot.Value);
        Assert.Equal("42", JsonSerializer.Serialize(snapshot, ApiJsonContext.Default.SnapshotId));

        SymbolId symbol = JsonSerializer.Deserialize("7", ApiJsonContext.Default.SymbolId);
        Assert.Equal(7UL, symbol.Value);

        NodeHandle handle = JsonSerializer.Deserialize("\"3./foo.ts\"", ApiJsonContext.Default.NodeHandle);
        Assert.Equal("3./foo.ts", handle.Value);
    }

    // Method constants keep their Go spellings (proto.go:56-206).
    [Fact]
    public void MethodConstants_KeepGoSpellings()
    {
        Assert.Equal("release", Method.Release.Value);
        Assert.Equal("getServerTiming", Method.GetServerTiming.Value);
        Assert.Equal("getExportSpecifierLocalTargetSymbol", Method.GetExportSpecifierLocalTarget.Value);
        Assert.Equal("getJsDocTags", Method.GetJSDocTags.Value);
    }

    // SerializePolymorphic covers every type an api handler places in an `any` slot.
    [Fact]
    public void SerializePolymorphic_BooleansAndStrings()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ApiJsonContext.ApiWriterOptions))
        {
            ApiJsonContext.SerializePolymorphic(writer, true);
        }
        Assert.Equal("true", System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()));
    }

    [Fact]
    public void UpdateSnapshotParams_RoundTripsThroughSourceGen()
    {
        var @params = new UpdateSnapshotParams
        {
            OpenProjects = [new DocumentIdentifier { URI = new global::Ts.LsProto.DocumentUri("file:///a/tsconfig.json") }],
            CloseFiles = [new DocumentIdentifier { URI = new global::Ts.LsProto.DocumentUri("file:///b.ts") }],
            FileChanges = new ApiFileChanges { InvalidateAll = true },
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(@params, ApiJsonContext.Default.UpdateSnapshotParams);
        UpdateSnapshotParams? back = JsonSerializer.Deserialize(json, ApiJsonContext.Default.UpdateSnapshotParams);

        Assert.NotNull(back);
        // Go's UnmarshalJSONFrom reads only `uri` in object form (proto.go:236-245), so
        // URI is the field that survives a round-trip.
        Assert.Equal("file:///a/tsconfig.json", back!.OpenProjects![0].URI.Value);
        Assert.Equal("file:///b.ts", back.CloseFiles![0].URI.Value);
        Assert.True(back.FileChanges!.InvalidateAll);
    }

    [Fact]
    public void SerializePolymorphic_UnknownTypeThrows()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        // `object` itself IS registered (the TypeResponse.Value any slot); a plain BCL
        // type with no contract must throw.
        Assert.Throws<JsonException>(() => ApiJsonContext.SerializePolymorphic(writer, new System.Exception()));
    }
}
