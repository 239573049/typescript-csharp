// Ported from tsgo internal/sourcemap/generator.go

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ts.Core;

namespace Ts.SourceMap;

// Go: type (SourceIndex int; NameIndex int). plan/05 §2.3: no-method named int types dissolve
// to `int`, kept as file-scoped aliases for grep fidelity. UTF16Offset likewise dissolves to int.
using SourceIndex = int;
using NameIndex = int;

// Go: type Generator struct { ... }
public sealed class Generator
{
    // Go generator.go package-level sentinels (sourceIndexNotSet / nameIndexNotSet / notSet /
    // notSetUTF16). UTF16Offset dissolves to int, so all four are int -1.
    private const SourceIndex SourceIndexNotSet = -1;
    private const NameIndex NameIndexNotSet = -1;
    private const int NotSet = -1;
    private const int NotSetUTF16 = -1;

    private readonly global::Ts.TsPath.ComparePathsOptions pathOptions;
    private readonly string file;
    private readonly string sourceRoot;
    private readonly string sourcesDirectoryPath;
    private readonly List<string> rawSources = new();
    private readonly List<string> sources = new();
    private Dictionary<string, SourceIndex>? sourceToSourceIndexMap;

    // Go: sourcesContent []*string. Nullable list; null distinguishes "never set" (omitted in
    // JSON) from a set array. Elements are string? (Go *string, nil vs value).
    private List<string?>? sourcesContent;
    private readonly List<string> names = new();
    private Dictionary<string, NameIndex>? nameToNameIndexMap;
    private readonly StringBuilder mappings = new();
    private int lastGeneratedLine;
    private int lastGeneratedCharacter; // Go core.UTF16Offset → int
    private SourceIndex lastSourceIndex;
    private int lastSourceLine;
    private int lastSourceCharacter; // Go core.UTF16Offset → int
    private NameIndex lastNameIndex;
    private bool hasLast;
    private int pendingGeneratedLine;
    private int pendingGeneratedCharacter; // Go core.UTF16Offset → int
    private SourceIndex pendingSourceIndex;
    private int pendingSourceLine;
    private int pendingSourceCharacter; // Go core.UTF16Offset → int
    private NameIndex pendingNameIndex;
    private bool hasPending;
    private bool hasPendingSource;
    private bool hasPendingName;

    private Generator(string file, string sourceRoot, string sourcesDirectoryPath, global::Ts.TsPath.ComparePathsOptions options)
    {
        this.file = file;
        this.sourceRoot = sourceRoot;
        this.sourcesDirectoryPath = sourcesDirectoryPath;
        this.pathOptions = options;
    }

    // Go: func NewGenerator(file, sourceRoot, sourcesDirectoryPath string, options tspath.ComparePathsOptions) *Generator
    public static Generator NewGenerator(string file, string sourceRoot, string sourcesDirectoryPath, global::Ts.TsPath.ComparePathsOptions options)
    {
        return new Generator(file, sourceRoot, sourcesDirectoryPath, options);
    }

    // Go: func (gen *Generator) Sources() []string { return gen.rawSources }
    // PORT-NOTE: returns a copy; no landed caller relies on backing-array identity.
    public string[] Sources()
    {
        return rawSources.ToArray();
    }

    // Adds a source to the source map
    // Go: func (gen *Generator) AddSource(fileName string) SourceIndex
    public SourceIndex AddSource(string fileName)
    {
        string source = global::Ts.TsPath.TsPath.GetRelativePathToDirectoryOrUrl(
            sourcesDirectoryPath,
            fileName,
            true, /*isAbsolutePathAnUrl*/
            pathOptions);

        if (sourceToSourceIndexMap == null || !sourceToSourceIndexMap.TryGetValue(source, out SourceIndex sourceIndex))
        {
            sourceIndex = sources.Count;
            sources.Add(source);
            rawSources.Add(fileName);
            sourceToSourceIndexMap ??= new Dictionary<string, SourceIndex>(StringComparer.Ordinal);
            sourceToSourceIndexMap[source] = sourceIndex;
        }

        return sourceIndex;
    }

    // Sets the content for a source
    // Go: func (gen *Generator) SetSourceContent(sourceIndex SourceIndex, content string) error
    // plan/05 §4: `error` consumed at call site → string? (null = success), text kept verbatim.
    public string? SetSourceContent(SourceIndex sourceIndex, string content)
    {
        if (sourceIndex < 0 || sourceIndex >= sources.Count)
        {
            return "sourceIndex is out of range";
        }
        sourcesContent ??= new List<string?>();
        while (sourcesContent.Count <= sourceIndex)
        {
            sourcesContent.Add(null);
        }
        sourcesContent[sourceIndex] = content;
        return null;
    }

