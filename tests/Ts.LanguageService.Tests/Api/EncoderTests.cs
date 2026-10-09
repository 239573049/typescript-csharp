// Ported from tsgo internal/api/encoder/encoder_test.go.
//
// PORT-NOTE(api): the two Go baseline subtests (encodeSourceFile.txt /
// encodeSourceFileWithUnicodeEscapes.txt, plan/08 baselines) assert against the diff
// baseline infrastructure, which lands with the conformance wave (plan/08 §3); here they
// assert the wire-format invariants the baseline files pin down instead (header layout,
// version, section ordering, root record, string-table reuse) plus a full
// encode → decode → re-encode byte-equality round trip. The benchmarks need the
// _submodules/TypeScript corpus (repo.SkipIfNoTypeScriptSubmodule) and are not ported.

using System;
using Ts.Ast;
using Ts.Core;
using Xunit;
// The namespace segment Ts.Api.Encoder shadows the class name Encoder at this nesting
// level, so bind the entry-point types through aliases (call sites keep the Go form).

namespace Ts.Api.Tests;

public class EncoderTests
{
    private static SourceFile ParseTs(string code)
    {
        return global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions
            {
                FileName = "/test.ts",
                Path = new global::Ts.TsPath.Path("/test.ts"),
            },
            code,
            ScriptKind.TS);
    }

    private static uint ReadUint32(byte[] buf, int offset)
    {
        return BitConverter.ToUInt32(buf, offset);
    }

    // ── TestEncodeSourceFile (baseline arm → wire-format invariants) ────────────────────────

    [Fact]
    public void EncodeSourceFile_HeaderAndRoot()
    {
        SourceFile sourceFile = ParseTs("import { bar } from \"bar\";\nexport function foo<T, U>(a: string, b: string): any {}\nfoo();");
        (byte[] buf, Ts.Api.Encoder.Encoder.NodeIndexTable table) = Ts.Api.Encoder.Encoder.EncodeSourceFile(sourceFile);

        Assert.True(buf.Length >= Ts.Api.Encoder.HeaderOffsets.HeaderSize);

        // Version in the top byte of the metadata word.
        uint metadata = ReadUint32(buf, Ts.Api.Encoder.HeaderOffsets.HeaderOffsetMetadata);
        Assert.Equal((uint)Ts.Api.Encoder.Protocol.ProtocolVersion << 24, metadata & 0xFF00_0000u);

        // Sections appear in this order and in range: string offsets < string data <
        // extended data < structured data < nodes.
        int offsetStringOffsets = (int)ReadUint32(buf, Ts.Api.Encoder.HeaderOffsets.HeaderOffsetStringOffsets);
        int offsetStrings = (int)ReadUint32(buf, Ts.Api.Encoder.HeaderOffsets.HeaderOffsetStringData);
        int offsetExtended = (int)ReadUint32(buf, Ts.Api.Encoder.HeaderOffsets.HeaderOffsetExtendedData);
        int offsetStructured = (int)ReadUint32(buf, Ts.Api.Encoder.HeaderOffsets.HeaderOffsetStructuredData);
        int offsetNodes = (int)ReadUint32(buf, Ts.Api.Encoder.HeaderOffsets.HeaderOffsetNodes);
        Assert.Equal(Ts.Api.Encoder.HeaderOffsets.HeaderSize, offsetStringOffsets);
        Assert.True(offsetStrings >= offsetStringOffsets);
        Assert.True(offsetExtended >= offsetStrings);
        Assert.True(offsetStructured >= offsetExtended);
        Assert.True(offsetNodes >= offsetStructured);
        Assert.Equal((buf.Length - offsetNodes) / Ts.Api.Encoder.NodeOffsets.NodeSize * Ts.Api.Encoder.NodeOffsets.NodeSize, buf.Length - offsetNodes);

        // Index 0 is the nil sentinel (all zero); index 1 is the root SourceFile.
        Assert.Equal(0u, ReadUint32(buf, offsetNodes));
        Assert.Equal((uint)SyntaxKind.SourceFile, ReadUint32(buf, offsetNodes + Ts.Api.Encoder.NodeOffsets.NodeSize + Ts.Api.Encoder.NodeOffsets.NodeOffsetKind));

        // Every node table entry maps back to a node of the encoded file.
        Assert.Equal(sourceFile.AsNode(), table.Nodes[1]);
    }

    // ── TestEncodeSourceFileWithUnicodeEscapes (baseline arm → round trip) ───────────────────

    [Fact]
    public void EncodeSourceFileWithUnicodeEscapes_RoundTrip()
    {
        SourceFile sourceFile = ParseTs("let a = \"😃\"; let b = \"\\ud83d\\ude03\"; let c = \"\\udc00\\ud83d\\ude03\"; let d = \"\\ud83d\\ud83d\\ude03\"");
        byte[] buf = Ts.Api.Encoder.Encoder.EncodeSourceFile(sourceFile).Data;

        // Lone surrogates must survive the round trip: decode and re-encode to identical
        // bytes (the Go baseline pins this byte-for-byte).
        SourceFile decoded = Ts.Api.Encoder.AstDecoder.DecodeSourceFile(buf);
        Assert.Equal(sourceFile.Text(), decoded.Text());
        // Go parity (decoder.go:256-290): the decoder deliberately does not carry
        // scriptKind/languageVariant across (real consumers re-derive them from the
        // parse pipeline), so restore them before re-encoding to pin the lossless parts.
        decoded.ScriptKind = sourceFile.ScriptKind;
        decoded.LanguageVariant = sourceFile.LanguageVariant;
        byte[] reencoded = Ts.Api.Encoder.Encoder.EncodeSourceFile(decoded).Data;
        Assert.Equal(buf, reencoded);
    }

    // ── TestBuildNodeIndexTableMatchesEncode ─────────────────────────────────────────────────

    [Fact]
    public void BuildNodeIndexTableMatchesEncode()
    {
        SourceFile sourceFile = ParseTs("import { bar } from \"bar\";\nexport function foo<T, U>(a: string, b: string): any {}\nfoo();");

        Ts.Api.Encoder.Encoder.NodeIndexTable encodeTable = Ts.Api.Encoder.Encoder.EncodeSourceFile(sourceFile).Table;
        Ts.Api.Encoder.Encoder.NodeIndexTable buildTable = Ts.Api.Encoder.Encoder.BuildNodeIndexTable(sourceFile);

        // Both tables should produce identical Nodes slices.
        Assert.Equal(encodeTable.Nodes.Length, buildTable.Nodes.Length);

        // Every index should map to the same node.
        for (int i = 0; i < encodeTable.Nodes.Length; i++)
        {
            Assert.Equal(encodeTable.Nodes[i], buildTable.Nodes[i]);
        }

        // GetIndex on both tables should agree for every non-nil node.
        for (int i = 0; i < encodeTable.Nodes.Length; i++)
        {
            Node? node = encodeTable.Nodes[i];
            if (node == null)
            {
                continue;
            }
            uint encIdx = encodeTable.GetIndex(node);
            uint buildIdx = buildTable.GetIndex(node);
            Assert.Equal((uint)i, encIdx);
            Assert.Equal(encIdx, buildIdx);
        }
    }

    // ── GetNodeIndexTable caching (encoder.go:391-393 via ConditionalWeakTable) ──────────────

    [Fact]
    public void GetNodeIndexTable_IsCachedPerSourceFile()
    {
        SourceFile sourceFile = ParseTs("let x = 1;");
        Ts.Api.Encoder.Encoder.NodeIndexTable first = Ts.Api.Encoder.Encoder.GetNodeIndexTable(sourceFile);
        Ts.Api.Encoder.Encoder.NodeIndexTable second = Ts.Api.Encoder.Encoder.GetNodeIndexTable(sourceFile);
        Assert.Same(first, second);
        // And it is the same table EncodeSourceFile installs.
        Assert.Same(first, Ts.Api.Encoder.Encoder.EncodeSourceFile(sourceFile).Table);
    }
}
