// Ported from tsgo internal/transformers/estransforms/esdecorator.go (lines 483-1230) —
// class-level transform: createClassInfo, transformClassLike, class declaration/expression
// visitors, and the synthetic-constructor worker. Function order follows the Go original.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;

internal sealed partial class ESDecoratorTransformer
{
    // esdecorator.go:483-575
    private ClassInfo CreateClassInfo(Node node)
    {
        var f = Factory();
        var ci = new ClassInfo
        {
            @class = node,
            metadataReference = f.NewUniqueNameEx("_metadata", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            }),
        };

        // Before visiting we perform a first pass to collect information we'll need
        // as we descend.

        // If the class itself is decorated, create a _classThis binding
        if (AstUtilities.NodeIsDecorated(false, node, null, null))
        {
            var needsUniqueClassThis = CoreUtil.Some(node.Members(), member =>
                (AstUtilities.IsPrivateIdentifierClassElementDeclaration(member) || AstUtilities.IsAutoAccessorPropertyDeclaration(member)) && AstUtilities.HasStaticModifier(member));
            // We do not mark _classThis as FileLevel if it may be reused by class private fields, which requires the
            // ability access the captured `_classThis` of outer scopes.
            var flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel;
            if (needsUniqueClassThis)
            {
                flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes;
            }
            ci.classThis = f.NewUniqueNameEx("_classThis", new global::Ts.Printer.AutoGenerateOptions { Flags = flags });
        }

        foreach (var member in node.Members()!)
        {
            if (AstUtilities.IsMethodOrAccessor(member) && AstUtilities.NodeOrChildIsDecorated(false, member, node, null))
            {
                if (AstUtilities.HasStaticModifier(member))
                {
                    if (ci.staticMethodExtraInitializersName == null)
                    {
                        ci.staticMethodExtraInitializersName = f.NewUniqueNameEx("_staticExtraInitializers", new global::Ts.Printer.AutoGenerateOptions
                        {
                            Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
                        });
                        Node renamedClassThis;
                        if (ci.classThis != null)
                        {
                            renamedClassThis = ci.classThis;
                        }
                        else
                        {
                            renamedClassThis = f.NewThisExpression();
                        }
                        var initializer = f.NewRunInitializersHelper(renamedClassThis, ci.staticMethodExtraInitializersName, null);
                        var nameRange = node.Name();
                        if (nameRange != null)
                        {
                            EmitContext().SetSourceMapRange(initializer, nameRange.Loc);
                        }
                        else
                        {
                            EmitContext().SetSourceMapRange(initializer, global::Ts.Transformers.Transformers.MoveRangePastDecorators(node));
                        }
                        (ci.pendingStaticInitializers ??= new List<Node>()).Add(initializer);
                    }
                }
                else
                {
                    if (ci.instanceMethodExtraInitializersName == null)
                    {
                        ci.instanceMethodExtraInitializersName = f.NewUniqueNameEx("_instanceExtraInitializers", new global::Ts.Printer.AutoGenerateOptions
                        {
                            Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
                        });
                        var initializer = f.NewRunInitializersHelper(f.NewThisExpression(), ci.instanceMethodExtraInitializersName, null);
                        var nameRange = node.Name();
                        if (nameRange != null)
                        {
                            EmitContext().SetSourceMapRange(initializer, nameRange.Loc);
                        }
                        else
                        {
                            EmitContext().SetSourceMapRange(initializer, global::Ts.Transformers.Transformers.MoveRangePastDecorators(node));
                        }
                        (ci.pendingInstanceInitializers ??= new List<Node>()).Add(initializer);
                    }
                }
            }

            if (NodeTests.IsClassStaticBlockDeclaration(member))
            {
                if (!NamedEvaluationUtilities.IsClassNamedEvaluationHelperBlock(EmitContext(), member))
                {
                    ci.hasStaticInitializers = true;
                }
            }
            else if (NodeTests.IsPropertyDeclaration(member))
            {
                if (AstUtilities.HasStaticModifier(member))
                {
                    ci.hasStaticInitializers = ci.hasStaticInitializers || member.Initializer() != null || AstUtilities.HasDecorators(member);
                }
                else
                {
                    ci.hasNonAmbientInstanceFields = ci.hasNonAmbientInstanceFields || !AstUtilities.HasSyntacticModifier(member, ModifierFlags.Ambient);
                }
            }

            if ((AstUtilities.IsPrivateIdentifierClassElementDeclaration(member) || AstUtilities.IsAutoAccessorPropertyDeclaration(member)) && AstUtilities.HasStaticModifier(member))
            {
                ci.hasStaticPrivateClassElements = true;
            }

            // exit early if possible
            if (ci.staticMethodExtraInitializersName != null &&
                ci.instanceMethodExtraInitializersName != null &&
                ci.hasStaticInitializers &&
                ci.hasNonAmbientInstanceFields &&
                ci.hasStaticPrivateClassElements)
            {
                break;
            }
        }