    // Declares a name in the source map, returning the index of the name
    // Go: func (gen *Generator) AddName(name string) NameIndex
    public NameIndex AddName(string name)
    {
        if (nameToNameIndexMap == null || !nameToNameIndexMap.TryGetValue(name, out NameIndex nameIndex))
        {
            nameIndex = names.Count;
            names.Add(name);
            nameToNameIndexMap ??= new Dictionary<string, NameIndex>(StringComparer.Ordinal);
            nameToNameIndexMap[name] = nameIndex;
        }
        return nameIndex;
    }

    // Go: func (gen *Generator) isNewGeneratedPosition(generatedLine int, generatedCharacter core.UTF16Offset) bool
    private bool IsNewGeneratedPosition(int generatedLine, int generatedCharacter)
    {
        return !hasPending ||
            pendingGeneratedLine != generatedLine ||
            pendingGeneratedCharacter != generatedCharacter;
    }

    // Go: func (gen *Generator) isBacktrackingSourcePosition(sourceIndex SourceIndex, sourceLine int, sourceCharacter core.UTF16Offset) bool
    private bool IsBacktrackingSourcePosition(SourceIndex sourceIndex, int sourceLine, int sourceCharacter)
    {
        return sourceIndex != SourceIndexNotSet &&
            sourceLine != NotSet &&
            sourceCharacter != NotSetUTF16 &&
            pendingSourceIndex == sourceIndex &&
            (pendingSourceLine > sourceLine ||
                (pendingSourceLine == sourceLine && pendingSourceCharacter > sourceCharacter));
    }

    // Go: func (gen *Generator) shouldCommitMapping() bool
    private bool ShouldCommitMapping()
    {
        return hasPending && (!hasLast ||
            lastGeneratedLine != pendingGeneratedLine ||
            lastGeneratedCharacter != pendingGeneratedCharacter ||
            lastSourceIndex != pendingSourceIndex ||
            lastSourceLine != pendingSourceLine ||
            lastSourceCharacter != pendingSourceCharacter ||
            lastNameIndex != pendingNameIndex);
    }

    // Go: func (gen *Generator) appendMappingCharCode(charCode rune)
    private void AppendMappingCharCode(char charCode)
    {
        mappings.Append(charCode);
    }

    // Go: func (gen *Generator) appendBase64VLQ(inValue int)
    private void AppendBase64VLQ(int inValue)
    {
        // Add a new least significant bit that has the sign of the value.
        // if negative number the least significant bit that gets added to the number has value 1
        // else least significant bit value that gets added is 0
        // eg. -1 changes to binary : 01 [1] => 3
        //     +1 changes to binary : 01 [0] => 2
        if (inValue < 0)
        {
            inValue = ((-inValue) << 1) + 1;
        }
        else
        {
            inValue = inValue << 1;
        }

        // Encode 5 bits at a time starting from least significant bits
        while (true)
        {
            int currentDigit = inValue & 31; // 11111
            inValue = inValue >> 5;
            if (inValue > 0)
            {
                // There are still more digits to decode, set the msb (6th bit)
                currentDigit = currentDigit | 32;
            }
            AppendMappingCharCode(Base64FormatEncode(currentDigit));
            if (inValue <= 0)
            {
                break;
            }
        }
    }

    // Go: func (gen *Generator) commitPendingMapping()
    private void CommitPendingMapping()
    {
        if (!ShouldCommitMapping())
        {
            return;
        }

        // Line/Comma delimiters
        if (lastGeneratedLine < pendingGeneratedLine)
        {
            // Emit line delimiters
            while (true)
            {
                AppendMappingCharCode(';');
                lastGeneratedLine++;
                if (lastGeneratedLine >= pendingGeneratedLine)
                {
                    break;
                }
            }
            // Only need to set this once
            lastGeneratedCharacter = 0;
        }
        else
        {
            if (lastGeneratedLine != pendingGeneratedLine)
            {
                // panic rather than error as an invariant has been violated
                throw new InvalidOperationException("generatedLine cannot backtrack");
            }
            // Emit comma to separate the entry
            if (hasLast)
            {
                AppendMappingCharCode(',');
            }
        }

        // 1. Relative generated character
        AppendBase64VLQ(pendingGeneratedCharacter - lastGeneratedCharacter);
        lastGeneratedCharacter = pendingGeneratedCharacter;

        if (hasPendingSource)
        {
            // 2. Relative sourceIndex
            AppendBase64VLQ(pendingSourceIndex - lastSourceIndex);
            lastSourceIndex = pendingSourceIndex;

            // 3. Relative source line
            AppendBase64VLQ(pendingSourceLine - lastSourceLine);
            lastSourceLine = pendingSourceLine;

            // 4. Relative source character
            AppendBase64VLQ(pendingSourceCharacter - lastSourceCharacter);
            lastSourceCharacter = pendingSourceCharacter;

            if (hasPendingName)
            {
                // 5. Relative nameIndex
                AppendBase64VLQ(pendingNameIndex - lastNameIndex);
                lastNameIndex = pendingNameIndex;
            }
        }

        hasLast = true;
    }

