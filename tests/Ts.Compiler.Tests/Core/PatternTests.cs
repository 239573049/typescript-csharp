// Ported from tsgo internal/core/pattern_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class PatternTests
{
    // Go: TestPatternOverlappingMatch
    [Fact]
    public void PatternOverlappingMatch()
    {
        Pattern p = Core.TryParsePattern("ab*ab");
        Assert.False(p.Matches("ab"), "expected 'ab' not to match 'ab*ab'");
        Assert.True(p.Matches("abXab"), "expected 'abXab' to match 'ab*ab'");
        Assert.Equal("X", p.MatchedText("abXab"));
        Assert.True(p.Matches("abab"), "expected 'abab' to match 'ab*ab'");
        Assert.Equal("", p.MatchedText("abab"));
    }
}
