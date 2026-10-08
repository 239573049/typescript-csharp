// Ported from tsgo internal/transformers/declarations/diagnostics.go.

using System;
using global::Ts.Ast;
using global::Ts.Diagnostics;
using global::Ts.Printer;

namespace Ts.Transformers.Declarations;

// diagnostics.go:10
internal delegate SymbolAccessibilityDiagnostic? GetSymbolAccessibilityDiagnostic(
    SymbolAccessibilityResult symbolAccessibilityResult);

// diagnostics.go:12-16
internal sealed class SymbolAccessibilityDiagnostic
{
    internal Node? ErrorNode;
    internal Message? DiagnosticMessage;
    internal Node? TypeName;
}

internal static class DeclarationDiagnostics
{
    // diagnostics.go:64-72 — selectDiagnosticBasedOnModuleName (landed earlier; kept in place).
    internal static Message? SelectDiagnosticBasedOnModuleName(
        SymbolAccessibilityResult result,
        Message moduleNotNameable,
        Message privateModule,
        Message nonModule)
    {
        if (!string.IsNullOrEmpty(result.ErrorModuleName))
        {
            return result.Accessibility == SymbolAccessibility.CannotBeNamed
                ? moduleNotNameable
                : privateModule;
        }
        return nonModule;
    }

    // diagnostics.go:74-79 — selectDiagnosticBasedOnModuleNameNoNameCheck (landed earlier; kept in place).
    internal static Message SelectDiagnosticBasedOnModuleNameNoNameCheck(
        SymbolAccessibilityResult result,
        Message privateModule,
        Message nonModule) =>
        !string.IsNullOrEmpty(result.ErrorModuleName) ? privateModule : nonModule;

    // diagnostics.go:18-30 — wrapSimpleDiagnosticSelector.
    internal static GetSymbolAccessibilityDiagnostic WrapSimpleDiagnosticSelector(
        Node node,
        Func<Node, SymbolAccessibilityResult, Message?> selector) =>
        symbolAccessibilityResult =>
        {
            var diagnosticMessage = selector(node, symbolAccessibilityResult);
            if (diagnosticMessage == null)
            {
                return null;
            }
            return new SymbolAccessibilityDiagnostic
            {
                ErrorNode = node,
                DiagnosticMessage = diagnosticMessage,
                TypeName = AstUtilities.GetNameOfDeclaration(node),
            };
        };

    // diagnostics.go:32-45 — wrapNamedDiagnosticSelector.
    internal static GetSymbolAccessibilityDiagnostic WrapNamedDiagnosticSelector(
        Node node,
        Func<Node, SymbolAccessibilityResult, Message?> selector) =>
        symbolAccessibilityResult =>
        {
            var diagnosticMessage = selector(node, symbolAccessibilityResult);
            if (diagnosticMessage == null)
            {
                return null;
            }
            var name = AstUtilities.GetNameOfDeclaration(node);
            return new SymbolAccessibilityDiagnostic
            {
                ErrorNode = name,
                DiagnosticMessage = diagnosticMessage,
                TypeName = name,
            };
        };

    // diagnostics.go:47-62 — wrapFallbackErrorDiagnosticSelector.
    internal static GetSymbolAccessibilityDiagnostic WrapFallbackErrorDiagnosticSelector(
        Node node,
        Func<Node, SymbolAccessibilityResult, Message?> selector) =>
        symbolAccessibilityResult =>
        {
            var diagnosticMessage = selector(node, symbolAccessibilityResult);
            if (diagnosticMessage == null)
            {
                return null;
            }
            var errorNode = AstUtilities.GetNameOfDeclaration(node);
            if (errorNode == null)
            {
                errorNode = node;
            }
            return new SymbolAccessibilityDiagnostic
            {
                ErrorNode = errorNode,
                DiagnosticMessage = diagnosticMessage,
            };
        };

    // diagnostics.go:81-89 — createGetSymbolAccessibilityDiagnosticForNodeName.
    internal static GetSymbolAccessibilityDiagnostic CreateGetSymbolAccessibilityDiagnosticForNodeName(Node node)
    {
        if (NodeTests.IsSetAccessorDeclaration(node) || NodeTests.IsGetAccessorDeclaration(node))
        {
            return WrapSimpleDiagnosticSelector(node, GetAccessorNameVisibilityDiagnosticMessage);
        }
        else if (NodeTests.IsMethodDeclaration(node) || NodeTests.IsMethodSignatureDeclaration(node))
        {
            return WrapSimpleDiagnosticSelector(node, GetMethodNameVisibilityDiagnosticMessage);
        }
        else
        {
            return CreateGetSymbolAccessibilityDiagnosticForNode(node);
        }
    }

