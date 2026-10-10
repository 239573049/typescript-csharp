// Ported from tsgo internal/format/indent_test.go (whole file).
//
// PORT-NOTE: Go threads lsutil.FormatCodeSettings through the format package; the
// FormatCodeSettings type lives in Ts.Format here (Format/FormatCodeSettings.cs).
// Indent.GetContainingList is internal and visible via InternalsVisibleTo
// (plan/05-conventions.md §16).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Format;
using Ts.Parser;
using Ts.TsPath;
using Xunit;

namespace Ts.Format.Tests;

public sealed class IndentTests
{
    // indent_test.go:13-40 — TestGetContainingList_NamedImports.
    [Fact]
    public void GetContainingList_NamedImports()
    {
        const string text = """
        import type {
            AAA,
            BBB,
        } from "./bar";
        """;

        var parseOptions = new SourceFileParseOptions
        {
            FileName = "/test.ts",
            Path = new Path("/test.ts"),
        };
        SourceFile sourceFile = global::Ts.Parser.Parser.ParseSourceFile(parseOptions, text, ScriptKind.TS);

        // Find ImportSpecifier nodes (AAA and BBB).
        var importSpecifiers = new List<Node>();
        ForEachDescendantOfKind(sourceFile.AsNode(), SyntaxKind.ImportSpecifier, importSpecifiers.Add);

        Assert.True(importSpecifiers.Count == 2, $"Expected 2 import specifiers, got {importSpecifiers.Count}");

        // Test GetContainingList for each import specifier.
        foreach (Node specifier in importSpecifiers)
        {
            NodeList? list = Indent.GetContainingList(specifier, sourceFile);
            Assert.NotNull(list);
            Assert.True(list!.Nodes.Length == 2, $"Expected list with 2 elements, got {list.Nodes.Length}");
        }
    }

    // indent_test.go:42-50 — forEachDescendantOfKind.
    private static void ForEachDescendantOfKind(Node node, SyntaxKind kind, System.Action<Node> action)
    {
        node.ForEachChild(child =>
        {
            if (child.Kind == kind)
            {
                action(child);
            }
            ForEachDescendantOfKind(child, kind, action);
            return false;
        });
    }
}