    // Go: func (gen *Generator) addMapping(generatedLine int, generatedCharacter core.UTF16Offset, sourceIndex SourceIndex, sourceLine int, sourceCharacter core.UTF16Offset, nameIndex NameIndex)
    private void AddMapping(int generatedLine, int generatedCharacter, SourceIndex sourceIndex, int sourceLine, int sourceCharacter, NameIndex nameIndex)
    {
        if (IsNewGeneratedPosition(generatedLine, generatedCharacter) ||
            IsBacktrackingSourcePosition(sourceIndex, sourceLine, sourceCharacter))
        {
            CommitPendingMapping();
            pendingGeneratedLine = generatedLine;
            pendingGeneratedCharacter = generatedCharacter;
            hasPendingSource = false;
            hasPendingName = false;
            hasPending = true;
        }

        if (sourceIndex != SourceIndexNotSet && sourceLine != NotSet && sourceCharacter != NotSetUTF16)
        {
            pendingSourceIndex = sourceIndex;
            pendingSourceLine = sourceLine;
            pendingSourceCharacter = sourceCharacter;
            hasPendingSource = true;
            if (nameIndex != NameIndexNotSet)
            {
                pendingNameIndex = nameIndex;
                hasPendingName = true;
            }
        }
    }

    // Adds a mapping without source information
    // Go: func (gen *Generator) AddGeneratedMapping(generatedLine int, generatedCharacter core.UTF16Offset) error
    public string? AddGeneratedMapping(int generatedLine, int generatedCharacter)
    {
        if (generatedLine < pendingGeneratedLine)
        {
            return "generatedLine cannot backtrack";
        }
        if (generatedCharacter < 0)
        {
            return "generatedCharacter cannot be negative";
        }
        AddMapping(generatedLine, generatedCharacter, SourceIndexNotSet, NotSet /*sourceLine*/, NotSetUTF16 /*sourceCharacter*/, NameIndexNotSet);
        return null;
    }

    // Adds a mapping with source information
    // Go: func (gen *Generator) AddSourceMapping(generatedLine int, generatedCharacter core.UTF16Offset, sourceIndex SourceIndex, sourceLine int, sourceCharacter core.UTF16Offset) error
    public string? AddSourceMapping(int generatedLine, int generatedCharacter, SourceIndex sourceIndex, int sourceLine, int sourceCharacter)
    {
        if (generatedLine < pendingGeneratedLine)
        {
            return "generatedLine cannot backtrack";
        }
        if (generatedCharacter < 0)
        {
            return "generatedCharacter cannot be negative";
        }
        if (sourceIndex < 0 || sourceIndex >= sources.Count)
        {
            return "sourceIndex is out of range";
        }
        if (sourceLine < 0)
        {
            return "sourceLine cannot be negative";
        }
        if (sourceCharacter < 0)
        {
            return "sourceCharacter cannot be negative";
        }
        AddMapping(generatedLine, generatedCharacter, sourceIndex, sourceLine, sourceCharacter, NameIndexNotSet);
        return null;
    }

