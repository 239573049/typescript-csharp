// Ported from tsgo internal/transformers/destructuring.go.

using System;
using System.Collections.Generic;
using System.Globalization;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers;

// FlattenLevel controls how deeply binding/assignment patterns are decomposed.
public enum FlattenLevel
{
    All, // Fully decompose all patterns into individual assignments/bindings
    ObjectRest, // Only decompose patterns containing object rest elements
}

// CreateAssignmentCallback is a callback used to create custom assignment expressions during destructuring flattening.
// When provided, the target will always be an Identifier, and the callback can wrap the assignment with additional logic
// (e.g. export expressions in CJS modules or namespace member assignments).
public delegate Expression CreateAssignmentCallback(IdentifierNode name, Expression value, TextRange location);

public static partial class Transformers
{
    // FlattenDestructuringAssignment flattens a destructuring assignment expression into a sequence of
    // individual property/element access assignments. Supports custom assignment callbacks for module
    // export or namespace member expressions.
    public static Expression FlattenDestructuringAssignment(
        Transformer transformer,
        Node node,
        bool needsValue,
        FlattenLevel level,
        CreateAssignmentCallback? createAssignmentCallback)
    {
        var flattener = NewFlattener(transformer, level);
        flattener.createAssignmentCallback = createAssignmentCallback;
        flattener.hoistTempVariables = true;
        flattener.UseAssignmentMode();
        return flattener.FlattenDestructuringAssignment(node, needsValue);
    }

    // FlattenDestructuringBinding flattens a binding pattern in a variable declaration or parameter
    // into individual variable declarations. Returns a single VariableDeclaration, a SyntaxList of
    // declarations, or null.
    public static Node? FlattenDestructuringBinding(
        Transformer transformer,
        Node node,
        Node? rval,
        FlattenLevel level,
        bool hoistTempVariables,
        bool skipInitializer)
    {
        var flattener = NewFlattener(transformer, level);
        flattener.hoistTempVariables = hoistTempVariables;
        flattener.UseBindingMode();
        return flattener.FlattenDestructuringBinding(node, rval, skipInitializer);
    }

    private sealed class PendingDecl
    {
        public readonly List<Node> PendingExpressions = [];
        public required Node Name;
        public required Node Value;
        public TextRange Location;
        public Node? Original;
    }

    private static Flattener NewFlattener(Transformer transformer, FlattenLevel level) => new(transformer, level);

    // Flattener encapsulates the state and logic for flattening destructuring patterns.
    // It is equivalent to TypeScript's FlattenContext in destructuring.ts.
    private sealed class Flattener
    {
        private readonly Transformer transformer;
        private readonly FlattenLevel level;
        internal CreateAssignmentCallback? createAssignmentCallback;
        private readonly List<Node> expressions = [];
        private readonly List<PendingDecl> declarations = [];
        private bool hasTransformedPriorElement;
        internal bool hoistTempVariables;

        private Action<Node, Node, TextRange, Node?> emitBindingOrAssignment = null!;
        private Func<Node[], Node> createArrayBindingOrAssignmentPattern = null!;
        private Func<Node[], Node> createObjectBindingOrAssignmentPattern = null!;
        private Func<Node, Node> createArrayBindingOrAssignmentElement = null!;

        public Flattener(Transformer transformer, FlattenLevel level)
        {
            this.transformer = transformer;
            this.level = level;
        }

        public void UseAssignmentMode()
        {
            emitBindingOrAssignment = EmitAssignment;
            createArrayBindingOrAssignmentPattern = CreateArrayAssignmentPattern;
            createObjectBindingOrAssignmentPattern = CreateObjectAssignmentPattern;
            createArrayBindingOrAssignmentElement = CreateArrayAssignmentElement;
        }

        public void UseBindingMode()
        {
            emitBindingOrAssignment = EmitBinding;
            createArrayBindingOrAssignmentPattern = CreateArrayBindingPattern;
            createObjectBindingOrAssignmentPattern = CreateObjectBindingPattern;
            createArrayBindingOrAssignmentElement = CreateArrayBindingElement;
        }

        // --- Assignment mode callbacks ---

        private Node CreateArrayAssignmentPattern(Node[] elements) =>
            transformer.Factory().NewArrayLiteralExpression(transformer.Factory().NewNodeList(elements), false);

