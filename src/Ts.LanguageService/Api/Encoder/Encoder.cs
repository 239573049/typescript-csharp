// Ported from tsgo internal/api/encoder/encoder.go (1:1, hand-translated per plan/06 §9.1:
// encoder.go/decoder.go/stringtable.go are hand-translated; the *_generated.go pair is
// mirrored in EncoderGenerated.cs/DecoderGenerated.cs pending the tools/generator
// bootstrap — plan/04 §8 names the .g.cs targets for when that lands).
//
// PORT-NOTE (UTF-16 position space, plan/05 §8.1): Go's encoder converts node positions
// (UTF-8 byte offsets) to UTF-16 via ast.PositionMap. The C# port deleted PositionMap —
// positions ARE UTF-16 code units — so `Utf16` below is the identity and the string-data
// section is UTF-16LE with code-unit offsets (see StringTable.cs). The header layout,
// node record layout, msgpack structured data (UTF-8 strings), and all packing rules are
// byte-layout-compatible with Go.
//
// PORT-NOTE (errors): Go's EncodeSourceFile/EncodeNode return (data, table, error) where
// the error is statically always nil; the C# port drops the error leg (plan/05 §4).

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using Ts.Ast;
using Ts.Core;

namespace Ts.Api.Encoder;

// Go: init() (encoder.go:15-19).
static class EncoderInit
{
    static EncoderInit()
    {
        if ((int)SyntaxKind.LastUnaryOperator > 0x3f)
        {
            throw new InvalidOperationException($"KindLastUnaryOperator ({(int)SyntaxKind.LastUnaryOperator}) exceeds the 6-bit commonData capacity (max 63)");
        }
    }

    // Touching the type runs the static ctor (Go: package init).
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Ensure() { }
}

// encoder.go:21-31 — node record field offsets (uint32 units of 4 bytes each) and NodeSize.
public static class NodeOffsets
{
    public const int NodeOffsetKind = 0 * 4;
    public const int NodeOffsetPos = 1 * 4;
    public const int NodeOffsetEnd = 2 * 4;
    public const int NodeOffsetNext = 3 * 4;
    public const int NodeOffsetParent = 4 * 4;
    public const int NodeOffsetData = 5 * 4;
    public const int NodeOffsetFlags = 6 * 4;

    // NodeSize is the number of bytes that represents a single node in the encoded format.
    public const int NodeSize = 7 * 4;
    public const int WordsPerNode = NodeSize / 4;
}

// encoder.go:33-37.
public static class NodeDataTypes
{
    public const uint NodeDataTypeChildren = 0u << 30;
    public const uint NodeDataTypeString = 1u << 30;
    public const uint NodeDataTypeExtendedData = 2u << 30;
}

// encoder.go:39-43.
public static class NodeDataMasks
{
    public const uint NodeDataTypeMask = 0xc0_00_00_00;
    public const uint NodeDataChildMask = 0x00_00_00_ff;
    public const uint NodeDataStringIndexMask = 0x00_ff_ff_ff;
}

// encoder.go:45-47.
public static class SyntaxKinds
{
    public const uint SyntaxKindNodeList = (1u << 32) - 1;
}

// encoder.go:49-62 — header field offsets.
public static class HeaderOffsets
{
    public const int HeaderOffsetMetadata = 0 * 4;
    public const int HeaderOffsetHashLo0 = 1 * 4;
    public const int HeaderOffsetHashLo1 = 2 * 4;
    public const int HeaderOffsetHashHi0 = 3 * 4;
    public const int HeaderOffsetHashHi1 = 4 * 4;
    public const int HeaderOffsetParseOptions = 5 * 4;
    public const int HeaderOffsetStringOffsets = 6 * 4;
    public const int HeaderOffsetStringData = 7 * 4;
    public const int HeaderOffsetExtendedData = 8 * 4;
    public const int HeaderOffsetStructuredData = 9 * 4;
    public const int HeaderOffsetNodes = 10 * 4;
    public const int HeaderSize = 11 * 4;
}

// encoder.go:64-66.
public static class Protocol
{
    public const byte ProtocolVersion = 5;
}