    // Adds a mapping with source and name information
    // Go: func (gen *Generator) AddNamedSourceMapping(generatedLine int, generatedCharacter core.UTF16Offset, sourceIndex SourceIndex, sourceLine int, sourceCharacter core.UTF16Offset, nameIndex NameIndex) error
    public string? AddNamedSourceMapping(int generatedLine, int generatedCharacter, SourceIndex sourceIndex, int sourceLine, int sourceCharacter, NameIndex nameIndex)
    {
        if (generatedLine < pendingGeneratedLine)
        {
            return "generatedLine cannot backtrack";
        }
        if (generatedCharacter < 0)
        {
            return "generatedCharacter cannot be negative";
        }
        if (sourceIndex < 0 || sourceIndex >= sources.Count)
        {
            return "sourceIndex is out of range";
        }
        if (sourceLine < 0)
        {
            return "sourceLine cannot be negative";
        }
        if (sourceCharacter < 0)
        {
            return "sourceCharacter cannot be negative";
        }
        if (nameIndex < 0 || nameIndex >= names.Count)
        {
            return "nameIndex is out of range";
        }
        AddMapping(generatedLine, generatedCharacter, sourceIndex, sourceLine, sourceCharacter, nameIndex);
        return null;
    }

    // Gets the source map as a `RawSourceMap` object
    // Go: func (gen *Generator) RawSourceMap() *RawSourceMap
    public RawSourceMap RawSourceMap()
    {
        CommitPendingMapping();
        // Go: slices.Clone(...) then nil→[]string{}. ToArray() of the empty list already yields
        // a non-null empty array, matching the post-clone nil replacement.
        string[] clonedSources = sources.ToArray();
        string[] clonedNames = names.ToArray();
        return new RawSourceMap
        {
            Version = 3,
            File = file,
            SourceRoot = sourceRoot,
            Sources = clonedSources,
            Names = clonedNames,
            Mappings = mappings.ToString(),
            SourcesContent = sourcesContent?.ToArray(),
        };
    }

    // Go: func (gen *Generator) bytes() []byte { json.Marshal(gen.RawSourceMap()) }
    // Hand-written Utf8JsonWriter (07-aot §4.2-6): key order = Go struct declaration order,
    // sourcesContent omitted when nil (omitzero), UnsafeRelaxedJsonEscaping for non-ASCII
    // pass-through and to avoid escaping < > & (matches go-json-experiment defaults).
    private byte[] Bytes()
    {
        RawSourceMap raw = RawSourceMap();
        var buffer = new ArrayBufferWriter<byte>();
        var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            WriteRawSourceMap(writer, raw);
        }
        return buffer.WrittenSpan.ToArray();
    }

    // Gets the string representation of the source map
    // Go: func (gen *Generator) String() string { return string(gen.bytes()) }
    public string String()
    {
        return Encoding.UTF8.GetString(Bytes());
    }

    // Go: func (gen *Generator) Base64DataURL() string
    public string Base64DataURL()
    {
        const string prefix = "data:application/json;base64,";
        byte[] data = Bytes();
        return prefix + Convert.ToBase64String(data);
    }

    private static void WriteRawSourceMap(Utf8JsonWriter writer, RawSourceMap raw)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version"u8, raw.Version);
        writer.WriteString("file"u8, raw.File);
        writer.WriteString("sourceRoot"u8, raw.SourceRoot);
        writer.WriteStartArray("sources"u8);
        foreach (string s in raw.Sources)
        {
            writer.WriteStringValue(s);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("names"u8);
        foreach (string n in raw.Names)
        {
            writer.WriteStringValue(n);
        }
        writer.WriteEndArray();
        writer.WriteString("mappings"u8, raw.Mappings);
        // json:"sourcesContent,omitzero" — emit only when the slice is non-nil.
        if (raw.SourcesContent != null)
        {
            writer.WriteStartArray("sourcesContent"u8);
            foreach (string? c in raw.SourcesContent)
            {
                if (c == null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStringValue(c);
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    // Go: func base64FormatEncode(value int) rune
    private static char Base64FormatEncode(int value)
    {
        if (value >= 0 && value < 26)
        {
            return (char)('A' + value);
        }
        if (value >= 26 && value < 52)
        {
            return (char)('a' + value - 26);
        }
        if (value >= 52 && value < 62)
        {
            return (char)('0' + value - 52);
        }
        if (value == 62)
        {
            return '+';
        }
        if (value == 63)
        {
            return '/';
        }
        throw new InvalidOperationException("not a base64 value");
    }
}

// Go: type RawSourceMap struct { ... `json:"..."` }. Field order below IS the JSON key order.
public sealed class RawSourceMap
{
    public int Version;                  // json:"version"
    public string File = "";             // json:"file"
    public string SourceRoot = "";       // json:"sourceRoot"
    public string[] Sources = [];        // json:"sources"
    public string[] Names = [];          // json:"names"
    public string Mappings = "";         // json:"mappings"
    public string?[]? SourcesContent;    // json:"sourcesContent,omitzero" ([]*string)
}
