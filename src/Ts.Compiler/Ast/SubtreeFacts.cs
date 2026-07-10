using System;

namespace Ts.Ast;

// Go: internal/ast/subtreefacts.go — `type SubtreeFacts uint32`. Only the exact
// `SubtreeFacts` type-name prefix is dropped (SubtreeFactsNone → None,
// SubtreeFactsComputed → Computed; plan/05-conventions.md §1.1 enum rule) —
// `SubtreeContains*` / `SubtreeExclusions*` names are kept verbatim.
[Flags]
public enum SubtreeFacts : uint
{
    None = 0, // Go: SubtreeFactsNone

    // Facts
    // - Flags used to indicate that a node or subtree contains syntax relevant to a specific transform

    SubtreeContainsTypeScript = 1 << 0,
    SubtreeContainsJsx = 1 << 1,
    SubtreeContainsESDecorators = 1 << 2,
    SubtreeContainsUsing = 1 << 3,
    SubtreeContainsClassStaticBlocks = 1 << 4,
    SubtreeContainsESClassFields = 1 << 5,
    SubtreeContainsLogicalAssignments = 1 << 6,
    SubtreeContainsNullishCoalescing = 1 << 7,
    SubtreeContainsOptionalChaining = 1 << 8,
    SubtreeContainsMissingCatchClauseVariable = 1 << 9,
    SubtreeContainsESObjectRestOrSpread = 1 << 10, // subtree has a `...` somewhere inside it, never cleared
    SubtreeContainsForAwaitOrAsyncGenerator = 1 << 11,
    SubtreeContainsAnyAwait = 1 << 12,
    SubtreeContainsExponentiationOperator = 1 << 13,

    // Markers
    // - Flags used to indicate that a node or subtree contains a particular kind of syntax.

    SubtreeContainsLexicalThis = 1 << 14,
    SubtreeContainsLexicalSuper = 1 << 15,
    SubtreeContainsRestOrSpread = 1 << 16, // marker on any `...` - cleared on binding pattern exit
    SubtreeContainsObjectRestOrSpread = 1 << 17, // marker on any `{...x}` - cleared on most scope exits
    SubtreeContainsAwait = 1 << 18,
    SubtreeContainsDynamicImport = 1 << 19,
    SubtreeContainsClassFields = 1 << 20,
    SubtreeContainsDecorators = 1 << 21,
    SubtreeContainsIdentifier = 1 << 22,
    SubtreeContainsPrivateIdentifierInExpression = 1 << 23,
    SubtreeContainsInvalidTemplateEscape = 1 << 24,

    Computed = 1 << 25, // Go: SubtreeFactsComputed — NOTE: This should always be last

    // Aliases (unused, for documentation purposes only - correspond to combinations in transformers/estransforms/definitions.go)

    SubtreeContainsESNext = SubtreeContainsESDecorators | SubtreeContainsUsing,
    SubtreeContainsES2022 = SubtreeContainsClassStaticBlocks | SubtreeContainsESClassFields,
    SubtreeContainsES2021 = SubtreeContainsLogicalAssignments,
    SubtreeContainsES2020 = SubtreeContainsNullishCoalescing | SubtreeContainsOptionalChaining,
    SubtreeContainsES2019 = SubtreeContainsMissingCatchClauseVariable,
    SubtreeContainsES2018 = SubtreeContainsESObjectRestOrSpread | SubtreeContainsForAwaitOrAsyncGenerator | SubtreeContainsInvalidTemplateEscape,
    SubtreeContainsES2017 = SubtreeContainsAnyAwait,
    SubtreeContainsES2016 = SubtreeContainsExponentiationOperator,

    // Scope Exclusions
    // - Bitmasks that exclude flags from propagating out of a specific context
    //   into the subtree flags of their container.

