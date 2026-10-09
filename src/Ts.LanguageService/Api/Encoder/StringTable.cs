// Ported from tsgo internal/api/encoder/stringtable.go (1:1).
//
// PORT-NOTE (UTF-16 position space, plan/05 §8.1 / plan/01 §一.6): Go measures the string
// table in UTF-8 bytes — node positions are UTF-8 byte offsets and the string-data section
// is UTF-8. The C# port's node positions ARE UTF-16 code units, so this table is measured
// in UTF-16 code units and the encoded string-data section is UTF-16LE (2 bytes per code
// unit). Offsets stored in the offsets array are code-unit offsets into
// `fileText + otherStrings`; the encoder writes the section with
// Encoding.Unicode so the decoder can slice it losslessly. See Encoder.cs for the
// corresponding header note.

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Ast;

namespace Ts.Api.Encoder;

// Go: internal/api/encoder/stringtable.go:9-14 — `type stringTable struct`.
internal sealed class StringTable
{
    private readonly string fileText;

    private readonly StringBuilder otherStrings = new();

    // offsets are pos/end pairs.
    private readonly List<uint> offsets;

    // Go: newStringTable (stringtable.go:16-23).
    internal StringTable(string fileText, int stringCount)
    {
        this.fileText = fileText;
        offsets = new List<uint>(stringCount * 2);
    }

    // Go: (*stringTable).add (stringtable.go:25-52).
    internal uint Add(string text, SyntaxKind kind, int pos, int end)
    {
        uint index = (uint)offsets.Count;
        if (kind == SyntaxKind.SourceFile)
        {
            offsets.Add((uint)pos);
            offsets.Add((uint)end);
            return index;
        }
        int length = text.Length;
        if (end - pos > 0 && end <= fileText.Length)
        {
            // pos includes leading trivia, but we can usually infer the actual start of the
            // string from the kind and end
            int endOffset = 0;
            if (kind == SyntaxKind.StringLiteral || kind == SyntaxKind.TemplateTail || kind == SyntaxKind.NoSubstitutionTemplateLiteral)
            {
                endOffset = 1;
            }
            end = end - endOffset;
            int start = end - length;
            string fileSlice = fileText[start..end];
            if (fileSlice == text)
            {
                offsets.Add((uint)start);
                offsets.Add((uint)end);
                return index;
            }
        }
        // no exact match, so we need to add it to the string table
        int offset = fileText.Length + otherStrings.Length;
        _ = otherStrings.Append(text);
        offsets.Add((uint)offset);
        offsets.Add((uint)(offset + length));
        return index;
    }

    // Go: (*stringTable).encode (stringtable.go:54-60). The C# result layout is
    // [offsets as LE uint32...] + [string data as UTF-16LE bytes]; length-prefixed by
    // EncodedLength(). Callers know the split (offsets are always N*4 bytes, data follows).
    internal byte[] Encode()
    {
        byte[] result = new byte[EncodedLength()];
        int o = 0;
        foreach (uint value in offsets)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(o), value);
            o += 4;
        }
        WriteDataTo(result, o);
        return result;
    }

    // Writes the string data section (UTF-16LE) into buf at offset. Go: the trailing
    // `append(result, t.fileText...); append(result, t.otherStrings.String()...)` half
    // of encode().
    internal void WriteDataTo(byte[] buf, int offset)
    {
        int o = offset;
        WriteStringData(buf, ref o, fileText);
        // StringBuilder has no bulk char dump; copy via indexer (built once per encode).
        for (int i = 0; i < otherStrings.Length; i++)
        {
            char c = otherStrings[i];
            buf[o++] = (byte)c;
            buf[o++] = (byte)(c >> 8);
        }
    }

    private static void WriteStringData(byte[] buf, ref int o, string s)
    {
        foreach (char c in s)
        {
            buf[o++] = (byte)c;
            buf[o++] = (byte)(c >> 8);
        }
    }

    // Go: (*stringTable).stringLength (stringtable.go:62-64) — length of the string data
    // section in bytes (UTF-16LE: 2 bytes per code unit).
    internal int StringLength()
    {
        return (fileText.Length + otherStrings.Length) * 2;
    }

    // Go: (*stringTable).encodedLength (stringtable.go:66-68).
    internal int EncodedLength()
    {
        return offsets.Count * 4 + StringLength();
    }
}
