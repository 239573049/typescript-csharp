// Tests for the phase-5 type-display wave: typeToStringEx renders a Type to its error-message name
// via the NodeBuilder type-node build → emit Printer. These call TypeToStringEx directly (not the
// TypeToString intrinsic shim) so they exercise the real nodebuilder → printer mechanism end to end.

using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class TypeToStringTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    [Fact]
    public void IntrinsicTypes_RenderTheirKeywordName()
    {
        var c = NewChecker();

        Assert.Equal("number", c.TypeToStringEx(c.numberType!, null, TypeFormatFlags.None, null));
        Assert.Equal("string", c.TypeToStringEx(c.stringType!, null, TypeFormatFlags.None, null));
        Assert.Equal("boolean", c.TypeToStringEx(c.booleanType!, null, TypeFormatFlags.None, null));
        Assert.Equal("any", c.TypeToStringEx(c.anyType!, null, TypeFormatFlags.None, null));
        Assert.Equal("unknown", c.TypeToStringEx(c.unknownType!, null, TypeFormatFlags.None, null));
        Assert.Equal("void", c.TypeToStringEx(c.voidType!, null, TypeFormatFlags.None, null));
        Assert.Equal("undefined", c.TypeToStringEx(c.undefinedType!, null, TypeFormatFlags.None, null));
        Assert.Equal("null", c.TypeToStringEx(c.nullType!, null, TypeFormatFlags.None, null));
        Assert.Equal("never", c.TypeToStringEx(c.neverType!, null, TypeFormatFlags.None, null));
        Assert.Equal("bigint", c.TypeToStringEx(c.bigintType!, null, TypeFormatFlags.None, null));
        Assert.Equal("symbol", c.TypeToStringEx(c.esSymbolType!, null, TypeFormatFlags.None, null));
    }

    [Fact]
    public void StringLiteralType_RendersQuoted()
    {
        var c = NewChecker();
        var lit = c.GetStringLiteralType("hello");
        Assert.Equal("\"hello\"", c.TypeToStringEx(lit, null, TypeFormatFlags.None, null));
    }

    [Fact]
    public void NumberLiteralType_RendersDigits()
    {
        var c = NewChecker();
        var lit = c.GetNumberLiteralType(new global::Ts.JsNum.Number(42));
        Assert.Equal("42", c.TypeToStringEx(lit, null, TypeFormatFlags.None, null));
    }

    [Fact]
    public void UnionOfIntrinsics_RendersBarDelimited()
    {
        var c = NewChecker();
        var union = c.GetUnionType([c.stringType!, c.numberType!]);
        Assert.Equal("string | number", c.TypeToStringEx(union, null, TypeFormatFlags.None, null));
    }
}
