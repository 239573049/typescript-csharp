// Ported from tsgo internal/transformers/tstransforms/legacydecorators.go — the
// experimentalDecorators (`--experimentalDecorators`) __decorate/__param lowering transform.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Binder;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

using EF = global::Ts.Printer.EmitFlags;

public sealed class LegacyDecoratorsTransformer : global::Ts.Transformers.Transformer
{
    private readonly ScriptTarget languageVersion;
    private readonly IReferenceResolver? referenceResolver;

    /**
     * A map that keeps track of aliases created for classes with decorators to avoid issues
     * with the double-binding behavior of classes.
     */
    private Dictionary<Node, Node>? classAliases;
    private readonly List<ClassDeclaration> enclosingClasses = new();

    // legacydecorators.go:25-28
    private LegacyDecoratorsTransformer(global::Ts.Transformers.TransformOptions options)
    {
        languageVersion = (options.CompilerOptions ?? new CompilerOptions()).GetEmitScriptTarget();
        referenceResolver = options.Resolver;
        NewTransformer(Visit, options.Context);
    }

    public static global::Ts.Transformers.Transformer NewLegacyDecoratorsTransformer(
        global::Ts.Transformers.TransformOptions options) => new LegacyDecoratorsTransformer(options);

    // legacydecorators.go:30-71
    private Node? Visit(Node node)
    {
        // we have to visit all identifiers in classes, just in case they require substitution
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsDecorators) == 0 && enclosingClasses.Count == 0)
        {
            return node;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                return VisitIdentifier(node.AsIdentifier());
            case SyntaxKind.PropertyAccessExpression:
                return VisitPropertyAccessExpression(node.AsPropertyAccessExpression());
            case SyntaxKind.Decorator:
                // Decorators are elided. They will be emitted as part of `visitClassDeclaration`.
                return null;
            case SyntaxKind.ClassDeclaration:
                return VisitClassDeclaration(node.AsClassDeclaration());
            case SyntaxKind.ClassExpression:
                return VisitClassExpression(node.AsClassExpression());
            case SyntaxKind.Constructor:
                return VisitConstructorDeclaration(node.AsConstructorDeclaration());
            case SyntaxKind.MethodDeclaration:
                return VisitMethodDeclaration(node.AsMethodDeclaration());
            case SyntaxKind.SetAccessor:
                return VisitSetAccessorDeclaration(node.AsSetAccessorDeclaration());
            case SyntaxKind.GetAccessor:
                return VisitGetAccessorDeclaration(node.AsGetAccessorDeclaration());
            case SyntaxKind.PropertyDeclaration:
                return VisitPropertyDeclaration(node.AsPropertyDeclaration());
            case SyntaxKind.Parameter:
                return VisitParamerDeclaration(node.AsParameterDeclaration());
            case SyntaxKind.SourceFile:
            {
                classAliases = new Dictionary<Node, Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
                enclosingClasses.Clear();
                var result = Visitor().VisitEachChild(node)!;
                EmitContext().AddEmitHelper(result, EmitContext().ReadEmitHelpers());
                classAliases = null;
                enclosingClasses.Clear();
                return result;
            }
            default:
                return Visitor().VisitEachChild(node);
        }
    }

    // legacydecorators.go:73-81
    private Node VisitIdentifier(Identifier node)
    {
        // takes the place of `substituteIdentifier` in the strada transform
        foreach (var d in enclosingClasses)
        {
            if (classAliases != null && classAliases.TryGetValue(d.AsNode(), out var alias) &&
                referenceResolver?.GetReferencedValueDeclaration(EmitContext().MostOriginal(node.AsNode())!) == EmitContext().MostOriginal(d.AsNode()))
            {
                return alias;
            }
        }
        return node.AsNode();
    }

    // legacydecorators.go:83-92
    private Node VisitPropertyAccessExpression(PropertyAccessExpression node)
    {
        // Visit the expression but not the name, since property access names should not be substituted.
        // Strada's onSubstituteNode only fires for EmitHint.Expression, which excludes the
        // .name of PropertyAccessExpression.
        var expression = Visitor().VisitNode(node.Expression);
        if (expression != node.Expression)
        {
            return Factory().AsNodeFactory().UpdatePropertyAccessExpression(node, expression, node.QuestionDotToken, node.Name(), node.Flags);
        }
        return node.AsNode();
    }

    // legacydecorators.go:94-104
    private static NodeList? ElideNodes(global::Ts.Printer.NodeFactory f, NodeList? nodes)
    {
        if (nodes == null)
        {
            return null;
        }
        if (nodes.Nodes.Length == 0)
        {
            return nodes;
        }
        var replacement = f.NewNodeList([]);
        replacement.Loc = nodes.Loc;
        return replacement;
    }

    // legacydecorators.go:106-116
    private static ModifierList? ElideModifiers(global::Ts.Printer.NodeFactory f, ModifierList? nodes)
    {
        if (nodes == null)
        {
            return null;
        }
        if (nodes.Nodes.Length == 0)
        {
            return nodes;
        }
        var replacement = f.AsNodeFactory().NewModifierList([]);
        replacement.Loc = nodes.Loc;
        return replacement;
    }

    // legacydecorators.go:118-126
    private Node FinishClassElement(Node updated, Node original)
    {
        if (updated != original)
        {
            // While we emit the source map for the node after skipping decorators and modifiers,
            // we need to emit the comments for the original range.
            EmitContext().SetCommentRange(updated, original.Loc);
            EmitContext().SetSourceMapRange(updated, global::Ts.Transformers.Transformers.MoveRangePastModifiers(original));
        }
        return updated;
    }

    // legacydecorators.go:128-148
    private Node VisitParamerDeclaration(ParameterDeclaration node)
    {
        var updated = Factory().AsNodeFactory().UpdateParameterDeclaration(
            node,
            ElideModifiers(Factory(), node.Modifiers()),
            node.DotDotDotToken,
            Visitor().VisitNode(node.Name()),
            null,
            null,
            Visitor().VisitNode(node.Initializer));
        if (updated != node.AsNode())
        {
            // While we emit the source map for the node after skipping decorators and modifiers,
            // we need to emit the comments for the original range.
            EmitContext().SetCommentRange(updated, node.Loc);
            var newLoc = global::Ts.Transformers.Transformers.MoveRangePastModifiers(node.AsNode());
            updated.Loc = newLoc;
            EmitContext().SetSourceMapRange(updated, newLoc);
            EmitContext().SetEmitFlags(updated.Name()!, EF.NoTrailingSourceMap);
        }
        return updated;
    }

    // legacydecorators.go:150-165 — visitPropertyNameOfClassElement visits the property name of a class
    // element, for use when emitting property initializers. For a computed property on a node
    // with decorators, a temporary value is stored for later use.
    private Node? VisitPropertyNameOfClassElement(Node member)
    {
        var name = member.Name()!;
        if (NodeTests.IsComputedPropertyName(name) && AstUtilities.HasDecorators(member))
        {
            var expression = Visitor().VisitNode(name.AsComputedPropertyName().Expression);
            var innerExpression = AstUtilities.SkipPartiallyEmittedExpressions(expression!);
            if (!global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(innerExpression))
            {
                var generatedName = Factory().NewGeneratedNameForNode(name);
                EmitContext().AddVariableDeclaration(generatedName);
                return Factory().AsNodeFactory().UpdateComputedPropertyName(name.AsComputedPropertyName(), Factory().NewAssignmentExpression(generatedName, expression!));
            }
        }
        return Visitor().VisitNode(name);
    }

    // legacydecorators.go:167-186
    private Node? VisitPropertyDeclaration(PropertyDeclaration node)
    {
        if ((node.Flags & NodeFlags.Ambient) != 0)
        {
            return null;
        }
        if (AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Ambient | ModifierFlags.Abstract))
        {
            return null;
        }

        return FinishClassElement(
            Factory().AsNodeFactory().UpdatePropertyDeclaration(
                node,
                Visitor().VisitModifiers(node.Modifiers()),
                VisitPropertyNameOfClassElement(node.AsNode()),
                null,
                null,
                Visitor().VisitNode(node.Initializer)),
            node.AsNode());
    }

    // legacydecorators.go:188-202
    private Node VisitGetAccessorDeclaration(GetAccessorDeclaration node)
    {
        return FinishClassElement(
            Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
                node,
                Visitor().VisitModifiers(node.Modifiers()),
                VisitPropertyNameOfClassElement(node.AsNode()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                Visitor().VisitNode(node.Body)),
            node.AsNode());
    }

    // legacydecorators.go:204-218
    private Node VisitSetAccessorDeclaration(SetAccessorDeclaration node)
    {
        return FinishClassElement(
            Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
                node,
                Visitor().VisitModifiers(node.Modifiers()),
                VisitPropertyNameOfClassElement(node.AsNode()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                Visitor().VisitNode(node.Body)),
            node.AsNode());
    }

    // legacydecorators.go:220-236
    private Node VisitMethodDeclaration(MethodDeclaration node)
    {
        return FinishClassElement(
            Factory().AsNodeFactory().UpdateMethodDeclaration(
                node,
                Visitor().VisitModifiers(node.Modifiers()),
                node.AsteriskToken,
                VisitPropertyNameOfClassElement(node.AsNode()),
                null,
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                Visitor().VisitNode(node.Body)),
            node.AsNode());
    }

    // legacydecorators.go:238-248
    private Node VisitConstructorDeclaration(ConstructorDeclaration node)
    {
        return Factory().AsNodeFactory().UpdateConstructorDeclaration(
            node,
            Visitor().VisitModifiers(node.Modifiers()),
            null,
            Visitor().VisitNodes(node.Parameters),
            null,
            null,
            Visitor().VisitNode(node.Body));
    }

    // legacydecorators.go:250-260
    private Node VisitClassExpression(ClassExpression node)
    {
        // Legacy decorators were not supported on class expressions
        return Factory().AsNodeFactory().UpdateClassExpression(
            node,
            Visitor().VisitModifiers(node.Modifiers()),
            node.Name(),
            null,
            Visitor().VisitNodes(node.HeritageClauses),
            Visitor().VisitNodes(node.Members));
    }

    // legacydecorators.go:262-272
    private Node? VisitClassDeclaration(ClassDeclaration node)
    {
        var decorated = AstUtilities.ClassOrConstructorParameterIsDecorated(true, node.AsNode());
        if (!(decorated || AstUtilities.ChildIsDecorated(true, node.AsNode(), null)))
        {
            return Visitor().VisitEachChild(node.AsNode());
        }

        if (decorated)
        {
            return TransformClassDeclarationWithClassDecorators(node, node.Name());
        }
        return TransformClassDeclarationWithoutClassDecorators(node, node.Name());
    }

    /**
    * Transforms a non-decorated class declaration.
    *
    * @param node A ClassDeclaration node.
    * @param name The name of the class.
     */
    // legacydecorators.go:280-306
    private Node TransformClassDeclarationWithoutClassDecorators(ClassDeclaration node, Node? name)
    {
        //  ${modifiers} class ${name} ${heritageClauses} {
        //      ${members}
        //  }
        var modifiers = Visitor().VisitModifiers(node.Modifiers());
        var heritageClauses = Visitor().VisitNodes(node.HeritageClauses);
        var initialMembers = Visitor().VisitNodes(node.Members);
        var (members, decorationStatements) = TransformDecoratorsOfClassElements(node, initialMembers);

        if (name == null && decorationStatements.Count > 0)
        {
            name = Factory().NewGeneratedNameForNode(node.AsNode());
        }

        var updated = Factory().AsNodeFactory().UpdateClassDeclaration(
            node,
            modifiers,
            name,
            null,
            heritageClauses,
            members);

        if (decorationStatements.Count == 0)
        {
            return updated;
        }
        Node[] list = [updated, .. decorationStatements];
        return Factory().AsNodeFactory().NewSyntaxList(list);
    }

    // legacydecorators.go:308-310
    private void PopEnclosingClass() => enclosingClasses.RemoveAt(enclosingClasses.Count - 1);

    // legacydecorators.go:312-314
    private void PushEnclosingClass(ClassDeclaration cls) => enclosingClasses.Add(cls);

    /**
    * Transforms a decorated class declaration and appends the resulting statements. If
    * the class requires an alias to avoid issues with double-binding, the alias is returned.
     */
    // legacydecorators.go:320-510 (the large emit-shape comment block is preserved in the Go source)
    private Node TransformClassDeclarationWithClassDecorators(ClassDeclaration node, Node? name)
    {
        var isExport = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export);
        var isDefault = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Default);
        ModifierList? modifiers = null;
        if (node.Modifiers() != null && node.Modifiers()!.Nodes.Length > 0)
        {
            var modifierNodes = node.Modifiers()!.Nodes.Where(IsNotExportOrDefaultOrDecorator).ToArray();
            if (modifierNodes.Length != node.Modifiers()!.Nodes.Length)
            {
                modifiers = Factory().AsNodeFactory().NewModifierList(modifierNodes);
                modifiers.Loc = node.Modifiers()!.Loc;
            }
            else
            {
                modifiers = node.Modifiers();
            }
        }

        var location = global::Ts.Transformers.Transformers.MoveRangePastModifiers(node.AsNode());
        var classAlias = GetClassAliasIfNeeded(node);
        var pushed = classAlias != null;
        if (pushed)
        {
            PushEnclosingClass(node);
        }
        try
        {
            // When we used to transform to ES5/3 this would be moved inside an IIFE and should reference the name
            // without any block-scoped variable collision handling - but we don't support that anymore, so we always
            // use the local name for the class
            var declName = Factory().GetLocalNameEx(node.AsNode(), new global::Ts.Printer.AssignedNameOptions { AllowComments = false, AllowSourceMaps = true });

            //  ... = class ${name} ${heritageClauses} {
            //      ${members}
            //  }
            var heritageClauses = Visitor().VisitNodes(node.HeritageClauses);
            var visitedMembers = Visitor().VisitNodes(node.Members);

            var (members, decorationStatements) = TransformDecoratorsOfClassElements(node, visitedMembers);

            // If we're emitting to ES2022 or later then we need to reassign the class alias before
            // static initializers are evaluated.
            var assignClassAliasInStaticBlock = languageVersion >= ScriptTarget.ES2022 && classAlias != null &&
                members != null && members.Nodes.Length > 0 && members.Nodes.Any(IsClassStaticBlockDeclarationOrStaticProperty);
            if (assignClassAliasInStaticBlock)
            {
                var memberList = new List<Node>
                {
                    Factory().AsNodeFactory().NewClassStaticBlockDeclaration(null, Factory().NewBlock(
                        Factory().NewNodeList([Factory().NewExpressionStatement(
                            Factory().NewAssignmentExpression(classAlias!, Factory().NewKeywordExpression(SyntaxKind.ThisKeyword)))]),
                        false)),
                };
                memberList.AddRange(members!.Nodes);
                var newList = Factory().NewNodeList(memberList.ToArray());
                newList.Loc = members.Loc;
                members = newList;
            }

            var exprName = name;
            if (name != null && global::Ts.Transformers.Transformers.IsGeneratedIdentifier(EmitContext(), name))
            {
                exprName = null;
            }
            var classExpression = Factory().AsNodeFactory().NewClassExpression(
                modifiers,
                exprName,
                null,
                heritageClauses,
                members);

            EmitContext().SetOriginal(classExpression, node.AsNode());
            classExpression.Loc = location;

            //  let ${name} = ${classExpression} where name is either declaredName if the class doesn't contain self-reference
            //                                         or decoratedClassAlias if the class contain self-reference.
            var varInitializer = classExpression;
            if (classAlias != null && !assignClassAliasInStaticBlock)
            {
                varInitializer = Factory().NewAssignmentExpression(classAlias, classExpression);
            }
            var varDecl = Factory().NewVariableDeclaration(
                declName,
                null,
                null,
                varInitializer);
            EmitContext().SetOriginal(varDecl, node.AsNode());

            var varDeclList = Factory().NewVariableDeclarationList(Factory().NewNodeList([varDecl]), NodeFlags.Let);
            var varStatement = Factory().NewVariableStatement(null, varDeclList);
            EmitContext().SetOriginal(varStatement, node.AsNode());
            varStatement.Loc = location;
            EmitContext().SetCommentRange(varStatement, node.Loc);

            var statements = new List<Node> { varStatement };
            statements.AddRange(decorationStatements);
            var ctorDecoration = GetConstructorDecorationStatement(node);
            if (ctorDecoration != null)
            {
                statements.Add(ctorDecoration);
            }

            if (isExport)
            {
                Node exportStatement;
                if (isDefault)
                {
                    exportStatement = Factory().NewExportDefault(declName);
                }
                else
                {
                    exportStatement = Factory().NewExternalModuleExport(Factory().GetDeclarationName(node.AsNode()));
                }
                statements.Add(exportStatement);
            }

            if (statements.Count == 1)
            {
                return statements[0];
            }
            return Factory().AsNodeFactory().NewSyntaxList(statements.ToArray());
        }
        finally
        {
            if (pushed)
            {
                PopEnclosingClass();
            }
        }
    }

    // legacydecorators.go:512-532
    private bool HasInternalStaticReference(ClassDeclaration node)
    {
        var classNode = EmitContext().MostOriginal(node.AsNode());
        Visitor? isOrContainsStaticSelfReference = null;
        isOrContainsStaticSelfReference = n =>
        {
            if (NodeTests.IsIdentifier(n) && referenceResolver?.GetReferencedValueDeclaration(EmitContext().MostOriginal(n)!) == classNode)
            {
                return true;
            }
            // For PropertyAccessExpression, only check the expression, not the name.
            // The .Name() is a property access name, not a value reference to the class.
            if (NodeTests.IsPropertyAccessExpression(n))
            {
                return isOrContainsStaticSelfReference!(n.Expression()!);
            }
            return n.ForEachChild(isOrContainsStaticSelfReference!);
        };
        foreach (var member in node.Members!.Nodes)
        {
            if (member.ForEachChild(isOrContainsStaticSelfReference))
            {
                return true;
            }
        }
        return false;
    }

    /**
    * Gets a local alias for a class declaration if it is a decorated class with an internal
    * reference to the static side of the class. This is necessary to avoid issues with
    * double-binding semantics for the class name.
     */
    // legacydecorators.go:539-553
    private Node? GetClassAliasIfNeeded(ClassDeclaration node)
    {
        if (!HasInternalStaticReference(node))
        {
            return null;
        }
        var nameText = "default";
        if (node.Name() != null && !global::Ts.Transformers.Transformers.IsGeneratedIdentifier(EmitContext(), node.Name()!))
        {
            nameText = node.Name()!.Text();
        }

        var classAlias = Factory().NewUniqueName(nameText);
        EmitContext().AddVariableDeclaration(classAlias);
        classAliases![node.AsNode()] = classAlias;

        return classAlias;
    }

    /**
    * Generates a __decorate helper call for a class constructor.
    *
    * @param node The class node.
     */
    // legacydecorators.go:560-568
    private Node? GetConstructorDecorationStatement(ClassDeclaration node)
    {
        var expression = GenerateConstructorDecorationExpression(node);
        if (expression != null)
        {
            var result = Factory().NewExpressionStatement(expression);
            EmitContext().SetOriginal(result, node.AsNode());
            return result;
        }
        return null;
    }

    /**
    * Generates a __decorate helper call for a class constructor.
    *
    * @param node The class node.
     */
    // legacydecorators.go:575-612
    private Node? GenerateConstructorDecorationExpression(ClassDeclaration node)
    {
        var allDecorators = GetAllDecoratorsOfClass(node, true);
        // Decorator expressions are evaluated outside the class body, so references to the
        // class name should use the original binding, not the class alias. In Strada, this is
        // handled by NodeCheckFlags.ConstructorReference which is only set for identifiers
        // inside the class body. Since Corsa lacks per-node flags, we temporarily pop the
        // enclosing class to prevent alias substitution during decorator expression visiting.
        var hasAlias = enclosingClasses.Count > 0 && enclosingClasses[^1] == node;
        if (hasAlias)
        {
            PopEnclosingClass();
        }
        var decoratorExpressions = TransformAllDecoratorsOfDeclaration(allDecorators);
        if (hasAlias)
        {
            PushEnclosingClass(node);
        }
        if (decoratorExpressions.Count == 0)
        {
            return null;
        }

        Node? classAlias = null;
        if (classAliases != null)
        {
            classAliases.TryGetValue(node.AsNode(), out classAlias);
        }

        // When we used to transform to ES5/3 this would be moved inside an IIFE and should reference the name
        // without any block-scoped variable collision handling - but we don't support that anymore, so we always
        // use the local name for the class
        var localName = Factory().GetDeclarationNameEx(node.AsNode(), new global::Ts.Printer.NameOptions { AllowComments = false, AllowSourceMaps = true });
        var decorate = Factory().NewDecorateHelper(decoratorExpressions.ToArray(), localName, null, null);
        var assignmentTarget = decorate;
        if (classAlias != null)
        {
            assignmentTarget = Factory().NewAssignmentExpression(classAlias, decorate);
        }
        var expression = Factory().NewAssignmentExpression(localName, assignmentTarget);
        EmitContext().SetEmitFlags(expression, EF.NoComments);
        EmitContext().SetSourceMapRange(expression, global::Ts.Transformers.Transformers.MoveRangePastModifiers(node.AsNode()));
        return expression;
    }

    // legacydecorators.go:614-616
    private static bool IsClassStaticBlockDeclarationOrStaticProperty(Node node)
    {
        return NodeTests.IsClassStaticBlockDeclaration(node) || (NodeTests.IsPropertyDeclaration(node) && AstUtilities.HasStaticModifier(node));
    }

    // legacydecorators.go:618-620
    private static bool IsNotExportOrDefaultOrDecorator(Node node)
    {
        return !(NodeTests.IsDecorator(node) || node.Kind == SyntaxKind.ExportKeyword || node.Kind == SyntaxKind.DefaultKeyword);
    }

    // legacydecorators.go:622-624
    private static bool DecoratorContainsPrivateIdentifierInExpression(Node decorator)
    {
        return (decorator.SubtreeFacts() & SubtreeFacts.SubtreeContainsPrivateIdentifierInExpression) != 0;
    }

    // legacydecorators.go:626-628
    private static bool ParameterDecoratorsContainPrivateIdentifierInExpression(Node[]? parameterDecorators)
    {
        return parameterDecorators != null && parameterDecorators.Any(DecoratorContainsPrivateIdentifierInExpression);
    }

    // legacydecorators.go:630-650
    private static bool HasClassElementWithDecoratorContainingPrivateIdentifierInExpression(ClassDeclaration node)
    {
        if (node.Members == null || node.Members.Nodes.Length == 0)
        {
            return false;
        }
        foreach (var member in node.Members.Nodes)
        {
            if (!AstUtilities.CanHaveDecorators(member))
            {
                continue;
            }
            var allDecorators = GetAllDecoratorsOfClassElement(member, node, true);
            if (allDecorators == null)
            {
                continue;
            }
            if (allDecorators.decorators != null && allDecorators.decorators.Any(DecoratorContainsPrivateIdentifierInExpression))
            {
                return true;
            }
            if (allDecorators.parameters != null && allDecorators.parameters.Any(ParameterDecoratorsContainPrivateIdentifierInExpression))
            {
                return true;
            }
        }
        return false;
    }

    // legacydecorators.go:652-655
    private sealed class AllDecorators
    {
        internal Node[]? decorators;
        internal Node[]?[]? parameters;
    }

    /**
     * Gets an allDecorators object containing the decorators for the class and the decorators for the
     * parameters of the constructor of the class.
     *
     * @param node The class node.
     *
     * @internal
     */
    // legacydecorators.go:665-675
    private static AllDecorators? GetAllDecoratorsOfClass(ClassDeclaration node, bool useLegacyDecorators)
    {
        var decorators = node.AsNode().Decorators();
        Node[]?[]? parameters = null;
        if (useLegacyDecorators)
        {
            parameters = GetDecoratorsOfParameters(AstUtilities.GetFirstConstructorWithBody(node.AsNode()));
        }
        if ((decorators == null || decorators.Length == 0) && (parameters == null || parameters.Length == 0))
        {
            return null;
        }
        return new AllDecorators { decorators = decorators, parameters = parameters };
    }

    /**
     * Gets an allDecorators object containing the decorators for the member and its parameters.
     *
     * @param parent The class node that contains the member.
     * @param member The class member.
     *
     * @internal
     */
    // legacydecorators.go:685-699
    private static AllDecorators? GetAllDecoratorsOfClassElement(Node member, ClassDeclaration parent, bool useLegacyDecorators)
    {
        switch (member.Kind)
        {
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                if (!useLegacyDecorators)
                {
                    return GetAllDecoratorsOfMethod(member, false);
                }
                return GetAllDecoratorsOfAccessors(member, parent, true);
            case SyntaxKind.MethodDeclaration:
                return GetAllDecoratorsOfMethod(member, useLegacyDecorators);
            case SyntaxKind.PropertyDeclaration:
                return GetAllDecoratorsOfProperty(member);
            default:
                return null;
        }
    }

    /**
     * Gets an allDecorators object containing the decorators for the accessor and its parameters.
     *
     * @param parent The class node that contains the accessor.
     * @param accessor The class accessor member.
     */
    // legacydecorators.go:707-737
    private static AllDecorators? GetAllDecoratorsOfAccessors(Node accessor, ClassDeclaration parent, bool useLegacyDecorators)
    {
        if (accessor.Body() == null)
        {
            return null;
        }
        var decls = AstUtilities.GetAllAccessorDeclarations(parent.Members!.Nodes, accessor);
        Node? firstAccessorWithDecorators = null;
        if (AstUtilities.HasDecorators(decls.FirstAccessor!))
        {
            firstAccessorWithDecorators = decls.FirstAccessor;
        }
        else if (decls.SecondAccessor != null && AstUtilities.HasDecorators(decls.SecondAccessor))
        {
            firstAccessorWithDecorators = decls.SecondAccessor;
        }

        if (firstAccessorWithDecorators == null || accessor != firstAccessorWithDecorators)
        {
            return null;
        }

        var decorators = firstAccessorWithDecorators.Decorators();
        Node[]?[]? parameters = null;
        if (useLegacyDecorators && decls.SetAccessor != null)
        {
            parameters = GetDecoratorsOfParameters(decls.SetAccessor.AsNode());
        }

        if ((decorators == null || decorators.Length == 0) && (parameters == null || parameters.Length == 0))
        {
            return null;
        }

        return new AllDecorators
        {
            decorators = decorators,
            parameters = parameters,
        };
    }

    // legacydecorators.go:739-745
    private static AllDecorators? GetAllDecoratorsOfProperty(Node property)
    {
        var decorators = property.Decorators();
        if (decorators == null || decorators.Length == 0)
        {
            return null;
        }
        return new AllDecorators { decorators = decorators };
    }

    // legacydecorators.go:747-760
    private static AllDecorators? GetAllDecoratorsOfMethod(Node method, bool useLegacyDecorators)
    {
        if (method.Body() == null)
        {
            return null;
        }
        var decorators = method.Decorators();
        Node[]?[]? parameters = null;
        if (useLegacyDecorators)
        {
            parameters = GetDecoratorsOfParameters(method);
        }
        if ((decorators == null || decorators.Length == 0) && (parameters == null || parameters.Length == 0))
        {
            return null;
        }
        return new AllDecorators { decorators = decorators, parameters = parameters };
    }

    /**
     * Gets an array of arrays of decorators for the parameters of a function-like node.
     * The offset into the result array should correspond to the offset of the parameter.
     *
     * @param node The function-like node.
     */
    // legacydecorators.go:768-791
    private static Node[]?[]? GetDecoratorsOfParameters(Node? node)
    {
        Node[]?[]? decorators = null;
        if (node != null)
        {
            var parameters = node.Parameters();
            var firstParameterIsThis = parameters.Length > 0 && AstUtilities.IsThisParameter(parameters[0]);
            var firstParameterOffset = 0;
            var numParameters = parameters.Length;
            if (firstParameterIsThis)
            {
                firstParameterOffset = 1;
                numParameters -= 1;
            }
            for (var i = 0; i < numParameters; i++)
            {
                var p = parameters[i + firstParameterOffset];
                if (decorators != null || AstUtilities.HasDecorators(p))
                {
                    decorators ??= new Node[]?[numParameters];
                    decorators[i] = p.Decorators();
                }
            }
        }
        return decorators;
    }

    // legacydecorators.go:793-812
    private (NodeList? Members, List<Node> DecorationStatements) TransformDecoratorsOfClassElements(ClassDeclaration node, NodeList? members)
    {
        var decorationStatements = new List<Node>();
        decorationStatements.AddRange(GetClassElementDecorationStatements(node, false));
        decorationStatements.AddRange(GetClassElementDecorationStatements(node, true));
        if (HasClassElementWithDecoratorContainingPrivateIdentifierInExpression(node))
        {
            var memberNodes = new List<Node>();
            if (members != null && members.Nodes.Length > 0)
            {
                memberNodes.AddRange(members.Nodes);
            }
            memberNodes.Add(Factory().AsNodeFactory().NewClassStaticBlockDeclaration(null, Factory().NewBlock(Factory().NewNodeList(decorationStatements.ToArray()), true)));
            members = Factory().NewNodeList(memberNodes.ToArray());
            decorationStatements = new List<Node>();
        }

        return (members, decorationStatements);
    }

    /**
    * Generates statements used to apply decorators to either the static or instance members
    * of a class.
    *
    * @param node The class node.
    * @param isStatic A value indicating whether to generate statements for static or
    *                 instance members.
     */
    // legacydecorators.go:822-829
    private List<Node> GetClassElementDecorationStatements(ClassDeclaration node, bool isStatic)
    {
        var exprs = GenerateClassElementDecorationExpressions(node, isStatic);
        var statements = new List<Node>();
        foreach (var e in exprs)
        {
            statements.Add(Factory().NewExpressionStatement(e));
        }
        return statements;
    }

    /**
    * Determines whether a class member is either a static or an instance member of a class
    * that is decorated, or has parameters that are decorated.
    *
    * @param member The class member.
     */
    // legacydecorators.go:837-839
    private static bool IsDecoratedClassElement(Node member, bool isStaticElement, ClassDeclaration parent)
    {
        return isStaticElement == AstUtilities.IsStatic(member) && AstUtilities.NodeOrChildIsDecorated(true, member, parent.AsNode(), null);
    }

    /**
    * Gets either the static or instance members of a class that are decorated, or have
    * parameters that are decorated.
    *
    * @param node The class containing the member.
    * @param isStatic A value indicating whether to retrieve static or instance members of
    *                 the class.
     */
    // legacydecorators.go:849-860
    private static List<Node> GetDecoratedClassElements(ClassDeclaration node, bool isStatic)
    {
        var members = new List<Node>();
        if (node.Members == null || node.Members.Nodes.Length == 0)
        {
            return members;
        }
        foreach (var member in node.Members.Nodes)
        {
            if (IsDecoratedClassElement(member, isStatic, node))
            {
                members.Add(member);
            }
        }
        return members;
    }

    /**
    * Generates expressions used to apply decorators to either the static or instance members
    * of a class.
    *
    * @param node The class node.
    * @param isStatic A value indicating whether to generate expressions for static or
    *                 instance members.
     */
    // legacydecorators.go:870-880
    private List<Node> GenerateClassElementDecorationExpressions(ClassDeclaration node, bool isStatic)
    {
        var members = GetDecoratedClassElements(node, isStatic);
        var expressions = new List<Node>();
        foreach (var member in members)
        {
            var expr = GenerateClassElementDecorationExpression(node, member);
            if (expr != null)
            {
                expressions.Add(expr);
            }
        }
        return expressions;
    }

    /**
    * Generates an expression used to evaluate class element decorators at runtime.
    *
    * @param node The class node that contains the member.
    * @param member The class member.
     */
    // legacydecorators.go:888-949
    private Node? GenerateClassElementDecorationExpression(ClassDeclaration node, Node member)
    {
        var allDecorators = GetAllDecoratorsOfClassElement(member, node, true);
        var decoratorExpressions = TransformAllDecoratorsOfDeclaration(allDecorators);
        if (decoratorExpressions.Count == 0)
        {
            return null;
        }

        // Emit the call to __decorate. Given the following:
        //
        //   class C {
        //     @dec method(@dec2 x) {}
        //     @dec get accessor() {}
        //     @dec prop;
        //   }
        //
        // The emit for a method is:
        //
        //   __decorate([
        //       dec,
        //       __param(0, dec2),
        //       __metadata("design:type", Function),
        //       __metadata("design:paramtypes", [Object]),
        //       __metadata("design:returntype", void 0)
        //   ], C.prototype, "method", null);
        //
        // The emit for an accessor is:
        //
        //   __decorate([
        //       dec
        //   ], C.prototype, "accessor", null);
        //
        // The emit for a property is:
        //
        //   __decorate([
        //       dec
        //   ], C.prototype, "prop");
        //

        var prefix = GetClassMemberPrefix(node, member);
        var memberName = GetExpressionForPropertyName(member, (member.Flags & NodeFlags.Ambient) == 0);
        Node descriptor;
        if (NodeTests.IsPropertyDeclaration(member) && !AstUtilities.HasAccessorModifier(member))
        {
            // We emit `void 0` here to indicate to `__decorate` that it can invoke `Object.defineProperty` directly, but that it
            // should not invoke `Object.getOwnPropertyDescriptor`.
            descriptor = Factory().NewVoidZeroExpression();
        }
        else
        {
            // We emit `null` here to indicate to `__decorate` that it can invoke `Object.getOwnPropertyDescriptor` directly.
            // We have this extra argument here so that we can inject an explicit property descriptor at a later date.
            descriptor = Factory().NewKeywordExpression(SyntaxKind.NullKeyword);
        }

        var helper = Factory().NewDecorateHelper(
            decoratorExpressions.ToArray(),
            prefix,
            memberName,
            descriptor);

        EmitContext().SetEmitFlags(helper, EF.NoComments);
        EmitContext().SetSourceMapRange(helper, global::Ts.Transformers.Transformers.MoveRangePastModifiers(member));
        return helper;
    }

    // legacydecorators.go:951-953
    private bool IsSyntheticMetadataDecorator(Node node)
    {
        return EmitContext().IsCallToHelper(node.Expression()!, "__metadata");
    }

    /**
    * Transforms all of the decorators for a declaration into an array of expressions.
    *
    * @param allDecorators An object containing all of the decorators for the declaration.
     */
    // legacydecorators.go:960-975
    private List<Node> TransformAllDecoratorsOfDeclaration(AllDecorators? allDecorators)
    {
        var decoratorExpressions = new List<Node>();
        if (allDecorators == null)
        {
            return decoratorExpressions;
        }

        // ensure that metadata decorators are last
        var mm = global::Ts.Collections.MultiMap.GroupBy(allDecorators.decorators, IsSyntheticMetadataDecorator);
        var metadata = mm.Get(true);
        var decorators = mm.Get(false);

        decoratorExpressions.AddRange(TransformDecorators(decorators));
        decoratorExpressions.AddRange(TransformDecoratorsOfParameters(allDecorators.parameters));
        decoratorExpressions.AddRange(TransformDecorators(metadata));
        return decoratorExpressions;
    }

    // legacydecorators.go:977-993
    private List<Node> TransformDecoratorsOfParameters(Node[]?[]? parameters)
    {
        var results = new List<Node>();
        if (parameters == null)
        {
            return results;
        }
        for (var i = 0; i < parameters.Length; i++)
        {
            var decorators = parameters[i];
            if (decorators != null && decorators.Length > 0)
            {
                foreach (var decorator in decorators)
                {
                    var helper = Factory().NewParamHelper(
                        Visitor().VisitNode(decorator.Expression())!,
                        i,
                        decorator.Expression()!.Loc);
                    EmitContext().SetEmitFlags(helper, EF.NoComments);
                    results.Add(helper);
                }
            }
        }
        return results;
    }

    /**
    * Transforms a list of decorators into an expression.
    *
    * @param decorator The decorator node.
     */
    // legacydecorators.go:1000-1006
    private List<Node> TransformDecorators(IReadOnlyList<Node>? decorators)
    {
        var results = new List<Node>();
        if (decorators == null)
        {
            return results;
        }
        foreach (var d in decorators)
        {
            results.Add(Visitor().VisitNode(d.Expression())!);
        }
        return results;
    }

    // legacydecorators.go:1008-1013
    private Node GetClassMemberPrefix(ClassDeclaration node, Node member)
    {
        if (AstUtilities.IsStatic(member))
        {
            return Factory().GetDeclarationName(node.AsNode());
        }
        return GetClassPrototype(node);
    }

    // legacydecorators.go:1015-1022
    private Node GetClassPrototype(ClassDeclaration node)
    {
        return Factory().NewPropertyAccessExpression(
            Factory().GetDeclarationName(node.AsNode()),
            null,
            Factory().NewIdentifier("prototype"),
            NodeFlags.None);
    }

    // legacydecorators.go:1024-1038
    private Node GetExpressionForPropertyName(Node member, bool generateNameForComputedPropertyName)
    {
        var name = member.Name()!;
        if (NodeTests.IsPrivateIdentifier(name))
        {
            return Factory().NewIdentifier("");
        }
        else if (NodeTests.IsComputedPropertyName(name))
        {
            if (generateNameForComputedPropertyName && !global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(name.AsComputedPropertyName().Expression!))
            {
                return Factory().NewGeneratedNameForNode(name);
            }
            return name.AsComputedPropertyName().Expression!;
        }
        else if (NodeTests.IsIdentifier(name))
        {
            return Factory().NewStringLiteral(name.Text(), TokenFlags.None);
        }
        else
        {
            return Factory().AsNodeFactory().DeepCloneNode(name)!;
        }
    }
}