        private Node CreateObjectAssignmentPattern(Node[] elements) =>
            transformer.Factory().NewObjectLiteralExpression(transformer.Factory().NewNodeList(elements), false);

        private static Node CreateArrayAssignmentElement(Node expression) => expression;

        private void EmitAssignment(Node target, Node value, TextRange location, Node? original)
        {
            Expression expression;
            if (createAssignmentCallback is not null && global::Ts.Ast.NodeTests.IsIdentifier(target))
            {
                expression = createAssignmentCallback(target, value, location);
            }
            else
            {
                expression = transformer.Factory().NewAssignmentExpression(transformer.Visitor().VisitNode(target)!, value);
                expression.Loc = location;
            }
            if (original is not null)
            {
                transformer.EmitContext().SetOriginal(expression, original);
            }
            EmitExpression(expression);
        }

        // --- Binding mode callbacks ---

        private Node CreateArrayBindingPattern(Node[] elements) =>
            transformer.Factory().NewBindingPattern(SyntaxKind.ArrayBindingPattern, transformer.Factory().NewNodeList(elements));

        private Node CreateObjectBindingPattern(Node[] elements) =>
            transformer.Factory().NewBindingPattern(SyntaxKind.ObjectBindingPattern, transformer.Factory().NewNodeList(elements));

        private Node CreateArrayBindingElement(Node expression) =>
            transformer.Factory().NewBindingElement(null, null, expression, null);

        private void EmitBinding(Node target, Node value, TextRange location, Node? original)
        {
            if (expressions.Count > 0)
            {
                var pending = new Node[expressions.Count + 1];
                expressions.CopyTo(pending, 0);
                pending[^1] = value;
                value = transformer.Factory().InlineExpressions(pending)!;
                expressions.Clear();
            }
            declarations.Add(new PendingDecl
            {
                Name = target,
                Value = value,
                Location = location,
                Original = original,
            });
        }

        // --- Shared helpers ---

        private void EmitExpression(Node expression) => expressions.Add(expression);

        private Node EnsureIdentifier(Node value, bool reuseIdentifierExpressions, TextRange location)
        {
            if (reuseIdentifierExpressions && global::Ts.Ast.NodeTests.IsIdentifier(value))
            {
                return value;
            }
            var temp = transformer.Factory().NewTempVariable();
            if (hoistTempVariables)
            {
                transformer.EmitContext().AddVariableDeclaration(temp);
                var assignment = transformer.Factory().NewAssignmentExpression(temp, value);
                assignment.Loc = location;
                EmitExpression(assignment);
            }
            else
            {
                emitBindingOrAssignment(temp, value, location, null);
            }
            return temp;
        }

        private Node CreateDefaultValueCheck(Expression value, Expression defaultValue, TextRange location)
        {
            value = EnsureIdentifier(value, true, location);
            return transformer.Factory().NewConditionalExpression(
                transformer.Factory().NewTypeCheck(value, "undefined"),
                transformer.Factory().NewToken(SyntaxKind.QuestionToken),
                defaultValue,
                transformer.Factory().NewToken(SyntaxKind.ColonToken),
                value);
        }

        private Node CreateDestructuringPropertyAccess(Node value, Node propertyName)
        {
            if (global::Ts.Ast.NodeTests.IsComputedPropertyName(propertyName))
            {
                var argumentExpression = EnsureIdentifier(
                    transformer.Visitor().VisitNode(propertyName.Expression())!,
                    false,
                    propertyName.Loc);
                return transformer.Factory().NewElementAccessExpression(value, null, argumentExpression, NodeFlags.None);
            }
            if (global::Ts.Ast.AstUtilities.IsStringOrNumericLiteralLike(propertyName) ||
                global::Ts.Ast.NodeTests.IsBigIntLiteral(propertyName))
            {
                var argumentExpression = propertyName.Clone(transformer.Factory())!;
                return transformer.Factory().NewElementAccessExpression(value, null, argumentExpression, NodeFlags.None);
            }
            var name = transformer.Factory().NewIdentifier(propertyName.Text());
            return transformer.Factory().NewPropertyAccessExpression(value, null, name, NodeFlags.None);
        }

        // --- Entry points ---

