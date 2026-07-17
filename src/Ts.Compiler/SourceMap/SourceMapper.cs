// Ported from tsgo internal/sourcemap/source_mapper.go

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Ts.Core;

namespace Ts.SourceMap;

// plan/05 §2.3: SourceIndex/NameIndex dissolve to int (aliased for grep fidelity).
using SourceIndex = int;
using NameIndex = int;

// Go: type Host interface { ... }. PORT-NOTE (plan/05 §1.3): kept without `I` prefix, matching
// landed host-like interfaces (module.Host, module.ResolutionHost).
public interface Host
{
    bool UseCaseSensitiveFileNames();

    ECMALineInfo? GetECMALineInfo(string fileName);

    // Go: ReadFile(fileName string) (string, bool) — (T, ok) → Try pattern (plan/05 §4).
    bool ReadFile(string fileName, out string contents);
}

// Similar to `Mapping`, but position-based.
// Go: type MappedPosition struct { ... } (unexported); SourceMappedPosition is an alias.
internal sealed class MappedPosition
{
    internal int generatedPosition;
    internal int sourcePosition;
    internal SourceIndex sourceIndex;
    internal NameIndex nameIndex;

    // Go: func (m *MappedPosition) isSourceMappedPosition() bool
    internal bool IsSourceMappedPosition()
    {
        return sourceIndex != Mapping.MissingSource && sourcePosition != SourceMapper.MissingPosition;
    }
}

// Go: type DocumentPosition struct { FileName string; Pos int }
public sealed class DocumentPosition
{
    public string FileName = "";
    public int Pos;
}

// Maps source positions to generated positions and vice versa.
// Go: type DocumentPositionMapper struct { ... }
public sealed class DocumentPositionMapper
{
    internal readonly bool useCaseSensitiveFileNames;
    internal readonly string[] sourceFileAbsolutePaths;
    internal readonly Dictionary<string, SourceIndex> sourceToSourceIndexMap;
    internal readonly string generatedAbsoluteFilePath;
    internal readonly MappedPosition[] generatedMappings;
    internal readonly Dictionary<SourceIndex, MappedPosition[]> sourceMappings;

    internal DocumentPositionMapper(
        bool useCaseSensitiveFileNames,
        string[] sourceFileAbsolutePaths,
        Dictionary<string, SourceIndex> sourceToSourceIndexMap,
        string generatedAbsoluteFilePath,
        MappedPosition[] generatedMappings,
        Dictionary<SourceIndex, MappedPosition[]> sourceMappings)
    {
        this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
        this.sourceFileAbsolutePaths = sourceFileAbsolutePaths;
        this.sourceToSourceIndexMap = sourceToSourceIndexMap;
        this.generatedAbsoluteFilePath = generatedAbsoluteFilePath;
        this.generatedMappings = generatedMappings;
        this.sourceMappings = sourceMappings;
    }
}

// Go source_mapper.go package-level functions live here (plan/05 §2.1 — file → static class).
public static class SourceMapper
{
    // Go: const missingPosition = -1
    internal const int MissingPosition = -1;

