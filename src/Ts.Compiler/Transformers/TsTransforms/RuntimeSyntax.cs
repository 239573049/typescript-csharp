// Ported from tsgo internal/transformers/tstransforms/runtimesyntax.go.
// !!! SourceMaps and Comments need to be validated
// PORT-NOTE: Deferred branches are limited to checker-dependent identifier substitution inside
// enum/namespace bodies, computed enum constant evaluation without EmitResolver, destructuring
// namespace exports, internal import-equals aliases, and the nested try/super parameter-property
// insertion path. Explicit enum values, sequential numeric members, namespace IIFEs, simple
// namespace exports, and direct-super parameter properties are implemented below.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

/// <summary>Transforms TypeScript runtime syntax into JavaScript-compatible syntax.</summary>
public sealed class RuntimeSyntaxTransformer : global::Ts.Transformers.Transformer
{
    private readonly CompilerOptions compilerOptions;
    private readonly global::Ts.Printer.EmitResolver? emitResolver;
    private Node? parentNode;
    private Node? currentNode;
    private SourceFile? currentSourceFile;
    private Node? currentScope;
    private Dictionary<string, Node>? currentScopeFirstDeclarationsOfName;
    private EnumDeclaration? currentEnum;
    private ModuleDeclaration? currentNamespace;

    private RuntimeSyntaxTransformer(global::Ts.Transformers.TransformOptions options)
    {
        compilerOptions = options.CompilerOptions ?? new CompilerOptions();
        emitResolver = options.EmitResolver;
        NewTransformer(Visit, options.Context);
    }

    public static global::Ts.Transformers.Transformer NewRuntimeSyntaxTransformer(
        global::Ts.Transformers.TransformOptions options) => new RuntimeSyntaxTransformer(options);

    private Node? PushNode(Node node)
    {
        var grandparentNode = parentNode;
        parentNode = currentNode;
        currentNode = node;
        return grandparentNode;
    }

    private void PopNode(Node? grandparentNode)
    {
        currentNode = parentNode;
        parentNode = grandparentNode;
    }

