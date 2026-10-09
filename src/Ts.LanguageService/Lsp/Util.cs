// Ported from tsgo internal/lsp/lsproto/util.go (comparison helpers subset), plus
// lsp.go DocumentUri.FileName (hosted here as an extension; moved from the retired
// hand-written Protocol.cs subset when the generated protocol face absorbed it).

using System;
using System.Text.Json;

namespace Ts.LsProto;

public static class Util
{
    // Implements a cmp.Compare like function for two Position
    // ComparePositions(pos, other) == cmp.Compare(pos, other)
    public static int ComparePositions(Position pos, Position other)
    {
        var lineComp = pos.Line.CompareTo(other.Line);
        if (lineComp != 0)
        {
            return lineComp;
        }
        return pos.Character.CompareTo(other.Character);
    }

    // Implements a cmp.Compare like function for two Range
    // CompareRanges(lsRange, other) == cmp.Compare(lsRange, other)
    //
    //	Range.Start is compared before Range.End
    public static int CompareRanges(Range lsRange, Range other)
    {
        var startComp = ComparePositions(lsRange.Start, other.Start);
        if (startComp != 0)
        {
            return startComp;
        }
        return ComparePositions(lsRange.End, other.End);
    }
}

// lsp.go:309-312 — hand-written CodeActionKind extensions beyond the metamodel.
public readonly partial record struct CodeActionKind
{
    public static CodeActionKind SourceRemoveUnusedImports => new("source.removeUnusedImports");
    public static CodeActionKind SourceSortImports => new("source.sortImports");
}

// lsproto lsp.go — DocumentUri.FileName(): URI → file name.
public static class DocumentUriExtensions
{
    public static string FileName(this DocumentUri uri)
    {
        var value = uri.Value;
        if (value.StartsWith("file://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            {
                throw new JsonException("invalid file URI: " + value);
            }
            if (!string.IsNullOrEmpty(parsed.Host))
            {
                return global::Ts.TsPath.TsPath.NormalizePath("//" + parsed.Host + parsed.AbsolutePath);
            }
            return global::Ts.TsPath.TsPath.NormalizePath(parsed.LocalPath);
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            throw new JsonException("invalid URI: " + value);
        }
        var scheme = value[..colon];
        var path = value[(colon + 1)..];
        var authority = "ts-nul-authority";
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var slash = path.IndexOf('/', 2);
            if (slash < 0)
            {
                throw new JsonException("invalid URI: " + value);
            }
            authority = path[2..slash];
            path = path[slash..];
        }
        return "^/" + scheme + "/" + authority + "/" + path;
    }
}
