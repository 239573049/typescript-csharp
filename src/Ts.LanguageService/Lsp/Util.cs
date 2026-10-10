// Ported from tsgo internal/lsp/lsproto/util.go (comparison helpers subset), plus
// lsp.go DocumentUri.FileName (hosted here as an extension; moved from the retired
// hand-written Protocol.cs subset when the generated protocol face absorbed it).

using System;
using System.Collections.Generic;
using System.Text;
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

// lsp.go:19-49 — DocumentUri.FileName(): URI → file name.
public static class DocumentUriExtensions
{
    // lsp.go:19-49 — DocumentUri.FileName.
    //
    // PORT-NOTE: Go parses with net/url (RFC 3986 reference resolution, percent-decoding
    // exactly once, the host returned verbatim, '#' as fragment). System.Uri is not a
    // stand-in — it lowercases the host, leaves the Windows drive colon escaped in
    // AbsolutePath, and treats '\' as a separator on Windows — so the reference parse is
    // reproduced directly below (SplitUri + UnescapeUriPath mirror net/url's
    // parse/unescape for the pieces this function reads).
    public static string FileName(this DocumentUri uri)
    {
        var value = uri.Value;
        if (global::Ts.Bundled.Bundled.IsBundled(value))
        {
            return value;
        }
        if (value.StartsWith("file://", StringComparison.Ordinal))
        {
            SplitUri(value, out _, out var authority, out var rawPath);
            var path = UnescapeUriPath(rawPath, value);
            if (authority.Length != 0)
            {
                // Go: return "//" + parsed.Host + parsed.Path
                return "//" + authority + path;
            }
            return FixWindowsUriPath(path);
        }

        // Leave all other URIs escaped so we can round-trip them.
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            throw new JsonException("invalid URI: " + value);
        }
        var scheme = value[..colon];
        var rest = value[(colon + 1)..];
        var auth = "ts-nul-authority";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            // Go: strings.Cut(rest, "/") — the remainder keeps no leading separator.
            int slash = rest.IndexOf('/', 2);
            if (slash < 0)
            {
                throw new JsonException("invalid URI: " + value);
            }
            auth = rest[2..slash];
            rest = rest[(slash + 1)..];
        }
        return "^/" + scheme + "/" + auth + "/" + rest;
    }

    // net/url parse: scheme, host, and the raw (still escaped) path. The fragment and
    // query are cut off first, as the reference parser does.
    private static void SplitUri(string raw, out string scheme, out string authority, out string rawPath)
    {
        int schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        scheme = raw[..schemeEnd];

        var rest = raw[(schemeEnd + 3)..];
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            rest = rest[..hash];
        }
        int query = rest.IndexOf('?');
        if (query >= 0)
        {
            rest = rest[..query];
        }

        int slash = rest.IndexOf('/');
        authority = slash < 0 ? rest : rest[..slash];
        rawPath = slash < 0 ? "" : rest[slash..];
    }

    // net/url unescape for the Path accessor: percent-decode exactly once; the resulting
    // bytes are then read as UTF-8 (invalid bytes become U+FFFD, matching Go's range over
    // the byte string).
    private static string UnescapeUriPath(string rawPath, string originalUri)
    {
        var bytes = new List<byte>(rawPath.Length);
        for (int i = 0; i < rawPath.Length; i++)
        {
            char c = rawPath[i];
            if (c == '%')
            {
                if (i + 2 >= rawPath.Length || !IsHex(rawPath[i + 1]) || !IsHex(rawPath[i + 2]))
                {
                    // Go: url.Parse errors (EscapeError), and FileName panics.
                    throw new JsonException("invalid file URI: " + originalUri);
                }
                bytes.Add((byte)((HexValue(rawPath[i + 1]) << 4) | HexValue(rawPath[i + 2])));
                i += 2;
                continue;
            }
            // A raw non-ASCII char cannot occur in a well-formed URI (it must be escaped);
            // Go only ever appends bytes here.
            bytes.Add((byte)c);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes.ToArray());
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexValue(char c) =>
        c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);

    // lsp.go:51-58 — fixWindowsURIPath.
    private static string FixWindowsUriPath(string path)
    {
        if (path.StartsWith('/'))
        {
            if (global::Ts.TsPath.TsPath.SplitVolumePath(path[1..], out var volume, out var rest))
            {
                return volume + rest;
            }
        }
        return path;
    }
}
