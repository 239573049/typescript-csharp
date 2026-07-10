// Item-for-item verification of the transcribed Unicode tables (plan/04-codegen.md
// §6.2 "验证" clause): every {lo, hi, stride} triple, LatinOffset, and special-casing
// mapping in the .g.cs products must equal the corresponding entry of tsgo's
// generated Go tables. The oracle is fixtures/stringutil/*, a verbatim copy of
// tsgo internal/stringutil/{js_case,identifier_parts}_generated.go; this parser is
// deliberately independent of the transcriber (tools/generator/transcribe-unicode.ts)
// so the two cross-check each other.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Ts.TestUtil;
using Xunit;

namespace Ts.StringUtil.Tests;

public partial class UnicodeTablesTests
{
    private readonly record struct GoRange(int Lo, int Hi, int Stride);

    private sealed record GoRangeTable(List<GoRange> R16, List<GoRange> R32, int LatinOffset);

    private sealed record GoSpecialCasing(int Key, string Lower, string Upper, string ConditionalLower, byte Condition);

    [GeneratedRegex(@"^\t\t\{(0x[0-9A-F]+), (0x[0-9A-F]+), (\d+)\},$")]
    private static partial Regex RangeEntryRegex();

    [GeneratedRegex(@"^\tLatinOffset: (\d+),$")]
    private static partial Regex LatinOffsetRegex();

    [GeneratedRegex(@"^\t(0x[0-9A-F]+): +\{lower: ""((?:\\u[0-9A-F]{4}|\\U[0-9A-F]{8})*)"", upper: ""((?:\\u[0-9A-F]{4}|\\U[0-9A-F]{8})*)"",(?: conditionalLower: ""((?:\\u[0-9A-F]{4}|\\U[0-9A-F]{8})*)"",)? condition: specialCasingCondition(None|FinalSigma)\},$")]
    private static partial Regex CasingEntryRegex();

    private static string[] FixtureLines(string name)
    {
        string path = Path.Combine(RepoPaths.RootPath, "fixtures", "stringutil", name);
        return File.ReadAllText(path).Split('\n');
    }

