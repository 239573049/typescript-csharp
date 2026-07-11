using Ts.Ast;
using Ts.Core;
using Ts.TestUtil;
using Xunit;

namespace Ts.Conformance.Tests;

public sealed class ErrorBaselineTests
{
    [Fact]
    public void SingleFileAssignmentDiagnostic_MatchesGoErrorsBaselineFormat()
    {
        const string sourceText = "const x: number = \"hi\";";
        var sourceFile = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions
            {
                FileName = "/.src/simple.ts",
                Path = new global::Ts.TsPath.Path("/.src/simple.ts"),
            },
            sourceText,
            ScriptKind.TS);
        int start = sourceText.IndexOf('"', System.StringComparison.Ordinal);
        Diagnostic diagnostic = Diagnostic.NewDiagnostic(
            sourceFile,
            new TextRange(start, start + 4),
            global::Ts.Diagnostics.Diagnostics.Type_0_is_not_assignable_to_type_1,
            "string",
            "number");

        string actual = ErrorBaseline.GetErrorBaseline(
            [new BaselineTestFile("/.src/simple.ts", sourceText)],
            [diagnostic]);

        Assert.Equal(
            "simple.ts(1,19): error TS2322: Type 'string' is not assignable to type 'number'.\r\n" +
            "\r\n" +
            "\r\n" +
            "==== simple.ts (1 errors) ====\r\n" +
            "    const x: number = \"hi\";\r\n" +
            "                      ~~~~\r\n" +
            "!!! error TS2322: Type 'string' is not assignable to type 'number'.",
            actual);
    }
}