    // Go: func createDocumentPositionMapper(host Host, sourceMap *RawSourceMap, mapPath string) *DocumentPositionMapper
    private static DocumentPositionMapper CreateDocumentPositionMapper(Host host, RawSourceMap sourceMap, string mapPath)
    {
        string mapDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(mapPath);
        string sourceRoot;
        if (sourceMap.SourceRoot != "")
        {
            sourceRoot = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(sourceMap.SourceRoot, mapDirectory);
        }
        else
        {
            sourceRoot = mapDirectory;
        }
        string generatedAbsoluteFilePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(sourceMap.File, mapDirectory);
        string[] sourceFileAbsolutePaths = CoreUtil.Map(sourceMap.Sources, source =>
            global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(source, sourceRoot))!;
        bool useCaseSensitiveFileNames = host.UseCaseSensitiveFileNames();
        var sourceToSourceIndexMap = new Dictionary<string, SourceIndex>(sourceFileAbsolutePaths.Length, StringComparer.Ordinal);
        for (int i = 0; i < sourceFileAbsolutePaths.Length; i++)
        {
            sourceToSourceIndexMap[global::Ts.TsPath.TsPath.GetCanonicalFileName(sourceFileAbsolutePaths[i], useCaseSensitiveFileNames)] = i;
        }

        // getDecodedMappings()
        List<MappedPosition>? decodedMappings = new();
        var sourceMappingsTemp = new Dictionary<SourceIndex, List<MappedPosition>>();

        var decoder = MappingsDecoder.DecodeMappings(sourceMap.Mappings);
        foreach (var mapping in decoder.Values())
        {
            // processMapping()
            int generatedPosition = -1;
            ECMALineInfo? lineInfo = host.GetECMALineInfo(generatedAbsoluteFilePath);
            if (lineInfo != null)
            {
                generatedPosition = ComputePositionOfLineAndUTF16Character(
                    lineInfo.lineStarts,
                    mapping.GeneratedLine,
                    mapping.GeneratedCharacter,
                    lineInfo.text,
                    true /*allowEdits*/);
            }

            int sourcePosition = -1;
            if (mapping.IsSourceMapping())
            {
                ECMALineInfo? sourceLineInfo = host.GetECMALineInfo(sourceFileAbsolutePaths[mapping.SourceIndex]);
                if (sourceLineInfo != null)
                {
                    int pos = ComputePositionOfLineAndUTF16Character(
                        sourceLineInfo.lineStarts,
                        mapping.SourceLine,
                        mapping.SourceCharacter,
                        sourceLineInfo.text,
                        true /*allowEdits*/);
                    sourcePosition = pos;
                }
            }

            decodedMappings.Add(new MappedPosition
            {
                generatedPosition = generatedPosition,
                sourceIndex = mapping.SourceIndex,
                sourcePosition = sourcePosition,
                nameIndex = mapping.NameIndex,
            });
        }
        if (decoder.Error() != null)
        {
            decodedMappings = null;
        }

        // getSourceMappings()
        if (decodedMappings != null)
        {
            foreach (var mapping in decodedMappings)
            {
                if (!mapping.IsSourceMappedPosition())
                {
                    continue;
                }
                SourceIndex sourceIndex = mapping.sourceIndex;
                if (!sourceMappingsTemp.TryGetValue(sourceIndex, out var list))
                {
                    list = new List<MappedPosition>();
                    sourceMappingsTemp[sourceIndex] = list;
                }
                list.Add(new MappedPosition
                {
                    generatedPosition = mapping.generatedPosition,
                    sourceIndex = sourceIndex,
                    sourcePosition = mapping.sourcePosition,
                    nameIndex = mapping.nameIndex,
                });
            }
        }

        var sourceMappings = new Dictionary<SourceIndex, MappedPosition[]>(sourceMappingsTemp.Count);
        // map-iter: 序无关 —— each source index is sorted+deduped independently; the result
        // dictionary content does not depend on iteration order.
        foreach (var (i, list) in sourceMappingsTemp)
        {
            MappedPosition[] arr = list.ToArray();
            Array.Sort(arr, (a, b) =>
            {
                global::Ts.Core.Debug.Assert(a.sourceIndex == b.sourceIndex, "All source mappings should have the same source index");
                return a.sourcePosition - b.sourcePosition;
            });
            sourceMappings[i] = CoreUtil.DeduplicateSorted(arr, (a, b) =>
                a.generatedPosition == b.generatedPosition &&
                a.sourceIndex == b.sourceIndex &&
                a.sourcePosition == b.sourcePosition)!;
        }

        // getGeneratedMappings()
        MappedPosition[] generatedMappings;
        if (decodedMappings != null)
        {
            MappedPosition[] arr = decodedMappings.ToArray();
            Array.Sort(arr, (a, b) => a.generatedPosition - b.generatedPosition);
            generatedMappings = CoreUtil.DeduplicateSorted(arr, (a, b) =>
                a.generatedPosition == b.generatedPosition &&
                a.sourceIndex == b.sourceIndex &&
                a.sourcePosition == b.sourcePosition)!;
        }
        else
        {
            // Go: generatedMappings = nil (decode error); consumers only test len == 0.
            generatedMappings = [];
        }

        return new DocumentPositionMapper(
            useCaseSensitiveFileNames,
            sourceFileAbsolutePaths,
            sourceToSourceIndexMap,
            generatedAbsoluteFilePath,
            generatedMappings,
            sourceMappings);
    }

