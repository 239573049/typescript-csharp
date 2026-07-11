// Ported from tsgo internal/printer/emitcontext.go
//
// This emit-infrastructure wave ports EmitContext (the AST-side emit side-table). To keep the wave
// self-contained and green, three things that belong to later printer waves are co-hosted here and
// clearly marked with PORT-NOTE for relocation:
//   * the printer `NodeFactory` (factory.go) — only the generated-identifier slice + the base-factory
//     forwards that EmitContext / NameGenerator / the NameGenerator tests require;
//   * a minimal `EmitHelper`/`Priority` data type (helpers.go) — shape only, no helper instances;
//   * a handful of `Utilities` string helpers (utilities.go) that NameGenerator/the factory slice need.
// The two `addDefaultValueAssignmentFor*` leaf methods depend on factory.go helper constructors
// (NewStrictEqualityExpression/NewVoidZeroExpression/NewAssignmentExpression/NewTypeCheck) and are
// left as `// !!!` NotImplementedException stubs until factory.go is ported.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

using EF = global::Ts.Printer.EmitFlags;

namespace Ts.Printer;

using StringUtil = global::Ts.StringUtil.StringUtil;

// Stores side-table information used during transformation that can be read by the printer to customize emit
//
// NOTE: EmitContext is not guaranteed to be thread-safe.
public sealed partial class EmitContext
{
    public NodeFactory Factory; // Required. The NodeFactory to use to create new nodes
    internal Dictionary<Node, AutoGenerateInfo>? autoGenerate;
    internal Dictionary<Node, Node>? textSource;
    internal Dictionary<Node, Node>? original;
    internal LinkStore<Node, EmitNode> emitNodes = new();
    internal Dictionary<Node, Node>? assignedName;
    internal Dictionary<Node, Node>? classThis;
    internal global::Ts.Core.Stack<VarScope> varScopeStack = new();
    internal global::Ts.Core.Stack<VarScope> letScopeStack = new();
    internal OrderedSet<EmitHelper> emitHelpers = new();

    public EmitContext()
    {
        Factory = new NodeFactory(this);
    }

    // Go: func NewEmitContext() *EmitContext
    public static EmitContext NewEmitContext()
    {
        return new EmitContext();
    }

    private static readonly ObjectPool<EmitContext> EmitContextPool = new(() => new EmitContext());

    // Go: func GetEmitContext() (*EmitContext, func())
    public static (EmitContext Context, Action Release) GetEmitContext()
    {
        var c = EmitContextPool.Get();
        return (c, () =>
        {
            c.Reset();
            EmitContextPool.Return(c);
        });
    }

    public void Reset()
    {
        // Go: *c = EmitContext{Factory: c.Factory}
        autoGenerate = null;
        textSource = null;
        original = null;
        emitNodes = new LinkStore<Node, EmitNode>();
        assignedName = null;
        classThis = null;
        varScopeStack = new global::Ts.Core.Stack<VarScope>();
        letScopeStack = new global::Ts.Core.Stack<VarScope>();
        emitHelpers = new OrderedSet<EmitHelper>();
    }

    internal void OnCreate(Node node)
    {
        node.Flags |= NodeFlags.Synthesized;
    }

    internal void OnUpdate(Node updated, Node original)
    {
        SetOriginal(updated, original);
    }

    internal void OnClone(Node updated, Node original)
    {
        SetOriginal(updated, original);
        if (NodeTests.IsIdentifier(updated) || NodeTests.IsPrivateIdentifier(updated))
        {
            if (autoGenerate is not null && autoGenerate.TryGetValue(original, out var info) && info is not null)
            {
                var autoGenerateCopy = info.Clone();
                autoGenerate[updated] = autoGenerateCopy;
            }
        }
    }

    // Creates a new NodeVisitor attached to this EmitContext
    public NodeVisitor NewNodeVisitor(Func<Node, Node?> visit)
    {
        return new NodeVisitor(visit, Factory.AsNodeFactory(), new NodeVisitorHooks
        {
            VisitParameters = VisitParameters,
            VisitFunctionBody = VisitFunctionBody,
            VisitIterationBody = VisitIterationBody,
            VisitTopLevelStatements = VisitVariableEnvironment,
            VisitEmbeddedStatement = VisitEmbeddedStatement,
        });
    }

    //
    // Environment tracking
    //

    // Starts a new VariableEnvironment used to track hoisted `var` statements and function declarations.
    //
    // NOTE: This is the equivalent of `transformContext.startLexicalEnvironment` in Strada.
    public void StartVariableEnvironment()
    {
        varScopeStack.Push(new VarScope());
        StartLexicalEnvironment();
    }

    // Ends the current VariableEnvironment, returning a list of statements that should be emitted at the start of the current scope.
    public Node[] EndVariableEnvironment()
    {
        var scope = varScopeStack.Pop();
        var statements = new List<Node>();
        if (scope.functions.Count > 0)
        {
            statements.AddRange(scope.functions);
        }
        if (scope.variables.Count > 0)
        {
            var varDeclList = Factory.NewVariableDeclarationList(Factory.NewNodeList(scope.variables.ToArray()), NodeFlags.None);
            var varStatement = Factory.NewVariableStatement(null, varDeclList);
            SetEmitFlags(varStatement, EF.CustomPrologue);
            statements.Add(varStatement);
        }
        if (scope.initializationStatements.Count > 0)
        {
            statements.AddRange(scope.initializationStatements);
        }
        statements.AddRange(EndLexicalEnvironment());
        return statements.ToArray();
    }

