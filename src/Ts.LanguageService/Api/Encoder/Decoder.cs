// Ported from tsgo internal/api/encoder/decoder.go (1:1, hand-translated per plan/06 §9.1;
// the *_generated.go dispatch pair is mirrored in DecoderGenerated.cs — see its header).
//
// PORT-NOTE (UTF-16 position space, plan/05 §8.1): Go decodes the string-data section as a
// single UTF-8 Go string and slices it zero-copy. The C# encoder writes the string-data
// section as UTF-16LE (see Encoder.cs/StringTable.cs header notes), so the decoder turns
// that region into one C# string with Encoding.Unicode up front and slices with Substring
// (one allocation per distinct string; positions are UTF-16 code units end to end).
//
// PORT-NOTE (errors): Go's DecodeSourceFile/DecodeNodes return (node, error) for malformed
// input (bad header, unknown kind). C# throws InvalidOperationException/ArgumentException
// with the same messages (plan/05 §4: (T, error) on the decode boundary is invalid-input,
// not a caller-recoverable result).
//
// PORT-NOTE (arena): Go pre-allocates a []*ast.Node arena for NodeList child slices; C#
// builds each child list in a small List<Node> and materializes it (the GC owns the
// arrays). No behavioral difference.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Api.Encoder;

// astDecoder reconstructs real Node objects from binary-encoded data.
// (decoder.go:13-30)
// Go exports DecodeSourceFile/DecodeNodes at package level (decoder.go); the class is
// public so the round-trip surface mirrors the Go export set (the helper surface below
// it stays internal). We deliberately do NOT use InternalsVisibleTo (port brief).
public sealed partial class AstDecoder
{
    private readonly byte[] raw;
    private readonly uint strTable;
    private readonly uint strData;
    private readonly uint extData;
    private readonly uint nodeOff;
    private readonly int nodeCount;
    private readonly NodeFactory factory;
    private readonly List<int> childBuf = [];
    // Single string covering all string data; substrings via Substring.
    private readonly string allStringData;
    // Results
    private Node?[] nodes = [];
    private NodeList?[] nodeLists = [];

