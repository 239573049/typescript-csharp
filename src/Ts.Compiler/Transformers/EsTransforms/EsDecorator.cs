// Ported from tsgo internal/transformers/estransforms/esdecorator.go — the ES Next decorator
// (__esDecorate/__runInitializers) transform. Large file split into partials, function order
// follows the Go original:
//   EsDecorator.cs            — types, transformer state, entry, lexical stack, dispatch visitors
//   EsDecorator.Classes.cs    — createClassInfo … finishClassElement
//   EsDecorator.Members.cs    — partialResult/visitMethodDeclaration … visitPropertyDeclaration
//   EsDecorator.Expressions.cs— visitThisExpression … visitPartiallyEmittedExpression
//   EsDecorator.Helpers.cs    — prependExpressions … injectClassThisAssignmentIfMissing

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;

// Class/Decorator evaluation order, as it pertains to this transformer:
//
// 1. Class decorators are evaluated outside of the private name scope of the class.
//    - 15.8.20 RS: BindingClassDeclarationEvaluation
//    - 15.8.21 RS: Evaluation
//    - 8.3.5 RS: NamedEvaluation
// 2. ClassHeritage clause is evaluated outside of the private name scope of the class.
//    - 15.8.19 RS: ClassDefinitionEvaluation, Step 8.c.
// 3. The name of the class is assigned.
// 4. For each member:
//    a. Member Decorators are evaluated.
//       - 15.8.19 RS: ClassDefinitionEvaluation, Step 23.
//       - Probably 15.7.13 RS: ClassElementEvaluation, but it's missing from spec text.
//    b. Computed Property name is evaluated
//       - 15.8.19 RS: ClassDefinitionEvaluation, Step 23.
//       - 15.8.15 RS: ClassFieldDefinitionEvaluation, Step 1.
//       - 15.4.5 RS: MethodDefinitionEvaluation, Step 1.
// 5. Static non-field (method/getter/setter/auto-accessor) element decorators are applied
// 6. Non-static non-field (method/getter/setter/auto-accessor) element decorators are applied
// 7. Static field (excl. auto-accessor) element decorators are applied
// 8. Non-static field (excl. auto-accessor) element decorators are applied
// 9. Class decorators are applied
// 10. Class binding is initialized
// 11. Static method extra initializers are evaluated
// 12. Static fields are initialized (incl. extra initializers) and static blocks are evaluated
// 13. Class extra initializers are evaluated
//
// Class constructor evaluation order, as it pertains to this transformer:
//
// 1. Instance method extra initializers are evaluated
// 2. For each instance field/auto-accessor:
//    a. The field is initialized and defined on the instance.
//    b. Extra initializers for the field are evaluated.

// esdecorator.go:47-55 — lexicalEntryKind discriminates the kind of lexical scope entry.
internal enum LexicalEntryKind
{
    Class,
    ClassElement,
    Name,
    Other,
}

// esdecorator.go:57-67 — lexicalEntry represents a single entry in the lexical scope stack used to
// track nested class declarations and their state during transformation.
internal sealed class LexicalEntry
{
    internal LexicalEntryKind kind;
    internal LexicalEntry? next;
    internal ClassInfo? classInfoData;
    internal Node[]? savedPendingExpressions;
    internal IdentifierNode? classThisData;
    internal IdentifierNode? classSuperData;
    internal int depth;
}

// esdecorator.go:69-75 — memberInfo stores decoration-related data for a single class element.
internal sealed class MemberInfo
{
    internal IdentifierNode? memberDecoratorsName; // used in class definition step 4.a
    internal IdentifierNode? memberInitializersName; // used in class definition step 12 and constructor evaluation step 2.a
    internal IdentifierNode? memberExtraInitializersName; // used in class definition step 12 and constructor evaluation step 2.b
    internal IdentifierNode? memberDescriptorName;
}