    // Invokes EndVariableEnvironment() and merges the results into `statements`
    public StatementList? EndAndMergeVariableEnvironmentList(StatementList? statements)
    {
        Node[]? nodes = statements?.Nodes;

        var (result, changed) = EndAndMergeVariableEnvironmentCore(nodes);
        if (changed)
        {
            var list = Factory.NewNodeList(result ?? []);
            list.Loc = statements!.Loc;
            return list;
        }

        return statements;
    }

    // Invokes EndVariableEnvironment() and merges the results into `statements`
    public Node[]? EndAndMergeVariableEnvironment(Node[]? statements)
    {
        var (result, _) = EndAndMergeVariableEnvironmentCore(statements);
        return result;
    }

    private (Node[]?, bool) EndAndMergeVariableEnvironmentCore(Node[]? statements)
    {
        return MergeEnvironmentCore(statements, EndVariableEnvironment());
    }

    // Adds a `var` declaration to the current VariableEnvironment
    public void AddVariableDeclaration(IdentifierNode name)
    {
        var varDecl = Factory.NewVariableDeclaration(name, null /*exclamationToken*/, null /*typeNode*/, null /*initializer*/);
        SetEmitFlags(varDecl, EF.NoNestedSourceMaps);
        var scope = varScopeStack.Peek();
        scope.variables.Add(varDecl);
        if ((scope.flags & EnvironmentFlags.InParameters) != 0)
        {
            scope.flags |= EnvironmentFlags.VariablesHoistedInParameters;
        }
    }

    // Adds a hoisted function declaration to the current VariableEnvironment
    public void AddHoistedFunctionDeclaration(Node node)
    {
        SetEmitFlags(node, EF.CustomPrologue);
        var scope = varScopeStack.Peek();
        scope.functions.Add(node);
    }

    // Starts a new LexicalEnvironment used to track block-scoped `let`, `const`, and `using` declarations.
    public void StartLexicalEnvironment()
    {
        letScopeStack.Push(new VarScope());
    }

    // Ends the current EndLexicalEnvironment, returning a list of statements that should be emitted at the start of the current scope.
    public Node[] EndLexicalEnvironment()
    {
        var scope = letScopeStack.Pop();
        var statements = new List<Node>();
        if (scope.variables.Count > 0)
        {
            var varDeclList = Factory.NewVariableDeclarationList(Factory.NewNodeList(scope.variables.ToArray()), NodeFlags.Let);
            var varStatement = Factory.NewVariableStatement(null, varDeclList);
            SetEmitFlags(varStatement, EF.CustomPrologue);
            statements.Add(varStatement);
        }
        return statements.ToArray();
    }

    // Invokes EndLexicalEnvironment() and merges the results into `statements`
    public StatementList? EndAndMergeLexicalEnvironmentList(StatementList? statements)
    {
        Node[]? nodes = statements?.Nodes;

        var (result, changed) = EndAndMergeLexicalEnvironmentCore(nodes);
        if (changed)
        {
            var list = Factory.NewNodeList(result ?? []);
            list.Loc = statements!.Loc;
            return list;
        }

        return statements;
    }

    // Invokes EndLexicalEnvironment() and merges the results into `statements`
    public Node[]? EndAndMergeLexicalEnvironment(Node[]? statements)
    {
        var (result, _) = EndAndMergeLexicalEnvironmentCore(statements);
        return result;
    }

    private (Node[]?, bool) EndAndMergeLexicalEnvironmentCore(Node[]? statements)
    {
        return MergeEnvironmentCore(statements, EndLexicalEnvironment());
    }

    // Adds a `let` declaration to the current LexicalEnvironment.
    public void AddLexicalDeclaration(IdentifierNode name)
    {
        var varDecl = Factory.NewVariableDeclaration(name, null /*exclamationToken*/, null /*typeNode*/, null /*initializer*/);
        SetEmitFlags(varDecl, EF.NoNestedSourceMaps);
        var scope = letScopeStack.Peek();
        scope.variables.Add(varDecl);
    }

    // Merges declarations produced by EndVariableEnvironment() or EndLexicalEnvironment() into a statement list
    public StatementList? MergeEnvironmentList(StatementList? statements, Node[]? declarations)
    {
        var (result, changed) = MergeEnvironmentCore(statements?.Nodes, declarations);
        if (changed)
        {
            var list = Factory.NewNodeList(result ?? []);
            list.Loc = statements!.Loc;
            return list;
        }
        return statements;
    }

    // Merges declarations produced by EndVariableEnvironment() or EndLexicalEnvironment() into a slice of statements
    public Node[]? MergeEnvironment(Node[]? statements, Node[]? declarations)
    {
        var (result, _) = MergeEnvironmentCore(statements, declarations);
        return result;
    }