    // Go: func (d *DocumentPositionMapper) GetSourcePosition(loc *DocumentPosition) *DocumentPosition
    // nil-receiver method → extension method (AGENTS §4 rule 6): callable on a null mapper.
    public static DocumentPosition? GetSourcePosition(this DocumentPositionMapper? d, DocumentPosition loc)
    {
        if (d == null)
        {
            return null;
        }
        if (d.generatedMappings.Length == 0)
        {
            return null;
        }

        var (targetIndex, _) = BinarySearchFunc(d.generatedMappings, loc.Pos, (m, pos) => m.generatedPosition - pos);

        if (targetIndex < 0 || targetIndex >= d.generatedMappings.Length)
        {
            return null;
        }

        MappedPosition mapping = d.generatedMappings[targetIndex];
        if (!mapping.IsSourceMappedPosition())
        {
            return null;
        }

        // Closest position
        return new DocumentPosition
        {
            FileName = d.sourceFileAbsolutePaths[mapping.sourceIndex],
            Pos = mapping.sourcePosition,
        };
    }

    // Go: func (d *DocumentPositionMapper) GetGeneratedPosition(loc *DocumentPosition) *DocumentPosition
    public static DocumentPosition? GetGeneratedPosition(this DocumentPositionMapper? d, DocumentPosition loc)
    {
        if (d == null)
        {
            return null;
        }
        if (!d.sourceToSourceIndexMap.TryGetValue(global::Ts.TsPath.TsPath.GetCanonicalFileName(loc.FileName, d.useCaseSensitiveFileNames), out SourceIndex sourceIndex))
        {
            return null;
        }
        if (sourceIndex < 0 || sourceIndex >= d.sourceMappings.Count)
        {
            return null;
        }
        MappedPosition[] sourceMappings = d.sourceMappings.GetValueOrDefault(sourceIndex) ?? [];
        var (targetIndex, _) = BinarySearchFunc(sourceMappings, loc.Pos, (m, pos) => m.sourcePosition - pos);

        if (targetIndex < 0 || targetIndex >= sourceMappings.Length)
        {
            return null;
        }

        MappedPosition mapping = sourceMappings[targetIndex];
        if (mapping.sourceIndex != sourceIndex)
        {
            return null;
        }

        // Closest position
        return new DocumentPosition
        {
            FileName = d.generatedAbsoluteFilePath,
            Pos = mapping.generatedPosition,
        };
    }

    // Go: func GetDocumentPositionMapper(host Host, generatedFileName string) *DocumentPositionMapper
    public static DocumentPositionMapper? GetDocumentPositionMapper(Host host, string generatedFileName)
    {
        string mapFileName = TryGetSourceMappingURL(host, generatedFileName);
        if (mapFileName != "")
        {
            var (base64Object, matched) = TryParseBase64Url(mapFileName);
            if (matched)
            {
                if (base64Object != "")
                {
                    if (TryDecodeBase64(base64Object, out byte[]? decoded))
                    {
                        return ConvertDocumentToSourceMapper(host, Encoding.UTF8.GetString(decoded!), generatedFileName);
                    }
                }
                // Not a data URL we can parse, skip it
                mapFileName = "";
            }
        }

        var possibleMapLocations = new List<string>();
        if (mapFileName != "")
        {
            possibleMapLocations.Add(mapFileName);
        }
        possibleMapLocations.Add(generatedFileName + ".map");
        foreach (string location in possibleMapLocations)
        {
            string mapFileNameLocal = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(location, global::Ts.TsPath.TsPath.GetDirectoryPath(generatedFileName));
            if (host.ReadFile(mapFileNameLocal, out string mapFileContents))
            {
                return ConvertDocumentToSourceMapper(host, mapFileContents, mapFileNameLocal);
            }
        }
        return null;
    }

