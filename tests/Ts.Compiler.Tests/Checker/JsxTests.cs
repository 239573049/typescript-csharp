// Smoke tests for the phase-5 checker jsx wave (plan/03 §4.2 — jsx.go → Checker.Jsx.cs).
//
// The JSX type-check trunk (checkJsxElement/Fragment/SelfClosingElement → getResolvedSignature,
// contextual typing, spread types, call resolution, iteration types, the implicit-JSX-runtime import
// specifier, markJsxAliasReferenced …) routes through cross-cluster stubs (Checker.Jsx.Deps.cs and
// sibling dedup surfaces) that throw NotImplementedException until their owning waves land. So this
// file exercises the wave's PURE, stub-free surface over REAL parsed TSX source:
//   - IsJsxIntrinsicTagName (utilities.go:1093): the tag-name classifier — a lowercase-first
//     identifier or any namespaced name is intrinsic; a capitalized identifier (component) is not.
//     Depends only on Scanner.IsIntrinsicJsxName + NodeTests, no symbol resolution or type computation.
//   - The JsxFlags / JsxReferenceKind enum algebra and the JsxNames / ReactNames name tables
//     (jsx.go:17-69) — carried faithfully from Go.
// The element/attributes end-to-end paths are validated once the contextual-typing and call-resolution
// waves land. (Construction/interning coverage lives in CheckerSmokeTests; do not modify that file.)

using Ts.Ast;
using Ts.Checker;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Checker.Tests;

public class JsxTests
{
    private static SourceFileParseOptions Opts(string fileName = "/test.tsx") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile Parse(string text) =>
        global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, ScriptKind.Tsx);

    private static Node? FindFirst(Node node, SyntaxKind kind)
    {
        Node? found = null;
        void Visit(Node n)
        {
            if (found is not null)
            {
                return;
            }
            if (n.Kind == kind)
            {
                found = n;
                return;
            }
            n.ForEachChild(child =>
            {
                Visit(child);
                return found is not null;
            });
        }
        Visit(node);
        return found;
    }

    private static Node TagNameOfSelfClosing(string source)
    {
        var sf = Parse(source);
        var element = FindFirst(sf.AsNode(), SyntaxKind.JsxSelfClosingElement);
        Assert.NotNull(element);
        var tagName = element!.TagName();
        Assert.NotNull(tagName);
        return tagName!;
    }

    // ===== isJsxIntrinsicTagName (utilities.go:1093) =====

    [Fact]
    public void IsJsxIntrinsicTagName_LowercaseIdentifier_IsIntrinsic()
    {
        var tagName = TagNameOfSelfClosing("const x = <div />;");
        Assert.True(Checker.IsJsxIntrinsicTagName(tagName));
    }

    [Fact]
    public void IsJsxIntrinsicTagName_CapitalizedIdentifier_IsNotIntrinsic()
    {
        var tagName = TagNameOfSelfClosing("const x = <Foo />;");
        Assert.False(Checker.IsJsxIntrinsicTagName(tagName));
    }

    [Fact]
    public void IsJsxIntrinsicTagName_NamespacedName_IsIntrinsic()
    {
        var tagName = TagNameOfSelfClosing("const x = <svg:rect />;");
        Assert.True(NodeTests.IsJsxNamespacedName(tagName));
        Assert.True(Checker.IsJsxIntrinsicTagName(tagName));
    }

    // ===== JsxFlags flag algebra (jsx.go:17-24) =====

    [Fact]
    public void JsxFlags_IntrinsicElement_IsUnionOfNamedAndIndexed()
    {
        Assert.Equal(JsxFlags.IntrinsicNamedElement | JsxFlags.IntrinsicIndexedElement, JsxFlags.IntrinsicElement);
        Assert.Equal((JsxFlags)0, JsxFlags.None);
        Assert.NotEqual(JsxFlags.None, JsxFlags.IntrinsicNamedElement & JsxFlags.IntrinsicElement);
    }

    // ===== JsxReferenceKind ordering (jsx.go:26-32) =====

    [Fact]
    public void JsxReferenceKind_MatchesGoIota()
    {
        Assert.Equal(0, (int)JsxReferenceKind.Component);
        Assert.Equal(1, (int)JsxReferenceKind.Function);
        Assert.Equal(2, (int)JsxReferenceKind.Mixed);
    }

    // ===== JsxNames / ReactNames tables (jsx.go:41-69) =====

    [Fact]
    public void JsxNames_MatchGoStructValues()
    {
        Assert.Equal("JSX", JsxNames.JSX);
        Assert.Equal("IntrinsicElements", JsxNames.IntrinsicElements);
        Assert.Equal("ElementClass", JsxNames.ElementClass);
        // The container name-container fields deliberately differ from their C# member names.
        Assert.Equal("ElementAttributesProperty", JsxNames.ElementAttributesPropertyNameContainer);
        Assert.Equal("ElementChildrenAttribute", JsxNames.ElementChildrenAttributeNameContainer);
        Assert.Equal("Element", JsxNames.Element);
        Assert.Equal("ElementType", JsxNames.ElementType);
        Assert.Equal("IntrinsicAttributes", JsxNames.IntrinsicAttributes);
        Assert.Equal("IntrinsicClassAttributes", JsxNames.IntrinsicClassAttributes);
        Assert.Equal("LibraryManagedAttributes", JsxNames.LibraryManagedAttributes);
        Assert.Equal("Fragment", ReactNames.Fragment);
    }
}