    SubtreeExclusionsNode = Computed,
    SubtreeExclusionsEraseable = ~SubtreeContainsTypeScript,
    SubtreeExclusionsOuterExpression = SubtreeExclusionsNode,
    SubtreeExclusionsPropertyAccess = SubtreeExclusionsNode,
    SubtreeExclusionsElementAccess = SubtreeExclusionsNode,
    SubtreeExclusionsArrowFunction = SubtreeExclusionsNode | SubtreeContainsAwait | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsFunction = SubtreeExclusionsNode | SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper | SubtreeContainsAwait | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsConstructor = SubtreeExclusionsNode | SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper | SubtreeContainsAwait | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsMethod = SubtreeExclusionsNode | SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper | SubtreeContainsAwait | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsAccessor = SubtreeExclusionsNode | SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper | SubtreeContainsAwait | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsProperty = SubtreeExclusionsNode | SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper,
    SubtreeExclusionsClass = SubtreeExclusionsNode,
    SubtreeExclusionsModule = SubtreeExclusionsNode | SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper,
    SubtreeExclusionsObjectLiteral = SubtreeExclusionsNode | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsArrayLiteral = SubtreeExclusionsNode,
    SubtreeExclusionsCall = SubtreeExclusionsNode,
    SubtreeExclusionsNew = SubtreeExclusionsNode,
    SubtreeExclusionsVariableDeclarationList = SubtreeExclusionsNode | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsParameter = SubtreeExclusionsNode,
    SubtreeExclusionsCatchClause = SubtreeExclusionsNode | SubtreeContainsObjectRestOrSpread,
    SubtreeExclusionsBindingPattern = SubtreeExclusionsNode | SubtreeContainsRestOrSpread,

    // Masks
    // - Additional bitmasks

    SubtreeContainsLexicalThisOrSuper = SubtreeContainsLexicalThis | SubtreeContainsLexicalSuper,
}

public abstract partial class Node
{
    // Go: subtreefacts.go:89-133 — package-level propagation helpers. Hosted as
    // statics on Node so generated computeSubtreeFacts bodies call them unqualified,
    // matching the Go text (`propagateSubtreeFacts(node.X)`).

    // subtreefacts.go:89-91
    internal static SubtreeFacts PropagateEraseableSyntaxListSubtreeFacts(TypeArgumentList? children) =>
        children != null ? Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript : Ts.Ast.SubtreeFacts.None;

    // subtreefacts.go:93-95
    internal static SubtreeFacts PropagateEraseableSyntaxSubtreeFacts(TypeNode? child) =>
        child != null ? Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript : Ts.Ast.SubtreeFacts.None;

    // subtreefacts.go:97-104
    internal static SubtreeFacts PropagateObjectBindingElementSubtreeFacts(BindingElementNode? child)
    {
        var facts = PropagateSubtreeFacts(child);
        if ((facts & Ts.Ast.SubtreeFacts.SubtreeContainsRestOrSpread) != 0)
        {
            facts &= ~Ts.Ast.SubtreeFacts.SubtreeContainsRestOrSpread;
            facts |= Ts.Ast.SubtreeFacts.SubtreeContainsObjectRestOrSpread | Ts.Ast.SubtreeFacts.SubtreeContainsESObjectRestOrSpread;
        }
        return facts;
    }

    // subtreefacts.go:106-108
    internal static SubtreeFacts PropagateBindingElementSubtreeFacts(BindingElementNode? child) =>
        PropagateSubtreeFacts(child) & ~Ts.Ast.SubtreeFacts.SubtreeContainsRestOrSpread;

    // subtreefacts.go:110-115
    internal static SubtreeFacts PropagateSubtreeFacts(Node? child)
    {
        if (child == null)
        {
            return Ts.Ast.SubtreeFacts.None;
        }
        return child.PropagateSubtreeFacts();
    }

    // subtreefacts.go:117-126
    internal static SubtreeFacts PropagateNodeListSubtreeFacts(NodeList? children, Func<Node?, SubtreeFacts> propagate)
    {
        if (children == null)
        {
            return Ts.Ast.SubtreeFacts.None;
        }
        var facts = Ts.Ast.SubtreeFacts.None;
        foreach (var child in children.Nodes)
        {
            facts |= propagate(child);
        }
        return facts;
    }

    // subtreefacts.go:128-133
    internal static SubtreeFacts PropagateModifierListSubtreeFacts(ModifierList? children)
    {
        if (children == null)
        {
            return Ts.Ast.SubtreeFacts.None;
        }
        return PropagateNodeListSubtreeFacts(children, PropagateSubtreeFacts);
    }
}
