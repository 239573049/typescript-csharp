// Ported from tsgo internal/transformers/estransforms/using.go — the `using` / `await using`
// explicit-resource-management downlevel transformer (__addDisposableResource/__disposeResources).

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

using PrinterNs = global::Ts.Printer;

// using.go:26-32
internal enum UsingKind
{
    None = 0,
    Sync = 1,
    Async = 2,
}

internal sealed class UsingDeclarationTransformer : Transformer
{
    // using.go:11-19
    private Dictionary<string, Node>? exportBindings;
    private List<string> exportBindingNames = new();
    private List<Node> exportVars = new();
    private IdentifierNode? defaultExportBinding;
    private IdentifierNode? exportEqualsBinding;

    // using.go:21-24
    private UsingDeclarationTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    internal static Transformer NewUsingDeclarationTransformer(TransformOptions opts) => new UsingDeclarationTransformer(opts);

    // using.go:34-52
    private Node? Visit(Node node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsUsing) == 0)
        {
            return node;
        }

        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                return VisitSourceFile(node.AsSourceFile());
            case SyntaxKind.Block:
                return VisitBlock(node.AsBlock());
            case SyntaxKind.ForStatement:
                return VisitForStatement(node.AsForStatement());
            case SyntaxKind.ForOfStatement:
                return VisitForOfStatement(node.AsForInOrOfStatement());
            default:
                return Visitor().VisitEachChild(node);
        }
    }

    // using.go:54-190
    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }

        Node visited;
        var usingKind = GetUsingKindOfStatements(node.Statements!.Nodes);
        if (usingKind != UsingKind.None)
        {
            // Imports and exports must stay at the top level. This means we must hoist all imports, exports, and
            // top-level function declarations and bindings out of the `try` statements we generate. (See the
            // worked example in using.go:62-99.) We hoist bindings to a new outer statement list while moving all
            // other statements in the source file into the `try` block, preserving all statements prior to the
            // first top-level `using` to isolate the complexity of the transformed output.
            EmitContext().StartVariableEnvironment();

            exportBindings = new Dictionary<string, Node>(System.StringComparer.Ordinal);
            exportVars = new List<Node>();

            var prologue = Factory().SplitStandardPrologue(node.Statements.Nodes, out var rest);
            var topLevelStatements = new List<Node>();
            var (visitedPrologue, _) = Visitor().VisitSlice(prologue);
            if (visitedPrologue != null)
            {
                topLevelStatements.AddRange(visitedPrologue);
            }

            // Collect and transform any leading statements up to the first `using` or `await using`. This preserves
            // the original statement order much as is possible.

            var pos = 0;
            while (pos < rest.Length)
            {
                var statement = rest[pos];
                if (GetUsingKind(statement) != UsingKind.None)
                {
                    if (pos > 0)
                    {
                        var (visitedLeading, _) = Visitor().VisitSlice(rest[..pos]);
                        if (visitedLeading != null)
                        {
                            topLevelStatements.AddRange(visitedLeading);
                        }
                    }
                    break;
                }
                pos++;
            }

            if (pos >= rest.Length)
            {
                throw new global::System.InvalidOperationException("Should have encountered at least one 'using' statement.");
            }

            // transform the rest of the body
            var envBinding = CreateEnvBinding();
            var bodyStatements = TransformUsingDeclarations(rest[pos..], envBinding, topLevelStatements);

            // add `export {}` declarations for any hoisted bindings.
            if (exportBindings.Count > 0)
            {
                var exportSpecifiers = new List<Node>(exportBindingNames.Count);
                foreach (var name in exportBindingNames)
                {
                    exportBindings.TryGetValue(name, out var specifier);
                    global::Ts.Core.Debug.Assert(specifier != null, "Missing export binding for hoisted export name");
                    exportSpecifiers.Add(specifier!);
                }
                topLevelStatements.Add(
                    Factory().NewExportDeclaration(
                        null,  /*modifiers*/
                        false, /*isTypeOnly*/
                        Factory().NewNamedExports(
                            Factory().NewNodeList(exportSpecifiers.ToArray())),
                        null, /*moduleSpecifier*/
                        null  /*attributes*/));
            }

            var endedEnvironment = EmitContext().EndVariableEnvironment();
            if (endedEnvironment != null)
            {
                topLevelStatements.AddRange(endedEnvironment);
            }
            if (exportVars.Count > 0)
            {
                topLevelStatements.Add(Factory().NewVariableStatement(
                    Factory().AsNodeFactory().NewModifierList([
                        Factory().AsNodeFactory().NewModifier(SyntaxKind.ExportKeyword),
                    ]),
                    Factory().NewVariableDeclarationList(
                        Factory().NewNodeList(exportVars.ToArray()),
                        NodeFlags.Let)));
            }
            topLevelStatements.AddRange(CreateDownlevelUsingStatements(bodyStatements, envBinding, usingKind == UsingKind.Async));

            if (exportEqualsBinding != null)
            {
                topLevelStatements.Add(Factory().NewExportAssignment(
                    null, /*modifiers*/
                    true, /*isExportEquals*/
                    null, /*typeNode*/
                    exportEqualsBinding));
            }

            visited = Factory().AsNodeFactory().UpdateSourceFile(node, Factory().NewNodeList(topLevelStatements.ToArray()), node.EndOfFileToken!);
        }
        else
        {
            visited = Visitor().VisitEachChild(node.AsNode())!;
        }
        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        exportVars = new List<Node>();
        exportBindings = null;
        exportBindingNames = new List<string>();
        defaultExportBinding = null;
        exportEqualsBinding = null;
        return visited;
    }

    // using.go:192-209
    private Node? VisitBlock(Block node)
    {
        var usingKind = GetUsingKindOfStatements(node.Statements!.Nodes);
        if (usingKind != UsingKind.None)
        {
            var prologue = Factory().SplitStandardPrologue(node.Statements.Nodes, out var rest);
            var envBinding = CreateEnvBinding();
            var statements = new List<Node>(prologue.Length + 2);
            var (visitedPrologue, _) = Visitor().VisitSlice(prologue);
            if (visitedPrologue != null)
            {
                statements.AddRange(visitedPrologue);
            }
            statements.AddRange(CreateDownlevelUsingStatements(
                TransformUsingDeclarations(rest, envBinding, null /*topLevelStatements*/),
                envBinding,
                usingKind == UsingKind.Async));
            var statementList = Factory().NewNodeList(statements.ToArray());
            statementList.Loc = node.Statements.Loc;
            return Factory().AsNodeFactory().UpdateBlock(node, statementList, node.MultiLine);
        }
        return Visitor().VisitEachChild(node.AsNode());
    }

    // using.go:211-239
    private Node? VisitForStatement(ForStatement node)
    {
        if (node.Initializer != null && IsUsingVariableDeclarationList(node.Initializer))
        {
            // given:
            //
            //  for (using x = expr; cond; incr) { ... }
            //
            // produces a shallow transformation to:
            //
            //  {
            //    using x = expr;
            //    for (; cond; incr) { ... }
            //  }
            //
            // before handing the shallow transformation back to the visitor for an in-depth transformation.
            return Visitor().VisitNode(
                Factory().NewBlock(Factory().NewNodeList([
                    Factory().NewVariableStatement(null /*modifiers*/, node.Initializer),
                    Factory().AsNodeFactory().UpdateForStatement(
                        node,
                        null, /*initializer*/
                        node.Condition,
                        node.Incrementor,
                        node.Statement),
                ]), false /*multiLine*/));
        }
        return Visitor().VisitEachChild(node.AsNode());
    }

    // using.go:241-304
    private Node? VisitForOfStatement(ForInOrOfStatement node)
    {
        if (node.Initializer != null && IsUsingVariableDeclarationList(node.Initializer))
        {
            // given:
            //
            //  for (using x of y) { ... }
            //
            // produces a shallow transformation to:
            //
            //  for (const x_1 of y) {
            //    using x = x;
            //    ...
            //  }
            //
            // before handing the shallow transformation back to the visitor for an in-depth transformation.
            var forInitializer = node.Initializer.AsVariableDeclarationList();
            var forDecl = forInitializer.Declarations!.Nodes.Length > 0 ? forInitializer.Declarations.Nodes[0] : null;
            forDecl ??= Factory().NewVariableDeclaration(Factory().NewTempVariable(), null, null, null);

            var isAwaitUsing = GetUsingKindOfVariableDeclarationList(forInitializer) == UsingKind.Async;
            var temp = Factory().NewGeneratedNameForNode(forDecl.Name()!);
            var usingVar = Factory().AsNodeFactory().UpdateVariableDeclaration(forDecl.AsVariableDeclaration(), forDecl.Name(), null /*exclamationToken*/, null /*type*/, temp);
            var usingVarList = Factory().NewVariableDeclarationList(
                Factory().NewNodeList([usingVar]),
                isAwaitUsing ? NodeFlags.AwaitUsing : NodeFlags.Using);
            var usingVarStatement = Factory().NewVariableStatement(null /*modifiers*/, usingVarList);
            Node statement;
            if (NodeTests.IsBlock(node.Statement!))
            {
                var statements = new List<Node>((node.Statement!.Statements()?.Length ?? 0) + 1) { usingVarStatement };
                statements.AddRange(node.Statement!.Statements()!);
                statement = Factory().AsNodeFactory().UpdateBlock(
                    node.Statement!.AsBlock(),
                    Factory().NewNodeList(statements.ToArray()),
                    node.Statement!.AsBlock().MultiLine);
            }
            else
            {
                statement = Factory().NewBlock(
                    Factory().NewNodeList([
                        usingVarStatement,
                        node.Statement!,
                    ]),
                    true /*multiLine*/);
            }
            return Visitor().VisitNode(
                Factory().AsNodeFactory().UpdateForInOrOfStatement(
                    node,
                    node.AwaitModifier,
                    Factory().NewVariableDeclarationList(
                        Factory().NewNodeList([
                            Factory().NewVariableDeclaration(temp, null /*exclamationToken*/, null /*type*/, null),
                        ]),
                        NodeFlags.Const),
                    node.Expression,
                    statement));
        }
        return Visitor().VisitEachChild(node.AsNode());
    }

    // using.go:306-395
    private List<Node> TransformUsingDeclarations(Node[] statementsIn, IdentifierNode envBinding, List<Node>? topLevelStatements)
    {
        var statements = new List<Node>();

        Node? Hoist(Node node)
        {
            if (topLevelStatements == null)
            {
                return node;
            }

            switch (node.Kind)
            {
                case SyntaxKind.ImportDeclaration:
                case SyntaxKind.ImportEqualsDeclaration:
                case SyntaxKind.ExportDeclaration:
                case SyntaxKind.FunctionDeclaration:
                    HoistImportOrExportOrHoistedDeclaration(node, topLevelStatements);
                    return null;
                case SyntaxKind.ExportAssignment:
                    return HoistExportAssignment(node.AsExportAssignment());
                case SyntaxKind.ClassDeclaration:
                    return HoistClassDeclaration(node.AsClassDeclaration());
                case SyntaxKind.VariableStatement:
                    return HoistVariableStatement(node.AsVariableStatement());
            }

            return node;
        }

        void HoistOrAppendNode(Node node)
        {
            var hoisted = Hoist(node);
            if (hoisted != null)
            {
                statements.Add(hoisted);
            }
        }

        foreach (var statement in statementsIn)
        {
            var usingKind = GetUsingKind(statement);
            if (usingKind != UsingKind.None)
            {
                var varStatement = statement.AsVariableStatement();
                var declarationList = varStatement.DeclarationList!;
                var declarations = new List<Node>();
                foreach (var declarationIter in declarationList.AsVariableDeclarationList().Declarations!.Nodes)
                {
                    var declaration = declarationIter;
                    if (!NodeTests.IsIdentifier(declaration.Name()!))
                    {
                        // Since binding patterns are a grammar error, we reset `declarations` so we don't process this as a `using`.
                        declarations.Clear();
                        break;
                    }

                    // perform a shallow transform for any named evaluation
                    if (NamedEvaluationUtilities.IsNamedEvaluation(EmitContext(), declaration))
                    {
                        declaration = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), declaration, false /*ignoreEmptyStringLiteral*/, "" /*assignedName*/);
                    }

                    var initializer = Visitor().VisitNode(declaration.Initializer());
                    initializer ??= Factory().NewVoidZeroExpression();
                    declarations.Add(Factory().AsNodeFactory().UpdateVariableDeclaration(
                        declaration.AsVariableDeclaration(),
                        declaration.Name(),
                        null, /*exclamationToken*/
                        null, /*type*/
                        Factory().NewAddDisposableResourceHelper(
                            envBinding,
                            initializer,
                            usingKind == UsingKind.Async)));
                }

                // Only replace the statement if it was valid.
                if (declarations.Count > 0)
                {
                    var varList = Factory().NewVariableDeclarationList(Factory().NewNodeList(declarations.ToArray()), NodeFlags.Const);
                    EmitContext().SetOriginal(varList, declarationList);
                    varList.Loc = declarationList.Loc;
                    HoistOrAppendNode(Factory().AsNodeFactory().UpdateVariableStatement(varStatement, null /*modifiers*/, varList));
                    continue;
                }
            }

            var result = Visit(statement);
            if (result != null)
            {
                if (result!.Kind == SyntaxKind.SyntaxList)
                {
                    foreach (var child in result.AsSyntaxList().Children!)
                    {
                        HoistOrAppendNode(child);
                    }
                }
                else
                {
                    HoistOrAppendNode(result);
                }
            }
        }
        return statements;
    }

    // using.go:397-400
    private static void HoistImportOrExportOrHoistedDeclaration(Node node, List<Node> topLevelStatements)
    {
        // NOTE: `node` has already been visited
        topLevelStatements.Add(node);
    }

    // using.go:402-408
    private Node HoistExportAssignment(ExportAssignment node)
    {
        if (node.IsExportEquals)
        {
            return HoistExportEquals(node);
        }
        else
        {
            return HoistExportDefault(node);
        }
    }

    // using.go:410-443
    private Node HoistExportDefault(ExportAssignment node)
    {
        // NOTE: `node` has already been visited
        if (defaultExportBinding != null)
        {
            // invalid case of multiple `export default` declarations. Don't assert here, just pass it through
            return node.AsNode();
        }

        // given:
        //
        //   export default expr;
        //
        // produces:
        //
        //   // top level
        //   var default_1;
        //   export { default_1 as default };
        //
        //   // body
        //   default_1 = expr;

        defaultExportBinding = Factory().NewUniqueNameEx("_default", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.ReservedInNestedScopes | PrinterNs.GeneratedIdentifierFlags.FileLevel | PrinterNs.GeneratedIdentifierFlags.Optimistic });
        HoistBindingIdentifier(defaultExportBinding, true /*isExport*/, Factory().NewIdentifier("default"), node.AsNode());

        // give a class or function expression an assigned name, if needed.
        var expression = node.Expression!;
        var innerExpression = AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.All);
        if (NamedEvaluationUtilities.IsNamedEvaluation(EmitContext(), innerExpression))
        {
            innerExpression = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), innerExpression, false /*ignoreEmptyStringLiteral*/, "default");
            expression = Factory().RestoreOuterExpressions(expression, innerExpression, OuterExpressionKinds.All);
        }

        var assignment = Factory().NewAssignmentExpression(defaultExportBinding, expression);
        return Factory().NewExpressionStatement(assignment);
    }

    // using.go:445-475
    private Node HoistExportEquals(ExportAssignment node)
    {
        // NOTE: `node` has already been visited
        if (exportEqualsBinding != null)
        {
            // invalid case of multiple `export default` declarations. Don't assert here, just pass it through
            return node.AsNode();
        }

        // given:
        //
        //   export = expr;
        //
        // produces:
        //
        //   // top level
        //   var default_1;
        //
        //   try {
        //       // body
        //       default_1 = expr;
        //   } ...
        //
        //   // top level suffix
        //   export = default_1;

        exportEqualsBinding = Factory().NewUniqueNameEx("_default", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.ReservedInNestedScopes | PrinterNs.GeneratedIdentifierFlags.FileLevel | PrinterNs.GeneratedIdentifierFlags.Optimistic });
        EmitContext().AddVariableDeclaration(exportEqualsBinding);

        // give a class or function expression an assigned name, if needed.
        var assignment = Factory().NewAssignmentExpression(exportEqualsBinding, node.Expression!);
        return Factory().NewExpressionStatement(assignment);
    }

    // using.go:477-560
    private Node HoistClassDeclaration(ClassDeclaration node)
    {
        // NOTE: `node` has already been visited
        if (node.Name() == null && defaultExportBinding != null)
        {
            // invalid case of multiple `export default` declarations. Don't assert here, just pass it through
            return node.AsNode();
        }

        var isExported = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export);
        var isDefault = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Default);

        // When hoisting a class declaration at the top level of a file containing a top-level `using` statement, we
        // must first convert it to a class expression so that we can hoist the binding outside of the `try`.
        var expression = EsTransformUtilities.ConvertClassDeclarationToClassExpression(EmitContext(), node);
        if (node.Name() != null)
        {
            // (See the worked example in using.go:490-512.) If the class is exported, we also produce an
            // `export { C };`
            HoistBindingIdentifier(Factory().GetLocalName(node.AsNode()), isExported && !isDefault, null /*exportAlias*/, node.AsNode());
            expression = Factory().NewAssignmentExpression(Factory().GetDeclarationName(node.AsNode()), expression);
            EmitContext().SetOriginal(expression, node.AsNode());
            EmitContext().SetSourceMapRange(expression, node.Loc);
            EmitContext().SetCommentRange(expression, node.Loc);
            if (NamedEvaluationUtilities.IsNamedEvaluation(EmitContext(), expression))
            {
                expression = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), expression, false /*ignoreEmptyStringLiteral*/, "" /*assignedName*/);
            }
        }

        if (isDefault && defaultExportBinding == null)
        {
            // In the case of a default export, we create a temporary variable that we export as the default and then
            // assign to that variable. (See the worked example in using.go:526-549.)
            // Though we will never reassign `default_1`, this most closely matches the specified runtime semantics.
            defaultExportBinding = Factory().NewUniqueNameEx("_default", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.ReservedInNestedScopes | PrinterNs.GeneratedIdentifierFlags.FileLevel | PrinterNs.GeneratedIdentifierFlags.Optimistic });
            HoistBindingIdentifier(defaultExportBinding, true /*isExport*/, Factory().NewIdentifier("default"), node.AsNode());
            expression = Factory().NewAssignmentExpression(defaultExportBinding, expression);
            EmitContext().SetOriginal(expression, node.AsNode());
            if (NamedEvaluationUtilities.IsNamedEvaluation(EmitContext(), expression))
            {
                expression = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), expression, false /*ignoreEmptyStringLiteral*/, "default");
            }
        }

        return Factory().NewExpressionStatement(expression);
    }

    // using.go:562-580
    private Node? HoistVariableStatement(VariableStatement node)
    {
        // NOTE: `node` has already been visited
        var expressions = new List<Node>();
        var isExported = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export);
        foreach (var variable in node.DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
        {
            HoistBindingElement(variable, isExported, variable);
            if (variable.Initializer() != null)
            {
                expressions.Add(HoistInitializedVariable(variable.AsVariableDeclaration()));
            }
        }
        if (expressions.Count > 0)
        {
            var statement = Factory().NewExpressionStatement(Factory().InlineExpressions(expressions.ToArray())!);
            EmitContext().SetOriginal(statement, node.AsNode());
            EmitContext().SetCommentRange(statement, node.Loc);
            EmitContext().SetSourceMapRange(statement, node.Loc);
            return statement;
        }
        return null;
    }

    // using.go:582-600
    private Node HoistInitializedVariable(VariableDeclaration node)
    {
        // NOTE: `node` has already been visited
        if (node.Initializer == null)
        {
            throw new global::System.InvalidOperationException("Expected initializer");
        }
        Node target;
        if (NodeTests.IsIdentifier(node.Name()!))
        {
            target = node.Name()!.Clone(Factory())!;
            EmitContext().SetEmitFlags(target, EmitContext().EmitFlags(target) & ~(PrinterNs.EmitFlags.LocalName | PrinterNs.EmitFlags.ExportName));
        }
        else
        {
            target = Transformers.ConvertBindingPatternToAssignmentPattern(EmitContext(), node.Name()!.AsBindingPattern());
        }

        var assignment = Factory().NewAssignmentExpression(target, node.Initializer!);
        EmitContext().SetOriginal(assignment, node.AsNode());
        EmitContext().SetCommentRange(assignment, node.Loc);
        EmitContext().SetSourceMapRange(assignment, node.Loc);
        return assignment;
    }

    // using.go:602-613
    private void HoistBindingElement(Node node /*VariableDeclaration|BindingElement*/, bool isExportedDeclaration, Node original)
    {
        // NOTE: `node` has already been visited
        if (AstUtilities.IsBindingPattern(node.Name()!))
        {
            foreach (var element in node.Name()!.Elements() ?? [])
            {
                if (element.Name() != null)
                {
                    HoistBindingElement(element, isExportedDeclaration, original);
                }
            }
        }
        else
        {
            HoistBindingIdentifier(node.Name()!, isExportedDeclaration, null /*exportAlias*/, original);
        }
    }

    // using.go:615-652
    private void HoistBindingIdentifier(IdentifierNode node, bool isExport, IdentifierNode? exportAlias, Node? original)
    {
        // NOTE: `node` has already been visited
        var name = node;
        if (!Transformers.IsGeneratedIdentifier(EmitContext(), node))
        {
            name = name.Clone(Factory())!;
        }
        if (isExport)
        {
            if (exportAlias == null && !Transformers.IsLocalName(EmitContext(), name))
            {
                var varDecl = Factory().NewVariableDeclaration(name, null /*exclamationToken*/, null /*type*/, null /*initializer*/);
                if (original != null)
                {
                    EmitContext().SetOriginal(varDecl, original);
                }
                exportVars.Add(varDecl);
                return;
            }

            Node? localName = null;
            Node? exportName;
            if (exportAlias != null)
            {
                localName = name;
                exportName = exportAlias;
            }
            else
            {
                exportName = name;
            }
            var specifier = Factory().NewExportSpecifier(false /*isTypeOnly*/, localName, exportName);
            if (original != null)
            {
                EmitContext().SetOriginal(specifier, original);
            }
            exportBindings ??= new Dictionary<string, Node>(System.StringComparer.Ordinal);
            if (!exportBindings.ContainsKey(name.Text()))
            {
                exportBindingNames.Add(name.Text());
            }
            exportBindings[name.Text()] = specifier;
        }
        EmitContext().AddVariableDeclaration(name);
    }

    // using.go:654-656
    private IdentifierNode CreateEnvBinding() => Factory().NewUniqueName("env");

    // using.go:658-759
    private List<Node> CreateDownlevelUsingStatements(List<Node> bodyStatements, IdentifierNode envBinding, bool async)
    {
        var statements = new List<Node>(2);

        // produces:
        //
        //  const env_1 = { stack: [], error: void 0, hasError: false };
        //
        var envObject = Factory().NewObjectLiteralExpression(Factory().NewNodeList([
            Factory().NewPropertyAssignment(null /*modifiers*/, Factory().NewIdentifier("stack"), null /*postfixToken*/, null /*typeNode*/, Factory().NewArrayLiteralExpression(null, false /*multiLine*/)),
            Factory().NewPropertyAssignment(null /*modifiers*/, Factory().NewIdentifier("error"), null /*postfixToken*/, null /*typeNode*/, Factory().NewVoidZeroExpression()),
            Factory().NewPropertyAssignment(null /*modifiers*/, Factory().NewIdentifier("hasError"), null /*postfixToken*/, null /*typeNode*/, Factory().NewFalseExpression()),
        ]), false /*multiLine*/);
        var envVar = Factory().NewVariableDeclaration(envBinding, null /*exclamationToken*/, null /*typeNode*/, envObject);
        var envVarList = Factory().NewVariableDeclarationList(Factory().NewNodeList([envVar]), NodeFlags.Const);
        var envVarStatement = Factory().NewVariableStatement(null /*modifiers*/, envVarList);
        statements.Add(envVarStatement);

        // when `async` is `false`, produces:
        //
        //  try { <bodyStatements> }
        //  catch (e_1) { env_1.error = e_1; env_1.hasError = true; }
        //  finally { __disposeResources(env_1); }
        //
        // when `async` is `true`, produces:
        //
        //  try { <bodyStatements> }
        //  catch (e_1) { env_1.error = e_1; env_1.hasError = true; }
        //  finally { const result_1 = __disposeResources(env_1); if (result_1) { await result_1; } }

        // Unfortunately, it is necessary to use two properties to indicate an error because `throw undefined` is legal
        // JavaScript.
        var tryBlock = Factory().NewBlock(Factory().NewNodeList(bodyStatements.ToArray()), true /*multiLine*/);
        var bodyCatchBinding = Factory().NewUniqueName("e");
        var catchClause = Factory().AsNodeFactory().NewCatchClause(
            Factory().NewVariableDeclaration(
                bodyCatchBinding,
                null, /*exclamationToken*/
                null, /*type*/
                null  /*initializer*/),
            Factory().NewBlock(Factory().NewNodeList([
                Factory().NewExpressionStatement(
                    Factory().NewAssignmentExpression(
                        Factory().NewPropertyAccessExpression(envBinding, null, Factory().NewIdentifier("error"), NodeFlags.None),
                        bodyCatchBinding)),
                Factory().NewExpressionStatement(
                    Factory().NewAssignmentExpression(
                        Factory().NewPropertyAccessExpression(envBinding, null, Factory().NewIdentifier("hasError"), NodeFlags.None),
                        Factory().NewTrueExpression())),
            ]), true /*multiLine*/));

        BlockNode finallyBlock;
        if (async)
        {
            var result = Factory().NewUniqueName("result");
            finallyBlock = Factory().NewBlock(Factory().NewNodeList([
                Factory().NewVariableStatement(
                    null, /*modifiers*/
                    Factory().NewVariableDeclarationList(Factory().NewNodeList([
                        Factory().NewVariableDeclaration(
                            result,
                            null, /*exclamationToken*/
                            null, /*type*/
                            Factory().NewDisposeResourcesHelper(envBinding)),
                    ]), NodeFlags.Const)),
                Factory().AsNodeFactory().NewIfStatement(result, Factory().NewExpressionStatement(Factory().AsNodeFactory().NewAwaitExpression(result)), null /*elseStatement*/),
            ]), true /*multiLine*/);
        }
        else
        {
            finallyBlock = Factory().NewBlock(Factory().NewNodeList([
                Factory().NewExpressionStatement(
                    Factory().NewDisposeResourcesHelper(envBinding)),
            ]), true /*multiLine*/);
        }

        var tryStatement = Factory().AsNodeFactory().NewTryStatement(tryBlock, catchClause, finallyBlock);
        statements.Add(tryStatement);
        return statements;
    }

    // using.go:761-763
    private static bool IsUsingVariableDeclarationList(Node node)
    {
        return NodeTests.IsVariableDeclarationList(node) && GetUsingKindOfVariableDeclarationList(node.AsVariableDeclarationList()) != UsingKind.None;
    }

    // using.go:765-774
    private static UsingKind GetUsingKindOfVariableDeclarationList(VariableDeclarationList node)
    {
        switch (node.Flags & NodeFlags.BlockScoped)
        {
            case NodeFlags.AwaitUsing:
                return UsingKind.Async;
            case NodeFlags.Using:
                return UsingKind.Sync;
            default:
                return UsingKind.None;
        }
    }

    // using.go:776-778
    private static UsingKind GetUsingKindOfVariableStatement(VariableStatement node)
    {
        return GetUsingKindOfVariableDeclarationList(node.DeclarationList!.AsVariableDeclarationList());
    }

    // using.go:780-785
    private static UsingKind GetUsingKind(Node statement)
    {
        if (NodeTests.IsVariableStatement(statement))
        {
            return GetUsingKindOfVariableStatement(statement.AsVariableStatement());
        }
        return UsingKind.None;
    }

    // using.go:787-799
    private static UsingKind GetUsingKindOfStatements(Node[] statements)
    {
        var result = UsingKind.None;
        foreach (var statement in statements)
        {
            var usingKind = GetUsingKind(statement);
            if (usingKind == UsingKind.Async)
            {
                return UsingKind.Async;
            }
            if (usingKind > result)
            {
                result = usingKind;
            }
        }
        return result;
    }
}