        public Expression FlattenDestructuringAssignment(Node node, bool needsValue)
        {
            var location = node.Loc;
            Node? value = null;
            if (global::Ts.Ast.AstUtilities.IsDestructuringAssignment(node))
            {
                value = node.AsBinaryExpression().Right;
                while (global::Ts.Ast.AstUtilities.IsEmptyArrayLiteral(node.AsBinaryExpression().Left!) ||
                    TransformerAstAdapters.IsEmptyObjectLiteral(node.AsBinaryExpression().Left!))
                {
                    if (global::Ts.Ast.AstUtilities.IsDestructuringAssignment(value!))
                    {
                        node = value!;
                        location = node.Loc;
                        value = node.AsBinaryExpression().Right;
                    }
                    else
                    {
                        return transformer.Visitor().VisitNode(value)!;
                    }
                }
            }

            if (value is not null)
            {
                value = transformer.Visitor().VisitNode(value)!;
                if ((global::Ts.Ast.NodeTests.IsIdentifier(value) && BindingOrAssignmentElementAssignsToName(node, value.Text())) ||
                    BindingOrAssignmentElementContainsNonLiteralComputedName(node))
                {
                    value = EnsureIdentifier(value, false, location);
                }
                else if (needsValue)
                {
                    value = EnsureIdentifier(value, true, location);
                }
                else if (global::Ts.Ast.AstUtilities.NodeIsSynthesized(node))
                {
                    location = value.Loc;
                }
            }

            FlattenBindingOrAssignmentElement(node, value, location, global::Ts.Ast.AstUtilities.IsDestructuringAssignment(node));

            if (value is not null && needsValue)
            {
                if (expressions.Count == 0)
                {
                    return value;
                }
                expressions.Add(value);
            }

            return transformer.Factory().InlineExpressions(expressions.ToArray()) ?? transformer.Factory().NewOmittedExpression();
        }

        public Node? FlattenDestructuringBinding(Node node, Node? rval, bool skipInitializer)
        {
            if (global::Ts.Ast.NodeTests.IsVariableDeclaration(node))
            {
                var initializer = GetInitializerOfBindingOrAssignmentElement(node);
                if (initializer is not null &&
                    ((global::Ts.Ast.NodeTests.IsIdentifier(initializer) && BindingOrAssignmentElementAssignsToName(node, initializer.Text())) ||
                    BindingOrAssignmentElementContainsNonLiteralComputedName(node)))
                {
                    initializer = EnsureIdentifier(transformer.Visitor().VisitNode(initializer)!, false, initializer.Loc);
                    node = transformer.Factory().UpdateVariableDeclaration(node.AsVariableDeclaration(), node.Name(), null, null, initializer);
                }
            }

            FlattenBindingOrAssignmentElement(node, rval, node.Loc, skipInitializer);

            if (expressions.Count > 0)
            {
                var temp = transformer.Factory().NewTempVariable();
                if (hoistTempVariables)
                {
                    var value = transformer.Factory().InlineExpressions(expressions.ToArray())!;
                    expressions.Clear();
                    emitBindingOrAssignment(temp, value, default, null);
                }
                else
                {
                    transformer.EmitContext().AddVariableDeclaration(temp);
                    var last = declarations[^1];
                    last.PendingExpressions.Add(transformer.Factory().NewAssignmentExpression(temp, last.Value));
                    last.PendingExpressions.AddRange(expressions);
                    last.Value = temp;
                }
            }

            var declarationsOutput = new List<Node>(declarations.Count);
            foreach (var pending in declarations)
            {
                var expression = pending.Value;
                if (pending.PendingExpressions.Count > 0)
                {
                    var pendingExpressions = new Node[pending.PendingExpressions.Count + 1];
                    pending.PendingExpressions.CopyTo(pendingExpressions, 0);
                    pendingExpressions[^1] = pending.Value;
                    expression = transformer.Factory().InlineExpressions(pendingExpressions)!;
                }
                var declaration = transformer.Factory().NewVariableDeclaration(pending.Name, null, null, expression);
                declaration.Loc = pending.Location;
                if (pending.Original is not null)
                {
                    transformer.EmitContext().SetOriginal(declaration, pending.Original);
                }
                declarationsOutput.Add(declaration);
            }

            return declarationsOutput.Count switch
            {
                0 => null,
                1 => declarationsOutput[0],
                _ => transformer.Factory().NewSyntaxList(declarationsOutput.ToArray()),
            };
        }

