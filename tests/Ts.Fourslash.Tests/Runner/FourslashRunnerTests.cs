// Fourslash runner smoke tests. Each test embeds the corresponding Go fourslash test's
// `content` verbatim (raw string, tabs and all — the bodies are TestBasicEdit,
// TestBasicBackspace, TestBasicReplaceLine, TestBasicMultifileCompletions,
// TestBasicInterfaceMembers, TestBasicQuickInfo from internal/fourslash/tests/, and
// TestAsConstRefsNoErrors2 from tests/gen/) and re-expresses its assertions against the C#
// harness. Recorded adaptations from Go, all forced by foundation seams (see the report):
//   - Completion ItemDefaults.CommitCharacters assertions are dropped: Go receives them
//     because the negotiated initialize capabilities turn itemDefaults on; the C# raw LS
//     entry has no capability carrier, so ItemDefaults is never populated.
//   - TestBasicQuickInfo's second documentation expectation ("Other var\nSee [someVar](file:///…)")
//     is Go's UTF-8 hover markdown; the foundation Documentation field is plain text, so the
//     test pins the display string and the documentation prefix.
//   - TestAsConstRefsNoErrors2's VerifyBaselineGoToDefinition call is dropped (reference
//     baselines arrive with the testdata mirror; plan/08 §4.8); VerifyNoErrors is kept.

using System;
using System.Linq;
using Ts.TestUtil.Fourslash;
using LsProto = global::Ts.LsProto;
using Xunit;

namespace Ts.Fourslash.Tests.Runner;

public sealed class FourslashRunnerTests
{
    private const string SortTextLocationPriority = "11"; // ls.SortTextLocationPriority

    [Fact]
    public void TestBasicEdit()
    {
        SkipIfFailing.Check();
        const string content = """
export {};
interface Point {
	x: number;
	y: number;
}
declare const p: Point;
p/*a*/
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "basicEdit", TestContext.Current.CancellationToken);
        f.GoToMarker("a");
        f.Insert(".");
        f.GoToEOF();
        f.VerifyCompletions(null, new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Exact =
                [
                    new LsProto.CompletionItem
                    {
                        Label = "x",
                        Kind = LsProto.CompletionItemKind.Field,
                        SortText = SortTextLocationPriority,
                    },
                    "y",
                ],
            },
        });
    }

    [Fact]
    public void TestBasicBackspace()
    {
        SkipIfFailing.Check();
        const string content = """
export {};
interface Point {
	x: number;
	y: number;/*b*/
}
declare const p: Point;
p./*a*/
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "basicBackspace", TestContext.Current.CancellationToken);
        f.VerifyCompletions("a", new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Includes = ["y"],
            },
        });
        f.GoToMarker("b");
        f.Backspace(10); // `y: number;`
        f.VerifyCompletions("a", new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Excludes = ["y"],
            },
        });
    }

    [Fact]
    public void TestBasicReplaceLine()
    {
        SkipIfFailing.Check();
        const string content = """
export {};
interface Point {
	x: number;
	y: number;
}
declare const p: Point;
p./*a*/
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "basicReplaceLine", TestContext.Current.CancellationToken);
        f.VerifyCompletions("a", new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Includes = ["y"],
            },
        });
        f.ReplaceLine(3, "\tz: number;"); // `y: number;`
        f.VerifyCompletions("a", new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Excludes = ["y"],
                Includes = ["z"],
            },
        });
    }

    [Fact]
    public void TestBasicMultifileCompletions()
    {
        SkipIfFailing.Check();
        const string content = """
// @Filename: /a.ts
export const foo = { bar: 'baz' };

// @Filename: /b.ts
import { foo } from './a';
const test = foo./*1*/
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "basicMultifileCompletions", TestContext.Current.CancellationToken);
        f.VerifyCompletions("1", new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Includes =
                [
                    new LsProto.CompletionItem
                    {
                        Label = "bar",
                        Kind = LsProto.CompletionItemKind.Field,
                        SortText = SortTextLocationPriority,
                    },
                ],
            },
        });
    }

    [Fact]
    public void TestBasicInterfaceMembers()
    {
        SkipIfFailing.Check();
        const string content = """
export {};
interface Point {
	x: number;
	y: number;
}
declare const p: Point;
p./*a*/
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "basicInterfaceMembers", TestContext.Current.CancellationToken);
        f.VerifyCompletions("a", new FourslashTest.CompletionsExpectedList
        {
            IsIncomplete = false,
            Items = new FourslashTest.CompletionsExpectedItems
            {
                Exact =
                [
                    new LsProto.CompletionItem
                    {
                        Label = "x",
                        Kind = LsProto.CompletionItemKind.Field,
                        SortText = SortTextLocationPriority,
                    },
                    "y",
                ],
            },
        });
    }

    [Fact]
    public void TestBasicQuickInfo()
    {
        SkipIfFailing.Check();
        const string content = """

/**
 * Some var
 */
var someVar/*1*/ = 123;

/**
 * Other var
 * See {@link someVar}
 */
var otherVar/*2*/ = someVar;

class Foo/*3*/ {
	#bar: string;
}
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "basicQuickInfo", TestContext.Current.CancellationToken);
        f.VerifyQuickInfoAt("1", "var someVar: number", "Some var");

        // Go's second expectation is the hover markdown with an LS link
        // ("Other var\nSee [someVar](file:///basicQuickInfo.ts#5,5-5,12)"); the foundation's
        // Documentation is plain text, so pin display string + documentation prefix.
        f.GoToMarker("2");
        Ts.Ls.QuickInfo hover = f.getQuickInfoAtCurrentPosition();
        Assert.Equal("var otherVar: number", hover.DisplayString);
        Assert.StartsWith("Other var", hover.Documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void TestAsConstRefsNoErrors2()
    {
        SkipIfFailing.Check();
        // Code generated by convertFourslash; DO NOT EDIT.
        // To modify this test, run "npm run makemanual asConstRefsNoErrors2"
        const string content = """
class Tex {
    type = </**/const>'Text';
}
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "asConstRefsNoErrors2", TestContext.Current.CancellationToken);
        // (VerifyBaselineGoToDefinition dropped — see file header.)
        f.VerifyNoErrors();
    }

    [Fact]
    public void TestRenameLocalVariable()
    {
        SkipIfFailing.Check();
        // Corpus: the plain-identifier rename flow exercised by fourslash rename tests (e.g.
        // tests/gen rename coverage); content authored for this smoke because every Go
        // VerifyRename test in the corpus goes through file renames or baselines.
        const string content = """
export {};
const value = 1;
const other = value/*use*/ + value;
""";

        using FourslashTest f = FourslashTest.New(content, fileName: "renameLocalVariable", TestContext.Current.CancellationToken);
        f.VerifyRename(
            "use",
            "renamed",
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["/renameLocalVariable.ts"] = "export {};\nconst renamed = 1;\nconst other = renamed + renamed;",
            });
    }
}
