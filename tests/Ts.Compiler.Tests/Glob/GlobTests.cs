using System;
using Xunit;
using GlobPattern = Ts.Glob.Glob;

namespace Ts.Glob.Tests;

public class GlobTests
{
    [Theory]
    // LSP spec examples
    [InlineData("**/*.{ts,js}", "src/a/b.ts", true)]
    [InlineData("**/*.{ts,js}", "b.js", true)]
    [InlineData("**/*.{ts,js}", "b.css", false)]
    [InlineData("example.[0-9]", "example.0", true)]
    [InlineData("example.[0-9]", "example.a", false)]
    // * within one segment
    [InlineData("a/*.ts", "a/b.ts", true)]
    [InlineData("a/*.ts", "a/b/c.ts", false)]
    [InlineData("*", "abc", true)]
    // ? single non-slash character
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a/c", false)]
    [InlineData("a?c", "ac", false)]
    // ** spans any number of segments, including none
    [InlineData("**/a", "a", true)]
    [InlineData("**/a", "x/y/a", true)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**", "a/anything/here", true)]
    // consecutive slashes collapse
    [InlineData("a/b", "a//b", true)]
    // literals must consume the whole input
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abcd", false)]
    public void MatchesLikeGo(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, GlobPattern.Parse(pattern).Match(input));
    }

    [Fact]
    public void TrailingSlashPatternMatchesTrailingSlashInput()
    {
        // Registered deviation: Go glob.go:233 panics on this input; we match instead.
        Assert.True(GlobPattern.Parse("a/").Match("a/"));
    }

    [Theory]
    [InlineData("a**")]     // ** not adjacent to '/'
    [InlineData("**a")]     // ** followed by non-'/'
    [InlineData("[a")]      // unterminated range
    [InlineData("[a-")]     // missing high bound
    [InlineData("[a-z")]    // missing ']'
    [InlineData("{a")]      // unmatched '{'
    public void InvalidPatternsThrow(string pattern)
    {
        Assert.Throws<FormatException>(() => GlobPattern.Parse(pattern));
    }

    [Fact]
    public void ToStringRoundTripsStructure()
    {
        Assert.Equal("**/*.{ts,js}", GlobPattern.Parse("**/*.{ts,js}").ToString());
        Assert.Equal("example.[0-9]", GlobPattern.Parse("example.[0-9]").ToString());
    }

    [Fact]
    public void GroupTriesEachBranchWithRemainingPattern()
    {
        var g = GlobPattern.Parse("{a,b}/c");
        Assert.True(g.Match("a/c"));
        Assert.True(g.Match("b/c"));
        Assert.False(g.Match("x/c"));
    }
}