        // --- Core flattening ---

        private void FlattenBindingOrAssignmentElement(Node element, Node? value, TextRange location, bool skipInitializer)
        {
            var bindingTarget = TransformerAstAdapters.GetTargetOfBindingOrAssignmentElement(element);
            if (bindingTarget is null)
            {
                return;
            }
            if (!skipInitializer)
            {
                var initializer = transformer.Visitor().VisitNode(GetInitializerOfBindingOrAssignmentElement(element));
                if (initializer is not null)
                {
                    if (value is not null)
                    {
                        value = CreateDefaultValueCheck(value, initializer, location);
                        if (!IsSimpleCopiableExpression(initializer) &&
                            (global::Ts.Ast.AstUtilities.IsBindingPattern(bindingTarget) || TransformerAstAdapters.IsAssignmentPattern(bindingTarget)))
                        {
                            value = EnsureIdentifier(value, true, location);
                        }
                    }
                    else
                    {
                        value = initializer;
                    }
                }
                else if (value is null)
                {
                    value = transformer.Factory().NewVoidZeroExpression();
                }
            }

            if (IsObjectBindingOrAssignmentPattern(bindingTarget))
            {
                FlattenObjectBindingOrAssignmentPattern(element, bindingTarget, value!, location);
            }
            else if (IsArrayBindingOrAssignmentPattern(bindingTarget))
            {
                FlattenArrayBindingOrAssignmentPattern(element, bindingTarget, value!, location);
            }
            else
            {
                emitBindingOrAssignment(bindingTarget, value!, location, element);
            }
        }

        private void FlattenObjectBindingOrAssignmentPattern(Node parent, Node pattern, Node value, TextRange location)
        {
            var elements = TransformerAstAdapters.GetElementsOfBindingOrAssignmentPattern(pattern);
            var numElements = elements.Length;
            if (numElements != 1)
            {
                var reuseIdentifierExpressions = !TransformerAstAdapters.IsDeclarationBindingElement(parent) || numElements != 0;
                value = EnsureIdentifier(value, reuseIdentifierExpressions, location);
            }
            List<Node> bindingElements = [];
            List<Node> computedTempVariables = [];
            for (var i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (TransformerAstAdapters.GetRestIndicatorOfBindingOrAssignmentElement(element) is null)
                {
                    var propertyName = TransformerAstAdapters.TryGetPropertyNameOfBindingOrAssignmentElement(element)!;
                    if (level >= FlattenLevel.ObjectRest &&
                        (element.SubtreeFacts() & (SubtreeFacts.SubtreeContainsRestOrSpread | SubtreeFacts.SubtreeContainsObjectRestOrSpread)) == 0 &&
                        (TransformerAstAdapters.GetTargetOfBindingOrAssignmentElement(element)!.SubtreeFacts() &
                            (SubtreeFacts.SubtreeContainsRestOrSpread | SubtreeFacts.SubtreeContainsObjectRestOrSpread)) == 0 &&
                        !global::Ts.Ast.NodeTests.IsComputedPropertyName(propertyName))
                    {
                        bindingElements.Add(transformer.Visitor().VisitNode(element)!);
                    }
                    else
                    {
                        if (bindingElements.Count > 0)
                        {
                            emitBindingOrAssignment(createObjectBindingOrAssignmentPattern(bindingElements.ToArray()), value, location, pattern);
                            bindingElements.Clear();
                        }
                        var rhsValue = CreateDestructuringPropertyAccess(value, propertyName);
                        if (global::Ts.Ast.NodeTests.IsComputedPropertyName(propertyName))
                        {
                            computedTempVariables.Add(rhsValue.AsElementAccessExpression().ArgumentExpression!);
                        }
                        FlattenBindingOrAssignmentElement(element, rhsValue, element.Loc, false);
                    }
                }
                else if (i == numElements - 1)
                {
                    if (bindingElements.Count > 0)
                    {
                        emitBindingOrAssignment(createObjectBindingOrAssignmentPattern(bindingElements.ToArray()), value, location, pattern);
                        bindingElements.Clear();
                    }
                    var rhsValue = transformer.Factory().NewRestHelper(value, elements, computedTempVariables.ToArray(), pattern.Loc);
                    FlattenBindingOrAssignmentElement(element, rhsValue, element.Loc, false);
                }
            }
            if (bindingElements.Count > 0)
            {
                emitBindingOrAssignment(createObjectBindingOrAssignmentPattern(bindingElements.ToArray()), value, location, pattern);
            }
        }