// esdecorator.go:77-98 — classInfo stores all transformation data for a single decorated class.
internal sealed class ClassInfo
{
    internal Node @class = null!;
    internal IdentifierNode? classDecoratorsName; // used in class definition step 2
    internal IdentifierNode? classDescriptorName; // used in class definition step 10
    internal IdentifierNode? classExtraInitializersName; // used in class definition step 13
    internal IdentifierNode? classThis; // `_classThis`, if needed.
    internal IdentifierNode? classSuper; // `_classSuper`, if needed.
    internal IdentifierNode metadataReference = null!;
    internal global::Ts.Collections.OrderedMap<Node, MemberInfo> memberInfos = new(); // used in class definition step 4.a, 12, and constructor evaluation
    internal IdentifierNode? instanceMethodExtraInitializersName; // used in constructor evaluation step 1
    internal IdentifierNode? staticMethodExtraInitializersName; // used in class definition step 11
    internal List<Node> staticNonFieldDecorationStatements = new();
    internal List<Node> nonStaticNonFieldDecorationStatements = new();
    internal List<Node> staticFieldDecorationStatements = new();
    internal List<Node> nonStaticFieldDecorationStatements = new();
    internal bool hasStaticInitializers;
    internal bool hasNonAmbientInstanceFields;
    internal bool hasStaticPrivateClassElements;
    internal List<Node>? pendingStaticInitializers;
    internal List<Node>? pendingInstanceInitializers;
}

