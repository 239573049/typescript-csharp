// Ported from tsgo internal/format/rule.go

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Format;

// Go: type ruleImpl struct { debugName; context; action; flags }
internal sealed class RuleImpl
{
    internal string DebugName = "";
    internal List<ContextPredicate> Context = new();
    internal RuleAction Action;
    internal RuleFlags Flags;

    // Go: func (r ruleImpl) Action() ruleAction
    internal RuleAction ActionValue() => Action;

    // Go: func (r ruleImpl) Context() []contextPredicate
    internal List<ContextPredicate> ContextValue() => Context;

    // Go: func (r ruleImpl) Flags() ruleFlags
    internal RuleFlags FlagsValue() => Flags;

    // Go: func (r ruleImpl) String() string { return r.debugName }
    public override string ToString() => DebugName;
}

// Go: type tokenRange struct { tokens []ast.Kind; isSpecific bool }
internal struct TokenRange
{
    internal SyntaxKind[] Tokens;
    internal bool IsSpecific;
}

// Go: type ruleSpec struct { leftTokenRange; rightTokenRange; rule }
internal sealed class RuleSpec
{
    internal TokenRange LeftTokenRange;
    internal TokenRange RightTokenRange;
    internal RuleImpl Rule = null!;
}

// Go: type contextPredicate = func(ctx *FormattingContext) bool
internal delegate bool ContextPredicate(FormattingContext ctx);

internal static partial class Rules
{
    /**
     * A rule takes a two tokens (left/right) and a particular context
     * for which you're meant to look at them. You then declare what should the
     * whitespace annotation be between these tokens via the action param.
     */
    // Go: func rule(debugName string, left any, right any, context []contextPredicate, action ruleAction, flags ...ruleFlags) ruleSpec
    //
    // PORT-NOTE: Go's `left any`/`right any` accept ast.Kind, []ast.Kind, or
    // tokenRange (see toTokenRange). C# has no `any`; we provide overloads accepting
    // the concrete argument shapes actually used by rules.go, all funneling through
    // ToTokenRange. Variadic `flags ...ruleFlags` (0 or 1 value) → optional param.
    internal static RuleSpec Rule(
        string debugName,
        SyntaxKind left,
        SyntaxKind right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, ToTokenRange(left), ToTokenRange(right), context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        SyntaxKind[] left,
        SyntaxKind right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, ToTokenRange(left), ToTokenRange(right), context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        SyntaxKind left,
        SyntaxKind[] right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, ToTokenRange(left), ToTokenRange(right), context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        SyntaxKind[] left,
        SyntaxKind[] right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, ToTokenRange(left), ToTokenRange(right), context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        TokenRange left,
        SyntaxKind right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, left, ToTokenRange(right), context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        SyntaxKind left,
        TokenRange right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, ToTokenRange(left), right, context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        TokenRange left,
        TokenRange right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, left, right, context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        TokenRange left,
        SyntaxKind[] right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, left, ToTokenRange(right), context, action, flags);

    internal static RuleSpec Rule(
        string debugName,
        SyntaxKind[] left,
        TokenRange right,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flags = RuleFlags.None)
        => RuleCore(debugName, ToTokenRange(left), right, context, action, flags);

    private static RuleSpec RuleCore(
        string debugName,
        TokenRange leftRange,
        TokenRange rightRange,
        List<ContextPredicate> context,
        RuleAction action,
        RuleFlags flag)
    {
        var rule = new RuleImpl
        {
            DebugName = debugName,
            Context = context,
            Action = action,
            Flags = flag,
        };
        return new RuleSpec
        {
            LeftTokenRange = leftRange,
            RightTokenRange = rightRange,
            Rule = rule,
        };
    }

    // Go: func toTokenRange(e any) tokenRange
    internal static TokenRange ToTokenRange(SyntaxKind t)
        => new() { IsSpecific = true, Tokens = new[] { t } };

    internal static TokenRange ToTokenRange(SyntaxKind[] t)
        => new() { IsSpecific = true, Tokens = t };

    internal static TokenRange ToTokenRange(TokenRange t) => t;

    // Go: var anyContext = []contextPredicate{}
    internal static readonly List<ContextPredicate> AnyContext = new();
}

// Go: type ruleAction int (bit flags)
[Flags]
internal enum RuleAction
{
    None = 0,
    StopProcessingSpaceActions = 1 << 0,
    StopProcessingTokenActions = 1 << 1,
    InsertSpace = 1 << 2,
    InsertNewLine = 1 << 3,
    DeleteSpace = 1 << 4,
    DeleteToken = 1 << 5,
    InsertTrailingSemicolon = 1 << 6,

    StopAction = StopProcessingSpaceActions | StopProcessingTokenActions,
    ModifySpaceAction = InsertSpace | InsertNewLine | DeleteSpace,
    ModifyTokenAction = DeleteToken | InsertTrailingSemicolon,
}

// Go: type ruleFlags int ; iota None, CanDeleteNewLines.
internal enum RuleFlags
{
    None = 0,
    CanDeleteNewLines = 1,
}