    private (Node[]?, bool) MergeEnvironmentCore(Node[]? statements, Node[]? declarations)
    {
        if (declarations is null || declarations.Length == 0)
        {
            return (statements, false);
        }

        // When we merge new lexical statements into an existing statement list, we merge them in the following manner:
        //
        // Given:
        //
        // | Left                               | Right                               |
        // |------------------------------------|-------------------------------------|
        // | [standard prologues (left)]        | [standard prologues (right)]        |
        // | [hoisted functions (left)]         | [hoisted functions (right)]         |
        // | [hoisted variables (left)]         | [hoisted variables (right)]         |
        // | [lexical init statements (left)]   | [lexical init statements (right)]   |
        // | [other statements (left)]          |                                     |
        //
        // NOTE: It is expected that new lexical init statements must be evaluated before existing lexical init statements.

        bool changed = false;

        // find standard prologues on left in the following order: standard directives, hoisted functions, hoisted variables, other custom
        int leftStandardPrologueEnd = Utilities.FindSpanEnd(statements, AstUtilities.IsPrologueDirective, 0);
        int leftHoistedFunctionsEnd = Utilities.FindSpanEnd(statements, IsHoistedFunction, leftStandardPrologueEnd);
        int leftHoistedVariablesEnd = Utilities.FindSpanEnd(statements, IsHoistedVariableStatement, leftHoistedFunctionsEnd);

        // find standard prologues on right in the following order: standard directives, hoisted functions, hoisted variables, other custom
        int rightStandardPrologueEnd = Utilities.FindSpanEnd(declarations, AstUtilities.IsPrologueDirective, 0);
        int rightHoistedFunctionsEnd = Utilities.FindSpanEnd(declarations, IsHoistedFunction, rightStandardPrologueEnd);
        int rightHoistedVariablesEnd = Utilities.FindSpanEnd(declarations, IsHoistedVariableStatement, rightHoistedFunctionsEnd);
        int rightCustomPrologueEnd = Utilities.FindSpanEnd(declarations, IsCustomPrologue, rightHoistedVariablesEnd);
        if (rightCustomPrologueEnd != declarations.Length)
        {
            throw new InvalidOperationException("Expected declarations to be valid standard or custom prologues");
        }

        Node[]? left = statements;

        // splice other custom prologues from right into left
        if (rightCustomPrologueEnd > rightHoistedVariablesEnd)
        {
            left = CoreUtil.Splice(left, leftHoistedVariablesEnd, 0, declarations[rightHoistedVariablesEnd..rightCustomPrologueEnd]);
            changed = true;
        }

        // splice hoisted variables from right into left
        if (rightHoistedVariablesEnd > rightHoistedFunctionsEnd)
        {
            left = CoreUtil.Splice(left, leftHoistedFunctionsEnd, 0, declarations[rightHoistedFunctionsEnd..rightHoistedVariablesEnd]);
            changed = true;
        }

        // splice hoisted functions from right into left
        if (rightHoistedFunctionsEnd > rightStandardPrologueEnd)
        {
            left = CoreUtil.Splice(left, leftStandardPrologueEnd, 0, declarations[rightStandardPrologueEnd..rightHoistedFunctionsEnd]);
            changed = true;
        }

        // splice standard prologues from right into left (that are not already in left)
        if (rightStandardPrologueEnd > 0)
        {
            if (leftStandardPrologueEnd == 0)
            {
                left = CoreUtil.Splice(left, 0, 0, declarations[..rightStandardPrologueEnd]);
                changed = true;
            }
            else
            {
                var leftPrologues = new Set<string>();
                for (int i = 0; i < leftStandardPrologueEnd; i++)
                {
                    var leftPrologue = statements![i];
                    leftPrologues.Add(leftPrologue.Expression()!.Text());
                }
                for (int i = rightStandardPrologueEnd - 1; i >= 0; i--)
                {
                    var rightPrologue = declarations[i];
                    if (!leftPrologues.Has(rightPrologue.Expression()!.Text()))
                    {
                        left = CoreUtil.Concatenate(new[] { rightPrologue }, left);
                        changed = true;
                    }
                }
            }
        }

        return (left, changed);
    }

    private bool IsCustomPrologue(Node node)
    {
        return (EmitFlags(node) & EF.CustomPrologue) != 0;
    }

    private bool IsHoistedFunction(Node node)
    {
        return IsCustomPrologue(node) && NodeTests.IsFunctionDeclaration(node);
    }

    private static bool IsHoistedVariable(Node node)
    {
        return NodeTests.IsIdentifier(node.Name()!) && node.Initializer() is null;
    }

    private bool IsHoistedVariableStatement(Node node)
    {
        return IsCustomPrologue(node) &&
            NodeTests.IsVariableStatement(node) &&
            CoreUtil.Every(node.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes, IsHoistedVariable);
    }

    //
    // Name Generation
    //

    // Gets whether a given name has an associated AutoGenerateInfo entry.
    public bool HasAutoGenerateInfo(Node? node)
    {
        if (node is not null)
        {
            return autoGenerate is not null && autoGenerate.ContainsKey(node);
        }
        return false;
    }

