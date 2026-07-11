// Ported from tsgo internal/format/rulesmap.go
//
// CRITICAL (plan/06 §8.2 危险点): "规则表初始化顺序 = 规则优先级 (rulesmap 桶排序
// 确定性)". The bucket construction order below is load-bearing and reproduced
// byte-for-byte: getAllRules() order → addRule() sub-bucket insertion order via the
// 6-section, 5-bit bitmap. Do not reorder.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Format;

internal static partial class RulesMap
{
    // Go: func getRules(context *FormattingContext, rules []*ruleImpl) []*ruleImpl
    internal static List<RuleImpl> GetRules(FormattingContext context, List<RuleImpl> rules)
    {
        var bucket = GetRulesMapValue()[GetRuleBucketIndex(context.CurrentTokenSpan.Kind, context.NextTokenSpan.Kind)];
        if (bucket is not null && bucket.Count > 0)
        {
            var ruleActionMask = RuleAction.None;
            // Go label `outer:` — emulate `continue outer` with a bool flag.
            foreach (var rule in bucket)
            {
                RuleAction acceptRuleActions = ~GetRuleActionExclusion(ruleActionMask);
                if ((rule.ActionValue() & acceptRuleActions) != 0)
                {
                    var preds = rule.ContextValue();
                    bool allPass = true;
                    foreach (var p in preds)
                    {
                        if (!p(context))
                        {
                            allPass = false;
                            break; // Go: continue outer
                        }
                    }
                    if (!allPass)
                    {
                        continue;
                    }
                    rules.Add(rule);
                    ruleActionMask |= rule.ActionValue();
                }
            }
            return rules;
        }
        return rules;
    }

    // Go: func getRuleBucketIndex(row ast.Kind, column ast.Kind) int
    internal static int GetRuleBucketIndex(SyntaxKind row, SyntaxKind column)
    {
        global::Ts.Core.Debug.Assert(
            row <= SyntaxKind.LastKeyword && column <= SyntaxKind.LastKeyword,
            "Must compute formatting context from tokens");
        return ((int)row * MapRowLength) + (int)column;
    }

    // Go constants.
    internal const int MaskBitSize = 5;
    internal const int Mask = 0b11111; // MaskBitSize bits
    internal static readonly int MapRowLength = (int)SyntaxKind.LastToken + 1;

    /**
     * For a given rule action, gets a mask of other rule actions that
     * cannot be applied at the same position.
     */
    // Go: func getRuleActionExclusion(ruleAction ruleAction) ruleAction
    internal static RuleAction GetRuleActionExclusion(RuleAction ruleAction)
    {
        var mask = RuleAction.None;
        if ((ruleAction & RuleAction.StopProcessingSpaceActions) != 0)
        {
            mask |= RuleAction.ModifySpaceAction;
        }
        if ((ruleAction & RuleAction.StopProcessingTokenActions) != 0)
        {
            mask |= RuleAction.ModifyTokenAction;
        }
        if ((ruleAction & RuleAction.ModifySpaceAction) != 0)
        {
            mask |= RuleAction.ModifySpaceAction;
        }
        if ((ruleAction & RuleAction.ModifyTokenAction) != 0)
        {
            mask |= RuleAction.ModifyTokenAction;
        }
        return mask;
    }

    // Go: var getRulesMap = sync.OnceValue(buildRulesMap)
    // map-iter: 已排序 — the bucket lists are ordered by construction; the outer map
    // is index-addressed (not iterated). Lazy is thread-safe (matches sync.OnceValue).
    private static readonly Lazy<List<RuleImpl>[]> RulesMapLazy = new(BuildRulesMap);

    internal static List<RuleImpl>[] GetRulesMapValue() => RulesMapLazy.Value;