// encoder.go:68-284 — the Source File Binary Format specification (Go comment, kept).
//
// The following defines a protocol for serializing TypeScript SourceFile objects to a
// compact binary format. All integer values are little-endian.
//
// The format comprises six sections:
//
//   - Header             44 bytes            content hash, parse options, flags, section offsets
//   - String offsets     8 bytes per string  pairs of start/end offsets into the string data section
//   - String data        variable            string data (UTF-16LE in this port; see PORT-NOTE above)
//   - Extended node data variable            extra data for some kinds of nodes
//   - Structured data    variable            msgpack-encoded metadata blobs (e.g. file references)
//   - Nodes              28 bytes per node   the AST structure of the file
//
// Header (44 bytes):
//
//   0      uint8   protocol version; 1-3 reserved
//   4-19   uint128 source file content hash (LE)
//   20-23  uint32  parse options (bit 0: JSX, bit 1: Force)
//   24-27  uint32  byte offset to string offsets section
//   28-31  uint32  byte offset to string data section
//   32-35  uint32  byte offset to extended node data section
//   36-39  uint32  byte offset to structured data section
//   40-43  uint32  byte offset to nodes section
//
// Nodes are represented in a flat array in source order; the first 28 bytes are zeros
// (nil node). NodeLists are normal nodes with kind 0xff_ff_ff_ff; their data field is the
// list length. Otherwise the node data field's top 2 bits select Children (0b00: low byte
// is the child-property presence bitmask) / String (0b01: 24-bit string-offsets index) /
// Extended (0b10: 24-bit extended-data offset); bits 24-29 carry per-kind common data
// (boolean flags, or the full operator SyntaxKind for unary expressions). See encoder.go
// for the full per-kind tables.

// SourceFileHash returns the 128-bit content hash for a source file as a hex string.
// (encoder.go:287-290)
public static class Encoder
{
    static Encoder()
    {
        EncoderInit.Ensure();
    }

    public static string SourceFileHash(SourceFile sourceFile)
    {
        UInt128 h = sourceFile.Hash;
        ulong hi = (ulong)(h >> 64);
        ulong lo = (ulong)h;
        return hi.ToString("x16", CultureInfo.InvariantCulture) + lo.ToString("x16", CultureInfo.InvariantCulture);
    }

    // encodeParseOptions encodes the per-file ExternalModuleIndicatorOptions as a uint32 bitmask.
    // (encoder.go:293-302)
    internal static uint EncodeParseOptions(ExternalModuleIndicatorOptions opts)
    {
        uint bits = 0;
        if (opts.Jsx)
        {
            bits |= 1;
        }
        if (opts.Force)
        {
            bits |= 2;
        }
        return bits;
    }

    // NodeIndexTable maps between AST nodes and their encoder indices for O(1) node handle resolution.
    // (encoder.go:305-309)
    public sealed class NodeIndexTable
    {
        public Node?[] Nodes; // index → node (for resolution); NodeLists occupy nil slots

        // Go: sortedOnce sync.Once; sortedIdx []uint32 — indices into Nodes, sorted by
        // node ID; built lazily. C#: lock + double-check mirrors sync.Once.
        private readonly object sortedLock = new();
        private uint[]? sortedIdx;

        internal NodeIndexTable(Node?[] nodes)
        {
            Nodes = nodes;
        }

        // GetIndex returns the encoder index for the given node.
        // On the first call the sortedIdx array is built (O(n log n) sort on a flat []uint32),
        // then subsequent calls use binary search (O(log n)). This turns out to be much faster than
        // building a map[*ast.Node]uint32 and not significantly slower for lookups.
        // (encoder.go:317-339)
        public uint GetIndex(Node node)
        {
            if (sortedIdx == null)
            {
                lock (sortedLock)
                {
                    if (sortedIdx == null)
                    {
                        var idx = new List<uint>(Nodes.Length);
                        for (int i = 0; i < Nodes.Length; i++)
                        {
                            if (Nodes[i] != null)
                            {
                                idx.Add((uint)i);
                            }
                        }
                        Node?[] nodes = Nodes;
                        idx.Sort((a, b) => AstUtilities.GetNodeId(nodes[(int)a]!).Value.CompareTo(AstUtilities.GetNodeId(nodes[(int)b]!).Value));
                        sortedIdx = [.. idx];
                    }
                }
            }
            ulong target = AstUtilities.GetNodeId(node).Value;
            (int foundIdx, bool found) = Core.Core.BinarySearchUniqueFunc(sortedIdx.AsSpan(), (int _, uint el) => AstUtilities.GetNodeId(Nodes[(int)el]!).Value.CompareTo(target));
            if (found)
            {
                return sortedIdx[foundIdx];
            }
            return 0;
        }
    }