    // Gets the associated AutoGenerateInfo entry for a given name.
    public AutoGenerateInfo? GetAutoGenerateInfo(Node? name)
    {
        if (name is null)
        {
            return null;
        }
        return autoGenerate?.GetValueOrDefault(name);
    }

    // Walks the associated AutoGenerateInfo entries of a name to find the root Node from which the name should be generated.
    public Node GetNodeForGeneratedName(Node name)
    {
        var info = autoGenerate?.GetValueOrDefault(name);
        if (info is not null && info.Flags.IsNode())
        {
            return GetNodeForGeneratedNameWorker(info.Node!, info.Id);
        }
        return name;
    }

    internal Node GetNodeForGeneratedNameWorker(Node node, AutoGenerateId autoGenerateId)
    {
        var original = Original(node);
        while (original is not null)
        {
            node = original;
            if (AstUtilities.IsMemberName(node))
            {
                // if "node" is a different generated name (having a different "autoGenerateId"), use it and stop traversing.
                var info = autoGenerate?.GetValueOrDefault(node);
                if (info is null || (info.Flags.IsNode() && info.Id != autoGenerateId))
                {
                    break;
                }
                if (info.Flags.IsNode())
                {
                    original = info.Node;
                    continue;
                }
            }
            original = Original(node);
        }
        return node;
    }

    //
    // Original Node Tracking
    //

    // Sets the original node for a given node.
    public void SetOriginal(Node node, Node original)
    {
        SetOriginalEx(node, original, false);
    }

    public void UnsetOriginal(Node node)
    {
        original?.Remove(node);
    }

    public void SetOriginalEx(Node node, Node original, bool allowOverwrite)
    {
        if (original is null)
        {
            throw new InvalidOperationException("Original cannot be nil.");
        }

        this.original ??= new Dictionary<Node, Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);