internal sealed partial class ESDecoratorTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;

    private LexicalEntry? top;
    private ClassInfo? classInfoStack;
    private IdentifierNode? classThis;
    private IdentifierNode? classSuper;
    private List<Node>? pendingExpressions;
    private IdentifierNode? outerThis;
    private bool shouldTransformPrivateStaticElementsInFile;

    private NodeVisitor outerThisVisitor = null!;
    private NodeVisitor discardedVisitor = null!;
    private NodeVisitor modifierVisitor = null!;
    private NodeVisitor exportStrippingModifierVisitor = null!;
    private NodeVisitor classElementVisitor = null!;
    private NodeVisitor nonConstructorClassElementVisitor = null!;
    private NodeVisitor constructorClassElementVisitor = null!;
    private NodeVisitor arrayAssignmentVisitor = null!;
    private NodeVisitor objectAssignmentVisitor = null!;
    private NodeVisitor staticOnlyModifierVisitor = null!;
    private NodeVisitor asyncOnlyModifierVisitor = null!;
    private NodeVisitor accessorStrippingModifierVisitor = null!;

    // esdecorator.go:100-163 — newESDecoratorTransformer.
    //
    // When experimentalDecorators is set, the legacy decorator transformer handles all
    // decorators. When targeting ESNext with useDefineForClassFields, there's nothing to
    // transform. In either case every node would be returned unchanged, so skip entirely.
    internal static Transformer? NewESDecoratorTransformer(TransformOptions opts)
    {
        if (opts.CompilerOptions!.ExperimentalDecorators.IsTrue() ||
            (opts.CompilerOptions.GetEmitScriptTarget() >= ScriptTarget.ESNext && opts.CompilerOptions.GetUseDefineForClassFields()))
        {
            return null;
        }
        var tx = new ESDecoratorTransformer(opts);
        tx.NewTransformer(tx.Visit, opts.Context);
        var ec = tx.EmitContext();
        tx.outerThisVisitor = ec.NewNodeVisitor(tx.OuterThisVisit);
        tx.discardedVisitor = ec.NewNodeVisitor(tx.DiscardedValueVisit);
        tx.modifierVisitor = ec.NewNodeVisitor(tx.ModifierVisitorVisit);
        tx.exportStrippingModifierVisitor = ec.NewNodeVisitor(tx.ExportStrippingModifierVisit);
        tx.classElementVisitor = ec.NewNodeVisitor(tx.ClassElementVisitorVisit);
        tx.nonConstructorClassElementVisitor = ec.NewNodeVisitor(tx.NonConstructorClassElementVisit);
        tx.constructorClassElementVisitor = ec.NewNodeVisitor(tx.ConstructorClassElementVisit);
        tx.arrayAssignmentVisitor = ec.NewNodeVisitor(tx.VisitArrayAssignmentElement);
        tx.objectAssignmentVisitor = ec.NewNodeVisitor(tx.VisitObjectAssignmentElement);
        tx.staticOnlyModifierVisitor = ec.NewNodeVisitor(node =>
        {
            if (node.Kind == SyntaxKind.StaticKeyword)
            {
                return node;
            }
            return null;
        });
        tx.asyncOnlyModifierVisitor = ec.NewNodeVisitor(node =>
        {
            if (node.Kind == SyntaxKind.AsyncKeyword)
            {
                return node;
            }
            return null;
        });
        tx.accessorStrippingModifierVisitor = ec.NewNodeVisitor(node =>
        {
            if (node.Kind == SyntaxKind.AccessorKeyword)
            {
                return null;
            }
            return node;
        });
        return tx;
    }

    private ESDecoratorTransformer(TransformOptions opts)
    {
        compilerOptions = opts.CompilerOptions!;
    }

    // esdecorator.go:165-187
    private void UpdateState()
    {
        classInfoStack = null;
        classThis = null;
        classSuper = null;
        if (top == null)
        {
            return;
        }
        switch (top.kind)
        {
            case LexicalEntryKind.Class:
                classInfoStack = top.classInfoData;
                break;
            case LexicalEntryKind.ClassElement:
                classInfoStack = top.next!.classInfoData;
                classThis = top.classThisData;
                classSuper = top.classSuperData;
                break;
            case LexicalEntryKind.Name:
                var grandparent = top.next!.next!.next;
                if (grandparent != null && grandparent.kind == LexicalEntryKind.ClassElement)
                {
                    classInfoStack = grandparent.next!.classInfoData;
                    classThis = grandparent.classThisData;
                    classSuper = grandparent.classSuperData;
                }
                break;
        }
    }

    // esdecorator.go:189-198
    private void EnterClass(ClassInfo? ci)
    {
        top = new LexicalEntry
        {
            kind = LexicalEntryKind.Class,
            next = top,
            classInfoData = ci,
            savedPendingExpressions = pendingExpressions?.ToArray(),
        };
        pendingExpressions = null;
        UpdateState();
    }

    // esdecorator.go:200-205
    private void ExitClass()
    {
        Debug.Assert(top != null && top.kind == LexicalEntryKind.Class, "Incorrect value for top.kind. Expected top.kind to be 'class' but got '", top!.kind, "' instead.");
        pendingExpressions = top.savedPendingExpressions != null ? new List<Node>(top.savedPendingExpressions) : null;
        top = top.next;
        UpdateState();
    }

    // esdecorator.go:207-220
    private void EnterClassElement(Node node)
    {
        Debug.Assert(top != null && top.kind == LexicalEntryKind.Class, "Incorrect value for top.kind. Expected top.kind to be 'class' but got '", top!.kind, "' instead.");
        top = new LexicalEntry
        {
            kind = LexicalEntryKind.ClassElement,
            next = top,
        };
        if (NodeTests.IsClassStaticBlockDeclaration(node) || (NodeTests.IsPropertyDeclaration(node) && AstUtilities.HasStaticModifier(node)))
        {
            if (top.next!.classInfoData != null)
            {
                top.classThisData = top.next.classInfoData!.classThis;
                top.classSuperData = top.next.classInfoData!.classSuper;
            }
        }
        UpdateState();
    }

    // esdecorator.go:222-227
    private void ExitClassElement()
    {
        Debug.Assert(top != null && top.kind == LexicalEntryKind.ClassElement, "Incorrect value for top.kind. Expected top.kind to be 'class-element' but got '", top!.kind, "' instead.");
        Debug.Assert(top.next != null && top.next.kind == LexicalEntryKind.Class, "Incorrect value for top.next.kind. Expected top.next.kind to be 'class' but got '", top.next!.kind, "' instead.");
        top = top.next;
        UpdateState();
    }

    // esdecorator.go:229-236
    private void EnterName()
    {
        Debug.Assert(top != null && top.kind == LexicalEntryKind.ClassElement, "Incorrect value for top.kind. Expected top.kind to be 'class-element' but got '", top!.kind, "' instead.");
        top = new LexicalEntry
        {
            kind = LexicalEntryKind.Name,
            next = top,
        };
        UpdateState();
    }

    // esdecorator.go:238-242
    private void ExitName()
    {
        Debug.Assert(top != null && top.kind == LexicalEntryKind.Name, "Incorrect value for top.kind. Expected top.kind to be 'name' but got '", top!.kind, "' instead.");
        top = top.next;
        UpdateState();
    }

    // esdecorator.go:244-257
    private void EnterOther()
    {
        if (top != null && top.kind == LexicalEntryKind.Other)
        {
            Debug.Assert(pendingExpressions == null || pendingExpressions.Count == 0);
            top.depth++;
        }
        else
        {
            top = new LexicalEntry
            {
                kind = LexicalEntryKind.Other,
                next = top,
                savedPendingExpressions = pendingExpressions?.ToArray(),
            };
            pendingExpressions = null;
            UpdateState();
        }
    }

    // esdecorator.go:259-269
    private void ExitOther()
    {
        Debug.Assert(top != null && top.kind == LexicalEntryKind.Other, "Incorrect value for top.kind. Expected top.kind to be 'other' but got '", top!.kind, "' instead.");
        if (top.depth > 0)
        {
            Debug.Assert(pendingExpressions == null || pendingExpressions.Count == 0);
            top.depth--;
        }
        else
        {
            pendingExpressions = top.savedPendingExpressions != null ? new List<Node>(top.savedPendingExpressions) : null;
            top = top.next;
            UpdateState();
        }
    }

    // esdecorator.go:271-281
    private Node VisitSourceFile(SourceFile node)
    {
        top = null;
        shouldTransformPrivateStaticElementsInFile = false;
        var visited = Visitor().VisitEachChild(node.AsNode())!;
        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        if (shouldTransformPrivateStaticElementsInFile)
        {
            EmitContext().AddEmitFlags(visited, EF.TransformPrivateStaticElements);
            shouldTransformPrivateStaticElementsInFile = false;
        }
        return visited;
    }

    // esdecorator.go:283-296
    private Node? OuterThisVisit(Node n)
    {
        if ((n.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalThis) == 0 && n.Kind != SyntaxKind.ThisKeyword)
        {
            return n;
        }
        if (n.Kind == SyntaxKind.ThisKeyword)
        {
            outerThis ??= Factory().NewUniqueNameEx("_outerThis", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic,
            });
            return outerThis;
        }
        return outerThisVisitor.VisitEachChild(n);
    }

    // esdecorator.go:298-302
    private bool ShouldVisitNode(Node node)
    {
        return (node.SubtreeFacts() & SubtreeFacts.SubtreeContainsDecorators) != 0 ||
            (classThis != null && (node.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalThis) != 0) ||
            (classThis != null && classSuper != null && (node.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalSuper) != 0);
    }

    // esdecorator.go:304-367
    private Node? Visit(Node node)
    {
        if (node.Kind == SyntaxKind.SourceFile)
        {
            return VisitSourceFile(node.AsSourceFile());
        }
        if (!ShouldVisitNode(node))
        {
            return node;
        }
        switch (node.Kind)
        {
            case SyntaxKind.Decorator:
                // Decorators are elided. In Strada, a separate `modifierVisitor` drops decorators
                // before they reach `visitor` via visitEachChild. Here, `visit` serves as both
                // visitors, so decorators from modifier lists reach it directly.
                return null;
            case SyntaxKind.ClassDeclaration:
                return VisitClassDeclaration(node.AsClassDeclaration());
            case SyntaxKind.ClassExpression:
                return VisitClassExpression(node.AsClassExpression());
            case SyntaxKind.Constructor:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.ClassStaticBlockDeclaration:
                Debug.Fail("Not supported outside of a class. Use 'classElementVisitor' instead.");
                return null;
            case SyntaxKind.Parameter:
                return VisitParameterDeclaration(node.AsParameterDeclaration());
            // Support NamedEvaluation to ensure the correct class name for class expressions.
            case SyntaxKind.BinaryExpression:
                return VisitBinaryExpression(node, discarded: false);
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.BindingElement:
                return VisitNamedEvaluationSite(node, node.Initializer()!);
            case SyntaxKind.ExportAssignment:
                return VisitExportAssignment(node);
            case SyntaxKind.ThisKeyword:
                return VisitThisExpression(node);
            case SyntaxKind.ForStatement:
                return VisitForStatement(node);
            case SyntaxKind.ExpressionStatement:
                return VisitExpressionStatement(node);
            case SyntaxKind.ParenthesizedExpression:
                return VisitParenthesizedExpression(node, discarded: false);
            case SyntaxKind.PartiallyEmittedExpression:
                return VisitPartiallyEmittedExpression(node, discarded: false);
            case SyntaxKind.CallExpression:
                return VisitCallExpression(node);
            case SyntaxKind.TaggedTemplateExpression:
                return VisitTaggedTemplateExpression(node);
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
                return VisitPreOrPostfixUnaryExpression(node, discarded: false);
            case SyntaxKind.PropertyAccessExpression:
                return VisitPropertyAccessExpression(node);
            case SyntaxKind.ElementAccessExpression:
                return VisitElementAccessExpression(node);
            case SyntaxKind.ComputedPropertyName:
                return VisitComputedPropertyName(node);
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
                EnterOther();
                var result = Visitor().VisitEachChild(node);
                ExitOther();
                return result;
            default:
                return Visitor().VisitEachChild(node);
        }
    }

    // esdecorator.go:369-374
    private Node? ModifierVisitorVisit(Node node)
    {
        if (node.Kind == SyntaxKind.Decorator)
        {
            return null;
        }
        return node;
    }

    // esdecorator.go:376-393
    private Node? ClassElementVisitorVisit(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
                return VisitConstructorDeclaration(node);
            case SyntaxKind.MethodDeclaration:
                return VisitMethodDeclaration(node);
            case SyntaxKind.GetAccessor:
                return VisitGetAccessorDeclaration(node);
            case SyntaxKind.SetAccessor:
                return VisitSetAccessorDeclaration(node);
            case SyntaxKind.PropertyDeclaration:
                return VisitPropertyDeclaration(node);
            case SyntaxKind.ClassStaticBlockDeclaration:
                return VisitClassStaticBlockDeclaration(node);
            default:
                return Visit(node);
        }
    }

    // esdecorator.go:395-408
    private Node? DiscardedValueVisit(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
                return VisitPreOrPostfixUnaryExpression(node, discarded: true);
            case SyntaxKind.BinaryExpression:
                return VisitBinaryExpression(node, discarded: true);
            case SyntaxKind.ParenthesizedExpression:
                return VisitParenthesizedExpression(node, discarded: true);
            case SyntaxKind.PartiallyEmittedExpression:
                return VisitPartiallyEmittedExpression(node, discarded: true);
            default:
                return Visit(node);
        }
    }

    // esdecorator.go:410-415
    private Node? NonConstructorClassElementVisit(Node node)
    {
        if (NodeTests.IsConstructorDeclaration(node))
        {
            return node; // skip constructors in pass 1
        }
        return ClassElementVisitorVisit(node);
    }

    // esdecorator.go:417-422
    private Node? ConstructorClassElementVisit(Node node)
    {
        if (NodeTests.IsConstructorDeclaration(node))
        {
            return ClassElementVisitorVisit(node);
        }
        return node;
    }

    // esdecorator.go:424-429
    private Node? ExportStrippingModifierVisit(Node node)
    {
        if (node.Kind == SyntaxKind.ExportKeyword)
        {
            return null;
        }
        return ModifierVisitorVisit(node);
    }

    // esdecorator.go:431-462 — getHelperVariableName.
    private static string GetHelperVariableName(global::Ts.Printer.EmitContext ec, Node node)
    {
        var name = node.Name();
        var declarationName = "";
        if (name != null && NodeTests.IsIdentifier(name) && !global::Ts.Transformers.Transformers.IsGeneratedIdentifier(ec, name))
        {
            declarationName = name.Text();
        }
        else if (name != null && NodeTests.IsPrivateIdentifier(name) && !ec.HasAutoGenerateInfo(name))
        {
            var text = name.Text();
            if (text.Length > 1)
            {
                declarationName = text[1..];
            }
        }
        else if (name != null && NodeTests.IsStringLiteral(name) && global::Ts.Scanner.Scanner.IsIdentifierText(name.Text(), LanguageVariant.Standard))
        {
            declarationName = name.Text();
        }
        else if (AstUtilities.IsClassLike(node))
        {
            declarationName = "class";
        }
        else
        {
            declarationName = "member";
        }

        if (NodeTests.IsGetAccessorDeclaration(node))
        {
            declarationName = "get_" + declarationName;
        }
        if (NodeTests.IsSetAccessorDeclaration(node))
        {
            declarationName = "set_" + declarationName;
        }
        if (name != null && NodeTests.IsPrivateIdentifier(name))
        {
            declarationName = "private_" + declarationName;
        }
        if (AstUtilities.IsStatic(node))
        {
            declarationName = "static_" + declarationName;
        }
        return "_" + declarationName;
    }

    // esdecorator.go:464-469
    private IdentifierNode CreateHelperVariable(Node node, string suffix) =>
        Factory().NewUniqueNameEx(
            GetHelperVariableName(EmitContext(), node) + "_" + suffix,
            new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
            });

    // esdecorator.go:471-481
    private Node CreateLet(IdentifierNode name, Node? initializer)
    {
        return Factory().NewVariableStatement(
            null,
            Factory().NewVariableDeclarationList(
                Factory().NewNodeList(
                [
                    Factory().NewVariableDeclaration(name, null, null, initializer),
                ]),
                NodeFlags.Let));
    }
}