    // Go: func convertDocumentToSourceMapper(host Host, contents string, mapFileName string) *DocumentPositionMapper
    private static DocumentPositionMapper? ConvertDocumentToSourceMapper(Host host, string contents, string mapFileName)
    {
        RawSourceMap? sourceMap = TryParseRawSourceMap(contents);
        if (sourceMap == null || sourceMap.Sources.Length == 0 || sourceMap.File == "" || sourceMap.Mappings == "")
        {
            // invalid map
            return null;
        }

        // Don't support source maps that contain inlined sources
        if (CoreUtil.Some(sourceMap.SourcesContent, s => s != null))
        {
            return null;
        }

        return CreateDocumentPositionMapper(host, sourceMap, mapFileName);
    }

    // Go: func tryParseRawSourceMap(contents string) *RawSourceMap
    public static RawSourceMap? TryParseRawSourceMap(string contents)
    {
        RawSourceMap? sourceMap = ParseRawSourceMap(contents);
        if (sourceMap == null)
        {
            return null;
        }
        if (sourceMap.Version != 3)
        {
            return null;
        }
        return sourceMap;
    }

    // Go: func tryGetSourceMappingURL(host Host, fileName string) string
    private static string TryGetSourceMappingURL(Host host, string fileName)
    {
        ECMALineInfo? lineInfo = host.GetECMALineInfo(fileName);
        return Util.TryGetSourceMappingURL(lineInfo);
    }

    // Equivalent to /^data:(?:application\/json;(?:charset=[uU][tT][fF]-8;)?base64,([A-Za-z0-9+/=]+)$)?/
    // Go: func tryParseBase64Url(url string) (parseableUrl string, isBase64Url bool)
    private static (string ParseableUrl, bool IsBase64Url) TryParseBase64Url(string url)
    {
        if (!CutPrefix(url, "data:", out url))
        {
            return ("", false);
        }
        if (!CutPrefix(url, "application/json;", out url))
        {
            return ("", true);
        }
        if (CutPrefix(url, "charset=", out url))
        {
            const string utf8Semicolon = "utf-8;";
            // PORT-NOTE (deviation, AGENTS §6): Go slices url[:len("utf-8;")] unconditionally,
            // which panics on a shorter suffix. We guard length and treat it as non-parseable
            // (return "", true) rather than crashing on malformed data URLs.
            if (url.Length < utf8Semicolon.Length || !global::Ts.StringUtil.StringUtil.EqualFold(url[..utf8Semicolon.Length], utf8Semicolon))
            {
                return ("", true);
            }
            url = url[utf8Semicolon.Length..];
        }
        if (!CutPrefix(url, "base64,", out url))
        {
            return ("", true);
        }
        foreach (char r in url)
        {
            if (!(global::Ts.StringUtil.StringUtil.IsASCIILetter(r) || global::Ts.StringUtil.StringUtil.IsDigit(r) || r == '+' || r == '/' || r == '='))
            {
                return ("", true);
            }
        }
        return (url, true);
    }

    // Go: strings.CutPrefix(s, prefix) (after string, found bool)
    private static bool CutPrefix(string s, string prefix, out string rest)
    {
        if (s.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = s[prefix.Length..];
            return true;
        }
        rest = s;
        return false;
    }

    // Go: base64.StdEncoding.DecodeString(s) — error → false.
    private static bool TryDecodeBase64(string s, out byte[]? decoded)
    {
        try
        {
            decoded = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            decoded = null;
            return false;
        }
    }

    // Go: slices.BinarySearchFunc(x, target, cmp) → (index, found). Leftmost insertion point.
    private static (int Index, bool Found) BinarySearchFunc<T>(T[] x, int target, Func<T, int, int> cmp)
    {
        int n = x.Length;
        int i = 0;
        int j = n;
        while (i < j)
        {
            int h = (int)((uint)(i + j) >> 1);
            if (cmp(x[h], target) < 0)
            {
                i = h + 1;
            }
            else
            {
                j = h;
            }
        }
        return (i, i < n && cmp(x[i], target) == 0);
    }

