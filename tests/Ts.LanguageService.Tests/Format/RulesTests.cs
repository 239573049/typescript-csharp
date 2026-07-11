// Tests for the format rule engine foundation (ported from tsgo internal/format).
// Exercises the load-bearing rules table + rulesmap bucket construction order
// (plan/06 §8.2 危险点: "规则表初始化顺序 = 规则优先级").

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Format;
using Xunit;

namespace Ts.Format.Tests;

public class RulesTests
{
    [Fact]
    public void GetAllRules_HasExpectedCount()
    {
        // Total = 135 rule specs, matching tsgo internal/format/rules.go exactly
        // (135 `rule(` builder calls in the Go source).
        List<RuleSpec> rules = Rules.GetAllRules();
        Assert.Equal(135, rules.Count);
    }

    [Fact]
    public void GetAllRules_FirstRuleIsIgnoreBeforeComment()
    {
        // Order is load-bearing; the first high-priority rule must be IgnoreBeforeComment.
        List<RuleSpec> rules = Rules.GetAllRules();
        Assert.Equal("IgnoreBeforeComment", rules[0].Rule.DebugName);
        Assert.Equal("IgnoreAfterLineComment", rules[1].Rule.DebugName);
    }

    [Fact]
    public void RulesMap_BuildsDeterministically()
    {
        // The lazy build must be stable across calls (sync.OnceValue equivalent).
        List<RuleImpl>[] first = RulesMap.GetRulesMapValue();
        List<RuleImpl>[] second = RulesMap.GetRulesMapValue();
        Assert.Same(first, second);
        Assert.Equal(RulesMap.MapRowLength * RulesMap.MapRowLength, first.Length);
    }

    [Fact]
    public void RulesMap_BucketForColonToken_HasRules()
    {
        // The pair (anyToken, ColonToken) should populate the bucket for e.g.
        // (Identifier, ColonToken) with the NotSpaceBeforeColon rule among others.
        List<RuleImpl>[] map = RulesMap.GetRulesMapValue();
        int index = RulesMap.GetRuleBucketIndex(SyntaxKind.Identifier, SyntaxKind.ColonToken);
        Assert.NotNull(map[index]);
        Assert.True(map[index].Count > 0);
    }

    [Fact]
    public void GetRuleActionExclusion_StopSpaceExcludesModifySpace()
    {
        RuleAction excl = RulesMap.GetRuleActionExclusion(RuleAction.StopProcessingSpaceActions);
        Assert.True((excl & RuleAction.ModifySpaceAction) != 0);
    }
}

public class FormatCodeSettingsTests
{
    [Fact]
    public void Defaults_MatchGo()
    {
        FormatCodeSettings s = FormatCodeSettings.GetDefaultFormatCodeSettings();

        Assert.Equal(4, s.IndentSize);
        Assert.Equal(4, s.TabSize);
        Assert.Equal("\n", s.NewLineCharacter);
        Assert.Equal(Tristate.True, s.ConvertTabsToSpaces);
        Assert.Equal(IndentStyle.Smart, s.IndentStyle);
        Assert.Equal(Tristate.True, s.TrimTrailingWhitespace);

        Assert.Equal(Tristate.True, s.InsertSpaceAfterCommaDelimiter);
        Assert.Equal(Tristate.True, s.InsertSpaceBeforeAndAfterBinaryOperators);
        Assert.Equal(Tristate.False, s.InsertSpaceAfterConstructor);
        Assert.Equal(Tristate.True, s.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces);
        Assert.Equal(SemicolonPreference.Ignore, s.Semicolons);
        Assert.Equal(Tristate.True, s.IndentSwitchCase);
    }

    [Fact]
    public void FormatContext_NewLineDefault_IsNewline()
    {
        var ctx = new FormatContext();
        Assert.Equal("\n", ctx.GetNewLineOrDefault());
    }

    [Fact]
    public void FormatContext_UsesHostNewLineWhenOptionEmpty()
    {
        var opts = FormatCodeSettings.GetDefaultFormatCodeSettings();
        opts.NewLineCharacter = ""; // force fallthrough to host newline
        var ctx = FormatContext.WithFormatCodeSettings(opts, "\r\n");
        Assert.Equal("\r\n", ctx.GetNewLineOrDefault());
    }
}
