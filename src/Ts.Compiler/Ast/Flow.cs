using System;

namespace Ts.Ast;

// Go: internal/ast/flow.go:5-23 — `type FlowFlags uint32`. Members drop the Go
// `FlowFlags` type-name prefix (plan/05-conventions.md §1.1 enum rule).
[Flags]
public enum FlowFlags : uint
{
    Unreachable = 1 << 0, // Unreachable code
    Start = 1 << 1, // Start of flow graph
    BranchLabel = 1 << 2, // Non-looping junction
    LoopLabel = 1 << 3, // Looping junction
    Assignment = 1 << 4, // Assignment
    TrueCondition = 1 << 5, // Condition known to be true
    FalseCondition = 1 << 6, // Condition known to be false
    SwitchClause = 1 << 7, // Switch statement clause
    ArrayMutation = 1 << 8, // Potential array mutation
    Call = 1 << 9, // Potential assertion call
    ReduceLabel = 1 << 10, // Temporarily reduce antecedents of label
    Referenced = 1 << 11, // Referenced as antecedent once
    Shared = 1 << 12, // Referenced as antecedent more than once
    Label = BranchLabel | LoopLabel,
    Condition = TrueCondition | FalseCondition,
}

// Go: internal/ast/flow.go:27-32. Real port of the data shape; the synthetic
// FlowSwitchClauseData/FlowReduceLabelData AST nodes (flow.go:43-75) and their
// factories land with the binder port (phase 4).
public sealed class FlowNode
{
    public FlowFlags Flags;
    public Node? Node; // Associated AST node
    public FlowNode? Antecedent; // Antecedent for all but FlowLabel
    public FlowList? Antecedents; // Linked list of antecedents for FlowLabel
}

// Go: internal/ast/flow.go:34-37
public sealed class FlowList
{
    public FlowNode? Flow;
    public FlowList? Next;
}