        if (!this.original.TryGetValue(node, out var existing))
        {
            this.original[node] = original;
            var emitNode = emitNodes.TryGet(original);
            if (emitNode is not null)
            {
                emitNodes.Get(node).CopyFrom(emitNode);
            }
        }
        else if (!allowOverwrite && existing != original)
        {
            throw new InvalidOperationException("Original node already set.");
        }
        else if (allowOverwrite)
        {
            this.original[node] = original;
        }
    }

    // Gets the original node for a given node.
    public Node? Original(Node node)
    {
        return original?.GetValueOrDefault(node);
    }

    // Gets the most original node associated with this node by walking Original pointers.
    public Node? MostOriginal(Node? node)
    {
        if (node is not null)
        {
            var orig = Original(node);
            while (orig is not null)
            {
                node = orig;
                orig = Original(node);
            }
        }
        return node;
    }

    // Gets the original parse tree node for a given node.
    public Node? ParseNode(Node? node)
    {
        node = MostOriginal(node);
        if (node is not null && IsParseTreeNode(node))
        {
            return node;
        }
        return null;
    }

    // PORT-NOTE: ast.IsParseTreeNode (internal/ast/utilities.go) is not yet present in the C# ast port.
    // Ported inline here; relocate to AstUtilities when the ast helper lands.
    private static bool IsParseTreeNode(Node node)
    {
        return (node.Flags & NodeFlags.Synthesized) == 0;
    }

    //
    // Emit-related Data
    //

    public EmitFlags EmitFlags(Node node)
    {
        var emitNode = emitNodes.TryGet(node);
        if (emitNode is not null)
        {
            return emitNode.emitFlags;
        }
        return global::Ts.Printer.EmitFlags.None;
    }

    public void SetEmitFlags(Node node, EmitFlags flags)
    {
        emitNodes.Get(node).emitFlags = flags;
    }

    public void AddEmitFlags(Node node, EmitFlags flags)
    {
        emitNodes.Get(node).emitFlags |= flags;
    }

    // Gets the range to use for a node when emitting comments.
    public TextRange CommentRange(Node node)
    {
        var emitNode = emitNodes.TryGet(node);
        if (emitNode is not null && (emitNode.flags & EmitNodeFlags.HasCommentRange) != 0)
        {
            return emitNode.commentRange;
        }
        return node.Loc;
    }

    // Sets the range to use for a node when emitting comments.
    public void SetCommentRange(Node node, TextRange loc)
    {
        var emitNode = emitNodes.Get(node);
        emitNode.commentRange = loc;
        emitNode.flags |= EmitNodeFlags.HasCommentRange;
    }

    // Sets the range to use for a node when emitting comments.
    public void AssignCommentRange(Node to, Node from)
    {
        SetCommentRange(to, CommentRange(from));
    }

    // Gets the range to use for a node when emitting source maps.
    public TextRange SourceMapRange(Node node)
    {
        var emitNode = emitNodes.TryGet(node);
        if (emitNode is not null && (emitNode.flags & EmitNodeFlags.HasSourceMapRange) != 0)
        {
            return emitNode.sourceMapRange;
        }
        return node.Loc;
    }

    // Sets the range to use for a node when emitting source maps.
    public void SetSourceMapRange(Node node, TextRange loc)
    {
        var emitNode = emitNodes.Get(node);
        emitNode.sourceMapRange = loc;
        emitNode.flags |= EmitNodeFlags.HasSourceMapRange;
    }

    // Sets the range to use for a node when emitting source maps.
    public void AssignSourceMapRange(Node to, Node from)
    {
        SetSourceMapRange(to, SourceMapRange(from));
    }

    // Sets the range to use for a node when emitting comments and source maps.
    public void AssignCommentAndSourceMapRanges(Node to, Node from)
    {
        var emitNode = emitNodes.Get(to);
        var commentRange = CommentRange(from);
        var sourceMapRange = SourceMapRange(from);
        emitNode.commentRange = commentRange;
        emitNode.sourceMapRange = sourceMapRange;
        emitNode.flags |= EmitNodeFlags.HasCommentRange | EmitNodeFlags.HasSourceMapRange;
    }

    // Gets the range for a token of a node when emitting source maps.
    // PORT-NOTE: Go `TokenSourceMapRange(node, kind) (core.TextRange, bool)` → Try pattern (05 §4.1).
    public bool TryTokenSourceMapRange(Node node, SyntaxKind kind, out TextRange loc)
    {
        var emitNode = emitNodes.TryGet(node);
        if (emitNode is not null && emitNode.tokenSourceMapRanges is not null)
        {
            if (emitNode.tokenSourceMapRanges.TryGetValue(kind, out loc))
            {
                return true;
            }
        }
        loc = default;
        return false;
    }

    // Sets the range for a token of a node when emitting source maps.
    public void SetTokenSourceMapRange(Node node, SyntaxKind kind, TextRange loc)
    {
        var emitNode = emitNodes.Get(node);
        emitNode.tokenSourceMapRanges ??= new Dictionary<SyntaxKind, TextRange>();
        emitNode.tokenSourceMapRanges[kind] = loc;
    }

    public Node? AssignedName(Node node)
    {
        return assignedName?.GetValueOrDefault(node);
    }

    public Node? TextSource(Node node)
    {
        return textSource?.GetValueOrDefault(node);
    }

    public void SetAssignedName(Node node, Node name)
    {
        assignedName ??= new Dictionary<Node, Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        assignedName[node] = name;
    }

    public Node? ClassThis(Node node)
    {
        return classThis?.GetValueOrDefault(node);
    }

    public void SetClassThis(Node node, IdentifierNode classThis)
    {
        this.classThis ??= new Dictionary<Node, Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        this.classThis[node] = classThis;
    }

    public void RequestEmitHelper(EmitHelper helper)
    {
        if (helper.Scoped)
        {
            throw new InvalidOperationException("Cannot request a scoped emit helper");
        }
        if (helper.Dependencies is not null)
        {
            foreach (var h in helper.Dependencies)
            {
                RequestEmitHelper(h);
            }
        }
        emitHelpers.Add(helper);
    }

    public EmitHelper[] ReadEmitHelpers()
    {
        var helpers = emitHelpers.Values().ToArray();
        emitHelpers.Clear();
        return helpers;
    }

    public void AddEmitHelper(Node node, params EmitHelper[] helper)
    {
        var emitNode = emitNodes.Get(node);
        foreach (var h in helper)
        {
            emitNode.helpers = CoreUtil.AppendIfUnique(emitNode.helpers, h);
        }
    }

    public void MoveEmitHelpers(Node source, Node target, Func<EmitHelper, bool> predicate)
    {
        var sourceEmitNode = emitNodes.TryGet(source);
        if (sourceEmitNode is null)
        {
            return;
        }
        var sourceEmitHelpers = sourceEmitNode.helpers;
        if (sourceEmitHelpers is null || sourceEmitHelpers.Length == 0)
        {
            return;
        }

        var targetEmitNode = emitNodes.Get(target);
        int helpersRemoved = 0;
        for (int i = 0; i < sourceEmitHelpers.Length; i++)
        {
            var helper = sourceEmitHelpers[i];
            if (predicate(helper))
            {
                helpersRemoved++;
                targetEmitNode.helpers = CoreUtil.AppendIfUnique(targetEmitNode.helpers, helper);
            }
            else if (helpersRemoved > 0)
            {
                sourceEmitHelpers[i - helpersRemoved] = helper;
            }
        }

        if (helpersRemoved > 0)
        {
            sourceEmitHelpers = sourceEmitHelpers[..(sourceEmitHelpers.Length - helpersRemoved)];
            sourceEmitNode.helpers = sourceEmitHelpers;
        }
    }

    public EmitHelper[]? GetEmitHelpers(Node node)
    {
        var emitNode = emitNodes.TryGet(node);
        if (emitNode is not null)
        {
            return emitNode.helpers;
        }
        return null;
    }

    public IdentifierNode? GetExternalHelpersModuleName(SourceFile node)
    {
        var parseNode = ParseNode(node.AsNode());
        if (parseNode is not null)
        {
            var emitNode = emitNodes.TryGet(parseNode);
            if (emitNode is not null)
            {
                return emitNode.externalHelpersModuleName;
            }
        }
        return null;
    }

    public void SetExternalHelpersModuleName(SourceFile node, IdentifierNode name)
    {
        var parseNode = ParseNode(node.AsNode());
        if (parseNode is null)
        {
            throw new InvalidOperationException("Node must be a parse tree node or have an Original pointer to a parse tree node.");
        }

        var emitNode = emitNodes.Get(parseNode);
        emitNode.externalHelpersModuleName = name;
    }

    public bool HasRecordedExternalHelpers(SourceFile node)
    {
        var parseNode = ParseNode(node.AsNode());
        if (parseNode is not null)
        {
            var emitNode = emitNodes.TryGet(parseNode);
            return emitNode is not null && (emitNode.externalHelpersModuleName is not null || (emitNode.emitFlags & global::Ts.Printer.EmitFlags.ExternalHelpers) != 0);
        }
        return false;
    }

    public bool IsCallToHelper(Node firstSegment, string helperName)
    {
        return NodeTests.IsCallExpression(firstSegment) &&
            NodeTests.IsIdentifier(firstSegment.Expression()!) &&
            (EmitFlags(firstSegment.Expression()!) & global::Ts.Printer.EmitFlags.HelperName) != 0 &&
            firstSegment.Expression()!.Text() == helperName;
    }

    //
    // Visitor Hooks
    //

    public StatementList? VisitVariableEnvironment(StatementList? nodes, NodeVisitor visitor)
    {
        StartVariableEnvironment();
        return EndAndMergeVariableEnvironmentList(visitor.VisitNodes(nodes));
    }

    public ParameterList? VisitParameters(ParameterList? nodes, NodeVisitor visitor)
    {
        StartVariableEnvironment();
        var scope = varScopeStack.Peek();
        var oldFlags = scope.flags;
        scope.flags |= EnvironmentFlags.InParameters;
        nodes = visitor.VisitNodes(nodes);

        // As of ES2015, any runtime execution that occurs for a parameter (such as evaluating an
        // initializer or a binding pattern) occurs in its own lexical scope. As a result, any expression
        // that we might transform that introduces a temporary variable would fail as the temporary variable
        // exists in a different lexical scope. To address this, we move any binding patterns and initializers
        // in a parameter list to the body if we detect a variable being hoisted while visiting a parameter list
        // when the emit target is greater than ES2015. (Which is now all targets.)
        if ((scope.flags & EnvironmentFlags.VariablesHoistedInParameters) != 0)
        {
            nodes = AddDefaultValueAssignmentsIfNeeded(nodes);
        }
        scope.flags = oldFlags;
        // !!! c.suspendVariableEnvironment()
        return nodes;
    }

    private ParameterList? AddDefaultValueAssignmentsIfNeeded(ParameterList? nodeList)
    {
        if (nodeList is null)
        {
            return nodeList;
        }
        Node[]? result = null;
        var nodes = nodeList.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            var parameter = nodes[i];
            var updated = AddDefaultValueAssignmentIfNeeded(parameter.AsParameterDeclaration());
            if (!ReferenceEquals(updated, parameter))
            {
                result ??= (Node[])nodes.Clone();
                result[i] = updated;
            }
        }
        if (result is not null)
        {
            var res = Factory.NewNodeList(result);
            res.Loc = nodeList.Loc;
            return res;
        }
        return nodeList;
    }

    private Node AddDefaultValueAssignmentIfNeeded(ParameterDeclaration parameter)
    {
        // A rest parameter cannot have a binding pattern or an initializer, so let's just ignore it.
        if (parameter.DotDotDotToken is not null)
        {
            return parameter.AsNode();
        }
        else if (AstUtilities.IsBindingPattern(parameter.Name()))
        {
            return AddDefaultValueAssignmentForBindingPattern(parameter);
        }
        else if (parameter.Initializer is not null)
        {
            return AddDefaultValueAssignmentForInitializer(parameter, parameter.Name(), parameter.Initializer);
        }
        return parameter.AsNode();
    }

    private Node AddDefaultValueAssignmentForBindingPattern(ParameterDeclaration parameter)
    {
        // !!! Requires printer NodeFactory helpers NewStrictEqualityExpression / NewVoidZeroExpression
        // (factory.go, later printer wave). Not exercised by this emit-infrastructure wave.
        throw new NotImplementedException("addDefaultValueAssignmentForBindingPattern: requires factory.go NodeFactory helpers (later printer wave)");
    }

    private Node AddDefaultValueAssignmentForInitializer(ParameterDeclaration parameter, Node? name, Node initializer)
    {
        // !!! Requires printer NodeFactory helpers NewAssignmentExpression / NewTypeCheck
        // (factory.go, later printer wave). Not exercised by this emit-infrastructure wave.
        throw new NotImplementedException("addDefaultValueAssignmentForInitializer: requires factory.go NodeFactory helpers (later printer wave)");
    }

    public void AddInitializationStatement(Node node)
    {
        // PORT-NOTE: Go core.Stack.Peek() returns nil on empty; C# Stack.Peek() throws (== Go panic path).
        var scope = varScopeStack.Peek();
        AddEmitFlags(node, EF.CustomPrologue);
        scope.initializationStatements.Add(node);
    }

    public BlockOrExpression? VisitFunctionBody(BlockOrExpression? node, NodeVisitor visitor)
    {
        // !!! c.resumeVariableEnvironment()
        var updated = visitor.VisitNode(node);
        var declarations = EndVariableEnvironment();
        if (declarations.Length == 0)
        {
            return updated;
        }

        if (updated is null)
        {
            return Factory.NewBlock(Factory.NewNodeList(declarations), true /*multiLine*/);
        }

        if (!NodeTests.IsBlock(updated))
        {
            var statements = MergeEnvironment(new[] { Factory.NewReturnStatement(updated) }, declarations);
            return Factory.NewBlock(Factory.NewNodeList(statements ?? []), false /*multiLine*/);
        }

        return Factory.UpdateBlock(
            updated.AsBlock(),
            MergeEnvironmentList(updated.StatementList(), declarations),
            updated.AsBlock().MultiLine);
    }

    public Statement? VisitIterationBody(Statement? body, NodeVisitor visitor)
    {
        if (body is null)
        {
            return null;
        }

        StartLexicalEnvironment();
        var updated = VisitEmbeddedStatement(body, visitor);
        if (updated is null)
        {
            throw new InvalidOperationException("Expected visitor to return a statement.");
        }

        var statements = new List<Node>(EndLexicalEnvironment());
        if (statements.Count > 0)
        {
            if (NodeTests.IsBlock(updated))
            {
                statements.AddRange(updated.Statements() ?? []);
                var statementsList = Factory.NewNodeList(statements.ToArray());
                statementsList.Loc = updated.StatementList()!.Loc;
                return Factory.UpdateBlock(updated.AsBlock(), statementsList, updated.AsBlock().MultiLine);
            }
            statements.Add(updated);
            return Factory.NewBlock(Factory.NewNodeList(statements.ToArray()), true /*multiLine*/);
        }

        return updated;
    }

    public Statement? VisitEmbeddedStatement(Statement? node, NodeVisitor visitor)
    {
        if (node is null)
        {
            return null;
        }
        var embeddedStatement = visitor.VisitEmbeddedStatement(node);
        if (embeddedStatement is null || NodeTests.IsNotEmittedStatement(embeddedStatement))
        {
            var emptyStatement = visitor.Factory.NewEmptyStatement();
            emptyStatement.Loc = node.Loc;
            SetOriginal(emptyStatement, node);
            AssignCommentRange(emptyStatement, node);
            return emptyStatement;
        }
        return embeddedStatement;
    }

    public Node SetSyntheticLeadingComments(Node node, List<SynthesizedComment>? comments)
    {
        emitNodes.Get(node).leadingComments = comments;
        return node;
    }

    public Node AddSyntheticLeadingComment(Node node, SyntaxKind kind, string text, bool hasTrailingNewLine)
    {
        var emitNode = emitNodes.Get(node);
        (emitNode.leadingComments ??= new List<SynthesizedComment>()).Add(new SynthesizedComment
        {
            Kind = kind,
            Loc = new TextRange(-1, -1),
            HasTrailingNewLine = hasTrailingNewLine,
            Text = text,
        });
        return node;
    }

    public List<SynthesizedComment>? GetSyntheticLeadingComments(Node node)
    {
        if (emitNodes.Has(node))
        {
            return emitNodes.Get(node).leadingComments;
        }
        return null;
    }

    public Node SetSyntheticTrailingComments(Node node, List<SynthesizedComment>? comments)
    {
        emitNodes.Get(node).trailingComments = comments;
        return node;
    }

    public Node AddSyntheticTrailingComment(Node node, SyntaxKind kind, string text, bool hasTrailingNewLine)
    {
        var emitNode = emitNodes.Get(node);
        (emitNode.trailingComments ??= new List<SynthesizedComment>()).Add(new SynthesizedComment
        {
            Kind = kind,
            Loc = new TextRange(-1, -1),
            HasTrailingNewLine = hasTrailingNewLine,
            Text = text,
        });
        return node;
    }

    public List<SynthesizedComment>? GetSyntheticTrailingComments(Node node)
    {
        if (emitNodes.Has(node))
        {
            return emitNodes.Get(node).trailingComments;
        }
        return null;
    }

    // SetTypeNode stores the original type node on a name node when the type is erased,
    // so the emitter can use the type's position for comment preservation.
    public void SetTypeNode(Node node, TypeNode typeNode)
    {
        emitNodes.Get(node).typeNode = typeNode;
    }

    // GetTypeNode gets the type node stored on a name node by the type eraser.
    public TypeNode? GetTypeNode(Node node)
    {
        var emitNode = emitNodes.TryGet(node);
        if (emitNode is not null)
        {
            return emitNode.typeNode;
        }
        return null;
    }

    public Statement NewNotEmittedStatement(Node node)
    {
        var statement = Factory.NewNotEmittedStatement();
        statement.Loc = node.Loc;
        SetOriginal(statement, node);
        AssignCommentRange(statement, node);
        return statement;
    }
}

