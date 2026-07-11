// Ported from tsgo internal/sourcemap/decoder.go

using System.Collections.Generic;
using Ts.Core;

namespace Ts.SourceMap;

// Go: type (SourceIndex int; NameIndex int) are declared in generator.go and used here.
// plan/05 §2.3: no-method named int types dissolve to `int` (like TextPos/UTF16Offset), kept
// as file-scoped aliases for grep fidelity against the Go source.
using SourceIndex = int;
using NameIndex = int;

// Go: type Mapping struct { ... } used as *Mapping (pointer) → reference type (class).
public sealed class Mapping
{
    // Go decoder.go package-level sentinels. Homed on Mapping (they are its sentinel values);
    // referenced as Mapping.MissingSource etc. UTF16Offset dissolves to int, so all are int -1.
    public const SourceIndex MissingSource = -1;
    public const NameIndex MissingName = -1;
    public const int MissingLineOrColumn = -1;
    public const int MissingUTF16Column = -1;

    public int GeneratedLine;
    public int GeneratedCharacter; // Go core.UTF16Offset → int
    public SourceIndex SourceIndex;
    public int SourceLine;
    public int SourceCharacter; // Go core.UTF16Offset → int
    public NameIndex NameIndex;

    // Go: func (m *Mapping) Equals(other *Mapping) bool
    // Overload of object.Equals(object) — reference identity is preserved for default
    // comparers (no IEquatable, no override), matching Go's per-field explicit method.
    public bool Equals(Mapping? other)
    {
        return this == other || (other != null &&
            GeneratedLine == other.GeneratedLine &&
            GeneratedCharacter == other.GeneratedCharacter &&
            SourceIndex == other.SourceIndex &&
            SourceLine == other.SourceLine &&
            SourceCharacter == other.SourceCharacter &&
            NameIndex == other.NameIndex);
    }

    // Go: func (m *Mapping) IsSourceMapping() bool
    public bool IsSourceMapping()
    {
        return SourceIndex != MissingSource &&
            SourceLine != MissingLineOrColumn &&
            SourceCharacter != MissingUTF16Column;
    }
}

// Go: type MappingsDecoder struct { ... }
public sealed class MappingsDecoder
{
    private readonly string mappings;
    private bool done;
    private int pos;
    private int generatedLine;
    private int generatedCharacter; // Go core.UTF16Offset → int
    private SourceIndex sourceIndex;
    private int sourceLine;
    private int sourceCharacter; // Go core.UTF16Offset → int
    private NameIndex nameIndex;

    // Go: error → string? (null = no error). plan/05 §4: error text is captured verbatim.
    private string? error;

    // Go field `mappingArena core.Arena[Mapping]` is dropped — AGENTS §5: arena.New() → new T().

    private MappingsDecoder(string mappings)
    {
        this.mappings = mappings;
    }

    // Go: func DecodeMappings(mappings string) *MappingsDecoder
    public static MappingsDecoder DecodeMappings(string mappings)
    {
        return new MappingsDecoder(mappings);
    }

    // Go: func (d *MappingsDecoder) MappingsString() string
    public string MappingsString()
    {
        return mappings;
    }

    // Go: func (d *MappingsDecoder) Pos() int
    public int Pos()
    {
        return pos;
    }

    // Go: func (d *MappingsDecoder) Error() error
    public string? Error()
    {
        return error;
    }

    // Go: func (d *MappingsDecoder) State() *Mapping
    public Mapping State()
    {
        return CaptureMapping(true /*hasSource*/, true /*hasName*/);
    }

    // Go: func (d *MappingsDecoder) Values() iter.Seq[*Mapping]
    public IEnumerable<Mapping> Values()
    {
        var (value, isDone) = Next();
        while (!isDone)
        {
            // value is non-null whenever !done (stopIterating sets done before returning nil).
            yield return value!;
            (value, isDone) = Next();
        }
    }