        private readonly record struct RestIdElemPair(Node Id, Node Element);

        private void FlattenArrayBindingOrAssignmentPattern(Node parent, Node pattern, Node value, TextRange location)
        {
            var elements = TransformerAstAdapters.GetElementsOfBindingOrAssignmentPattern(pattern);
            var numElements = elements.Length;
            if (((numElements != 1 && (level < FlattenLevel.ObjectRest || numElements == 0)) ||
                global::Ts.Core.CoreUtil.Every(elements, global::Ts.Ast.NodeTests.IsOmittedExpression)))
            {
                var reuseIdentifierExpressions = !TransformerAstAdapters.IsDeclarationBindingElement(parent) || numElements != 0;
                value = EnsureIdentifier(value, reuseIdentifierExpressions, location);
            }
            List<Node> bindingElements = [];
            List<RestIdElemPair> restContainingElements = [];
            for (var i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (level >= FlattenLevel.ObjectRest)
                {
                    if ((element.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0 ||
                        (hasTransformedPriorElement && !IsSimpleBindingOrAssignmentElement(element)))
                    {
                        hasTransformedPriorElement = true;
                        var temp = transformer.Factory().NewTempVariable();
                        if (hoistTempVariables)
                        {
                            transformer.EmitContext().AddVariableDeclaration(temp);
                        }
                        restContainingElements.Add(new RestIdElemPair(temp, element));
                        bindingElements.Add(createArrayBindingOrAssignmentElement(temp));
                    }
                    else
                    {
                        bindingElements.Add(element);
                    }
                }
                else if (global::Ts.Ast.NodeTests.IsOmittedExpression(element))
                {
                    continue;
                }
                else if (TransformerAstAdapters.GetRestIndicatorOfBindingOrAssignmentElement(element) is null)
                {
                    var rhsValue = transformer.Factory().NewElementAccessExpression(
                        value,
                        null,
                        transformer.Factory().NewNumericLiteral(i.ToString(CultureInfo.InvariantCulture), TokenFlags.None),
                        NodeFlags.None);
                    FlattenBindingOrAssignmentElement(element, rhsValue, element.Loc, false);
                }
                else if (i == numElements - 1)
                {
                    var rhsValue = transformer.Factory().NewArraySliceCall(value, i);
                    FlattenBindingOrAssignmentElement(element, rhsValue, element.Loc, false);
                }
            }
            if (bindingElements.Count > 0)
            {
                emitBindingOrAssignment(createArrayBindingOrAssignmentPattern(bindingElements.ToArray()), value, location, pattern);
            }
            foreach (var pair in restContainingElements)
            {
                FlattenBindingOrAssignmentElement(pair.Element, pair.Id, pair.Element.Loc, false);
            }
        }
    }

    // BindingOrAssignmentElementAssignsToName checks if any target in a binding/assignment pattern assigns to the given name.
    public static bool BindingOrAssignmentElementAssignsToName(Node element, string name)
    {
        var target = TransformerAstAdapters.GetTargetOfBindingOrAssignmentElement(element);
        if (target is null)
        {
            return false;
        }
        if (global::Ts.Ast.AstUtilities.IsBindingPattern(target) || TransformerAstAdapters.IsAssignmentPattern(target))
        {
            return BindingOrAssignmentPatternAssignsToName(target, name);
        }
        return global::Ts.Ast.NodeTests.IsIdentifier(target) && string.Equals(target.Text(), name, StringComparison.Ordinal);
    }

    private static bool BindingOrAssignmentPatternAssignsToName(Node pattern, string name)
    {
        foreach (var element in TransformerAstAdapters.GetElementsOfBindingOrAssignmentPattern(pattern))
        {
            if (BindingOrAssignmentElementAssignsToName(element, name))
            {
                return true;
            }
        }
        return false;
    }