internal enum EnvironmentFlags
{
    None = 0,
    InParameters = 1 << 0, // currently visiting a parameter list
    VariablesHoistedInParameters = 1 << 1, // a temp variable was hoisted while visiting a parameter list
}

internal sealed class VarScope
{
    public List<Node> variables = new();
    public List<Node> functions = new();
    public EnvironmentFlags flags;
    public List<Node> initializationStatements = new();
}

public struct AutoGenerateOptions
{
    public GeneratedIdentifierFlags Flags;
    public string Prefix;
    public string Suffix;

    public AutoGenerateOptions()
    {
        Flags = GeneratedIdentifierFlags.None;
        Prefix = "";
        Suffix = "";
    }
}

public readonly record struct AutoGenerateId(uint Value);

public sealed class AutoGenerateInfo
{
    public GeneratedIdentifierFlags Flags; // Specifies whether to auto-generate the text for an identifier.
    public AutoGenerateId Id; // Ensures unique generated identifiers get unique names, but clones get the same name.
    public string Prefix = ""; // Optional prefix to apply to the start of the generated name
    public string Suffix = ""; // Optional suffix to apply to the end of the generated name
    public Node? Node; // For a GeneratedIdentifierFlagsNode, the node from which to generate an identifier

    public AutoGenerateInfo Clone()
    {
        return new AutoGenerateInfo
        {
            Flags = Flags,
            Id = Id,
            Prefix = Prefix,
            Suffix = Suffix,
            Node = Node,
        };
    }
}

