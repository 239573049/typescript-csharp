using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/flow.go:41-75 — the two synthetic AST nodes used by the binder's flow graph
// (FlowFlagsSwitchClause / FlowFlagsReduceLabel) and their factories. Deferred from the phase-3
// flow.go port (see Flow.cs's note); landed here for phase 4. In Go these embed NodeBase and are
// built with newNode(KindUnknown, node, NodeFactoryHooks{}); in C# the node IS the object, so
// each derives from Node directly and the factory sets Kind = Unknown + Loc = Undefined inline
// (the faithful equivalent of the empty-hooks newNode, ast.go:74-83).

// FlowSwitchClauseData (synthetic AST node for FlowFlagsSwitchClause)
// Go: flow.go:43-48
public sealed class FlowSwitchClauseData : Node
{
    public Node? SwitchStatement;
    public int ClauseStart; // Start index of case/default clause range
    public int ClauseEnd; // End index of case/default clause range

    // Go: flow.go:58-60
    public bool IsEmpty()
    {
        return ClauseStart == ClauseEnd;
    }

    // Synthetic node with no AST children (Go embeds NodeBase; Node.ForEachChild defaults to false).
    public override bool ForEachChild(Visitor v) => false;
}

// FlowReduceLabelData (synthetic AST node for FlowFlagsReduceLabel)
// Go: flow.go:64-68 — `Target *FlowLabel` where `type FlowLabel = FlowNode` (flow.go:39).
public sealed class FlowReduceLabelData : Node
{
    public FlowNode? Target; // Target label
    public FlowList? Antecedents; // Temporary antecedent list

    // Synthetic node with no AST children (Go embeds NodeBase; Node.ForEachChild defaults to false).
    public override bool ForEachChild(Visitor v) => false;
}

// As* cast helpers: in C# the node IS the object, so these are simple downcasts (extension
// methods keep the generated Node partial untouched; call form node.AsFlowSwitchClauseData()).
public static class FlowSyntheticNodeCasts
{
    public static FlowSwitchClauseData AsFlowSwitchClauseData(this Node n) => (FlowSwitchClauseData)n;

    public static FlowReduceLabelData AsFlowReduceLabelData(this Node n) => (FlowReduceLabelData)n;
}

public static partial class AstUtilities
{
    // Go: flow.go:50-56
    public static Node NewFlowSwitchClauseData(Node switchStatement, int clauseStart, int clauseEnd)
    {
        var node = new FlowSwitchClauseData();
        node.SwitchStatement = switchStatement;
        node.ClauseStart = clauseStart;
        node.ClauseEnd = clauseEnd;
        // newNode(KindUnknown, node, NodeFactoryHooks{}) with empty hooks (ast.go:74-83).
        node.Loc = TextRange.Undefined;
        node.Kind = SyntaxKind.Unknown;
        return node;
    }

    // Go: flow.go:70-75
    public static Node NewFlowReduceLabelData(FlowNode target, FlowList antecedents)
    {
        var node = new FlowReduceLabelData();
        node.Target = target;
        node.Antecedents = antecedents;
        // newNode(KindUnknown, node, NodeFactoryHooks{}) with empty hooks (ast.go:74-83).
        node.Loc = TextRange.Undefined;
        node.Kind = SyntaxKind.Unknown;
        return node;
    }
}
