// Ported from tsgo internal/checker/grammarchecks.go (grammarchecks.go:1-2202), plan/03 §4.2 grammar wave.
// The syntax-grammar gatekeeper: CheckGrammarSourceFile is the first thing CheckSourceFile calls,
// and this file carries the full checkGrammar* family + the grammarErrorOn*/grammarErrorAtPos
// reporters. Faithful per-function translation: same function order as Go, same branch structure;
// Go comments and `// !!!` markers preserved.
//
// §1.5: diagnostics via `using Diag = global::Ts.Diagnostics.Diagnostics;`; scanner via
// `global::Ts.Scanner.Scanner.X` (Scanner is a sibling namespace, so it is fully qualified);
// the no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used directly, as in
// relater/inference/flow/statements. The `ast.NewDiagnostic` factory maps to `Diagnostic.NewDiagnostic`.
//
// A handful of AST-layer utilities this file references are not yet landed in Ts.Ast
// (NodeCanBeDecorated / GetAllAccessorDeclarationsForDeclaration / HasDecorators /
// CanHaveModifiers / CanHaveIllegalModifiers + the AllAccessorDeclarations shape). Those belong to
// the AST layer (out of scope for this wave); they are single-dedup stubbed in Checker.Grammar.Deps.cs
// and reached only on deep decorator/accessor paths.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // grammarchecks.go:18
    internal bool GrammarErrorOnFirstToken(Node node, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        if (!HasParseDiagnostics(sourceFile))
        {
            var span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(sourceFile, node.Pos);
            AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, span, message, args));
            return true;
        }
        return false;
    }

    // grammarchecks.go:28
    internal bool GrammarErrorAtPos(Node nodeForSourceFile, int start, int length, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(nodeForSourceFile)!;
        if (!HasParseDiagnostics(sourceFile))
        {
            AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, new TextRange(start, start + length), message, args));
            return true;
        }
        return false;
    }

    // grammarchecks.go:37
    internal bool GrammarErrorOnNode(Node node, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        if (!HasParseDiagnostics(sourceFile))
        {
            Error(node, message, args);
            return true;
        }
        return false;
    }

    // grammarchecks.go:46
    internal bool GrammarErrorOnNodeSkippedOnNoEmit(Node node, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        if (!HasParseDiagnostics(sourceFile))
        {
            var d = NewDiagnosticForNode(node, message, args);
            d.SetSkippedOnNoEmit();
            AddDiagnostic(d);
            return true;
        }
        return false;
    }

    // grammarchecks.go:57
    internal static Node? GetIdentifierFromEntityNameExpression(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                return node;
            case SyntaxKind.PropertyAccessExpression:
                return node.AsPropertyAccessExpression().Name();
            default:
                return null;
        }
    }

    // grammarchecks.go:68
    internal bool CheckGrammarRegularExpressionLiteral(RegularExpressionLiteral node)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(node.AsNode())!;
        if (!HasParseDiagnostics(sourceFile))
        {
            Diagnostic? lastError = null;
            if (regExpScanner == null)
            {
                regExpScanner = new global::Ts.Scanner.Scanner();
            }
            regExpScanner.SetScriptTarget(languageVersion);
            regExpScanner.SetLanguageVariant(sourceFile.LanguageVariant);
            regExpScanner.SetOnError((global::Ts.Diagnostics.Message message, int start, int length, object?[] args) =>
            {
                if (message.Category == global::Ts.Diagnostics.Category.Message && lastError != null && start == lastError.Pos() && length == lastError.Len())
                {
                    // For providing spelling suggestions.
                    var err = Diagnostic.NewDiagnostic(null, new TextRange(start, start + length), message, args);
                    lastError.AddRelatedInfo(err);
                }
                else if (lastError == null || start != lastError.Pos())
                {
                    lastError = Diagnostic.NewDiagnostic(sourceFile, new TextRange(start, start + length), message, args);
                    AddDiagnostic(lastError);
                }
            });
            regExpScanner.SetText(sourceFile.Text());
            regExpScanner.ResetTokenState(node.AsNode().Pos);
            regExpScanner.Scan();
            var tokenIsRegularExpressionLiteral = regExpScanner.ReScanSlashToken(true) == SyntaxKind.RegularExpressionLiteral;
            regExpScanner.SetText("");
            regExpScanner.SetOnError(null!); // PORT-NOTE: Go clears the callback with nil; SetOnError param non-nullable.
            global::Ts.Core.Debug.Assert(tokenIsRegularExpressionLiteral);
            return lastError != null;
        }
        return false;
    }

    // grammarchecks.go:99
    internal bool CheckGrammarPrivateIdentifierExpression(PrivateIdentifier privId)
    {
        var privIdAsNode = privId.AsNode();
        if (AstUtilities.GetContainingClass(privId.AsNode()) == null)
        {
            return GrammarErrorOnNode(privId.AsNode(), Diag.Private_identifiers_are_not_allowed_outside_class_bodies);
        }

        if (!NodeTests.IsForInStatement(privId.Parent!))
        {
            if (!AstUtilities.IsExpressionNode(privIdAsNode))
            {
                return GrammarErrorOnNode(privIdAsNode, Diag.Private_identifiers_are_only_allowed_in_class_bodies_and_may_only_be_used_as_part_of_a_class_member_declaration_property_access_or_on_the_left_hand_side_of_an_in_expression);
            }

            var isInOperation = NodeTests.IsBinaryExpression(privId.Parent!) && privId.Parent!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.InKeyword;
            if (GetSymbolForPrivateIdentifierExpression(privIdAsNode) == null && !isInOperation)
            {
                return GrammarErrorOnNode(privIdAsNode, Diag.Cannot_find_name_0, privId.Text);
            }
        }

        return false;
    }

    // grammarchecks.go:119
    internal bool CheckGrammarMappedType(MappedTypeNode node)
    {
        if (node.Members != null && node.Members.Nodes.Length > 0)
        {
            return GrammarErrorOnNode(node.Members.Nodes[0], Diag.A_mapped_type_may_not_declare_properties_or_methods);
        }
        return false;
    }

    // grammarchecks.go:126
    internal bool CheckGrammarDecorator(Decorator decorator)
    {
        var sourceFile = AstUtilities.GetSourceFileOfNode(decorator.AsNode())!;
        if (!HasParseDiagnostics(sourceFile))
        {
            var node = decorator.Expression!.AsNode();

            // DecoratorParenthesizedExpression :
            //   `(` Expression `)`

            if (NodeTests.IsParenthesizedExpression(node))
            {
                return false;
            }

            var canHaveCallExpression = true;
            Node? errorNode = null;
            for (; ; )
            {
                // Allow TS syntax such as non-null assertions and instantiation expressions
                if (NodeTests.IsExpressionWithTypeArguments(node) || NodeTests.IsNonNullExpression(node))
                {
                    node = node.Expression()!;
                    continue;
                }

                // DecoratorCallExpression :
                //   DecoratorMemberExpression Arguments

                if (NodeTests.IsCallExpression(node))
                {
                    var callExpr = node.AsCallExpression();
                    if (!canHaveCallExpression)
                    {
                        errorNode = node;
                    }
                    if (callExpr.QuestionDotToken != null)
                    {
                        // Even if we already have an error node, error at the `?.` token since it appears earlier.
                        errorNode = callExpr.QuestionDotToken;
                    }
                    node = callExpr.Expression!.AsNode();
                    canHaveCallExpression = false;
                    continue;
                }

                // DecoratorMemberExpression :
                //   IdentifierReference
                //   DecoratorMemberExpression `.` IdentifierName
                //   DecoratorMemberExpression `.` PrivateIdentifier

                if (NodeTests.IsPropertyAccessExpression(node))
                {
                    var propertyAccessExpr = node.AsPropertyAccessExpression();
                    if (propertyAccessExpr.QuestionDotToken != null)
                    {
                        // Even if we already have an error node, error at the `?.` token since it appears earlier.
                        errorNode = propertyAccessExpr.QuestionDotToken;
                    }
                    node = propertyAccessExpr.Expression!.AsNode();
                    canHaveCallExpression = false;
                    continue;
                }

                if (!NodeTests.IsIdentifier(node))
                {
                    // Even if we already have an error node, error at this node since it appears earlier.
                    errorNode = node;
                }

                break;
            }

            if (errorNode != null)
            {
                var err = Error(decorator.Expression!.AsNode(), Diag.Expression_must_be_enclosed_in_parentheses_to_be_used_as_a_decorator)!;
                err.AddRelatedInfo(CreateDiagnosticForNode(errorNode, Diag.Invalid_syntax_in_decorator));
                return true;
            }
        }

        return false;
    }

    // grammarchecks.go:198
    internal bool CheckGrammarExportDeclaration(ExportDeclaration node)
    {
        if (node.IsTypeOnly && node.ExportClause != null && node.ExportClause.Kind == SyntaxKind.NamedExports)
        {
            return CheckGrammarTypeOnlyNamedImportsOrExports(node.ExportClause);
        }
        return false;
    }

    // grammarchecks.go:205
    internal bool CheckGrammarModuleElementContext(Node node, global::Ts.Diagnostics.Message errorMessage)
    {
        var isInAppropriateContext = node.Parent!.Kind == SyntaxKind.SourceFile || node.Parent.Kind == SyntaxKind.ModuleBlock || node.Parent.Kind == SyntaxKind.ModuleDeclaration;
        if (!isInAppropriateContext)
        {
            GrammarErrorOnFirstToken(node, errorMessage);
        }
        return !isInAppropriateContext;
    }

    // grammarchecks.go:213
    internal bool CheckGrammarModifiers(Node node /*Union[HasModifiers, HasDecorators, HasIllegalModifiers, HasIllegalDecorators]*/)
    {
        if (node.Modifiers() == null)
        {
            return false;
        }
        if (ReportObviousDecoratorErrors(node) || ReportObviousModifierErrors(node))
        {
            return true;
        }
        // PORT-NOTE: ast.IsThisParameter inlined (not yet on AstUtilities; IsThisIdentifier host is AstUtilities).
        if (NodeTests.IsParameterDeclaration(node) && node.Name() != null && JSDocReparseAstGaps.IsThisIdentifier(node.Name()))
        {
            return GrammarErrorOnFirstToken(node, Diag.Neither_decorators_nor_modifiers_may_be_applied_to_this_parameters);
        }
        var blockScopeKind = NodeFlags.None;
        if (NodeTests.IsVariableStatement(node))
        {
            blockScopeKind = node.AsVariableStatement().DeclarationList!.Flags & NodeFlags.BlockScoped;
        }
        Node? lastStatic = null;
        Node? lastDeclare = null;
        Node? lastAsync = null;
        Node? lastOverride = null;
        Node? firstDecorator = null;
        var flags = ModifierFlags.None;
        var sawExportBeforeDecorators = false;
        // We parse decorators and modifiers in four contiguous chunks:
        // [...leadingDecorators, ...leadingModifiers, ...trailingDecorators, ...trailingModifiers]. It is an error to
        // have both leading and trailing decorators.
        var hasLeadingDecorators = false;
        var modifiers = node.ModifierNodes();
        foreach (var modifier in modifiers.OrEmpty())
        {
            if (NodeTests.IsDecorator(modifier))
            {
                if (!AstUtilities.NodeCanBeDecorated(legacyDecorators, node, node.Parent!, node.Parent!.Parent!))
                {
                    if (node.Kind == SyntaxKind.MethodDeclaration && !AstUtilities.NodeIsPresent(node.Body()))
                    {
                        return GrammarErrorOnFirstToken(node, Diag.A_decorator_can_only_decorate_a_method_implementation_not_an_overload);
                    }
                    else
                    {
                        return GrammarErrorOnFirstToken(node, Diag.Decorators_are_not_valid_here);
                    }
                }
                else if (legacyDecorators && (node.Kind == SyntaxKind.GetAccessor || node.Kind == SyntaxKind.SetAccessor))
                {
                    var accessors = GetAllAccessorDeclarationsForDeclaration(node, GetSymbolOfDeclaration(node)!.Declarations!);
                    if (AstUtilities.HasDecorators(accessors.FirstAccessor!) && node == accessors.SecondAccessor)
                    {
                        return GrammarErrorOnFirstToken(node, Diag.Decorators_cannot_be_applied_to_multiple_get_Slashset_accessors_of_the_same_name);
                    }
                }

                // if we've seen any modifiers aside from `export`, `default`, or another decorator, then this is an invalid position
                if ((flags & ~(ModifierFlags.ExportDefault | ModifierFlags.Decorator)) != 0)
                {
                    return GrammarErrorOnNode(modifier, Diag.Decorators_are_not_valid_here);
                }

                // if we've already seen leading decorators and leading modifiers, then trailing decorators are an invalid position
                if (hasLeadingDecorators && (flags & ModifierFlags.Modifier) != 0)
                {
                    if (firstDecorator == null)
                    {
                        throw new InvalidOperationException("Expected firstDecorator to be set");
                    }
                    var sourceFile = AstUtilities.GetSourceFileOfNode(modifier)!;
                    if (!HasParseDiagnostics(sourceFile))
                    {
                        var err = Error(modifier, Diag.Decorators_may_not_appear_after_export_or_export_default_if_they_also_appear_before_export)!;
                        err.AddRelatedInfo(CreateDiagnosticForNode(firstDecorator, Diag.Decorator_used_before_export_here));
                        return true;
                    }
                    return false;
                }

                flags |= ModifierFlags.Decorator;

                // if we have not yet seen a modifier, then these are leading decorators
                if ((flags & ModifierFlags.Modifier) == 0)
                {
                    hasLeadingDecorators = true;
                }
                else if ((flags & ModifierFlags.Export) != 0)
                {
                    sawExportBeforeDecorators = true;
                }

                if (firstDecorator == null)
                {
                    firstDecorator = modifier;
                }
            }
            else
            {
                if (modifier.Kind != SyntaxKind.ReadonlyKeyword)
                {
                    if (node.Kind == SyntaxKind.PropertySignature || node.Kind == SyntaxKind.MethodSignature)
                    {
                        return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_type_member, global::Ts.Scanner.Scanner.TokenToString(modifier.Kind));
                    }
                    if (node.Kind == SyntaxKind.IndexSignature && (modifier.Kind != SyntaxKind.StaticKeyword || !AstUtilities.IsClassLike(node.Parent!)))
                    {
                        return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_an_index_signature, global::Ts.Scanner.Scanner.TokenToString(modifier.Kind));
                    }
                }
                if (modifier.Kind != SyntaxKind.InKeyword && modifier.Kind != SyntaxKind.OutKeyword && modifier.Kind != SyntaxKind.ConstKeyword)
                {
                    if (node.Kind == SyntaxKind.TypeParameter)
                    {
                        return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_type_parameter, global::Ts.Scanner.Scanner.TokenToString(modifier.Kind));
                    }
                }
                switch (modifier.Kind)
                {
                    case SyntaxKind.ConstKeyword:
                        if (node.Kind != SyntaxKind.EnumDeclaration && node.Kind != SyntaxKind.TypeParameter)
                        {
                            return GrammarErrorOnNode(node, Diag.A_class_member_cannot_have_the_0_keyword, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.ConstKeyword));
                        }
                        var constParent = node.Parent!;
                        if (node.Kind == SyntaxKind.TypeParameter)
                        {
                            if (!(AstUtilities.IsFunctionLikeDeclaration(constParent) || AstUtilities.IsClassLike(constParent) ||
                                NodeTests.IsFunctionTypeNode(constParent) || NodeTests.IsConstructorTypeNode(constParent) ||
                                NodeTests.IsCallSignatureDeclaration(constParent) || NodeTests.IsConstructSignatureDeclaration(constParent) ||
                                NodeTests.IsMethodSignatureDeclaration(constParent)))
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_can_only_appear_on_a_type_parameter_of_a_function_method_or_class, global::Ts.Scanner.Scanner.TokenToString(modifier.Kind));
                            }
                        }
                        break;
                    case SyntaxKind.OverrideKeyword:
                        // If node.kind === SyntaxKind.Parameter, checkParameter reports an error if it's not a parameter property.
                        if ((flags & ModifierFlags.Override) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "override");
                        }
                        else if ((flags & ModifierFlags.Ambient) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "override", "declare");
                        }
                        else if ((flags & ModifierFlags.Readonly) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "override", "readonly");
                        }
                        else if ((flags & ModifierFlags.Accessor) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "override", "accessor");
                        }
                        else if ((flags & ModifierFlags.Async) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "override", "async");
                        }
                        flags |= ModifierFlags.Override;
                        lastOverride = modifier;
                        break;

                    case SyntaxKind.PublicKeyword:
                    case SyntaxKind.ProtectedKeyword:
                    case SyntaxKind.PrivateKeyword:
                        var text = VisibilityToString(AstUtilities.ModifierToFlag(modifier.Kind));

                        if ((flags & ModifierFlags.AccessibilityModifier) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.Accessibility_modifier_already_seen);
                        }
                        else if ((flags & ModifierFlags.Override) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, text, "override");
                        }
                        else if ((flags & ModifierFlags.Static) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, text, "static");
                        }
                        else if ((flags & ModifierFlags.Accessor) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, text, "accessor");
                        }
                        else if ((flags & ModifierFlags.Readonly) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, text, "readonly");
                        }
                        else if ((flags & ModifierFlags.Async) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, text, "async");
                        }
                        else if (node.Parent!.Kind == SyntaxKind.ModuleBlock || node.Parent.Kind == SyntaxKind.SourceFile)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_module_or_namespace_element, text);
                        }
                        else if ((flags & ModifierFlags.Abstract) != 0)
                        {
                            if (modifier.Kind == SyntaxKind.PrivateKeyword)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, text, "abstract");
                            }
                            else if ((modifier.Flags & NodeFlags.Reparsed) == 0)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, text, "abstract");
                            }
                        }
                        else if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(node))
                        {
                            return GrammarErrorOnNode(modifier, Diag.An_accessibility_modifier_cannot_be_used_with_a_private_identifier);
                        }
                        flags |= AstUtilities.ModifierToFlag(modifier.Kind);
                        break;
                    case SyntaxKind.StaticKeyword:
                        if ((flags & ModifierFlags.Static) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "static");
                        }
                        else if ((flags & ModifierFlags.Readonly) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "static", "readonly");
                        }
                        else if ((flags & ModifierFlags.Async) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "static", "async");
                        }
                        else if ((flags & ModifierFlags.Accessor) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "static", "accessor");
                        }
                        else if (node.Parent!.Kind == SyntaxKind.ModuleBlock || node.Parent.Kind == SyntaxKind.SourceFile)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_module_or_namespace_element, "static");
                        }
                        else if (node.Kind == SyntaxKind.Parameter)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_parameter, "static");
                        }
                        else if ((flags & ModifierFlags.Abstract) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "static", "abstract");
                        }
                        else if ((flags & ModifierFlags.Override) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "static", "override");
                        }
                        flags |= ModifierFlags.Static;
                        lastStatic = modifier;
                        break;
                    case SyntaxKind.AccessorKeyword:
                        if ((flags & ModifierFlags.Accessor) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "accessor");
                        }
                        else if ((flags & ModifierFlags.Readonly) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "accessor", "readonly");
                        }
                        else if ((flags & ModifierFlags.Ambient) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "accessor", "declare");
                        }
                        else if (node.Kind != SyntaxKind.PropertyDeclaration)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_accessor_modifier_can_only_appear_on_a_property_declaration);
                        }

                        flags |= ModifierFlags.Accessor;
                        break;
                    case SyntaxKind.ReadonlyKeyword:
                        if ((flags & ModifierFlags.Readonly) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "readonly");
                        }
                        else if (node.Kind != SyntaxKind.PropertyDeclaration && node.Kind != SyntaxKind.PropertySignature && node.Kind != SyntaxKind.IndexSignature && node.Kind != SyntaxKind.Parameter)
                        {
                            // If node.kind === SyntaxKind.Parameter, checkParameter reports an error if it's not a parameter property.
                            return GrammarErrorOnNode(modifier, Diag.X_readonly_modifier_can_only_appear_on_a_property_declaration_or_index_signature);
                        }
                        else if ((flags & ModifierFlags.Accessor) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "readonly", "accessor");
                        }
                        flags |= ModifierFlags.Readonly;
                        break;
                    case SyntaxKind.ExportKeyword:
                        if (compilerOptions.VerbatimModuleSyntax == Tristate.True && (node.Flags & NodeFlags.Ambient) == 0 && node.Kind != SyntaxKind.TypeAliasDeclaration && node.Kind != SyntaxKind.InterfaceDeclaration && node.Kind != SyntaxKind.ModuleDeclaration && node.Parent!.Kind == SyntaxKind.SourceFile && program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
                        {
                            return GrammarErrorOnNode(modifier, Diag.A_top_level_export_modifier_cannot_be_used_on_value_declarations_in_a_CommonJS_module_when_verbatimModuleSyntax_is_enabled);
                        }
                        if ((flags & ModifierFlags.Export) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "export");
                        }
                        else if ((flags & ModifierFlags.Ambient) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "export", "declare");
                        }
                        else if ((flags & ModifierFlags.Abstract) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "export", "abstract");
                        }
                        else if ((flags & ModifierFlags.Async) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "export", "async");
                        }
                        else if (AstUtilities.IsClassLike(node.Parent!) && !NodeTests.IsJSTypeAliasDeclaration(node))
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_class_elements_of_this_kind, "export");
                        }
                        else if (node.Kind == SyntaxKind.Parameter)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_parameter, "export");
                        }
                        else if (blockScopeKind == NodeFlags.Using)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_using_declaration, "export");
                        }
                        else if (blockScopeKind == NodeFlags.AwaitUsing)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_an_await_using_declaration, "export");
                        }
                        flags |= ModifierFlags.Export;
                        break;
                    case SyntaxKind.DefaultKeyword:
                        Node? container;
                        if (node.Parent!.Kind == SyntaxKind.SourceFile)
                        {
                            container = node.Parent;
                        }
                        else
                        {
                            container = node.Parent.Parent;
                        }
                        if (container!.Kind == SyntaxKind.ModuleDeclaration && !AstUtilities.IsAmbientModule(container))
                        {
                            return GrammarErrorOnNode(modifier, Diag.A_default_export_can_only_be_used_in_an_ECMAScript_style_module);
                        }
                        else if (blockScopeKind == NodeFlags.Using)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_using_declaration, "default");
                        }
                        else if (blockScopeKind == NodeFlags.AwaitUsing)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_an_await_using_declaration, "default");
                        }
                        else if ((flags & ModifierFlags.Export) == 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "export", "default");
                        }
                        else if (sawExportBeforeDecorators)
                        {
                            return GrammarErrorOnNode(firstDecorator!, Diag.Decorators_are_not_valid_here);
                        }

                        flags |= ModifierFlags.Default;
                        break;
                    case SyntaxKind.DeclareKeyword:
                        if ((flags & ModifierFlags.Ambient) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "declare");
                        }
                        else if ((flags & ModifierFlags.Async) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_in_an_ambient_context, "async");
                        }
                        else if ((flags & ModifierFlags.Override) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_in_an_ambient_context, "override");
                        }
                        else if (AstUtilities.IsClassLike(node.Parent!) && !NodeTests.IsPropertyDeclaration(node))
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_class_elements_of_this_kind, "declare");
                        }
                        else if (node.Kind == SyntaxKind.Parameter)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_parameter, "declare");
                        }
                        else if (blockScopeKind == NodeFlags.Using)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_using_declaration, "declare");
                        }
                        else if (blockScopeKind == NodeFlags.AwaitUsing)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_an_await_using_declaration, "declare");
                        }
                        else if ((node.Parent!.Flags & NodeFlags.Ambient) != 0 && node.Parent.Kind == SyntaxKind.ModuleBlock)
                        {
                            return GrammarErrorOnNode(modifier, Diag.A_declare_modifier_cannot_be_used_in_an_already_ambient_context);
                        }
                        else if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(node))
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_a_private_identifier, "declare");
                        }
                        else if ((flags & ModifierFlags.Accessor) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "declare", "accessor");
                        }
                        flags |= ModifierFlags.Ambient;
                        lastDeclare = modifier;
                        break;
                    case SyntaxKind.AbstractKeyword:
                        if ((flags & ModifierFlags.Abstract) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "abstract");
                        }
                        if (node.Kind != SyntaxKind.ClassDeclaration && node.Kind != SyntaxKind.ConstructorType)
                        {
                            if (node.Kind != SyntaxKind.MethodDeclaration && node.Kind != SyntaxKind.PropertyDeclaration && node.Kind != SyntaxKind.GetAccessor && node.Kind != SyntaxKind.SetAccessor)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_abstract_modifier_can_only_appear_on_a_class_method_or_property_declaration);
                            }
                            if (!(node.Parent!.Kind == SyntaxKind.ClassDeclaration && AstUtilities.HasSyntacticModifier(node.Parent, ModifierFlags.Abstract)))
                            {
                                global::Ts.Diagnostics.Message message;
                                if (node.Kind == SyntaxKind.PropertyDeclaration)
                                {
                                    message = Diag.Abstract_properties_can_only_appear_within_an_abstract_class;
                                }
                                else
                                {
                                    message = Diag.Abstract_methods_can_only_appear_within_an_abstract_class;
                                }
                                return GrammarErrorOnNode(modifier, message);
                            }
                            if ((flags & ModifierFlags.Static) != 0)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "static", "abstract");
                            }
                            if ((flags & ModifierFlags.Private) != 0)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "private", "abstract");
                            }
                            if ((flags & ModifierFlags.Async) != 0 && lastAsync != null)
                            {
                                return GrammarErrorOnNode(lastAsync, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "async", "abstract");
                            }
                            if ((flags & ModifierFlags.Override) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "abstract", "override");
                            }
                            if ((flags & ModifierFlags.Accessor) != 0 && (modifier.Flags & NodeFlags.Reparsed) == 0)
                            {
                                return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "abstract", "accessor");
                            }
                        }
                        var abstractName = node.Name();
                        if (abstractName != null && abstractName.Kind == SyntaxKind.PrivateIdentifier)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_a_private_identifier, "abstract");
                        }

                        flags |= ModifierFlags.Abstract;
                        break;
                    case SyntaxKind.AsyncKeyword:
                        if ((flags & ModifierFlags.Async) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, "async");
                        }
                        else if ((flags & ModifierFlags.Ambient) != 0 || (node.Parent!.Flags & NodeFlags.Ambient) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_in_an_ambient_context, "async");
                        }
                        else if (node.Kind == SyntaxKind.Parameter)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_appear_on_a_parameter, "async");
                        }
                        if ((flags & ModifierFlags.Abstract) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_cannot_be_used_with_1_modifier, "async", "abstract");
                        }
                        flags |= ModifierFlags.Async;
                        lastAsync = modifier;
                        break;
                    case SyntaxKind.InKeyword:
                    case SyntaxKind.OutKeyword:
                        ModifierFlags inOutFlag;
                        if (modifier.Kind == SyntaxKind.InKeyword)
                        {
                            inOutFlag = ModifierFlags.In;
                        }
                        else
                        {
                            inOutFlag = ModifierFlags.Out;
                        }
                        string inOutText;
                        if (modifier.Kind == SyntaxKind.InKeyword)
                        {
                            inOutText = "in";
                        }
                        else
                        {
                            inOutText = "out";
                        }
                        var inOutParent = node.Parent;
                        // PORT-NOTE: ast.IsTypeOrJSTypeAliasDeclaration inlined (trivial 2-Kind check; not yet on AstUtilities).
                        if (node.Kind != SyntaxKind.TypeParameter || (inOutParent != null && !(NodeTests.IsInterfaceDeclaration(inOutParent) || AstUtilities.IsClassLike(inOutParent) || inOutParent.Kind == SyntaxKind.TypeAliasDeclaration || inOutParent.Kind == SyntaxKind.JSTypeAliasDeclaration)))
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_can_only_appear_on_a_type_parameter_of_a_class_interface_or_type_alias, inOutText);
                        }
                        if ((flags & inOutFlag) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_already_seen, inOutText);
                        }
                        if ((inOutFlag & ModifierFlags.In) != 0 && (flags & ModifierFlags.Out) != 0)
                        {
                            return GrammarErrorOnNode(modifier, Diag.X_0_modifier_must_precede_1_modifier, "in", "out");
                        }
                        flags |= inOutFlag;
                        break;
                }
            }
        }

        if (node.Kind == SyntaxKind.Constructor)
        {
            if ((flags & ModifierFlags.Static) != 0)
            {
                return GrammarErrorOnNode(lastStatic!, Diag.X_0_modifier_cannot_appear_on_a_constructor_declaration, "static");
            }
            if ((flags & ModifierFlags.Override) != 0)
            {
                return GrammarErrorOnNode(lastOverride!, Diag.X_0_modifier_cannot_appear_on_a_constructor_declaration, "override");
            }
            if ((flags & ModifierFlags.Async) != 0)
            {
                return GrammarErrorOnNode(lastAsync!, Diag.X_0_modifier_cannot_appear_on_a_constructor_declaration, "async");
            }
            return false;
        }
        else if ((node.Kind == SyntaxKind.ImportDeclaration || node.Kind == SyntaxKind.JSImportDeclaration || node.Kind == SyntaxKind.ImportEqualsDeclaration) && (flags & ModifierFlags.Ambient) != 0)
        {
            return GrammarErrorOnNode(lastDeclare!, Diag.A_0_modifier_cannot_be_used_with_an_import_declaration, "declare");
        }
        else if (node.Kind == SyntaxKind.Parameter && (flags & ModifierFlags.ParameterPropertyModifier) != 0 && AstUtilities.IsBindingPattern(node.Name()))
        {
            return GrammarErrorOnNode(node, Diag.A_parameter_property_may_not_be_declared_using_a_binding_pattern);
        }
        else if (node.Kind == SyntaxKind.Parameter && (flags & ModifierFlags.ParameterPropertyModifier) != 0 && node.AsParameterDeclaration().DotDotDotToken != null)
        {
            return GrammarErrorOnNode(node, Diag.A_parameter_property_cannot_be_declared_using_a_rest_parameter);
        }
        if ((flags & ModifierFlags.Async) != 0)
        {
            return CheckGrammarAsyncModifier(node, lastAsync!);
        }
        return false;
    }

    // grammarchecks.go:570
    internal bool ReportObviousModifierErrors(Node node)
    {
        var modifier = FindFirstIllegalModifier(node);
        if (modifier == null)
        {
            return false;
        }
        return GrammarErrorOnFirstToken(modifier, Diag.Modifiers_cannot_appear_here);
    }

    // grammarchecks.go:578
    internal Node? FindFirstModifierExcept(Node node, SyntaxKind allowedModifier)
    {
        var modifier = CoreUtil.Find(node.ModifierNodes(), AstUtilities.IsModifier);
        if (modifier != null && modifier.Kind != allowedModifier)
        {
            return modifier;
        }
        return null;
    }

    // grammarchecks.go:586
    internal Node? FindFirstIllegalModifier(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.Constructor:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.Parameter:
            case SyntaxKind.TypeParameter:
            case SyntaxKind.JSTypeAliasDeclaration:
                return null;
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.NamespaceExportDeclaration:
            case SyntaxKind.MissingDeclaration:
                return CoreUtil.Find(node.ModifierNodes(), AstUtilities.IsModifier);
            default:
                if (node.Parent!.Kind == SyntaxKind.ModuleBlock || node.Parent.Kind == SyntaxKind.SourceFile)
                {
                    return null;
                }
                switch (node.Kind)
                {
                    case SyntaxKind.FunctionDeclaration:
                        return FindFirstModifierExcept(node, SyntaxKind.AsyncKeyword);
                    case SyntaxKind.ClassDeclaration:
                    case SyntaxKind.ConstructorType:
                        return FindFirstModifierExcept(node, SyntaxKind.AbstractKeyword);
                    case SyntaxKind.ClassExpression:
                    case SyntaxKind.InterfaceDeclaration:
                    case SyntaxKind.TypeAliasDeclaration:
                        return CoreUtil.Find(node.ModifierNodes(), AstUtilities.IsModifier);
                    case SyntaxKind.VariableStatement:
                        if ((node.AsVariableStatement().DeclarationList!.Flags & NodeFlags.Using) != 0)
                        {
                            return FindFirstModifierExcept(node, SyntaxKind.AwaitKeyword);
                        }
                        return CoreUtil.Find(node.ModifierNodes(), AstUtilities.IsModifier);
                    case SyntaxKind.EnumDeclaration:
                        return FindFirstModifierExcept(node, SyntaxKind.ConstKeyword);
                    default:
                        throw new InvalidOperationException("Unhandled case in findFirstIllegalModifier.");
                }
        }
    }

    // grammarchecks.go:641
    internal bool ReportObviousDecoratorErrors(Node node)
    {
        var decorator = FindFirstIllegalDecorator(node);
        if (decorator == null)
        {
            return false;
        }
        return GrammarErrorOnFirstToken(decorator, Diag.Decorators_are_not_valid_here);
    }

    // grammarchecks.go:649
    internal Node? FindFirstIllegalDecorator(Node node)
    {
        if (AstUtilities.CanHaveIllegalDecorators(node))
        {
            var decorator = CoreUtil.Find(node.ModifierNodes(), NodeTests.IsDecorator);
            return decorator;
        }
        else
        {
            return null;
        }
    }

    // grammarchecks.go:658
    internal bool CheckGrammarAsyncModifier(Node node, Node asyncModifier)
    {
        switch (node.Kind)
        {
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                return false;
        }

        return GrammarErrorOnNode(asyncModifier, Diag.X_0_modifier_cannot_be_used_here, "async");
    }

    // grammarchecks.go:670
    internal bool CheckGrammarForDisallowedTrailingComma(NodeList? list, global::Ts.Diagnostics.Message diag)
    {
        if (list != null && list.HasTrailingComma())
        {
            return GrammarErrorAtPos(list.Nodes[0], list.End - ",".Length, ",".Length, diag);
        }
        return false;
    }

    // grammarchecks.go:677
    internal bool CheckGrammarTypeParameterList(NodeList? typeParameters, SourceFile file)
    {
        if (typeParameters != null && typeParameters.Nodes.Length == 0)
        {
            var start = typeParameters.Pos - "<".Length;
            var end = global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), typeParameters.End) + ">".Length;
            return GrammarErrorAtPos(file.AsNode(), start, end - start, Diag.Type_parameter_list_cannot_be_empty);
        }
        return false;
    }

    // grammarchecks.go:686
    internal bool CheckGrammarParameterList(NodeList parameters)
    {
        var seenOptionalParameter = false;
        var parameterCount = parameters.Nodes.Length;

        for (var i = 0; i < parameterCount; i++)
        {
            var parameter = parameters.Nodes[i].AsParameterDeclaration();
            if (parameter.DotDotDotToken != null)
            {
                if (i != parameterCount - 1)
                {
                    return GrammarErrorOnNode(parameter.DotDotDotToken, Diag.A_rest_parameter_must_be_last_in_a_parameter_list);
                }
                if ((parameter.Flags & NodeFlags.Ambient) == 0)
                {
                    CheckGrammarForDisallowedTrailingComma(parameters, Diag.A_rest_parameter_or_binding_pattern_may_not_have_a_trailing_comma);
                }

                if (parameter.QuestionToken != null)
                {
                    return GrammarErrorOnNode(parameter.QuestionToken, Diag.A_rest_parameter_cannot_be_optional);
                }

                if (parameter.Initializer != null)
                {
                    return GrammarErrorOnNode(parameter.Name()!, Diag.A_rest_parameter_cannot_have_an_initializer);
                }
            }
            else if (IsOptionalDeclaration(parameter.AsNode()))
            {
                seenOptionalParameter = true;
                // A reparsed '?' token indicates a bracketed name in @param tag
                if (parameter.QuestionToken != null && (parameter.QuestionToken.Flags & NodeFlags.Reparsed) == 0 && parameter.Initializer != null)
                {
                    return GrammarErrorOnNode(parameter.Name()!, Diag.Parameter_cannot_have_question_mark_and_initializer);
                }
            }
            else if (seenOptionalParameter && parameter.Initializer == null)
            {
                return GrammarErrorOnNode(parameter.Name()!, Diag.A_required_parameter_cannot_follow_an_optional_parameter);
            }
        }

        return false;
    }

    // grammarchecks.go:721
    internal bool CheckGrammarForUseStrictSimpleParameterList(Node node)
    {
        if (languageVersion >= ScriptTarget.ES2016)
        {
            var body = node.Body();
            Node? useStrictDirective = null;
            if (body != null && NodeTests.IsBlock(body))
            {
                useStrictDirective = global::Ts.Binder.Binder.FindUseStrictPrologue(AstUtilities.GetSourceFileOfNode(node)!, body.Statements()!);
            }
            if (useStrictDirective != null)
            {
                var nonSimpleParameters = CoreUtil.Filter(node.Parameters(), (Node n) =>
                {
                    var parameter = n.AsParameterDeclaration();
                    return parameter.Initializer != null || AstUtilities.IsBindingPattern(parameter.Name()) || IsRestParameter(parameter.AsNode());
                })!;
                if (nonSimpleParameters.Length != 0)
                {
                    foreach (var parameter in nonSimpleParameters)
                    {
                        var err = Error(parameter, Diag.This_parameter_is_not_allowed_with_use_strict_directive)!;
                        err.AddRelatedInfo(CreateDiagnosticForNode(useStrictDirective, Diag.X_use_strict_directive_used_here));
                    }

                    var errUseStrict = Error(useStrictDirective, Diag.X_use_strict_directive_cannot_be_used_with_non_simple_parameter_list)!;
                    for (var index = 0; index < nonSimpleParameters.Length; index++)
                    {
                        var parameter = nonSimpleParameters[index];
                        global::Ts.Diagnostics.Message relatedMessage;
                        if (index == 0)
                        {
                            relatedMessage = Diag.Non_simple_parameter_declared_here;
                        }
                        else
                        {
                            relatedMessage = Diag.X_and_here;
                        }
                        errUseStrict.AddRelatedInfo(CreateDiagnosticForNode(parameter, relatedMessage));
                    }

                    return true;
                }
            }
        }
        return false;
    }

    // grammarchecks.go:757
    internal bool CheckGrammarFunctionLikeDeclaration(Node node)
    {
        // Prevent cascading error by short-circuit
        var file = AstUtilities.GetSourceFileOfNode(node)!;
        var funcData = node.FunctionLikeData()!;
        return CheckGrammarModifiers(node) || CheckGrammarTypeParameterList(funcData.TypeParameters, file) ||
            CheckGrammarParameterList(funcData.Parameters!) || CheckGrammarArrowFunction(node, file) ||
            (AstUtilities.IsFunctionLikeDeclaration(node) && CheckGrammarForUseStrictSimpleParameterList(node));
    }

    // grammarchecks.go:766
    internal bool CheckGrammarClassLikeDeclaration(Node node)
    {
        var file = AstUtilities.GetSourceFileOfNode(node)!;
        return CheckGrammarClassDeclarationHeritageClauses(node, file) || CheckGrammarTypeParameterList(node.TypeParameterList(), file);
    }

    // grammarchecks.go:771
    internal bool CheckGrammarArrowFunction(Node node, SourceFile file)
    {
        if (!NodeTests.IsArrowFunction(node))
        {
            return false;
        }

        var arrowFunc = node.AsArrowFunction();
        var typeParameters = arrowFunc.TypeParameters;
        if (typeParameters != null)
        {
            var typeParamNodes = typeParameters.Nodes;
            var hasConstraint = typeParamNodes.Length > 0 && typeParamNodes[0].AsTypeParameterDeclaration().Constraint != null;
            if (!(typeParamNodes.Length > 1 || typeParameters.HasTrailingComma() || hasConstraint))
            {
                if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(file.FileName(), new[] { global::Ts.TsPath.TsPath.ExtensionMts, global::Ts.TsPath.TsPath.ExtensionCts }))
                {
                    // TODO(danielr): should we return early here?
                    GrammarErrorOnNode(typeParameters.Nodes[0], Diag.This_syntax_is_reserved_in_files_with_the_mts_or_cts_extension_Add_a_trailing_comma_or_explicit_constraint);
                }
            }
        }

        var equalsGreaterThanToken = arrowFunc.EqualsGreaterThanToken!;
        var startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, equalsGreaterThanToken.Pos);
        var endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, equalsGreaterThanToken.End);
        return startLine != endLine && GrammarErrorOnNode(equalsGreaterThanToken, Diag.Line_terminator_not_permitted_before_arrow);
    }

    // grammarchecks.go:795
    internal bool CheckGrammarIndexSignatureParameters(IndexSignatureDeclaration node)
    {
        var paramNodes = node.Parameters!.Nodes;

        if (paramNodes.Length == 0)
        {
            return GrammarErrorOnNode(node.AsNode(), Diag.An_index_signature_must_have_exactly_one_parameter);
        }

        var parameter = paramNodes[0].AsParameterDeclaration();
        if (paramNodes.Length != 1)
        {
            return GrammarErrorOnNode(parameter.Name()!, Diag.An_index_signature_must_have_exactly_one_parameter);
        }

        CheckGrammarForDisallowedTrailingComma(node.Parameters, Diag.An_index_signature_cannot_have_a_trailing_comma);
        if (parameter.DotDotDotToken != null)
        {
            return GrammarErrorOnNode(parameter.DotDotDotToken, Diag.An_index_signature_cannot_have_a_rest_parameter);
        }
        if (parameter.Modifiers() != null)
        {
            return GrammarErrorOnNode(parameter.Name()!, Diag.An_index_signature_parameter_cannot_have_an_accessibility_modifier);
        }
        if (parameter.QuestionToken != null)
        {
            return GrammarErrorOnNode(parameter.QuestionToken, Diag.An_index_signature_parameter_cannot_have_a_question_mark);
        }
        if (parameter.Initializer != null)
        {
            return GrammarErrorOnNode(parameter.Name()!, Diag.An_index_signature_parameter_cannot_have_an_initializer);
        }
        var typeNode = parameter.Type;
        if (typeNode == null)
        {
            return GrammarErrorOnNode(parameter.Name()!, Diag.An_index_signature_parameter_must_have_a_type_annotation);
        }
        var t = GetTypeFromTypeNode(typeNode);
        if (SomeType(t, (Type t) =>
        {
            return (t.flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0;
        }) || IsGenericType(t))
        {
            return GrammarErrorOnNode(parameter.Name()!, Diag.An_index_signature_parameter_type_cannot_be_a_literal_type_or_generic_type_Consider_using_a_mapped_object_type_instead);
        }
        if (!EveryType(t, IsValidIndexKeyType))
        {
            return GrammarErrorOnNode(parameter.Name()!, Diag.An_index_signature_parameter_type_must_be_string_number_symbol_or_a_template_literal_type);
        }
        if (node.Type == null)
        {
            return GrammarErrorOnNode(node.AsNode(), Diag.An_index_signature_must_have_a_type_annotation);
        }
        return false;
    }

    // grammarchecks.go:839
    internal bool CheckGrammarIndexSignature(IndexSignatureDeclaration node)
    {
        // Prevent cascading error by short-circuit
        return CheckGrammarModifiers(node.AsNode()) || CheckGrammarIndexSignatureParameters(node);
    }

    // grammarchecks.go:844
    internal bool CheckGrammarForAtLeastOneTypeArgument(Node node, NodeList? typeArguments)
    {
        if (typeArguments != null && typeArguments.Nodes.Length == 0)
        {
            var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
            var start = typeArguments.Pos - "<".Length;
            var end = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), typeArguments.End) + ">".Length;
            return GrammarErrorAtPos(sourceFile.AsNode(), start, end - start, Diag.Type_argument_list_cannot_be_empty);
        }
        return false;
    }

    // grammarchecks.go:854
    internal bool CheckGrammarTypeArguments(Node node, NodeList? typeArguments)
    {
        return CheckGrammarForDisallowedTrailingComma(typeArguments, Diag.Trailing_comma_not_allowed) || CheckGrammarForAtLeastOneTypeArgument(node, typeArguments);
    }

    // grammarchecks.go:858
    internal bool CheckGrammarTaggedTemplateChain(TaggedTemplateExpression node)
    {
        if (node.QuestionDotToken != null || (node.Flags & NodeFlags.OptionalChain) != 0)
        {
            return GrammarErrorOnNode(node.Template!.AsNode(), Diag.Tagged_template_expressions_are_not_permitted_in_an_optional_chain);
        }
        return false;
    }

    // grammarchecks.go:865
    internal bool CheckGrammarHeritageClause(HeritageClause node)
    {
        var types = node.Types;
        if (CheckGrammarForDisallowedTrailingComma(types, Diag.Trailing_comma_not_allowed))
        {
            return true;
        }
        if (types != null && types.Nodes.Length == 0)
        {
            var listType = global::Ts.Scanner.Scanner.TokenToString(node.Token);
            // TODO(danielr): why not error on the token?
            return GrammarErrorAtPos(node.AsNode(), types.Pos, 0, Diag.X_0_list_cannot_be_empty, listType);
        }

        foreach (var typeNode in types!.Nodes) // nolint:modernize
        {
            if (CheckGrammarExpressionWithTypeArguments(typeNode))
            {
                return true;
            }
        }
        return false;
    }

    // grammarchecks.go:884
    internal bool CheckGrammarExpressionWithTypeArguments(Node node /*Union[ExpressionWithTypeArguments, TypeQuery]*/)
    {
        if (NodeTests.IsExpressionWithTypeArguments(node) && node.Expression()!.Kind == SyntaxKind.ImportKeyword && node.TypeArgumentList() != null)
        {
            return GrammarErrorOnNode(node, Diag.This_use_of_import_is_invalid_import_calls_can_be_written_but_they_must_have_parentheses_and_cannot_have_type_arguments);
        }
        return CheckGrammarTypeArguments(node, node.TypeArgumentList());
    }

    // grammarchecks.go:891
    internal bool CheckGrammarClassDeclarationHeritageClauses(Node node /*ClassLikeDeclaration*/, SourceFile file)
    {
        var seenExtendsClause = false;
        var seenImplementsClause = false;

        var classLikeData = node.ClassLikeData()!;

        if (!CheckGrammarModifiers(node) && classLikeData.HeritageClauses != null)
        {
            foreach (var heritageClauseNode in classLikeData.HeritageClauses.Nodes)
            {
                var heritageClause = heritageClauseNode.AsHeritageClause();
                if (heritageClause.Token == SyntaxKind.ExtendsKeyword)
                {
                    if (seenExtendsClause)
                    {
                        return GrammarErrorOnFirstToken(heritageClauseNode, Diag.X_extends_clause_already_seen);
                    }

                    if (seenImplementsClause)
                    {
                        return GrammarErrorOnFirstToken(heritageClauseNode, Diag.X_extends_clause_must_precede_implements_clause);
                    }

                    var typeNodes = heritageClause.Types!.Nodes;
                    if (typeNodes.Length > 1)
                    {
                        return GrammarErrorOnFirstToken(typeNodes[1], Diag.Classes_can_only_extend_a_single_class);
                    }

                    if (typeNodes.Length > 0)
                    {
                        foreach (var j in node.EagerJSDoc(file).OrEmpty())
                        {
                            if (j.AsJSDoc().Tags == null)
                            {
                                continue;
                            }
                            foreach (var tag in j.AsJSDoc().Tags!.Nodes)
                            {
                                if (tag.Kind == SyntaxKind.JSDocAugmentsTag)
                                {
                                    var target = typeNodes[0].AsExpressionWithTypeArguments();
                                    var source = tag.ClassName()!.AsExpressionWithTypeArguments();
                                    var targetName = GetIdentifierFromEntityNameExpression(target.Expression!.AsNode());
                                    var sourceName = GetIdentifierFromEntityNameExpression(source.Expression!.AsNode());
                                    if (targetName != null && sourceName != null && targetName.Text() != sourceName.Text())
                                    {
                                        return GrammarErrorOnNode(sourceName, Diag.JSDoc_0_1_does_not_match_the_extends_2_clause, tag.TagName()!.Text(), sourceName.Text(), targetName.Text());
                                    }
                                }
                            }
                        }
                    }
                    seenExtendsClause = true;
                }
                else
                {
                    if (heritageClause.Token != SyntaxKind.ImplementsKeyword)
                    {
                        throw new InvalidOperationException($"Unexpected token \"{heritageClause.Token}\"");
                    }
                    if (seenImplementsClause)
                    {
                        return GrammarErrorOnFirstToken(heritageClauseNode, Diag.X_implements_clause_already_seen);
                    }

                    seenImplementsClause = true;
                }

                // Grammar checking heritageClause inside class declaration
                CheckGrammarHeritageClause(heritageClause);
            }
        }

        return false;
    }

    // grammarchecks.go:952
    internal bool CheckGrammarInterfaceDeclaration(InterfaceDeclaration node)
    {
        if (node.HeritageClauses != null)
        {
            var seenExtendsClause = false;
            foreach (var heritageClauseNode in node.HeritageClauses.Nodes)
            {
                var heritageClause = heritageClauseNode.AsHeritageClause();

                switch (heritageClause.Token)
                {
                    case SyntaxKind.ExtendsKeyword:
                        if (seenExtendsClause)
                        {
                            return GrammarErrorOnFirstToken(heritageClauseNode, Diag.X_extends_clause_already_seen);
                        }
                        seenExtendsClause = true;
                        break;
                    case SyntaxKind.ImplementsKeyword:
                        return GrammarErrorOnFirstToken(heritageClauseNode, Diag.Interface_declaration_cannot_have_implements_clause);
                    default:
                        throw new InvalidOperationException($"Unexpected token \"{heritageClause.Token}\"");
                }

                // Grammar checking heritageClause inside class declaration
                CheckGrammarHeritageClause(heritageClause);
            }
        }

        return false;
    }

    // grammarchecks.go:978
    internal bool CheckGrammarComputedPropertyName(Node node)
    {
        // If node is not a computedPropertyName, just skip the grammar checking
        if (node.Kind != SyntaxKind.ComputedPropertyName)
        {
            return false;
        }

        var computedPropertyName = node.AsComputedPropertyName();
        if (computedPropertyName.Expression!.Kind == SyntaxKind.BinaryExpression && computedPropertyName.Expression.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken)
        {
            return GrammarErrorOnNode(computedPropertyName.Expression.AsNode(), Diag.A_comma_expression_is_not_allowed_in_a_computed_property_name);
        }
        return false;
    }

    // grammarchecks.go:991
    internal bool CheckGrammarForGenerator(Node node)
    {
        var bodyData = node.BodyData();
        if (bodyData != null && bodyData.AsteriskToken != null)
        {
            if (node.Kind != SyntaxKind.FunctionDeclaration && node.Kind != SyntaxKind.FunctionExpression && node.Kind != SyntaxKind.MethodDeclaration)
            {
                throw new InvalidOperationException($"Unexpected node kind \"{node.Kind}\"");
            }
            if ((node.Flags & NodeFlags.Ambient) != 0)
            {
                return GrammarErrorOnNode(bodyData.AsteriskToken, Diag.Generators_are_not_allowed_in_an_ambient_context);
            }
            if (bodyData.Body == null)
            {
                return GrammarErrorOnNode(bodyData.AsteriskToken, Diag.An_overload_signature_cannot_be_declared_as_a_generator);
            }
        }

        return false;
    }

    // grammarchecks.go:1007
    internal bool CheckGrammarForInvalidQuestionMark(Node? postfixToken, global::Ts.Diagnostics.Message message)
    {
        return postfixToken != null && postfixToken.Kind == SyntaxKind.QuestionToken && GrammarErrorOnNode(postfixToken, message);
    }

    // grammarchecks.go:1011
    internal bool CheckGrammarForInvalidExclamationToken(Node? postfixToken, global::Ts.Diagnostics.Message message)
    {
        return postfixToken != null && postfixToken.Kind == SyntaxKind.ExclamationToken && GrammarErrorOnNode(postfixToken, message);
    }

    // grammarchecks.go:1015
    internal bool CheckGrammarObjectLiteralExpression(ObjectLiteralExpression node, bool inDestructuring)
    {
        var seen = new Dictionary<string, DeclarationMeaning>();

        Node[] properties = node.Properties != null ? node.Properties.Nodes : [];
        foreach (var prop in properties)
        {
            if (prop.Kind == SyntaxKind.SpreadAssignment)
            {
                var spreadAssignment = prop.AsSpreadAssignment();
                if (inDestructuring)
                {
                    // a rest property cannot be destructured any further
                    var expression = AstUtilities.SkipParentheses(spreadAssignment.Expression!.AsNode());
                    if (NodeTests.IsArrayLiteralExpression(expression) || NodeTests.IsObjectLiteralExpression(expression))
                    {
                        return GrammarErrorOnNode(spreadAssignment.Expression!.AsNode(), Diag.A_rest_element_cannot_contain_a_binding_pattern);
                    }
                }
                continue;
            }
            var name = prop.Name()!;
            if (name.Kind == SyntaxKind.ComputedPropertyName)
            {
                // If the name is not a ComputedPropertyName, the grammar checking will skip it
                CheckGrammarComputedPropertyName(name);
            }

            if (prop.Kind == SyntaxKind.ShorthandPropertyAssignment && !inDestructuring)
            {
                var shorthandProp = prop.AsShorthandPropertyAssignment();
                if (shorthandProp.ObjectAssignmentInitializer != null)
                {
                    // having objectAssignmentInitializer is only valid in an ObjectAssignmentPattern.
                    // Outside of destructuring, it is a syntax error.

                    // Try to grab the last node prior to the initializer,
                    // then error on the first token following (which should be the `=` token).
                    Node? lastNodeBeforeInitializer = null;
                    shorthandProp.ForEachChild((Node child) =>
                    {
                        if (child != shorthandProp.ObjectAssignmentInitializer)
                        {
                            lastNodeBeforeInitializer = child;
                            return false;
                        }
                        return true;
                    });

                    GrammarErrorOnFirstToken(lastNodeBeforeInitializer!, Diag.Did_you_mean_to_use_a_Colon_An_can_only_follow_a_property_name_when_the_containing_object_literal_is_part_of_a_destructuring_pattern);
                }
            }

            if (name.Kind == SyntaxKind.PrivateIdentifier)
            {
                GrammarErrorOnNode(name, Diag.Private_identifiers_are_not_allowed_outside_class_bodies);
            }

            // Modifiers are never allowed on properties except for 'async' on a method declaration
            var modifiers = prop.ModifierNodes();
            if (modifiers.Len() != 0)
            {
                if (CanHaveModifiers(prop))
                {
                    foreach (var mod in modifiers!)
                    {
                        if (AstUtilities.IsModifier(mod) && (mod.Kind != SyntaxKind.AsyncKeyword || prop.Kind != SyntaxKind.MethodDeclaration))
                        {
                            GrammarErrorOnNode(mod, Diag.X_0_modifier_cannot_be_used_here, global::Ts.Scanner.Scanner.GetTextOfNode(mod));
                        }
                    }
                }
                else if (CanHaveIllegalModifiers(prop))
                {
                    foreach (var mod in modifiers!)
                    {
                        if (AstUtilities.IsModifier(mod))
                        {
                            GrammarErrorOnNode(mod, Diag.X_0_modifier_cannot_be_used_here, global::Ts.Scanner.Scanner.GetTextOfNode(mod));
                        }
                    }
                }
            }

            // ECMA-262 11.1.5 Object Initializer
            // If previous is not undefined then throw a SyntaxError exception if any of the following conditions are true
            // a.This production is contained in strict code and IsDataDescriptor(previous) is true and
            // IsDataDescriptor(propId.descriptor) is true.
            //    b.IsDataDescriptor(previous) is true and IsAccessorDescriptor(propId.descriptor) is true.
            //    c.IsAccessorDescriptor(previous) is true and IsDataDescriptor(propId.descriptor) is true.
            //    d.IsAccessorDescriptor(previous) is true and IsAccessorDescriptor(propId.descriptor) is true
            // and either both previous and propId.descriptor have[[Get]] fields or both previous and propId.descriptor have[[Set]] fields
            DeclarationMeaning currentKind;
            switch (prop.Kind)
            {
                case SyntaxKind.ShorthandPropertyAssignment:
                case SyntaxKind.PropertyAssignment:
                    NamedMemberBase commonProp;
                    if (prop.Kind == SyntaxKind.ShorthandPropertyAssignment)
                    {
                        prop.ClassLikeData();
                        commonProp = prop.AsShorthandPropertyAssignment();
                    }
                    else
                    {
                        commonProp = prop.AsPropertyAssignment();
                    }

                    // Grammar checking for computedPropertyName and shorthandPropertyAssignment
                    CheckGrammarForInvalidExclamationToken(commonProp.PostfixToken, Diag.A_definite_assignment_assertion_is_not_permitted_in_this_context);
                    CheckGrammarForInvalidQuestionMark(commonProp.PostfixToken, Diag.An_object_member_cannot_be_declared_optional);

                    if (name.Kind == SyntaxKind.NumericLiteral)
                    {
                        CheckGrammarNumericLiteral(name.AsNumericLiteral());
                    }

                    if (name.Kind == SyntaxKind.BigIntLiteral)
                    {
                        AddErrorOrSuggestion(true, CreateDiagnosticForNode(name, Diag.A_bigint_literal_cannot_be_used_as_a_property_name));
                    }

                    currentKind = DeclarationMeaning.PropertyAssignment;
                    break;
                case SyntaxKind.MethodDeclaration:
                    currentKind = DeclarationMeaning.Method;
                    break;
                case SyntaxKind.GetAccessor:
                    currentKind = DeclarationMeaning.GetAccessor;
                    break;
                case SyntaxKind.SetAccessor:
                    currentKind = DeclarationMeaning.SetAccessor;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected node kind \"{prop.Kind}\"");
            }

            if (!inDestructuring)
            {
                var (effectiveName, ok) = GetEffectivePropertyNameForPropertyNameNode(name);
                if (!ok)
                {
                    continue;
                }

                var existingKind = seen.GetValueOrDefault(effectiveName);
                if (existingKind == 0)
                {
                    seen[effectiveName] = currentKind;
                }
                else
                {
                    if ((currentKind & DeclarationMeaning.Method) != 0 && (existingKind & DeclarationMeaning.Method) != 0)
                    {
                        GrammarErrorOnNode(name, Diag.Duplicate_identifier_0, global::Ts.Scanner.Scanner.GetTextOfNode(name));
                    }
                    else if ((currentKind & DeclarationMeaning.PropertyAssignment) != 0 && (existingKind & DeclarationMeaning.PropertyAssignment) != 0)
                    {
                        GrammarErrorOnNode(name, Diag.An_object_literal_cannot_have_multiple_properties_with_the_same_name, global::Ts.Scanner.Scanner.GetTextOfNode(name));
                    }
                    else if ((currentKind & DeclarationMeaning.GetOrSetAccessor) != 0 && (existingKind & DeclarationMeaning.GetOrSetAccessor) != 0)
                    {
                        if (existingKind != DeclarationMeaning.GetOrSetAccessor && currentKind != existingKind)
                        {
                            seen[effectiveName] = currentKind | existingKind;
                        }
                        else
                        {
                            return GrammarErrorOnNode(name, Diag.An_object_literal_cannot_have_multiple_get_Slashset_accessors_with_the_same_name);
                        }
                    }
                    else
                    {
                        return GrammarErrorOnNode(name, Diag.An_object_literal_cannot_have_property_and_accessor_with_the_same_name);
                    }
                }
            }
        }

        return false;
    }

    // grammarchecks.go:1155
    internal bool CheckGrammarJsxElement(Node node)
    {
        CheckGrammarJsxName(node.TagName()!);
        CheckGrammarTypeArguments(node, node.TypeArgumentList());
        var seen = new Set<string>();
        foreach (var attrNode in node.Attributes()!.Properties().OrEmpty())
        {
            if (attrNode.Kind == SyntaxKind.JsxSpreadAttribute)
            {
                continue;
            }
            var attr = attrNode.AsJsxAttribute();
            var name = attr.Name()!;
            var initializer = attr.Initializer;
            var textOfName = name.Text();
            if (!seen.Has(textOfName))
            {
                seen.Add(textOfName);
            }
            else
            {
                return GrammarErrorOnNode(name, Diag.JSX_elements_cannot_have_multiple_attributes_with_the_same_name);
            }
            if (initializer != null && initializer.Kind == SyntaxKind.JsxExpression && initializer.Expression() == null)
            {
                return GrammarErrorOnNode(initializer, Diag.JSX_attributes_must_only_be_assigned_a_non_empty_expression);
            }
        }
        return false;
    }

    // grammarchecks.go:1179
    internal bool CheckGrammarJsxName(Node node /*JsxTagNameExpression*/)
    {
        if (NodeTests.IsPropertyAccessExpression(node) && NodeTests.IsJsxNamespacedName(node.Expression()!))
        {
            return GrammarErrorOnNode(node.Expression()!, Diag.JSX_property_access_expressions_cannot_include_JSX_namespace_names);
        }

        if (NodeTests.IsJsxNamespacedName(node) && compilerOptions.GetJSXTransformEnabled() && !global::Ts.Scanner.Scanner.IsIntrinsicJsxName(node.AsJsxNamespacedName().Namespace!.Text()))
        {
            return GrammarErrorOnNode(node, Diag.React_components_cannot_include_JSX_namespace_names);
        }

        return false;
    }

    // grammarchecks.go:1191
    internal bool CheckGrammarJsxExpression(JsxExpression node)
    {
        if (node.Expression != null && AstUtilities.IsCommaSequence(node.Expression.AsNode()))
        {
            return GrammarErrorOnNode(node.Expression.AsNode(), Diag.JSX_expressions_may_not_use_the_comma_operator_Did_you_mean_to_write_an_array);
        }

        return false;
    }

    // grammarchecks.go:1199
    internal bool CheckGrammarForInOrForOfStatement(ForInOrOfStatement forInOrOfStatement)
    {
        var asNode = forInOrOfStatement.AsNode();
        if (CheckGrammarStatementInAmbientContext(asNode))
        {
            return true;
        }

        if (forInOrOfStatement.Kind == SyntaxKind.ForOfStatement && forInOrOfStatement.AwaitModifier != null)
        {
            if ((forInOrOfStatement.Flags & NodeFlags.AwaitContext) == 0)
            {
                var sourceFile = AstUtilities.GetSourceFileOfNode(asNode)!;
                if (AstUtilities.IsInTopLevelContext(asNode))
                {
                    if (!HasParseDiagnostics(sourceFile))
                    {
                        if (!AstUtilities.IsEffectiveExternalModule(sourceFile, compilerOptions))
                        {
                            AddDiagnostic(CreateDiagnosticForNode(forInOrOfStatement.AwaitModifier, Diag.X_for_await_loops_are_only_allowed_at_the_top_level_of_a_file_when_that_file_is_a_module_but_this_file_has_no_imports_or_exports_Consider_adding_an_empty_export_to_make_this_file_a_module));
                        }
                        switch (moduleKind)
                        {
                            case ModuleKind.Node16:
                            case ModuleKind.Node18:
                            case ModuleKind.Node20:
                            case ModuleKind.NodeNext:
                                var sourceFileMetaData = program.GetSourceFileMetaData(sourceFile.Path());
                                if (sourceFileMetaData.ImpliedNodeFormat == ModuleKind.CommonJS)
                                {
                                    AddDiagnostic(CreateDiagnosticForNode(forInOrOfStatement.AwaitModifier, Diag.The_current_file_is_a_CommonJS_module_and_cannot_use_await_at_the_top_level));
                                    break;
                                }
                                goto case ModuleKind.ES2022;
                            case ModuleKind.ES2022:
                            case ModuleKind.ESNext:
                            case ModuleKind.Preserve:
                            case ModuleKind.System:
                                if (languageVersion >= ScriptTarget.ES2017)
                                {
                                    break;
                                }
                                goto default;
                            default:
                                AddDiagnostic(CreateDiagnosticForNode(forInOrOfStatement.AwaitModifier, Diag.Top_level_for_await_loops_are_only_allowed_when_the_module_option_is_set_to_es2022_esnext_system_node16_node18_node20_nodenext_or_preserve_and_the_target_option_is_set_to_es2017_or_higher));
                                break;
                        }
                    }
                }
                else
                {
                    // use of 'for-await-of' in non-async function
                    if (!HasParseDiagnostics(sourceFile))
                    {
                        var diagnostic = CreateDiagnosticForNode(forInOrOfStatement.AwaitModifier, Diag.X_for_await_loops_are_only_allowed_within_async_functions_and_at_the_top_levels_of_modules);
                        var containingFunc = GetContainingFunction(forInOrOfStatement.AsNode());
                        if (containingFunc != null && containingFunc.Kind != SyntaxKind.Constructor)
                        {
                            global::Ts.Core.Debug.Assert((AstUtilities.GetFunctionFlags(containingFunc) & FunctionFlags.Async) == 0, "Enclosing function should never be an async function.");
                            var relatedInfo = CreateDiagnosticForNode(containingFunc, Diag.Did_you_mean_to_mark_this_function_as_async);
                            diagnostic.AddRelatedInfo(relatedInfo);
                        }
                        AddDiagnostic(diagnostic);
                        return true;
                    }
                }
            }
        }

        if (NodeTests.IsForOfStatement(asNode) && (forInOrOfStatement.Flags & NodeFlags.AwaitContext) == 0 && NodeTests.IsIdentifier(forInOrOfStatement.Initializer!) && forInOrOfStatement.Initializer!.Text() == "async")
        {
            GrammarErrorOnNode(forInOrOfStatement.Initializer!, Diag.The_left_hand_side_of_a_for_of_statement_may_not_be_async);
            return false;
        }

        if (forInOrOfStatement.Initializer!.Kind == SyntaxKind.VariableDeclarationList)
        {
            var variableList = forInOrOfStatement.Initializer.AsVariableDeclarationList();
            if (!CheckGrammarVariableDeclarationList(variableList))
            {
                var declarations = variableList.Declarations!;

                // declarations.length can be zero if there is an error in variable declaration in for-of or for-in
                // See http://www.ecma-international.org/ecma-262/6.0/#sec-for-in-and-for-of-statements for details
                // For example:
                //      var let = 10;
                //      for (let of [1,2,3]) {} // this is invalid ES6 syntax
                //      for (let in [1,2,3]) {} // this is invalid ES6 syntax
                // We will then want to skip on grammar checking on variableList declaration
                if (declarations.Nodes.Length == 0)
                {
                    return false;
                }

                if (declarations.Nodes.Length > 1)
                {
                    global::Ts.Diagnostics.Message diagnostic;
                    if (forInOrOfStatement.Kind == SyntaxKind.ForInStatement)
                    {
                        diagnostic = Diag.Only_a_single_variable_declaration_is_allowed_in_a_for_in_statement;
                    }
                    else
                    {
                        diagnostic = Diag.Only_a_single_variable_declaration_is_allowed_in_a_for_of_statement;
                    }
                    return GrammarErrorOnFirstToken(declarations.Nodes[1], diagnostic);
                }

                var firstVariableDeclaration = declarations.Nodes[0].AsVariableDeclaration();
                if (firstVariableDeclaration.Initializer != null)
                {
                    global::Ts.Diagnostics.Message diagnostic;
                    if (forInOrOfStatement.Kind == SyntaxKind.ForInStatement)
                    {
                        diagnostic = Diag.The_variable_declaration_of_a_for_in_statement_cannot_have_an_initializer;
                    }
                    else
                    {
                        diagnostic = Diag.The_variable_declaration_of_a_for_of_statement_cannot_have_an_initializer;
                    }
                    return GrammarErrorOnNode(firstVariableDeclaration.Name()!, diagnostic);
                }
                if (firstVariableDeclaration.Type != null)
                {
                    global::Ts.Diagnostics.Message diagnostic;
                    if (forInOrOfStatement.Kind == SyntaxKind.ForInStatement)
                    {
                        diagnostic = Diag.The_left_hand_side_of_a_for_in_statement_cannot_use_a_type_annotation;
                    }
                    else
                    {
                        diagnostic = Diag.The_left_hand_side_of_a_for_of_statement_cannot_use_a_type_annotation;
                    }
                    return GrammarErrorOnNode(firstVariableDeclaration.AsNode(), diagnostic);
                }
            }
        }

        return false;
    }

    // grammarchecks.go:1306
    internal bool CheckGrammarAccessor(AccessorDeclaration accessor)
    {
        var body = accessor.Body();
        if ((accessor.Flags & NodeFlags.Ambient) == 0 && accessor.Parent!.Kind != SyntaxKind.TypeLiteral && accessor.Parent.Kind != SyntaxKind.InterfaceDeclaration)
        {
            if (body == null && !AstUtilities.HasSyntacticModifier(accessor.AsNode(), ModifierFlags.Abstract))
            {
                return GrammarErrorAtPos(accessor.AsNode(), accessor.End - 1, ";".Length, Diag.X_0_expected, "{");
            }
        }
        if (body != null)
        {
            if (AstUtilities.HasSyntacticModifier(accessor.AsNode(), ModifierFlags.Abstract))
            {
                return GrammarErrorOnNode(accessor.AsNode(), Diag.An_abstract_accessor_cannot_have_an_implementation);
            }
            if (accessor.Parent!.Kind == SyntaxKind.TypeLiteral || accessor.Parent.Kind == SyntaxKind.InterfaceDeclaration)
            {
                return GrammarErrorOnNode(body, Diag.An_implementation_cannot_be_declared_in_ambient_contexts);
            }
        }

        var funcData = accessor.FunctionLikeData();
        NodeList? typeParameters = null;
        if (funcData != null)
        {
            typeParameters = funcData.TypeParameters;
        }

        if (typeParameters != null)
        {
            return GrammarErrorOnNode(accessor.Name()!, Diag.An_accessor_cannot_have_type_parameters);
        }
        if (!DoesAccessorHaveCorrectParameterCount(accessor))
        {
            return GrammarErrorOnNode(accessor.Name()!, CoreUtil.IfElse(accessor.Kind == SyntaxKind.GetAccessor, Diag.A_get_accessor_cannot_have_parameters, Diag.A_set_accessor_must_have_exactly_one_parameter));
        }
        if (accessor.Kind == SyntaxKind.SetAccessor)
        {
            if (funcData!.Type != null)
            {
                return GrammarErrorOnNode(accessor.Name()!, Diag.A_set_accessor_cannot_have_a_return_type_annotation);
            }

            var parameterNode = GetSetAccessorValueParameter(accessor.AsNode());
            if (parameterNode == null)
            {
                throw new InvalidOperationException("Return value does not match parameter count assertion.");
            }
            var parameter = parameterNode.AsParameterDeclaration();
            if (parameter.DotDotDotToken != null)
            {
                return GrammarErrorOnNode(parameter.DotDotDotToken, Diag.A_set_accessor_cannot_have_rest_parameter);
            }
            if (parameter.QuestionToken != null)
            {
                return GrammarErrorOnNode(parameter.QuestionToken, Diag.A_set_accessor_cannot_have_an_optional_parameter);
            }
            if (parameter.Initializer != null)
            {
                return GrammarErrorOnNode(accessor.Name()!, Diag.A_set_accessor_parameter_cannot_have_an_initializer);
            }
        }

        return false;
    }

    // Does the accessor have the right number of parameters?
    //
    //	A `get` accessor has no parameters or a single `this` parameter.
    //	A `set` accessor has one parameter or a `this` parameter and one more parameter.
    // grammarchecks.go:1362
    internal bool DoesAccessorHaveCorrectParameterCount(AccessorDeclaration accessor)
    {
        // `getAccessorThisParameter` returns `nil` if the accessor's arity is incorrect,
        // even if there is a `this` parameter declared.
        return GetAccessorThisParameter(accessor.AsNode()) != null || accessor.Parameters().Len() == CoreUtil.IfElse(accessor.Kind == SyntaxKind.GetAccessor, 0, 1);
    }

    // grammarchecks.go:1368
    internal bool CheckGrammarTypeOperatorNode(TypeOperatorNode node)
    {
        if (node.Operator == SyntaxKind.UniqueKeyword)
        {
            var innerType = node.Type!;
            if (innerType.Kind != SyntaxKind.SymbolKeyword)
            {
                return GrammarErrorOnNode(innerType, Diag.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.SymbolKeyword));
            }
            var parent = AstUtilities.WalkUpParenthesizedTypes(node.Parent!)!;
            switch (parent.Kind)
            {
                case SyntaxKind.VariableDeclaration:
                    var decl = parent.AsVariableDeclaration();
                    if (decl.Name()!.Kind != SyntaxKind.Identifier)
                    {
                        return GrammarErrorOnNode(node.AsNode(), Diag.X_unique_symbol_types_may_not_be_used_on_a_variable_declaration_with_a_binding_name);
                    }
                    if (!IsVariableDeclarationInVariableStatement(decl.AsNode()))
                    {
                        return GrammarErrorOnNode(node.AsNode(), Diag.X_unique_symbol_types_are_only_allowed_on_variables_in_a_variable_statement);
                    }
                    if ((decl.Parent!.Flags & NodeFlags.Const) == 0)
                    {
                        return GrammarErrorOnNode(parent.AsVariableDeclaration().Name()!, Diag.A_variable_whose_type_is_a_unique_symbol_type_must_be_const);
                    }
                    break;
                case SyntaxKind.PropertyDeclaration:
                    if (!AstUtilities.IsStatic(parent) || !HasReadonlyModifier(parent))
                    {
                        return GrammarErrorOnNode(parent.AsPropertyDeclaration().Name()!, Diag.A_property_of_a_class_whose_type_is_a_unique_symbol_type_must_be_both_static_and_readonly);
                    }
                    break;
                case SyntaxKind.PropertySignature:
                    if (!AstUtilities.HasSyntacticModifier(parent, ModifierFlags.Readonly))
                    {
                        return GrammarErrorOnNode(parent.AsPropertySignatureDeclaration().Name()!, Diag.A_property_of_an_interface_or_type_literal_whose_type_is_a_unique_symbol_type_must_be_readonly);
                    }
                    break;
                default:
                    return GrammarErrorOnNode(node.AsNode(), Diag.X_unique_symbol_types_are_not_allowed_here);
            }
        }
        else if (node.Operator == SyntaxKind.ReadonlyKeyword)
        {
            var innerType = node.Type!;
            if (innerType.Kind != SyntaxKind.ArrayType && innerType.Kind != SyntaxKind.TupleType)
            {
                return GrammarErrorOnFirstToken(node.AsNode(), Diag.X_readonly_type_modifier_is_only_permitted_on_array_and_tuple_literal_types, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.SymbolKeyword));
            }
        }

        return false;
    }

    // grammarchecks.go:1408
    internal bool CheckGrammarForInvalidDynamicName(Node node /*DeclarationName*/, global::Ts.Diagnostics.Message message)
    {
        if (!IsNonBindableDynamicName(node))
        {
            return false;
        }
        Node? expression;
        if (NodeTests.IsElementAccessExpression(node))
        {
            expression = AstUtilities.SkipParentheses(node.AsElementAccessExpression().ArgumentExpression!.AsNode());
        }
        else
        {
            expression = node.Expression();
        }

        if (!AstUtilities.IsEntityNameExpression(expression!))
        {
            return GrammarErrorOnNode(node, message);
        }

        return false;
    }

    // Indicates whether a declaration name is a dynamic name that cannot be late-bound.
    // grammarchecks.go:1427
    internal bool IsNonBindableDynamicName(Node node /*DeclarationName*/)
    {
        return AstUtilities.IsDynamicName(node) && !IsLateBindableName(node);
    }

    // grammarchecks.go:1431
    internal bool CheckGrammarMethod(Node node /*Union[MethodDeclaration, MethodSignature]*/)
    {
        if (CheckGrammarFunctionLikeDeclaration(node))
        {
            return true;
        }

        if (node.Kind == SyntaxKind.MethodDeclaration)
        {
            if (node.Parent!.Kind == SyntaxKind.ObjectLiteralExpression)
            {
                // We only disallow modifier on a method declaration if it is a property of object-literal-expression
                var modifiers = node.Modifiers();
                if (modifiers != null && !(modifiers.Nodes.Length == 1 && modifiers.Nodes[0].Kind == SyntaxKind.AsyncKeyword))
                {
                    return GrammarErrorOnFirstToken(node, Diag.Modifiers_cannot_appear_here);
                }

                var methodDecl = node.AsMethodDeclaration();
                if (CheckGrammarForInvalidQuestionMark(methodDecl.PostfixToken, Diag.An_object_member_cannot_be_declared_optional))
                {
                    return true;
                }
                if (CheckGrammarForInvalidExclamationToken(methodDecl.PostfixToken, Diag.A_definite_assignment_assertion_is_not_permitted_in_this_context))
                {
                    return true;
                }
                if (node.Body() == null)
                {
                    return GrammarErrorAtPos(node, node.End - 1, ";".Length, Diag.X_0_expected, "{");
                }
            }
            if (CheckGrammarForGenerator(node))
            {
                return true;
            }
        }

        if (AstUtilities.IsClassLike(node.Parent!))
        {
            // Technically, computed properties in ambient contexts is disallowed
            // for property declarations and accessors too, not just methods.
            // However, property declarations disallow computed names in general,
            // and accessors are not allowed in ambient contexts in general,
            // so this error only really matters for methods.
            if ((node.Flags & NodeFlags.Ambient) != 0)
            {
                return CheckGrammarForInvalidDynamicName(node.Name()!, Diag.A_computed_property_name_in_an_ambient_context_must_refer_to_an_expression_whose_type_is_a_literal_type_or_a_unique_symbol_type);
            }
            else if (node.Kind == SyntaxKind.MethodDeclaration && node.Body() == null)
            {
                return CheckGrammarForInvalidDynamicName(node.Name()!, Diag.A_computed_property_name_in_a_method_overload_must_refer_to_an_expression_whose_type_is_a_literal_type_or_a_unique_symbol_type);
            }
        }
        else if (node.Parent!.Kind == SyntaxKind.InterfaceDeclaration)
        {
            return CheckGrammarForInvalidDynamicName(node.Name()!, Diag.A_computed_property_name_in_an_interface_must_refer_to_an_expression_whose_type_is_a_literal_type_or_a_unique_symbol_type);
        }
        else if (node.Parent.Kind == SyntaxKind.TypeLiteral)
        {
            return CheckGrammarForInvalidDynamicName(node.Name()!, Diag.A_computed_property_name_in_a_type_literal_must_refer_to_an_expression_whose_type_is_a_literal_type_or_a_unique_symbol_type);
        }

        return false;
    }

    // grammarchecks.go:1479
    internal bool CheckGrammarBreakOrContinueStatement(Node node)
    {
        var targetLabel = node.Label();
        Node? current = node;
        while (current != null)
        {
            if (AstUtilities.IsFunctionLikeOrClassStaticBlockDeclaration(current))
            {
                return GrammarErrorOnNode(node, Diag.Jump_target_cannot_cross_function_boundary);
            }

            switch (current.Kind)
            {
                case SyntaxKind.LabeledStatement:
                    if (targetLabel != null && current.Label()!.Text() == targetLabel.Text())
                    {
                        // found matching label - verify that label usage is correct
                        // continue can only target labels that are on iteration statements
                        var isMisplacedContinueLabel = node.Kind == SyntaxKind.ContinueStatement && !AstUtilities.IsIterationStatement(current.Statement()!, true /*lookInLabeledStatements*/);

                        if (isMisplacedContinueLabel)
                        {
                            return GrammarErrorOnNode(node, Diag.A_continue_statement_can_only_jump_to_a_label_of_an_enclosing_iteration_statement);
                        }

                        return false;
                    }
                    break;
                case SyntaxKind.SwitchStatement:
                    if (node.Kind == SyntaxKind.BreakStatement && targetLabel == null)
                    {
                        // unlabeled break within switch statement - ok
                        return false;
                    }
                    break;
                default:
                    if (AstUtilities.IsIterationStatement(current, false /*lookInLabeledStatements*/) && targetLabel == null)
                    {
                        // unlabeled break or continue within iteration statement - ok
                        return false;
                    }
                    break;
            }

            current = current.Parent;
        }

        if (targetLabel != null)
        {
            global::Ts.Diagnostics.Message message;
            if (node.Kind == SyntaxKind.BreakStatement)
            {
                message = Diag.A_break_statement_can_only_jump_to_a_label_of_an_enclosing_statement;
            }
            else
            {
                message = Diag.A_continue_statement_can_only_jump_to_a_label_of_an_enclosing_iteration_statement;
            }

            return GrammarErrorOnNode(node, message);
        }
        else
        {
            global::Ts.Diagnostics.Message message;
            if (node.Kind == SyntaxKind.BreakStatement)
            {
                message = Diag.A_break_statement_can_only_be_used_within_an_enclosing_iteration_or_switch_statement;
            }
            else
            {
                message = Diag.A_continue_statement_can_only_be_used_within_an_enclosing_iteration_statement;
            }
            return GrammarErrorOnNode(node, message);
        }
    }

    // grammarchecks.go:1535
    internal bool CheckGrammarBindingElement(BindingElement node)
    {
        if (node.DotDotDotToken != null)
        {
            var elements = node.Parent!.ElementList()!;
            if (node.AsNode() != CoreUtil.LastOrNil(elements.Nodes))
            {
                return GrammarErrorOnNode(node.AsNode(), Diag.A_rest_element_must_be_last_in_a_destructuring_pattern);
            }
            CheckGrammarForDisallowedTrailingComma(elements, Diag.A_rest_parameter_or_binding_pattern_may_not_have_a_trailing_comma);

            if (node.PropertyName != null)
            {
                return GrammarErrorOnNode(node.Name()!, Diag.A_rest_element_cannot_have_a_property_name);
            }
        }

        if (node.DotDotDotToken != null && node.Initializer != null)
        {
            // Error on equals token which immediately precedes the initializer
            return GrammarErrorAtPos(node.AsNode(), node.Initializer.Pos - 1, 1, Diag.A_rest_element_cannot_have_an_initializer);
        }

        return false;
    }

    // grammarchecks.go:1556
    internal bool CheckGrammarVariableDeclaration(VariableDeclaration node)
    {
        var nodeFlags = GetCombinedNodeFlagsCached(node.AsNode());
        var blockScopeKind = nodeFlags & NodeFlags.BlockScoped;
        if (AstUtilities.IsBindingPattern(node.Name()))
        {
            switch (blockScopeKind)
            {
                case NodeFlags.AwaitUsing:
                    return GrammarErrorOnNode(node.AsNode(), Diag.X_0_declarations_may_not_have_binding_patterns, "await using");
                case NodeFlags.Using:
                    return GrammarErrorOnNode(node.AsNode(), Diag.X_0_declarations_may_not_have_binding_patterns, "using");
            }
        }

        if (node.Parent!.Parent!.Kind != SyntaxKind.ForInStatement && node.Parent.Parent.Kind != SyntaxKind.ForOfStatement)
        {
            if ((nodeFlags & NodeFlags.Ambient) != 0)
            {
                CheckAmbientInitializer(node.AsNode());
            }
            else if (node.Initializer == null)
            {
                if (AstUtilities.IsBindingPattern(node.Name()) && !AstUtilities.IsBindingPattern(node.Parent))
                {
                    return GrammarErrorOnNode(node.AsNode(), Diag.A_destructuring_declaration_must_have_an_initializer);
                }
                switch (blockScopeKind)
                {
                    case NodeFlags.AwaitUsing:
                        return GrammarErrorOnNode(node.AsNode(), Diag.X_0_declarations_must_be_initialized, "await using");
                    case NodeFlags.Using:
                        return GrammarErrorOnNode(node.AsNode(), Diag.X_0_declarations_must_be_initialized, "using");
                    case NodeFlags.Const:
                        return GrammarErrorOnNode(node.AsNode(), Diag.X_0_declarations_must_be_initialized, "const");
                }
            }
        }

        if (node.ExclamationToken != null && (node.Parent.Parent.Kind != SyntaxKind.VariableStatement || node.Type == null || node.Initializer != null || (nodeFlags & NodeFlags.Ambient) != 0))
        {
            global::Ts.Diagnostics.Message message;
            if (node.Initializer != null)
            {
                message = Diag.Declarations_with_initializers_cannot_also_have_definite_assignment_assertions;
            }
            else if (node.Type == null)
            {
                message = Diag.Declarations_with_definite_assignment_assertions_must_also_have_type_annotations;
            }
            else
            {
                message = Diag.A_definite_assignment_assertion_is_not_permitted_in_this_context;
            }
            return GrammarErrorOnNode(node.ExclamationToken, message);
        }

        if (program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node.AsNode())!)) < ModuleKind.System && (node.Parent.Parent.Flags & NodeFlags.Ambient) == 0 && AstUtilities.HasSyntacticModifier(node.Parent.Parent, ModifierFlags.Export))
        {
            CheckGrammarForEsModuleMarkerInBindingName(node.Name()!);
        }

        // 1. LexicalDeclaration : LetOrConst BindingList ;
        // It is a Syntax Error if the BoundNames of BindingList contains "let".
        // 2. ForDeclaration: ForDeclaration : LetOrConst ForBinding
        // It is a Syntax Error if the BoundNames of ForDeclaration contains "let".

        // It is a SyntaxError if a VariableDeclaration or VariableDeclarationNoIn occurs within strict code
        // and its Identifier is eval or arguments
        return blockScopeKind != 0 && CheckGrammarNameInLetOrConstDeclarations(node.Name()!);
    }

    // grammarchecks.go:1613
    internal bool CheckGrammarForEsModuleMarkerInBindingName(Node name)
    {
        if (NodeTests.IsIdentifier(name))
        {
            if (name.Text() == "__esModule")
            {
                return GrammarErrorOnNodeSkippedOnNoEmit(name, Diag.Identifier_expected_esModule_is_reserved_as_an_exported_marker_when_transforming_ECMAScript_modules);
            }
        }
        else
        {
            foreach (var element in name.Elements().OrEmpty())
            {
                if (element.Name() != null)
                {
                    return CheckGrammarForEsModuleMarkerInBindingName(element.Name()!);
                }
            }
        }
        return false;
    }

    // grammarchecks.go:1628
    internal bool CheckGrammarNameInLetOrConstDeclarations(Node name /*Union[Identifier, BindingPattern]*/)
    {
        if (name.Kind == SyntaxKind.Identifier)
        {
            if (name.Text() == "let")
            {
                return GrammarErrorOnNode(name, Diag.X_let_is_not_allowed_to_be_used_as_a_name_in_let_or_const_declarations);
            }
        }
        else
        {
            var elements = name.Elements();
            foreach (var element in elements.OrEmpty())
            {
                var bindingElement = element.AsBindingElement();
                if (bindingElement.Name() != null)
                {
                    CheckGrammarNameInLetOrConstDeclarations(bindingElement.Name()!);
                }
            }
        }
        return false;
    }

    // grammarchecks.go:1645
    internal bool CheckGrammarVariableDeclarationList(VariableDeclarationList declarationList)
    {
        var declarations = declarationList.Declarations!;
        if (CheckGrammarForDisallowedTrailingComma(declarations, Diag.Trailing_comma_not_allowed))
        {
            return true;
        }

        if (declarations.Nodes.Length == 0)
        {
            return GrammarErrorAtPos(declarationList.AsNode(), declarations.Pos, declarations.End - declarations.Pos, Diag.Variable_declaration_list_cannot_be_empty);
        }

        var blockScopeFlags = declarationList.Flags & NodeFlags.BlockScoped;
        if (blockScopeFlags == NodeFlags.Using || blockScopeFlags == NodeFlags.AwaitUsing)
        {
            if (NodeTests.IsForInStatement(declarationList.Parent!))
            {
                return GrammarErrorOnNode(declarationList.AsNode(), CoreUtil.IfElse(blockScopeFlags == NodeFlags.Using, Diag.The_left_hand_side_of_a_for_in_statement_cannot_be_a_using_declaration, Diag.The_left_hand_side_of_a_for_in_statement_cannot_be_an_await_using_declaration));
            }
            if ((declarationList.Flags & NodeFlags.Ambient) != 0)
            {
                return GrammarErrorOnNode(declarationList.AsNode(), CoreUtil.IfElse(blockScopeFlags == NodeFlags.Using, Diag.X_using_declarations_are_not_allowed_in_ambient_contexts, Diag.X_await_using_declarations_are_not_allowed_in_ambient_contexts));
            }
            if (NodeTests.IsVariableStatement(declarationList.Parent!) && (NodeTests.IsCaseClause(declarationList.Parent!.Parent!) || NodeTests.IsDefaultClause(declarationList.Parent.Parent!)))
            {
                return GrammarErrorOnNode(declarationList.AsNode(), CoreUtil.IfElse(blockScopeFlags == NodeFlags.Using, Diag.X_using_declarations_are_not_allowed_in_case_or_default_clauses_unless_contained_within_a_block, Diag.X_await_using_declarations_are_not_allowed_in_case_or_default_clauses_unless_contained_within_a_block));
            }
        }

        if (blockScopeFlags == NodeFlags.AwaitUsing)
        {
            return CheckGrammarAwaitOrAwaitUsing(declarationList.AsNode());
        }

        return false;
    }

    // grammarchecks.go:1675
    internal bool CheckGrammarAwaitOrAwaitUsing(Node node)
    {
        // Grammar checking
        var hasError = false;
        var container = GetContainingFunctionOrClassStaticBlock(node);
        if (container != null && NodeTests.IsClassStaticBlockDeclaration(container))
        {
            // NOTE: We report this regardless as to whether there are parse diagnostics.
            global::Ts.Diagnostics.Message message;
            if (NodeTests.IsAwaitExpression(node))
            {
                message = Diag.X_await_expression_cannot_be_used_inside_a_class_static_block;
            }
            else
            {
                message = Diag.X_await_using_statements_cannot_be_used_inside_a_class_static_block;
            }
            Error(node, message);
            hasError = true;
        }
        else if ((node.Flags & NodeFlags.AwaitContext) == 0)
        {
            if (AstUtilities.IsInTopLevelContext(node))
            {
                var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
                if (!HasParseDiagnostics(sourceFile))
                {
                    TextRange span = default;
                    var spanCalculated = false;
                    if (!AstUtilities.IsEffectiveExternalModule(sourceFile, compilerOptions))
                    {
                        span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(sourceFile, node.Pos);
                        spanCalculated = true;
                        global::Ts.Diagnostics.Message message;
                        if (NodeTests.IsAwaitExpression(node))
                        {
                            message = Diag.X_await_expressions_are_only_allowed_at_the_top_level_of_a_file_when_that_file_is_a_module_but_this_file_has_no_imports_or_exports_Consider_adding_an_empty_export_to_make_this_file_a_module;
                        }
                        else
                        {
                            message = Diag.X_await_using_statements_are_only_allowed_at_the_top_level_of_a_file_when_that_file_is_a_module_but_this_file_has_no_imports_or_exports_Consider_adding_an_empty_export_to_make_this_file_a_module;
                        }
                        var diagnostic = Diagnostic.NewDiagnostic(sourceFile, span, message);
                        AddDiagnostic(diagnostic);
                        hasError = true;
                    }
                    switch (moduleKind)
                    {
                        case ModuleKind.Node16:
                        case ModuleKind.Node18:
                        case ModuleKind.Node20:
                        case ModuleKind.NodeNext:
                            var sourceFileMetaData = program.GetSourceFileMetaData(sourceFile.Path());
                            if (sourceFileMetaData.ImpliedNodeFormat == ModuleKind.CommonJS)
                            {
                                if (!spanCalculated)
                                {
                                    span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(sourceFile, node.Pos);
                                }
                                AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, span, Diag.The_current_file_is_a_CommonJS_module_and_cannot_use_await_at_the_top_level));
                                hasError = true;
                                break;
                            }
                            goto case ModuleKind.ES2022;
                        case ModuleKind.ES2022:
                        case ModuleKind.ESNext:
                        case ModuleKind.Preserve:
                        case ModuleKind.System:
                            if (languageVersion >= ScriptTarget.ES2017)
                            {
                                break;
                            }
                            goto default;
                        default:
                            if (!spanCalculated)
                            {
                                span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(sourceFile, node.Pos);
                            }
                            global::Ts.Diagnostics.Message message;
                            if (NodeTests.IsAwaitExpression(node))
                            {
                                message = Diag.Top_level_await_expressions_are_only_allowed_when_the_module_option_is_set_to_es2022_esnext_system_node16_node18_node20_nodenext_or_preserve_and_the_target_option_is_set_to_es2017_or_higher;
                            }
                            else
                            {
                                message = Diag.Top_level_await_using_statements_are_only_allowed_when_the_module_option_is_set_to_es2022_esnext_system_node16_node18_node20_nodenext_or_preserve_and_the_target_option_is_set_to_es2017_or_higher;
                            }
                            AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, span, message));
                            hasError = true;
                            break;
                    }
                }
            }
            else
            {
                // use of 'await' in non-async function
                var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
                if (!HasParseDiagnostics(sourceFile))
                {
                    var span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(sourceFile, node.Pos);
                    global::Ts.Diagnostics.Message message;
                    if (NodeTests.IsAwaitExpression(node))
                    {
                        message = Diag.X_await_expressions_are_only_allowed_within_async_functions_and_at_the_top_levels_of_modules;
                    }
                    else
                    {
                        message = Diag.X_await_using_statements_are_only_allowed_within_async_functions_and_at_the_top_levels_of_modules;
                    }
                    var diagnostic = Diagnostic.NewDiagnostic(sourceFile, span, message);
                    if (container != null && container.Kind != SyntaxKind.Constructor && !HasAsyncModifier(container))
                    {
                        var relatedInfo = NewDiagnosticForNode(container, Diag.Did_you_mean_to_mark_this_function_as_async);
                        diagnostic.AddRelatedInfo(relatedInfo);
                    }
                    AddDiagnostic(diagnostic);
                    hasError = true;
                }
            }
        }

        if (NodeTests.IsAwaitExpression(node) && IsInParameterInitializerBeforeContainingFunction(node))
        {
            // NOTE: We report this regardless as to whether there are parse diagnostics.
            Error(node, Diag.X_await_expressions_cannot_be_used_in_a_parameter_initializer);
            hasError = true;
        }

        return hasError;
    }

    // grammarchecks.go:1776
    internal bool CheckGrammarYieldExpression(Node node)
    {
        var hasError = false;
        if ((node.Flags & NodeFlags.YieldContext) == 0)
        {
            GrammarErrorOnFirstToken(node, Diag.A_yield_expression_is_only_allowed_in_a_generator_body);
            hasError = true;
        }
        if (IsInParameterInitializerBeforeContainingFunction(node))
        {
            Error(node, Diag.X_yield_expressions_cannot_be_used_in_a_parameter_initializer);
            hasError = true;
        }
        return hasError;
    }

    // grammarchecks.go:1789
    internal bool CheckGrammarForDisallowedBlockScopedVariableStatement(VariableStatement node)
    {
        if (!ContainerAllowsBlockScopedVariable(node.Parent!))
        {
            var blockScopeKind = GetCombinedNodeFlagsCached(node.DeclarationList!.AsNode()) & NodeFlags.BlockScoped;
            if (blockScopeKind != 0)
            {
                string keyword;
                if (blockScopeKind == NodeFlags.Let)
                {
                    keyword = "let";
                }
                else if (blockScopeKind == NodeFlags.Const)
                {
                    keyword = "const";
                }
                else if (blockScopeKind == NodeFlags.Using)
                {
                    keyword = "using";
                }
                else if (blockScopeKind == NodeFlags.AwaitUsing)
                {
                    keyword = "await using";
                }
                else
                {
                    throw new InvalidOperationException("Unknown BlockScope flag");
                }
                Error(node.AsNode(), Diag.X_0_declarations_can_only_be_declared_inside_a_block, keyword);
            }
        }

        return false;
    }

    // grammarchecks.go:1813
    internal bool ContainerAllowsBlockScopedVariable(Node parent)
    {
        switch (parent.Kind)
        {
            case SyntaxKind.IfStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                return false;
            case SyntaxKind.LabeledStatement:
                return ContainerAllowsBlockScopedVariable(parent.Parent!);
        }

        return true;
    }

    // grammarchecks.go:1830
    internal bool CheckGrammarMetaProperty(MetaProperty node)
    {
        var nodeName = node.Name()!;
        var nameText = nodeName.Text();

        switch (node.KeywordToken)
        {
            case SyntaxKind.NewKeyword:
                if (nameText != "target")
                {
                    return GrammarErrorOnNode(nodeName, Diag.X_0_is_not_a_valid_meta_property_for_keyword_1_Did_you_mean_2, nameText, global::Ts.Scanner.Scanner.TokenToString(node.KeywordToken), "target");
                }
                break;
            case SyntaxKind.ImportKeyword:
                if (nameText != "meta")
                {
                    var isCallee = NodeTests.IsCallExpression(node.Parent!) && node.Parent!.Expression() == node.AsNode();
                    if (nameText == "defer")
                    {
                        if (!isCallee)
                        {
                            return GrammarErrorAtPos(node.AsNode(), node.AsNode().End, 0, Diag.X_0_expected, "(");
                        }
                    }
                    else
                    {
                        if (isCallee)
                        {
                            return GrammarErrorOnNode(nodeName, Diag.X_0_is_not_a_valid_meta_property_for_keyword_import_Did_you_mean_meta_or_defer, nameText);
                        }
                        return GrammarErrorOnNode(nodeName, Diag.X_0_is_not_a_valid_meta_property_for_keyword_1_Did_you_mean_2, nameText, global::Ts.Scanner.Scanner.TokenToString(node.KeywordToken), "meta");
                    }
                }
                break;
        }

        return false;
    }

    // grammarchecks.go:1858
    internal bool CheckGrammarConstructorTypeParameters(ConstructorDeclaration node)
    {
        var range = node.TypeParameters;
        if (range != null)
        {
            int pos;
            if (range.Pos == range.End)
            {
                pos = range.Pos;
            }
            else
            {
                pos = global::Ts.Scanner.Scanner.SkipTrivia(AstUtilities.GetSourceFileOfNode(node.AsNode())!.Text(), range.Pos);
            }
            return GrammarErrorAtPos(node.AsNode(), pos, range.End - pos, Diag.Type_parameters_cannot_appear_on_a_constructor_declaration);
        }

        return false;
    }

    // grammarchecks.go:1873
    internal bool CheckGrammarConstructorTypeAnnotation(ConstructorDeclaration node)
    {
        var t = node.Type;
        if (t != null)
        {
            return GrammarErrorOnNode(t, Diag.Type_annotation_cannot_appear_on_a_constructor_declaration);
        }
        return false;
    }

    // grammarchecks.go:1881
    internal bool CheckGrammarProperty(Node node /*Union[PropertyDeclaration, PropertySignature]*/)
    {
        var propertyName = node.Name()!;
        if (NodeTests.IsComputedPropertyName(propertyName) && NodeTests.IsBinaryExpression(propertyName.Expression()!) && propertyName.Expression()!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.InKeyword)
        {
            return GrammarErrorOnNode(node.Parent!.Members()![0], Diag.A_mapped_type_may_not_declare_properties_or_methods);
        }
        if (AstUtilities.IsClassLike(node.Parent!))
        {
            if (NodeTests.IsStringLiteral(propertyName) && propertyName.Text() == "constructor")
            {
                return GrammarErrorOnNode(propertyName, Diag.Classes_may_not_have_a_field_named_constructor);
            }
            if (CheckGrammarForInvalidDynamicName(propertyName, Diag.A_computed_property_name_in_a_class_property_declaration_must_have_a_simple_literal_type_or_a_unique_symbol_type))
            {
                return true;
            }
            if (AstUtilities.IsAutoAccessorPropertyDeclaration(node) && CheckGrammarForInvalidQuestionMark(node.PostfixToken(), Diag.An_accessor_property_cannot_be_declared_optional))
            {
                return true;
            }
        }
        else if (NodeTests.IsInterfaceDeclaration(node.Parent!))
        {
            if (CheckGrammarForInvalidDynamicName(propertyName, Diag.A_computed_property_name_in_an_interface_must_refer_to_an_expression_whose_type_is_a_literal_type_or_a_unique_symbol_type))
            {
                return true;
            }
            if (!NodeTests.IsPropertySignatureDeclaration(node))
            {
                // Interfaces cannot contain property declarations
                throw new InvalidOperationException($"Unexpected node kind \"{node.Kind}\"");
            }
            var initializer = node.Initializer();
            if (initializer != null)
            {
                return GrammarErrorOnNode(initializer, Diag.An_interface_property_cannot_have_an_initializer);
            }
        }
        else if (NodeTests.IsTypeLiteralNode(node.Parent!))
        {
            if (CheckGrammarForInvalidDynamicName(node.Name()!, Diag.A_computed_property_name_in_a_type_literal_must_refer_to_an_expression_whose_type_is_a_literal_type_or_a_unique_symbol_type))
            {
                return true;
            }
            if (!NodeTests.IsPropertySignatureDeclaration(node))
            {
                // Type literals cannot contain property declarations
                throw new InvalidOperationException($"Unexpected node kind \"{node.Kind}\"");
            }
            var initializer = node.Initializer();
            if (initializer != null)
            {
                return GrammarErrorOnNode(initializer, Diag.A_type_literal_property_cannot_have_an_initializer);
            }
        }

        if ((node.Flags & NodeFlags.Ambient) != 0)
        {
            CheckAmbientInitializer(node);
        }

        if (NodeTests.IsPropertyDeclaration(node))
        {
            var propDecl = node.AsPropertyDeclaration();
            var postfixToken = propDecl.PostfixToken;
            if (postfixToken != null && postfixToken.Kind == SyntaxKind.ExclamationToken)
            {
                if (propDecl.Initializer != null)
                {
                    return GrammarErrorOnNode(postfixToken, Diag.Declarations_with_initializers_cannot_also_have_definite_assignment_assertions);
                }
                else if (propDecl.Type == null)
                {
                    return GrammarErrorOnNode(postfixToken, Diag.Declarations_with_definite_assignment_assertions_must_also_have_type_annotations);
                }
                else if (!AstUtilities.IsClassLike(node.Parent!) || (node.Flags & NodeFlags.Ambient) != 0 || AstUtilities.IsStatic(node) || AstUtilities.HasSyntacticModifier(node, ModifierFlags.Abstract) /* PORT-NOTE: ast.HasAbstractModifier */)
                {
                    return GrammarErrorOnNode(postfixToken, Diag.A_definite_assignment_assertion_is_not_permitted_in_this_context);
                }
            }
        }

        return false;
    }

    // grammarchecks.go:1942
    internal bool CheckAmbientInitializer(Node node)
    {
        Expression? initializer;
        TypeNode? typeNode;
        switch (node.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                var varDecl = node.AsVariableDeclaration();
                initializer = varDecl.Initializer;
                typeNode = varDecl.Type;
                break;
            case SyntaxKind.PropertyDeclaration:
                var propDecl = node.AsPropertyDeclaration();
                initializer = propDecl.Initializer;
                typeNode = propDecl.Type;
                break;
            case SyntaxKind.PropertySignature:
                var propSig = node.AsPropertySignatureDeclaration();
                initializer = propSig.Initializer;
                typeNode = propSig.Type;
                break;
            default:
                throw new InvalidOperationException($"Unexpected node kind \"{node.Kind}\"");
        }

        if (initializer != null)
        {
            var isInvalidInitializer = !(IsInitializerStringOrNumberLiteralExpression(initializer.AsNode()) || IsInitializerSimpleLiteralEnumReference(initializer.AsNode()) || initializer.Kind == SyntaxKind.TrueKeyword || initializer.Kind == SyntaxKind.FalseKeyword || IsInitializerBigIntLiteralExpression(initializer.AsNode()));
            var isConstOrReadonly = IsDeclarationReadonly(node) || (NodeTests.IsVariableDeclaration(node) && IsVarConstLike(node));
            if (isConstOrReadonly && typeNode == null)
            {
                if (isInvalidInitializer)
                {
                    return GrammarErrorOnNode(initializer.AsNode(), Diag.A_const_initializer_in_an_ambient_context_must_be_a_string_or_numeric_literal_or_literal_enum_reference);
                }
            }
            else
            {
                return GrammarErrorOnNode(initializer.AsNode(), Diag.Initializers_are_not_allowed_in_ambient_contexts);
            }
        }

        return false;
    }

    // grammarchecks.go:1977
    internal static bool IsInitializerStringOrNumberLiteralExpression(Node expr /*Expression*/)
    {
        return AstUtilities.IsStringOrNumericLiteralLike(expr) ||
            (expr.Kind == SyntaxKind.PrefixUnaryExpression && expr.AsPrefixUnaryExpression().Operator == SyntaxKind.MinusToken && expr.AsPrefixUnaryExpression().Operand!.Kind == SyntaxKind.NumericLiteral);
    }

    // grammarchecks.go:1982
    internal static bool IsInitializerBigIntLiteralExpression(Node expr /*Expression*/)
    {
        if (expr.Kind == SyntaxKind.BigIntLiteral)
        {
            return true;
        }

        if (expr.Kind == SyntaxKind.PrefixUnaryExpression)
        {
            var unaryExpr = expr.AsPrefixUnaryExpression();
            return unaryExpr.Operator == SyntaxKind.MinusToken && unaryExpr.Operand!.Kind == SyntaxKind.BigIntLiteral;
        }

        return false;
    }

    // grammarchecks.go:1995
    internal bool IsInitializerSimpleLiteralEnumReference(Node expr /*Expression*/)
    {
        if (NodeTests.IsPropertyAccessExpression(expr))
        {
            return (CheckExpressionCached(expr).flags & TypeFlags.EnumLike) != 0;
        }

        if (NodeTests.IsElementAccessExpression(expr))
        {
            var elementAccess = expr.AsElementAccessExpression();

            return IsInitializerStringOrNumberLiteralExpression(elementAccess.ArgumentExpression!.AsNode()) &&
                AstUtilities.IsEntityNameExpression(elementAccess.Expression!.AsNode()) &&
                (CheckExpressionCached(expr).flags & TypeFlags.EnumLike) != 0;
        }

        return false;
    }

    // grammarchecks.go:2011
    internal bool CheckGrammarTopLevelElementForRequiredDeclareModifier(Node node)
    {
        // A declare modifier is required for any top level .d.ts declaration except export=, export default, export as namespace
        // interfaces and imports categories:
        //
        //  DeclarationElement:
        //     ExportAssignment
        //     export_opt   InterfaceDeclaration
        //     export_opt   TypeAliasDeclaration
        //     export_opt   ImportDeclaration
        //     export_opt   ExternalImportDeclaration
        //     export_opt   AmbientDeclaration
        //
        // TODO: The spec needs to be amended to reflect this grammar.
        if (node.Kind == SyntaxKind.InterfaceDeclaration || node.Kind == SyntaxKind.TypeAliasDeclaration || node.Kind == SyntaxKind.ImportDeclaration || node.Kind == SyntaxKind.JSImportDeclaration || node.Kind == SyntaxKind.ImportEqualsDeclaration || node.Kind == SyntaxKind.ExportDeclaration || node.Kind == SyntaxKind.ExportAssignment || node.Kind == SyntaxKind.NamespaceExportDeclaration || AstUtilities.HasSyntacticModifier(node, ModifierFlags.Ambient | ModifierFlags.Export | ModifierFlags.Default))
        {
            return false;
        }

        return GrammarErrorOnFirstToken(node, Diag.Top_level_declarations_in_d_ts_files_must_start_with_either_a_declare_or_export_modifier);
    }

    // grammarchecks.go:2031
    internal bool CheckGrammarTopLevelElementsForRequiredDeclareModifier(SourceFile file)
    {
        foreach (var decl in file.Statements!.Nodes)
        {
            if (AstUtilities.IsDeclarationNode(decl) || decl.Kind == SyntaxKind.VariableStatement)
            {
                if (CheckGrammarTopLevelElementForRequiredDeclareModifier(decl))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // grammarchecks.go:2042
    internal bool CheckGrammarSourceFile(SourceFile node)
    {
        return (node.Flags & NodeFlags.Ambient) != 0 && CheckGrammarTopLevelElementsForRequiredDeclareModifier(node);
    }

    // grammarchecks.go:2046
    internal bool CheckGrammarStatementInAmbientContext(Node node)
    {
        if ((node.Flags & NodeFlags.Ambient) != 0)
        {
            // Find containing block which is either Block, ModuleBlock, SourceFile
            var links = nodeLinks.Get(node);
            if (!links.hasReportedStatementInAmbientContext && (AstUtilities.IsFunctionLike(node.Parent) || AstUtilities.IsAccessor(node.Parent!)))
            {
                links.hasReportedStatementInAmbientContext = GrammarErrorOnFirstToken(node, Diag.An_implementation_cannot_be_declared_in_ambient_contexts);
                return links.hasReportedStatementInAmbientContext;
            }

            // We are either parented by another statement, or some sort of block.
            // If we're in a block, we only want to really report an error once
            // to prevent noisiness.  So use a bit on the block to indicate if
            // this has already been reported, and don't report if it has.
            //
            if (node.Parent!.Kind == SyntaxKind.Block || node.Parent.Kind == SyntaxKind.ModuleBlock || node.Parent.Kind == SyntaxKind.SourceFile)
            {
                var parentLinks = nodeLinks.Get(node.Parent);
                // Check if the containing block ever report this error
                if (!parentLinks.hasReportedStatementInAmbientContext)
                {
                    parentLinks.hasReportedStatementInAmbientContext = GrammarErrorOnFirstToken(node, Diag.Statements_are_not_allowed_in_ambient_contexts);
                    return parentLinks.hasReportedStatementInAmbientContext;
                }
            }
            else
            {
                // We must be parented by a statement.  If so, there's no need
                // to report the error as our parent will have already done it.
                // debug.Assert(ast.IsStatement(node.Parent)) // !!! commented out in strada - fails if uncommented
            }
        }
        return false;
    }

    // grammarchecks.go:2076
    internal void CheckGrammarNumericLiteral(NumericLiteral node)
    {
        var nodeText = global::Ts.Scanner.Scanner.GetTextOfNode(node.AsNode());

        // Realism (size) checking
        // We should test against `getTextOfNode(node)` rather than `node.text`, because `node.text` for large numeric literals can contain "."
        // e.g. `node.text` for numeric literal `1100000000000000000000` is `1.1e21`.
        var isFractional = nodeText.Contains('.');
        var isScientific = (node.TokenFlags & TokenFlags.Scientific) != 0;

        // Scientific notation (e.g. 2e54 and 1e00000000010) can't be converted to bigint
        // Fractional numbers (e.g. 9000000000000000.001) are inherently imprecise anyway
        if (isFractional || isScientific)
        {
            return;
        }

        // Here `node` is guaranteed to be a numeric literal representing an integer.
        // We need to judge whether the integer `node` represents is <= 2 ** 53 - 1, which can be accomplished by comparing to `value` defined below because:
        // 1) when `node` represents an integer <= 2 ** 53 - 1, `node.text` is its exact string representation and thus `value` precisely represents the integer.
        // 2) otherwise, although `node.text` may be imprecise string representation, its mathematical value and consequently `value` cannot be less than 2 ** 53,
        //    thus the result of the predicate won't be affected.
        var value = global::Ts.JsNum.JsNum.FromString(node.Text);
        if (value <= global::Ts.JsNum.Number.MaxSafeInteger)
        {
            return;
        }

        AddErrorOrSuggestion(false, CreateDiagnosticForNode(node.AsNode(), Diag.Numeric_literals_with_absolute_values_equal_to_2_53_or_greater_are_too_large_to_be_represented_accurately_as_integers));
    }

    // grammarchecks.go:2104
    internal bool CheckGrammarBigIntLiteral(BigIntLiteral node)
    {
        var literalType = NodeTests.IsLiteralTypeNode(node.Parent!) || (NodeTests.IsPrefixUnaryExpression(node.Parent!) && NodeTests.IsLiteralTypeNode(node.Parent!.Parent!));
        if (!literalType)
        {
            // Don't error on BigInt literals in ambient contexts
            if ((node.Flags & NodeFlags.Ambient) == 0 && languageVersion < ScriptTarget.ES2020)
            {
                if (GrammarErrorOnNode(node.AsNode(), Diag.BigInt_literals_are_not_available_when_targeting_lower_than_ES2020))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // grammarchecks.go:2117
    internal bool CheckGrammarImportClause(ImportClause node)
    {
        switch (node.PhaseModifier)
        {
            case SyntaxKind.TypeKeyword:
                if ((node.Flags & NodeFlags.JSDoc) == 0 && node.Name() != null && node.NamedBindings != null)
                {
                    return GrammarErrorOnNode(node.AsNode(), Diag.A_type_only_import_can_specify_a_default_import_or_named_bindings_but_not_both);
                }
                if (node.NamedBindings != null && node.NamedBindings.Kind == SyntaxKind.NamedImports)
                {
                    return CheckGrammarTypeOnlyNamedImportsOrExports(node.NamedBindings);
                }
                break;
            case SyntaxKind.DeferKeyword:
                if (node.Name() != null)
                {
                    return GrammarErrorOnNode(node.AsNode(), Diag.Default_imports_are_not_allowed_in_a_deferred_import);
                }
                if (node.NamedBindings != null && node.NamedBindings.Kind == SyntaxKind.NamedImports)
                {
                    return GrammarErrorOnNode(node.AsNode(), Diag.Named_imports_are_not_allowed_in_a_deferred_import);
                }
                if (moduleKind != ModuleKind.ESNext && moduleKind != ModuleKind.Preserve)
                {
                    return GrammarErrorOnNode(node.AsNode(), Diag.Deferred_imports_are_only_supported_when_the_module_flag_is_set_to_esnext_or_preserve);
                }
                break;
        }
        return false;
    }

    // grammarchecks.go:2140
    internal bool CheckGrammarTypeOnlyNamedImportsOrExports(Node namedBindings)
    {
        var nodeList = namedBindings.ElementList()!;
        foreach (var specifier in nodeList.Nodes)
        {
            bool specifierIsTypeOnly;
            global::Ts.Diagnostics.Message message;
            if (specifier.Kind == SyntaxKind.ImportSpecifier)
            {
                specifierIsTypeOnly = specifier.IsTypeOnly();
                message = Diag.The_type_modifier_cannot_be_used_on_a_named_import_when_import_type_is_used_on_its_import_statement;
            }
            else
            {
                specifierIsTypeOnly = specifier.IsTypeOnly();
                message = Diag.The_type_modifier_cannot_be_used_on_a_named_export_when_export_type_is_used_on_its_export_statement;
            }

            if (specifierIsTypeOnly)
            {
                return GrammarErrorOnFirstToken(specifier, message);
            }
        }

        return false;
    }

    // grammarchecks.go:2161
    internal bool CheckGrammarImportCallExpression(Node node)
    {
        if (compilerOptions.VerbatimModuleSyntax == Tristate.True && moduleKind == ModuleKind.CommonJS)
        {
            return GrammarErrorOnNode(node, GetVerbatimModuleSyntaxErrorMessage(node));
        }

        if (node.Expression()!.Kind == SyntaxKind.MetaProperty)
        {
            if (moduleKind != ModuleKind.ESNext && moduleKind != ModuleKind.Preserve)
            {
                return GrammarErrorOnNode(node, Diag.Deferred_imports_are_only_supported_when_the_module_flag_is_set_to_esnext_or_preserve);
            }
        }
        else if (moduleKind == ModuleKind.ES2015)
        {
            return GrammarErrorOnNode(node, Diag.Dynamic_imports_are_only_supported_when_the_module_flag_is_set_to_es2020_es2022_esnext_commonjs_amd_system_umd_node16_node18_node20_or_nodenext);
        }

        var nodeAsCall = node.AsCallExpression();
        if (nodeAsCall.TypeArguments != null)
        {
            return GrammarErrorOnNode(node, Diag.This_use_of_import_is_invalid_import_calls_can_be_written_but_they_must_have_parentheses_and_cannot_have_type_arguments);
        }

        var nodeArguments = nodeAsCall.Arguments!;
        var argumentNodes = nodeArguments.Nodes;
        if (!(ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext) && moduleKind != ModuleKind.ESNext && moduleKind != ModuleKind.Preserve)
        {
            // We are allowed trailing comma after proposal-import-assertions.
            CheckGrammarForDisallowedTrailingComma(nodeArguments, Diag.Trailing_comma_not_allowed);

            if (argumentNodes.Length > 1)
            {
                var importAttributesArgument = argumentNodes[1];
                return GrammarErrorOnNode(importAttributesArgument, Diag.Dynamic_imports_only_support_a_second_argument_when_the_module_option_is_set_to_esnext_node16_node18_node20_nodenext_or_preserve);
            }
        }

        if (argumentNodes.Length == 0 || argumentNodes.Length > 2)
        {
            return GrammarErrorOnNode(node, Diag.Dynamic_imports_can_only_accept_a_module_specifier_and_an_optional_set_of_attributes_as_arguments);
        }

        // see: parseArgumentOrArrayLiteralElement...we use this function which parse arguments of callExpression to parse specifier for dynamic import.
        // parseArgumentOrArrayLiteralElement allows spread element to be in an argument list which is not allowed as specifier in dynamic import.
        var spreadElement = CoreUtil.Find(argumentNodes, NodeTests.IsSpreadElement);
        if (spreadElement != null)
        {
            return GrammarErrorOnNode(spreadElement, Diag.Argument_of_dynamic_import_cannot_be_spread_element);
        }
        return false;
    }
}