    internal AstDecoder(byte[] data)
    {
        if (data.Length < HeaderOffsets.HeaderSize)
        {
            throw new ArgumentException($"data too short for header: {data.Length} bytes");
        }
        byte version = data[HeaderOffsets.HeaderOffsetMetadata + 3];
        if (version != Protocol.ProtocolVersion)
        {
            throw new InvalidOperationException($"unsupported protocol version {version} (expected {Protocol.ProtocolVersion})");
        }

        strTable = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderOffsets.HeaderOffsetStringOffsets));
        strData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderOffsets.HeaderOffsetStringData));
        extData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderOffsets.HeaderOffsetExtendedData));
        nodeOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderOffsets.HeaderOffsetNodes));

        uint dataLen = (uint)data.Length;

        // Validate that all offsets are within the buffer.
        if (strTable > dataLen || strData > dataLen || extData > dataLen || nodeOff > dataLen)
        {
            throw new ArgumentException($"invalid AST header offsets: offsets exceed data length ({dataLen})");
        }

        // Validate monotonic non-decreasing order of regions.
        if (!(strTable <= strData && strData <= extData && extData <= nodeOff))
        {
            throw new ArgumentException($"invalid AST header offsets: expected strTable <= strData <= extData <= nodeOff (got {strTable}, {strData}, {extData}, {nodeOff})");
        }

        raw = data;
        factory = new NodeFactory();

        nodeCount = (data.Length - (int)nodeOff) / NodeOffsets.NodeSize;

        // Convert the entire string data region to a single string upfront (UTF-16LE;
        // see file header PORT-NOTE), then slice it per string index. The region may hold
        // unpaired surrogates (lone \udc00 escapes survive into node text), which
        // Encoding.Unicode.GetString would replace with U+FFFD — reinterpret the bytes
        // instead so the decode is lossless.
        allStringData = new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(data.AsSpan((int)strData, (int)(nodeOff - strData))));
    }

    // DecodeSourceFile decodes binary-encoded data into a SourceFile. (decoder.go:32-42)
    public static SourceFile DecodeSourceFile(byte[] data)
    {
        Node node = DecodeNodes(data);
        if (node.Kind != SyntaxKind.SourceFile)
        {
            throw new InvalidOperationException($"expected SourceFile root, got {node.Kind}");
        }
        return node.AsSourceFile();
    }

    // DecodeNodes decodes binary-encoded AST data into a tree of Node objects. (decoder.go:44-51)
    public static Node DecodeNodes(byte[] data)
    {
        AstDecoder d = new(data);
        return d.Decode();
    }

    // nodeField reads a uint32 field from node i at the given field offset. (decoder.go:106-109)
    private uint NodeField(int i, int field)
    {
        return ReadLE32(raw, (int)nodeOff + (i * NodeOffsets.NodeSize) + field);
    }

    // (decoder.go:111-116)
    private string GetString(uint idx)
    {
        int offBase = (int)strTable + ((int)idx * 4);
        uint start = ReadLE32(raw, offBase);
        uint end = ReadLE32(raw, offBase + 4);
        return allStringData.Substring((int)start, (int)(end - start));
    }

    // collectChildren returns indices of direct children of node i.
    // The returned list is reused across calls; callers must not retain it.
    // (decoder.go:118-136)
    private List<int> CollectChildren(int i)
    {
        childBuf.Clear();
        if (i + 1 >= nodeCount)
        {
            return childBuf;
        }
        int firstChild = i + 1;
        if (NodeField(firstChild, NodeOffsets.NodeOffsetParent) != (uint)i)
        {
            return childBuf;
        }
        childBuf.Add(firstChild);
        int next = (int)NodeField(firstChild, NodeOffsets.NodeOffsetNext);
        while (next != 0)
        {
            childBuf.Add(next);
            next = (int)NodeField(next, NodeOffsets.NodeOffsetNext);
        }
        return childBuf;
    }

    // (decoder.go:138-180)
    private Node Decode()
    {
        if (nodeCount < 2)
        {
            throw new InvalidOperationException("no nodes to decode");
        }

        nodes = new Node?[nodeCount];
        nodeLists = new NodeList?[nodeCount];

        // Process bottom-up so children exist before parents.
        for (int i = nodeCount - 1; i >= 1; i--)
        {
            uint kind = NodeField(i, NodeOffsets.NodeOffsetKind);
            uint pos = NodeField(i, NodeOffsets.NodeOffsetPos);
            uint end = NodeField(i, NodeOffsets.NodeOffsetEnd);
            uint data = NodeField(i, NodeOffsets.NodeOffsetData);
            List<int> childIndices = CollectChildren(i);

            if (kind == SyntaxKinds.SyntaxKindNodeList)
            {
                var childNodes = new List<Node>(childIndices.Count);
                foreach (int ci in childIndices)
                {
                    if (nodes[ci] != null)
                    {
                        childNodes.Add(nodes[ci]!);
                    }
                }
                NodeList nl = factory.NewNodeList(childNodes.ToArray());
                nl.Loc = new TextRange((int)pos, (int)end);
                nodeLists[i] = nl;
                continue;
            }

            Node node;
            try
            {
                node = CreateNode((SyntaxKind)kind, data, childIndices);
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"at node {i} (kind {(SyntaxKind)kind}): {e.Message}", e);
            }
            node.Loc = new TextRange((int)pos, (int)end);
            node.Flags = (NodeFlags)NodeField(i, NodeOffsets.NodeOffsetFlags);
            nodes[i] = node;
        }

        return nodes[1]!;
    }

    // getModifierList creates a ModifierList from a child index that is a NodeList.
    // (decoder.go:182-191)
    private ModifierList? GetModifierList(int ci)
    {
        NodeList? nl = nodeLists[ci];
        if (nl == null)
        {
            return null;
        }
        ModifierList ml = factory.NewModifierList(nl.Nodes);
        ml.Loc = nl.Loc;
        return ml;
    }

    // childIterator helps walk through children based on a bitmask. (decoder.go:193-219)
    private struct ChildIterator
    {
        private readonly List<int> indices;
        private int pos;

        internal ChildIterator(List<int> indices)
        {
            this.indices = indices;
            pos = 0;
        }

        // next returns the index of the next child, advancing the position.
        internal int Next()
        {
            if (pos >= indices.Count)
            {
                return 0;
            }
            int ci = indices[pos];
            pos++;
            return ci;
        }

        // nextIf returns the index of the next child if the corresponding mask bit is set.
        internal int NextIf(uint mask, uint bit)
        {
            if ((mask & (1u << (int)bit)) == 0)
            {
                return 0;
            }
            return Next();
        }
    }

    // (decoder.go:221-226)
    private Node? NodeAt(int ci)
    {
        if (ci == 0)
        {
            return null;
        }
        return nodes[ci];
    }

    // (decoder.go:228-233)
    private NodeList? NodeListAt(int ci)
    {
        if (ci == 0)
        {
            return null;
        }
        return nodeLists[ci];
    }

    // (decoder.go:235-240)
    private ModifierList? ModifierListAt(int ci)
    {
        if (ci == 0)
        {
            return null;
        }
        return GetModifierList(ci);
    }

    // (decoder.go:242-254) — Go returns (node, error); C# throws on unknown data types
    // (the error legs live in the Create* dispatchers).
    private Node CreateNode(SyntaxKind kind, uint data, List<int> childIndices)
    {
        uint dataType = data & NodeDataMasks.NodeDataTypeMask;
        uint commonData = (data >> 24) & 0x3f;

        switch (dataType)
        {
            case NodeDataTypes.NodeDataTypeString:
                return CreateStringNode(kind, data, commonData);
            case NodeDataTypes.NodeDataTypeExtendedData:
                return CreateExtendedNode(kind, data, childIndices, commonData);
            default:
                return CreateChildrenNode(kind, data, childIndices, commonData);
        }
    }

    // (decoder.go:256-291)
    private Node DecodeExtendedData_SourceFile(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);

        uint textIdx = ReadLE32(raw, extOff);
        uint fileNameIdx = ReadLE32(raw, extOff + 4);
        uint pathIdx = ReadLE32(raw, extOff + 8);
        string text = GetString(textIdx);
        string fileName = GetString(fileNameIdx);
        string path = GetString(pathIdx);

        // Recover parse options from header.
        uint parseOpts = ReadLE32(raw, HeaderOffsets.HeaderOffsetParseOptions);
        var opts = new SourceFileParseOptions
        {
            FileName = fileName,
            Path = new TsPath.Path(path),
            ExternalModuleIndicatorOptions = new ExternalModuleIndicatorOptions
            {
                Jsx = (parseOpts & 1) != 0,
                Force = (parseOpts & 2) != 0,
            },
        };

        // Collect children: first is statements NodeList, second is EndOfFile.
        NodeList? stmts = null;
        Node? endOfFile = null;
        foreach (int ci in childIndices)
        {
            if (NodeField(ci, NodeOffsets.NodeOffsetKind) == SyntaxKinds.SyntaxKindNodeList)
            {
                stmts = NodeListAt(ci);
            }
            else if (nodes[ci] != null && nodes[ci]!.Kind == SyntaxKind.EndOfFile)
            {
                endOfFile = nodes[ci];
            }
        }
        endOfFile ??= factory.NewToken(SyntaxKind.EndOfFile);
        // Go parity (decoder.go:256-290): the decoded SourceFile restores only
        // text/fileName/path/parseOptions/statements/endOfFile — scriptKind,
        // languageVariant, referencedFiles/type/lib directives and the external-module
        // indicator are NOT carried across (Go's factory.NewSourceFile call is the same
        // shape), so decode→re-encode is not byte-identical in those words.
        return factory.NewSourceFile(opts, text, stmts, endOfFile);
    }

    // (decoder.go:293-299)
    private Node DecodeExtendedData_TemplateHead(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint rawTextIdx = ReadLE32(raw, extOff + 4);
        uint flags = ReadLE32(raw, extOff + 8);
        return factory.NewTemplateHead(GetString(textIdx), GetString(rawTextIdx), (TokenFlags)flags);
    }

    // (decoder.go:301-307)
    private Node DecodeExtendedData_TemplateMiddle(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint rawTextIdx = ReadLE32(raw, extOff + 4);
        uint flags = ReadLE32(raw, extOff + 8);
        return factory.NewTemplateMiddle(GetString(textIdx), GetString(rawTextIdx), (TokenFlags)flags);
    }

    // (decoder.go:309-315)
    private Node DecodeExtendedData_TemplateTail(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint rawTextIdx = ReadLE32(raw, extOff + 4);
        uint flags = ReadLE32(raw, extOff + 8);
        return factory.NewTemplateTail(GetString(textIdx), GetString(rawTextIdx), (TokenFlags)flags);
    }

    // (decoder.go:317-322)
    private Node? SingleChild(List<int> childIndices)
    {
        if (childIndices.Count == 0)
        {
            return null;
        }
        return nodes[childIndices[0]];
    }

    // (decoder.go:324-329)
    private NodeList? SingleNodeListChild(List<int> childIndices)
    {
        if (childIndices.Count == 0)
        {
            return null;
        }
        return nodeLists[childIndices[0]];
    }

    // (decoder.go:331-336) — Go returns 0 for out-of-bounds reads; keep that behavior.
    private static uint ReadLE32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            return 0;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    // Hand-written commonData decoding functions. Each extracts the original values
    // from the 6-bit commonData that were packed by the corresponding
    // getNodeCommonData_* function.
    //
    // Go: func decodeNodeCommonData_SyntheticExpression(_ uint8) (any, bool)
    // (decoder.go:342-344).
    private static (object? TypeNode, bool IsSpread) DecodeNodeCommonData_SyntheticExpression(uint _)
    {
        throw new InvalidOperationException("SyntheticExpression should never be decoded");
    }

    // Hand-written extended data decoding functions for literal nodes.

    // (decoder.go:348-353)
    private Node DecodeExtendedData_StringLiteral(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint flags = ReadLE32(raw, extOff + 4);
        return factory.NewStringLiteral(GetString(textIdx), (TokenFlags)flags);
    }

    // (decoder.go:355-360)
    private Node DecodeExtendedData_NumericLiteral(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint flags = ReadLE32(raw, extOff + 4);
        return factory.NewNumericLiteral(GetString(textIdx), (TokenFlags)flags);
    }

    // (decoder.go:362-367)
    private Node DecodeExtendedData_BigIntLiteral(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint flags = ReadLE32(raw, extOff + 4);
        return factory.NewBigIntLiteral(GetString(textIdx), (TokenFlags)flags);
    }

    // (decoder.go:369-374)
    private Node DecodeExtendedData_RegularExpressionLiteral(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint flags = ReadLE32(raw, extOff + 4);
        return factory.NewRegularExpressionLiteral(GetString(textIdx), (TokenFlags)flags);
    }

    // (decoder.go:376-381)
    private Node DecodeExtendedData_NoSubstitutionTemplateLiteral(uint data, List<int> childIndices, uint commonData)
    {
        int extOff = (int)extData + (int)(data & NodeDataMasks.NodeDataStringIndexMask);
        uint textIdx = ReadLE32(raw, extOff);
        uint flags = ReadLE32(raw, extOff + 4);
        return factory.NewNoSubstitutionTemplateLiteral(GetString(textIdx), (TokenFlags)flags);
    }
}