    // Go: var nodeIndexTableKey = ast.NewSourceFileDataKey[*NodeIndexTable]() (encoder.go:311).
    // PORT-NOTE: the ast.SourceFileDataKey external-lazy-data mechanism is deferred in the
    // C# port (SourceFile.cs SCOPE note), so the per-file cache is a ConditionalWeakTable
    // here (AOT-safe, no reflection): first writer wins, lifetime tied to the SourceFile.
    private static readonly ConditionalWeakTable<SourceFile, NodeIndexTable> nodeIndexTables = new();

    // BuildNodeIndexTable walks the AST in the same order as encodeTree and builds
    // a NodeIndexTable without performing the full binary encoding. This is used to
    // eagerly create index tables for files that need node handles before getSourceFile
    // is called. The indices produced are guaranteed to match those from EncodeSourceFile.
    // (encoder.go:345-389)
    public static NodeIndexTable BuildNodeIndexTable(SourceFile sourceFile)
    {
        uint nodeCount = 0;
        var nodeTable = new List<Node?>(sourceFile.NodeCount + 1)
        {
            null, // index 0 = nil sentinel
        };

        NodeVisitor visitor = null!;
        visitor = new NodeVisitor(
            node =>
            {
                nodeCount++;
                nodeTable.Add(node);
                visitor.VisitEachChild(node);
                // Go: for _, jsdoc := range node.JSDoc(sourceFile) — nil range = no iterations.
                foreach (Node jsdoc in node.JSDoc(sourceFile) ?? [])
                {
                    visitor.Visit!(jsdoc);
                }
                return node;
            },
            null,
            new NodeVisitorHooks
            {
                VisitNodes = (nodeList, v) =>
                {
                    if (nodeList == null)
                    {
                        return nodeList;
                    }
                    nodeCount++;
                    nodeTable.Add(null); // NodeLists are not *ast.Node
                    v.VisitSlice(nodeList.Nodes);
                    return nodeList;
                },
                VisitModifiers = (modifiers, v) =>
                {
                    if (modifiers != null && modifiers.Nodes.Length > 0)
                    {
                        _ = v.Hooks.VisitNodes!(modifiers, v);
                    }
                    return modifiers;
                },
            });

        Node rootNode = sourceFile.AsNode();
        // Index 1 = root node (matches encodeTree)
        nodeCount++;
        nodeTable.Add(rootNode);

        visitor.VisitEachChild(rootNode);
        foreach (Node jsdoc in rootNode.JSDoc(sourceFile) ?? [])
        {
            visitor.Visit!(jsdoc);
        }

        return new NodeIndexTable([.. nodeTable]);
    }

    // (encoder.go:391-393)
    public static NodeIndexTable GetNodeIndexTable(SourceFile sourceFile)
    {
        return nodeIndexTables.GetValue(sourceFile, static sf => BuildNodeIndexTable(sf));
    }

    // EncodeSourceFile encodes an entire source file AST into the binary format.
    // Returns the encoded bytes and a NodeIndexTable mapping encoder indices to AST nodes.
    // (encoder.go:397-406)
    public static (byte[] Data, NodeIndexTable Table) EncodeSourceFile(SourceFile sourceFile)
    {
        (byte[] data, NodeIndexTable nodeTable) = EncodeTree(sourceFile.AsNode(), sourceFile);
        NodeIndexTable cached = nodeIndexTables.GetValue(sourceFile, _ => nodeTable);
        return (data, cached);
    }

    // EncodeNode encodes an arbitrary AST node and its descendants into the binary format.
    // The sourceFile is needed to provide the source text for efficient string encoding.
    // When encoding a non-SourceFile node, the header hash and parse options fields will be zero.
    // Returns the encoded bytes and a NodeIndexTable mapping encoder indices to AST nodes.
    // (encoder.go:412-414)
    public static (byte[] Data, NodeIndexTable Table) EncodeNode(Node node, SourceFile? sourceFile)
    {
        return EncodeTree(node, sourceFile);
    }