internal enum EmitNodeFlags : uint
{
    HasCommentRange = 1 << 0,
    HasSourceMapRange = 1 << 1,
}

public struct SynthesizedComment
{
    public SyntaxKind Kind;
    public TextRange Loc;
    public bool HasLeadingNewLine;
    public bool HasTrailingNewLine;
    public string Text;
}

internal sealed class EmitNode
{
    public EmitNode()
    {
    }

    public EmitNodeFlags flags;
    public EmitFlags emitFlags;
    public TextRange commentRange;
    public TextRange sourceMapRange;
    public Dictionary<SyntaxKind, TextRange>? tokenSourceMapRanges;
    public EmitHelper[]? helpers;
    public IdentifierNode? externalHelpersModuleName;
    public List<SynthesizedComment>? leadingComments;
    public List<SynthesizedComment>? trailingComments;
    public TypeNode? typeNode;

    // NOTE: This method is not guaranteed to be thread-safe
    public void CopyFrom(EmitNode source)
    {
        flags = source.flags;
        emitFlags = source.emitFlags;
        commentRange = source.commentRange;
        sourceMapRange = source.sourceMapRange;
        tokenSourceMapRanges = source.tokenSourceMapRanges is null ? null : new Dictionary<SyntaxKind, TextRange>(source.tokenSourceMapRanges);
        helpers = source.helpers is null ? null : (EmitHelper[])source.helpers.Clone();
        externalHelpersModuleName = source.externalHelpersModuleName;
    }
}