    // diagnostics.go:91-113 — getAccessorNameVisibilityDiagnosticMessage.
    internal static Message? GetAccessorNameVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        if (AstUtilities.IsStatic(node))
        {
            return SelectDiagnosticBasedOnModuleName(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                global::Ts.Diagnostics.Diagnostics.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Public_static_property_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else if (node.Parent!.Kind == SyntaxKind.ClassDeclaration)
        {
            return SelectDiagnosticBasedOnModuleName(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Public_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                global::Ts.Diagnostics.Diagnostics.Public_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Public_property_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else
        {
            return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Property_0_of_exported_interface_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Property_0_of_exported_interface_has_or_is_using_private_name_1);
        }
    }

    // diagnostics.go:115-137 — getMethodNameVisibilityDiagnosticMessage.
    internal static Message? GetMethodNameVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        if (AstUtilities.IsStatic(node))
        {
            return SelectDiagnosticBasedOnModuleName(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Public_static_method_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                global::Ts.Diagnostics.Diagnostics.Public_static_method_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Public_static_method_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else if (node.Parent!.Kind == SyntaxKind.ClassDeclaration)
        {
            return SelectDiagnosticBasedOnModuleName(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Public_method_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                global::Ts.Diagnostics.Diagnostics.Public_method_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Public_method_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else
        {
            return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Method_0_of_exported_interface_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Method_0_of_exported_interface_has_or_is_using_private_name_1);
        }
    }

    // diagnostics.go:139-221 — createGetSymbolAccessibilityDiagnosticForNode.
    internal static GetSymbolAccessibilityDiagnostic CreateGetSymbolAccessibilityDiagnosticForNode(Node node)
    {
        if (NodeTests.IsVariableDeclaration(node) || NodeTests.IsPropertyDeclaration(node) || NodeTests.IsPropertySignatureDeclaration(node) || NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsElementAccessExpression(node) || NodeTests.IsBinaryExpression(node) || NodeTests.IsBindingElement(node) || NodeTests.IsConstructorDeclaration(node))
        {
            return WrapSimpleDiagnosticSelector(node, GetVariableDeclarationTypeVisibilityDiagnosticMessage);
        }
        else if (NodeTests.IsSetAccessorDeclaration(node) || NodeTests.IsGetAccessorDeclaration(node))
        {
            return WrapNamedDiagnosticSelector(node, GetAccessorDeclarationTypeVisibilityDiagnosticMessage);
        }
        else if (NodeTests.IsConstructSignatureDeclaration(node) || NodeTests.IsCallSignatureDeclaration(node) || NodeTests.IsMethodDeclaration(node) || NodeTests.IsMethodSignatureDeclaration(node) || NodeTests.IsFunctionDeclaration(node) || NodeTests.IsIndexSignatureDeclaration(node))
        {
            return WrapFallbackErrorDiagnosticSelector(node, GetReturnTypeVisibilityDiagnosticMessage);
        }
        else if (NodeTests.IsParameterDeclaration(node))
        {
            if (AstUtilities.IsParameterPropertyDeclaration(node, node.Parent!) && AstUtilities.HasSyntacticModifier(node.Parent!, ModifierFlags.Private))
            {
                return WrapSimpleDiagnosticSelector(node, GetVariableDeclarationTypeVisibilityDiagnosticMessage);
            }
            return WrapSimpleDiagnosticSelector(node, GetParameterDeclarationTypeVisibilityDiagnosticMessage);
        }
        else if (NodeTests.IsTypeParameterDeclaration(node))
        {
            return WrapSimpleDiagnosticSelector(node, GetTypeParameterConstraintVisibilityDiagnosticMessage);
        }
        else if (NodeTests.IsExpressionWithTypeArguments(node))
        {
            // unique node selection behavior, inline closure
            return symbolAccessibilityResult =>
            {
                Message diagnosticMessage;
                // Heritage clause is written by user so it can always be named
                if (NodeTests.IsClassDeclaration(node.Parent!.Parent!))
                {
                    // Class or Interface implemented/extended is inaccessible
                    if (NodeTests.IsHeritageClause(node.Parent!) && node.Parent!.AsHeritageClause().Token == SyntaxKind.ImplementsKeyword)
                    {
                        diagnosticMessage = global::Ts.Diagnostics.Diagnostics.Implements_clause_of_exported_class_0_has_or_is_using_private_name_1;
                    }
                    else
                    {
                        if (node.Parent!.Parent!.Name() != null)
                        {
                            diagnosticMessage = global::Ts.Diagnostics.Diagnostics.X_extends_clause_of_exported_class_0_has_or_is_using_private_name_1;
                        }
                        else
                        {
                            diagnosticMessage = global::Ts.Diagnostics.Diagnostics.X_extends_clause_of_exported_class_has_or_is_using_private_name_0;
                        }
                    }
                }
                else
                {
                    // interface is inaccessible
                    diagnosticMessage = global::Ts.Diagnostics.Diagnostics.X_extends_clause_of_exported_interface_0_has_or_is_using_private_name_1;
                }

                return new SymbolAccessibilityDiagnostic
                {
                    DiagnosticMessage = diagnosticMessage,
                    ErrorNode = node,
                    TypeName = AstUtilities.GetNameOfDeclaration(node.Parent!.Parent!),
                };
            };
        }
        else if (NodeTests.IsImportEqualsDeclaration(node))
        {
            return WrapSimpleDiagnosticSelector(node, static (_, _) => global::Ts.Diagnostics.Diagnostics.Import_declaration_0_is_using_private_name_1);
        }
        else if (NodeTests.IsTypeAliasDeclaration(node) || NodeTests.IsJSTypeAliasDeclaration(node))
        {
            // unique node selection behavior, inline closure
            return symbolAccessibilityResult =>
            {
                var diagnosticMessage = SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Exported_type_alias_0_has_or_is_using_private_name_1_from_module_2,
                    global::Ts.Diagnostics.Diagnostics.Exported_type_alias_0_has_or_is_using_private_name_1);
                var errorNode = node.Type();
                var typeName = node.Name();
                return new SymbolAccessibilityDiagnostic
                {
                    ErrorNode = errorNode,
                    DiagnosticMessage = diagnosticMessage,
                    TypeName = typeName,
                };
            };
        }
        else if (NodeTests.IsCallExpression(node))
        {
            // JS object.defineProperty call
            // unique node selection behavior, inline closure
            return symbolAccessibilityResult =>
            {
                var diagnosticMessage = SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Exported_variable_0_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Exported_variable_0_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Exported_variable_0_has_or_is_using_private_name_1);
                var errorNode = node.Arguments()![1];
                var typeName = node.Arguments()![1];
                return new SymbolAccessibilityDiagnostic
                {
                    ErrorNode = errorNode,
                    DiagnosticMessage = diagnosticMessage,
                    TypeName = typeName,
                };
            };
        }
        else
        {
            throw new InvalidOperationException("Attempted to set a declaration diagnostic context for unhandled node kind: " + node.Kind.ToDisplayString()); // Go: panic
        }
    }

    // diagnostics.go:223-261 — getVariableDeclarationTypeVisibilityDiagnosticMessage.
    internal static Message? GetVariableDeclarationTypeVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        if (node.Kind == SyntaxKind.VariableDeclaration || node.Kind == SyntaxKind.BindingElement)
        {
            return SelectDiagnosticBasedOnModuleName(
                symbolAccessibilityResult,
                global::Ts.Diagnostics.Diagnostics.Exported_variable_0_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                global::Ts.Diagnostics.Diagnostics.Exported_variable_0_has_or_is_using_name_1_from_private_module_2,
                global::Ts.Diagnostics.Diagnostics.Exported_variable_0_has_or_is_using_private_name_1);
        }
        // This check is to ensure we don't report error on constructor parameter property as that error would be reported during parameter emit
        // The only exception here is if the constructor was marked as private. we are not emitting the constructor parameters at all.
        else if (node.Kind == SyntaxKind.PropertyDeclaration || node.Kind == SyntaxKind.PropertyAccessExpression || node.Kind == SyntaxKind.ElementAccessExpression || node.Kind == SyntaxKind.BinaryExpression || node.Kind == SyntaxKind.PropertySignature ||
            (node.Kind == SyntaxKind.Parameter && AstUtilities.HasSyntacticModifier(node.Parent!, ModifierFlags.Private)))
        {
            // TODO(jfreeman): Deal with computed properties in error reporting.
            if (AstUtilities.IsStatic(node))
            {
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Public_static_property_0_of_exported_class_has_or_is_using_private_name_1);
            }
            else if (node.Parent!.Kind == SyntaxKind.ClassDeclaration || node.Kind == SyntaxKind.Parameter)
            {
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Public_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Public_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Public_property_0_of_exported_class_has_or_is_using_private_name_1);
            }
            else
            {
                // Interfaces cannot have types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Property_0_of_exported_interface_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Property_0_of_exported_interface_has_or_is_using_private_name_1);
            }
        }
        return null; // TODO: Audit behavior - should this panic? potentially silent error state in strada
    }

    // diagnostics.go:263-297 — getAccessorDeclarationTypeVisibilityDiagnosticMessage.
    internal static Message? GetAccessorDeclarationTypeVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        if (node.Kind == SyntaxKind.SetAccessor)
        {
            // Getters can infer the return type from the returned expression, but setters cannot, so the
            // "_from_external_module_1_but_cannot_be_named" case cannot occur.
            if (AstUtilities.IsStatic(node))
            {
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_type_of_public_static_setter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_type_of_public_static_setter_0_from_exported_class_has_or_is_using_private_name_1);
            }
            else
            {
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_type_of_public_setter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_type_of_public_setter_0_from_exported_class_has_or_is_using_private_name_1);
            }
        }
        else
        {
            if (AstUtilities.IsStatic(node))
            {
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_public_static_getter_0_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_public_static_getter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_public_static_getter_0_from_exported_class_has_or_is_using_private_name_1);
            }
            else
            {
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_public_getter_0_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_public_getter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_public_getter_0_from_exported_class_has_or_is_using_private_name_1);
            }
        }
    }

    // diagnostics.go:299-356 — getReturnTypeVisibilityDiagnosticMessage.
    internal static Message? GetReturnTypeVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ConstructSignature:
                // Interfaces cannot have return types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_constructor_signature_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_constructor_signature_from_exported_interface_has_or_is_using_private_name_0);
            case SyntaxKind.CallSignature:
                // Interfaces cannot have return types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_call_signature_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_call_signature_from_exported_interface_has_or_is_using_private_name_0);
            case SyntaxKind.IndexSignature:
                // Interfaces cannot have return types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_index_signature_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_index_signature_from_exported_interface_has_or_is_using_private_name_0);

            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                if (AstUtilities.IsStatic(node))
                {
                    return SelectDiagnosticBasedOnModuleName(
                        symbolAccessibilityResult,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_public_static_method_from_exported_class_has_or_is_using_name_0_from_external_module_1_but_cannot_be_named,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_public_static_method_from_exported_class_has_or_is_using_name_0_from_private_module_1,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_public_static_method_from_exported_class_has_or_is_using_private_name_0);
                }
                else if (node.Parent!.Kind == SyntaxKind.ClassDeclaration)
                {
                    return SelectDiagnosticBasedOnModuleName(
                        symbolAccessibilityResult,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_public_method_from_exported_class_has_or_is_using_name_0_from_external_module_1_but_cannot_be_named,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_public_method_from_exported_class_has_or_is_using_name_0_from_private_module_1,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_public_method_from_exported_class_has_or_is_using_private_name_0);
                }
                else
                {
                    // Interfaces cannot have return types that cannot be named
                    return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                        symbolAccessibilityResult,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_method_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                        global::Ts.Diagnostics.Diagnostics.Return_type_of_method_from_exported_interface_has_or_is_using_private_name_0);
                }
            case SyntaxKind.FunctionDeclaration:
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_exported_function_has_or_is_using_name_0_from_external_module_1_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_exported_function_has_or_is_using_name_0_from_private_module_1,
                    global::Ts.Diagnostics.Diagnostics.Return_type_of_exported_function_has_or_is_using_private_name_0);
            default:
                throw new InvalidOperationException("This is unknown kind for signature: " + node.Kind.ToDisplayString()); // Go: panic
        }
    }

    // diagnostics.go:358-433 — getParameterDeclarationTypeVisibilityDiagnosticMessage.
    internal static Message? GetParameterDeclarationTypeVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.Constructor:
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_constructor_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_constructor_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_constructor_from_exported_class_has_or_is_using_private_name_1);

            case SyntaxKind.ConstructSignature:
            case SyntaxKind.ConstructorType:
                // Interfaces cannot have parameter types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_constructor_signature_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_constructor_signature_from_exported_interface_has_or_is_using_private_name_1);

            case SyntaxKind.CallSignature:
                // Interfaces cannot have parameter types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_call_signature_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_call_signature_from_exported_interface_has_or_is_using_private_name_1);

            case SyntaxKind.IndexSignature:
                // Interfaces cannot have parameter types that cannot be named
                return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_index_signature_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_index_signature_from_exported_interface_has_or_is_using_private_name_1);

            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                if (AstUtilities.IsStatic(node.Parent!))
                {
                    return SelectDiagnosticBasedOnModuleName(
                        symbolAccessibilityResult,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_public_static_method_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_public_static_method_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_public_static_method_from_exported_class_has_or_is_using_private_name_1);
                }
                else if (node.Parent!.Parent!.Kind == SyntaxKind.ClassDeclaration)
                {
                    return SelectDiagnosticBasedOnModuleName(
                        symbolAccessibilityResult,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_public_method_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_public_method_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_public_method_from_exported_class_has_or_is_using_private_name_1);
                }
                else
                {
                    // Interfaces cannot have parameter types that cannot be named
                    return SelectDiagnosticBasedOnModuleNameNoNameCheck(
                        symbolAccessibilityResult,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_method_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                        global::Ts.Diagnostics.Diagnostics.Parameter_0_of_method_from_exported_interface_has_or_is_using_private_name_1);
                }

            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionType:
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_exported_function_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_exported_function_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_exported_function_has_or_is_using_private_name_1);
            case SyntaxKind.SetAccessor:
            case SyntaxKind.GetAccessor:
                return SelectDiagnosticBasedOnModuleName(
                    symbolAccessibilityResult,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_accessor_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_accessor_has_or_is_using_name_1_from_private_module_2,
                    global::Ts.Diagnostics.Diagnostics.Parameter_0_of_accessor_has_or_is_using_private_name_1);
            default:
                throw new InvalidOperationException("Unknown parent for parameter: " + node.Parent!.Kind.ToDisplayString()); // Go: panic
        }
    }

    // diagnostics.go:435-468 — getTypeParameterConstraintVisibilityDiagnosticMessage.
    internal static Message GetTypeParameterConstraintVisibilityDiagnosticMessage(Node node, SymbolAccessibilityResult symbolAccessibilityResult)
    {
        // Type parameter constraints are named by user so we should always be able to name it
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_exported_class_has_or_is_using_private_name_1;
            case SyntaxKind.InterfaceDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_exported_interface_has_or_is_using_private_name_1;
            case SyntaxKind.MappedType:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_exported_mapped_object_type_is_using_private_name_1;
            case SyntaxKind.ConstructorType:
            case SyntaxKind.ConstructSignature:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_constructor_signature_from_exported_interface_has_or_is_using_private_name_1;
            case SyntaxKind.CallSignature:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_call_signature_from_exported_interface_has_or_is_using_private_name_1;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                if (AstUtilities.IsStatic(node.Parent!))
                {
                    return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_public_static_method_from_exported_class_has_or_is_using_private_name_1;
                }
                else if (node.Parent!.Parent!.Kind == SyntaxKind.ClassDeclaration)
                {
                    return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_public_method_from_exported_class_has_or_is_using_private_name_1;
                }
                else
                {
                    return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_method_from_exported_interface_has_or_is_using_private_name_1;
                }
            case SyntaxKind.FunctionType:
            case SyntaxKind.FunctionDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_exported_function_has_or_is_using_private_name_1;

            case SyntaxKind.InferType:
                return global::Ts.Diagnostics.Diagnostics.Extends_clause_for_inferred_type_0_has_or_is_using_private_name_1;

            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Type_parameter_0_of_exported_type_alias_has_or_is_using_private_name_1;

            default:
                throw new InvalidOperationException("This is unknown parent for type parameter: " + node.Parent!.Kind.ToDisplayString()); // Go: panic
        }
    }

    // diagnostics.go:470-499 — getRelatedSuggestionByDeclarationKind.
    internal static Message? GetRelatedSuggestionByDeclarationKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.ArrowFunction:
                return global::Ts.Diagnostics.Diagnostics.Add_a_return_type_to_the_function_expression;
            case SyntaxKind.FunctionExpression:
                return global::Ts.Diagnostics.Diagnostics.Add_a_return_type_to_the_function_expression;
            case SyntaxKind.MethodDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Add_a_return_type_to_the_method;
            case SyntaxKind.GetAccessor:
                return global::Ts.Diagnostics.Diagnostics.Add_a_return_type_to_the_get_accessor_declaration;
            case SyntaxKind.SetAccessor:
                return global::Ts.Diagnostics.Diagnostics.Add_a_type_to_parameter_of_the_set_accessor_declaration;
            case SyntaxKind.FunctionDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Add_a_return_type_to_the_function_declaration;
            case SyntaxKind.ConstructSignature:
                return global::Ts.Diagnostics.Diagnostics.Add_a_return_type_to_the_function_declaration;
            case SyntaxKind.Parameter:
                return global::Ts.Diagnostics.Diagnostics.Add_a_type_annotation_to_the_parameter_0;
            case SyntaxKind.VariableDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Add_a_type_annotation_to_the_variable_0;
            case SyntaxKind.PropertyDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Add_a_type_annotation_to_the_property_0;
            case SyntaxKind.PropertySignature:
                return global::Ts.Diagnostics.Diagnostics.Add_a_type_annotation_to_the_property_0;
            case SyntaxKind.ExportAssignment:
                return global::Ts.Diagnostics.Diagnostics.Move_the_expression_in_default_export_to_a_variable_and_add_a_type_annotation_to_it;
            default:
                return null;
        }
    }

    // diagnostics.go:501-540 — getErrorByDeclarationKind.
    internal static Message? GetErrorByDeclarationKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.FunctionExpression:
                return global::Ts.Diagnostics.Diagnostics.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.FunctionDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.ArrowFunction:
                return global::Ts.Diagnostics.Diagnostics.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.MethodDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Method_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.ConstructSignature:
                return global::Ts.Diagnostics.Diagnostics.Method_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.GetAccessor:
                return global::Ts.Diagnostics.Diagnostics.At_least_one_accessor_must_have_an_explicit_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.SetAccessor:
                return global::Ts.Diagnostics.Diagnostics.At_least_one_accessor_must_have_an_explicit_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.Parameter:
                return global::Ts.Diagnostics.Diagnostics.Parameter_must_have_an_explicit_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.VariableDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Variable_must_have_an_explicit_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.PropertyDeclaration:
                return global::Ts.Diagnostics.Diagnostics.Property_must_have_an_explicit_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.PropertySignature:
                return global::Ts.Diagnostics.Diagnostics.Property_must_have_an_explicit_type_annotation_with_isolatedDeclarations;
            case SyntaxKind.ComputedPropertyName:
                return global::Ts.Diagnostics.Diagnostics.Computed_property_names_on_class_or_object_literals_cannot_be_inferred_with_isolatedDeclarations;
            case SyntaxKind.SpreadAssignment:
                return global::Ts.Diagnostics.Diagnostics.Objects_that_contain_spread_assignments_can_t_be_inferred_with_isolatedDeclarations;
            case SyntaxKind.ShorthandPropertyAssignment:
                return global::Ts.Diagnostics.Diagnostics.Objects_that_contain_shorthand_properties_can_t_be_inferred_with_isolatedDeclarations;
            case SyntaxKind.ArrayLiteralExpression:
                return global::Ts.Diagnostics.Diagnostics.Only_const_arrays_can_be_inferred_with_isolatedDeclarations;
            case SyntaxKind.ExportAssignment:
                return global::Ts.Diagnostics.Diagnostics.Default_exports_can_t_be_inferred_with_isolatedDeclarations;
            case SyntaxKind.SpreadElement:
                return global::Ts.Diagnostics.Diagnostics.Arrays_with_spread_elements_can_t_inferred_with_isolatedDeclarations;
            default:
                return null;
        }
    }

    // diagnostics.go:542-544 — isDeclarationEnoughForErrors.
    internal static bool IsDeclarationEnoughForErrors(Node node)
    {
        return NodeTests.IsExportAssignment(node) || AstUtilities.IsStatement(node) || NodeTests.IsVariableDeclaration(node) || NodeTests.IsPropertyDeclaration(node) || NodeTests.IsParameterDeclaration(node);
    }

    // diagnostics.go:546-548 — isFunctionLikeAndNotConstructor.
    internal static bool IsFunctionLikeAndNotConstructor(Node node)
    {
        return AstUtilities.IsFunctionLikeDeclaration(node) && !NodeTests.IsConstructorDeclaration(node);
    }

    // diagnostics.go:550-565 — findNearestDeclaration.
    internal static Node? FindNearestDeclaration(Node node)
    {
        var result = AstUtilities.FindAncestor(node, IsDeclarationEnoughForErrors);
        if (result == null)
        {
            return null;
        }
        if (NodeTests.IsExportAssignment(result))
        {
            return result;
        }
        if (NodeTests.IsReturnStatement(result))
        {
            return AstUtilities.FindAncestor(result, IsFunctionLikeAndNotConstructor);
        }
        if (AstUtilities.IsStatement(result))
        {
            return null;
        }
        return result;
    }

    // diagnostics.go:567-571 — createEntityInTypeNodeError.
    internal static Diagnostic CreateEntityInTypeNodeError(Node node)
    {
        var diag = CreateDiagnosticForNode(node, global::Ts.Diagnostics.Diagnostics.Type_containing_private_name_0_can_t_be_used_with_isolatedDeclarations, global::Ts.Scanner.Scanner.GetTextOfNode(node));
        AddParentDeclarationRelatedInfo(node, diag);
        return diag;
    }

    // diagnostics.go:573-583 — addParentDeclarationRelatedInfo.
    internal static void AddParentDeclarationRelatedInfo(Node node, Diagnostic diag)
    {
        var parentDeclaration = FindNearestDeclaration(node);
        if (parentDeclaration == null)
        {
            return;
        }
        var targetStr = "";
        if (!NodeTests.IsExportAssignment(parentDeclaration) && parentDeclaration.Name() != null)
        {
            targetStr = global::Ts.Scanner.Scanner.GetTextOfNode(parentDeclaration.Name()!);
        }
        diag.AddRelatedInfo(CreateDiagnosticForNode(parentDeclaration, GetRelatedSuggestionByDeclarationKind(parentDeclaration.Kind)!, targetStr));
    }

    // diagnostics.go:585-601 — createAccessorTypeError.
    internal static Diagnostic CreateAccessorTypeError(Node node)
    {
        var allDeclarations = AstUtilities.GetAllAccessorDeclarationsForDeclaration(node, node.Symbol()!.Declarations ?? []);
        var getAccessor = allDeclarations.GetAccessor;
        var setAccessor = allDeclarations.SetAccessor;
        var targetNode = node;
        if (NodeTests.IsSetAccessorDeclaration(node) && node.Parameters().Length > 0)
        {
            targetNode = node.Parameters()[0];
        }
        var diag = CreateDiagnosticForNode(targetNode, GetErrorByDeclarationKind(node.Kind)!);
        if (setAccessor != null)
        {
            diag.AddRelatedInfo(CreateDiagnosticForNode(setAccessor.AsNode(), GetRelatedSuggestionByDeclarationKind(setAccessor.Kind)!));
        }
        if (getAccessor != null)
        {
            diag.AddRelatedInfo(CreateDiagnosticForNode(getAccessor.AsNode(), GetRelatedSuggestionByDeclarationKind(getAccessor.Kind)!));
        }
        return diag;
    }

    // diagnostics.go:603-607 — createObjectLiteralError.
    internal static Diagnostic CreateObjectLiteralError(Node node)
    {
        var diag = CreateDiagnosticForNode(node, GetErrorByDeclarationKind(node.Kind)!);
        AddParentDeclarationRelatedInfo(node, diag);
        return diag;
    }

    // diagnostics.go:609-613 — createArrayLiteralError.
    internal static Diagnostic CreateArrayLiteralError(Node node)
    {
        var diag = CreateDiagnosticForNode(node, GetErrorByDeclarationKind(node.Kind)!);
        AddParentDeclarationRelatedInfo(node, diag);
        return diag;
    }

    // diagnostics.go:615-620 — createReturnTypeError.
    internal static Diagnostic CreateReturnTypeError(Node node)
    {
        var diag = CreateDiagnosticForNode(node, GetErrorByDeclarationKind(node.Kind)!);
        AddParentDeclarationRelatedInfo(node, diag);
        diag.AddRelatedInfo(CreateDiagnosticForNode(node, GetRelatedSuggestionByDeclarationKind(node.Kind)!));
        return diag;
    }

    // diagnostics.go:622-624 — createBindingElementError.
    internal static Diagnostic CreateBindingElementError(Node node)
    {
        return CreateDiagnosticForNode(node, global::Ts.Diagnostics.Diagnostics.Binding_elements_can_t_be_exported_directly_with_isolatedDeclarations);
    }

    // diagnostics.go:626-630 — createVariableOrPropertyError.
    internal static Diagnostic CreateVariableOrPropertyError(Node node)
    {
        var diag = CreateDiagnosticForNode(node, GetErrorByDeclarationKind(node.Kind)!);
        diag.AddRelatedInfo(CreateDiagnosticForNode(node, GetRelatedSuggestionByDeclarationKind(node.Kind)!, global::Ts.Scanner.Scanner.GetTextOfNode(node.Name()!)));
        return diag;
    }

    // diagnostics.go:632-634 — createExpressionError.
    internal static Diagnostic CreateExpressionError(Node node)
    {
        return CreateExpressionErrorEx(node, null);
    }

    // diagnostics.go:636-638 — createClassExpressionError.
    internal static Diagnostic CreateClassExpressionError(Node node)
    {
        return CreateExpressionErrorEx(node, global::Ts.Diagnostics.Diagnostics.Inference_from_class_expressions_is_not_supported_with_isolatedDeclarations);
    }

    // diagnostics.go:640-648 — isParentForIDDIagnostic.
    internal static FindAncestorResult IsParentForIDDIagnostic(Node node)
    {
        if (NodeTests.IsExportAssignment(node))
        {
            return FindAncestorResult.True;
        }
        if (AstUtilities.IsStatement(node))
        {
            return FindAncestorResult.Quit;
        }
        return AstUtilities.ToFindAncestorResult(!NodeTests.IsParenthesizedExpression(node) && !AstUtilities.IsAssertionExpression(node));
    }

    // diagnostics.go:650-680 — createExpressionErrorEx.
    internal static Diagnostic CreateExpressionErrorEx(Node node, Message? diagnosticMessage)
    {
        var parentDeclaration = FindNearestDeclaration(node);
        if (parentDeclaration == null)
        {
            if (diagnosticMessage == null)
            {
                diagnosticMessage = global::Ts.Diagnostics.Diagnostics.Expression_type_can_t_be_inferred_with_isolatedDeclarations;
            }
            return CreateDiagnosticForNode(node, diagnosticMessage);
        }

        var targetStr = "";
        if (!NodeTests.IsExportAssignment(parentDeclaration) && parentDeclaration.Name() != null)
        {
            targetStr = global::Ts.Scanner.Scanner.GetTextOfNode(parentDeclaration.Name()!);
        }
        var parent = AstUtilities.FindAncestorOrQuit(node.Parent!, IsParentForIDDIagnostic);

        if (parentDeclaration == parent)
        {
            if (diagnosticMessage == null)
            {
                diagnosticMessage = GetErrorByDeclarationKind(parentDeclaration.Kind)!;
            }
            var diag = CreateDiagnosticForNode(node, diagnosticMessage);
            diag.AddRelatedInfo(CreateDiagnosticForNode(parentDeclaration, GetRelatedSuggestionByDeclarationKind(parentDeclaration.Kind)!, targetStr));
            return diag;
        }
        if (diagnosticMessage == null)
        {
            diagnosticMessage = global::Ts.Diagnostics.Diagnostics.Expression_type_can_t_be_inferred_with_isolatedDeclarations;
        }
        var diag2 = CreateDiagnosticForNode(node, diagnosticMessage);
        diag2.AddRelatedInfo(CreateDiagnosticForNode(parentDeclaration, GetRelatedSuggestionByDeclarationKind(parentDeclaration.Kind)!, targetStr));
        diag2.AddRelatedInfo(CreateDiagnosticForNode(node, global::Ts.Diagnostics.Diagnostics.Add_satisfies_and_a_type_assertion_to_this_expression_satisfies_T_as_T_to_make_the_type_explicit));
        return diag2;
    }

    // diagnostics.go:682-735 — createGetIsolatedDeclarationErrors.
    internal static Func<Node, Diagnostic> CreateGetIsolatedDeclarationErrors(EmitResolver? resolver)
    {
        Diagnostic CreateParameterError(Node node)
        {
            if (NodeTests.IsSetAccessorDeclaration(node.Parent!))
            {
                return CreateAccessorTypeError(node.Parent!);
            }
            var addUndefined = resolver!.RequiresAddingImplicitUndefinedUnsafe(node, null!, null!); // skip checker lock - node builder will already have one
            if (!addUndefined && node.Initializer() != null)
            {
                return CreateExpressionError(node.Initializer()!);
            }
            var message = GetErrorByDeclarationKind(node.Kind)!;
            if (addUndefined)
            {
                message = global::Ts.Diagnostics.Diagnostics.Declaration_emit_for_this_parameter_requires_implicitly_adding_undefined_to_its_type_This_is_not_supported_with_isolatedDeclarations;
            }
            var diag = CreateDiagnosticForNode(node, message);
            var targetStr = global::Ts.Scanner.Scanner.GetTextOfNode(node.Name()!);
            diag.AddRelatedInfo(CreateDiagnosticForNode(node, GetRelatedSuggestionByDeclarationKind(node.Kind)!, targetStr));
            return diag;
        }

        return node =>
        {
            var heritageClause = AstUtilities.FindAncestor(node, NodeTests.IsHeritageClause);
            if (heritageClause != null)
            {
                return CreateDiagnosticForNode(node, global::Ts.Diagnostics.Diagnostics.Extends_clause_can_t_contain_an_expression_with_isolatedDeclarations);
            }
            if (AstUtilities.IsPartOfTypeNode(node) || NodeTests.IsTypeQueryNode(node))
            {
                return CreateEntityInTypeNodeError(node);
            }
            if (AstUtilities.IsEntityName(node) || AstUtilities.IsEntityNameExpression(node))
            {
                return CreateEntityInTypeNodeError(node);
            }
            switch (node.Kind)
            {
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                    return CreateAccessorTypeError(node);
                case SyntaxKind.ComputedPropertyName:
                case SyntaxKind.ShorthandPropertyAssignment:
                case SyntaxKind.SpreadAssignment:
                    return CreateObjectLiteralError(node);
                case SyntaxKind.ArrayLiteralExpression:
                case SyntaxKind.SpreadElement:
                    return CreateArrayLiteralError(node);
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.ConstructSignature:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.ArrowFunction:
                case SyntaxKind.FunctionDeclaration:
                    return CreateReturnTypeError(node);
                case SyntaxKind.BindingElement:
                    return CreateBindingElementError(node);
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.VariableDeclaration:
                    return CreateVariableOrPropertyError(node);
                case SyntaxKind.Parameter:
                    return CreateParameterError(node);
                case SyntaxKind.PropertyAssignment:
                    return CreateExpressionError(node.Initializer()!);
                case SyntaxKind.ClassExpression:
                    return CreateClassExpressionError(node);
                default:
                    return CreateExpressionError(node);
            }
        };
    }

    // tracker.go:229-231 → checker/utilities.go:22-30 — createDiagnosticForNode / NewDiagnosticForNode.
    internal static Diagnostic CreateDiagnosticForNode(
        Node? node,
        Message message,
        params object?[] args)
    {
        SourceFile? file = null;
        var loc = new global::Ts.Core.TextRange();
        if (node != null)
        {
            file = AstUtilities.GetSourceFileOfNode(node);
            loc = global::Ts.Scanner.Scanner.GetErrorRangeForNode(file!, node);
        }
        return Diagnostic.NewDiagnostic(file, loc, message, args);
    }
}