    // (encoder.go:416-620)
    private static (byte[] Data, NodeIndexTable Table) EncodeTree(Node rootNode, SourceFile? sourceFile)
    {
        uint parentIndex = 0, nodeCount = 0, prevIndex = 0;
        var extendedData = new List<uint>();
        var structuredData = new List<byte>();
        StringTable strs;
        // Go: positionMap := sourceFile.GetPositionMap(); utf16 := positionMap.UTF8ToUTF16 —
        // identity here; positions are already UTF-16 code units (plan/05 §8.1).
        static uint Utf16(int pos)
        {
            return (uint)pos;
        }

        if (rootNode.Kind == SyntaxKind.SourceFile)
        {
            strs = new StringTable(sourceFile!.Text(), sourceFile.TextCount);
        }
        else
        {
            strs = new StringTable("", 0);
        }

        int initialNodeCount = sourceFile != null ? sourceFile.NodeCount : 0;
        var nodes = new List<uint>((initialNodeCount + 1) * NodeOffsets.WordsPerNode);

        // Build node index table for O(1) handle resolution.
        // Index 0 is a nil sentinel; real nodes start at index 1.
        var nodeTable = new List<Node?>(initialNodeCount + 1)
        {
            null, // index 0 = nil sentinel
        };

        // Build a small map of nodes we need to track indices for (imports + moduleAugmentations).
        // Values start at 0 and are filled in during the walk.
        Dictionary<Node, uint>? nodeIndexMap = null;
        int sfExtendedDataOffset; // word offset in extendedData where SourceFile fields start
        if (rootNode.Kind == SyntaxKind.SourceFile)
        {
            SourceFile sf = rootNode.AsSourceFile();
            int total = (sf.Imports()?.Length ?? 0) + (sf.ModuleAugmentations?.Length ?? 0);
            if (sf.ExternalModuleIndicator != null && sf.ExternalModuleIndicator != rootNode)
            {
                total++;
            }
            if (total > 0)
            {
                nodeIndexMap = new Dictionary<Node, uint>(total);
                // map-iter: 序无关 —— 仅作 membership 建表，不依赖迭代序。
                foreach (Node imp in sf.Imports() ?? [])
                {
                    nodeIndexMap[imp] = 0;
                }
                foreach (Node aug in sf.ModuleAugmentations ?? [])
                {
                    nodeIndexMap[aug] = 0;
                }
                if (sf.ExternalModuleIndicator != null && sf.ExternalModuleIndicator != rootNode)
                {
                    nodeIndexMap[sf.ExternalModuleIndicator] = 0;
                }
            }
        }

        NodeVisitor visitor = null!;
        visitor = new NodeVisitor(
            node =>
            {
                nodeCount++;
                nodeTable.Add(node);
                if (prevIndex != 0)
                {
                    // this is the next sibling of `prevNode`
                    nodes[(int)(prevIndex * NodeOffsets.WordsPerNode + NodeOffsets.NodeOffsetNext / 4)] = nodeCount;
                }

                AppendUint32s(nodes, (uint)node.Kind, Utf16(node.Pos), Utf16(node.End), 0, parentIndex, GetNodeData(node, strs, extendedData, structuredData), (uint)node.Flags);

                if (nodeIndexMap != null && nodeIndexMap.ContainsKey(node))
                {
                    nodeIndexMap[node] = nodeCount;
                }

                uint saveParentIndex = parentIndex;

                uint currentIndex = nodeCount;
                prevIndex = 0;
                parentIndex = currentIndex;
                visitor.VisitEachChild(node);
                if (sourceFile != null)
                {
                    foreach (Node jsdoc in node.JSDoc(sourceFile) ?? [])
                    {
                        visitor.Visit!(jsdoc);
                    }
                }
                prevIndex = currentIndex;
                parentIndex = saveParentIndex;
                return node;
            },
            null,
            new NodeVisitorHooks
            {
                VisitNodes = (nodeList, v) =>
                {
                    if (nodeList == null)
                    {
                        return nodeList;
                    }

                    nodeCount++;
                    nodeTable.Add(null); // NodeLists are not *ast.Node
                    if (prevIndex != 0)
                    {
                        // this is the next sibling of `prevNode`
                        nodes[(int)(prevIndex * NodeOffsets.WordsPerNode + NodeOffsets.NodeOffsetNext / 4)] = nodeCount;
                    }

                    AppendUint32s(nodes, SyntaxKinds.SyntaxKindNodeList, Utf16(nodeList.Pos), Utf16(nodeList.End), 0, parentIndex, (uint)nodeList.Nodes.Length, 0);

                    uint saveParentIndex = parentIndex;

                    uint currentIndex = nodeCount;
                    prevIndex = 0;
                    parentIndex = currentIndex;
                    v.VisitSlice(nodeList.Nodes);
                    prevIndex = currentIndex;
                    parentIndex = saveParentIndex;

                    return nodeList;
                },
                VisitModifiers = (modifiers, v) =>
                {
                    if (modifiers != null && modifiers.Nodes.Length > 0)
                    {
                        _ = v.Hooks.VisitNodes!(modifiers, v);
                    }
                    return modifiers;
                },
            });

        AppendUint32s(nodes, 0, 0, 0, 0, 0, 0, 0); // nil-sentinel node record

        nodeCount++;
        parentIndex++;
        nodeTable.Add(rootNode); // index 1 = root node

        sfExtendedDataOffset = extendedData.Count;
        AppendUint32s(nodes, (uint)rootNode.Kind, Utf16(rootNode.Pos), Utf16(rootNode.End), 0, 0, GetNodeData(rootNode, strs, extendedData, structuredData), (uint)rootNode.Flags);

        visitor.VisitEachChild(rootNode);
        if (sourceFile != null)
        {
            foreach (Node jsdoc in rootNode.JSDoc(sourceFile) ?? [])
            {
                visitor.Visit!(jsdoc);
            }
        }

        UInt128 hash = UInt128.Zero;
        uint parseOpts = 0;
        if (rootNode.Kind == SyntaxKind.SourceFile)
        {
            hash = sourceFile!.Hash;
            parseOpts = EncodeParseOptions(sourceFile.ParseOptions().ExternalModuleIndicatorOptions);

            // Encode imports, moduleAugmentations, and ambientModuleNames into structured data,
            // and patch the placeholder offsets in the SourceFile extended data.
            SourceFile sf = rootNode.AsSourceFile();
            uint importsOffset = EncodeNodeIndexArray(sf.Imports(), nodeIndexMap, structuredData);
            uint moduleAugmentationsOffset = EncodeModuleAugmentations(sf.ModuleAugmentations, nodeIndexMap, structuredData);
            uint ambientModuleNamesOffset = EncodeStringArray(sf.AmbientModuleNames, structuredData);
            // Patch the 3 placeholder uint32s at sfExtendedDataOffset + 32, 36, 40 (words +8/+9/+10)
            extendedData[sfExtendedDataOffset + 8] = importsOffset;
            extendedData[sfExtendedDataOffset + 9] = moduleAugmentationsOffset;
            extendedData[sfExtendedDataOffset + 10] = ambientModuleNamesOffset;
            // Patch externalModuleIndicator node index at offset 44 (word +11)
            uint externalModuleIndicatorIndex = 0;
            if (sf.ExternalModuleIndicator != null)
            {
                if (sf.ExternalModuleIndicator == rootNode)
                {
                    externalModuleIndicatorIndex = 1; // root node index
                }
                else
                {
                    externalModuleIndicatorIndex = nodeIndexMap != null && nodeIndexMap.TryGetValue(sf.ExternalModuleIndicator, out uint v) ? v : 0;
                }
            }
            extendedData[sfExtendedDataOffset + 11] = externalModuleIndicatorIndex;
        }

        uint metadata = (uint)Protocol.ProtocolVersion << 24;
        int offsetStringTableOffsets = HeaderOffsets.HeaderSize;
        int offsetStringTableData = HeaderOffsets.HeaderSize + strs.EncodedLength() - strs.StringLength();
        int offsetExtendedData = offsetStringTableData + strs.StringLength();
        int offsetStructuredData = offsetExtendedData + extendedData.Count * 4;
        int offsetNodes = offsetStructuredData + structuredData.Count;

        ulong hashLo = (ulong)hash;
        ulong hashHi = (ulong)(hash >> 64);

        uint[] header =
        [
            metadata,
            (uint)hashLo, (uint)(hashLo >> 32),
            (uint)hashHi, (uint)(hashHi >> 32),
            parseOpts,
            (uint)offsetStringTableOffsets,
            (uint)offsetStringTableData,
            (uint)offsetExtendedData,
            (uint)offsetStructuredData,
            (uint)offsetNodes,
        ];

        byte[] result = new byte[offsetNodes + nodes.Count * 4];
        int o = 0;
        foreach (uint word in header)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(o), word);
            o += 4;
        }
        // string offsets + string data (Go: headerBytes + strsBytes via slices.Concat).
        byte[] strsBytes = strs.Encode();
        Buffer.BlockCopy(strsBytes, 0, result, offsetStringTableOffsets, strsBytes.Length);
        // extended data (Go: extendedData bytes).
        o = offsetExtendedData;
        foreach (uint word in extendedData)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(o), word);
            o += 4;
        }
        // structured data bytes.
        structuredData.CopyTo(result, offsetStructuredData);
        // node words.
        o = offsetNodes;
        foreach (uint word in nodes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(o), word);
            o += 4;
        }

        return (result, new NodeIndexTable([.. nodeTable]));
    }

    // (encoder.go:622-627)
    private static void AppendUint32s(List<uint> buf, params uint[] values)
    {
        foreach (uint value in values)
        {
            buf.Add(value);
        }
    }

    // (encoder.go:629-641)
    private static uint GetNodeData(Node node, StringTable strs, List<uint> extendedData, List<byte> structuredData)
    {
        uint t = EncoderGenerated.GetNodeDataType(node);
        switch (t)
        {
            case NodeDataTypes.NodeDataTypeChildren:
                return t | EncoderGenerated.GetNodeCommonData(node) | EncoderGenerated.GetChildrenPropertyMask(node);
            case NodeDataTypes.NodeDataTypeString:
                return t | EncoderGenerated.GetNodeCommonData(node) | EncoderGenerated.RecordNodeStrings(node, strs);
            case NodeDataTypes.NodeDataTypeExtendedData:
                return t | EncoderGenerated.GetNodeCommonData(node) | EncoderGenerated.RecordExtendedData(node, strs, extendedData, structuredData);
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    // (encoder.go:643)
    internal const uint NoStructuredData = 0xFFFFFFFF;

    // (encoder.go:645-656)
    internal static void RecordExtendedData_SourceFile(Node node, StringTable strs, List<uint> extendedData, List<byte> structuredData)
    {
        SourceFile sf = node.AsSourceFile();
        uint textIndex = strs.Add(sf.Text(), sf.Kind, sf.Pos, sf.End);
        uint fileNameIndex = strs.Add(sf.FileName(), (SyntaxKind)0, 0, 0);
        uint pathIndex = strs.Add(sf.Path().Value, (SyntaxKind)0, 0, 0);
        uint referencedFilesOffset = EncodeFileReferences(sf.ReferencedFiles, structuredData);
        uint typeRefDirectivesOffset = EncodeFileReferences(sf.TypeReferenceDirectives, structuredData);
        uint libRefDirectivesOffset = EncodeFileReferences(sf.LibReferenceDirectives, structuredData);
        // imports, moduleAugmentations, ambientModuleNames offsets are placeholders;
        // they will be patched after the tree walk when node indices are known.
        AppendUint32s(extendedData, textIndex, fileNameIndex, pathIndex, (uint)sf.LanguageVariant, (uint)sf.ScriptKind, referencedFilesOffset, typeRefDirectivesOffset, libRefDirectivesOffset, NoStructuredData, NoStructuredData, NoStructuredData, 0);
    }

    // (encoder.go:658-663)
    internal static void RecordExtendedData_TemplateHead(Node node, StringTable strs, List<uint> extendedData)
    {
        TemplateHead n = node.AsTemplateHead();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        uint rawTextIndex = strs.Add(n.RawText, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, rawTextIndex, (uint)n.TemplateFlags);
    }

    // (encoder.go:665-670)
    internal static void RecordExtendedData_TemplateMiddle(Node node, StringTable strs, List<uint> extendedData)
    {
        TemplateMiddle n = node.AsTemplateMiddle();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        uint rawTextIndex = strs.Add(n.RawText, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, rawTextIndex, (uint)n.TemplateFlags);
    }

    // (encoder.go:672-677)
    internal static void RecordExtendedData_TemplateTail(Node node, StringTable strs, List<uint> extendedData)
    {
        TemplateTail n = node.AsTemplateTail();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        uint rawTextIndex = strs.Add(n.RawText, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, rawTextIndex, (uint)n.TemplateFlags);
    }

    // (encoder.go:679-684) — Go returns byte; C# returns uint so the shift expressions
    // below stay in uint arithmetic (Go's untyped constants promote naturally).
    internal static uint BoolToByte(bool b)
    {
        return b ? 1u : 0u;
    }

    // hasModifiers returns true if the modifier list is non-nil and has at least one modifier.
    // (encoder.go:687-689)
    internal static bool HasModifiers(ModifierList? modifiers)
    {
        return modifiers != null && modifiers.Nodes.Length > 0;
    }

    // encodeFileReferences encodes a slice of FileReferences as a msgpack array of tuples
    // into the structured data buffer. Returns the byte offset into the buffer, or
    // noStructuredData (0xFFFFFFFF) if the slice is empty.
    // (encoder.go:694-710)
    private static uint EncodeFileReferences(FileReference[]? refs, List<byte> buf)
    {
        if (refs == null || refs.Length == 0)
        {
            return NoStructuredData;
        }
        uint offset = (uint)buf.Count;
        MsgpackWriteArrayHeader(buf, refs.Length);
        foreach (FileReference @ref in refs)
        {
            // Each entry is a 5-element tuple: [pos, end, fileName, resolutionMode, preserve]
            MsgpackWriteArrayHeader(buf, 5);
            MsgpackWriteUint(buf, Utf16(@ref.Pos));
            MsgpackWriteUint(buf, Utf16(@ref.End));
            MsgpackWriteString(buf, @ref.FileName);
            MsgpackWriteUint(buf, (uint)@ref.ResolutionMode);
            MsgpackWriteBool(buf, @ref.Preserve);
        }
        return offset;

        static uint Utf16(int pos)
        {
            return (uint)pos;
        }
    }

    // encodeNodeIndexArray encodes a slice of LiteralLikeNodes as a msgpack array of
    // uint node indices. Returns the byte offset into the buffer, or noStructuredData
    // if the slice is empty.
    // (encoder.go:715-725)
    private static uint EncodeNodeIndexArray(Node[]? nodes, Dictionary<Node, uint>? indexMap, List<byte> buf)
    {
        if (nodes == null || nodes.Length == 0)
        {
            return NoStructuredData;
        }
        uint offset = (uint)buf.Count;
        MsgpackWriteArrayHeader(buf, nodes.Length);
        foreach (Node node in nodes)
        {
            MsgpackWriteUint(buf, indexMap != null && indexMap.TryGetValue(node, out uint v) ? v : 0);
        }
        return offset;
    }

    // encodeModuleAugmentations encodes a slice of ModuleName nodes as a msgpack array
    // of uint node indices. Returns the byte offset into the buffer, or noStructuredData
    // if the slice is empty.
    // (encoder.go:730-740)
    private static uint EncodeModuleAugmentations(Node[]? nodes, Dictionary<Node, uint>? indexMap, List<byte> buf)
    {
        if (nodes == null || nodes.Length == 0)
        {
            return NoStructuredData;
        }
        uint offset = (uint)buf.Count;
        MsgpackWriteArrayHeader(buf, nodes.Length);
        foreach (Node node in nodes)
        {
            MsgpackWriteUint(buf, indexMap != null && indexMap.TryGetValue(node, out uint v) ? v : 0);
        }
        return offset;
    }

    // encodeStringArray encodes a slice of strings as a msgpack array of strings.
    // Returns the byte offset into the buffer, or noStructuredData if the slice is empty.
    // (encoder.go:744-754)
    private static uint EncodeStringArray(string[]? strs, List<byte> buf)
    {
        if (strs == null || strs.Length == 0)
        {
            return NoStructuredData;
        }
        uint offset = (uint)buf.Count;
        MsgpackWriteArrayHeader(buf, strs.Length);
        foreach (string s in strs)
        {
            MsgpackWriteString(buf, s);
        }
        return offset;
    }

    // Minimal msgpack writers for the structured data section.
    // (encoder.go:758-766)
    private static void MsgpackWriteArrayHeader(List<byte> buf, int length)
    {
        if (length <= 0x0f)
        {
            buf.Add((byte)(0x90 | length));
            return;
        }
        if (length <= 0xffff)
        {
            buf.Add(0xdc);
            buf.Add((byte)(length >> 8));
            buf.Add((byte)length);
            return;
        }
        buf.Add(0xdd);
        buf.Add((byte)(length >> 24));
        buf.Add((byte)(length >> 16));
        buf.Add((byte)(length >> 8));
        buf.Add((byte)length);
    }

    // (encoder.go:768-779)
    private static void MsgpackWriteUint(List<byte> buf, uint value)
    {
        if (value <= 0x7f)
        {
            buf.Add((byte)value);
            return;
        }
        if (value <= 0xff)
        {
            buf.Add(0xcc);
            buf.Add((byte)value);
            return;
        }
        if (value <= 0xffff)
        {
            buf.Add(0xcd);
            buf.Add((byte)(value >> 8));
            buf.Add((byte)value);
            return;
        }
        buf.Add(0xce);
        buf.Add((byte)(value >> 24));
        buf.Add((byte)(value >> 16));
        buf.Add((byte)(value >> 8));
        buf.Add((byte)value);
    }

    // (encoder.go:781-793) — msgpack strings are UTF-8 with byte lengths, matching Go's
    // wire format exactly (unlike the string-data section; see file header PORT-NOTE).
    private static void MsgpackWriteString(List<byte> buf, string s)
    {
        int n = System.Text.Encoding.UTF8.GetByteCount(s);
        if (n <= 0x1f)
        {
            buf.Add((byte)(0xa0 | n));
        }
        else if (n <= 0xff)
        {
            buf.Add(0xd9);
            buf.Add((byte)n);
        }
        else if (n <= 0xffff)
        {
            buf.Add(0xda);
            buf.Add((byte)(n >> 8));
            buf.Add((byte)n);
        }
        else
        {
            buf.Add(0xdb);
            buf.Add((byte)(n >> 24));
            buf.Add((byte)(n >> 16));
            buf.Add((byte)(n >> 8));
            buf.Add((byte)n);
        }
        buf.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
    }

    // (encoder.go:795-800)
    private static void MsgpackWriteBool(List<byte> buf, bool value)
    {
        buf.Add(value ? (byte)0xc3 : (byte)0xc2);
    }

    // Hand-written commonData encoding functions for nodes whose non-bool data
    // members cannot be automatically encoded by the generator. Each function
    // packs relevant fields into the 6-bit commonData area (bits 24-29) of the
    // 32-bit node data word.
    // (encoder.go:807-811 — lives in encoder.go but is mirrored in EncoderGenerated.cs's
    // switch; kept here as the Go-side function it is.)
    internal static uint GetNodeCommonData_SyntheticExpression(Node _)
    {
        // SyntheticExpression is an internal compiler node that is never part of a parsed AST.
        // It should never be encoded.
        throw new InvalidOperationException("SyntheticExpression should never be encoded");
    }

    // Hand-written extended data encoding functions for literal nodes that were
    // previously string-type but whose TokenFlags/TemplateFlags cannot fit in 6 bits.
    // (encoder.go:816-844)

    internal static void RecordExtendedData_StringLiteral(Node node, StringTable strs, List<uint> extendedData)
    {
        StringLiteral n = node.AsStringLiteral();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, (uint)n.TokenFlags);
    }

    internal static void RecordExtendedData_NumericLiteral(Node node, StringTable strs, List<uint> extendedData)
    {
        NumericLiteral n = node.AsNumericLiteral();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, (uint)n.TokenFlags);
    }

    internal static void RecordExtendedData_BigIntLiteral(Node node, StringTable strs, List<uint> extendedData)
    {
        BigIntLiteral n = node.AsBigIntLiteral();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, (uint)n.TokenFlags);
    }

    internal static void RecordExtendedData_RegularExpressionLiteral(Node node, StringTable strs, List<uint> extendedData)
    {
        RegularExpressionLiteral n = node.AsRegularExpressionLiteral();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, (uint)n.TokenFlags);
    }

    internal static void RecordExtendedData_NoSubstitutionTemplateLiteral(Node node, StringTable strs, List<uint> extendedData)
    {
        NoSubstitutionTemplateLiteral n = node.AsNoSubstitutionTemplateLiteral();
        uint textIndex = strs.Add(n.Text, node.Kind, node.Pos, node.End);
        AppendUint32s(extendedData, textIndex, (uint)n.TemplateFlags);
    }
}