    // Go: func (d *MappingsDecoder) Next() (value *Mapping, done bool)
    public (Mapping? Value, bool Done) Next()
    {
        while (!done && pos < mappings.Length)
        {
            char ch = mappings[pos];
            if (ch == ';')
            {
                // new line
                generatedLine++;
                generatedCharacter = 0;
                pos++;
                continue;
            }

            if (ch == ',')
            {
                // Next entry is on same line - no action needed
                pos++;
                continue;
            }

            bool hasSource = false;
            bool hasName = false;
            generatedCharacter += Base64VLQFormatDecode();
            if (HasReportedError())
            {
                return StopIterating();
            }
            if (generatedCharacter < 0)
            {
                return SetErrorAndStopIterating("Invalid generatedCharacter found");
            }

            if (!IsSourceMappingSegmentEnd())
            {
                hasSource = true;

                sourceIndex += Base64VLQFormatDecode();
                if (HasReportedError())
                {
                    return StopIterating();
                }
                if (sourceIndex < 0)
                {
                    return SetErrorAndStopIterating("Invalid sourceIndex found");
                }
                if (IsSourceMappingSegmentEnd())
                {
                    return SetErrorAndStopIterating("Unsupported Format: No entries after sourceIndex");
                }

                sourceLine += Base64VLQFormatDecode();
                if (HasReportedError())
                {
                    return StopIterating();
                }
                if (sourceLine < 0)
                {
                    return SetErrorAndStopIterating("Invalid sourceLine found");
                }
                if (IsSourceMappingSegmentEnd())
                {
                    return SetErrorAndStopIterating("Unsupported Format: No entries after sourceLine");
                }

                sourceCharacter += Base64VLQFormatDecode();
                if (HasReportedError())
                {
                    return StopIterating();
                }
                if (sourceCharacter < 0)
                {
                    return SetErrorAndStopIterating("Invalid sourceCharacter found");
                }

                if (!IsSourceMappingSegmentEnd())
                {
                    hasName = true;
                    nameIndex += Base64VLQFormatDecode();
                    if (HasReportedError())
                    {
                        return StopIterating();
                    }
                    if (nameIndex < 0)
                    {
                        return SetErrorAndStopIterating("Invalid nameIndex found");
                    }

                    if (!IsSourceMappingSegmentEnd())
                    {
                        return SetErrorAndStopIterating("Unsupported Error Format: Entries after nameIndex");
                    }
                }
            }

            return (CaptureMapping(hasSource, hasName), false);
        }

        return StopIterating();
    }

    // Go: func (d *MappingsDecoder) captureMapping(hasSource bool, hasName bool) *Mapping
    private Mapping CaptureMapping(bool hasSource, bool hasName)
    {
        var mapping = new Mapping(); // Go: d.mappingArena.New()
        mapping.GeneratedLine = generatedLine;
        mapping.GeneratedCharacter = generatedCharacter;
        mapping.SourceIndex = CoreUtil.IfElse(hasSource, sourceIndex, Mapping.MissingSource);
        mapping.SourceLine = CoreUtil.IfElse(hasSource, sourceLine, Mapping.MissingLineOrColumn);
        mapping.SourceCharacter = CoreUtil.IfElse(hasSource, sourceCharacter, Mapping.MissingUTF16Column);
        mapping.NameIndex = CoreUtil.IfElse(hasName, nameIndex, Mapping.MissingName);
        return mapping;
    }

    // Go: func (d *MappingsDecoder) stopIterating() (*Mapping, bool)
    private (Mapping?, bool) StopIterating()
    {
        done = true;
        return (null, true);
    }

    // Go: func (d *MappingsDecoder) setError(err string)
    private void SetError(string err)
    {
        error = err;
    }

    // Go: func (d *MappingsDecoder) setErrorAndStopIterating(err string) (*Mapping, bool)
    private (Mapping?, bool) SetErrorAndStopIterating(string err)
    {
        SetError(err);
        return StopIterating();
    }

    // Go: func (d *MappingsDecoder) hasReportedError() bool
    private bool HasReportedError()
    {
        return error != null;
    }

    // Go: func (d *MappingsDecoder) isSourceMappingSegmentEnd() bool
    private bool IsSourceMappingSegmentEnd()
    {
        return pos == mappings.Length || mappings[pos] == ',' || mappings[pos] == ';';
    }

    // Go: func (d *MappingsDecoder) base64VLQFormatDecode() int
    private int Base64VLQFormatDecode()
    {
        bool moreDigits = true;
        int shiftCount = 0;
        int value = 0;
        for (; moreDigits; pos++)
        {
            if (pos >= mappings.Length)
            {
                SetError("Error in decoding base64VLQFormatDecode, past the mapping string");
                return -1;
            }

            // 6 digit number
            int currentByte = Base64FormatDecode(mappings[pos]);
            if (currentByte == -1)
            {
                SetError("Invalid character in VLQ");
                return -1;
            }

            // If msb is set, we still have more bits to continue
            moreDigits = (currentByte & 32) != 0;

            // least significant 5 bits are the next msbs in the final value.
            value = value | ((currentByte & 31) << shiftCount);
            shiftCount += 5;
        }

        // Least significant bit if 1 represents negative and rest of the msb is actual absolute value
        if ((value & 1) == 0)
        {
            // + number
            value = value >> 1;
        }
        else
        {
            // - number
            value = value >> 1;
            value = -value;
        }

        return value;
    }

    // Go: func base64FormatDecode(ch byte) int. `mappings[pos]` is a UTF-16 char here; VLQ
    // characters are all ASCII, so char values match the Go byte values.
    private static int Base64FormatDecode(char ch)
    {
        if (ch >= 'A' && ch <= 'Z')
        {
            return ch - 'A';
        }
        if (ch >= 'a' && ch <= 'z')
        {
            return ch - 'a' + 26;
        }
        if (ch >= '0' && ch <= '9')
        {
            return ch - '0' + 52;
        }
        if (ch == '+')
        {
            return 62;
        }
        if (ch == '/')
        {
            return 63;
        }
        return -1;
    }
}