    private static int ParseHexOrInt(string text)
    {
        return text.StartsWith("0x", StringComparison.Ordinal)
            ? int.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static GoRangeTable ParseRangeTable(string[] lines, string name)
    {
        int i = Array.IndexOf(lines, $"var {name} = &unicode.RangeTable{{");
        Assert.True(i >= 0, $"table {name} not found in fixture");
        i++;
        Assert.Equal("\tR16: []unicode.Range16{", lines[i]);
        i++;
        var r16 = new List<GoRange>();
        while (lines[i] != "\t},")
        {
            r16.Add(ParseRangeEntry(lines[i], name));
            i++;
        }
        i++;
        Assert.Equal("\tR32: []unicode.Range32{", lines[i]);
        i++;
        var r32 = new List<GoRange>();
        while (lines[i] != "\t},")
        {
            r32.Add(ParseRangeEntry(lines[i], name));
            i++;
        }
        i++;
        Match m = LatinOffsetRegex().Match(lines[i]);
        Assert.True(m.Success, $"{name}: expected LatinOffset, got {lines[i]}");
        i++;
        Assert.Equal("}", lines[i]);
        return new GoRangeTable(r16, r32, ParseHexOrInt(m.Groups[1].Value));
    }

    private static GoRange ParseRangeEntry(string line, string name)
    {
        Match m = RangeEntryRegex().Match(line);
        Assert.True(m.Success, $"{name}: unparseable range entry {line}");
        return new GoRange(ParseHexOrInt(m.Groups[1].Value), ParseHexOrInt(m.Groups[2].Value), ParseHexOrInt(m.Groups[3].Value));
    }

    // Decodes Go's \uXXXX / \UXXXXXXXX escapes.
    private static string DecodeGoEscapes(string raw)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < raw.Length;)
        {
            Assert.Equal('\\', raw[i]);
            char kind = raw[i + 1];
            Assert.True(kind is 'u' or 'U', $"unexpected escape kind {kind}");
            int len = kind == 'u' ? 4 : 8;
            int cp = int.Parse(raw.AsSpan(i + 2, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            sb.Append(char.ConvertFromUtf32(cp));
            i += 2 + len;
        }
        return sb.ToString();
    }

    private static List<GoSpecialCasing> ParseSpecialCasings(string[] lines)
    {
        int i = Array.IndexOf(lines, "var specialCasingMappings = map[rune]specialCasingMapping{");
        Assert.True(i >= 0, "specialCasingMappings not found in fixture");
        i++;
        var entries = new List<GoSpecialCasing>();
        while (lines[i] != "}")
        {
            Match m = CasingEntryRegex().Match(lines[i]);
            Assert.True(m.Success, $"unparseable special-casing entry {lines[i]}");
            entries.Add(new GoSpecialCasing(
                ParseHexOrInt(m.Groups[1].Value),
                DecodeGoEscapes(m.Groups[2].Value),
                DecodeGoEscapes(m.Groups[3].Value),
                m.Groups[4].Success ? DecodeGoEscapes(m.Groups[4].Value) : "",
                m.Groups[5].Value == "FinalSigma" ? (byte)1 : (byte)0));
            i++;
        }
        return entries;
    }

    private static void AssertRangeTableEqual(GoRangeTable go, ReadOnlySpan<ushort> r16, ReadOnlySpan<uint> r32, int latinOffset, string name)
    {
        Assert.True(go.R16.Count * 3 == r16.Length, $"{name}: R16 count {r16.Length / 3}, Go has {go.R16.Count}");
        for (int i = 0; i < go.R16.Count; i++)
        {
            GoRange g = go.R16[i];
            Assert.True(g.Lo == r16[3 * i] && g.Hi == r16[3 * i + 1] && g.Stride == r16[3 * i + 2],
                $"{name}: R16[{i}] = {{0x{r16[3 * i]:X}, 0x{r16[3 * i + 1]:X}, {r16[3 * i + 2]}}}, Go has {{0x{g.Lo:X}, 0x{g.Hi:X}, {g.Stride}}}");
        }
        Assert.True(go.R32.Count * 3 == r32.Length, $"{name}: R32 count {r32.Length / 3}, Go has {go.R32.Count}");
        for (int i = 0; i < go.R32.Count; i++)
        {
            GoRange g = go.R32[i];
            Assert.True((uint)g.Lo == r32[3 * i] && (uint)g.Hi == r32[3 * i + 1] && (uint)g.Stride == r32[3 * i + 2],
                $"{name}: R32[{i}] = {{0x{r32[3 * i]:X}, 0x{r32[3 * i + 1]:X}, {r32[3 * i + 2]}}}, Go has {{0x{g.Lo:X}, 0x{g.Hi:X}, {g.Stride}}}");
        }
        Assert.True(go.LatinOffset == latinOffset, $"{name}: LatinOffset {latinOffset}, Go has {go.LatinOffset}");
    }

    [Fact]
    public void IdentifierStartTableMatchesGoItemForItem()
    {
        string[] lines = FixtureLines("identifier_parts_generated.go");
        GoRangeTable go = ParseRangeTable(lines, "unicodeESNextIdentifierStart");
        Assert.NotEmpty(go.R16);
        AssertRangeTableEqual(go, UnicodeTables.IdentifierStartR16, UnicodeTables.IdentifierStartR32, UnicodeTables.IdentifierStartLatinOffset, "IdentifierStart");
    }

    [Fact]
    public void IdentifierPartTableMatchesGoItemForItem()
    {
        string[] lines = FixtureLines("identifier_parts_generated.go");
        GoRangeTable go = ParseRangeTable(lines, "unicodeESNextIdentifierPart");
        Assert.NotEmpty(go.R16);
        AssertRangeTableEqual(go, UnicodeTables.IdentifierPartR16, UnicodeTables.IdentifierPartR32, UnicodeTables.IdentifierPartLatinOffset, "IdentifierPart");
    }

    [Fact]
    public void CasedAndCaseIgnorableTablesMatchGoItemForItem()
    {
        string[] lines = FixtureLines("js_case_generated.go");
        GoRangeTable cased = ParseRangeTable(lines, "unicodeCasedRanges");
        Assert.NotEmpty(cased.R16);
        AssertRangeTableEqual(cased, UnicodeTables.CasedR16, UnicodeTables.CasedR32, UnicodeTables.CasedLatinOffset, "Cased");
        GoRangeTable ignorable = ParseRangeTable(lines, "unicodeCaseIgnorableRanges");
        Assert.NotEmpty(ignorable.R16);
        AssertRangeTableEqual(ignorable, UnicodeTables.CaseIgnorableR16, UnicodeTables.CaseIgnorableR32, UnicodeTables.CaseIgnorableLatinOffset, "CaseIgnorable");
    }

    [Fact]
    public void SpecialCasingMappingsMatchGoItemForItem()
    {
        string[] lines = FixtureLines("js_case_generated.go");
        List<GoSpecialCasing> entries = ParseSpecialCasings(lines);
        Assert.NotEmpty(entries);

        Assert.Equal(entries.Count, UnicodeTables.SpecialCasingKeys.Length);
        Assert.Equal(entries.Count, UnicodeTables.SpecialCasingLower.Length);
        Assert.Equal(entries.Count, UnicodeTables.SpecialCasingUpper.Length);
        Assert.Equal(entries.Count, UnicodeTables.SpecialCasingConditionalLower.Length);
        Assert.Equal(entries.Count, UnicodeTables.SpecialCasingConditions.Length);

        for (int i = 0; i < entries.Count; i++)
        {
            GoSpecialCasing e = entries[i];
            // Keys strictly ascending — the binary-search precondition.
            if (i > 0)
            {
                Assert.True(entries[i - 1].Key < e.Key, $"fixture keys not ascending at index {i}");
            }
            Assert.True((uint)e.Key == UnicodeTables.SpecialCasingKeys[i], $"key[{i}]: 0x{UnicodeTables.SpecialCasingKeys[i]:X}, Go has 0x{e.Key:X}");
            Assert.True(e.Lower == UnicodeTables.SpecialCasingLower[i], $"lower[0x{e.Key:X}] mismatch");
            Assert.True(e.Upper == UnicodeTables.SpecialCasingUpper[i], $"upper[0x{e.Key:X}] mismatch");
            Assert.True(e.ConditionalLower == UnicodeTables.SpecialCasingConditionalLower[i], $"conditionalLower[0x{e.Key:X}] mismatch");
            Assert.True(e.Condition == UnicodeTables.SpecialCasingConditions[i], $"condition[0x{e.Key:X}] mismatch");
        }
    }
}