    // Go: func buildRulesMap() [][]*ruleImpl
    private static List<RuleImpl>[] BuildRulesMap()
    {
        var rules = Rules.GetAllRules();
        // Map from bucket index to array of rules
        var m = new List<RuleImpl>[MapRowLength * MapRowLength];
        // This array is used only during construction of the rulesbucket in the map
        var rulesBucketConstructionStateList = new int[m.Length];
        // map-iter: 已排序 — iterate getAllRules() in definition order (load-bearing).
        foreach (var rule in rules)
        {
            bool specificRule = rule.LeftTokenRange.IsSpecific && rule.RightTokenRange.IsSpecific;

            foreach (var left in rule.LeftTokenRange.Tokens)
            {
                foreach (var right in rule.RightTokenRange.Tokens)
                {
                    int index = GetRuleBucketIndex(left, right);
                    m[index] = AddRule(m[index], rule.Rule, specificRule, rulesBucketConstructionStateList, index);
                }
            }
        }
        return m;
    }

    // Go: type RulesPosition int
    internal enum RulesPosition
    {
        StopRulesSpecific = 0,
        StopRulesAny = MaskBitSize * 1,
        ContextRulesSpecific = MaskBitSize * 2,
        ContextRulesAny = MaskBitSize * 3,
        NoContextRulesSpecific = MaskBitSize * 4,
        NoContextRulesAny = MaskBitSize * 5,
    }

    // The Rules list contains all the inserted rules into a rulebucket in the following order:
    //
    //	1- Ignore rules with specific token combination
    //	2- Ignore rules with any token combination
    //	3- Context rules with specific token combination
    //	4- Context rules with any token combination
    //	5- Non-context rules with specific token combination
    //	6- Non-context rules with any token combination
    //
    // The member rulesInsertionIndexBitmap is used to describe the number of rules
    // in each sub-bucket (above) hence can be used to know the index of where to insert
    // the next rule. It's a bitmap which contains 6 different sections each is given 5 bits.
    //
    // Go: func addRule(rules []*ruleImpl, rule *ruleImpl, specificTokens bool, constructionState []int, rulesBucketIndex int) []*ruleImpl
    private static List<RuleImpl> AddRule(
        List<RuleImpl>? rules,
        RuleImpl rule,
        bool specificTokens,
        int[] constructionState,
        int rulesBucketIndex)
    {
        rules ??= new List<RuleImpl>();

        RulesPosition position;
        if ((rule.ActionValue() & RuleAction.StopAction) != 0)
        {
            position = specificTokens ? RulesPosition.StopRulesSpecific : RulesPosition.StopRulesAny;
        }
        else if (rule.ContextValue().Count != 0)
        {
            position = specificTokens ? RulesPosition.ContextRulesSpecific : RulesPosition.ContextRulesAny;
        }
        else
        {
            position = specificTokens ? RulesPosition.NoContextRulesSpecific : RulesPosition.NoContextRulesAny;
        }

        int state = constructionState[rulesBucketIndex];

        // Go: rules = slices.Insert(rules, getRuleInsertionIndex(state, position), rule)
        rules.Insert(GetRuleInsertionIndex(state, position), rule);
        constructionState[rulesBucketIndex] = IncreaseInsertionIndex(state, position);
        return rules;
    }

    // Go: func getRuleInsertionIndex(indexBitmap int, maskPosition RulesPosition) int
    private static int GetRuleInsertionIndex(int indexBitmap, RulesPosition maskPosition)
    {
        int index = 0;
        for (int pos = 0; pos <= (int)maskPosition; pos += MaskBitSize)
        {
            index += indexBitmap & Mask;
            indexBitmap >>= MaskBitSize;
        }
        return index;
    }

    // Go: func increaseInsertionIndex(indexBitmap int, maskPosition RulesPosition) int
    private static int IncreaseInsertionIndex(int indexBitmap, RulesPosition maskPosition)
    {
        int value = ((indexBitmap >> (int)maskPosition) & Mask) + 1;
        global::Ts.Core.Debug.Assert(
            (value & Mask) == value,
            "Adding more rules into the sub-bucket than allowed. Maximum allowed is 32 rules.");
        return (indexBitmap & ~(Mask << (int)maskPosition)) | (value << (int)maskPosition);
    }
}