    // PORT-NOTE (STUB — pending scanner phase 3): scanner.ComputePositionOfLineAndUTF16Character
    // is deferred to phase 3 in Ts.Scanner (Scanner.cs SCOPE note). Ported here as a local
    // private helper so the position mapper is buildable/functional now; remove this and
    // re-point to the scanner once it lands. UTF-16 world-view (plan/05 §8.1): text is UTF-16
    // and positions are code-unit offsets, so a rune advances `pos` and the UTF-16 count by the
    // same amount. All callers here pass allowEdits=true, so the throwing branches are inert.
    private static int ComputePositionOfLineAndUTF16Character(int[] lineStarts, int line, int character, string text, bool allowEdits)
    {
        if (line < 0 || line >= lineStarts.Length)
        {
            if (allowEdits)
            {
                // Clamp line to nearest allowable value
                if (line < 0)
                {
                    line = 0;
                }
                else if (line >= lineStarts.Length)
                {
                    line = lineStarts.Length - 1;
                }
            }
            else
            {
                throw new InvalidOperationException($"Bad line number. Line: {line}, lineStarts.length: {lineStarts.Length}.");
            }
        }

        int lineStart = lineStarts[line];

        if (character > 0)
        {
            // UTF-16 character offset: scan from line start counting UTF-16 code units.
            int lineEnd = text.Length;
            if (line + 1 < lineStarts.Length)
            {
                lineEnd = lineStarts[line + 1];
            }
            int utf16Count = 0;
            int pos = lineStart;
            while (pos < lineEnd)
            {
                if (utf16Count >= character)
                {
                    break;
                }
                int size = 1;
                if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
                {
                    size = 2;
                }
                utf16Count += size;
                pos += size;
            }
            if (!allowEdits)
            {
                if (pos == lineEnd && utf16Count < character)
                {
                    throw new InvalidOperationException($"Bad UTF-16 character offset. Line: {line}, character: {character}.");
                }
                global::Ts.Core.Debug.Assert(pos <= text.Length);
                return pos;
            }
            if (pos > text.Length)
            {
                return text.Length;
            }
            return pos;
        }

        // Character is 0: line start position.
        int res = lineStart;

        if (allowEdits)
        {
            if (res > text.Length)
            {
                return text.Length;
            }
            return res;
        }
        global::Ts.Core.Debug.Assert(res <= text.Length); // Allow single character overflow for trailing newline
        return res;
    }

    // Hand-written Utf8JsonReader (07-aot §4.1/§4.3): replaces go-json-experiment json.Unmarshal.
    // Unknown fields are skipped; type mismatches / malformed input → null (Go returns nil on
    // any Unmarshal error). Untested this wave (no source_mapper test), no landed consumer.
    private static RawSourceMap? ParseRawSourceMap(string contents)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }
            var map = new RawSourceMap();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return map;
                }
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return null;
                }
                string propertyName = reader.GetString()!;
                if (!reader.Read())
                {
                    return null;
                }
                switch (propertyName)
                {
                    case "version":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int version))
                        {
                            return null;
                        }
                        map.Version = version;
                        break;
                    case "file":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            return null;
                        }
                        map.File = reader.GetString()!;
                        break;
                    case "sourceRoot":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            return null;
                        }
                        map.SourceRoot = reader.GetString()!;
                        break;
                    case "sources":
                        if (!TryReadStringArray(ref reader, out string[]? sources))
                        {
                            return null;
                        }
                        map.Sources = sources!;
                        break;
                    case "names":
                        if (!TryReadStringArray(ref reader, out string[]? names))
                        {
                            return null;
                        }
                        map.Names = names!;
                        break;
                    case "mappings":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            return null;
                        }
                        map.Mappings = reader.GetString()!;
                        break;
                    case "sourcesContent":
                        if (!TryReadNullableStringArray(ref reader, out string?[]? sourcesContent))
                        {
                            return null;
                        }
                        map.SourcesContent = sourcesContent;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadStringArray(ref Utf8JsonReader reader, out string[]? result)
    {
        result = null;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }
        var list = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                result = list.ToArray();
                return true;
            }
            if (reader.TokenType != JsonTokenType.String)
            {
                return false;
            }
            list.Add(reader.GetString()!);
        }
        return false;
    }

    private static bool TryReadNullableStringArray(ref Utf8JsonReader reader, out string?[]? result)
    {
        result = null;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }
        var list = new List<string?>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                result = list.ToArray();
                return true;
            }
            if (reader.TokenType == JsonTokenType.Null)
            {
                list.Add(null);
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                list.Add(reader.GetString());
            }
            else
            {
                return false;
            }
        }
        return false;
    }
}