    private (Node? Scope, Dictionary<string, Node>? Declarations) PushScope(Node node)
    {
        var saved = (currentScope, currentScopeFirstDeclarationsOfName);
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                currentScope = node;
                currentSourceFile = node.AsSourceFile();
                currentScopeFirstDeclarationsOfName = null;
                break;
            case SyntaxKind.CaseBlock:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.Block:
                currentScope = node;
                currentScopeFirstDeclarationsOfName = null;
                break;
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.VariableStatement:
                RecordDeclarationInScope(node);
                break;
        }
        return saved;
    }

    private void PopScope((Node? Scope, Dictionary<string, Node>? Declarations) saved)
    {
        if (currentScope != saved.Scope)
        {
            currentScopeFirstDeclarationsOfName = saved.Declarations;
        }
        currentScope = saved.Scope;
    }

    private Node? Visit(Node node)
    {
        var grandparentNode = PushNode(node);
        var savedScope = PushScope(node);
        try
        {
            switch (node.Kind)
            {
                // TypeScript parameter-property modifiers are elided.
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.PrivateKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.ReadonlyKeyword:
                case SyntaxKind.OverrideKeyword:
                    return null;
                case SyntaxKind.EnumDeclaration:
                    return VisitEnumDeclaration(node.AsEnumDeclaration());
                case SyntaxKind.ModuleDeclaration:
                    return VisitModuleDeclaration(node.AsModuleDeclaration());
                case SyntaxKind.ClassDeclaration:
                    return VisitClassDeclaration(node.AsClassDeclaration());
                case SyntaxKind.ClassExpression:
                    return VisitClassExpression(node.AsClassExpression());
                case SyntaxKind.Constructor:
                    return VisitConstructorDeclaration(node.AsConstructorDeclaration());
                case SyntaxKind.FunctionDeclaration:
                    return VisitFunctionDeclaration(node.AsFunctionDeclaration());
                case SyntaxKind.VariableStatement:
                    return VisitVariableStatement(node.AsVariableStatement());
                case SyntaxKind.ExportDeclaration:
                case SyntaxKind.ImportDeclaration:
                case SyntaxKind.ImportClause:
                    return currentNamespace is not null && currentScope?.Kind != SyntaxKind.Block
                        ? null
                        : Visitor().VisitEachChild(node);
                default:
                    return Visitor().VisitEachChild(node);
            }
        }
        finally
        {
            PopScope(savedScope);
            PopNode(grandparentNode);
        }
    }

    private void RecordDeclarationInScope(Node node)
    {
        if (node.Kind == SyntaxKind.VariableStatement)
        {
            RecordDeclarationInScope(node.AsVariableStatement().DeclarationList!);
            return;
        }
        if (node.Kind == SyntaxKind.VariableDeclarationList)
        {
            foreach (var declaration in node.AsVariableDeclarationList().Declarations?.Nodes ?? [])
            {
                RecordDeclarationInScope(declaration);
            }
            return;
        }
        if (node.Kind is SyntaxKind.ArrayBindingPattern or SyntaxKind.ObjectBindingPattern)
        {
            foreach (var element in node.Elements() ?? [])
            {
                RecordDeclarationInScope(element);
            }
            return;
        }
        if (node.Name() is { Kind: SyntaxKind.Identifier } name)
        {
            currentScopeFirstDeclarationsOfName ??= new Dictionary<string, Node>(StringComparer.Ordinal);
            currentScopeFirstDeclarationsOfName.TryAdd(name.Text(), node);
        }
    }

    private bool IsFirstDeclarationInScope(Node node)
    {
        if (node.Name() is not { Kind: SyntaxKind.Identifier } name ||
            currentScopeFirstDeclarationsOfName is null)
        {
            return false;
        }
        return currentScopeFirstDeclarationsOfName.TryGetValue(name.Text(), out var first) && first == node;
    }

    private bool IsExportOfNamespace(Node node) =>
        currentNamespace is not null && currentScope?.Kind != SyntaxKind.Block &&
        (node.ModifierFlags() & ModifierFlags.Export) != 0;

    private Expression GetExpressionForPropertyName(EnumMember member)
    {
        var name = member.Name()!;
        return name.Kind switch
        {
            SyntaxKind.PrivateIdentifier => Factory().NewIdentifier(""),
            SyntaxKind.ComputedPropertyName => Visitor().VisitNode(name.Expression())!,
            SyntaxKind.Identifier or SyntaxKind.StringLiteral =>
                Factory().NewStringLiteral(name.Text(), TokenFlags.None),
            SyntaxKind.NumericLiteral => Factory().NewNumericLiteral(name.Text(), TokenFlags.None),
            _ => name,
        };
    }

    private IdentifierNode GetNamespaceContainerName(Node node) =>
        Factory().NewGeneratedNameForNode(node);

    private Expression GetNamespaceQualifiedElement(IdentifierNode ns, Expression expression)
    {
        var qualifiedName = Factory().NewElementAccessExpression(ns, null, expression, NodeFlags.None);
        EmitContext().AssignCommentAndSourceMapRanges(qualifiedName, expression);
        return qualifiedName;
    }

    private Expression GetExportQualifiedReferenceToDeclaration(Node node)
    {
        if (IsExportOfNamespace(node))
        {
            return Factory().GetExternalModuleOrNamespaceExportName(
                GetNamespaceContainerName(currentNamespace!), node, false, true);
        }
        return Factory().GetDeclarationNameEx(node, new global::Ts.Printer.NameOptions { AllowSourceMaps = true });
    }

    private (List<Node> Statements, bool Added) AddVarForDeclaration(List<Node> statements, Node node)
    {
        RecordDeclarationInScope(node);
        // Synthetic tests may not have passed through a scope that recorded the declaration.
        if (!IsFirstDeclarationInScope(node) && currentScopeFirstDeclarationsOfName is not null)
        {
            return (statements, false);
        }

        var name = Factory().GetLocalNameEx(
            node, new global::Ts.Printer.AssignedNameOptions { AllowSourceMaps = true });
        var variable = Factory().NewVariableDeclaration(name, null, null, null);
        var flags = currentScope?.AsNode() == currentSourceFile?.AsNode() ? NodeFlags.None : NodeFlags.Let;
        var declarations = Factory().NewVariableDeclarationList(Factory().NewNodeList([variable]), flags);
        var modifierMask = ~(ModifierFlags.TypeScriptModifier | ModifierFlags.Decorator);
        if (currentNamespace is not null)
        {
            modifierMask &= ~ModifierFlags.Export;
        }
        var modifiers = global::Ts.Transformers.Transformers.ExtractModifiers(
            EmitContext(), node.Modifiers(), modifierMask);
        var statement = Factory().NewVariableStatement(modifiers, declarations);
        EmitContext().SetOriginal(variable, node);
        EmitContext().SetOriginal(statement, node);
        statements.Add(statement);
        return (statements, true);
    }

    private Node VisitEnumDeclaration(EnumDeclaration node)
    {
        if (!ShouldEmitEnumDeclaration(node))
        {
            return EmitContext().NewNotEmittedStatement(node.AsNode());
        }

        var (statements, _) = AddVarForDeclaration([], node.AsNode());
        var enumReference = GetExportQualifiedReferenceToDeclaration(node.AsNode());
        var enumArgument = Factory().NewLogicalORExpression(
            enumReference,
            Factory().NewAssignmentExpression(
                GetExportQualifiedReferenceToDeclaration(node.AsNode()),
                Factory().NewObjectLiteralExpression(Factory().NewNodeList([]), false)));

        var enumParameterName = GetNamespaceContainerName(node.AsNode());
        var enumParameter = Factory().NewParameterDeclaration(null, null, enumParameterName, null, null, null);
        var enumBody = TransformEnumBody(node);
        var enumFunction = Factory().NewFunctionExpression(
            null, null, null, null, Factory().NewNodeList([enumParameter]), null, null, enumBody);
        var enumCall = Factory().NewCallExpression(
            Factory().NewParenthesizedExpression(enumFunction), null, null,
            Factory().NewNodeList([enumArgument]), NodeFlags.None);
        var enumStatement = Factory().NewExpressionStatement(enumCall);
        EmitContext().SetOriginal(enumStatement, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(enumStatement, node.AsNode());
        statements.Add(enumStatement);
        return Factory().AsNodeFactory().NewSyntaxList(statements.ToArray());
    }

    private BlockNode TransformEnumBody(EnumDeclaration node)
    {
        var savedCurrentEnum = currentEnum;
        currentEnum = node;
        try
        {
            var statements = new List<Node>();
            double nextNumericValue = 0;
            foreach (var memberNode in node.Members?.Nodes ?? [])
            {
                TransformEnumMember(statements, node, memberNode.AsEnumMember(), ref nextNumericValue);
            }
            var statementList = Factory().NewNodeList(statements.ToArray());
            statementList.Loc = node.Members?.Loc ?? node.Loc;
            return Factory().NewBlock(statementList, true);
        }
        finally
        {
            currentEnum = savedCurrentEnum;
        }
    }

    private void TransformEnumMember(
        List<Node> statements,
        EnumDeclaration enumDeclaration,
        EnumMember member,
        ref double nextNumericValue)
    {
        var initializer = Visitor().VisitNode(member.Initializer);
        var useReverseMapping = true;

        if (emitResolver is not null && EmitContext().ParseNode(member.AsNode()) is { } parseNode)
        {
            var result = emitResolver.GetEnumMemberValue(parseNode);
            initializer = result.Value switch
            {
                global::Ts.JsNum.Number number => TsTransformUtilities.ConstantExpression(number, Factory()),
                string text => TsTransformUtilities.ConstantExpression(text, Factory()),
                _ => initializer,
            } ?? initializer;
            useReverseMapping = result.Value is global::Ts.JsNum.Number || !result.IsSyntacticallyString;
        }
        else if (TryGetSyntacticEnumValue(member.Initializer, ref nextNumericValue, out var value, out var isString))
        {
            initializer = TsTransformUtilities.ConstantExpression(value, Factory());
            useReverseMapping = !isString;
        }
        else
        {
            initializer ??= Factory().NewVoidZeroExpression();
            useReverseMapping = member.Initializer?.Kind != SyntaxKind.StringLiteral;
        }

        var memberAccess = GetNamespaceQualifiedElement(
            GetNamespaceContainerName(enumDeclaration.AsNode()),
            GetExpressionForPropertyName(member));
        Expression expression = Factory().NewAssignmentExpression(memberAccess, initializer!);
        if (useReverseMapping)
        {
            expression = Factory().NewAssignmentExpression(
                Factory().NewElementAccessExpression(
                    GetNamespaceContainerName(enumDeclaration.AsNode()), null, expression, NodeFlags.None),
                GetExpressionForPropertyName(member));
        }
        var statement = Factory().NewExpressionStatement(expression);
        EmitContext().AssignCommentAndSourceMapRanges(expression, member.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(statement, member.AsNode());
        statements.Add(statement);
    }

    private static bool TryGetSyntacticEnumValue(
        Node? initializer,
        ref double nextNumericValue,
        out object value,
        out bool isString)
    {
        isString = false;
        if (initializer is null)
        {
            value = nextNumericValue;
            nextNumericValue++;
            return true;
        }
        if (initializer.Kind == SyntaxKind.StringLiteral)
        {
            value = initializer.Text();
            isString = true;
            return true;
        }
        if (initializer.Kind == SyntaxKind.NumericLiteral &&
            double.TryParse(initializer.Text(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            value = number;
            nextNumericValue = number + 1;
            return true;
        }
        if (initializer.Kind == SyntaxKind.PrefixUnaryExpression &&
            initializer.AsPrefixUnaryExpression().Operand is { Kind: SyntaxKind.NumericLiteral } operand &&
            double.TryParse(operand.Text(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            number = initializer.AsPrefixUnaryExpression().Operator == SyntaxKind.MinusToken ? -number : number;
            value = number;
            nextNumericValue = number + 1;
            return true;
        }
        value = 0d;
        return false;
    }

    private Node VisitModuleDeclaration(ModuleDeclaration node)
    {
        if (!ShouldEmitModuleDeclaration(node))
        {
            return EmitContext().NewNotEmittedStatement(node.AsNode());
        }

        var (statements, _) = AddVarForDeclaration([], node.AsNode());
        var moduleReference = GetExportQualifiedReferenceToDeclaration(node.AsNode());
        var moduleArgument = Factory().NewLogicalORExpression(
            moduleReference,
            Factory().NewAssignmentExpression(
                GetExportQualifiedReferenceToDeclaration(node.AsNode()),
                Factory().NewObjectLiteralExpression(Factory().NewNodeList([]), false)));
        var moduleParameterName = GetNamespaceContainerName(node.AsNode());
        var moduleParameter = Factory().NewParameterDeclaration(null, null, moduleParameterName, null, null, null);
        var moduleBody = TransformModuleBody(node);
        var moduleFunction = Factory().NewFunctionExpression(
            null, null, null, null, Factory().NewNodeList([moduleParameter]), null, null, moduleBody);
        var moduleCall = Factory().NewCallExpression(
            Factory().NewParenthesizedExpression(moduleFunction), null, null,
            Factory().NewNodeList([moduleArgument]), NodeFlags.None);
        var moduleStatement = Factory().NewExpressionStatement(moduleCall);
        EmitContext().SetOriginal(moduleStatement, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(moduleStatement, node.AsNode());
        statements.Add(moduleStatement);
        return Factory().AsNodeFactory().NewSyntaxList(statements.ToArray());
    }

    private BlockNode TransformModuleBody(ModuleDeclaration node)
    {
        var savedNamespace = currentNamespace;
        var savedScope = currentScope;
        var savedDeclarations = currentScopeFirstDeclarationsOfName;
        currentNamespace = node;
        currentScopeFirstDeclarationsOfName = null;
        try
        {
            Node[] statements = [];
            var statementLocation = node.Loc;
            if (node.Body is { Kind: SyntaxKind.ModuleBlock } bodyNode)
            {
                currentScope = bodyNode;
                var body = bodyNode.AsModuleBlock();
                var (visited, _) = Visitor().VisitSlice(body.Statements?.Nodes ?? []);
                statements = visited ?? [];
                statementLocation = body.Statements?.Loc ?? body.Loc;
            }
            else if (node.Body is { Kind: SyntaxKind.ModuleDeclaration } nested)
            {
                var (visited, _) = Visitor().VisitSlice([nested]);
                statements = visited ?? [];
            }
            var statementList = Factory().NewNodeList(statements);
            statementList.Loc = statementLocation;
            return Factory().NewBlock(statementList, true);
        }
        finally
        {
            currentNamespace = savedNamespace;
            currentScope = savedScope;
            currentScopeFirstDeclarationsOfName = savedDeclarations;
        }
    }

    private Node VisitClassDeclaration(ClassDeclaration node)
    {
        var exported = IsExportOfNamespace(node.AsNode());
        var constructor = FindConstructor(node.Members?.Nodes);
        var parameterProperties = GetParameterProperties(constructor);
        var members = Visitor().VisitNodes(node.Members);
        members = AddParameterPropertyDeclarations(members, parameterProperties, node.Members?.Loc);
        var modifiers = exported
            ? Visitor().VisitModifiers(global::Ts.Transformers.Transformers.ExtractModifiers(
                EmitContext(), node.Modifiers(), ~ModifierFlags.ExportDefault))
            : Visitor().VisitModifiers(node.Modifiers());
        var updated = Factory().AsNodeFactory().UpdateClassDeclaration(
            node,
            modifiers,
            Visitor().VisitNode(node.Name()),
            null,
            Visitor().VisitNodes(node.HeritageClauses),
            members);
        if (!exported)
        {
            return updated;
        }
        var export = CreateExportStatementForDeclaration(node.AsNode());
        return Factory().AsNodeFactory().NewSyntaxList([updated, export]);
    }

    private Node VisitClassExpression(ClassExpression node)
    {
        var constructor = FindConstructor(node.Members?.Nodes);
        var parameterProperties = GetParameterProperties(constructor);
        var members = Visitor().VisitNodes(node.Members);
        members = AddParameterPropertyDeclarations(members, parameterProperties, node.Members?.Loc);
        return Factory().AsNodeFactory().UpdateClassExpression(
            node,
            Visitor().VisitModifiers(node.Modifiers()),
            Visitor().VisitNode(node.Name()),
            null,
            Visitor().VisitNodes(node.HeritageClauses),
            members);
    }

    private static ConstructorDeclaration? FindConstructor(Node[]? members)
    {
        foreach (var member in members ?? [])
        {
            if (member.Kind == SyntaxKind.Constructor)
            {
                return member.AsConstructorDeclaration();
            }
        }
        return null;
    }

    private static List<ParameterDeclaration> GetParameterProperties(ConstructorDeclaration? constructor)
    {
        var result = new List<ParameterDeclaration>();
        if (constructor is null)
        {
            return result;
        }
        foreach (var parameter in constructor.Parameters?.Nodes ?? [])
        {
            if (global::Ts.Ast.AstUtilities.IsParameterPropertyDeclaration(parameter, constructor.AsNode()))
            {
                result.Add(parameter.AsParameterDeclaration());
            }
        }
        return result;
    }

    private NodeList? AddParameterPropertyDeclarations(
        NodeList? members,
        List<ParameterDeclaration> parameterProperties,
        TextRange? location)
    {
        var additions = new List<Node>();
        foreach (var parameter in parameterProperties)
        {
            if (parameter.Name() is { Kind: SyntaxKind.Identifier } name)
            {
                var property = Factory().AsNodeFactory().NewPropertyDeclaration(
                    null, name.Clone(Factory()), null, null, null);
                EmitContext().SetOriginal(property, parameter.AsNode());
                additions.Add(property);
            }
        }
        if (additions.Count == 0)
        {
            return members;
        }
        additions.AddRange(members?.Nodes ?? []);
        var result = Factory().NewNodeList(additions.ToArray());
        if (location is { } loc)
        {
            result.Loc = loc;
        }
        return result;
    }

    private Node VisitConstructorDeclaration(ConstructorDeclaration node)
    {
        var parameters = Visitor().VisitNodes(node.Parameters);
        var body = node.Body is null ? null : VisitConstructorBody(node.Body.AsBlock(), node);
        return Factory().AsNodeFactory().UpdateConstructorDeclaration(
            node, Visitor().VisitModifiers(node.Modifiers()), null, parameters, null, null, body);
    }

    private Node? VisitVariableStatement(VariableStatement node)
    {
        if (!IsExportOfNamespace(node.AsNode()))
        {
            return Visitor().VisitEachChild(node.AsNode());
        }

        var expressions = new List<Expression>();
        foreach (var declarationNode in node.DeclarationList?.AsVariableDeclarationList().Declarations?.Nodes ?? [])
        {
            var declaration = declarationNode.AsVariableDeclaration();
            if (declaration.Initializer is null || declaration.Name() is not { Kind: SyntaxKind.Identifier } name)
            {
                // PORT-NOTE: Namespace-export destructuring uses FlattenDestructuringAssignment in
                // tsgo and remains in the deferred branch documented at the file head.
                continue;
            }
            var target = Factory().GetNamespaceMemberName(
                GetNamespaceContainerName(currentNamespace!),
                name,
                new global::Ts.Printer.NameOptions { AllowSourceMaps = true });
            expressions.Add(Factory().NewAssignmentExpression(
                target,
                Visitor().VisitNode(declaration.Initializer)!));
        }
        if (expressions.Count == 0)
        {
            return null;
        }
        var expression = expressions.Count == 1
            ? expressions[0]
            : Factory().InlineExpressions(expressions.ToArray());
        var statement = Factory().NewExpressionStatement(expression);
        EmitContext().SetOriginal(statement, node.AsNode());
        EmitContext().AssignCommentAndSourceMapRanges(statement, node.AsNode());
        return statement;
    }

    private Node VisitFunctionDeclaration(FunctionDeclaration node)
    {
        if (!IsExportOfNamespace(node.AsNode()))
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }
        var updated = Factory().AsNodeFactory().UpdateFunctionDeclaration(
            node,
            Visitor().VisitModifiers(global::Ts.Transformers.Transformers.ExtractModifiers(
                EmitContext(), node.Modifiers(), ~ModifierFlags.Export)),
            node.AsteriskToken,
            Visitor().VisitNode(node.Name()),
            null,
            Visitor().VisitNodes(node.Parameters),
            null,
            null,
            Visitor().VisitNode(node.Body));
        return Factory().AsNodeFactory().NewSyntaxList(
            [updated, CreateExportStatementForDeclaration(node.AsNode())]);
    }

    private Node CreateExportStatementForDeclaration(Node node)
    {
        var exportName = Factory().GetExternalModuleOrNamespaceExportName(
            GetNamespaceContainerName(currentNamespace!), node, false, true);
        var localName = Factory().GetLocalName(node);
        var statement = Factory().NewExpressionStatement(
            Factory().NewAssignmentExpression(exportName, localName));
        EmitContext().SetOriginal(statement, node);
        return statement;
    }

    private BlockNode VisitConstructorBody(Block body, ConstructorDeclaration constructor)
    {
        var parameterProperties = GetParameterProperties(constructor);
        if (parameterProperties.Count == 0)
        {
            return Visitor().VisitEachChild(body.AsNode())!.AsBlock();
        }

        var assignments = new List<Node>();
        foreach (var parameter in parameterProperties)
        {
            if (parameter.Name() is not { Kind: SyntaxKind.Identifier } name)
            {
                continue;
            }
            var propertyName = name.Clone(Factory())!;
            var localName = name.Clone(Factory())!;
            var assignment = Factory().NewExpressionStatement(
                Factory().NewAssignmentExpression(
                    Factory().NewPropertyAccessExpression(
                        Factory().NewThisExpression(), null, propertyName, NodeFlags.None),
                    localName));
            EmitContext().SetOriginal(assignment, parameter.AsNode());
            assignments.Add(assignment);
        }

        var (visited, _) = Visitor().VisitSlice(body.Statements?.Nodes ?? []);
        var existing = new List<Node>(visited ?? []);
        var insertionIndex = FindDirectSuperStatementIndex(existing);
        existing.InsertRange(insertionIndex, assignments);
        var statementList = Factory().NewNodeList(existing.ToArray());
        statementList.Loc = body.Statements?.Loc ?? body.Loc;
        var updated = Factory().NewBlock(statementList, true);
        EmitContext().SetOriginal(updated, body.AsNode());
        updated.Loc = body.Loc;
        return updated;
    }

    private static int FindDirectSuperStatementIndex(List<Node> statements)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i].Kind == SyntaxKind.ExpressionStatement &&
                global::Ts.Transformers.TransformerAstAdapters.IsSuperCall(statements[i].Expression()!))
            {
                return i + 1;
            }
        }
        return 0;
    }

    private bool ShouldEmitEnumDeclaration(EnumDeclaration node) =>
        !global::Ts.Ast.AstUtilities.IsEnumConst(node.AsNode()) || compilerOptions.ShouldPreserveConstEnums();

    private bool ShouldEmitModuleDeclaration(ModuleDeclaration node)
    {
        var parseNode = EmitContext().ParseNode(node.AsNode());
        return parseNode is null ||
            global::Ts.Ast.AstUtilities.IsInstantiatedModule(
                parseNode, compilerOptions.ShouldPreserveConstEnums());
    }
}