// PORT-NOTE: `Priority`/`EmitHelper` (from helpers.go) and the printer `NodeFactory` (from factory.go) were
// previously co-hosted here for the emit-infrastructure wave. They now live in their own files — Helpers.cs
// and Factory.cs respectively — as of the printer emit-support wave. The `Utilities` helpers below remain
// borrowed from utilities.go until that file is ported.


// PORT-NOTE: helpers borrowed from printer/utilities.go for this wave (NameGenerator + the generated-identifier
// factory slice depend on them). Relocate the full utilities.go port to Utilities.cs when that wave lands and
// remove these definitions.
internal static partial class Utilities
{
    internal static bool HasLeadingHash(string text)
    {
        return text.Length > 0 && text[0] == '#';
    }

    internal static string RemoveLeadingHash(string text)
    {
        return HasLeadingHash(text) ? text[1..] : text;
    }

    internal static string EnsureLeadingHash(string text)
    {
        return HasLeadingHash(text) ? text : "#" + text;
    }

    internal static string FormatGeneratedName(bool privateName, string prefix, string @base, string suffix)
    {
        string name = RemoveLeadingHash(prefix) + RemoveLeadingHash(@base) + RemoveLeadingHash(suffix);
        if (privateName)
        {
            return EnsureLeadingHash(name);
        }
        return name;
    }

    private static bool IsAsciiWordCharacter(int ch)
    {
        return StringUtil.IsASCIILetter(ch) || StringUtil.IsDigit(ch) || ch == '_';
    }

    internal static string MakeIdentifierFromModuleName(string moduleName)
    {
        moduleName = global::Ts.TsPath.TsPath.GetBaseFileName(moduleName);
        var builder = new StringBuilder();
        int start = 0;
        int pos = 0;
        while (pos < moduleName.Length)
        {
            int ch = moduleName[pos];
            if (pos == 0 && StringUtil.IsDigit(ch))
            {
                builder.Append('_');
            }
            else if (!IsAsciiWordCharacter(ch))
            {
                if (start < pos)
                {
                    builder.Append(moduleName[start..pos]);
                }
                builder.Append('_');
                start = pos + 1;
            }
            pos++;
        }
        if (start < pos)
        {
            builder.Append(moduleName[start..pos]);
        }
        return builder.ToString();
    }

    internal static int FindSpanEnd<T>(T[]? array, Func<T, bool> test, int start)
    {
        int i = start;
        while (i < (array?.Length ?? 0) && test(array![i]))
        {
            i++;
        }
        return i;
    }
}