        return ci;
    }

    // esdecorator.go:577-990
    private Node TransformClassLike(Node node)
    {
        var f = Factory();
        var ec = EmitContext();

        ec.StartVariableEnvironment();

        // When a class has class decorators we end up transforming it into a statement that would otherwise give it an
        // assigned name. If the class doesn't have an assigned name, we'll give it an assigned name of `""`.
        if (!NamedEvaluationUtilities.ClassHasDeclaredOrExplicitlyAssignedName(ec, node) && AstUtilities.ClassOrConstructorParameterIsDecorated(false, node))
        {
            node = NamedEvaluationUtilities.InjectClassNamedEvaluationHelperBlockIfMissing(ec, node, f.NewStringLiteral("", TokenFlags.None), null);
        }

        var classReference = f.GetLocalNameEx(node, new global::Ts.Printer.AssignedNameOptions());
        var ci = CreateClassInfo(node);
        var classDefinitionStatements = new List<Node>();
        List<Node>? leadingBlockStatements = null;
        List<Node>? trailingBlockStatements = null;
        Node? syntheticConstructor = null;
        NodeList? heritageClauses = null;
        var shouldTransformPrivateStaticElementsInClass = false;

        // 1. Class decorators are evaluated outside the private name scope of the class.
        //
        // - Since class decorators don't have privileged access to private names defined inside the class,
        //   they must be evaluated outside of the class body.
        // - Since a class decorator can replace the class constructor, we must define a variable to keep track
        //   of the mutated class.
        // - Since a class decorator can add extra initializers, we must define a variable to keep track of
        //   extra initializers.
        var classDecorators = TransformAllDecoratorsOfDeclaration(node.Decorators());
        if (classDecorators.Length > 0)
        {
            Debug.Assert(ci.classThis != null);

            ci.classDecoratorsName = f.NewUniqueNameEx("_classDecorators", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            });
            ci.classDescriptorName = f.NewUniqueNameEx("_classDescriptor", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            });
            ci.classExtraInitializersName = f.NewUniqueNameEx("_classExtraInitializers", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            });

            var decoratorsArray = f.NewArrayLiteralExpression(
                f.NewNodeList(classDecorators),
                false);
            classDefinitionStatements.AddRange(
            [
                CreateLet(ci.classDecoratorsName, decoratorsArray),
                CreateLet(ci.classDescriptorName, null),
                CreateLet(ci.classExtraInitializersName, f.NewArrayLiteralExpression(f.NewNodeList([]), false)),
                CreateLet(ci.classThis!, null),
            ]);

            if (classDecorators.Length > 0 && ci.hasStaticPrivateClassElements)
            {
                shouldTransformPrivateStaticElementsInClass = true;
                shouldTransformPrivateStaticElementsInFile = true;
            }
        }

        // 2. ClassHeritage clause is evaluated outside of the private name scope of the class.
        var extendsClause = AstUtilities.GetHeritageClause(node, SyntaxKind.ExtendsKeyword);
        Node? extendsElement = null;
        if (extendsClause != null)
        {
            var hc = extendsClause.AsHeritageClause();
            if (hc.Types != null && hc.Types.Nodes.Length > 0)
            {
                extendsElement = hc.Types.Nodes[0];
            }
        }
        Node? extendsExpression = null;
        if (extendsElement != null)
        {
            extendsExpression = Visitor().VisitNode(extendsElement!.AsExpressionWithTypeArguments().Expression);
        }

        if (extendsExpression != null)
        {
            // Rewrite `super` in static initializers so that we can use the correct `this`.
            ci.classSuper = f.NewUniqueNameEx("_classSuper", new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.FileLevel,
            });

            // Ensure we do not give the class or function an assigned name due to the variable by prefixing it
            // with `0, `.
            var unwrapped = AstUtilities.SkipOuterExpressions(extendsExpression, OuterExpressionKinds.All);
            var safeExtendsExpression = extendsExpression;
            if ((NodeTests.IsClassExpression(unwrapped) && unwrapped.Name() == null) ||
                (NodeTests.IsFunctionExpression(unwrapped) && unwrapped.Name() == null) ||
                NodeTests.IsArrowFunction(unwrapped))
            {
                safeExtendsExpression = f.NewCommaExpression(
                    f.NewNumericLiteral("0", TokenFlags.None),
                    extendsExpression);
            }
            classDefinitionStatements.Add(CreateLet(ci.classSuper, safeExtendsExpression));

            var updatedExtendsElement = f.UpdateExpressionWithTypeArguments(extendsElement!.AsExpressionWithTypeArguments(), ci.classSuper, null);
            var hc2 = extendsClause!.AsHeritageClause();
            var updatedExtendsClause = f.AsNodeFactory().UpdateHeritageClause(hc2, hc2.Token, f.NewNodeList([updatedExtendsElement]));
            heritageClauses = f.NewNodeList([updatedExtendsClause]);
        }

        Node renamedClassThis;
        if (ci.classThis != null)
        {
            renamedClassThis = ci.classThis;
        }
        else
        {
            renamedClassThis = f.NewThisExpression();
        }

        // 3. The name of the class is assigned.
        //
        // If the class did not have a name, the caller should have performed injectClassNamedEvaluationHelperBlockIfMissing
        // prior to calling this function if a name was needed.

        // 4. For each member:
        //    a. Member Decorators are evaluated
        //    b. Computed Property Name is evaluated, if present
        //
        // We visit members in two passes:
        // - The first pass visits methods, accessors, and fields to collect decorators and computed property names.
        // - The second pass visits the constructor to add instance initializers.
        //
        // NOTE: If there are no constructors, but there are instance initializers, a synthetic constructor is added.
        EnterClass(ci);

        (leadingBlockStatements ??= new List<Node>()).Add(CreateMetadata(ci.metadataReference, ci.classSuper));

        // Since the constructor can appear anywhere in the class body and its transform depends on other class elements,
        // we must first visit all non-constructor members, then visit the constructor, all while maintaining document order.
        var members = nonConstructorClassElementVisitor.VisitNodes(node.MemberList())!;
        members = constructorClassElementVisitor.VisitNodes(members)!;

        // Handle pending expressions (computed property names and decorator evaluations)
        if (pendingExpressions != null && pendingExpressions.Count > 0)
        {
            // If a pending expression contains a lexical `this`, we'll need to capture the lexical `this` of the
            // container and transform it in the expression. This ensures we use the correct `this` in the resulting
            // class `static` block. We don't use substitution here because the size of the tree we are visiting
            // is likely to be small and doesn't justify the complexity of introducing substitution.
            outerThis = null;
            foreach (var exprNode in pendingExpressions)
            {
                var expr = exprNode;
                // If a pending expression contains lexical `this`, capture it
                if ((expr.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalThis) != 0)
                {
                    expr = outerThisVisitor.VisitNode(expr)!;
                }
                var statement = f.NewExpressionStatement(expr);
                leadingBlockStatements.Add(statement);
            }
            if (outerThis != null)
            {
                classDefinitionStatements.Insert(0, CreateLet(outerThis, f.NewThisExpression()));
            }
            pendingExpressions = null;
        }
        ExitClass();

        // If there are instance initializers but no constructor, synthesize one
        if ((ci.pendingInstanceInitializers?.Count ?? 0) > 0 && AstUtilities.GetFirstConstructorWithBody(node) == null)
        {
            var initializerStatements = PrepareConstructor(ci);
            if (initializerStatements.Count > 0)
            {
                var isDerivedClass = extendsElement != null && AstUtilities.SkipOuterExpressions(extendsElement!.AsExpressionWithTypeArguments().Expression!, OuterExpressionKinds.All).Kind != SyntaxKind.NullKeyword;
                var constructorStatements = new List<Node>();
                if (isDerivedClass)
                {
                    var spreadArguments = f.NewSpreadElement(f.NewIdentifier("arguments"));
                    var superCall = f.NewCallExpression(f.NewKeywordExpression(SyntaxKind.SuperKeyword), null, null, f.NewNodeList([spreadArguments]), NodeFlags.None);
                    constructorStatements.Add(f.NewExpressionStatement(superCall));
                }
                constructorStatements.AddRange(initializerStatements);
                var constructorBody = f.NewBlock(f.NewNodeList(constructorStatements.ToArray()), true);
                syntheticConstructor = f.AsNodeFactory().NewConstructorDeclaration(null, null, f.NewNodeList([]), null, null, constructorBody);
            }
        }

        // Used in class definition steps 5,7,11
        if (ci.staticMethodExtraInitializersName != null)
        {
            classDefinitionStatements.Add(
                CreateLet(ci.staticMethodExtraInitializersName, f.NewArrayLiteralExpression(f.NewNodeList([]), false)));
        }

        // Used in class definition steps 6,8, and construction
        if (ci.instanceMethodExtraInitializersName != null)
        {
            classDefinitionStatements.Add(
                CreateLet(ci.instanceMethodExtraInitializersName, f.NewArrayLiteralExpression(f.NewNodeList([]), false)));
        }

        // Used in class definition steps 7, 8, 12, and construction.
        // Emit member info variable declarations; the reference implementation emits static member vars first, then non-static.
        if (ci.memberInfos.Size() > 0)
        {
            classDefinitionStatements.AddRange(EmitMemberInfoDeclarations(ci, isStatic: true));
            classDefinitionStatements.AddRange(EmitMemberInfoDeclarations(ci, isStatic: false));
        }

        // 5. Static non-field element decorators are applied
        (leadingBlockStatements ??= new List<Node>()).AddRange(ci.staticNonFieldDecorationStatements);

        // 6. Non-static non-field element decorators are applied
        leadingBlockStatements.AddRange(ci.nonStaticNonFieldDecorationStatements);

        // 7. Static field element decorators are applied
        leadingBlockStatements.AddRange(ci.staticFieldDecorationStatements);

        // 8. Non-static field element decorators are applied
        leadingBlockStatements.AddRange(ci.nonStaticFieldDecorationStatements);

        // 9. Class decorators are applied
        // 10. Class binding is initialized
        //
        // produces:
        //   __esDecorate(null, _classDescriptor = { value: this }, _classDecorators, { kind: "class", name: this.name, metadata }, null, _classExtraInitializers);
        if (ci.classDescriptorName != null && ci.classDecoratorsName != null && ci.classExtraInitializersName != null && ci.classThis != null)
        {
            var valueProperty = f.NewPropertyAssignment(null, f.NewIdentifier("value"), null, null, renamedClassThis);
            var classDescriptor = f.NewObjectLiteralExpression(f.NewNodeList([valueProperty]), false);
            var classDescriptorAssignment = f.NewAssignmentExpression(ci.classDescriptorName, classDescriptor);
            var classNameReference = f.NewPropertyAccessExpression(renamedClassThis, null, f.NewIdentifier("name"), NodeFlags.None);

            var contextObj = f.NewESDecorateClassContextObject(classNameReference, ci.metadataReference);

            var esDecorateHelper = f.NewESDecorateHelper(
                f.NewToken(SyntaxKind.NullKeyword),
                classDescriptorAssignment,
                ci.classDecoratorsName,
                contextObj,
                f.NewToken(SyntaxKind.NullKeyword),
                ci.classExtraInitializersName);
            var esDecorateStatement = f.NewExpressionStatement(esDecorateHelper);
            ec.SetSourceMapRange(esDecorateStatement, global::Ts.Transformers.Transformers.MoveRangePastDecorators(node));
            leadingBlockStatements.Add(esDecorateStatement);

            // produces:
            //   C = _classThis = _classDescriptor.value;
            var classDescriptorValueRef = f.NewPropertyAccessExpression(ci.classDescriptorName, null, f.NewIdentifier("value"), NodeFlags.None);
            var classThisAssignment = f.NewAssignmentExpression(ci.classThis, classDescriptorValueRef);
            var classReferenceAssignment = f.NewAssignmentExpression(classReference, classThisAssignment);
            leadingBlockStatements.Add(f.NewExpressionStatement(classReferenceAssignment));
        }

        // produces:
        //   if (metadata) Object.defineProperty(C, Symbol.metadata, { configurable: true, writable: true, value: metadata });
        (leadingBlockStatements ??= new List<Node>()).Add(CreateSymbolMetadata(renamedClassThis, ci.metadataReference));

        // 11. Static extra initializers
        // 12. Static fields are initialized
        if ((ci.pendingStaticInitializers?.Count ?? 0) > 0)
        {
            trailingBlockStatements ??= new List<Node>();
            foreach (var initializer in ci.pendingStaticInitializers!)
            {
                var initializerStatement = f.NewExpressionStatement(initializer);
                ec.SetSourceMapRange(initializerStatement, ec.SourceMapRange(initializer));
                trailingBlockStatements.Add(initializerStatement);
            }
            ci.pendingStaticInitializers = null;
        }

        // 13. Class extra initializers
        if (ci.classExtraInitializersName != null)
        {
            var runClassInitializersHelper = f.NewRunInitializersHelper(renamedClassThis, ci.classExtraInitializersName, null);
            var runClassInitializersStatement = f.NewExpressionStatement(runClassInitializersHelper);
            if (node.Name() != null)
            {
                ec.SetSourceMapRange(runClassInitializersStatement, node.Name()!.Loc);
            }
            else
            {
                ec.SetSourceMapRange(runClassInitializersStatement, global::Ts.Transformers.Transformers.MoveRangePastDecorators(node));
            }
            (trailingBlockStatements ??= new List<Node>()).Add(runClassInitializersStatement);
        }

        // If there are no other static initializers to run, combine the leading and trailing block statements
        if (leadingBlockStatements.Count > 0 && (trailingBlockStatements?.Count ?? 0) > 0 && !ci.hasStaticInitializers)
        {
            leadingBlockStatements.AddRange(trailingBlockStatements!);
            trailingBlockStatements = null;
        }

        // prepare a leading `static {}` block, if necessary
        //
        // produces:
        //   class C {
        //       static { ... }
        //       ...
        //   }
        Node? leadingStaticBlock = null;
        if (leadingBlockStatements.Count > 0)
        {
            leadingStaticBlock = f.AsNodeFactory().NewClassStaticBlockDeclaration(
                null,
                f.NewBlock(f.NewNodeList(leadingBlockStatements.ToArray()), true));
        }

        if (leadingStaticBlock != null && shouldTransformPrivateStaticElementsInClass)
        {
            // We use EFTransformPrivateStaticElements as a marker on a class static block
            // to inform the classFields transform that it shouldn't rename `this` to `_classThis` in the
            // transformed class static block.
            ec.SetEmitFlags(leadingStaticBlock, EF.TransformPrivateStaticElements);
        }

        // prepare a trailing `static {}` block, if necessary
        //
        // produces:
        //   class C {
        //       ...
        //       static { ... }
        //   }
        Node? trailingStaticBlock = null;
        if ((trailingBlockStatements?.Count ?? 0) > 0)
        {
            trailingStaticBlock = f.AsNodeFactory().NewClassStaticBlockDeclaration(
                null,
                f.NewBlock(f.NewNodeList(trailingBlockStatements!.ToArray()), true));
        }

        // Assemble new members list
        if (leadingStaticBlock != null || syntheticConstructor != null || trailingStaticBlock != null)
        {
            var newMembers = new List<Node>(members.Nodes.Length + 3);

            // Find the existing NamedEvaluation helper block index
            var existingNamedEvaluationHelperBlockIndex = -1;
            for (var i = 0; i < members.Nodes.Length; i++)
            {
                if (NamedEvaluationUtilities.IsClassNamedEvaluationHelperBlock(ec, members.Nodes[i]))
                {
                    existingNamedEvaluationHelperBlockIndex = i;
                    break;
                }
            }

            // add the leading `static {}` block
            if (leadingStaticBlock != null)
            {
                // add the `static {}` block after any existing NamedEvaluation helper block, if one exists.
                newMembers.AddRange(members.Nodes[..(existingNamedEvaluationHelperBlockIndex + 1)]);
                newMembers.Add(leadingStaticBlock);
                newMembers.AddRange(members.Nodes[(existingNamedEvaluationHelperBlockIndex + 1)..]);
            }
            else
            {
                newMembers.AddRange(members.Nodes);
            }

            // append the synthetic constructor, if necessary
            if (syntheticConstructor != null)
            {
                newMembers.Add(syntheticConstructor);
            }

            // append a trailing `static {}` block, if necessary
            if (trailingStaticBlock != null)
            {
                newMembers.Add(trailingStaticBlock);
            }

            var membersList = f.NewNodeList(newMembers.ToArray());
            membersList.Loc = members.Loc;
            members = membersList;
        }

        var lexicalEnvironment = ec.EndVariableEnvironment();

        Node classExpression;
        if (classDecorators.Length > 0)
        {
            classExpression = f.AsNodeFactory().NewClassExpression(null, null, null, heritageClauses, members);
            ec.SetOriginal(classExpression, node);
            if (ci.classThis != null)
            {
                classExpression = InjectClassThisAssignmentIfMissing(ec, f, classExpression, ci.classThis);
            }

            // We use `var` instead of `let` so we can leverage NamedEvaluation to define the class name
            // and still be able to ensure it is initialized prior to any use in `static {}`.

            // produces:
            //   (() => {
            //       let _classDecorators = [...];
            //       let _classDescriptor;
            //       let _classExtraInitializers = [];
            //       let _classThis;
            //       ...
            //       var C = class {
            //           static {
            //               __esDecorate(null, _classDescriptor = { value: this }, _classDecorators, ...);
            //               C = _classThis = _classDescriptor.value;
            //           }
            //           static x = 1;
            //           static y = C.x; // `C` will already be defined here.
            //           static { ... }
            //       };
            //       return C;
            //   })();

            var classReferenceDeclaration = f.NewVariableDeclaration(classReference, null, null, classExpression);
            var classReferenceVarDeclList = f.NewVariableDeclarationList(f.NewNodeList([classReferenceDeclaration]), NodeFlags.None);
            Node returnExpr;
            if (ci.classThis != null)
            {
                returnExpr = f.NewAssignmentExpression(classReference, ci.classThis);
            }
            else
            {
                returnExpr = classReference;
            }
            classDefinitionStatements.Add(f.NewVariableStatement(null, classReferenceVarDeclList));
            classDefinitionStatements.Add(f.NewReturnStatement(returnExpr));
        }
        else
        {
            // produces:
            //   return <classExpression>;
            classExpression = f.AsNodeFactory().NewClassExpression(null, node.Name(), null, heritageClauses, members);
            ec.SetOriginal(classExpression, node);
            classDefinitionStatements.Add(f.NewReturnStatement(classExpression));
        }

        if (shouldTransformPrivateStaticElementsInClass)
        {
            ec.AddEmitFlags(classExpression, EF.TransformPrivateStaticElements);
            foreach (var member in classExpression.Members()!)
            {
                if ((AstUtilities.IsPrivateIdentifierClassElementDeclaration(member) || AstUtilities.IsAutoAccessorPropertyDeclaration(member)) && AstUtilities.HasStaticModifier(member))
                {
                    ec.AddEmitFlags(member, EF.TransformPrivateStaticElements);
                }
            }
        }

        var mergedStatements = ec.MergeEnvironment(classDefinitionStatements.ToArray(), lexicalEnvironment);
        return f.NewImmediatelyInvokedArrowFunction(mergedStatements!);
    }

    // esdecorator.go:992-1012 — Generates let declarations for member decorator info variables,
    // filtered by static/non-static.
    private List<Node> EmitMemberInfoDeclarations(ClassInfo ci, bool isStatic)
    {
        var f = Factory();
        var stmts = new List<Node>();
        foreach (var (member, mi) in ci.memberInfos.Entries())
        {
            if (AstUtilities.IsStatic(member) != isStatic)
            {
                continue;
            }
            stmts.Add(CreateLet(mi.memberDecoratorsName!, null));
            if (mi.memberInitializersName != null)
            {
                stmts.Add(CreateLet(mi.memberInitializersName, f.NewArrayLiteralExpression(f.NewNodeList([]), false)));
            }
            if (mi.memberExtraInitializersName != null)
            {
                stmts.Add(CreateLet(mi.memberExtraInitializersName, f.NewArrayLiteralExpression(f.NewNodeList([]), false)));
            }
            if (mi.memberDescriptorName != null)
            {
                stmts.Add(CreateLet(mi.memberDescriptorName, null));
            }
        }
        return stmts;
    }

    // esdecorator.go:1014-1017 — isDecoratedClassLike.
    private static bool IsDecoratedClassLike(Node node)
    {
        return AstUtilities.ClassOrConstructorParameterIsDecorated(false, node) ||
            AstUtilities.ChildIsDecorated(false, node, null);
    }

    // esdecorator.go:1019-1105
    private Node? VisitClassDeclaration(ClassDeclaration node)
    {
        if (IsDecoratedClassLike(node.AsNode()))
        {
            var f = Factory();
            var ec = EmitContext();
            var statements = new List<Node>();

            var originalClass = ec.MostOriginal(node.AsNode())!;
            if (!AstUtilities.IsClassLike(originalClass))
            {
                originalClass = node.AsNode();
            }
            Node className;
            if (originalClass.Name() != null)
            {
                className = f.NewStringLiteralFromNode(originalClass.Name()!);
            }
            else
            {
                className = f.NewStringLiteral("default", TokenFlags.None);
            }

            var isExport = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export);
            var isDefault = AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Default);

            var classNode = node.AsNode();
            if (node.Name() == null)
            {
                classNode = NamedEvaluationUtilities.InjectClassNamedEvaluationHelperBlockIfMissing(ec, classNode, className, null);
            }

            if (isExport && isDefault)
            {
                var iife = TransformClassLike(classNode);
                if (classNode.Name() != null)
                {
                    // produces:
                    //   let C = (() => { ... })();
                    //   export default C;
                    var varDecl = f.NewVariableDeclaration(f.GetLocalName(classNode), null, null, iife);
                    ec.SetOriginal(varDecl, classNode);
                    var varDecls = f.NewVariableDeclarationList(f.NewNodeList([varDecl]), NodeFlags.Let);
                    var varStatement = f.NewVariableStatement(null, varDecls);
                    statements.Add(varStatement);

                    var exportStatement = f.NewExportDefault(f.GetDeclarationName(classNode));
                    ec.SetOriginal(exportStatement, classNode);
                    ec.AssignCommentRange(exportStatement, classNode);
                    ec.SetSourceMapRange(exportStatement, global::Ts.Transformers.Transformers.MoveRangePastDecorators(classNode));
                    statements.Add(exportStatement);
                }
                else
                {
                    // produces:
                    //   export default (() => { ... })();
                    var exportStatement = f.NewExportDefault(iife);
                    ec.SetOriginal(exportStatement, classNode);
                    ec.AssignCommentRange(exportStatement, classNode);
                    ec.SetSourceMapRange(exportStatement, global::Ts.Transformers.Transformers.MoveRangePastDecorators(classNode));
                    statements.Add(exportStatement);
                }
            }
            else
            {
                Debug.Assert(classNode.Name() != null, "A class declaration that is not a default export must have a name.");
                // produces:
                //   let C = (() => { ... })();
                var iife = TransformClassLike(classNode);
                var modifiers = exportStrippingModifierVisitor.VisitModifiers(classNode.Modifiers());

                var declName = f.GetLocalNameEx(classNode, new global::Ts.Printer.AssignedNameOptions { AllowSourceMaps = true });
                var varDecl = f.NewVariableDeclaration(declName, null, null, iife);
                ec.SetOriginal(varDecl, classNode);
                var varDecls = f.NewVariableDeclarationList(f.NewNodeList([varDecl]), NodeFlags.Let);
                var varStatement = f.NewVariableStatement(modifiers, varDecls);
                ec.SetOriginal(varStatement, classNode);
                ec.AssignCommentRange(varStatement, classNode);
                statements.Add(varStatement);

                if (isExport)
                {
                    // produces:
                    //   export { C };
                    var exportStatement = f.NewExternalModuleExport(declName);
                    ec.SetOriginal(exportStatement, classNode);
                    statements.Add(exportStatement);
                }
            }

            return global::Ts.Transformers.Transformers.SingleOrMany(statements.ToArray(), f);
        }

        // Non-decorated class
        var nonDecoratedModifiers = modifierVisitor.VisitModifiers(node.Modifiers());
        var heritageClauses = Visitor().VisitNodes(node.HeritageClauses);
        EnterClass(null);
        var members = classElementVisitor.VisitNodes(node.Members)!;
        ExitClass();
        return Factory().UpdateClassDeclaration(node, nonDecoratedModifiers, node.Name(), null, heritageClauses, members);
    }

    // esdecorator.go:1107-1120
    private Node? VisitClassExpression(ClassExpression node)
    {
        if (IsDecoratedClassLike(node.AsNode()))
        {
            var iife = TransformClassLike(node.AsNode());
            EmitContext().SetOriginal(iife, node.AsNode());
            return iife;
        }

        var modifiers = modifierVisitor.VisitModifiers(node.Modifiers());
        var heritageClauses = Visitor().VisitNodes(node.HeritageClauses);
        EnterClass(null);
        var members = classElementVisitor.VisitNodes(node.Members)!;
        ExitClass();
        return Factory().AsNodeFactory().UpdateClassExpression(node, modifiers, node.Name(), null, heritageClauses, members);
    }

    // esdecorator.go:1122-1136
    private List<Node> PrepareConstructor(ClassInfo ci)
    {
        // Decorated instance members can add "extra" initializers to the instance. If a class contains any instance
        // fields, we'll inject the `__runInitializers()` call for these extra initializers into the initializer of
        // the first class member that will be initialized. However, if the class does not contain any fields that
        // we can piggyback on, we need to synthesize a `__runInitializers()` call in the constructor instead.
        if ((ci.pendingInstanceInitializers?.Count ?? 0) == 0)
        {
            return new List<Node>();
        }
        var f = Factory();
        var statements = new List<Node>
        {
            f.NewExpressionStatement(f.InlineExpressions(ci.pendingInstanceInitializers!.ToArray())),
        };
        ci.pendingInstanceInitializers = null;
        return statements;
    }

    // esdecorator.go:1138-1182
    private List<Node> TransformConstructorBodyWorker(List<Node> statementsOut, Node[] statementsIn, int statementOffset, int[] superPath, int superPathDepth, List<Node> initializerStatements)
    {
        var superStatementIndex = superPath[superPathDepth];
        // Visit statements before super
        if (superStatementIndex > statementOffset)
        {
            for (var i = statementOffset; i < superStatementIndex; i++)
            {
                statementsOut.Add(Visitor().VisitNode(statementsIn[i])!);
            }
        }

        var superStatement = statementsIn[superStatementIndex];
        if (NodeTests.IsTryStatement(superStatement))
        {
            // Recurse into try block
            var tryBlockNode = superStatement.AsTryStatement().TryBlock;
            var tryBlock = tryBlockNode!.AsBlock();
            var tryBlockStatements = TransformConstructorBodyWorker(new List<Node>(), tryBlock.Statements!.Nodes, 0, superPath, superPathDepth + 1, initializerStatements);

            var newTryBlock = Factory().NewBlock(Factory().NewNodeList(tryBlockStatements.ToArray()), true);
            // Use the original try block's range even though the statements may differ due to
            // injected initializer statements. This preserves source map fidelity for the enclosing
            // try statement.
            newTryBlock.Loc = tryBlockNode.Loc;

            Node? catchClause = null;
            if (superStatement.AsTryStatement().CatchClause != null)
            {
                catchClause = Visitor().VisitNode(superStatement.AsTryStatement().CatchClause);
            }
            Node? finallyBlock = null;
            if (superStatement.AsTryStatement().FinallyBlock != null)
            {
                finallyBlock = Visitor().VisitNode(superStatement.AsTryStatement().FinallyBlock);
            }
            var updated = Factory().AsNodeFactory().UpdateTryStatement(superStatement.AsTryStatement(), newTryBlock, catchClause, finallyBlock);
            statementsOut.Add(updated);
        }
        else
        {
            statementsOut.Add(Visitor().VisitNode(superStatement)!);
            statementsOut.AddRange(initializerStatements);
        }

        // Visit statements after super
        if (superStatementIndex + 1 < statementsIn.Length)
        {
            for (var i = superStatementIndex + 1; i < statementsIn.Length; i++)
            {
                statementsOut.Add(Visitor().VisitNode(statementsIn[i])!);
            }
        }
        return statementsOut;
    }

    // esdecorator.go:1184-1220
    private Node? VisitConstructorDeclaration(Node node)
    {
        EnterClassElement(node);
        var modifiers = modifierVisitor.VisitModifiers(node.Modifiers());
        var parameters = Visitor().VisitNodes(node.ParameterList());

        Node? body = null;
        var ctor = node.AsConstructorDeclaration();
        if (ctor.Body != null && classInfoStack != null)
        {
            // If there are instance extra initializers we need to add them to the body along with any
            // field initializers
            var initializerStatements = PrepareConstructor(classInfoStack);
            if (initializerStatements.Count > 0)
            {
                var stmts = new List<Node>();
                var prologue = Factory().SplitStandardPrologue(ctor.Body!.AsBlock().Statements!.Nodes, out var rest);
                stmts.AddRange(prologue);

                var superStatementIndices = global::Ts.Transformers.Transformers.FindSuperStatementIndexPath(rest, 0);
                if (superStatementIndices != null && superStatementIndices.Length > 0)
                {
                    stmts = TransformConstructorBodyWorker(stmts, rest, 0, superStatementIndices, 0, initializerStatements);
                }
                else
                {
                    stmts.AddRange(initializerStatements);
                    var visited = Visitor().VisitSlice(rest).Nodes;
                    stmts.AddRange(visited!);
                }

                body = Factory().NewBlock(Factory().NewNodeList(stmts.ToArray()), true);
                EmitContext().SetOriginal(body, ctor.Body.AsNode());
                body.Loc = ctor.Body.Loc;
            }
        }

        if (body == null)
        {
            body = Visitor().VisitNode(ctor.Body!.AsNode());
        }
        ExitClassElement();
        return Factory().AsNodeFactory().UpdateConstructorDeclaration(ctor, modifiers, null, parameters, null, null, body);
    }

    // esdecorator.go:1222-1230
    private Node FinishClassElement(Node updated, Node original)
    {
        if (updated != original)
        {
            // While we emit the source map for the node after skipping decorators and modifiers,
            // we need to emit the comments for the original range.
            EmitContext().AssignCommentRange(updated, original);
            EmitContext().SetSourceMapRange(updated, global::Ts.Transformers.Transformers.MoveRangePastDecorators(original));
        }
        return updated;
    }
}
