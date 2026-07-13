using System;
using System.Collections.Generic;
using global::Ts.TestUtil;
using Xunit;

namespace Ts.Conformance.Tests;

public sealed class TypeSymbolBaselineTests
{
    [Fact]
    public void GenerateBaseline_PreservesGoWalkLayoutAndBraceContinuation()
    {
        BaselineTestFile[] files =
        [
            new BaselineTestFile(
                "/.src/sample.ts",
                "const a = 1;\n{\n    a;\n}"),
        ];
        TypeSymbolBaselineEntry[] entries =
        [
            new TypeSymbolBaselineEntry(0, "a", "number"),
            new TypeSymbolBaselineEntry(0, "1", "1"),
            new TypeSymbolBaselineEntry(2, "a", "number"),
        ];

        string actual = TypeSymbolBaseline.GenerateBaseline(
            files,
            "tests/cases/conformance/sample.ts",
            isSymbolBaseline: false,
            _ => entries);

        const string expected =
            "//// [tests/cases/conformance/sample.ts] ////\r\n\r\n" +
            "=== sample.ts ===\r\n" +
            "const a = 1;\r\n" +
            ">a : number\r\n" +
            ">1 : 1\r\n" +
            "{\r\n" +
            "    a;\r\n" +
            ">a : number\r\n" +
            "}\r\n";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GenerateBaseline_InsertsBlankLineBetweenOrdinarySourceGroups()
    {
        BaselineTestFile[] files = [new BaselineTestFile("/.src/two.ts", "a;\nb;")];
        TypeSymbolBaselineEntry[] entries =
        [
            new TypeSymbolBaselineEntry(0, "a", "number"),
            new TypeSymbolBaselineEntry(1, "b", "string"),
        ];

        string actual = TypeSymbolBaseline.GenerateBaseline(
            files,
            "two.ts",
            isSymbolBaseline: false,
            _ => entries);

        Assert.Contains(">a : number\r\n\r\nb;\r\n>b : string", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateBaseline_EmptyFileListUsesNoContentSentinel()
    {
        string actual = TypeSymbolBaseline.GenerateBaseline(
            Array.Empty<BaselineTestFile>(),
            "empty.ts",
            isSymbolBaseline: false,
            _ => Array.Empty<TypeSymbolBaselineEntry>());

        Assert.Equal(Baseline.NoContent, actual);
    }

    [Fact]
    public void GenerateBaseline_SymbolEmptyEntryAbortsLikeGoWriter()
    {
        BaselineTestFile[] files =
        [
            new BaselineTestFile("/.src/first.ts", "a;"),
            new BaselineTestFile("/.src/second.ts", "b;"),
        ];

        string actual = TypeSymbolBaseline.GenerateBaseline(
            files,
            "symbols.ts",
            isSymbolBaseline: true,
            file => file.UnitName.EndsWith("first.ts", StringComparison.Ordinal)
                ? [new TypeSymbolBaselineEntry(0, "a", "")]
                : [new TypeSymbolBaselineEntry(0, "b", "Symbol(b)")]);

        Assert.Equal(Baseline.NoContent, actual);
    }

    [Fact]
    public void FixupOldTypeBaseline_RemovesReuseAndPerformanceLinesAndDotSlash()
    {
        const string oldBaseline =
            "=== Performance Stats ===\n" +
            "Type Count: 123\n" +
            "=== ./sample.ts ===\n" +
            ">x : number\n" +
            ">x : ^^^\n";

        string actual = TypeSymbolBaseline.FixupOldTypeBaseline(oldBaseline);

        Assert.Equal("=== sample.ts ===\n>x : number\n", actual);
    }

    [Fact]
    public void GenerateBaseline_RejectsDormantUnderlinePath()
    {
        BaselineTestFile[] files = [new BaselineTestFile("/.src/u.ts", "x;")];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            TypeSymbolBaseline.GenerateBaseline(
                files,
                "u.ts",
                isSymbolBaseline: false,
                _ => [new TypeSymbolBaselineEntry(0, "x", "number", "^^")]));

        Assert.Contains("intentionally unsupported", exception.Message, StringComparison.Ordinal);
    }
}