    // BindingOrAssignmentElementContainsNonLiteralComputedName checks if any element has a non-literal computed property name.
    public static bool BindingOrAssignmentElementContainsNonLiteralComputedName(Node element)
    {
        var propertyName = TransformerAstAdapters.TryGetPropertyNameOfBindingOrAssignmentElement(element);
        if (propertyName is not null && global::Ts.Ast.NodeTests.IsComputedPropertyName(propertyName) &&
            !global::Ts.Ast.AstUtilities.IsLiteralExpression(propertyName.Expression()!))
        {
            return true;
        }
        var target = TransformerAstAdapters.GetTargetOfBindingOrAssignmentElement(element);
        return target is not null &&
            (global::Ts.Ast.AstUtilities.IsBindingPattern(target) || TransformerAstAdapters.IsAssignmentPattern(target)) &&
            BindingOrAssignmentPatternContainsNonLiteralComputedName(target);
    }

    private static bool BindingOrAssignmentPatternContainsNonLiteralComputedName(Node pattern)
    {
        foreach (var element in TransformerAstAdapters.GetElementsOfBindingOrAssignmentPattern(pattern))
        {
            if (BindingOrAssignmentElementContainsNonLiteralComputedName(element))
            {
                return true;
            }
        }
        return false;
    }

    // GetInitializerOfBindingOrAssignmentElement returns the initializer/default value of a binding or assignment element.
    public static Node? GetInitializerOfBindingOrAssignmentElement(Node? bindingElement)
    {
        if (bindingElement is null)
        {
            return null;
        }
        if (TransformerAstAdapters.IsDeclarationBindingElement(bindingElement))
        {
            return bindingElement.Initializer();
        }
        if (global::Ts.Ast.NodeTests.IsPropertyAssignment(bindingElement))
        {
            var initializer = bindingElement.Initializer();
            if (initializer is not null && global::Ts.Ast.AstUtilities.IsAssignmentExpression(initializer, true))
            {
                return initializer.AsBinaryExpression().Right;
            }
            return null;
        }
        if (global::Ts.Ast.NodeTests.IsShorthandPropertyAssignment(bindingElement))
        {
            return bindingElement.AsShorthandPropertyAssignment().ObjectAssignmentInitializer;
        }
        if (global::Ts.Ast.AstUtilities.IsAssignmentExpression(bindingElement, true))
        {
            return bindingElement.AsBinaryExpression().Right;
        }
        if (global::Ts.Ast.NodeTests.IsSpreadElement(bindingElement))
        {
            return GetInitializerOfBindingOrAssignmentElement(bindingElement.Expression());
        }
        return null;
    }

    private static bool IsObjectBindingOrAssignmentPattern(Node? node) =>
        node is not null && node.Kind is SyntaxKind.ObjectBindingPattern or SyntaxKind.ObjectLiteralExpression;

    private static bool IsArrayBindingOrAssignmentPattern(Node? node) =>
        node is not null && node.Kind is SyntaxKind.ArrayBindingPattern or SyntaxKind.ArrayLiteralExpression;

    private static bool IsSimpleBindingOrAssignmentElement(Node element)
    {
        var target = TransformerAstAdapters.GetTargetOfBindingOrAssignmentElement(element);
        if (target is null || global::Ts.Ast.NodeTests.IsOmittedExpression(target))
        {
            return true;
        }
        var propertyName = TransformerAstAdapters.TryGetPropertyNameOfBindingOrAssignmentElement(element);
        if (propertyName is not null && !global::Ts.Ast.AstUtilities.IsPropertyNameLiteral(propertyName))
        {
            return false;
        }
        var initializer = GetInitializerOfBindingOrAssignmentElement(element);
        if (initializer is not null && !IsSimpleInlineableExpression(initializer))
        {
            return false;
        }
        if (global::Ts.Ast.AstUtilities.IsBindingPattern(target) || TransformerAstAdapters.IsAssignmentPattern(target))
        {
            return global::Ts.Core.CoreUtil.Every(
                TransformerAstAdapters.GetElementsOfBindingOrAssignmentPattern(target),
                IsSimpleBindingOrAssignmentElement);
        }
        return global::Ts.Ast.NodeTests.IsIdentifier(target);
    }
}
